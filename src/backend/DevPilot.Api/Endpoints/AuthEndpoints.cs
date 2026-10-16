using DevPilot.Api.Infrastructure;
using DevPilot.Api.Security;
using DevPilot.Application.Identity;
using DevPilot.Application.Organizations;
using Microsoft.Extensions.Options;

namespace DevPilot.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record SessionResponse(UserDto User, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (RegisterRequest request, AuthService auth, HttpContext http, IOptions<SessionCookieOptions> cookies, CancellationToken ct) =>
            {
                var session = await auth.RegisterAsync(request, http.Request.Headers.UserAgent.ToString(), ct);
                WriteSessionCookies(http, cookies.Value, session);
                return Results.Created("/api/auth/me", new SessionResponse(session.User, session.ExpiresAt));
            })
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Create an account and start a session");

        group.MapPost("/login", async (LoginRequest request, AuthService auth, HttpContext http, IOptions<SessionCookieOptions> cookies, CancellationToken ct) =>
            {
                var session = await auth.LoginAsync(request, http.Request.Headers.UserAgent.ToString(), ct);
                WriteSessionCookies(http, cookies.Value, session);
                return Results.Ok(new SessionResponse(session.User, session.ExpiresAt));
            })
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Start a session (sets HttpOnly session cookie and CSRF cookie)");

        group.MapPost("/logout", async (AuthService auth, HttpContext http, IOptions<SessionCookieOptions> cookies, CancellationToken ct) =>
            {
                await auth.LogoutAsync(ct);
                ClearSessionCookies(http, cookies.Value);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithSummary("Revoke the current session");

        group.MapGet("/me", (AuthService auth, CancellationToken ct) => auth.GetMeAsync(ct))
            .RequireAuthorization()
            .WithSummary("Current user and organizations");

        group.MapPut("/me/profile", (UpdateProfileRequest request, AuthService auth, CancellationToken ct) => auth.UpdateProfileAsync(request, ct))
            .RequireAuthorization();

        group.MapPost("/me/password", async (ChangePasswordRequest request, AuthService auth, CancellationToken ct) =>
            {
                await auth.ChangePasswordAsync(request, ct);
                return Results.NoContent();
            })
            .RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Change password and revoke all other sessions");

        var invitations = app.MapGroup("/api/invitations").WithTags("Invitations").RequireAuthorization();
        invitations.MapGet("/mine", (InvitationService service, CancellationToken ct) => service.ListMineAsync(ct));
        invitations.MapPost("/preview", (AcceptTokenRequest request, InvitationService service, CancellationToken ct) => service.PreviewAsync(request.Token, ct))
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Preview an invitation from its secret token (POST so the token never appears in URLs or logs)");
        invitations.MapPost("/accept", (AcceptTokenRequest request, InvitationService service, CancellationToken ct) => service.AcceptAsync(request.Token, ct))
            .RequireRateLimiting(RateLimitPolicies.Auth);
        invitations.MapPost("/{invitationId:guid}/accept", (Guid invitationId, InvitationService service, CancellationToken ct) =>
            service.AcceptByIdAsync(invitationId, ct));

        return app;
    }

    private static void WriteSessionCookies(HttpContext http, SessionCookieOptions options, IssuedSession session)
    {
        http.Response.Cookies.Append(options.SessionCookieName, session.SessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.Secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = session.ExpiresAt,
            IsEssential = true,
        });

        // Readable by the SPA (same origin) so it can echo it in the X-CSRF-Token header.
        http.Response.Cookies.Append(options.CsrfCookieName, session.CsrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = options.Secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = session.ExpiresAt,
            IsEssential = true,
        });
    }

    private static void ClearSessionCookies(HttpContext http, SessionCookieOptions options)
    {
        var expired = new CookieOptions { Secure = options.Secure, SameSite = SameSiteMode.Strict, Path = "/" };
        http.Response.Cookies.Delete(options.SessionCookieName, expired);
        http.Response.Cookies.Delete(options.CsrfCookieName, expired);
    }
}
