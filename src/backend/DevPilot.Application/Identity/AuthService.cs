using DevPilot.Application.Common;
using DevPilot.Application.Organizations;
using DevPilot.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Application.Identity;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Absolute session lifetime; the user must log in again after this regardless of activity.</summary>
    public int SessionLifetimeHours { get; set; } = 7 * 24;

    /// <summary>Session expires after this long without requests.</summary>
    public int IdleTimeoutMinutes { get; set; } = 12 * 60;

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    public TimeSpan SessionLifetime => TimeSpan.FromHours(SessionLifetimeHours);
    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);
}

public sealed record RegisterRequest(string Email, string DisplayName, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UpdateProfileRequest(string DisplayName);

public sealed record UserDto(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt);

public sealed record MeResponse(UserDto User, IReadOnlyList<OrgSummaryDto> Organizations, int PendingInvitations);

/// <summary>Raw tokens are returned exactly once so the API layer can set cookies; only hashes are stored.</summary>
public sealed record IssuedSession(Guid SessionId, string SessionToken, string CsrfToken, DateTimeOffset ExpiresAt, UserDto User);

/// <summary>Validated session used by the authentication handler.</summary>
public sealed record AuthenticatedSession(Guid SessionId, Guid UserId, string Email, string DisplayName, string CsrfTokenHash);

public sealed class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ICurrentUser currentUser,
    AuditLog audit,
    TimeProvider clock,
    IOptions<AuthOptions> options,
    IValidator<RegisterRequest> registerValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator,
    IValidator<UpdateProfileRequest> profileValidator,
    ILogger<AuthService> logger)
{
    // Hash of a random value, verified when the email does not exist so response times stay similar (user enumeration).
    private static string? s_dummyHash;

    private readonly AuthOptions _options = options.Value;

    public async Task<IssuedSession> RegisterAsync(RegisterRequest request, string? userAgent, CancellationToken ct)
    {
        await registerValidator.ValidateAndThrowAsync(request, ct);
        var email = User.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            throw new ConflictException("auth.email_taken", "An account with this email already exists.");
        }

        var now = clock.GetUtcNow();
        var user = new User(email, request.DisplayName, now);
        user.SetPasswordHash(hasher.Hash(request.Password));
        user.RegisterSuccessfulLogin(now);
        db.Users.Add(user);
        audit.Record(null, "auth.registered", "user", user.Id.ToString(), actorUserId: user.Id);

        var session = CreateSession(user, userAgent, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ix_users_email", StringComparison.Ordinal) == true)
        {
            throw new ConflictException("auth.email_taken", "An account with this email already exists.");
        }

        logger.LogInformation("User {UserId} registered", user.Id);
        return session;
    }

    public async Task<IssuedSession> LoginAsync(LoginRequest request, string? userAgent, CancellationToken ct)
    {
        const string invalid = "Invalid email or password.";
        var email = User.NormalizeEmail(request.Email ?? string.Empty);
        var password = request.Password ?? string.Empty;
        var now = clock.GetUtcNow();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            hasher.Verify(GetDummyHash(), password);
            throw new UnauthorizedException(invalid);
        }

        if (user.IsLockedOut(now))
        {
            // Same generic message: a distinct "locked" response would confirm that the account exists.
            audit.Record(null, "auth.login_locked", "user", user.Id.ToString(), actorUserId: user.Id);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(invalid);
        }

        var (valid, needsRehash) = hasher.Verify(user.PasswordHash, password);
        if (!valid)
        {
            user.RegisterFailedLogin(now, _options.MaxFailedLogins, TimeSpan.FromMinutes(_options.LockoutMinutes));
            audit.Record(null, "auth.login_failed", "user", user.Id.ToString(), actorUserId: user.Id);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(invalid);
        }

        if (needsRehash)
        {
            user.SetPasswordHash(hasher.Hash(password));
        }

        user.RegisterSuccessfulLogin(now);
        var session = CreateSession(user, userAgent, now);
        audit.Record(null, "auth.login", "user", user.Id.ToString(), actorUserId: user.Id);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Resolves a raw session token to an active session, sliding the idle window. Returns null if invalid.</summary>
    public async Task<AuthenticatedSession?> AuthenticateAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > 128)
        {
            return null;
        }

        var hash = SecureTokens.Hash(rawToken);
        var session = await db.Sessions.Include(s => s.User).FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (session?.User is null || !session.IsActive(now, _options.IdleTimeout))
        {
            return null;
        }

        if (session.Touch(now, TimeSpan.FromMinutes(1)))
        {
            await db.SaveChangesAsync(ct);
        }

        return new AuthenticatedSession(session.Id, session.UserId, session.User.Email, session.User.DisplayName, session.CsrfTokenHash);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (currentUser.SessionId is not { } sessionId)
        {
            return;
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return;
        }

        session.Revoke(clock.GetUtcNow());
        audit.Record(null, "auth.logout", "session", session.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new UnauthorizedException();
        var orgs = await OrganizationService.MyOrganizations(db, userId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var pending = await db.Invitations.IgnoreQueryFilters()
            .CountAsync(i => i.Email == user.Email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        return new MeResponse(ToDto(user), orgs, pending);
    }

    public async Task<UserDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct)
    {
        await profileValidator.ValidateAndThrowAsync(request, ct);
        var userId = currentUser.RequireUserId();
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.UpdateProfile(request.DisplayName);
        audit.Record(null, "user.profile_updated", "user", user.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    /// <summary>Changes the password and revokes every other session of the user.</summary>
    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        await changePasswordValidator.ValidateAndThrowAsync(request, ct);
        var userId = currentUser.RequireUserId();
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!hasher.Verify(user.PasswordHash, request.CurrentPassword).Valid)
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.CurrentPassword), "The current password is incorrect.")]);
        }

        user.SetPasswordHash(hasher.Hash(request.NewPassword));
        var now = clock.GetUtcNow();
        var others = await db.Sessions.Where(s => s.UserId == userId && s.Id != currentUser.SessionId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in others)
        {
            session.Revoke(now);
        }

        audit.Record(null, "auth.password_changed", "user", user.Id.ToString(),
            new Dictionary<string, string?> { ["revokedSessions"] = others.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await db.SaveChangesAsync(ct);
    }

    public static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.CreatedAt);

    private IssuedSession CreateSession(User user, string? userAgent, DateTimeOffset now)
    {
        var token = SecureTokens.Generate();
        var csrf = SecureTokens.Generate();
        var session = new UserSession(user.Id, SecureTokens.Hash(token), SecureTokens.Hash(csrf), now, _options.SessionLifetime,
            currentUser.IpAddress, userAgent);
        db.Sessions.Add(session);
        return new IssuedSession(session.Id, token, csrf, session.ExpiresAt, ToDto(user));
    }

    private string GetDummyHash() => s_dummyHash ??= hasher.Hash(SecureTokens.Generate());
}
