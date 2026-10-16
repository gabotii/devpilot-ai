using DevPilot.Domain.Common;

namespace DevPilot.Domain.Organizations;

/// <summary>Organization-level roles. Numeric values are ordered so that "at least X" checks are simple comparisons.</summary>
public enum OrgRole
{
    Viewer = 10,
    Developer = 20,
    Admin = 30,
    Owner = 40,
}

/// <summary>Tenant root. Owns the membership list and enforces role-management rules.</summary>
public sealed class Organization : Entity
{
    public const int MaxNameLength = 100;
    public const int DefaultAuditRetentionDays = 365;

    private readonly List<Membership> _members = [];

    private Organization() { }

    public static Organization Create(string name, string slug, Guid ownerUserId, DateTimeOffset now)
    {
        var org = new Organization
        {
            Name = name.Trim(),
            Slug = slug,
            CreatedAt = now,
            AuditRetentionDays = DefaultAuditRetentionDays,
        };
        org._members.Add(new Membership(org.Id, ownerUserId, OrgRole.Owner, now));
        return org;
    }

    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public int AuditRetentionDays { get; private set; }

    /// <summary>Incident sequence (INC-n). Incremented atomically in SQL.</summary>
    public int NextIncidentNumber { get; private set; } = 1;

    public IReadOnlyCollection<Membership> Members => _members.AsReadOnly();

    public void Rename(string name) => Name = name.Trim();

    public void SetAuditRetention(int days)
    {
        if (days is < 30 or > 3650)
        {
            throw new DomainException("org.invalid_retention", "Audit retention must be between 30 and 3650 days.");
        }

        AuditRetentionDays = days;
    }

    public Membership AddMember(Guid userId, OrgRole role, DateTimeOffset now)
    {
        if (_members.Any(m => m.UserId == userId))
        {
            throw new DomainException("org.already_member", "The user is already a member of this organization.");
        }

        var membership = new Membership(Id, userId, role, now);
        _members.Add(membership);
        return membership;
    }

    /// <summary>
    /// Owners may change any role. Admins may only manage members below Admin and grant at most Developer.
    /// The last Owner can never be demoted.
    /// </summary>
    public void ChangeRole(OrgRole actorRole, Guid targetUserId, OrgRole newRole)
    {
        var target = GetMember(targetUserId);
        EnsureCanManage(actorRole, target.Role, newRole);

        if (target.Role == OrgRole.Owner && newRole != OrgRole.Owner && OwnerCount() == 1)
        {
            throw new DomainException("org.last_owner", "The organization must keep at least one owner.");
        }

        target.ChangeRole(newRole);
    }

    public void RemoveMember(OrgRole actorRole, Guid actorUserId, Guid targetUserId)
    {
        var target = GetMember(targetUserId);
        var leavingSelf = actorUserId == targetUserId;
        if (!leavingSelf)
        {
            EnsureCanManage(actorRole, target.Role, null);
        }

        if (target.Role == OrgRole.Owner && OwnerCount() == 1)
        {
            throw new DomainException("org.last_owner", "The organization must keep at least one owner.");
        }

        _members.Remove(target);
    }

    public static bool CanInvite(OrgRole actorRole, OrgRole invitedRole) =>
        actorRole == OrgRole.Owner || (actorRole == OrgRole.Admin && invitedRole < OrgRole.Admin);

    private static void EnsureCanManage(OrgRole actorRole, OrgRole targetRole, OrgRole? newRole)
    {
        var allowed = actorRole switch
        {
            OrgRole.Owner => true,
            OrgRole.Admin => targetRole < OrgRole.Admin && (newRole is null || newRole < OrgRole.Admin),
            _ => false,
        };

        if (!allowed)
        {
            throw new ForbiddenDomainException("You are not allowed to manage this member's role.");
        }
    }

    private Membership GetMember(Guid userId) =>
        _members.FirstOrDefault(m => m.UserId == userId)
        ?? throw new DomainException("org.not_member", "The user is not a member of this organization.");

    private int OwnerCount() => _members.Count(m => m.Role == OrgRole.Owner);
}

public sealed class Membership
{
    private Membership() { }

    internal Membership(Guid organizationId, Guid userId, OrgRole role, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        JoinedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public OrgRole Role { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }

    internal void ChangeRole(OrgRole role) => Role = role;
}

/// <summary>A permission rule was violated inside the domain. Mapped to HTTP 403.</summary>
public sealed class ForbiddenDomainException(string message) : DomainException("forbidden", message);
