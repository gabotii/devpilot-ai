using DevPilot.Domain.Common;
using DevPilot.Domain.Integrations;

namespace DevPilot.Domain.Operations;

public enum EventSource
{
    GitHub,
    Api,
}

public sealed class Pipeline : Entity, IOrganizationScoped
{
    private Pipeline() { }

    public Pipeline(Guid organizationId, EventSource source, string externalKey, string name, string? path, Guid? repositoryId, Guid? projectId,
        DateTimeOffset now)
    {
        OrganizationId = organizationId;
        Source = source;
        ExternalKey = externalKey;
        Name = Truncate(name, 200);
        Path = path is null ? null : Truncate(path, 300);
        RepositoryId = repositoryId;
        ProjectId = projectId;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public EventSource Source { get; private set; }

    /// <summary>Provider-scoped identity, e.g. "github:{repoId}:{workflowId}" or a client-defined key for the API.</summary>
    public string ExternalKey { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Path { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void Update(string name, string? path, Guid? projectId)
    {
        Name = Truncate(name, 200);
        Path = path is null ? null : Truncate(path, 300);
        ProjectId = projectId ?? ProjectId;
    }

    internal static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}

public enum RunStatus
{
    Queued = 0,
    InProgress = 1,
    Completed = 2,
}

public enum RunConclusion
{
    Success,
    Failure,
    Cancelled,
    TimedOut,
    Skipped,
    ActionRequired,
    Neutral,
    Stale,
    Unknown,
}

public sealed record PipelineRunUpdate(
    RunStatus Status,
    RunConclusion? Conclusion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset EventAt,
    string? HtmlUrl);

/// <summary>
/// One attempt of a pipeline execution. Events can arrive duplicated or out of order (webhook retries), so updates are
/// monotonic: a run never moves back from Completed to an earlier status, and older events never overwrite newer ones.
/// </summary>
public sealed class PipelineRun : Entity, IOrganizationScoped
{
    private PipelineRun() { }

    public PipelineRun(Pipeline pipeline, string externalRunId, int attempt, long? runNumber, string? branch, string? commitSha, string? trigger,
        string? actor, DateTimeOffset createdAt)
    {
        OrganizationId = pipeline.OrganizationId;
        PipelineId = pipeline.Id;
        ProjectId = pipeline.ProjectId;
        ExternalRunId = externalRunId;
        Attempt = attempt;
        RunNumber = runNumber;
        Branch = branch is null ? null : Pipeline.Truncate(branch, 200);
        CommitSha = commitSha is null ? null : Pipeline.Truncate(commitSha, 64);
        Trigger = trigger is null ? null : Pipeline.Truncate(trigger, 50);
        Actor = actor is null ? null : Pipeline.Truncate(actor, 100);
        CreatedAt = createdAt;
        Status = RunStatus.Queued;
        LastEventAt = DateTimeOffset.MinValue;
    }

    public Guid OrganizationId { get; private set; }
    public Guid PipelineId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string ExternalRunId { get; private set; } = string.Empty;
    public int Attempt { get; private set; }
    public long? RunNumber { get; private set; }
    public string? Branch { get; private set; }
    public string? CommitSha { get; private set; }
    public string? Trigger { get; private set; }
    public string? Actor { get; private set; }
    public RunStatus Status { get; private set; }
    public RunConclusion? Conclusion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset LastEventAt { get; private set; }
    public string? HtmlUrl { get; private set; }

    public double? DurationSeconds => StartedAt is { } s && CompletedAt is { } c && c >= s ? (c - s).TotalSeconds : null;

    public bool IsFailed => Status == RunStatus.Completed && Conclusion is RunConclusion.Failure or RunConclusion.TimedOut;

    /// <summary>Applies an event; returns true when it changed the run's state (false for stale/duplicate events).</summary>
    public bool Apply(PipelineRunUpdate update)
    {
        if (Status == RunStatus.Completed && update.Status != RunStatus.Completed)
        {
            return false; // late "in_progress" after completion
        }

        if (update.EventAt < LastEventAt && update.Status <= Status)
        {
            return false; // older event
        }

        var changed = Status != update.Status || Conclusion != update.Conclusion;
        Status = update.Status;
        Conclusion = update.Status == RunStatus.Completed ? update.Conclusion ?? RunConclusion.Unknown : null;
        StartedAt ??= update.StartedAt;
        if (update.Status == RunStatus.Completed)
        {
            CompletedAt = update.CompletedAt ?? update.EventAt;
        }

        HtmlUrl = Repository.SafeUrl(update.HtmlUrl) ?? HtmlUrl;
        LastEventAt = update.EventAt > LastEventAt ? update.EventAt : LastEventAt;
        return changed;
    }
}

public enum DeploymentStatus
{
    Pending,
    Queued,
    InProgress,
    Success,
    Failure,
    Error,
    Inactive,
}

/// <summary>A deployment reported by GitHub or the ingestion API. DevPilot records deployments; it never performs them.</summary>
public sealed class Deployment : Entity, IOrganizationScoped
{
    private Deployment() { }

    public Deployment(Guid organizationId, EventSource source, string externalId, string environment, string? reference, string? commitSha,
        string? description, string? creator, Guid? repositoryId, Guid? projectId, DateTimeOffset createdAt)
    {
        OrganizationId = organizationId;
        Source = source;
        ExternalId = externalId;
        Environment = Pipeline.Truncate(environment.Trim(), 50);
        Ref = reference is null ? null : Pipeline.Truncate(reference, 200);
        CommitSha = commitSha is null ? null : Pipeline.Truncate(commitSha, 64);
        Description = description is null ? null : Pipeline.Truncate(description, 500);
        Creator = creator is null ? null : Pipeline.Truncate(creator, 100);
        RepositoryId = repositoryId;
        ProjectId = projectId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = DeploymentStatus.Pending;
    }

    public Guid OrganizationId { get; private set; }
    public EventSource Source { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string Environment { get; private set; } = string.Empty;
    public string? Ref { get; private set; }
    public string? CommitSha { get; private set; }
    public string? Description { get; private set; }
    public string? Creator { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public DeploymentStatus Status { get; private set; }
    public string? Url { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsProduction => Environment.Equals("production", StringComparison.OrdinalIgnoreCase)
        || Environment.Equals("prod", StringComparison.OrdinalIgnoreCase);

    public bool IsTerminal => Status is DeploymentStatus.Success or DeploymentStatus.Failure or DeploymentStatus.Error or DeploymentStatus.Inactive;

    public double? DurationSeconds => FinishedAt is { } f ? (f - CreatedAt).TotalSeconds : null;

    /// <summary>Ignores status events older than the latest one applied (out-of-order delivery).</summary>
    public bool ApplyStatus(DeploymentStatus status, string? url, DateTimeOffset at)
    {
        if (at < UpdatedAt && Status != DeploymentStatus.Pending)
        {
            return false;
        }

        var changed = Status != status;
        Status = status;
        Url = Repository.SafeUrl(url) ?? Url;
        UpdatedAt = at;
        if (status is DeploymentStatus.Success or DeploymentStatus.Failure or DeploymentStatus.Error)
        {
            FinishedAt = at;
        }

        return changed;
    }
}

public enum CodeEventKind
{
    Push,
    PullRequest,
}

public sealed class CodeEvent : Entity, IOrganizationScoped
{
    private CodeEvent() { }

    public CodeEvent(Guid organizationId, Guid repositoryId, Guid? projectId, CodeEventKind kind, string externalId, string title, string? author,
        string? branch, string? url, string? state, int commitCount, DateTimeOffset occurredAt)
    {
        OrganizationId = organizationId;
        RepositoryId = repositoryId;
        ProjectId = projectId;
        Kind = kind;
        ExternalId = externalId;
        Update(title, author, branch, url, state, commitCount, occurredAt);
    }

    public Guid OrganizationId { get; private set; }
    public Guid RepositoryId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public CodeEventKind Kind { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string? Author { get; private set; }
    public string? Branch { get; private set; }
    public string? Url { get; private set; }
    public string? State { get; private set; }
    public int CommitCount { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    public void Update(string title, string? author, string? branch, string? url, string? state, int commitCount, DateTimeOffset occurredAt)
    {
        Title = Pipeline.Truncate(title, 300);
        Author = author is null ? null : Pipeline.Truncate(author, 100);
        Branch = branch is null ? null : Pipeline.Truncate(branch, 200);
        Url = Repository.SafeUrl(url);
        State = state is null ? null : Pipeline.Truncate(state, 30);
        CommitCount = commitCount;
        OccurredAt = occurredAt;
    }
}

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>Operational log/error event pushed through the ingestion API.</summary>
public sealed class LogEvent : Entity, IOrganizationScoped
{
    public const int MaxMessageLength = 4000;
    public const int MaxExceptionLength = 16000;

    private LogEvent() { }

    public LogEvent(Guid organizationId, Guid? projectId, string source, LogLevel level, string message, string? exception, string fingerprint,
        string? environment, DateTimeOffset timestamp, DateTimeOffset receivedAt, Dictionary<string, string?> attributes)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
        Source = Pipeline.Truncate(source, 100);
        Level = level;
        Message = Pipeline.Truncate(message, MaxMessageLength);
        Exception = exception is null ? null : Pipeline.Truncate(exception, MaxExceptionLength);
        Fingerprint = fingerprint;
        Environment = environment is null ? null : Pipeline.Truncate(environment, 50);
        Timestamp = timestamp;
        ReceivedAt = receivedAt;
        Attributes = attributes;
    }

    public Guid OrganizationId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public LogLevel Level { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string? Exception { get; private set; }

    /// <summary>Hash of the normalized message + exception type; groups recurring errors.</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public string? Environment { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public Dictionary<string, string?> Attributes { get; private set; } = [];
}
