using DevPilot.Application.Common;
using DevPilot.Application.Organizations;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Projects;

public sealed record UserRefDto(Guid Id, string DisplayName, string Email);

public sealed record ProjectSummaryDto(Guid Id, string Name, string Key, string? Description, ProjectStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int OpenItems, int TotalItems, int MemberCount, ProjectRole? MyRole);

public sealed record ProjectMemberDto(Guid UserId, string DisplayName, string Email, ProjectRole Role, OrgRole OrgRole, DateTimeOffset AddedAt);

public sealed record StatusDto(Guid Id, string Name, StatusCategory Category, int Position);

public sealed record LabelDto(Guid Id, string Name, string Color);

public sealed record TransitionDto(Guid FromStatusId, Guid ToStatusId);

public sealed record ProjectDetailDto(Guid Id, string Name, string Key, string? Description, ProjectStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, IReadOnlyList<ProjectMemberDto> Members, IReadOnlyList<StatusDto> Statuses,
    IReadOnlyList<TransitionDto> Transitions, IReadOnlyList<LabelDto> Labels, ProjectPermissions Permissions, int OpenItems, int TotalItems);

public sealed record CreateProjectRequest(string Name, string Key, string? Description);

public sealed record UpdateProjectRequest(string Name, string? Description);

public sealed record AddProjectMemberRequest(Guid UserId, ProjectRole Role);

public sealed record ChangeProjectMemberRoleRequest(ProjectRole Role);

public sealed record ProjectListQuery(string? Q, ProjectStatus? Status, string? Sort, int Page = 1, int PageSize = 25);

public sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(Project.MaxNameLength);
        RuleFor(x => x.Key).NotEmpty().Matches("^[A-Za-z][A-Za-z0-9]{1,9}$")
            .WithMessage("Key must be 2-10 letters or digits and start with a letter (e.g. API, WEB2).");
        RuleFor(x => x.Description).MaximumLength(Project.MaxDescriptionLength);
    }
}

public sealed class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(Project.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(Project.MaxDescriptionLength);
    }
}

