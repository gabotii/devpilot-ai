using DevPilot.Application.Common;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Projects;

/// <summary>
/// Effective permissions of the caller on a project.
/// All organization members can read every project of the organization. Writing work items requires being a project
/// member with at least Developer org role (or Admin+). Managing project settings requires project Lead (or Admin+).
/// </summary>
public sealed record ProjectPermissions(bool CanEditItems, bool CanManage, bool CanDelete)
{
    public static ProjectPermissions For(OrgRole orgRole, ProjectRole? projectRole, bool archived)
    {
        var isAdmin = orgRole >= OrgRole.Admin;
        var isDeveloper = orgRole >= OrgRole.Developer;
        return new ProjectPermissions(
            CanEditItems: !archived && (isAdmin || (isDeveloper && projectRole is not null)),
            CanManage: isAdmin || (isDeveloper && projectRole == ProjectRole.Lead),
            CanDelete: isAdmin);
    }
}

public sealed class ProjectAccess(IAppDbContext db, ICurrentUser currentUser, ITenantContext tenant)
{
    /// <summary>Loads a project of the current organization (tenant filter applies) or throws 404.</summary>
    public async Task<Project> LoadAsync(Guid projectId, CancellationToken ct, bool tracking = true)
    {
        var query = db.Projects.Include(p => p.Members).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
    }

    public ProjectPermissions PermissionsFor(Project project)
    {
        var userId = currentUser.RequireUserId();
        return ProjectPermissions.For(tenant.RequireRole(), project.FindMember(userId)?.Role, project.IsArchived);
    }

    public void EnsureCanEditItems(Project project)
    {
        project.EnsureWritable();
        if (!PermissionsFor(project).CanEditItems)
        {
            throw new ForbiddenException("You must be a member of this project with at least the Developer role to change its items.");
        }
    }

    public void EnsureCanManage(Project project)
    {
        if (!PermissionsFor(project).CanManage)
        {
            throw new ForbiddenException("Only project leads and organization admins can manage this project.");
        }
    }

    public async Task<Workflow> LoadWorkflowAsync(Guid projectId, CancellationToken ct)
    {
        var statuses = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync(ct);
        var transitions = await db.WorkflowTransitions.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(ct);
        return new Workflow(statuses, transitions);
    }
}
