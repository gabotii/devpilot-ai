using System.Text.Json;
using DevPilot.Domain.Auditing;
using DevPilot.Domain.Identity;
using DevPilot.Domain.Notifications;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DevPilot.Infrastructure.Persistence;

internal static class JsonColumn
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<Dictionary<string, string?>> AsJsonb(this PropertyBuilder<Dictionary<string, string?>> builder) =>
        builder.HasColumnType("jsonb").HasConversion(
            new ValueConverter<Dictionary<string, string?>, string>(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<Dictionary<string, string?>>(v, Options) ?? new Dictionary<string, string?>()),
            new ValueComparer<Dictionary<string, string?>>(
                (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                v => JsonSerializer.Serialize(v, Options).GetHashCode(StringComparison.Ordinal),
                v => new Dictionary<string, string?>(v)));
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(x => x.Email).HasMaxLength(User.MaxEmailLength);
        b.Property(x => x.DisplayName).HasMaxLength(User.MaxDisplayNameLength);
        b.Property(x => x.PasswordHash).HasMaxLength(512);
        b.HasIndex(x => x.Email).IsUnique();
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("user_sessions");
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.CsrfTokenHash).HasMaxLength(64);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.UserAgent).HasMaxLength(256);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.ExpiresAt });
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.Property(x => x.Name).HasMaxLength(Organization.MaxNameLength);
        b.Property(x => x.Slug).HasMaxLength(64);
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable("memberships");
        b.HasKey(x => new { x.OrganizationId, x.UserId });
        b.HasIndex(x => x.UserId);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> b)
    {
        b.Property(x => x.Email).HasMaxLength(User.MaxEmailLength);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Email });
        b.HasIndex(x => x.Email);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.Property(x => x.Name).HasMaxLength(Project.MaxNameLength);
        b.Property(x => x.Key).HasMaxLength(10);
        b.Property(x => x.Description).HasMaxLength(Project.MaxDescriptionLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> b)
    {
        b.HasKey(x => new { x.ProjectId, x.UserId });
        b.HasIndex(x => x.UserId);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkflowStatusConfiguration : IEntityTypeConfiguration<WorkflowStatus>
{
    public void Configure(EntityTypeBuilder<WorkflowStatus> b)
    {
        b.Property(x => x.Name).HasMaxLength(WorkflowStatus.MaxNameLength);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.ProjectId, x.Position });
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkflowTransitionConfiguration : IEntityTypeConfiguration<WorkflowTransition>
{
    public void Configure(EntityTypeBuilder<WorkflowTransition> b)
    {
        b.HasKey(x => new { x.ProjectId, x.FromStatusId, x.ToStatusId });
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<WorkflowStatus>().WithMany().HasForeignKey(x => x.FromStatusId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<WorkflowStatus>().WithMany().HasForeignKey(x => x.ToStatusId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> b)
    {
        b.Property(x => x.Name).HasMaxLength(Label.MaxNameLength);
        b.Property(x => x.Color).HasMaxLength(7);
        b.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkItemConfiguration : IEntityTypeConfiguration<WorkItem>
{
    public void Configure(EntityTypeBuilder<WorkItem> b)
    {
        b.Property(x => x.Title).HasMaxLength(WorkItem.MaxTitleLength);
        b.Property(x => x.Description).HasMaxLength(WorkItem.MaxDescriptionLength);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Priority).HasConversion<int>();
        b.Property(x => x.StatusCategory).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.StatusCategory });
        b.HasIndex(x => new { x.ProjectId, x.StatusId });
        b.HasIndex(x => x.AssigneeId);
        b.HasIndex(x => x.ParentId);
        b.HasIndex(x => new { x.OrganizationId, x.CompletedAt });
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // NO ACTION (checked at end of statement) lets a project delete cascade to both statuses and items,
        // while still preventing deletion of a status that items reference.
        b.HasOne<WorkflowStatus>().WithMany().HasForeignKey(x => x.StatusId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Labels).WithOne().HasForeignKey(l => l.WorkItemId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Labels).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class WorkItemLabelConfiguration : IEntityTypeConfiguration<WorkItemLabel>
{
    public void Configure(EntityTypeBuilder<WorkItemLabel> b)
    {
        b.HasKey(x => new { x.WorkItemId, x.LabelId });
        b.HasIndex(x => x.LabelId);
        b.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.Property(x => x.Body).HasMaxLength(Comment.MaxBodyLength);
        b.HasIndex(x => new { x.WorkItemId, x.CreatedAt });
        b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkItemChangeConfiguration : IEntityTypeConfiguration<WorkItemChange>
{
    public void Configure(EntityTypeBuilder<WorkItemChange> b)
    {
        b.Property(x => x.Field).HasMaxLength(40);
        b.Property(x => x.OldValue).HasMaxLength(WorkItemChange.MaxValueLength);
        b.Property(x => x.NewValue).HasMaxLength(WorkItemChange.MaxValueLength);
        b.HasIndex(x => new { x.WorkItemId, x.At });
        b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.Property(x => x.Action).HasMaxLength(80);
        b.Property(x => x.TargetType).HasMaxLength(40);
        b.Property(x => x.TargetId).HasMaxLength(80);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.Details).AsJsonb();
        b.HasIndex(x => new { x.OrganizationId, x.At });
        b.HasIndex(x => new { x.ActorUserId, x.At });
    }
}

internal sealed class ActivityEntryConfiguration : IEntityTypeConfiguration<ActivityEntry>
{
    public void Configure(EntityTypeBuilder<ActivityEntry> b)
    {
        b.Property(x => x.Kind).HasMaxLength(60);
        b.Property(x => x.Summary).HasMaxLength(ActivityEntry.MaxSummaryLength);
        b.Property(x => x.SubjectType).HasMaxLength(40);
        b.HasIndex(x => new { x.OrganizationId, x.At });
        b.HasIndex(x => new { x.ProjectId, x.At });
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(x => x.Kind).HasMaxLength(60);
        b.Property(x => x.Title).HasMaxLength(Notification.MaxTitleLength);
        b.Property(x => x.Body).HasMaxLength(Notification.MaxBodyLength);
        b.Property(x => x.Link).HasMaxLength(300);
        b.Property(x => x.DedupeKey).HasMaxLength(200);
        b.HasIndex(x => new { x.UserId, x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique().HasFilter("dedupe_key IS NOT NULL");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
