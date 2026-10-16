using DevPilot.Application.Common;
using DevPilot.Application.Organizations;
using DevPilot.Domain.Organizations;

namespace DevPilot.Api.Security;

/// <summary>Endpoint metadata: minimum organization role required.</summary>
public sealed record RequireOrgRoleMetadata(OrgRole MinimumRole);

/// <summary>
/// Resolves the organization from the "orgId" route value, verifies the caller is a member (404 otherwise, so the existence of
/// other tenants is not revealed), enforces the minimum role and sets the tenant context used by query filters.
/// The client-supplied organization id is never trusted without this check.
/// </summary>
public sealed class OrganizationContextFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["orgId"]?.ToString(), out var orgId))
        {
            return Results.Problem(statusCode: 404, title: "Not Found", detail: "Organization was not found.");
        }

        var currentUser = http.RequestServices.GetRequiredService<ICurrentUser>();
        if (currentUser.UserId is not { } userId)
        {
            return Results.Problem(statusCode: 401, title: "Unauthorized");
        }

        var access = http.RequestServices.GetRequiredService<OrganizationAccess>();
        var role = await access.GetRoleAsync(userId, orgId, http.RequestAborted);
        if (role is null)
        {
            return Results.Problem(statusCode: 404, title: "Not Found", detail: "Organization was not found.");
        }

        var required = http.GetEndpoint()?.Metadata.GetOrderedMetadata<RequireOrgRoleMetadata>().Select(m => m.MinimumRole).DefaultIfEmpty(OrgRole.Viewer).Max()
            ?? OrgRole.Viewer;
        if (role < required)
        {
            return Results.Problem(statusCode: 403, title: "Forbidden", detail: $"This action requires the {required} role or higher.",
                extensions: new Dictionary<string, object?> { ["code"] = "org.insufficient_role" });
        }

        http.RequestServices.GetRequiredService<ITenantContext>().SetOrganization(orgId, role.Value);
        return await next(context);
    }
}

public static class OrganizationAuthorizationExtensions
{
    public static TBuilder RequireOrgRole<TBuilder>(this TBuilder builder, OrgRole minimumRole)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequireOrgRoleMetadata(minimumRole));
}
