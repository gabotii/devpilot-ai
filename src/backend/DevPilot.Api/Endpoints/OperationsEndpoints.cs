using DevPilot.Api.Infrastructure;
using DevPilot.Api.Security;
using DevPilot.Application.Admin;
using DevPilot.Application.Ai;
using DevPilot.Application.Common;
using DevPilot.Application.Incidents;
using DevPilot.Application.Integrations;
using DevPilot.Application.Operations;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using DevPilot.Domain.Organizations;

namespace DevPilot.Api.Endpoints;

public static class OperationsEndpoints
{
    /// <summary>Unauthenticated-by-session endpoints: GitHub webhooks (HMAC signature) and the ingestion API (bearer key).</summary>
    public static void MapExternalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/github/{integrationId:guid}", async (Guid integrationId, HttpContext http, WebhookReceiver receiver, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync(http.Request, WebhookReceiver.MaxPayloadBytes, ct);
                if (body is null)
                {
                    return Results.Problem(statusCode: 413, title: "Payload Too Large");
                }

                var headers = http.Request.Headers;
                var result = await receiver.ReceiveGitHubAsync(integrationId, headers["X-GitHub-Event"].ToString(), headers["X-GitHub-Delivery"].ToString(),
                    headers["X-Hub-Signature-256"].ToString(), body, ct);
                return result.Outcome switch
                {
                    WebhookOutcome.Accepted => Results.Accepted(value: new { status = result.Status?.ToString(), detail = result.Detail }),
                    WebhookOutcome.Duplicate => Results.Ok(new { status = "Duplicate" }),
                    WebhookOutcome.InvalidSignature => Results.Problem(statusCode: 401, title: "Invalid signature"),
                    WebhookOutcome.BadRequest => Results.Problem(statusCode: 400, title: "Bad Request", detail: result.Detail),
                    _ => Results.Problem(statusCode: 404, title: "Not Found"),
                };
            })
            .AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RateLimitPolicies.Webhooks)
            .WithTags("Webhooks").WithSummary("GitHub webhook receiver (X-Hub-Signature-256 required; idempotent by X-GitHub-Delivery)");

        var ingest = app.MapGroup("/api/ingest").WithTags("Ingestion").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Webhooks);
        ingest.MapPost("/logs", async (IngestLogBatch batch, HttpContext http, IngestService service, CancellationToken ct) =>
        {
            var key = await service.AuthenticateAsync(http.Request.Headers.Authorization, IngestScopes.Logs, ct);
            return Results.Accepted(value: await service.IngestLogsAsync(key, batch, ct));
        }).WithSummary("Ingest up to 500 log events (Authorization: Bearer dpk_...)");
        ingest.MapPost("/deployments", async (IngestDeploymentRequest request, HttpContext http, IngestService service, CancellationToken ct) =>
        {
            var key = await service.AuthenticateAsync(http.Request.Headers.Authorization, IngestScopes.Deployments, ct);
            return Results.Accepted(value: await service.IngestDeploymentAsync(key, request, ct));
        }).WithSummary("Report a deployment or deployment status change (idempotent by externalId)");
        ingest.MapPost("/pipeline-runs", async (IngestPipelineRunRequest request, HttpContext http, IngestService service, CancellationToken ct) =>
        {
            var key = await service.AuthenticateAsync(http.Request.Headers.Authorization, IngestScopes.Pipelines, ct);
            return Results.Accepted(value: await service.IngestPipelineRunAsync(key, request, ct));
        }).WithSummary("Report a pipeline run status change (idempotent by pipelineKey + runId + attempt)");
    }

    public static void MapOperationsEndpoints(this RouteGroupBuilder org)
    {
        var integrations = org.MapGroup("/integrations").WithTags("Integrations").RequireOrgRole(OrgRole.Admin);
        integrations.MapGet("/", (IntegrationService s, CancellationToken ct) => s.ListAsync(ct));
        integrations.MapPost("/github", async (CreateIntegrationRequest r, IntegrationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateGitHubWebhookAsync(r, ct)));
        integrations.MapPost("/{integrationId:guid}/rotate-secret", (Guid integrationId, IntegrationService s, CancellationToken ct) =>
            s.RotateSecretAsync(integrationId, ct));
        integrations.MapPut("/{integrationId:guid}/status", (Guid integrationId, SetIntegrationStatusRequest r, IntegrationService s, CancellationToken ct) =>
            s.SetStatusAsync(integrationId, r, ct));
        integrations.MapDelete("/{integrationId:guid}", async (Guid integrationId, IntegrationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(integrationId, ct);
            return Results.NoContent();
        });
        integrations.MapGet("/{integrationId:guid}/deliveries", (Guid integrationId, DeliveryStatus? status, int? page, int? pageSize, IntegrationService s,
            CancellationToken ct) => s.ListDeliveriesAsync(integrationId, status, new PageRequest(page ?? 1, pageSize ?? 25), ct));
        integrations.MapPost("/{integrationId:guid}/deliveries/{deliveryId:guid}/retry", (Guid integrationId, Guid deliveryId, IntegrationService s,
            CancellationToken ct) => s.RetryDeliveryAsync(integrationId, deliveryId, ct));

        var repositories = org.MapGroup("/repositories").WithTags("Integrations");
        repositories.MapGet("/", (RepositoryStatus? status, IntegrationService s, CancellationToken ct) => s.ListRepositoriesAsync(status, ct));
        repositories.MapPut("/{repositoryId:guid}", (Guid repositoryId, ConfigureRepositoryRequest r, IntegrationService s, CancellationToken ct) =>
            s.ConfigureRepositoryAsync(repositoryId, r, ct)).RequireOrgRole(OrgRole.Admin);

        var keys = org.MapGroup("/ingest-keys").WithTags("Integrations").RequireOrgRole(OrgRole.Admin);
        keys.MapGet("/", (IntegrationService s, CancellationToken ct) => s.ListIngestKeysAsync(ct));
        keys.MapPost("/", async (CreateIngestKeyRequest r, IntegrationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateIngestKeyAsync(r, ct)));
        keys.MapDelete("/{keyId:guid}", async (Guid keyId, IntegrationService s, CancellationToken ct) =>
        {
            await s.RevokeIngestKeyAsync(keyId, ct);
            return Results.NoContent();
        });

        var ops = org.MapGroup("").WithTags("Operations");
        ops.MapGet("/operations/summary", (OperationsQueryService s, CancellationToken ct) => s.GetSummaryAsync(ct));
        ops.MapGet("/pipeline-runs", (Guid? projectId, Guid? pipelineId, string? branch, RunConclusion? conclusion, bool? failed, int? page, int? pageSize,
            OperationsQueryService s, CancellationToken ct) =>
            s.ListRunsAsync(new RunQuery(projectId, pipelineId, branch, conclusion, failed, page ?? 1, pageSize ?? 25), ct));
        ops.MapGet("/deployments", (Guid? projectId, string? environment, DeploymentStatus? status, int? page, int? pageSize, OperationsQueryService s,
            CancellationToken ct) => s.ListDeploymentsAsync(new DeploymentQuery(projectId, environment, status, page ?? 1, pageSize ?? 25), ct));
        ops.MapGet("/deployments/environments", (OperationsQueryService s, CancellationToken ct) => s.ListEnvironmentsAsync(ct));
        ops.MapGet("/code-events", (Guid? projectId, int? page, int? pageSize, OperationsQueryService s, CancellationToken ct) =>
            s.ListCodeEventsAsync(projectId, new PageRequest(page ?? 1, pageSize ?? 25), ct));
        ops.MapGet("/logs", (Guid? projectId, Domain.Operations.LogLevel? minLevel, string? source, string? q, string? fingerprint, DateTimeOffset? since,
            int? page, int? pageSize, OperationsQueryService s, CancellationToken ct) =>
            s.ListLogsAsync(new LogQuery(projectId, minLevel, source, q, fingerprint, since, page ?? 1, pageSize ?? 50), ct)).WithTags("Logs");
        ops.MapGet("/logs/groups", (Guid? projectId, DateTimeOffset? since, OperationsQueryService s, CancellationToken ct) =>
            s.ListErrorGroupsAsync(new LogQuery(projectId, null, null, null, null, since), ct)).WithTags("Logs");

        var incidents = org.MapGroup("/incidents").WithTags("Incidents");
        incidents.MapGet("/", (IncidentStatus? status, bool? open, IncidentSeverity? severity, Guid? projectId, string? q, int? page, int? pageSize,
            IncidentService s, CancellationToken ct) =>
            s.ListAsync(new IncidentListQuery(status, open, severity, projectId, q, page ?? 1, pageSize ?? 25), ct));
        incidents.MapPost("/", async (CreateIncidentRequest r, IncidentService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateAsync(r, ct)));
        incidents.MapGet("/{incidentId:guid}", (Guid incidentId, IncidentService s, CancellationToken ct) => s.GetAsync(incidentId, ct));
        incidents.MapPut("/{incidentId:guid}", (Guid incidentId, UpdateIncidentRequest r, IncidentService s, CancellationToken ct) =>
            s.UpdateAsync(incidentId, r, ct));
        incidents.MapPost("/{incidentId:guid}/notes", (Guid incidentId, IncidentNoteRequest r, IncidentService s, CancellationToken ct) =>
            s.AddNoteAsync(incidentId, r, ct));
        incidents.MapPost("/{incidentId:guid}/log", (Guid incidentId, AttachLogRequest r, IncidentService s, CancellationToken ct) =>
            s.AttachLogAsync(incidentId, r, ct));
        incidents.MapPost("/{incidentId:guid}/analyze", (Guid incidentId, AiAnalysisService s, CancellationToken ct) =>
            s.AnalyzeIncidentAsync(incidentId, ct)).RequireRateLimiting(RateLimitPolicies.Ai);

        var ai = org.MapGroup("/ai").WithTags("AI");
        ai.MapGet("/status", (AiAnalysisService s, CancellationToken ct) => s.GetStatusAsync(ct));
        ai.MapPost("/analyses", (AnalyzeLogsRequest r, AiAnalysisService s, CancellationToken ct) => s.AnalyzeLogsAsync(r, ct))
            .RequireRateLimiting(RateLimitPolicies.Ai);
        ai.MapPost("/analyses/error-group/{fingerprint}", (string fingerprint, AiAnalysisService s, CancellationToken ct) =>
            s.AnalyzeErrorGroupAsync(fingerprint, ct)).RequireRateLimiting(RateLimitPolicies.Ai);
        ai.MapGet("/analyses", (AiAnalysisKind? kind, Guid? incidentId, Guid? projectId, int? page, int? pageSize, AiAnalysisService s, CancellationToken ct) =>
            s.ListAsync(kind, incidentId, projectId, new PageRequest(page ?? 1, pageSize ?? 20), ct));
        ai.MapGet("/analyses/{analysisId:guid}", (Guid analysisId, AiAnalysisService s, CancellationToken ct) => s.GetAsync(analysisId, ct));
        ai.MapPost("/assistant", (AskRequest r, AiAnalysisService s, CancellationToken ct) => s.AskAsync(r, ct)).RequireRateLimiting(RateLimitPolicies.Ai);
        org.MapPost("/projects/{projectId:guid}/activity-summary", (Guid projectId, int? days, AiAnalysisService s, CancellationToken ct) =>
            s.SummarizeProjectActivityAsync(projectId, days ?? 7, ct)).RequireRateLimiting(RateLimitPolicies.Ai).WithTags("AI");

        var admin = org.MapGroup("/admin").WithTags("Administration").RequireOrgRole(OrgRole.Admin);
        admin.MapGet("/overview", (AdminService s, CancellationToken ct) => s.GetOverviewAsync(ct));
        admin.MapGet("/audit", (string? action, Guid? actorUserId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, AdminService s,
            CancellationToken ct) => s.ListAuditAsync(new AuditQuery(action, actorUserId, from, to, page ?? 1, pageSize ?? 50), ct));
    }

    /// <summary>Reads the raw body (needed for signature verification) with a hard size limit. Returns null when too large.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }
}
