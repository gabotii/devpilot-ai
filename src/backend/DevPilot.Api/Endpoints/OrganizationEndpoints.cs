using DevPilot.Api.Security;
using DevPilot.Application.Common;
using DevPilot.Application.Dashboard;
using DevPilot.Application.Notifications;
using DevPilot.Application.Organizations;
using DevPilot.Domain.Organizations;

namespace DevPilot.Api.Endpoints;

public static class OrganizationEndpoints
{
    /// <summary>Route group for everything scoped to one organization; membership and role are enforced by the filter.</summary>
    public static RouteGroupBuilder MapOrganizationScope(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/orgs/{orgId:guid}")
            .RequireAuthorization()
            .AddEndpointFilter<OrganizationContextFilter>();

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app, RouteGroupBuilder org)
    {
        var root = app.MapGroup("/api/orgs").WithTags("Organizations").RequireAuthorization();
        root.MapGet("/", (OrganizationService service, CancellationToken ct) => service.ListMineAsync(ct));
        root.MapPost("/", async (CreateOrganizationRequest request, OrganizationService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/orgs/{created.Id}", created);
        });

        var scoped = org.MapGroup("").WithTags("Organizations");
        scoped.MapGet("/", (OrganizationService service, CancellationToken ct) => service.GetAsync(ct));
        scoped.MapPut("/", (UpdateOrganizationRequest request, OrganizationService service, CancellationToken ct) => service.UpdateAsync(request, ct))
            .RequireOrgRole(OrgRole.Admin);

        scoped.MapGet("/members", (string? q, int? page, int? pageSize, OrganizationService service, CancellationToken ct) =>
            service.ListMembersAsync(q, new PageRequest(page ?? 1, pageSize ?? 50), ct));
        scoped.MapPut("/members/{userId:guid}/role", (Guid userId, ChangeRoleRequest request, OrganizationService service, CancellationToken ct) =>
            service.ChangeRoleAsync(userId, request, ct)).RequireOrgRole(OrgRole.Admin);
        scoped.MapDelete("/members/{userId:guid}", async (Guid userId, OrganizationService service, CancellationToken ct) =>
        {
            await service.RemoveMemberAsync(userId, ct);
            return Results.NoContent();
        }).WithSummary("Remove a member (Admin+), or leave the organization when userId is yourself");

        scoped.MapGet("/invitations", (InvitationService service, CancellationToken ct) => service.ListAsync(ct)).RequireOrgRole(OrgRole.Admin);
        scoped.MapPost("/invitations", async (CreateInvitationRequest request, InvitationService service, CancellationToken ct) =>
            Results.Created((string?)null, await service.CreateAsync(request, ct))).RequireOrgRole(OrgRole.Admin);
        scoped.MapDelete("/invitations/{invitationId:guid}", async (Guid invitationId, InvitationService service, CancellationToken ct) =>
        {
            await service.RevokeAsync(invitationId, ct);
            return Results.NoContent();
        }).RequireOrgRole(OrgRole.Admin);

        scoped.MapGet("/dashboard", (DashboardService service, CancellationToken ct) => service.GetAsync(ct)).WithTags("Dashboard");
        scoped.MapGet("/activity", (Guid? projectId, int? page, int? pageSize, ActivityService service, CancellationToken ct) =>
            service.ListAsync(projectId, new PageRequest(page ?? 1, pageSize ?? 25), ct)).WithTags("Dashboard");

        var notifications = org.MapGroup("/notifications").WithTags("Notifications");
        notifications.MapGet("/", (bool? unread, int? page, int? pageSize, NotificationService service, CancellationToken ct) =>
            service.ListAsync(unread ?? false, new PageRequest(page ?? 1, pageSize ?? 20), ct));
        notifications.MapPost("/{notificationId:guid}/read", async (Guid notificationId, NotificationService service, CancellationToken ct) =>
        {
            await service.MarkReadAsync(notificationId, ct);
            return Results.NoContent();
        });
        notifications.MapPost("/read-all", async (NotificationService service, CancellationToken ct) =>
            Results.Ok(new { updated = await service.MarkAllReadAsync(ct) }));

        return app;
    }
}
