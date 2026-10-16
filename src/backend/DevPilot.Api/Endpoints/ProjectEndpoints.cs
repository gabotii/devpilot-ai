using DevPilot.Application.Common;
using DevPilot.Application.Projects;
using DevPilot.Application.WorkItems;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;

namespace DevPilot.Api.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this RouteGroupBuilder org)
    {
        var projects = org.MapGroup("/projects").WithTags("Projects");
        projects.MapGet("/", (string? q, ProjectStatus? status, string? sort, int? page, int? pageSize, ProjectService service, CancellationToken ct) =>
            service.ListAsync(new ProjectListQuery(q, status, sort, page ?? 1, pageSize ?? 25), ct));
        projects.MapPost("/", async (CreateProjectRequest request, ProjectService service, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"{http.Request.Path}/{created.Id}", created);
        });

        var project = projects.MapGroup("/{projectId:guid}");
        project.MapGet("/", (Guid projectId, ProjectService service, CancellationToken ct) => service.GetAsync(projectId, ct));
        project.MapPut("/", (Guid projectId, UpdateProjectRequest request, ProjectService service, CancellationToken ct) => service.UpdateAsync(projectId, request, ct));
        project.MapPost("/archive", (Guid projectId, ProjectService service, CancellationToken ct) => service.ArchiveAsync(projectId, ct));
        project.MapPost("/restore", (Guid projectId, ProjectService service, CancellationToken ct) => service.RestoreAsync(projectId, ct));
        project.MapDelete("/", async (Guid projectId, ProjectService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(projectId, ct);
            return Results.NoContent();
        }).WithSummary("Permanently delete an archived project (Admin+)");

        project.MapPost("/members", (Guid projectId, AddProjectMemberRequest request, ProjectService service, CancellationToken ct) =>
            service.AddMemberAsync(projectId, request, ct));
        project.MapPut("/members/{userId:guid}", (Guid projectId, Guid userId, ChangeProjectMemberRoleRequest request, ProjectService service, CancellationToken ct) =>
            service.ChangeMemberRoleAsync(projectId, userId, request, ct));
        project.MapDelete("/members/{userId:guid}", (Guid projectId, Guid userId, ProjectService service, CancellationToken ct) =>
            service.RemoveMemberAsync(projectId, userId, ct));

        project.MapGet("/workflow", (Guid projectId, ProjectSettingsService service, CancellationToken ct) => service.GetWorkflowAsync(projectId, ct));
        project.MapPost("/workflow/statuses", (Guid projectId, UpsertStatusRequest request, ProjectSettingsService service, CancellationToken ct) =>
            service.AddStatusAsync(projectId, request, ct));
        project.MapPut("/workflow/statuses/{statusId:guid}", (Guid projectId, Guid statusId, UpsertStatusRequest request, ProjectSettingsService service, CancellationToken ct) =>
            service.UpdateStatusAsync(projectId, statusId, request, ct));
        project.MapDelete("/workflow/statuses/{statusId:guid}", (Guid projectId, Guid statusId, ProjectSettingsService service, CancellationToken ct) =>
            service.DeleteStatusAsync(projectId, statusId, ct));
        project.MapPut("/workflow/transitions", (Guid projectId, SetTransitionsRequest request, ProjectSettingsService service, CancellationToken ct) =>
            service.SetTransitionsAsync(projectId, request, ct));

        project.MapGet("/labels", (Guid projectId, ProjectSettingsService service, CancellationToken ct) => service.ListLabelsAsync(projectId, ct));
        project.MapPost("/labels", (Guid projectId, UpsertLabelRequest request, ProjectSettingsService service, CancellationToken ct) =>
            service.CreateLabelAsync(projectId, request, ct));
        project.MapPut("/labels/{labelId:guid}", (Guid projectId, Guid labelId, UpsertLabelRequest request, ProjectSettingsService service, CancellationToken ct) =>
            service.UpdateLabelAsync(projectId, labelId, request, ct));
        project.MapDelete("/labels/{labelId:guid}", async (Guid projectId, Guid labelId, ProjectSettingsService service, CancellationToken ct) =>
        {
            await service.DeleteLabelAsync(projectId, labelId, ct);
            return Results.NoContent();
        });

        project.MapGet("/board", (Guid projectId, string? q, string? assignee, WorkItemType? type, Priority? priority, Guid? labelId,
            WorkItemService service, Application.Common.ICurrentUser user, CancellationToken ct) =>
            service.GetBoardAsync(projectId, new WorkItemListQuery(Q: q, AssigneeId: ResolveAssignee(assignee, user), Unassigned: assignee == "none",
                Type: type, Priority: priority, LabelId: labelId), ct)).WithTags("Work items");

        MapWorkItems(org, project);
    }

    private static void MapWorkItems(RouteGroupBuilder org, RouteGroupBuilder project)
    {
        org.MapGet("/work-items", ([AsParameters] WorkItemQueryParameters p, WorkItemService service, ICurrentUser user, CancellationToken ct) =>
            service.ListAsync(p.ToQuery(null, user), ct)).WithTags("Work items").WithSummary("Search work items across all projects of the organization");

        var items = project.MapGroup("/items").WithTags("Work items");
        items.MapGet("/", (Guid projectId, [AsParameters] WorkItemQueryParameters p, WorkItemService service, ICurrentUser user, CancellationToken ct) =>
            service.ListAsync(p.ToQuery(projectId, user), ct));
        items.MapPost("/", async (Guid projectId, CreateWorkItemRequest request, WorkItemService service, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(projectId, request, ct);
            return Results.Created($"{http.Request.Path}/{created.Item.Number}", created);
        });
        items.MapGet("/{number:int}", (Guid projectId, int number, WorkItemService service, CancellationToken ct) => service.GetAsync(projectId, number, ct));
        items.MapPut("/{number:int}", (Guid projectId, int number, UpdateWorkItemRequest request, WorkItemService service, CancellationToken ct) =>
            service.UpdateAsync(projectId, number, request, ct));
        items.MapPost("/{number:int}/transition", (Guid projectId, int number, TransitionWorkItemRequest request, WorkItemService service, CancellationToken ct) =>
            service.TransitionAsync(projectId, number, request, ct));
        items.MapDelete("/{number:int}", async (Guid projectId, int number, WorkItemService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(projectId, number, ct);
            return Results.NoContent();
        });
        items.MapGet("/{number:int}/history", (Guid projectId, int number, WorkItemService service, CancellationToken ct) =>
            service.ListHistoryAsync(projectId, number, ct));
        items.MapGet("/{number:int}/comments", (Guid projectId, int number, int? page, int? pageSize, WorkItemService service, CancellationToken ct) =>
            service.ListCommentsAsync(projectId, number, new PageRequest(page ?? 1, pageSize ?? 50), ct));
        items.MapPost("/{number:int}/comments", (Guid projectId, int number, CommentRequest request, WorkItemService service, CancellationToken ct) =>
            service.AddCommentAsync(projectId, number, request, ct));
        items.MapPut("/{number:int}/comments/{commentId:guid}", (Guid projectId, int number, Guid commentId, CommentRequest request, WorkItemService service, CancellationToken ct) =>
            service.EditCommentAsync(projectId, number, commentId, request, ct));
        items.MapDelete("/{number:int}/comments/{commentId:guid}", async (Guid projectId, int number, Guid commentId, WorkItemService service, CancellationToken ct) =>
        {
            await service.DeleteCommentAsync(projectId, number, commentId, ct);
            return Results.NoContent();
        });
    }

    /// <summary>"me" resolves to the caller; "none" means unassigned (handled separately); otherwise a user id.</summary>
    private static Guid? ResolveAssignee(string? assignee, ICurrentUser user) => assignee switch
    {
        null or "" or "none" => null,
        "me" => user.UserId,
        _ when Guid.TryParse(assignee, out var id) => id,
        _ => throw new FluentValidation.ValidationException([new FluentValidation.Results.ValidationFailure("assignee", "Use 'me', 'none' or a user id.")]),
    };

    public sealed record WorkItemQueryParameters(
        string? Q,
        Guid? StatusId,
        StatusCategory? Category,
        bool? Open,
        string? Assignee,
        Priority? Priority,
        WorkItemType? Type,
        Guid? LabelId,
        Guid? ParentId,
        bool? Overdue,
        string? Sort,
        int? Page,
        int? PageSize)
    {
        public WorkItemListQuery ToQuery(Guid? projectId, ICurrentUser user) => new(
            Q, projectId, StatusId, Category, Open, ResolveAssignee(Assignee, user), Assignee == "none", Priority, Type, LabelId, ParentId, Overdue, Sort,
            Page ?? 1, PageSize ?? 25);
    }
}