public sealed class ProjectService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    ProjectAccess access,
    AuditLog audit,
    TimeProvider clock,
    IValidator<CreateProjectRequest> createValidator,
    IValidator<UpdateProjectRequest> updateValidator)
{
    private static readonly HashSet<string> SortFields = ["name", "key", "createdAt", "updatedAt"];

    public async Task<PagedResult<ProjectSummaryDto>> ListAsync(ProjectListQuery query, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var projects = db.Projects.AsNoTracking();
        if (query.Status is { } status)
        {
            projects = projects.Where(p => p.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim().ToLowerInvariant();
            projects = projects.Where(p => p.Name.ToLower().Contains(term) || p.Key.ToLower().Contains(term));
        }

        var sort = SortSpec.Parse(query.Sort, "updatedAt", true, SortFields);
        projects = (sort.Field, sort.Descending) switch
        {
            ("name", false) => projects.OrderBy(p => p.Name),
            ("name", true) => projects.OrderByDescending(p => p.Name),
            ("key", false) => projects.OrderBy(p => p.Key),
            ("key", true) => projects.OrderByDescending(p => p.Key),
            ("createdAt", false) => projects.OrderBy(p => p.CreatedAt),
            ("createdAt", true) => projects.OrderByDescending(p => p.CreatedAt),
            ("updatedAt", false) => projects.OrderBy(p => p.UpdatedAt),
            _ => projects.OrderByDescending(p => p.UpdatedAt),
        };

        var dtoQuery = projects.Select(p => new ProjectSummaryDto(
            p.Id, p.Name, p.Key, p.Description, p.Status, p.CreatedAt, p.UpdatedAt,
            db.WorkItems.Count(w => w.ProjectId == p.Id && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled),
            db.WorkItems.Count(w => w.ProjectId == p.Id),
            p.Members.Count,
            p.Members.Where(m => m.UserId == userId).Select(m => (ProjectRole?)m.Role).FirstOrDefault()));
        return await dtoQuery.ToPagedResultAsync(new PageRequest(query.Page, query.PageSize), ct);
    }

    public async Task<ProjectDetailDto> GetAsync(Guid projectId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct, tracking: false);
        var memberIds = project.Members.Select(m => m.UserId).ToList();
        var orgId = tenant.RequireOrganizationId();
        var users = await db.Users.AsNoTracking().Where(u => memberIds.Contains(u.Id))
            .Join(db.Memberships.Where(m => m.OrganizationId == orgId), u => u.Id, m => m.UserId, (u, m) => new { u.Id, u.DisplayName, u.Email, m.Role })
            .ToDictionaryAsync(x => x.Id, ct);
        var members = project.Members
            .Where(m => users.ContainsKey(m.UserId))
            .Select(m => new ProjectMemberDto(m.UserId, users[m.UserId].DisplayName, users[m.UserId].Email, m.Role, users[m.UserId].Role, m.AddedAt))
            .OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName)
            .ToList();

        var statuses = await db.WorkflowStatuses.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Position)
            .Select(s => new StatusDto(s.Id, s.Name, s.Category, s.Position)).ToListAsync(ct);
        var transitions = await db.WorkflowTransitions.AsNoTracking().Where(t => t.ProjectId == projectId)
            .Select(t => new TransitionDto(t.FromStatusId, t.ToStatusId)).ToListAsync(ct);
        var labels = await db.Labels.AsNoTracking().Where(l => l.ProjectId == projectId).OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color)).ToListAsync(ct);
        var total = await db.WorkItems.CountAsync(w => w.ProjectId == projectId, ct);
        var open = await db.WorkItems.CountAsync(w => w.ProjectId == projectId && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled, ct);

        return new ProjectDetailDto(project.Id, project.Name, project.Key, project.Description, project.Status, project.CreatedAt, project.UpdatedAt,
            project.ArchivedAt, members, statuses, transitions, labels, access.PermissionsFor(project), open, total);
    }

    public async Task<ProjectDetailDto> CreateAsync(CreateProjectRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        if (tenant.RequireRole() < OrgRole.Developer)
        {
            throw new ForbiddenException("Viewers cannot create projects.");
        }

        var orgId = tenant.RequireOrganizationId();
        var key = request.Key.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.Key == key, ct))
        {
            throw new ConflictException("project.key_taken", $"The key '{key}' is already used by another project in this organization.");
        }

        var project = Project.Create(orgId, request.Name, key, request.Description, currentUser.RequireUserId(), clock.GetUtcNow());
        var (statuses, transitions) = WorkflowTemplate.CreateDefault(orgId, project.Id);
        db.Projects.Add(project);
        db.WorkflowStatuses.AddRange(statuses);
        db.WorkflowTransitions.AddRange(transitions);
        audit.Record(orgId, "project.created", "project", project.Id.ToString(), new() { ["name"] = project.Name, ["key"] = project.Key });
        audit.Activity(orgId, project.Id, "project.created", $"creó el proyecto {project.Name} ({project.Key})", "project", project.Id);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ix_projects_organization_id_key", StringComparison.Ordinal) == true)
        {
            throw new ConflictException("project.key_taken", $"The key '{key}' is already used by another project in this organization.");
        }

        return await GetAsync(project.Id, ct);
    }

    public async Task<ProjectDetailDto> UpdateAsync(Guid projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        await updateValidator.ValidateAndThrowAsync(request, ct);
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        project.EnsureWritable();
        project.Update(request.Name, request.Description, clock.GetUtcNow());
        audit.Record(project.OrganizationId, "project.updated", "project", project.Id.ToString());
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    public async Task<ProjectDetailDto> ArchiveAsync(Guid projectId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        project.Archive(clock.GetUtcNow());
        audit.Record(project.OrganizationId, "project.archived", "project", project.Id.ToString());
        audit.Activity(project.OrganizationId, project.Id, "project.archived", $"archivó el proyecto {project.Name}", "project", project.Id);
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    public async Task<ProjectDetailDto> RestoreAsync(Guid projectId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        project.Restore(clock.GetUtcNow());
        audit.Record(project.OrganizationId, "project.restored", "project", project.Id.ToString());
        audit.Activity(project.OrganizationId, project.Id, "project.restored", $"restauró el proyecto {project.Name}", "project", project.Id);
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    /// <summary>Permanently deletes an archived project and all its items. Admin-only; requires archiving first as a safety step.</summary>
    public async Task DeleteAsync(Guid projectId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        if (!access.PermissionsFor(project).CanDelete)
        {
            throw new ForbiddenException("Only organization admins can delete projects.");
        }

        if (!project.IsArchived)
        {
            throw new ConflictException("project.not_archived", "Archive the project before deleting it.");
        }

        var itemCount = await db.WorkItems.CountAsync(w => w.ProjectId == projectId, ct);
        db.Projects.Remove(project);
        audit.Record(project.OrganizationId, "project.deleted", "project", project.Id.ToString(),
            new() { ["name"] = project.Name, ["key"] = project.Key, ["deletedItems"] = itemCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        audit.Activity(project.OrganizationId, null, "project.deleted", $"eliminó el proyecto {project.Name}", "project", null);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProjectDetailDto> AddMemberAsync(Guid projectId, AddProjectMemberRequest request, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        if (!Enum.IsDefined(request.Role))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Role), "Unknown project role.")]);
        }

        var orgId = tenant.RequireOrganizationId();
        var orgRole = await db.Memberships.Where(m => m.OrganizationId == orgId && m.UserId == request.UserId).Select(m => (OrgRole?)m.Role).FirstOrDefaultAsync(ct)
            ?? throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.UserId), "The user is not a member of this organization.")]);
        if (request.Role == ProjectRole.Lead && orgRole < OrgRole.Developer)
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Role), "Viewers cannot lead a project.")]);
        }

        project.AddMember(request.UserId, request.Role, clock.GetUtcNow());
        audit.Record(orgId, "project.member_added", "project", project.Id.ToString(), new() { ["userId"] = request.UserId.ToString(), ["role"] = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    public async Task<ProjectDetailDto> ChangeMemberRoleAsync(Guid projectId, Guid userId, ChangeProjectMemberRoleRequest request, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        if (!Enum.IsDefined(request.Role))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Role), "Unknown project role.")]);
        }

        project.ChangeMemberRole(userId, request.Role);
        audit.Record(project.OrganizationId, "project.member_role_changed", "project", project.Id.ToString(), new() { ["userId"] = userId.ToString(), ["role"] = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    public async Task<ProjectDetailDto> RemoveMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        project.RemoveMember(userId);
        audit.Record(project.OrganizationId, "project.member_removed", "project", project.Id.ToString(), new() { ["userId"] = userId.ToString() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }
}
