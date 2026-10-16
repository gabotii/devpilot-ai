using System.Linq.Expressions;
using DevPilot.Application.Common;
using DevPilot.Domain.Auditing;
using DevPilot.Domain.Common;
using DevPilot.Domain.Identity;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using DevPilot.Domain.Notifications;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<WorkflowStatus> WorkflowStatuses => Set<WorkflowStatus>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<WorkItemChange> WorkItemChanges => Set<WorkItemChange>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<IngestKey> IngestKeys => Set<IngestKey>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<CodeEvent> CodeEvents => Set<CodeEvent>();
    public DbSet<LogEvent> LogEvents => Set<LogEvent>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentUpdate> IncidentUpdates => Set<IncidentUpdate>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();

    /// <summary>
    /// Read by the tenant query filters. EF Core evaluates context properties per instance, so each request sees its own tenant.
    /// When unset the filters match nothing (fail closed).
    /// </summary>
    private Guid? CurrentOrganizationId => tenant.OrganizationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IOrganizationScoped).IsAssignableFrom(entityType.ClrType) && entityType.BaseType is null)
            {
                typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IOrganizationScoped
    {
        Expression<Func<TEntity, bool>> filter = e => e.OrganizationId == CurrentOrganizationId;
        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }
}
