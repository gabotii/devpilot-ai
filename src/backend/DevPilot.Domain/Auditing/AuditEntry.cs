using DevPilot.Domain.Common;

namespace DevPilot.Domain.Auditing;

/// <summary>
/// Security-relevant action record (who did what, when, from where). Details must never contain secrets,
/// passwords, tokens or full external payloads; the writer sanitizes them.
/// </summary>
public sealed class AuditEntry : Entity
{
    private AuditEntry() { }

    public AuditEntry(Guid? organizationId, Guid? actorUserId, string action, string targetType, string? targetId,
        Dictionary<string, string?> details, string? ipAddress, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        ActorUserId = actorUserId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        Details = details;
        IpAddress = ipAddress;
        At = now;
    }

    public Guid? OrganizationId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public string? TargetId { get; private set; }
    public Dictionary<string, string?> Details { get; private set; } = [];
    public string? IpAddress { get; private set; }
    public DateTimeOffset At { get; private set; }
}

/// <summary>Product-facing activity feed entry, visible to organization members.</summary>
public sealed class ActivityEntry : Entity, IOrganizationScoped
{
    public const int MaxSummaryLength = 300;

    private ActivityEntry() { }

    public ActivityEntry(Guid organizationId, Guid? projectId, Guid? actorUserId, string kind, string summary,
        string subjectType, Guid? subjectId, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
        ActorUserId = actorUserId;
        Kind = kind;
        Summary = summary.Length > MaxSummaryLength ? summary[..(MaxSummaryLength - 1)] + "…" : summary;
        SubjectType = subjectType;
        SubjectId = subjectId;
        At = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string SubjectType { get; private set; } = string.Empty;
    public Guid? SubjectId { get; private set; }
    public DateTimeOffset At { get; private set; }
}
