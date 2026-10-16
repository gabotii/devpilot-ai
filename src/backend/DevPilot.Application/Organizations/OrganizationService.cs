using DevPilot.Application.Common;
using DevPilot.Domain.Organizations;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Organizations;

public sealed record OrgSummaryDto(Guid Id, string Name, string Slug, OrgRole Role);

public sealed record OrgDetailDto(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt, int AuditRetentionDays, OrgRole MyRole, int MemberCount);

public sealed record CreateOrganizationRequest(string Name);

public sealed record UpdateOrganizationRequest(string Name, int AuditRetentionDays);

public sealed record MemberDto(Guid UserId, string Email, string DisplayName, OrgRole Role, DateTimeOffset JoinedAt);

public sealed record ChangeRoleRequest(OrgRole Role);

public sealed class CreateOrganizationRequestValidator : AbstractValidator<CreateOrganizationRequest>
{
    public CreateOrganizationRequestValidator() => RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(Organization.MaxNameLength);
}

public sealed class UpdateOrganizationRequestValidator : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(Organization.MaxNameLength);
        RuleFor(x => x.AuditRetentionDays).InclusiveBetween(30, 3650);
    }
}

/// <summary>Resolves the caller's role in an organization. The single source of truth for tenant membership checks.</summary>
public sealed class OrganizationAccess(IAppDbContext db)
{
    public Task<OrgRole?> GetRoleAsync(Guid userId, Guid organizationId, CancellationToken ct) =>
        db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.OrganizationId == organizationId)
            .Select(m => (OrgRole?)m.Role)
            .FirstOrDefaultAsync(ct);
}

public sealed class OrganizationService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    AuditLog audit,
    TimeProvider clock,
    IValidator<CreateOrganizationRequest> createValidator,
    IValidator<UpdateOrganizationRequest> updateValidator)
{
    public async Task<IReadOnlyList<OrgSummaryDto>> ListMineAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return await MyOrganizations(db, userId).ToListAsync(ct);
    }

    /// <summary>Organizations the user belongs to, ordered by name (ordering happens before projection so EF can translate it).</summary>
    public static IQueryable<OrgSummaryDto> MyOrganizations(IAppDbContext db, Guid userId) =>
        db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { o.Id, o.Name, o.Slug, m.Role })
            .OrderBy(x => x.Name)
            .Select(x => new OrgSummaryDto(x.Id, x.Name, x.Slug, x.Role));

    public async Task<OrgSummaryDto> CreateAsync(CreateOrganizationRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        var userId = currentUser.RequireUserId();
        var owned = await db.Memberships.CountAsync(m => m.UserId == userId && m.Role == OrgRole.Owner, ct);
        if (owned >= 10)
        {
            throw new ConflictException("org.limit_reached", "You can own at most 10 organizations.");
        }

        var slug = await UniqueSlugAsync(Slug.From(request.Name), ct);
        var org = Organization.Create(request.Name, slug, userId, clock.GetUtcNow());
        db.Organizations.Add(org);
        audit.Record(org.Id, "org.created", "organization", org.Id.ToString(), new() { ["name"] = org.Name });
        audit.Activity(org.Id, null, "org.created", $"creó la organización {org.Name}", "organization", org.Id);
        await db.SaveChangesAsync(ct);
        return new OrgSummaryDto(org.Id, org.Name, org.Slug, OrgRole.Owner);
    }

    public async Task<OrgDetailDto> GetAsync(CancellationToken ct)
    {
        var orgId = tenant.RequireOrganizationId();
        var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct) ?? throw new NotFoundException("Organization");
        var count = await db.Memberships.CountAsync(m => m.OrganizationId == orgId, ct);
        return new OrgDetailDto(org.Id, org.Name, org.Slug, org.CreatedAt, org.AuditRetentionDays, tenant.RequireRole(), count);
    }

    public async Task<OrgDetailDto> UpdateAsync(UpdateOrganizationRequest request, CancellationToken ct)
    {
        await updateValidator.ValidateAndThrowAsync(request, ct);
        RequireRole(OrgRole.Admin);
        var org = await LoadAsync(includeMembers: false, ct);
        var before = (org.Name, org.AuditRetentionDays);
        org.Rename(request.Name);
        if (request.AuditRetentionDays != org.AuditRetentionDays)
        {
            RequireRole(OrgRole.Owner);
            org.SetAuditRetention(request.AuditRetentionDays);
        }

        audit.Record(org.Id, "org.updated", "organization", org.Id.ToString(), new()
        {
            ["name"] = before.Name == org.Name ? null : $"{before.Name} -> {org.Name}",
            ["auditRetentionDays"] = before.AuditRetentionDays == org.AuditRetentionDays ? null : $"{before.AuditRetentionDays} -> {org.AuditRetentionDays}",
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<PagedResult<MemberDto>> ListMembersAsync(string? search, PageRequest page, CancellationToken ct)
    {
        var orgId = tenant.RequireOrganizationId();
        var query = db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == orgId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.Email, u.DisplayName, m.Role, m.JoinedAt });
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(m => m.DisplayName.ToLower().Contains(term) || m.Email.ToLower().Contains(term));
        }

        return await query.OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName)
            .Select(m => new MemberDto(m.Id, m.Email, m.DisplayName, m.Role, m.JoinedAt))
            .ToPagedResultAsync(page, ct);
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid userId, ChangeRoleRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Role))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Role), "Unknown role.")]);
        }

        var org = await LoadAsync(includeMembers: true, ct);
        var previous = org.Members.FirstOrDefault(m => m.UserId == userId)?.Role ?? throw new NotFoundException("Member");
        org.ChangeRole(tenant.RequireRole(), userId, request.Role);
        audit.Record(org.Id, "org.member_role_changed", "user", userId.ToString(), new() { ["from"] = previous.ToString(), ["to"] = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var membership = org.Members.First(m => m.UserId == userId);
        return new MemberDto(user.Id, user.Email, user.DisplayName, membership.Role, membership.JoinedAt);
    }

    public async Task RemoveMemberAsync(Guid userId, CancellationToken ct)
    {
        var org = await LoadAsync(includeMembers: true, ct);
        if (org.Members.All(m => m.UserId != userId))
        {
            throw new NotFoundException("Member");
        }

        var actorId = currentUser.RequireUserId();
        org.RemoveMember(tenant.RequireRole(), actorId, userId);

        // Project memberships depend on org membership; drop them in the same transaction.
        var projectMemberships = await db.ProjectMembers.Where(pm => pm.UserId == userId).ToListAsync(ct);
        db.ProjectMembers.RemoveRange(projectMemberships);

        audit.Record(org.Id, actorId == userId ? "org.member_left" : "org.member_removed", "user", userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    private async Task<Organization> LoadAsync(bool includeMembers, CancellationToken ct)
    {
        var orgId = tenant.RequireOrganizationId();
        var query = db.Organizations.AsQueryable();
        if (includeMembers)
        {
            query = query.Include(o => o.Members);
        }

        return await query.FirstOrDefaultAsync(o => o.Id == orgId, ct) ?? throw new NotFoundException("Organization");
    }

    private void RequireRole(OrgRole minimum)
    {
        if (tenant.RequireRole() < minimum)
        {
            throw new ForbiddenException();
        }
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var i = 2; await db.Organizations.AnyAsync(o => o.Slug == slug, ct); i++)
        {
            slug = $"{baseSlug}-{i}";
        }

        return slug;
    }

}
