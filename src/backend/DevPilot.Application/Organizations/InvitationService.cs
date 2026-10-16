using DevPilot.Application.Common;
using DevPilot.Domain.Identity;
using DevPilot.Domain.Organizations;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Organizations;

public sealed record CreateInvitationRequest(string Email, OrgRole Role);

public sealed record InvitationDto(Guid Id, string Email, OrgRole Role, InvitationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// The accept token is returned once to the inviter so they can share the link. No email channel is configured by default,
/// so we never claim an email was sent.
/// </summary>
public sealed record InvitationCreatedDto(InvitationDto Invitation, string AcceptToken, bool EmailSent);

public sealed record InvitationPreviewDto(Guid Id, Guid OrganizationId, string OrganizationName, string Email, OrgRole Role, InvitationStatus Status,
    DateTimeOffset ExpiresAt, bool EmailMatchesCurrentUser);

public sealed record AcceptTokenRequest(string Token);

public sealed class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(User.MaxEmailLength).EmailAddress();
        RuleFor(x => x.Role).IsInEnum().NotEqual(OrgRole.Owner).WithMessage("Owners cannot be invited directly.");
    }
}

public sealed class InvitationService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    AuditLog audit,
    TimeProvider clock,
    IValidator<CreateInvitationRequest> validator)
{
    public async Task<InvitationCreatedDto> CreateAsync(CreateInvitationRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var orgId = tenant.RequireOrganizationId();
        if (!Organization.CanInvite(tenant.RequireRole(), request.Role))
        {
            throw new ForbiddenException("You are not allowed to invite members with this role.");
        }

        var email = User.NormalizeEmail(request.Email);
        var alreadyMember = await db.Memberships
            .Where(m => m.OrganizationId == orgId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Email)
            .AnyAsync(e => e == email, ct);
        if (alreadyMember)
        {
            throw new ConflictException("invitation.already_member", "This person is already a member of the organization.");
        }

        var now = clock.GetUtcNow();
        var previous = await db.Invitations.Where(i => i.Email == email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now).ToListAsync(ct);
        foreach (var old in previous)
        {
            old.Revoke(now);
        }

        var token = SecureTokens.Generate();
        var invitation = new Invitation(orgId, email, request.Role, SecureTokens.Hash(token), currentUser.RequireUserId(), now);
        db.Invitations.Add(invitation);
        audit.Record(orgId, "org.invitation_created", "invitation", invitation.Id.ToString(), new() { ["email"] = email, ["role"] = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return new InvitationCreatedDto(ToDto(invitation, now), token, EmailSent: false);
    }

    public async Task<IReadOnlyList<InvitationDto>> ListAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var items = await db.Invitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(ct);
        return items.Select(i => ToDto(i, now)).ToList();
    }

    public async Task RevokeAsync(Guid invitationId, CancellationToken ct)
    {
        if (tenant.RequireRole() < OrgRole.Admin)
        {
            throw new ForbiddenException();
        }

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct) ?? throw new NotFoundException("Invitation");
        invitation.Revoke(clock.GetUtcNow());
        audit.Record(invitation.OrganizationId, "org.invitation_revoked", "invitation", invitation.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Pending invitations addressed to the current user's email, across organizations.</summary>
    public async Task<IReadOnlyList<InvitationPreviewDto>> ListMineAsync(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var now = clock.GetUtcNow();
        var rows = await db.Invitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Email == user.Email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Join(db.Organizations, i => i.OrganizationId, o => o.Id, (i, o) => new { Invitation = i, OrgName = o.Name })
            .ToListAsync(ct);
        return rows.Select(r => ToPreview(r.Invitation, r.OrgName, user.Email, now)).ToList();
    }

    public async Task<InvitationPreviewDto> PreviewAsync(string token, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var (invitation, orgName) = await FindByTokenAsync(token, ct);
        return ToPreview(invitation, orgName, user.Email, clock.GetUtcNow());
    }

    public async Task<OrgSummaryDto> AcceptAsync(string token, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var (invitation, _) = await FindByTokenAsync(token, ct);
        return await AcceptInternalAsync(invitation, user, ct);
    }

    public async Task<OrgSummaryDto> AcceptByIdAsync(Guid invitationId, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var invitation = await db.Invitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == invitationId && i.Email == user.Email, ct)
            ?? throw new NotFoundException("Invitation");
        return await AcceptInternalAsync(invitation, user, ct);
    }

    private async Task<OrgSummaryDto> AcceptInternalAsync(Invitation invitation, User user, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        invitation.Accept(user.Id, user.Email, now);
        var org = await db.Organizations.Include(o => o.Members).FirstAsync(o => o.Id == invitation.OrganizationId, ct);
        org.AddMember(user.Id, invitation.Role, now);
        audit.Record(org.Id, "org.invitation_accepted", "invitation", invitation.Id.ToString(), new() { ["role"] = invitation.Role.ToString() });
        audit.Activity(org.Id, null, "org.member_joined", $"se unió a la organización como {RoleNames.Spanish(invitation.Role)}", "user", user.Id);
        await db.SaveChangesAsync(ct);
        return new OrgSummaryDto(org.Id, org.Name, org.Slug, invitation.Role);
    }

    private async Task<(Invitation Invitation, string OrgName)> FindByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            throw new NotFoundException("Invitation");
        }

        var hash = SecureTokens.Hash(token);
        var row = await db.Invitations.IgnoreQueryFilters()
            .Where(i => i.TokenHash == hash)
            .Join(db.Organizations, i => i.OrganizationId, o => o.Id, (i, o) => new { Invitation = i, OrgName = o.Name })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Invitation");
        return (row.Invitation, row.OrgName);
    }

    private async Task<User> CurrentUserAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new UnauthorizedException();
    }

    private static InvitationDto ToDto(Invitation i, DateTimeOffset now) => new(i.Id, i.Email, i.Role, i.GetStatus(now), i.CreatedAt, i.ExpiresAt);

    private static InvitationPreviewDto ToPreview(Invitation i, string orgName, string userEmail, DateTimeOffset now) =>
        new(i.Id, i.OrganizationId, orgName, i.Email, i.Role, i.GetStatus(now), i.ExpiresAt, string.Equals(i.Email, userEmail, StringComparison.Ordinal));
}
