using DevPilot.Application.Common;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Operations;

public sealed record PipelineRunDto(Guid Id, Guid PipelineId, string PipelineName, Guid? ProjectId, string? RepositoryFullName, string ExternalRunId,
    long? RunNumber, int Attempt, string? Branch, string? CommitSha, string? Trigger, string? Actor, RunStatus Status, RunConclusion? Conclusion,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, double? DurationSeconds, string? HtmlUrl, EventSource Source);

public sealed record DeploymentDto(Guid Id, string Environment, DeploymentStatus Status, string? Ref, string? CommitSha, string? Description,
    string? Creator, Guid? ProjectId, string? RepositoryFullName, string? Url, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt, double? DurationSeconds, EventSource Source);

public sealed record CodeEventDto(Guid Id, CodeEventKind Kind, string Title, string? Author, string? Branch, string? Url, string? State,
    int CommitCount, string RepositoryFullName, DateTimeOffset OccurredAt);

public sealed record PipelineHealthDto(Guid PipelineId, string Name, int Runs, int Failures, double? SuccessRate, double? AvgDurationSeconds,
    RunConclusion? LastConclusion, DateTimeOffset? LastRunAt);

public sealed record OperationsSummaryDto(int Runs7d, int FailedRuns7d, double? SuccessRate7d, double? AvgDurationSeconds7d, int Deployments7d,
    int FailedDeployments7d, IReadOnlyList<DeploymentDto> RecentDeployments, IReadOnlyList<PipelineRunDto> RecentFailures,
    IReadOnlyList<PipelineHealthDto> Pipelines, int ActiveIntegrations, int IntegrationsWithErrors, int PendingRepositories, int OpenIncidents,
    int CriticalOpenIncidents, bool HasOperationalData);

public sealed record LogEventDto(Guid Id, Guid? ProjectId, string Source, Domain.Operations.LogLevel Level, string Message, string? Exception,
    string Fingerprint, string? Environment, DateTimeOffset Timestamp);

