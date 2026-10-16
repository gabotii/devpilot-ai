using DevPilot.Application.Common;
using DevPilot.Domain.Notifications;
using DevPilot.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Notifications;

public static class NotificationKinds
{
    public const string TaskAssigned = "task.assigned";
    public const string PipelineFailed = "pipeline.failed";
    public const string DeploymentFailed = "deployment.failed";
    public const string DeploymentSucceeded = "deployment.succeeded";
    public const string IncidentOpened = "incident.opened";
    public const string IntegrationError = "integration.error";
}

public sealed record NotificationDto(Guid Id, string Kind, string Title, string? Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationListDto(PagedResult<NotificationDto> Page, int UnreadCount);

/// <summary>
/// Writes notifications into the current unit of work so they commit atomically with the change that caused them.
/// Duplicate suppression: a (user, dedupeKey) pair is checked in-transaction and backed by a unique index.
/// </summary>
public sealed class Notifier(IAppDbContext db, TimeProvider clock)
{
    private readonly HashSet<(Guid UserId, string Key)> _pending = [];

    public void Notify(Guid organizationId, Guid userId, string kind, string title, string? body, string? link, string? dedupeKey = null)
    {
        if (dedupeKey is not null && !_pending.Add((userId, dedupeKey)))
        {
            return;
        }

        db.Notifications.Add(new Notification(organizationId, userId, kind, title, body, link, dedupeKey, clock.GetUtcNow()));
    }

    /// <summary>Notifies every member with at least <paramref name="minimumRole"/>, skipping users already notified for the dedupe key.</summary>
    public async Task NotifyRoleAsync(Guid organizationId, OrgRole minimumRole, string kind, string title, string? body, string? link, string dedupeKey,
        CancellationToken ct)
    {
        var recipients = await db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.Role >= minimumRole)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        var already = await db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.OrganizationId == organizationId && n.DedupeKey == dedupeKey)
            .Select(n => n.UserId)
            .ToListAsync(ct);
        foreach (var userId in recipients.Except(already))
        {
            Notify(organizationId, userId, kind, title, body, link, dedupeKey);
        }
    }
}

public sealed class NotificationService(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<NotificationListDto> ListAsync(bool unreadOnly, PageRequest page, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        var unread = await query.CountAsync(n => n.ReadAt == null, ct);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var result = await query.OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt))
            .ToPagedResultAsync(page, ct);
        return new NotificationListDto(result, unread);
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct)
            ?? throw new NotFoundException("Notification");
        notification.MarkRead(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.GetUtcNow();
        var unread = await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).Take(1000).ToListAsync(ct);
        foreach (var notification in unread)
        {
            notification.MarkRead(now);
        }

        await db.SaveChangesAsync(ct);
        return unread.Count;
    }
}
