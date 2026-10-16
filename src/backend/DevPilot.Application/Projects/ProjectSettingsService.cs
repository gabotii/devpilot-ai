using System.Text.RegularExpressions;
using DevPilot.Application.Common;
using DevPilot.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Projects;

public sealed record UpsertStatusRequest(string Name, StatusCategory Category, int Position);

public sealed record SetTransitionsRequest(IReadOnlyList<TransitionDto> Transitions);

public sealed record UpsertLabelRequest(string Name, string Color);

public sealed record WorkflowDto(IReadOnlyList<StatusDto> Statuses, IReadOnlyList<TransitionDto> Transitions);

public sealed partial class UpsertStatusRequestValidator : AbstractValidator<UpsertStatusRequest>
{
    public UpsertStatusRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(WorkflowStatus.MaxNameLength);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Position).InclusiveBetween(0, 100);
    }
}

public sealed partial class UpsertLabelRequestValidator : AbstractValidator<UpsertLabelRequest>
{
    public UpsertLabelRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Label.MaxNameLength);
        RuleFor(x => x.Color).NotEmpty().Must(c => c is not null && HexColor().IsMatch(c)).WithMessage("Color must be a hex value like #3b82f6.");
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}

/// <summary>Configurable workflow (statuses + allowed transitions) and labels of a project.</summary>
public sealed class ProjectSettingsService(
    IAppDbContext db,
    ProjectAccess access,
    AuditLog audit,
    TimeProvider clock,
    IValidator<UpsertStatusRequest> statusValidator,
    IValidator<UpsertLabelRequest> labelValidator)
{
    public const int MaxStatuses = 15;
    public const int MaxLabels = 50;

    public async Task<WorkflowDto> GetWorkflowAsync(Guid projectId, CancellationToken ct)
    {
        await access.LoadAsync(projectId, ct, tracking: false);
        return await BuildWorkflowDtoAsync(projectId, ct);
    }

    public async Task<WorkflowDto> AddStatusAsync(Guid projectId, UpsertStatusRequest request, CancellationToken ct)
    {
        await statusValidator.ValidateAndThrowAsync(request, ct);
        var project = await LoadManageableAsync(projectId, ct);
        var statuses = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync(ct);
        if (statuses.Count >= MaxStatuses)
        {
            throw new ConflictException("workflow.too_many_statuses", $"A workflow can have at most {MaxStatuses} statuses.");
        }

        EnsureUniqueName(statuses, request.Name, null);
        var status = new WorkflowStatus(project.OrganizationId, projectId, request.Name, request.Category, request.Position);
        db.WorkflowStatuses.Add(status);
        audit.Record(project.OrganizationId, "workflow.status_added", "project", projectId.ToString(), new() { ["status"] = status.Name });
        await db.SaveChangesAsync(ct);
        return await BuildWorkflowDtoAsync(projectId, ct);
    }

    public async Task<WorkflowDto> UpdateStatusAsync(Guid projectId, Guid statusId, UpsertStatusRequest request, CancellationToken ct)
    {
        await statusValidator.ValidateAndThrowAsync(request, ct);
        var project = await LoadManageableAsync(projectId, ct);
        var statuses = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync(ct);
        var status = statuses.FirstOrDefault(s => s.Id == statusId) ?? throw new NotFoundException("Status");
        EnsureUniqueName(statuses, request.Name, statusId);

        var categoryChanged = status.Category != request.Category;
        status.Update(request.Name, request.Category, request.Position);
        EnsureCategoriesCovered(statuses);

        if (categoryChanged)
        {
            var now = clock.GetUtcNow();
            var items = await db.WorkItems.Where(w => w.StatusId == statusId).ToListAsync(ct);
            foreach (var item in items)
            {
                item.SyncStatusCategory(request.Category, now);
            }
        }

        audit.Record(project.OrganizationId, "workflow.status_updated", "project", projectId.ToString(), new() { ["status"] = status.Name });
        await db.SaveChangesAsync(ct);
        return await BuildWorkflowDtoAsync(projectId, ct);
    }

    public async Task<WorkflowDto> DeleteStatusAsync(Guid projectId, Guid statusId, CancellationToken ct)
    {
        var project = await LoadManageableAsync(projectId, ct);
        var statuses = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).ToListAsync(ct);
        var status = statuses.FirstOrDefault(s => s.Id == statusId) ?? throw new NotFoundException("Status");
        var inUse = await db.WorkItems.CountAsync(w => w.StatusId == statusId, ct);
        if (inUse > 0)
        {
            throw new ConflictException("workflow.status_in_use", $"{inUse} item(s) are in '{status.Name}'. Move them to another status first.");
        }

        statuses.Remove(status);
        EnsureCategoriesCovered(statuses);
        var transitions = await db.WorkflowTransitions.Where(t => t.FromStatusId == statusId || t.ToStatusId == statusId).ToListAsync(ct);
        db.WorkflowTransitions.RemoveRange(transitions);
        db.WorkflowStatuses.Remove(status);
        audit.Record(project.OrganizationId, "workflow.status_deleted", "project", projectId.ToString(), new() { ["status"] = status.Name });
        await db.SaveChangesAsync(ct);
        return await BuildWorkflowDtoAsync(projectId, ct);
    }

    /// <summary>Replaces the full set of allowed transitions.</summary>
    public async Task<WorkflowDto> SetTransitionsAsync(Guid projectId, SetTransitionsRequest request, CancellationToken ct)
    {
        var project = await LoadManageableAsync(projectId, ct);
        var statusIds = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId).Select(s => s.Id).ToListAsync(ct);
        var requested = (request.Transitions ?? []).Distinct().ToList();
        if (requested.Any(t => !statusIds.Contains(t.FromStatusId) || !statusIds.Contains(t.ToStatusId)))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Transitions), "Every transition must reference statuses of this project.")]);
        }

        if (requested.Count > statusIds.Count * statusIds.Count)
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Transitions), "Too many transitions.")]);
        }

        var existing = await db.WorkflowTransitions.Where(t => t.ProjectId == projectId).ToListAsync(ct);
        db.WorkflowTransitions.RemoveRange(existing);
        db.WorkflowTransitions.AddRange(requested.Select(t => new WorkflowTransition(project.OrganizationId, projectId, t.FromStatusId, t.ToStatusId)));
        audit.Record(project.OrganizationId, "workflow.transitions_updated", "project", projectId.ToString(),
            new() { ["count"] = requested.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await db.SaveChangesAsync(ct);
        return await BuildWorkflowDtoAsync(projectId, ct);
    }

    public async Task<IReadOnlyList<LabelDto>> ListLabelsAsync(Guid projectId, CancellationToken ct)
    {
        await access.LoadAsync(projectId, ct, tracking: false);
        return await db.Labels.AsNoTracking().Where(l => l.ProjectId == projectId).OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color)).ToListAsync(ct);
    }

    public async Task<LabelDto> CreateLabelAsync(Guid projectId, UpsertLabelRequest request, CancellationToken ct)
    {
        await labelValidator.ValidateAndThrowAsync(request, ct);
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanEditItems(project);
        var labels = await db.Labels.Where(l => l.ProjectId == projectId).ToListAsync(ct);
        if (labels.Count >= MaxLabels)
        {
            throw new ConflictException("label.limit", $"A project can have at most {MaxLabels} labels.");
        }

        if (labels.Any(l => string.Equals(l.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException("label.duplicate", "A label with this name already exists.");
        }

        var label = new Label(project.OrganizationId, projectId, request.Name, request.Color);
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        return new LabelDto(label.Id, label.Name, label.Color);
    }

    public async Task<LabelDto> UpdateLabelAsync(Guid projectId, Guid labelId, UpsertLabelRequest request, CancellationToken ct)
    {
        await labelValidator.ValidateAndThrowAsync(request, ct);
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanEditItems(project);
        var labels = await db.Labels.Where(l => l.ProjectId == projectId).ToListAsync(ct);
        var label = labels.FirstOrDefault(l => l.Id == labelId) ?? throw new NotFoundException("Label");
        if (labels.Any(l => l.Id != labelId && string.Equals(l.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException("label.duplicate", "A label with this name already exists.");
        }

        label.Update(request.Name, request.Color);
        await db.SaveChangesAsync(ct);
        return new LabelDto(label.Id, label.Name, label.Color);
    }

    public async Task DeleteLabelAsync(Guid projectId, Guid labelId, CancellationToken ct)
    {
        var project = await LoadManageableAsync(projectId, ct);
        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == labelId && l.ProjectId == projectId, ct) ?? throw new NotFoundException("Label");
        db.Labels.Remove(label);
        audit.Record(project.OrganizationId, "label.deleted", "project", projectId.ToString(), new() { ["label"] = label.Name });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Project> LoadManageableAsync(Guid projectId, CancellationToken ct)
    {
        var project = await access.LoadAsync(projectId, ct);
        access.EnsureCanManage(project);
        project.EnsureWritable();
        return project;
    }

    private static void EnsureUniqueName(IEnumerable<WorkflowStatus> statuses, string name, Guid? exceptId)
    {
        if (statuses.Any(s => s.Id != exceptId && string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException("workflow.duplicate_status", "A status with this name already exists.");
        }
    }

    /// <summary>A workflow needs at least one open (Todo) status for new items and one Done status to complete them.</summary>
    private static void EnsureCategoriesCovered(IReadOnlyCollection<WorkflowStatus> statuses)
    {
        if (statuses.All(s => s.Category != StatusCategory.Todo))
        {
            throw new ConflictException("workflow.requires_todo", "The workflow must keep at least one status in the 'Todo' category.");
        }

        if (statuses.All(s => s.Category != StatusCategory.Done))
        {
            throw new ConflictException("workflow.requires_done", "The workflow must keep at least one status in the 'Done' category.");
        }
    }

    private async Task<WorkflowDto> BuildWorkflowDtoAsync(Guid projectId, CancellationToken ct)
    {
        var statuses = await db.WorkflowStatuses.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Position).ThenBy(s => s.Name)
            .Select(s => new StatusDto(s.Id, s.Name, s.Category, s.Position)).ToListAsync(ct);
        var transitions = await db.WorkflowTransitions.AsNoTracking().Where(t => t.ProjectId == projectId)
            .Select(t => new TransitionDto(t.FromStatusId, t.ToStatusId)).ToListAsync(ct);
        return new WorkflowDto(statuses, transitions);
    }
}
