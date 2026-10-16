using DevPilot.Domain.Identity;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DevPilot.Infrastructure.Persistence;

internal sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> b)
    {
        b.Property(x => x.Name).HasMaxLength(Integration.MaxNameLength);
        b.Property(x => x.Provider).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ProtectedSecret).HasMaxLength(500);
        b.Property(x => x.ExternalAccount).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => x.ExternalInstallationId);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RepositoryConfiguration : IEntityTypeConfiguration<Repository>
{
    public void Configure(EntityTypeBuilder<Repository> b)
    {
        b.Property(x => x.FullName).HasMaxLength(200);
        b.Property(x => x.DefaultBranch).HasMaxLength(200);
        b.Property(x => x.HtmlUrl).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.IntegrationId, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasOne<Integration>().WithMany().HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.Property(x => x.DeliveryId).HasMaxLength(100);
        b.Property(x => x.EventType).HasMaxLength(60);
        b.Property(x => x.Action).HasMaxLength(60);
        b.Property(x => x.RepositoryFullName).HasMaxLength(200);
        b.Property(x => x.PayloadSha256).HasMaxLength(64);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasIndex(x => new { x.IntegrationId, x.DeliveryId }).IsUnique();
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.HasIndex(x => new { x.IntegrationId, x.ReceivedAt });
        b.HasOne<Integration>().WithMany().HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IngestKeyConfiguration : IEntityTypeConfiguration<IngestKey>
{
    public void Configure(EntityTypeBuilder<IngestKey> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Prefix).HasMaxLength(12);
        b.Property(x => x.KeyHash).HasMaxLength(64);
        b.Property(x => x.Scopes).HasConversion<int>();
        b.HasIndex(x => x.KeyHash).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PipelineConfiguration : IEntityTypeConfiguration<Pipeline>
{
    public void Configure(EntityTypeBuilder<Pipeline> b)
    {
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExternalKey).HasMaxLength(200);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Path).HasMaxLength(300);
        b.HasIndex(x => new { x.OrganizationId, x.Source, x.ExternalKey }).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PipelineRunConfiguration : IEntityTypeConfiguration<PipelineRun>
{
    public void Configure(EntityTypeBuilder<PipelineRun> b)
    {
        b.Property(x => x.ExternalRunId).HasMaxLength(100);
        b.Property(x => x.Branch).HasMaxLength(200);
        b.Property(x => x.CommitSha).HasMaxLength(64);
        b.Property(x => x.Trigger).HasMaxLength(50);
        b.Property(x => x.Actor).HasMaxLength(100);
        b.Property(x => x.HtmlUrl).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Conclusion).HasConversion<string>().HasMaxLength(20);
        b.Ignore(x => x.DurationSeconds);
        b.HasIndex(x => new { x.PipelineId, x.ExternalRunId, x.Attempt }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> b)
    {
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.Property(x => x.Environment).HasMaxLength(50);
        b.Property(x => x.Ref).HasMaxLength(200);
        b.Property(x => x.CommitSha).HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Creator).HasMaxLength(100);
        b.Property(x => x.Url).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Ignore(x => x.DurationSeconds);
        b.HasIndex(x => new { x.OrganizationId, x.Source, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class CodeEventConfiguration : IEntityTypeConfiguration<CodeEvent>
{
    public void Configure(EntityTypeBuilder<CodeEvent> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Author).HasMaxLength(100);
        b.Property(x => x.Branch).HasMaxLength(200);
        b.Property(x => x.Url).HasMaxLength(500);
        b.Property(x => x.State).HasMaxLength(30);
        b.HasIndex(x => new { x.RepositoryId, x.Kind, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        b.HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class LogEventConfiguration : IEntityTypeConfiguration<LogEvent>
{
    public void Configure(EntityTypeBuilder<LogEvent> b)
    {
        b.Property(x => x.Source).HasMaxLength(100);
        b.Property(x => x.Level).HasConversion<int>();
        b.Property(x => x.Message).HasMaxLength(LogEvent.MaxMessageLength);
        b.Property(x => x.Exception).HasMaxLength(LogEvent.MaxExceptionLength);
        b.Property(x => x.Fingerprint).HasMaxLength(16);
        b.Property(x => x.Environment).HasMaxLength(50);
        b.Property(x => x.Attributes).AsJsonb();
        b.HasIndex(x => new { x.OrganizationId, x.Timestamp });
        b.HasIndex(x => new { x.OrganizationId, x.Fingerprint, x.Timestamp });
        b.HasIndex(x => x.ReceivedAt);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> b)
    {
        b.Property(x => x.Title).HasMaxLength(Incident.MaxTitleLength);
        b.Property(x => x.Description).HasMaxLength(10_000);
        b.Property(x => x.LogExcerpt).HasMaxLength(Incident.MaxLogLength);
        b.Property(x => x.Severity).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Fingerprint).HasMaxLength(200);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.Severity });

        // At most one open automatic incident per fingerprint (resolving clears the fingerprint).
        b.HasIndex(x => new { x.OrganizationId, x.Fingerprint }).IsUnique().HasFilter("fingerprint IS NOT NULL");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<PipelineRun>().WithMany().HasForeignKey(x => x.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Deployment>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class IncidentUpdateConfiguration : IEntityTypeConfiguration<IncidentUpdate>
{
    public void Configure(EntityTypeBuilder<IncidentUpdate> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Body).HasMaxLength(IncidentUpdate.MaxBodyLength);
        b.HasIndex(x => new { x.IncidentId, x.At });
        b.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AiAnalysisConfiguration : IEntityTypeConfiguration<AiAnalysis>
{
    public void Configure(EntityTypeBuilder<AiAnalysis> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.InputSha256).HasMaxLength(64);
        b.Property(x => x.Provider).HasMaxLength(50);
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.ErrorCode).HasMaxLength(60);
        b.Property(x => x.ResultJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => x.IncidentId);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
