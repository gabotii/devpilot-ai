using DevPilot.Domain.Common;

namespace DevPilot.Domain.WorkItems;

public sealed class Comment : Entity, IOrganizationScoped
{
    public const int MaxBodyLength = 10000;

    private Comment() { }

    public Comment(WorkItem workItem, Guid authorId, string body, DateTimeOffset now)
    {
        OrganizationId = workItem.OrganizationId;
        WorkItemId = workItem.Id;
        AuthorId = authorId;
        Body = body.Trim();
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EditedAt { get; private set; }

    public void Edit(Guid actorId, string body, DateTimeOffset now)
    {
        if (actorId != AuthorId)
        {
            throw new Organizations.ForbiddenDomainException("Only the author can edit a comment.");
        }

        Body = body.Trim();
        EditedAt = now;
    }
}

/// <summary>Immutable field-level change record that powers the item history.</summary>
public sealed class WorkItemChange : Entity, IOrganizationScoped
{
    public const int MaxValueLength = 500;

    private WorkItemChange() { }

    public WorkItemChange(WorkItem item, Guid actorId, string field, string? oldValue, string? newValue, DateTimeOffset now)
    {
        OrganizationId = item.OrganizationId;
        WorkItemId = item.Id;
        ActorId = actorId;
        Field = field;
        OldValue = Truncate(oldValue);
        NewValue = Truncate(newValue);
        At = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Field { get; private set; } = string.Empty;
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public DateTimeOffset At { get; private set; }

    private static string? Truncate(string? value) =>
        value is { Length: > MaxValueLength } ? value[..(MaxValueLength - 1)] + "…" : value;
}
