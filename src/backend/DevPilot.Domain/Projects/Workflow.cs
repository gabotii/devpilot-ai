using DevPilot.Domain.Common;

namespace DevPilot.Domain.Projects;

/// <summary>Semantic bucket of a configurable status; drives dashboards, completion and kanban styling.</summary>
public enum StatusCategory
{
    Todo,
    InProgress,
    Done,
    Cancelled,
}

public sealed class WorkflowStatus : Entity, IOrganizationScoped
{
    public const int MaxNameLength = 40;

    private WorkflowStatus() { }

    public WorkflowStatus(Guid organizationId, Guid projectId, string name, StatusCategory category, int position)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
        Name = name.Trim();
        Category = category;
        Position = position;
    }

    public Guid OrganizationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public StatusCategory Category { get; private set; }
    public int Position { get; private set; }

    public void Update(string name, StatusCategory category, int position)
    {
        Name = name.Trim();
        Category = category;
        Position = position;
    }

    public bool IsClosed => Category is StatusCategory.Done or StatusCategory.Cancelled;
}

public sealed class WorkflowTransition : IOrganizationScoped
{
    private WorkflowTransition() { }

    public WorkflowTransition(Guid organizationId, Guid projectId, Guid fromStatusId, Guid toStatusId)
    {
        if (fromStatusId == toStatusId)
        {
            throw new DomainException("workflow.self_transition", "A status cannot transition to itself.");
        }

        OrganizationId = organizationId;
        ProjectId = projectId;
        FromStatusId = fromStatusId;
        ToStatusId = toStatusId;
    }

    public Guid OrganizationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid FromStatusId { get; private set; }
    public Guid ToStatusId { get; private set; }
}

/// <summary>Read-only view of a project's statuses and allowed transitions, used to validate status changes.</summary>
public sealed class Workflow
{
    private readonly Dictionary<Guid, WorkflowStatus> _statuses;
    private readonly HashSet<(Guid From, Guid To)> _transitions;

    public Workflow(IEnumerable<WorkflowStatus> statuses, IEnumerable<WorkflowTransition> transitions)
    {
        _statuses = statuses.ToDictionary(s => s.Id);
        _transitions = transitions.Select(t => (t.FromStatusId, t.ToStatusId)).ToHashSet();
    }

    public IEnumerable<WorkflowStatus> Statuses => _statuses.Values.OrderBy(s => s.Position);

    public WorkflowStatus Get(Guid statusId) =>
        _statuses.TryGetValue(statusId, out var status)
            ? status
            : throw new DomainException("workflow.unknown_status", "The status does not belong to this project's workflow.");

    public bool CanTransition(Guid fromStatusId, Guid toStatusId) =>
        fromStatusId == toStatusId || _transitions.Contains((fromStatusId, toStatusId));

    public WorkflowStatus InitialStatus =>
        Statuses.FirstOrDefault(s => s.Category == StatusCategory.Todo)
        ?? throw new DomainException("workflow.no_initial_status", "The workflow has no status in the 'Todo' category.");
}

public static class WorkflowTemplate
{
    /// <summary>Default software workflow: Backlog → Por hacer → En curso → En revisión → Hecho, with Cancelado and reopen paths.</summary>
    public static (List<WorkflowStatus> Statuses, List<WorkflowTransition> Transitions) CreateDefault(Guid organizationId, Guid projectId)
    {
        var backlog = new WorkflowStatus(organizationId, projectId, "Backlog", StatusCategory.Todo, 0);
        var todo = new WorkflowStatus(organizationId, projectId, "Por hacer", StatusCategory.Todo, 1);
        var inProgress = new WorkflowStatus(organizationId, projectId, "En curso", StatusCategory.InProgress, 2);
        var inReview = new WorkflowStatus(organizationId, projectId, "En revisión", StatusCategory.InProgress, 3);
        var done = new WorkflowStatus(organizationId, projectId, "Hecho", StatusCategory.Done, 4);
        var cancelled = new WorkflowStatus(organizationId, projectId, "Cancelado", StatusCategory.Cancelled, 5);

        (WorkflowStatus From, WorkflowStatus To)[] pairs =
        [
            (backlog, todo), (backlog, inProgress), (backlog, cancelled),
            (todo, backlog), (todo, inProgress), (todo, cancelled),
            (inProgress, todo), (inProgress, inReview), (inProgress, done), (inProgress, cancelled),
            (inReview, inProgress), (inReview, done), (inReview, cancelled),
            (done, inProgress),
            (cancelled, backlog),
        ];

        var transitions = pairs.Select(p => new WorkflowTransition(organizationId, projectId, p.From.Id, p.To.Id)).ToList();
        return ([backlog, todo, inProgress, inReview, done, cancelled], transitions);
    }
}

public sealed class Label : Entity, IOrganizationScoped
{
    public const int MaxNameLength = 30;

    private Label() { }

    public Label(Guid organizationId, Guid projectId, string name, string color)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
        Name = name.Trim();
        Color = color.ToLowerInvariant();
    }

    public Guid OrganizationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Hex color "#rrggbb".</summary>
    public string Color { get; private set; } = string.Empty;

    public void Update(string name, string color)
    {
        Name = name.Trim();
        Color = color.ToLowerInvariant();
    }
}
