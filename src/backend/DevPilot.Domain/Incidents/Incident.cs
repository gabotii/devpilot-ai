using DevPilot.Domain.Common;

namespace DevPilot.Domain.Incidents;

public enum IncidentSeverity
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

public enum IncidentStatus
{
    Open,
    Investigating,
    Mitigated,
    Resolved,
}

public enum IncidentSource
{
    Manual,
    PipelineFailure,
    DeploymentFailure,
    ErrorGroup,
}

public sealed class Incident : Entity, IOrganizationScoped
{
    public const int MaxTitleLength = 200;
    public const int MaxLogLength = 100_000;

    private Incident() { }

    public Incident(Guid organizationId, int number, string title, IncidentSeverity severity, IncidentSource source, string? fingerprint,
        Guid? projectId, Guid? createdByUserId, string? description, string? logExcerpt, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        Number = number;
        Title = Clip(title.Trim(), MaxTitleLength);
        Severity = severity;
        Source = source;
        Fingerprint = fingerprint;
        ProjectId = projectId;
        CreatedByUserId = createdByUserId;
        Description = string.IsNullOrWhiteSpace(description) ? null : Clip(description.Trim(), 10_000);
        LogExcerpt = string.IsNullOrWhiteSpace(logExcerpt) ? null : Clip(logExcerpt, MaxLogLength);
        Status = IncidentStatus.Open;
        CreatedAt = now;
        UpdatedAt = now;
        LastOccurredAt = now;
        OccurrenceCount = 1;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>Per-organization sequence shown as INC-{Number}.</summary>
    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? LogExcerpt { get; private set; }
    public IncidentSeverity Severity { get; private set; }
    public IncidentStatus Status { get; private set; }
    public IncidentSource Source { get; private set; }

    /// <summary>Deduplication key for automatic incidents (e.g. pipeline + branch). At most one open incident per fingerprint.</summary>
    public string? Fingerprint { get; private set; }

    public Guid? ProjectId { get; private set; }
    public Guid? PipelineRunId { get; private set; }
    public Guid? DeploymentId { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public int OccurrenceCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset LastOccurredAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public uint Version { get; private set; }

    public bool IsOpen => Status != IncidentStatus.Resolved;

    public void LinkRun(Guid pipelineRunId) => PipelineRunId = pipelineRunId;

    public void LinkDeployment(Guid deploymentId) => DeploymentId = deploymentId;

    public void RecordOccurrence(DateTimeOffset now)
    {
        OccurrenceCount++;
        LastOccurredAt = now;
        UpdatedAt = now;
        if (Status == IncidentStatus.Mitigated)
        {
            Status = IncidentStatus.Investigating; // recurred after mitigation
        }
    }

    public void Update(string title, string? description, IncidentSeverity severity, Guid? assigneeId, DateTimeOffset now)
    {
        Title = Clip(title.Trim(), MaxTitleLength);
        Description = string.IsNullOrWhiteSpace(description) ? null : Clip(description.Trim(), 10_000);
        Severity = severity;
        AssigneeId = assigneeId;
        UpdatedAt = now;
    }

    public void AttachLog(string log, DateTimeOffset now)
    {
        LogExcerpt = Clip(log, MaxLogLength);
        UpdatedAt = now;
    }

    public void ChangeStatus(IncidentStatus status, DateTimeOffset now)
    {
        if (status == Status)
        {
            return;
        }

        Status = status;
        ResolvedAt = status == IncidentStatus.Resolved ? now : null;
        if (status == IncidentStatus.Resolved)
        {
            Fingerprint = null; // a recurrence after resolution opens a new incident
        }

        UpdatedAt = now;
    }

    private static string Clip(string value, int max) => value.Length > max ? value[..max] : value;
}

public enum IncidentUpdateKind
{
    Note,
    StatusChange,
    System,
}

public sealed class IncidentUpdate : Entity, IOrganizationScoped
{
    public const int MaxBodyLength = 5000;

    private IncidentUpdate() { }

    public IncidentUpdate(Incident incident, IncidentUpdateKind kind, string body, Guid? authorId, DateTimeOffset now)
    {
        OrganizationId = incident.OrganizationId;
        IncidentId = incident.Id;
        Kind = kind;
        Body = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
        AuthorId = authorId;
        At = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid IncidentId { get; private set; }
    public IncidentUpdateKind Kind { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public Guid? AuthorId { get; private set; }
    public DateTimeOffset At { get; private set; }
}

public enum AiAnalysisKind
{
    LogAnalysis,
    PipelineFailure,
    IncidentSummary,
    Assistant,
    ActivitySummary,
}

public enum AiAnalysisStatus
{
    Succeeded,
    Failed,
}

/// <summary>History of every AI request: what was asked (hash + size, not raw logs), which provider answered and the result.</summary>
public sealed class AiAnalysis : Entity, IOrganizationScoped
{
    private AiAnalysis() { }

    public AiAnalysis(Guid organizationId, AiAnalysisKind kind, Guid requestedBy, Guid? projectId, Guid? incidentId, string? subject,
        int inputChars, string inputSha256, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        Kind = kind;
        RequestedByUserId = requestedBy;
        ProjectId = projectId;
        IncidentId = incidentId;
        Subject = subject is { Length: > 300 } ? subject[..300] : subject;
        InputChars = inputChars;
        InputSha256 = inputSha256;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public AiAnalysisKind Kind { get; private set; }
    public AiAnalysisStatus Status { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? IncidentId { get; private set; }

    /// <summary>Short label: incident title, question text (for the assistant), etc.</summary>
    public string? Subject { get; private set; }

    public int InputChars { get; private set; }
    public string InputSha256 { get; private set; } = string.Empty;
    public string? Provider { get; private set; }
    public string? Model { get; private set; }

    /// <summary>JSON document returned by the AI service (already redacted there).</summary>
    public string? ResultJson { get; private set; }

    public string? ErrorCode { get; private set; }
    public int? LatencyMs { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void Succeed(string provider, string model, string resultJson, int latencyMs)
    {
        Status = AiAnalysisStatus.Succeeded;
        Provider = provider;
        Model = model;
        ResultJson = resultJson;
        LatencyMs = latencyMs;
    }

    public void Fail(string errorCode, int latencyMs)
    {
        Status = AiAnalysisStatus.Failed;
        ErrorCode = errorCode;
        LatencyMs = latencyMs;
    }
}
