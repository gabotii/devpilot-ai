using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevPilot.Application.Common;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Operations;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Ai;

public sealed record AnalyzeLogsRequest(string Content, string? Kind, Guid? ProjectId, string? Environment, string? Title);

public sealed record AskRequest(string Question, Guid? ProjectId);

public sealed record AiAnalysisDto(Guid Id, AiAnalysisKind Kind, AiAnalysisStatus Status, string? Subject, Guid? ProjectId, Guid? IncidentId,
    string? Provider, string? Model, string? ErrorCode, int InputChars, int? LatencyMs, DateTimeOffset CreatedAt, string? RequestedBy,
    JsonElement? Result);

public sealed record AiStatusDto(bool Reachable, string? Provider, string? Model, bool ExternalProviderConfigured, string Mode);

public sealed class AnalyzeLogsRequestValidator : AbstractValidator<AnalyzeLogsRequest>
{
    public AnalyzeLogsRequestValidator()
    {
        RuleFor(x => x.Content).NotEmpty().MaximumLength(200_000);
        RuleFor(x => x.Kind).Must(k => k is null or "log_analysis" or "pipeline_failure" or "incident_summary");
        RuleFor(x => x.Title).MaximumLength(300);
        RuleFor(x => x.Environment).MaximumLength(50);
    }
}

