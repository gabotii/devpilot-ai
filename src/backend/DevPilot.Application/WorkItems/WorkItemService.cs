using System.Globalization;
using DevPilot.Application.Common;
using DevPilot.Application.Notifications;
using DevPilot.Application.Organizations;
using DevPilot.Application.Projects;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.WorkItems;

public sealed class WorkItemService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    ProjectAccess access,
    IWorkItemNumberAllocator numbers,
    Notifier notifier,
    AuditLog audit,
    TimeProvider clock,
    IValidator<CreateWorkItemRequest> createValidator,
    IValidator<UpdateWorkItemRequest> updateValidator,
    IValidator<CommentRequest> commentValidator)
{
    private static readonly HashSet<string> SortFields = ["number", "priority", "dueDate", "createdAt", "updatedAt", "title"];
    private const int BoardColumnLimit = 100;

    public async Task<PagedResult<WorkItemSummaryDto>> ListAsync(WorkItemListQuery query, CancellationToken ct)
    {
        if (query.ProjectId is { } projectId)
        {
            await access.LoadAsync(projectId, ct, tracking: false);
        }

        var items = ApplyFilters(db.WorkItems.AsNoTracking(), query);
        var sort = SortSpec.Parse(query.Sort, "updatedAt", true, SortFields);
        items = (sort.Field, sort.Descending) switch
        {
            ("number", false) => items.OrderBy(w => w.Number),
            ("number", true) => items.OrderByDescending(w => w.Number),
            ("priority", false) => items.OrderBy(w => w.Priority).ThenByDescending(w => w.UpdatedAt),
            ("priority", true) => items.OrderByDescending(w => w.Priority).ThenByDescending(w => w.UpdatedAt),
            ("dueDate", false) => items.OrderBy(w => w.DueDate == null).ThenBy(w => w.DueDate),
            ("dueDate", true) => items.OrderBy(w => w.DueDate == null).ThenByDescending(w => w.DueDate),
            ("createdAt", false) => items.OrderBy(w => w.CreatedAt),
            ("createdAt", true) => items.OrderByDescending(w => w.CreatedAt),
            ("title", false) => items.OrderBy(w => w.Title),
            ("title", true) => items.OrderByDescending(w => w.Title),
            ("updatedAt", false) => items.OrderBy(w => w.UpdatedAt),
            _ => items.OrderByDescending(w => w.UpdatedAt),
        };

        var page = new PageRequest(query.Page, query.PageSize);
        var total = await items.CountAsync(ct);
        var rows = await items.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var dtos = await ToSummariesAsync(rows, ct);
        return new PagedResult<WorkItemSummaryDto>(dtos, page.SafePage, page.SafePageSize, total);
    }

    public async Task<BoardDto> GetBoardAsync(Guid projectId, WorkItemListQuery filters, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct, tracking: false);
        var statuses = await db.WorkflowStatuses.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Position).ToListAsync(ct);
        var transitions = await db.WorkflowTransitions.AsNoTracking().Where(t => t.ProjectId == projectId)
            .Select(t => new TransitionDto(t.FromStatusId, t.ToStatusId)).ToListAsync(ct);

        // Closed columns only show recent items so the board stays usable on long-lived projects.
        var closedSince = clock.GetUtcNow().AddDays(-14);
        var baseQuery = ApplyFilters(db.WorkItems.AsNoTracking(), filters with { ProjectId = projectId, StatusId = null, Category = null, Open = null })
            .Where(w => (w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled) || w.UpdatedAt >= closedSince);

        var counts = await baseQuery.GroupBy(w => w.StatusId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = new List<WorkItem>();
        foreach (var status in statuses)
        {
            rows.AddRange(await baseQuery.Where(w => w.StatusId == status.Id)
                .OrderByDescending(w => w.Priority).ThenByDescending(w => w.UpdatedAt)
                .Take(BoardColumnLimit).ToListAsync(ct));
        }

        var summaries = (await ToSummariesAsync(rows, ct)).ToLookup(s => s.StatusId);
        var columns = statuses.Select(s => new BoardColumnDto(
            new StatusDto(s.Id, s.Name, s.Category, s.Position),
            summaries[s.Id].ToList(),
            counts.GetValueOrDefault(s.Id))).ToList();
        return new BoardDto(projectId, columns, transitions, access.PermissionsFor(project));
    }

    public async Task<WorkItemDetailDto> GetAsync(Guid projectId, int number, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct, tracking: false);
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Number == number, ct)
            ?? throw new NotFoundException("Work item");
        return await BuildDetailAsync(project, item, ct);
    }

    public async Task<WorkItemDetailDto> CreateAsync(Guid projectId, CreateWorkItemRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanEditItems(project);
        var workflow = await access.LoadWorkflowAsync(projectId, ct);

        var status = request.StatusId is { } statusId ? workflow.Get(statusId) : workflow.InitialStatus;
        if (status.IsClosed)
        {
            throw Invalid(nameof(request.StatusId), "New items must start in an open status.");
        }

        WorkItem? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == parentId && w.ProjectId == projectId, ct)
                ?? throw Invalid(nameof(request.ParentId), "The parent item does not exist in this project.");
        }

        if (request.AssigneeId is { } assigneeId)
        {
            EnsureAssignable(project, assigneeId);
        }

        var labelIds = await ValidateLabelsAsync(projectId, request.LabelIds, ct);
        var now = clock.GetUtcNow();
        var actorId = currentUser.RequireUserId();
        var number = await numbers.NextAsync(projectId, ct);
        var item = new WorkItem(project, number, request.Type, request.Title, request.Description, status, request.Priority, actorId, parent, now);
        item.Assign(request.AssigneeId, now);
        item.SetDueDate(request.DueDate, now);
        item.SetLabels(labelIds, now);
        db.WorkItems.Add(item);
        db.WorkItemChanges.Add(new WorkItemChange(item, actorId, "created", null, item.Title, now));
        audit.Activity(project.OrganizationId, project.Id, "workitem.created", $"creó {project.Key}-{number}: {item.Title}", "work_item", item.Id);
        if (item.AssigneeId is { } assigned && assigned != actorId)
        {
            NotifyAssigned(project, item, assigned);
        }

        await db.SaveChangesAsync(ct);
        return await BuildDetailAsync(project, item, ct);
    }

    public async Task<WorkItemDetailDto> UpdateAsync(Guid projectId, int number, UpdateWorkItemRequest request, CancellationToken ct)
    {
        await updateValidator.ValidateAndThrowAsync(request, ct);
        var (project, item) = await LoadForEditAsync(projectId, number, ct);
        EnsureVersion(item, request.Version);

        var now = clock.GetUtcNow();
        var actorId = currentUser.RequireUserId();
        var changes = new List<WorkItemChange>();

        if (item.Title != request.Title.Trim())
        {
            changes.Add(new WorkItemChange(item, actorId, "title", item.Title, request.Title.Trim(), now));
            item.Rename(request.Title, now);
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (item.Description != description)
        {
            changes.Add(new WorkItemChange(item, actorId, "description", null, null, now));
            item.Describe(description, now);
        }

        if (item.Priority != request.Priority)
        {
            changes.Add(new WorkItemChange(item, actorId, "priority", item.Priority.ToString(), request.Priority.ToString(), now));
            item.SetPriority(request.Priority, now);
        }

        if (item.AssigneeId != request.AssigneeId)
        {
            if (request.AssigneeId is { } assigneeId)
            {
                EnsureAssignable(project, assigneeId);
            }

            var names = await UsersAsync([item.AssigneeId, request.AssigneeId], ct);
            changes.Add(new WorkItemChange(item, actorId, "assignee", NameOf(names, item.AssigneeId), NameOf(names, request.AssigneeId), now));
            item.Assign(request.AssigneeId, now);
            if (request.AssigneeId is { } assigned && assigned != actorId)
            {
                NotifyAssigned(project, item, assigned);
            }
        }

        if (item.DueDate != request.DueDate)
        {
            changes.Add(new WorkItemChange(item, actorId, "dueDate", item.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                request.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), now));
            item.SetDueDate(request.DueDate, now);
        }

        var labelIds = await ValidateLabelsAsync(projectId, request.LabelIds, ct);
        var currentLabels = item.Labels.Select(l => l.LabelId).ToHashSet();
        if (!currentLabels.SetEquals(labelIds))
        {
            var names = await db.Labels.Where(l => l.ProjectId == projectId).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
            changes.Add(new WorkItemChange(item, actorId, "labels",
                string.Join(", ", currentLabels.Select(id => names.GetValueOrDefault(id, "?")).Order()),
                string.Join(", ", labelIds.Select(id => names.GetValueOrDefault(id, "?")).Order()), now));
            item.SetLabels(labelIds, now);
        }

        if (changes.Count > 0)
        {
            db.WorkItemChanges.AddRange(changes);
            await db.SaveChangesAsync(ct);
        }

        return await BuildDetailAsync(project, item, ct);
    }

    public async Task<WorkItemDetailDto> TransitionAsync(Guid projectId, int number, TransitionWorkItemRequest request, CancellationToken ct)
    {
        var (project, item) = await LoadForEditAsync(projectId, number, ct);
        if (request.Version is { } version)
        {
            EnsureVersion(item, version);
        }

        var workflow = await access.LoadWorkflowAsync(projectId, ct);
        var openSubtasks = await db.WorkItems.CountAsync(w => w.ParentId == item.Id && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled, ct);
        var from = workflow.Get(item.StatusId);
        var now = clock.GetUtcNow();
        item.TransitionTo(workflow, request.StatusId, openSubtasks, now);
        var to = workflow.Get(item.StatusId);
        if (from.Id != to.Id)
        {
            var actorId = currentUser.RequireUserId();
            db.WorkItemChanges.Add(new WorkItemChange(item, actorId, "status", from.Name, to.Name, now));
            var verb = to.Category switch
            {
                StatusCategory.Done => "completó",
                StatusCategory.Cancelled => "canceló",
                _ => $"movió a {to.Name}",
            };
            audit.Activity(project.OrganizationId, project.Id, "workitem.transitioned", $"{verb} {project.Key}-{item.Number}: {item.Title}", "work_item", item.Id);
            await db.SaveChangesAsync(ct);
        }

        return await BuildDetailAsync(project, item, ct);
    }

    /// <summary>Deletes an item and (via FK cascade) its subtasks, comments and history.</summary>
    public async Task DeleteAsync(Guid projectId, int number, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Number == number, ct) ?? throw new NotFoundException("Work item");
        var permissions = access.PermissionsFor(project);
        var isReporter = item.ReporterId == currentUser.RequireUserId();
        if (!(permissions.CanManage || (permissions.CanEditItems && isReporter)))
        {
            throw new ForbiddenException("Only project leads, admins or the reporter can delete this item.");
        }

        project.EnsureWritable();
        db.WorkItems.Remove(item);
        audit.Record(project.OrganizationId, "workitem.deleted", "work_item", item.Id.ToString(), new() { ["key"] = $"{project.Key}-{item.Number}" });
        audit.Activity(project.OrganizationId, project.Id, "workitem.deleted", $"eliminó {project.Key}-{item.Number}: {item.Title}", "work_item", null);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<CommentDto>> ListCommentsAsync(Guid projectId, int number, PageRequest page, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct, tracking: false);
        var item = await FindAsync(projectId, number, ct, tracking: false);
        var query = db.Comments.AsNoTracking().Where(c => c.WorkItemId == item.Id).OrderBy(c => c.CreatedAt);
        var total = await query.CountAsync(ct);
        var comments = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var users = await UsersAsync(comments.Select(c => (Guid?)c.AuthorId), ct);
        var userId = currentUser.RequireUserId();
        var canManage = access.PermissionsFor(project).CanManage;
        var dtos = comments.Select(c => new CommentDto(c.Id, users.GetValueOrDefault(c.AuthorId), c.Body, c.CreatedAt, c.EditedAt,
            CanEdit: c.AuthorId == userId && !project.IsArchived,
            CanDelete: (c.AuthorId == userId || canManage) && !project.IsArchived)).ToList();
        return new PagedResult<CommentDto>(dtos, page.SafePage, page.SafePageSize, total);
    }

    public async Task<CommentDto> AddCommentAsync(Guid projectId, int number, CommentRequest request, CancellationToken ct)
    {
        await commentValidator.ValidateAndThrowAsync(request, ct);
        var project = await access.LoadAsync(projectId, ct);
        project.EnsureWritable();
        if (tenant.RequireRole() < OrgRole.Developer)
        {
            throw new ForbiddenException("Viewers cannot comment.");
        }

        var item = await FindAsync(projectId, number, ct);
        var userId = currentUser.RequireUserId();
        var now = clock.GetUtcNow();
        var comment = new Comment(item, userId, request.Body, now);
        db.Comments.Add(comment);
        audit.Activity(project.OrganizationId, project.Id, "comment.created", $"comentó en {project.Key}-{item.Number}: {item.Title}", "work_item", item.Id);
        await db.SaveChangesAsync(ct);
        var users = await UsersAsync([userId], ct);
        return new CommentDto(comment.Id, users.GetValueOrDefault(userId), comment.Body, comment.CreatedAt, null, true, true);
    }

    public async Task<CommentDto> EditCommentAsync(Guid projectId, int number, Guid commentId, CommentRequest request, CancellationToken ct)
    {
        await commentValidator.ValidateAndThrowAsync(request, ct);
        var project = await access.LoadAsync(projectId, ct);
        project.EnsureWritable();
        var item = await FindAsync(projectId, number, ct, tracking: false);
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.WorkItemId == item.Id, ct) ?? throw new NotFoundException("Comment");
        var userId = currentUser.RequireUserId();
        comment.Edit(userId, request.Body, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        var users = await UsersAsync([userId], ct);
        return new CommentDto(comment.Id, users.GetValueOrDefault(userId), comment.Body, comment.CreatedAt, comment.EditedAt, true, true);
    }

    public async Task DeleteCommentAsync(Guid projectId, int number, Guid commentId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        project.EnsureWritable();
        var item = await FindAsync(projectId, number, ct, tracking: false);
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.WorkItemId == item.Id, ct) ?? throw new NotFoundException("Comment");
        if (comment.AuthorId != currentUser.RequireUserId() && !access.PermissionsFor(project).CanManage)
        {
            throw new ForbiddenException("Only the author or a project lead can delete this comment.");
        }

        db.Comments.Remove(comment);
        audit.Record(project.OrganizationId, "comment.deleted", "work_item", item.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkItemChangeDto>> ListHistoryAsync(Guid projectId, int number, CancellationToken ct)
    {
        await access.LoadAsync(projectId, ct, tracking: false);
        var item = await FindAsync(projectId, number, ct, tracking: false);
        var changes = await db.WorkItemChanges.AsNoTracking().Where(c => c.WorkItemId == item.Id).OrderByDescending(c => c.At).Take(200).ToListAsync(ct);
        var users = await UsersAsync(changes.Select(c => (Guid?)c.ActorId), ct);
        return changes.Select(c => new WorkItemChangeDto(c.Id, users.GetValueOrDefault(c.ActorId), c.Field, c.OldValue, c.NewValue, c.At)).ToList();
    }

    private IQueryable<WorkItem> ApplyFilters(IQueryable<WorkItem> items, WorkItemListQuery q)
    {
        if (q.ProjectId is { } projectId)
        {
            items = items.Where(w => w.ProjectId == projectId);
        }

        if (q.StatusId is { } statusId)
        {
            items = items.Where(w => w.StatusId == statusId);
        }

        if (q.Category is { } category)
        {
            items = items.Where(w => w.StatusCategory == category);
        }

        if (q.Open is { } open)
        {
            items = open
                ? items.Where(w => w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled)
                : items.Where(w => w.StatusCategory == StatusCategory.Done || w.StatusCategory == StatusCategory.Cancelled);
        }

        if (q.Unassigned == true)
        {
            items = items.Where(w => w.AssigneeId == null);
        }
        else if (q.AssigneeId is { } assigneeId)
        {
            items = items.Where(w => w.AssigneeId == assigneeId);
        }

        if (q.Priority is { } priority)
        {
            items = items.Where(w => w.Priority == priority);
        }

        if (q.Type is { } type)
        {
            items = items.Where(w => w.Type == type);
        }

        if (q.LabelId is { } labelId)
        {
            items = items.Where(w => w.Labels.Any(l => l.LabelId == labelId));
        }

        if (q.ParentId is { } parentId)
        {
            items = items.Where(w => w.ParentId == parentId);
        }

        if (q.Overdue == true)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            items = items.Where(w => w.DueDate < today && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled);
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var text = q.Q.Trim();
            var dash = text.LastIndexOf('-');
            var numberPart = dash >= 0 ? text[(dash + 1)..] : text;
            if (int.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                var term = text.ToLowerInvariant();
                items = items.Where(w => w.Number == n || w.Title.ToLower().Contains(term));
            }
            else
            {
                var term = text.ToLowerInvariant();
                items = items.Where(w => w.Title.ToLower().Contains(term) || (w.Description != null && w.Description.ToLower().Contains(term)));
            }
        }

        return items;
    }

    private async Task<List<WorkItemSummaryDto>> ToSummariesAsync(IReadOnlyCollection<WorkItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var ids = items.Select(i => i.Id).ToList();
        var projectIds = items.Select(i => i.ProjectId).Distinct().ToList();
        var labelsByItem = await db.WorkItems.AsNoTracking().Where(w => ids.Contains(w.Id))
            .SelectMany(w => w.Labels.Select(l => new { w.Id, l.LabelId }))
            .ToListAsync(ct);
        var labelIds = labelsByItem.Select(x => x.LabelId).Distinct().ToList();

        var projectKeys = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Key, ct);
        var statusNames = await db.WorkflowStatuses.AsNoTracking().Where(s => projectIds.Contains(s.ProjectId)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var labels = await db.Labels.AsNoTracking().Where(l => labelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new LabelDto(l.Id, l.Name, l.Color), ct);
        var users = await UsersAsync(items.Select(i => i.AssigneeId), ct);
        var subtaskCounts = await db.WorkItems.AsNoTracking().Where(w => w.ParentId != null && ids.Contains(w.ParentId.Value))
            .GroupBy(w => w.ParentId!.Value)
            .Select(g => new { g.Key, Total = g.Count(), Open = g.Count(w => w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled) })
            .ToDictionaryAsync(x => x.Key, ct);
        var labelLookup = labelsByItem.ToLookup(x => x.Id, x => x.LabelId);

        return items.Select(w => new WorkItemSummaryDto(
            w.Id, w.ProjectId, $"{projectKeys.GetValueOrDefault(w.ProjectId)}-{w.Number}", w.Number, w.Type, w.Title, w.StatusId,
            statusNames.GetValueOrDefault(w.StatusId, "?"), w.StatusCategory, w.Priority,
            w.AssigneeId is { } a ? users.GetValueOrDefault(a) : null, w.DueDate,
            labelLookup[w.Id].Where(labels.ContainsKey).Select(id => labels[id]).OrderBy(l => l.Name).ToList(),
            w.ParentId,
            subtaskCounts.TryGetValue(w.Id, out var sc) ? sc.Total : 0,
            subtaskCounts.TryGetValue(w.Id, out var so) ? so.Open : 0,
            w.CreatedAt, w.UpdatedAt, w.CompletedAt)).ToList();
    }

    private async Task<WorkItemDetailDto> BuildDetailAsync(Project project, WorkItem item, CancellationToken ct)
    {
        var summary = (await ToSummariesAsync([item], ct))[0];
        var reporter = (await UsersAsync([item.ReporterId], ct)).GetValueOrDefault(item.ReporterId);
        WorkItemRefDto? parent = null;
        if (item.ParentId is { } parentId)
        {
            parent = await db.WorkItems.AsNoTracking().Where(w => w.Id == parentId)
                .Select(w => new WorkItemRefDto(w.Id, project.Key + "-" + w.Number, w.Number, w.Title, w.StatusCategory))
                .FirstOrDefaultAsync(ct);
        }

        var subtaskRows = await db.WorkItems.AsNoTracking().Where(w => w.ParentId == item.Id).OrderBy(w => w.Number).ToListAsync(ct);
        var subtasks = await ToSummariesAsync(subtaskRows, ct);
        var commentCount = await db.Comments.CountAsync(c => c.WorkItemId == item.Id, ct);
        var workflow = await access.LoadWorkflowAsync(project.Id, ct);
        var allowed = workflow.Statuses
            .Where(s => s.Id != item.StatusId && workflow.CanTransition(item.StatusId, s.Id))
            .Select(s => new StatusDto(s.Id, s.Name, s.Category, s.Position))
            .ToList();
        return new WorkItemDetailDto(summary, item.Description, reporter, parent, subtasks, commentCount, item.Version, allowed, access.PermissionsFor(project));
    }

    private async Task<(Project Project, WorkItem Item)> LoadForEditAsync(Guid projectId, int number, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanEditItems(project);
        var item = await db.WorkItems.Include(w => w.Labels).FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Number == number, ct)
            ?? throw new NotFoundException("Work item");
        return (project, item);
    }

    private async Task<WorkItem> FindAsync(Guid projectId, int number, CancellationToken ct, bool tracking = true)
    {
        var query = tracking ? db.WorkItems : db.WorkItems.AsNoTracking();
        return await query.FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Number == number, ct) ?? throw new NotFoundException("Work item");
    }

    private void NotifyAssigned(Project project, WorkItem item, Guid assigneeId) =>
        notifier.Notify(project.OrganizationId, assigneeId, NotificationKinds.TaskAssigned,
            $"Se te asignó {project.Key}-{item.Number}", item.Title, $"/projects/{project.Id}/items/{item.Number}");

    private static void EnsureVersion(WorkItem item, uint expected)
    {
        if (item.Version != expected)
        {
            throw new ConflictException("workitem.stale", "This item was modified by someone else. Reload it and try again.");
        }
    }

    private static void EnsureAssignable(Project project, Guid assigneeId)
    {
        if (project.FindMember(assigneeId) is null)
        {
            throw Invalid("AssigneeId", "Only project members can be assigned. Add the person to the project first.");
        }
    }

    private async Task<List<Guid>> ValidateLabelsAsync(Guid projectId, IReadOnlyList<Guid>? labelIds, CancellationToken ct)
    {
        var requested = (labelIds ?? []).Distinct().ToList();
        if (requested.Count == 0)
        {
            return requested;
        }

        var valid = await db.Labels.Where(l => l.ProjectId == projectId && requested.Contains(l.Id)).CountAsync(ct);
        if (valid != requested.Count)
        {
            throw Invalid("LabelIds", "One or more labels do not belong to this project.");
        }

        return requested;
    }

    private async Task<Dictionary<Guid, UserRefDto>> UsersAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var set = ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (set.Count == 0)
        {
            return [];
        }

        return await db.Users.AsNoTracking().Where(u => set.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRefDto(u.Id, u.DisplayName, u.Email), ct);
    }


    private static string? NameOf(Dictionary<Guid, UserRefDto> users, Guid? id) => id is { } v && users.TryGetValue(v, out var u) ? u.DisplayName : null;

    private static ValidationException Invalid(string property, string message) => new([new ValidationFailure(property, message)]);
}
