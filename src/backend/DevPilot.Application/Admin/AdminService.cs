using DevPilot.Application.Ai;
using DevPilot.Application.Common;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Application.Admin;

public sealed record AuditEntryDto(Guid Id, string Action, string TargetType, string? TargetId, string? ActorName, string? ActorEmail,
    Dictionary<string, string?> Details, string? IpAddress, DateTimeOffset At);

public sealed record AuditQuery(string? Action, Guid? ActorUserId, DateTimeOffset? From, DateTimeOffset? To, int Page = 1, int PageSize = 50);

public sealed record RetentionPolicyDto(int AuditRetentionDays, int LogRetentionDays, int WebhookPayloadRetentionDays, int ReadNotificationRetentionDays);

public sealed record AdminOverviewDto(int Members, int Projects, int ArchivedProjects, int ActiveIntegrations, int PendingRepositories,
    int FailedDeliveries24h, int ActiveIngestKeys, int LogEvents7d, int AiAnalyses30d, int FailedAiAnalyses30d, AiStatusDto Ai,
    RetentionPolicyDto Retention, DateTimeOffset? OldestAuditEntry);

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public int LogRetentionDays { get; set; } = 30;
    public int WebhookPayloadRetentionDays { get; set; } = 30;
    public int ReadNotificationRetentionDays { get; set; } = 90;
    public int ExpiredSessionRetentionDays { get; set; } = 30;
}

public sealed class AdminService(IAppDbContext db, ITenantContext tenant, AiAnalysisService ai, TimeProvider clock, IOptions<RetentionOptions> retention)
{
    public async Task<PagedResult<AuditEntryDto>> ListAuditAsync(AuditQuery q, CancellationToken ct)
    {
        RequireAdmin();
        var orgId = tenant.RequireOrganizationId();
        var entries = db.AuditEntries.AsNoTracking().Where(a => a.OrganizationId == orgId);
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            var action = q.Action.Trim();
            entries = entries.Where(a => a.Action.StartsWith(action));
        }

        if (q.ActorUserId is { } actor)
        {
            entries = entries.Where(a => a.ActorUserId == actor);
        }

        if (q.From is { } from)
        {
            entries = entries.Where(a => a.At >= from);
        }

        if (q.To is { } to)
        {
            entries = entries.Where(a => a.At <= to);
        }

        var page = new PageRequest(q.Page, q.PageSize);
        var total = await entries.CountAsync(ct);
        var rows = await entries.OrderByDescending(a => a.At).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var actorIds = rows.Where(r => r.ActorUserId.HasValue).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var items = rows.Select(a =>
        {
            var user = a.ActorUserId is { } id ? users.GetValueOrDefault(id) : null;
            return new AuditEntryDto(a.Id, a.Action, a.TargetType, a.TargetId, user?.DisplayName, user?.Email, a.Details, a.IpAddress, a.At);
        }).ToList();
        return new PagedResult<AuditEntryDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<AdminOverviewDto> GetOverviewAsync(CancellationToken ct)
    {
        RequireAdmin();
        var orgId = tenant.RequireOrganizationId();
        var now = clock.GetUtcNow();
        var org = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
        return new AdminOverviewDto(
            await db.Memberships.CountAsync(m => m.OrganizationId == orgId, ct),
            await db.Projects.CountAsync(ct),
            await db.Projects.CountAsync(p => p.Status == Domain.Projects.ProjectStatus.Archived, ct),
            await db.Integrations.CountAsync(i => i.Status == IntegrationStatus.Active, ct),
            await db.Repositories.CountAsync(r => r.Status == RepositoryStatus.Pending, ct),
            await db.WebhookDeliveries.CountAsync(d => d.Status == DeliveryStatus.Failed && d.ReceivedAt >= now.AddDays(-1), ct),
            await db.IngestKeys.CountAsync(k => k.RevokedAt == null, ct),
            await db.LogEvents.CountAsync(l => l.ReceivedAt >= now.AddDays(-7), ct),
            await db.AiAnalyses.CountAsync(a => a.CreatedAt >= now.AddDays(-30), ct),
            await db.AiAnalyses.CountAsync(a => a.CreatedAt >= now.AddDays(-30) && a.Status == Domain.Incidents.AiAnalysisStatus.Failed, ct),
            await ai.GetStatusAsync(ct),
            new RetentionPolicyDto(org.AuditRetentionDays, retention.Value.LogRetentionDays, retention.Value.WebhookPayloadRetentionDays,
                retention.Value.ReadNotificationRetentionDays),
            await db.AuditEntries.Where(a => a.OrganizationId == orgId).OrderBy(a => a.At).Select(a => (DateTimeOffset?)a.At).FirstOrDefaultAsync(ct));
    }

    private void RequireAdmin()
    {
        if (tenant.RequireRole() < OrgRole.Admin)
        {
            throw new ForbiddenException("Only organization admins can access administration data.");
        }
    }
}

public sealed record RetentionRunResult(int AuditEntries, int LogEvents, int WebhookPayloads, int Notifications, int Sessions);

/// <summary>
/// Applies data-retention policies across all organizations. Runs as a system job (bypasses tenant filters on purpose) and
/// deletes with set-based SQL in batches; it never touches business records (projects, items, incidents, deployments).
/// </summary>
public sealed partial class RetentionService(IAppDbContext db, TimeProvider clock, IOptions<RetentionOptions> options, ILogger<RetentionService> logger)
{
    public async Task<RetentionRunResult> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var opts = options.Value;

        var audit = 0;
        var orgs = await db.Organizations.AsNoTracking().Select(o => new { o.Id, o.AuditRetentionDays }).ToListAsync(ct);
        foreach (var org in orgs)
        {
            var cutoff = now.AddDays(-org.AuditRetentionDays);
            audit += await db.AuditEntries.Where(a => a.OrganizationId == org.Id && a.At < cutoff).ExecuteDeleteAsync(ct);
        }

        var logCutoff = now.AddDays(-opts.LogRetentionDays);
        var logs = await db.LogEvents.IgnoreQueryFilters().Where(l => l.ReceivedAt < logCutoff).ExecuteDeleteAsync(ct);

        var payloadCutoff = now.AddDays(-opts.WebhookPayloadRetentionDays);
        var payloads = await db.WebhookDeliveries.IgnoreQueryFilters().Where(d => d.ReceivedAt < payloadCutoff && d.Payload != null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Payload, (string?)null), ct);

        var notificationCutoff = now.AddDays(-opts.ReadNotificationRetentionDays);
        var notifications = await db.Notifications.IgnoreQueryFilters().Where(n => n.ReadAt != null && n.ReadAt < notificationCutoff).ExecuteDeleteAsync(ct);

        var sessionCutoff = now.AddDays(-opts.ExpiredSessionRetentionDays);
        var sessions = await db.Sessions.Where(s => s.ExpiresAt < sessionCutoff || (s.RevokedAt != null && s.RevokedAt < sessionCutoff)).ExecuteDeleteAsync(ct);

        var result = new RetentionRunResult(audit, logs, payloads, notifications, sessions);
        LogRetention(logger, result.AuditEntries, result.LogEvents, result.WebhookPayloads, result.Notifications, result.Sessions);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention run: removed {Audit} audit entries, {Logs} log events, {Payloads} webhook payloads, {Notifications} notifications, {Sessions} sessions")]
    private static partial void LogRetention(ILogger logger, int audit, int logs, int payloads, int notifications, int sessions);
}
