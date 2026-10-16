using DevPilot.Application.Common;
using DevPilot.Application.Projects;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Organizations;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Incidents;

public sealed record IncidentSummaryDto(Guid Id, int Number, string Key, string Title, IncidentSeverity Severity, IncidentStatus Status,
    IncidentSource Source, Guid? ProjectId, string? ProjectName, UserRefDto? Assignee, int OccurrenceCount, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset LastOccurredAt, DateTimeOffset? ResolvedAt);

public sealed record IncidentUpdateDto(Guid Id, IncidentUpdateKind Kind, string Body, UserRefDto? Author, DateTimeOffset At);

public sealed record IncidentDetailDto(IncidentSummaryDto Incident, string? Description, string? LogExcerpt, Guid? PipelineRunId, Guid? DeploymentId,
    IReadOnlyList<IncidentUpdateDto> Updates, uint Version, bool CanEdit);

public sealed record CreateIncidentRequest(string Title, IncidentSeverity Severity, Guid? ProjectId, string? Description, string? LogExcerpt);

public sealed record UpdateIncidentRequest(string Title, string? Description, IncidentSeverity Severity, IncidentStatus Status, Guid? AssigneeId,
    uint Version);

public sealed record IncidentNoteRequest(string Body);

public sealed record AttachLogRequest(string Log);

public sealed record IncidentListQuery(IncidentStatus? Status, bool? Open, IncidentSeverity? Severity, Guid? ProjectId, string? Q, int Page = 1,
    int PageSize = 25);

public sealed class CreateIncidentRequestValidator : AbstractValidator<CreateIncidentRequest>
{
    public CreateIncidentRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Incident.MaxTitleLength);
        RuleFor(x => x.Severity).IsInEnum();
        RuleFor(x => x.Description).MaximumLength(10_000);
        RuleFor(x => x.LogExcerpt).MaximumLength(Incident.MaxLogLength);
    }
}

public sealed class UpdateIncidentRequestValidator : AbstractValidator<UpdateIncidentRequest>
{
    public UpdateIncidentRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Incident.MaxTitleLength);
        RuleFor(x => x.Severity).IsInEnum();
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Description).MaximumLength(10_000);
    }
}

