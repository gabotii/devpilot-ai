using DevPilot.Domain.Common;

namespace DevPilot.Domain.Projects;

public enum ProjectStatus
{
    Active,
    Archived,
}

public enum ProjectRole
{
    Member = 10,
    Lead = 20,
}

public sealed class Project : Entity, IOrganizationScoped
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 4000;

    private readonly List<ProjectMember> _members = [];

    private Project() { }

    public static Project Create(Guid organizationId, string name, string key, string? description, Guid creatorUserId, DateTimeOffset now)
    {
        var project = new Project
        {
            OrganizationId = organizationId,
            Name = name.Trim(),
            Key = key.Trim().ToUpperInvariant(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Status = ProjectStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = creatorUserId,
            NextItemNumber = 1,
        };
        project._members.Add(new ProjectMember(project.Id, organizationId, creatorUserId, ProjectRole.Lead, now));
        return project;
    }

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Short immutable prefix used in work item keys (e.g. "API-42").</summary>
    public string Key { get; private set; } = string.Empty;

    public string? Description { get; private set; }
    public ProjectStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    /// <summary>Per-project sequence. Incremented atomically in SQL; never mutated through the entity.</summary>
    public int NextItemNumber { get; private set; }

    public IReadOnlyCollection<ProjectMember> Members => _members.AsReadOnly();

    public bool IsArchived => Status == ProjectStatus.Archived;

    public void Update(string name, string? description, DateTimeOffset now)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (IsArchived)
        {
            throw new DomainException("project.already_archived", "The project is already archived.");
        }

        Status = ProjectStatus.Archived;
        ArchivedAt = now;
        UpdatedAt = now;
    }

    public void Restore(DateTimeOffset now)
    {
        if (!IsArchived)
        {
            throw new DomainException("project.not_archived", "The project is not archived.");
        }

        Status = ProjectStatus.Active;
        ArchivedAt = null;
        UpdatedAt = now;
    }

    public void EnsureWritable()
    {
        if (IsArchived)
        {
            throw new DomainException("project.archived", "The project is archived and read-only. Restore it to make changes.");
        }
    }

    public ProjectMember? FindMember(Guid userId) => _members.FirstOrDefault(m => m.UserId == userId);

    public ProjectMember AddMember(Guid userId, ProjectRole role, DateTimeOffset now)
    {
        if (FindMember(userId) is not null)
        {
            throw new DomainException("project.already_member", "The user is already a member of this project.");
        }

        var member = new ProjectMember(Id, OrganizationId, userId, role, now);
        _members.Add(member);
        return member;
    }

    public void ChangeMemberRole(Guid userId, ProjectRole role)
    {
        var member = FindMember(userId) ?? throw new DomainException("project.not_member", "The user is not a member of this project.");
        member.ChangeRole(role);
    }

    public void RemoveMember(Guid userId)
    {
        var member = FindMember(userId) ?? throw new DomainException("project.not_member", "The user is not a member of this project.");
        _members.Remove(member);
    }
}

public sealed class ProjectMember : IOrganizationScoped
{
    private ProjectMember() { }

    internal ProjectMember(Guid projectId, Guid organizationId, Guid userId, ProjectRole role, DateTimeOffset now)
    {
        ProjectId = projectId;
        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        AddedAt = now;
    }

    public Guid ProjectId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public ProjectRole Role { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    internal void ChangeRole(ProjectRole role) => Role = role;
}