public sealed record ErrorGroupDto(string Fingerprint, string SampleMessage, string? SampleException, string Source, Domain.Operations.LogLevel MaxLevel,
    int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

public sealed record RunQuery(Guid? ProjectId, Guid? PipelineId, string? Branch, RunConclusion? Conclusion, bool? Failed, int Page = 1, int PageSize = 25);

public sealed record DeploymentQuery(Guid? ProjectId, string? Environment, DeploymentStatus? Status, int Page = 1, int PageSize = 25);

public sealed record LogQuery(Guid? ProjectId, Domain.Operations.LogLevel? MinLevel, string? Source, string? Q, string? Fingerprint, DateTimeOffset? Since,
    int Page = 1, int PageSize = 50);

/// <summary>Read side for pipelines, deployments, code activity and logs. Everything comes from recorded external events.</summary>
public sealed class OperationsQueryService(IAppDbContext db, TimeProvider clock)
{
    public async Task<PagedResult<PipelineRunDto>> ListRunsAsync(RunQuery q, CancellationToken ct)
    {
        var runs = db.PipelineRuns.AsNoTracking();
        if (q.ProjectId is { } projectId)
        {
            runs = runs.Where(r => r.ProjectId == projectId);
        }

        if (q.PipelineId is { } pipelineId)
        {
            runs = runs.Where(r => r.PipelineId == pipelineId);
        }

        if (!string.IsNullOrWhiteSpace(q.Branch))
        {
            runs = runs.Where(r => r.Branch == q.Branch);
        }

        if (q.Conclusion is { } conclusion)
        {
            runs = runs.Where(r => r.Conclusion == conclusion);
        }
        else if (q.Failed == true)
        {
            runs = runs.Where(r => r.Conclusion == RunConclusion.Failure || r.Conclusion == RunConclusion.TimedOut);
        }

        var page = new PageRequest(q.Page, q.PageSize);
        var total = await runs.CountAsync(ct);
        var rows = await runs.OrderByDescending(r => r.CreatedAt).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<PipelineRunDto>(await ToRunDtosAsync(rows, ct), page.SafePage, page.SafePageSize, total);
    }

    public async Task<PagedResult<DeploymentDto>> ListDeploymentsAsync(DeploymentQuery q, CancellationToken ct)
    {
        var deployments = db.Deployments.AsNoTracking();
        if (q.ProjectId is { } projectId)
        {
            deployments = deployments.Where(d => d.ProjectId == projectId);
        }

        if (!string.IsNullOrWhiteSpace(q.Environment))
        {
            deployments = deployments.Where(d => d.Environment == q.Environment);
        }

        if (q.Status is { } status)
        {
            deployments = deployments.Where(d => d.Status == status);
        }

        var page = new PageRequest(q.Page, q.PageSize);
        var total = await deployments.CountAsync(ct);
        var rows = await deployments.OrderByDescending(d => d.CreatedAt).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<DeploymentDto>(await ToDeploymentDtosAsync(rows, ct), page.SafePage, page.SafePageSize, total);
    }

    public async Task<IReadOnlyList<string>> ListEnvironmentsAsync(CancellationToken ct) =>
        await db.Deployments.AsNoTracking().Select(d => d.Environment).Distinct().OrderBy(e => e).Take(50).ToListAsync(ct);

    public async Task<PagedResult<CodeEventDto>> ListCodeEventsAsync(Guid? projectId, PageRequest page, CancellationToken ct)
    {
        var events = db.CodeEvents.AsNoTracking();
        if (projectId is { } pid)
        {
            events = events.Where(e => e.ProjectId == pid);
        }

        var total = await events.CountAsync(ct);
        var rows = await events.OrderByDescending(e => e.OccurredAt).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var repoIds = rows.Select(r => r.RepositoryId).Distinct().ToList();
        var repos = await db.Repositories.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.FullName, ct);
        var items = rows.Select(e => new CodeEventDto(e.Id, e.Kind, e.Title, e.Author, e.Branch, e.Url, e.State, e.CommitCount,
            repos.GetValueOrDefault(e.RepositoryId, "?"), e.OccurredAt)).ToList();
        return new PagedResult<CodeEventDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<OperationsSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-7);
        var runs7 = await db.PipelineRuns.AsNoTracking().Where(r => r.CreatedAt >= since && r.Status == RunStatus.Completed).ToListAsync(ct);
        var failed = runs7.Count(r => r.IsFailed);
        var decisive = runs7.Count(r => r.Conclusion is RunConclusion.Success or RunConclusion.Failure or RunConclusion.TimedOut);
        var durations = runs7.Where(r => r.DurationSeconds.HasValue).Select(r => r.DurationSeconds!.Value).ToList();

        var deployments7 = await db.Deployments.AsNoTracking().Where(d => d.CreatedAt >= since).ToListAsync(ct);
        var recentDeploymentRows = await db.Deployments.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(5).ToListAsync(ct);
        var recentFailureRows = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Conclusion == RunConclusion.Failure || r.Conclusion == RunConclusion.TimedOut)
            .OrderByDescending(r => r.CompletedAt).Take(5).ToListAsync(ct);

        var pipelines = await db.Pipelines.AsNoTracking().ToListAsync(ct);
        var health = pipelines.Select(p =>
        {
            var pr = runs7.Where(r => r.PipelineId == p.Id).ToList();
            var dec = pr.Count(r => r.Conclusion is RunConclusion.Success or RunConclusion.Failure or RunConclusion.TimedOut);
            var last = pr.OrderByDescending(r => r.CompletedAt).FirstOrDefault();
            var d = pr.Where(r => r.DurationSeconds.HasValue).Select(r => r.DurationSeconds!.Value).ToList();
            return new PipelineHealthDto(p.Id, p.Name, pr.Count, pr.Count(r => r.IsFailed),
                dec == 0 ? null : Math.Round(pr.Count(r => r.Conclusion == RunConclusion.Success) / (double)dec, 3),
                d.Count == 0 ? null : Math.Round(d.Average(), 1), last?.Conclusion, last?.CompletedAt);
        }).Where(h => h.Runs > 0).OrderByDescending(h => h.Failures).ThenBy(h => h.Name).Take(10).ToList();

        var integrations = await db.Integrations.AsNoTracking().ToListAsync(ct);
        var dayAgo = clock.GetUtcNow().AddDays(-1);
        return new OperationsSummaryDto(
            runs7.Count,
            failed,
            decisive == 0 ? null : Math.Round(runs7.Count(r => r.Conclusion == RunConclusion.Success) / (double)decisive, 3),
            durations.Count == 0 ? null : Math.Round(durations.Average(), 1),
            deployments7.Count,
            deployments7.Count(d => d.Status is DeploymentStatus.Failure or DeploymentStatus.Error),
            await ToDeploymentDtosAsync(recentDeploymentRows, ct),
            await ToRunDtosAsync(recentFailureRows, ct),
            health,
            integrations.Count(i => i.IsActive),
            integrations.Count(i => i.IsActive && i.LastErrorAt >= dayAgo),
            await db.Repositories.CountAsync(r => r.Status == RepositoryStatus.Pending, ct),
            await db.Incidents.CountAsync(i => i.Status != Domain.Incidents.IncidentStatus.Resolved, ct),
            await db.Incidents.CountAsync(i => i.Status != Domain.Incidents.IncidentStatus.Resolved && i.Severity == Domain.Incidents.IncidentSeverity.Critical, ct),
            pipelines.Count > 0 || await db.Deployments.AnyAsync(ct));
    }

    public async Task<PagedResult<LogEventDto>> ListLogsAsync(LogQuery q, CancellationToken ct)
    {
        var logs = FilterLogs(q);
        var page = new PageRequest(q.Page, q.PageSize);
        return await logs.OrderByDescending(l => l.Timestamp)
            .Select(l => new LogEventDto(l.Id, l.ProjectId, l.Source, l.Level, l.Message, l.Exception, l.Fingerprint, l.Environment, l.Timestamp))
            .ToPagedResultAsync(page, ct);
    }

    /// <summary>Frequent error patterns: Warning+ events grouped by fingerprint over the selected window (default 7 days).</summary>
    public async Task<IReadOnlyList<ErrorGroupDto>> ListErrorGroupsAsync(LogQuery q, CancellationToken ct)
    {
        var since = q.Since ?? clock.GetUtcNow().AddDays(-7);
        var logs = FilterLogs(q with { MinLevel = q.MinLevel ?? Domain.Operations.LogLevel.Warning, Since = since });
        var groups = await logs.GroupBy(l => l.Fingerprint)
            .Select(g => new { Fingerprint = g.Key, Count = g.Count(), First = g.Min(l => l.Timestamp), Last = g.Max(l => l.Timestamp), MaxLevel = g.Max(l => l.Level) })
            .OrderByDescending(g => g.Count).Take(50).ToListAsync(ct);
        var fingerprints = groups.Select(g => g.Fingerprint).ToList();
        var samples = await db.LogEvents.AsNoTracking().Where(l => fingerprints.Contains(l.Fingerprint))
            .GroupBy(l => l.Fingerprint).Select(g => g.OrderByDescending(l => l.Timestamp).First()).ToListAsync(ct);
        var byFingerprint = samples.ToDictionary(s => s.Fingerprint);
        return groups.Select(g =>
        {
            var sample = byFingerprint.GetValueOrDefault(g.Fingerprint);
            return new ErrorGroupDto(g.Fingerprint, sample?.Message ?? "?", sample?.Exception, sample?.Source ?? "?", g.MaxLevel, g.Count, g.First, g.Last);
        }).ToList();
    }

    private IQueryable<LogEvent> FilterLogs(LogQuery q)
    {
        var logs = db.LogEvents.AsNoTracking();
        if (q.ProjectId is { } projectId)
        {
            logs = logs.Where(l => l.ProjectId == projectId);
        }

        if (q.MinLevel is { } level)
        {
            logs = logs.Where(l => l.Level >= level);
        }

        if (!string.IsNullOrWhiteSpace(q.Source))
        {
            logs = logs.Where(l => l.Source == q.Source);
        }

        if (!string.IsNullOrWhiteSpace(q.Fingerprint))
        {
            logs = logs.Where(l => l.Fingerprint == q.Fingerprint);
        }

        if (q.Since is { } since)
        {
            logs = logs.Where(l => l.Timestamp >= since);
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim().ToLowerInvariant();
            logs = logs.Where(l => l.Message.ToLower().Contains(term));
        }

        return logs;
    }

    private async Task<List<PipelineRunDto>> ToRunDtosAsync(IReadOnlyCollection<PipelineRun> rows, CancellationToken ct)
    {
        var pipelineIds = rows.Select(r => r.PipelineId).Distinct().ToList();
        var pipelines = await db.Pipelines.AsNoTracking().Where(p => pipelineIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var repoIds = pipelines.Values.Where(p => p.RepositoryId.HasValue).Select(p => p.RepositoryId!.Value).Distinct().ToList();
        var repos = await db.Repositories.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.FullName, ct);
        return rows.Select(r =>
        {
            var p = pipelines.GetValueOrDefault(r.PipelineId);
            return new PipelineRunDto(r.Id, r.PipelineId, p?.Name ?? "?", r.ProjectId, p?.RepositoryId is { } repo ? repos.GetValueOrDefault(repo) : null,
                r.ExternalRunId, r.RunNumber, r.Attempt, r.Branch, r.CommitSha, r.Trigger, r.Actor, r.Status, r.Conclusion, r.CreatedAt, r.StartedAt,
                r.CompletedAt, r.DurationSeconds, r.HtmlUrl, p?.Source ?? EventSource.Api);
        }).ToList();
    }

    private async Task<List<DeploymentDto>> ToDeploymentDtosAsync(IReadOnlyCollection<Deployment> rows, CancellationToken ct)
    {
        var repoIds = rows.Where(r => r.RepositoryId.HasValue).Select(r => r.RepositoryId!.Value).Distinct().ToList();
        var repos = await db.Repositories.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.FullName, ct);
        return rows.Select(d => new DeploymentDto(d.Id, d.Environment, d.Status, d.Ref, d.CommitSha, d.Description, d.Creator, d.ProjectId,
            d.RepositoryId is { } r ? repos.GetValueOrDefault(r) : null, d.Url, d.CreatedAt, d.UpdatedAt, d.FinishedAt, d.DurationSeconds, d.Source)).ToList();
    }
}