/// <summary>
/// Entry point for every AI feature. Only data the caller is authorized to see (tenant-filtered queries) is sent to the
/// AI service; the service redacts secrets before any external call. Each request is recorded in the analysis history.
/// The AI never acts: results are advisory text shown to humans.
/// </summary>
public sealed class AiAnalysisService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IAiServiceClient client,
    AuditLog audit,
    TimeProvider clock,
    IValidator<AnalyzeLogsRequest> validator)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var health = await client.GetHealthAsync(ct);
        return health is null
            ? new AiStatusDto(false, null, null, false, "unavailable")
            : new AiStatusDto(true, health.Provider, health.Model, health.ExternalProviderConfigured, health.Provider == "heuristic" ? "deterministic" : "model");
    }

    public async Task<AiAnalysisDto> AnalyzeLogsAsync(AnalyzeLogsRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        RequireDeveloper();
        string? projectName = null;
        if (request.ProjectId is { } projectId)
        {
            projectName = await db.Projects.Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Project");
        }

        var kind = request.Kind ?? "log_analysis";
        var aiKind = kind == "pipeline_failure" ? AiAnalysisKind.PipelineFailure : AiAnalysisKind.LogAnalysis;
        return await RunAnalysisAsync(aiKind, kind, request.Content, new AiAnalysisContext(projectName, request.Environment, null, request.Title, null),
            request.ProjectId, null, request.Title ?? "Análisis de log", ct);
    }

    public async Task<AiAnalysisDto> AnalyzeIncidentAsync(Guid incidentId, CancellationToken ct)
    {
        RequireDeveloper();
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident");
        var updates = await db.IncidentUpdates.AsNoTracking().Where(u => u.IncidentId == incidentId).OrderBy(u => u.At).Take(50).ToListAsync(ct);
        string? pipelineName = null;
        string? branch = null;
        if (incident.PipelineRunId is { } runId)
        {
            var run = await db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is not null)
            {
                branch = run.Branch;
                pipelineName = await db.Pipelines.Where(p => p.Id == run.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct);
            }
        }

        var projectName = incident.ProjectId is { } pid ? await db.Projects.Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var content = new StringBuilder();
        content.AppendLine(CultureInfo.InvariantCulture, $"Incident: {incident.Title} (severity {incident.Severity}, status {incident.Status}, occurrences {incident.OccurrenceCount})");
        if (incident.Description is not null)
        {
            content.AppendLine(incident.Description);
        }

        foreach (var update in updates)
        {
            content.AppendLine(CultureInfo.InvariantCulture, $"[{update.At:u}] {update.Kind}: {update.Body}");
        }

        if (incident.LogExcerpt is not null)
        {
            content.AppendLine("--- log ---").AppendLine(incident.LogExcerpt);
        }

        var kind = incident.Source == IncidentSource.PipelineFailure && incident.LogExcerpt is not null ? "pipeline_failure" : "incident_summary";
        return await RunAnalysisAsync(kind == "pipeline_failure" ? AiAnalysisKind.PipelineFailure : AiAnalysisKind.IncidentSummary, kind,
            content.ToString(), new AiAnalysisContext(projectName, null, pipelineName, incident.Title, branch), incident.ProjectId, incident.Id,
            $"INC-{incident.Number}: {incident.Title}", ct);
    }

    public async Task<AiAnalysisDto> AnalyzeErrorGroupAsync(string fingerprint, CancellationToken ct)
    {
        RequireDeveloper();
        var events = await db.LogEvents.AsNoTracking().Where(l => l.Fingerprint == fingerprint).OrderByDescending(l => l.Timestamp).Take(50).ToListAsync(ct);
        if (events.Count == 0)
        {
            throw new NotFoundException("Error group");
        }

        var content = string.Join('\n', events.OrderBy(e => e.Timestamp).Select(e =>
            $"{e.Timestamp:O} {e.Level.ToString().ToUpperInvariant()} [{e.Source}] {e.Message}{(e.Exception is null ? string.Empty : "\n" + e.Exception)}"));
        var projectId = events[0].ProjectId;
        var projectName = projectId is { } pid ? await db.Projects.Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        return await RunAnalysisAsync(AiAnalysisKind.LogAnalysis, "log_analysis", content,
            new AiAnalysisContext(projectName, events[0].Environment, null, events[0].Message, null), projectId, null, $"Grupo de errores {fingerprint}", ct);
    }

    public async Task<AiAnalysisDto> AskAsync(AskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        {
            throw new ValidationException([new ValidationFailure(nameof(request.Question), "The question must be 1-2000 characters.")]);
        }

        var orgId = tenant.RequireOrganizationId();
        var orgName = await db.Organizations.Where(o => o.Id == orgId).Select(o => o.Name).FirstAsync(ct);
        var documents = await CollectAuthorizedDocumentsAsync(request.ProjectId, ct);
        var analysis = NewRecord(AiAnalysisKind.Assistant, request.ProjectId, null, request.Question, request.Question);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await client.AnswerAsync(new AiAssistantRequest(analysis.Id.ToString("N"), request.Question, orgName, documents), ct);
            analysis.Succeed(result.Provider, result.Model, JsonSerializer.Serialize(result, Json), result.LatencyMs);
        }
        catch (AiServiceException ex)
        {
            analysis.Fail(ex.Code, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(analysis.Id, ct);
    }

    public async Task<AiAnalysisDto> SummarizeProjectActivityAsync(Guid projectId, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
        var since = clock.GetUtcNow().AddDays(-days);
        var activity = await db.ActivityEntries.AsNoTracking().Where(a => a.ProjectId == projectId && a.At >= since).OrderByDescending(a => a.At)
            .Take(300).ToListAsync(ct);
        var actorIds = activity.Where(a => a.ActorUserId.HasValue).Select(a => a.ActorUserId!.Value).Distinct().ToList();
        var actors = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var open = db.WorkItems.AsNoTracking().Where(w => w.ProjectId == projectId && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled);
        var stats = new Dictionary<string, int>
        {
            ["openItems"] = await open.CountAsync(ct),
            ["overdue"] = await open.CountAsync(w => w.DueDate < today, ct),
            ["openCritical"] = await open.CountAsync(w => w.Priority == Domain.WorkItems.Priority.Critical, ct),
            ["completed"] = await db.WorkItems.CountAsync(w => w.ProjectId == projectId && w.CompletedAt >= since, ct),
            ["failedPipelines"] = await db.PipelineRuns.CountAsync(r => r.ProjectId == projectId && r.CreatedAt >= since
                && (r.Conclusion == RunConclusion.Failure || r.Conclusion == RunConclusion.TimedOut), ct),
        };

        var analysis = NewRecord(AiAnalysisKind.ActivitySummary, projectId, null, $"Resumen de {project.Name} ({days} días)",
            string.Join('\n', activity.Select(a => a.Summary)));
        var started = Stopwatch.GetTimestamp();
        try
        {
            var events = activity.Select(a => new AiActivityEvent(a.At.ToString("O", CultureInfo.InvariantCulture),
                a.ActorUserId is { } id ? actors.GetValueOrDefault(id) : null, a.Summary)).ToList();
            var result = await client.SummarizeActivityAsync(new AiActivitySummaryRequest(analysis.Id.ToString("N"), project.Name, days, events, stats), ct);
            analysis.Succeed(result.Provider, result.Model, JsonSerializer.Serialize(result, Json), result.LatencyMs);
        }
        catch (AiServiceException ex)
        {
            analysis.Fail(ex.Code, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(analysis.Id, ct);
    }

    public async Task<PagedResult<AiAnalysisDto>> ListAsync(AiAnalysisKind? kind, Guid? incidentId, Guid? projectId, PageRequest page, CancellationToken ct)
    {
        var query = db.AiAnalyses.AsNoTracking();
        if (kind is { } k)
        {
            query = query.Where(a => a.Kind == k);
        }

        if (incidentId is { } iid)
        {
            query = query.Where(a => a.IncidentId == iid);
        }

        if (projectId is { } pid)
        {
            query = query.Where(a => a.ProjectId == pid);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.CreatedAt).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<AiAnalysisDto>(await ToDtosAsync(rows, ct), page.SafePage, page.SafePageSize, total);
    }

    public async Task<AiAnalysisDto> GetAsync(Guid analysisId, CancellationToken ct)
    {
        var row = await db.AiAnalyses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == analysisId, ct) ?? throw new NotFoundException("Analysis");
        return (await ToDtosAsync([row], ct))[0];
    }

    private async Task<AiAnalysisDto> RunAnalysisAsync(AiAnalysisKind kind, string wireKind, string content, AiAnalysisContext context, Guid? projectId,
        Guid? incidentId, string subject, CancellationToken ct)
    {
        var analysis = NewRecord(kind, projectId, incidentId, subject, content);
        audit.Record(analysis.OrganizationId, "ai.analysis_requested", "ai_analysis", analysis.Id.ToString(),
            new() { ["kind"] = kind.ToString(), ["inputChars"] = content.Length.ToString(CultureInfo.InvariantCulture) });
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await client.AnalyzeAsync(new AiAnalysisRequest(analysis.Id.ToString("N"), wireKind, content, context), ct);
            analysis.Succeed(result.Provider, result.Model, JsonSerializer.Serialize(result, Json), result.LatencyMs);
        }
        catch (AiServiceException ex)
        {
            analysis.Fail(ex.Code, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await db.SaveChangesAsync(ct);
            throw;
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(analysis.Id, ct);
    }

    private AiAnalysis NewRecord(AiAnalysisKind kind, Guid? projectId, Guid? incidentId, string subject, string input)
    {
        var record = new AiAnalysis(tenant.RequireOrganizationId(), kind, currentUser.RequireUserId(), projectId, incidentId, subject, input.Length,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input))), clock.GetUtcNow());
        db.AiAnalyses.Add(record);
        return record;
    }

    /// <summary>Builds the assistant's context from tenant-filtered data only (what the caller can already see in the app).</summary>
    private async Task<List<AiContextDocument>> CollectAuthorizedDocumentsAsync(Guid? projectId, CancellationToken ct)
    {
        var docs = new List<AiContextDocument>();
        var projects = await db.Projects.AsNoTracking().Where(p => projectId == null || p.Id == projectId).OrderByDescending(p => p.UpdatedAt).Take(30).ToListAsync(ct);
        foreach (var p in projects)
        {
            var open = await db.WorkItems.CountAsync(w => w.ProjectId == p.Id && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled, ct);
            docs.Add(new AiContextDocument("project", p.Id.ToString(), $"{p.Name} ({p.Key})",
                $"Proyecto {p.Name} ({p.Key}), estado {p.Status}, {open} tareas abiertas. {p.Description}"));
        }

        var projectKeys = projects.ToDictionary(p => p.Id, p => p.Key);
        var items = await db.WorkItems.AsNoTracking()
            .Where(w => (projectId == null || w.ProjectId == projectId) && w.StatusCategory != StatusCategory.Done && w.StatusCategory != StatusCategory.Cancelled)
            .OrderByDescending(w => w.Priority).ThenByDescending(w => w.UpdatedAt).Take(80).ToListAsync(ct);
        foreach (var w in items)
        {
            var key = projectKeys.TryGetValue(w.ProjectId, out var k) ? $"{k}-{w.Number}" : $"#{w.Number}";
            docs.Add(new AiContextDocument("work_item", w.Id.ToString(), $"{key} {w.Title}",
                $"{key} ({w.Type}, prioridad {w.Priority}, categoría {w.StatusCategory}{(w.DueDate is { } d ? $", vence {d:yyyy-MM-dd}" : string.Empty)}). "
                + Clip(w.Description, 600)));
        }

        var incidents = await db.Incidents.AsNoTracking().Where(i => (projectId == null || i.ProjectId == projectId) && i.Status != IncidentStatus.Resolved)
            .OrderByDescending(i => i.Severity).Take(30).ToListAsync(ct);
        docs.AddRange(incidents.Select(i => new AiContextDocument("incident", i.Id.ToString(), $"INC-{i.Number} {i.Title}",
            $"Incidente INC-{i.Number}, severidad {i.Severity}, estado {i.Status}, {i.OccurrenceCount} ocurrencias, última {i.LastOccurredAt:u}. {Clip(i.Description, 600)}")));

        var since = clock.GetUtcNow().AddDays(-14);
        var runs = await db.PipelineRuns.AsNoTracking().Where(r => (projectId == null || r.ProjectId == projectId) && r.CreatedAt >= since
            && (r.Conclusion == RunConclusion.Failure || r.Conclusion == RunConclusion.TimedOut)).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        var pipelineNames = await db.Pipelines.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        docs.AddRange(runs.Select(r => new AiContextDocument("pipeline_run", r.Id.ToString(), $"{pipelineNames.GetValueOrDefault(r.PipelineId, "pipeline")} #{r.RunNumber}",
            $"Ejecución fallida ({r.Conclusion}) en la rama {r.Branch}, commit {r.CommitSha}, {r.CreatedAt:u}.")));

        var deployments = await db.Deployments.AsNoTracking().Where(d => (projectId == null || d.ProjectId == projectId) && d.CreatedAt >= since)
            .OrderByDescending(d => d.CreatedAt).Take(20).ToListAsync(ct);
        docs.AddRange(deployments.Select(d => new AiContextDocument("deployment", d.Id.ToString(), $"Despliegue {d.Environment} {d.Ref}",
            $"Despliegue en {d.Environment}: estado {d.Status}, ref {d.Ref}, commit {d.CommitSha}, {d.CreatedAt:u}. {d.Description}")));
        return docs;
    }

    private async Task<List<AiAnalysisDto>> ToDtosAsync(IReadOnlyCollection<AiAnalysis> rows, CancellationToken ct)
    {
        var userIds = rows.Select(r => r.RequestedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return rows.Select(a => new AiAnalysisDto(a.Id, a.Kind, a.Status, a.Subject, a.ProjectId, a.IncidentId, a.Provider, a.Model, a.ErrorCode,
            a.InputChars, a.LatencyMs, a.CreatedAt, users.GetValueOrDefault(a.RequestedByUserId),
            a.ResultJson is null ? null : JsonDocument.Parse(a.ResultJson).RootElement.Clone())).ToList();
    }

    private void RequireDeveloper()
    {
        if (tenant.RequireRole() < OrgRole.Developer)
        {
            throw new ForbiddenException("Viewers cannot run AI analyses.");
        }
    }

    private static string Clip(string? text, int max) => text is null ? string.Empty : text.Length > max ? text[..max] + "…" : text;
}
