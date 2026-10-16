using DevPilot.Domain.Common;

namespace DevPilot.Domain.Organizations;

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}

public sealed class Invitation : Entity, IOrganizationScoped
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private Invitation() { }

    public Invitation(Guid organizationId, string email, OrgRole role, string tokenHash, Guid invitedByUserId, DateTimeOffset now)
    {
        if (role == OrgRole.Owner)
        {
            throw new DomainException("invitation.owner_role", "Owners cannot be invited directly; promote an existing member instead.");
        }

        OrganizationId = organizationId;
        Email = email.Trim().ToLowerInvariant();
        Role = role;
        TokenHash = tokenHash;
        InvitedByUserId = invitedByUserId;
        CreatedAt = now;
        ExpiresAt = now.Add(Lifetime);
    }

    public Guid OrganizationId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public OrgRole Role { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Guid InvitedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public InvitationStatus GetStatus(DateTimeOffset now) => this switch
    {
        { AcceptedAt: not null } => InvitationStatus.Accepted,
        { RevokedAt: not null } => InvitationStatus.Revoked,
        _ when now >= ExpiresAt => InvitationStatus.Expired,
        _ => InvitationStatus.Pending,
    };

    public void Accept(Guid userId, string userEmail, DateTimeOffset now)
    {
        EnsurePending(now);
        if (!string.Equals(userEmail, Email, StringComparison.Ordinal))
        {
            throw new DomainException("invitation.email_mismatch", "This invitation was issued for a different email address.");
        }

        AcceptedAt = now;
        AcceptedByUserId = userId;
    }

    public void Revoke(DateTimeOffset now)
    {
        EnsurePending(now);
        RevokedAt = now;
    }

    private void EnsurePending(DateTimeOffset now)
    {
        var status = GetStatus(now);
        if (status != InvitationStatus.Pending)
        {
            throw new DomainException("invitation.not_pending", $"The invitation is {status.ToString().ToLowerInvariant()}.");
        }
    }
}
