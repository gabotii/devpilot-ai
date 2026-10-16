using DevPilot.Domain.Common;
using DevPilot.Domain.Projects;

namespace DevPilot.Domain.WorkItems;

public enum WorkItemType
{
    Task,
    Issue,
    Subtask,
}

public enum Priority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

public sealed class WorkItem : Entity, IOrganizationScoped
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 20000;
    public const int MaxLabels = 10;

    private readonly List<WorkItemLabel> _labels = [];

    private WorkItem() { }

    public WorkItem(Project project, int number, WorkItemType type, string title, string? description, WorkflowStatus initialStatus,
        Priority priority, Guid reporterId, WorkItem? parent, DateTimeOffset now)
    {
        project.EnsureWritable();
        if (initialStatus.ProjectId != project.Id)
        {
            throw new DomainException("workflow.unknown_status", "The status does not belong to this project's workflow.");
        }

        ValidateParent(project.Id, type, parent);

        OrganizationId = project.OrganizationId;
        ProjectId = project.Id;
        Number = number;
        Type = type;
        Title = title.Trim();
        Description = Normalize(description);
        StatusId = initialStatus.Id;
        StatusCategory = initialStatus.Category;
        Priority = priority;
        ReporterId = reporterId;
        ParentId = parent?.Id;
        CreatedAt = now;
        UpdatedAt = now;
        if (initialStatus.Category == StatusCategory.Done)
        {
            CompletedAt = now;
        }
    }

    public Guid OrganizationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Number { get; private set; }
    public WorkItemType Type { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid StatusId { get; private set; }

    /// <summary>Denormalized from the status so that open/closed queries do not need a join.</summary>
    public StatusCategory StatusCategory { get; private set; }

    public Priority Priority { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public Guid ReporterId { get; private set; }
    public Guid? ParentId { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyCollection<WorkItemLabel> Labels => _labels.AsReadOnly();

    public bool IsClosed => StatusCategory is StatusCategory.Done or StatusCategory.Cancelled;

    public void Rename(string title, DateTimeOffset now)
    {
        Title = title.Trim();
        UpdatedAt = now;
    }

    public void Describe(string? description, DateTimeOffset now)
    {
        Description = Normalize(description);
        UpdatedAt = now;
    }

    public void SetPriority(Priority priority, DateTimeOffset now)
    {
        Priority = priority;
        UpdatedAt = now;
    }

    /// <summary>The application layer must verify the assignee is a project member before calling this.</summary>
    public void Assign(Guid? assigneeId, DateTimeOffset now)
    {
        AssigneeId = assigneeId;
        UpdatedAt = now;
    }

    public void SetDueDate(DateOnly? dueDate, DateTimeOffset now)
    {
        DueDate = dueDate;
        UpdatedAt = now;
    }

    public void SetLabels(IReadOnlyCollection<Guid> labelIds, DateTimeOffset now)
    {
        if (labelIds.Count > MaxLabels)
        {
            throw new DomainException("workitem.too_many_labels", $"A work item can have at most {MaxLabels} labels.");
        }

        _labels.RemoveAll(l => !labelIds.Contains(l.LabelId));
        foreach (var labelId in labelIds.Where(id => _labels.All(l => l.LabelId != id)))
        {
            _labels.Add(new WorkItemLabel(Id, labelId));
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Moves the item to another status. Enforces the project workflow and the rule that an item cannot be
    /// completed while it still has open subtasks.
    /// </summary>
    public void TransitionTo(Workflow workflow, Guid targetStatusId, int openSubtaskCount, DateTimeOffset now)
    {
        var target = workflow.Get(targetStatusId);
        if (target.Id == StatusId)
        {
            return;
        }

        var current = workflow.Get(StatusId);
        if (!workflow.CanTransition(current.Id, target.Id))
        {
            throw new DomainException("workitem.invalid_transition",
                $"Moving from '{current.Name}' to '{target.Name}' is not allowed by the project workflow.");
        }

        if (target.Category == StatusCategory.Done && openSubtaskCount > 0)
        {
            throw new DomainException("workitem.open_subtasks",
                $"This item still has {openSubtaskCount} open subtask(s). Complete or cancel them first.");
        }

        StatusId = target.Id;
        StatusCategory = target.Category;
        CompletedAt = target.Category == StatusCategory.Done ? now : null;
        UpdatedAt = now;
    }

    /// <summary>Called when an administrator changes the category of a status this item is in.</summary>
    public void SyncStatusCategory(StatusCategory category, DateTimeOffset now)
    {
        if (StatusCategory == category)
        {
            return;
        }

        StatusCategory = category;
        CompletedAt = category == StatusCategory.Done ? now : null;
    }

    private static void ValidateParent(Guid projectId, WorkItemType type, WorkItem? parent)
    {
        switch (type)
        {
            case WorkItemType.Subtask when parent is null:
                throw new DomainException("workitem.subtask_requires_parent", "A subtask must have a parent item.");
            case not WorkItemType.Subtask when parent is not null:
                throw new DomainException("workitem.parent_only_for_subtasks", "Only subtasks can have a parent item.");
        }

        if (parent is null)
        {
            return;
        }

        if (parent.ProjectId != projectId)
        {
            throw new DomainException("workitem.parent_other_project", "The parent item belongs to a different project.");
        }

        if (parent.Type == WorkItemType.Subtask)
        {
            throw new DomainException("workitem.nested_subtask", "Subtasks cannot have subtasks of their own.");
        }

        if (parent.IsClosed)
        {
            throw new DomainException("workitem.parent_closed", "Cannot add a subtask to a closed item.");
        }
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

public sealed class WorkItemLabel
{
    private WorkItemLabel() { }

    internal WorkItemLabel(Guid workItemId, Guid labelId)
    {
        WorkItemId = workItemId;
        LabelId = labelId;
    }

    public Guid WorkItemId { get; private set; }
    public Guid LabelId { get; private set; }
}