public sealed class IncidentService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IWorkItemNumberAllocator numbers,
    AuditLog audit,
    TimeProvider clock,
    IValidator<CreateIncidentRequest> createValidator,
    IValidator<UpdateIncidentRequest> updateValidator)
{
    public async Task<PagedResult<IncidentSummaryDto>> ListAsync(IncidentListQuery q, CancellationToken ct)
    {
        var query = db.Incidents.AsNoTracking();
        if (q.Status is { } status)
        {
            query = query.Where(i => i.Status == status);
        }
        else if (q.Open is { } open)
        {
            query = open ? query.Where(i => i.Status != IncidentStatus.Resolved) : query.Where(i => i.Status == IncidentStatus.Resolved);
        }

        if (q.Severity is { } severity)
        {
            query = query.Where(i => i.Severity == severity);
        }

        if (q.ProjectId is { } projectId)
        {
            query = query.Where(i => i.ProjectId == projectId);
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim().ToLowerInvariant();
            query = query.Where(i => i.Title.ToLower().Contains(term));
        }

        var page = new PageRequest(q.Page, q.PageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(i => i.Status == IncidentStatus.Resolved).ThenByDescending(i => i.Severity).ThenByDescending(i => i.LastOccurredAt)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<IncidentSummaryDto>(await ToSummariesAsync(rows, ct), page.SafePage, page.SafePageSize, total);
    }

    public async Task<IncidentDetailDto> GetAsync(Guid incidentId, CancellationToken ct)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident");
        var updates = await db.IncidentUpdates.AsNoTracking().Where(u => u.IncidentId == incidentId).OrderByDescending(u => u.At).Take(200).ToListAsync(ct);
        var users = await UsersAsync(updates.Select(u => u.AuthorId), ct);
        return new IncidentDetailDto((await ToSummariesAsync([incident], ct))[0], incident.Description, incident.LogExcerpt, incident.PipelineRunId,
            incident.DeploymentId,
            updates.Select(u => new IncidentUpdateDto(u.Id, u.Kind, u.Body, u.AuthorId is { } a ? users.GetValueOrDefault(a) : null, u.At)).ToList(),
            incident.Version, tenant.RequireRole() >= OrgRole.Developer);
    }

    public async Task<IncidentDetailDto> CreateAsync(CreateIncidentRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        RequireDeveloper();
        var orgId = tenant.RequireOrganizationId();
        if (request.ProjectId is { } projectId && !await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.ProjectId), "Project not found.")]);
        }

        var number = await numbers.NextIncidentNumberAsync(orgId, ct);
        var incident = new Incident(orgId, number, request.Title, request.Severity, IncidentSource.Manual, null, request.ProjectId,
            currentUser.RequireUserId(), request.Description, request.LogExcerpt, clock.GetUtcNow());
        db.Incidents.Add(incident);
        audit.Record(orgId, "incident.created", "incident", incident.Id.ToString(), new() { ["severity"] = incident.Severity.ToString() });
        audit.Activity(orgId, incident.ProjectId, "incident.created", $"abrió el incidente INC-{number}: {incident.Title}", "incident", incident.Id);
        await db.SaveChangesAsync(ct);
        return await GetAsync(incident.Id, ct);
    }

    public async Task<IncidentDetailDto> UpdateAsync(Guid incidentId, UpdateIncidentRequest request, CancellationToken ct)
    {
        await updateValidator.ValidateAndThrowAsync(request, ct);
        RequireDeveloper();
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident");
        if (incident.Version != request.Version)
        {
            throw new ConflictException("incident.stale", "This incident was modified by someone else. Reload it and try again.");
        }

        if (request.AssigneeId is { } assignee && !await db.Memberships.AnyAsync(m => m.OrganizationId == incident.OrganizationId && m.UserId == assignee, ct))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.AssigneeId), "The assignee is not a member of this organization.")]);
        }

        var now = clock.GetUtcNow();
        var actor = currentUser.RequireUserId();
        if (incident.Status != request.Status)
        {
            db.IncidentUpdates.Add(new IncidentUpdate(incident, IncidentUpdateKind.StatusChange, $"{incident.Status} → {request.Status}", actor, now));
            audit.Activity(incident.OrganizationId, incident.ProjectId, "incident.status",
                $"cambió INC-{incident.Number} a {request.Status}: {incident.Title}", "incident", incident.Id);
        }

        incident.Update(request.Title, request.Description, request.Severity, request.AssigneeId, now);
        incident.ChangeStatus(request.Status, now);
        audit.Record(incident.OrganizationId, "incident.updated", "incident", incident.Id.ToString(), new() { ["status"] = request.Status.ToString() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(incidentId, ct);
    }

    public async Task<IncidentDetailDto> AddNoteAsync(Guid incidentId, IncidentNoteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > IncidentUpdate.MaxBodyLength)
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Body), $"Note must be 1-{IncidentUpdate.MaxBodyLength} characters.")]);
        }

        RequireDeveloper();
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident");
        db.IncidentUpdates.Add(new IncidentUpdate(incident, IncidentUpdateKind.Note, request.Body.Trim(), currentUser.RequireUserId(), clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return await GetAsync(incidentId, ct);
    }

    public async Task<IncidentDetailDto> AttachLogAsync(Guid incidentId, AttachLogRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Log) || request.Log.Length > Incident.MaxLogLength)
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Log), $"Log must be 1-{Incident.MaxLogLength} characters.")]);
        }

        RequireDeveloper();
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident");
        incident.AttachLog(request.Log, clock.GetUtcNow());
        db.IncidentUpdates.Add(new IncidentUpdate(incident, IncidentUpdateKind.System, "Se adjuntó un log al incidente.", currentUser.RequireUserId(), clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return await GetAsync(incidentId, ct);
    }

    /// <summary>Opens a new automatic incident, or records a new occurrence of the open one with the same fingerprint.</summary>
    public async Task<Incident> OpenOrRecurAsync(Guid orgId, string fingerprint, string title, IncidentSeverity severity, IncidentSource source,
        Guid? projectId, string description, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var existing = db.Incidents.Local.FirstOrDefault(i => i.Fingerprint == fingerprint && i.IsOpen)
            ?? await db.Incidents.FirstOrDefaultAsync(i => i.Fingerprint == fingerprint && i.Status != IncidentStatus.Resolved, ct);
        if (existing is not null)
        {
            existing.RecordOccurrence(now);
            db.IncidentUpdates.Add(new IncidentUpdate(existing, IncidentUpdateKind.System, $"Nueva ocurrencia: {description}", null, now));
            return existing;
        }

        var number = await numbers.NextIncidentNumberAsync(orgId, ct);
        var incident = new Incident(orgId, number, title, severity, source, fingerprint, projectId, null, description, null, now);
        db.Incidents.Add(incident);
        db.IncidentUpdates.Add(new IncidentUpdate(incident, IncidentUpdateKind.System, description, null, now));
        audit.Activity(orgId, projectId, "incident.created", $"abrió automáticamente INC-{number}: {incident.Title}", "incident", incident.Id);
        return incident;
    }

    /// <summary>When a later run/deployment succeeds, an open automatic incident is marked as mitigated (a human resolves it).</summary>
    public async Task MarkRecoveredAsync(string fingerprint, string note, CancellationToken ct)
    {
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Fingerprint == fingerprint && (i.Status == IncidentStatus.Open || i.Status == IncidentStatus.Investigating), ct);
        if (incident is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        incident.ChangeStatus(IncidentStatus.Mitigated, now);
        db.IncidentUpdates.Add(new IncidentUpdate(incident, IncidentUpdateKind.System, note, null, now));
    }

    private void RequireDeveloper()
    {
        if (tenant.RequireRole() < OrgRole.Developer)
        {
            throw new ForbiddenException("Viewers cannot modify incidents.");
        }
    }

    private async Task<List<IncidentSummaryDto>> ToSummariesAsync(IReadOnlyCollection<Incident> rows, CancellationToken ct)
    {
        var projectIds = rows.Where(r => r.ProjectId.HasValue).Select(r => r.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var users = await UsersAsync(rows.Select(r => r.AssigneeId), ct);
        return rows.Select(i => new IncidentSummaryDto(i.Id, i.Number, $"INC-{i.Number}", i.Title, i.Severity, i.Status, i.Source, i.ProjectId,
            i.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, i.AssigneeId is { } a ? users.GetValueOrDefault(a) : null, i.OccurrenceCount,
            i.CreatedAt, i.UpdatedAt, i.LastOccurredAt, i.ResolvedAt)).ToList();
    }

    private async Task<Dictionary<Guid, UserRefDto>> UsersAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var set = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return set.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => set.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new UserRefDto(u.Id, u.DisplayName, u.Email), ct);
    }
}
