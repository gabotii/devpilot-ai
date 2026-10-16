using DevPilot.Domain.Auditing;
using DevPilot.Domain.Identity;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using DevPilot.Domain.Notifications;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Common;

/// <summary>
/// Unit of work over the relational store. Tenant-scoped sets are filtered by the current organization
/// (see <see cref="ITenantContext"/>); queries outside a tenant context return no tenant rows (fail closed).
/// </summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<UserSession> Sessions { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<Project> Projects { get; }
    DbSet<ProjectMember> ProjectMembers { get; }
    DbSet<WorkflowStatus> WorkflowStatuses { get; }
    DbSet<WorkflowTransition> WorkflowTransitions { get; }
    DbSet<Label> Labels { get; }
    DbSet<WorkItem> WorkItems { get; }
    DbSet<Comment> Comments { get; }
    DbSet<WorkItemChange> WorkItemChanges { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<ActivityEntry> ActivityEntries { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<Integration> Integrations { get; }
    DbSet<Repository> Repositories { get; }
    DbSet<WebhookDelivery> WebhookDeliveries { get; }
    DbSet<IngestKey> IngestKeys { get; }
    DbSet<Pipeline> Pipelines { get; }
    DbSet<PipelineRun> PipelineRuns { get; }
    DbSet<Deployment> Deployments { get; }
    DbSet<CodeEvent> CodeEvents { get; }
    DbSet<LogEvent> LogEvents { get; }
    DbSet<Incident> Incidents { get; }
    DbSet<IncidentUpdate> IncidentUpdates { get; }
    DbSet<AiAnalysis> AiAnalyses { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The authenticated caller, if any.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? SessionId { get; }
    string? IpAddress { get; }

    Guid RequireUserId() => UserId ?? throw new UnauthorizedException();
}

/// <summary>The organization the current request operates on, resolved and authorized from the route.</summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    OrgRole? Role { get; }

    void SetOrganization(Guid organizationId, OrgRole role);

    /// <summary>Background/system processing for one organization (webhooks, ingestion). No user role is attached.</summary>
    void EnterSystemScope(Guid organizationId);

    Guid RequireOrganizationId() => OrganizationId ?? throw new InvalidOperationException("No organization context is set.");

    OrgRole RequireRole() => Role ?? throw new InvalidOperationException("No organization context is set.");
}

public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Returns (valid, needsRehash).</summary>
    (bool Valid, bool NeedsRehash) Verify(string hash, string password);
}

/// <summary>Allocates per-project work item numbers and per-organization incident numbers atomically in the database.</summary>
public interface IWorkItemNumberAllocator
{
    Task<int> NextAsync(Guid projectId, CancellationToken cancellationToken);

    Task<int> NextIncidentNumberAsync(Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>Authenticated encryption for secrets stored in the database (webhook secrets).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
