using DevPilot.Domain.Auditing;

namespace DevPilot.Application.Common;

/// <summary>
/// Adds audit and activity records to the current unit of work; they are persisted atomically with the change
/// they describe when the caller saves.
/// </summary>
public sealed class AuditLog(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly string[] SensitiveKeyFragments = ["password", "secret", "token", "apikey", "api_key", "authorization", "cookie"];

    public void Record(Guid? organizationId, string action, string targetType, string? targetId, Dictionary<string, string?>? details = null,
        Guid? actorUserId = null)
    {
        db.AuditEntries.Add(new AuditEntry(organizationId, actorUserId ?? currentUser.UserId, action, targetType, targetId,
            Sanitize(details), currentUser.IpAddress, clock.GetUtcNow()));
    }

    public void Activity(Guid organizationId, Guid? projectId, string kind, string summary, string subjectType, Guid? subjectId,
        Guid? actorUserId = null)
    {
        db.ActivityEntries.Add(new ActivityEntry(organizationId, projectId, actorUserId ?? currentUser.UserId, kind, summary, subjectType,
            subjectId, clock.GetUtcNow()));
    }

    /// <summary>Drops values whose keys look like credentials and caps value length.</summary>
    public static Dictionary<string, string?> Sanitize(Dictionary<string, string?>? details)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (details is null)
        {
            return result;
        }

        foreach (var (key, value) in details)
        {
            var lower = key.ToLowerInvariant();
            if (SensitiveKeyFragments.Any(lower.Contains))
            {
                result[key] = "[redacted]";
                continue;
            }

            result[key] = value is { Length: > 300 } ? value[..300] + "…" : value;
        }

        return result;
    }
}
