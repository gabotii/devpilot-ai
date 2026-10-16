using System.Security.Claims;
using System.Text.Encodings.Web;
using DevPilot.Application.Common;
using DevPilot.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DevPilot.Api.Security;

public sealed class SessionCookieOptions
{
    public const string SectionName = "SessionCookie";

    /// <summary>Must be true in any deployment served over HTTPS. Enables the "__Host-" prefix (no Domain, Path=/, Secure).</summary>
    public bool Secure { get; set; } = true;

    public string SessionCookieName => Secure ? "__Host-devpilot_session" : "devpilot_session";
    public string CsrfCookieName => Secure ? "__Host-devpilot_csrf" : "devpilot_csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";
}

public static class SessionClaims
{
    public const string SessionId = "sid";
    public const string CsrfHash = "csrf_hash";
}

/// <summary>Authenticates requests using the opaque session cookie, validated against the hashed server-side session store.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SessionCookieOptions> cookieOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(cookieOptions.Value.SessionCookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var auth = Context.RequestServices.GetRequiredService<AuthService>();
        var session = await auth.AuthenticateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("Invalid or expired session.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim(ClaimTypes.Email, session.Email),
            new Claim(ClaimTypes.Name, session.DisplayName),
            new Claim(SessionClaims.SessionId, session.SessionId.ToString()),
            new Claim(SessionClaims.CsrfHash, session.CsrfTokenHash),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Results.Problem(statusCode: 401, title: "Unauthorized", detail: "Authentication is required.",
            extensions: new Dictionary<string, object?> { ["code"] = "auth.required" }).ExecuteAsync(Context);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Results.Problem(statusCode: 403, title: "Forbidden").ExecuteAsync(Context);
    }
}

/// <summary>
/// Double-submit CSRF protection bound to the session: unsafe requests authenticated by cookie must echo the CSRF token
/// (from the readable CSRF cookie) in the X-CSRF-Token header; its hash must match the one stored with the session.
/// SameSite=Strict cookies are the first line of defense; this is the second.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var expected = context.User.FindFirst(SessionClaims.CsrfHash)?.Value;
        if (expected is null)
        {
            // Not cookie-authenticated (anonymous endpoints such as login, or signed webhooks): nothing to protect.
            await next(context);
            return;
        }

        var provided = context.Request.Headers[SessionCookieOptions.CsrfHeaderName].ToString();
        if (string.IsNullOrEmpty(provided) || provided.Length > 128 || !SecureTokens.FixedTimeEqualsHex(SecureTokens.Hash(provided), expected))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await Results.Problem(statusCode: 403, title: "Forbidden", detail: "Missing or invalid CSRF token.",
                extensions: new Dictionary<string, object?> { ["code"] = "csrf.invalid" }).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public Guid? SessionId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(SessionClaims.SessionId), out var id) ? id : null;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
