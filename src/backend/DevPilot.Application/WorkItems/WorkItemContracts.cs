using DevPilot.Application.Projects;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;
using FluentValidation;

namespace DevPilot.Application.WorkItems;

public sealed record WorkItemSummaryDto(
    Guid Id,
    Guid ProjectId,
    string Key,
    int Number,
    WorkItemType Type,
    string Title,
    Guid StatusId,
    string StatusName,
    StatusCategory StatusCategory,
    Priority Priority,
    UserRefDto? Assignee,
    DateOnly? DueDate,
    IReadOnlyList<LabelDto> Labels,
    Guid? ParentId,
    int SubtaskCount,
    int OpenSubtaskCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record WorkItemDetailDto(
    WorkItemSummaryDto Item,
    string? Description,
    UserRefDto? Reporter,
    WorkItemRefDto? Parent,
    IReadOnlyList<WorkItemSummaryDto> Subtasks,
    int CommentCount,
    uint Version,
    IReadOnlyList<StatusDto> AllowedTransitions,
    ProjectPermissions Permissions);

public sealed record WorkItemRefDto(Guid Id, string Key, int Number, string Title, StatusCategory StatusCategory);

public sealed record CreateWorkItemRequest(
    WorkItemType Type,
    string Title,
    string? Description,
    Priority Priority,
    Guid? AssigneeId,
    DateOnly? DueDate,
    Guid? ParentId,
    Guid? StatusId,
    IReadOnlyList<Guid>? LabelIds);

/// <summary>Full replacement of editable fields. <see cref="Version"/> enables optimistic concurrency.</summary>
public sealed record UpdateWorkItemRequest(
    string Title,
    string? Description,
    Priority Priority,
    Guid? AssigneeId,
    DateOnly? DueDate,
    IReadOnlyList<Guid>? LabelIds,
    uint Version);

public sealed record TransitionWorkItemRequest(Guid StatusId, uint? Version);

public sealed record WorkItemListQuery(
    string? Q = null,
    Guid? ProjectId = null,
    Guid? StatusId = null,
    StatusCategory? Category = null,
    bool? Open = null,
    Guid? AssigneeId = null,
    bool? Unassigned = null,
    Priority? Priority = null,
    WorkItemType? Type = null,
    Guid? LabelId = null,
    Guid? ParentId = null,
    bool? Overdue = null,
    string? Sort = null,
    int Page = 1,
    int PageSize = 25);

public sealed record BoardColumnDto(StatusDto Status, IReadOnlyList<WorkItemSummaryDto> Items, int TotalCount);

public sealed record BoardDto(Guid ProjectId, IReadOnlyList<BoardColumnDto> Columns, IReadOnlyList<TransitionDto> Transitions, ProjectPermissions Permissions);

public sealed record CommentDto(Guid Id, UserRefDto? Author, string Body, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool CanEdit, bool CanDelete);

public sealed record CommentRequest(string Body);

public sealed record WorkItemChangeDto(Guid Id, UserRefDto? Actor, string Field, string? OldValue, string? NewValue, DateTimeOffset At);

public sealed class CreateWorkItemRequestValidator : AbstractValidator<CreateWorkItemRequest>
{
    public CreateWorkItemRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(WorkItem.MaxTitleLength);
        RuleFor(x => x.Description).MaximumLength(WorkItem.MaxDescriptionLength);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.LabelIds).Must(l => l is null || l.Count <= WorkItem.MaxLabels).WithMessage($"At most {WorkItem.MaxLabels} labels.");
        RuleFor(x => x.ParentId).NotNull().When(x => x.Type == WorkItemType.Subtask).WithMessage("A subtask must have a parent item.");
        RuleFor(x => x.DueDate).Must(d => d is null || d.Value.Year is >= 2000 and <= 2100).WithMessage("Due date is out of range.");
    }
}

public sealed class UpdateWorkItemRequestValidator : AbstractValidator<UpdateWorkItemRequest>
{
    public UpdateWorkItemRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(WorkItem.MaxTitleLength);
        RuleFor(x => x.Description).MaximumLength(WorkItem.MaxDescriptionLength);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.LabelIds).Must(l => l is null || l.Count <= WorkItem.MaxLabels).WithMessage($"At most {WorkItem.MaxLabels} labels.");
        RuleFor(x => x.DueDate).Must(d => d is null || d.Value.Year is >= 2000 and <= 2100).WithMessage("Due date is out of range.");
    }
}

public sealed class CommentRequestValidator : AbstractValidator<CommentRequest>
{
    public CommentRequestValidator() => RuleFor(x => x.Body).NotEmpty().MaximumLength(Comment.MaxBodyLength);
}
