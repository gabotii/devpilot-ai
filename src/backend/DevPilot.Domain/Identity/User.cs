using DevPilot.Domain.Common;

namespace DevPilot.Domain.Identity;

public sealed class User : Entity
{
    public const int MaxDisplayNameLength = 100;
    public const int MaxEmailLength = 254;

    private User() { }

    public User(string email, string displayName, DateTimeOffset now)
    {
        Email = NormalizeEmail(email);
        DisplayName = displayName.Trim();
        CreatedAt = now;
    }

    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void UpdateProfile(string displayName) => DisplayName = displayName.Trim();

    public void RegisterFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= maxAttempts)
        {
            LockedUntil = now.Add(lockoutDuration);
            FailedLoginAttempts = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }
}
