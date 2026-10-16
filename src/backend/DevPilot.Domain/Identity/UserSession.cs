using DevPilot.Domain.Common;

namespace DevPilot.Domain.Identity;

/// <summary>
/// Server-side session. Only SHA-256 hashes of the session and CSRF tokens are stored, so a database
/// leak does not allow session hijacking.
/// </summary>
public sealed class UserSession : Entity
{
    private UserSession() { }

    public UserSession(Guid userId, string tokenHash, string csrfTokenHash, DateTimeOffset now, TimeSpan absoluteLifetime,
        string? ipAddress, string? userAgent)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CsrfTokenHash = csrfTokenHash;
        CreatedAt = now;
        LastSeenAt = now;
        ExpiresAt = now.Add(absoluteLifetime);
        IpAddress = ipAddress;
        UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent;
    }

    public Guid UserId { get; private set; }
    public User? User { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public string CsrfTokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public bool IsActive(DateTimeOffset now, TimeSpan idleTimeout) =>
        RevokedAt is null && now < ExpiresAt && now - LastSeenAt < idleTimeout;

    /// <summary>Returns true when LastSeenAt was refreshed (callers persist only then, to avoid a write per request).</summary>
    public bool Touch(DateTimeOffset now, TimeSpan granularity)
    {
        if (now - LastSeenAt < granularity)
        {
            return false;
        }

        LastSeenAt = now;
        return true;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
