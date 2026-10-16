using DevPilot.Application.Common;
using DevPilot.Application.Projects;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Dashboard;

public sealed record ActivityDto(Guid Id, Guid? ProjectId, UserRefDto? Actor, string Kind, string Summary, string SubjectType, Guid? SubjectId, DateTimeOffset At);

public sealed record PriorityCountDto(Priority Priority, int Count);

public sealed record WeeklyTrendDto(DateOnly WeekStart, int Created, int Completed);

public sealed record WorkSummaryDto(int ActiveProjects, int OpenItems, int InProgressItems, int CompletedLast30Days, int OpenIssues, int OverdueItems,
    int MyOpenItems);

public sealed record DashboardDto(WorkSummaryDto Work, IReadOnlyList<PriorityCountDto> OpenByPriority, IReadOnlyList<WeeklyTrendDto> CompletionTrend,
    IReadOnlyList<ActivityDto> RecentActivity, DateTimeOffset GeneratedAt);

public sealed class ActivityService(IAppDbContext db)
{
    public async Task<PagedResult<ActivityDto>> ListAsync(Guid? projectId, PageRequest page, CancellationToken ct)
    {
        var query = db.ActivityEntries.AsNoTracking();
        if (projectId is { } pid)
        {
            query = query.Where(a => a.ProjectId == pid);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.At).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var items = await ToDtosAsync(rows, ct);
        return new PagedResult<ActivityDto>(items, page.SafePage, page.SafePageSize, total);
    }

    internal async Task<List<ActivityDto>> ToDtosAsync(IReadOnlyCollection<Domain.Auditing.ActivityEntry> rows, CancellationToken ct)
    {
        var actorIds = rows.Where(r => r.ActorUserId.HasValue).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRefDto(u.Id, u.DisplayName, u.Email), ct);
        return rows.Select(r => new ActivityDto(r.Id, r.ProjectId, r.ActorUserId is { } a ? users.GetValueOrDefault(a) : null, r.Kind, r.Summary,
            r.SubjectType, r.SubjectId, r.At)).ToList();
    }
}

/// <summary>Organization dashboard. Every number is computed from persisted data at request time; nothing is sampled or simulated.</summary>
public sealed class DashboardService(IAppDbContext db, ICurrentUser currentUser, ActivityService activity, TimeProvider clock)
{
    public const int TrendWeeks = 8;

    public async Task<DashboardDto> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var userId = currentUser.RequireUserId();
        var items = db.WorkItems.AsNoTracking();
        var open = items.Where(w => w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled);

        var work = new WorkSummaryDto(
            ActiveProjects: await db.Projects.CountAsync(p => p.Status == ProjectStatus.Active, ct),
            OpenItems: await open.CountAsync(ct),
            InProgressItems: await items.CountAsync(w => w.StatusCategory == StatusCategory.InProgress, ct),
            CompletedLast30Days: await items.CountAsync(w => w.CompletedAt >= now.AddDays(-30), ct),
            OpenIssues: await open.CountAsync(w => w.Type == WorkItemType.Issue, ct),
            OverdueItems: await open.CountAsync(w => w.DueDate < today, ct),
            MyOpenItems: await open.CountAsync(w => w.AssigneeId == userId, ct));

        var byPriority = await open.GroupBy(w => w.Priority).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var openByPriority = Enum.GetValues<Priority>().OrderByDescending(p => p)
            .Select(p => new PriorityCountDto(p, byPriority.FirstOrDefault(x => x.Key == p)?.Count ?? 0)).ToList();

        var trend = await BuildTrendAsync(items, today, ct);
        var recentRows = await db.ActivityEntries.AsNoTracking().OrderByDescending(a => a.At).Take(10).ToListAsync(ct);
        var recent = await activity.ToDtosAsync(recentRows, ct);
        return new DashboardDto(work, openByPriority, trend, recent, now);
    }

    private static async Task<List<WeeklyTrendDto>> BuildTrendAsync(IQueryable<WorkItem> items, DateOnly today, CancellationToken ct)
    {
        var currentWeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // ISO week, Monday start
        var firstWeek = currentWeekStart.AddDays(-7 * (TrendWeeks - 1));
        var since = new DateTimeOffset(firstWeek.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var created = await items.Where(w => w.CreatedAt >= since).Select(w => w.CreatedAt).ToListAsync(ct);
        var completed = await items.Where(w => w.CompletedAt >= since).Select(w => w.CompletedAt!.Value).ToListAsync(ct);

        int WeekIndex(DateTimeOffset at) => (DateOnly.FromDateTime(at.UtcDateTime).DayNumber - firstWeek.DayNumber) / 7;
        var createdCounts = created.GroupBy(WeekIndex).ToDictionary(g => g.Key, g => g.Count());
        var completedCounts = completed.GroupBy(WeekIndex).ToDictionary(g => g.Key, g => g.Count());

        return Enumerable.Range(0, TrendWeeks)
            .Select(i => new WeeklyTrendDto(firstWeek.AddDays(7 * i), createdCounts.GetValueOrDefault(i), completedCounts.GetValueOrDefault(i)))
            .ToList();
    }
}
