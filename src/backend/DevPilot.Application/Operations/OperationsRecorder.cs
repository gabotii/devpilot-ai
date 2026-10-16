using DevPilot.Application.Common;
using DevPilot.Application.Incidents;
using DevPilot.Application.Notifications;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Operations;
using DevPilot.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Operations;

public sealed record RunEvent(
    Guid OrganizationId,
    EventSource Source,
    string PipelineKey,
    string PipelineName,
    string? PipelinePath,
    Guid? RepositoryId,
    Guid? ProjectId,
    string? DefaultBranch,
    string ExternalRunId,
    int Attempt,
    long? RunNumber,
    string? Branch,
    string? CommitSha,
    string? Trigger,
    string? Actor,
    DateTimeOffset CreatedAt,
    PipelineRunUpdate Update);

public sealed record DeploymentEvent(
    Guid OrganizationId,
    EventSource Source,
    string ExternalId,
    string Environment,
    string? Ref,
    string? CommitSha,
    string? Description,
    string? Creator,
    Guid? RepositoryId,
    Guid? ProjectId,
    DateTimeOffset CreatedAt,
    DeploymentStatus? Status,
    string? Url,
    DateTimeOffset StatusAt);

/// <summary>
/// Records pipeline runs and deployments from any source (GitHub webhooks, ingestion API) idempotently, and raises
/// incidents and alerts for failures. Must run inside a tenant (or system) scope for the event's organization.
/// </summary>
public sealed class OperationsRecorder(IAppDbContext db, IncidentService incidents, Notifier notifier, AuditLog audit, TimeProvider clock)
{
    public async Task<PipelineRun> RecordRunAsync(RunEvent e, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.FirstOrDefaultAsync(p => p.Source == e.Source && p.ExternalKey == e.PipelineKey, ct);
        if (pipeline is null)
        {
            pipeline = new Pipeline(e.OrganizationId, e.Source, e.PipelineKey, e.PipelineName, e.PipelinePath, e.RepositoryId, e.ProjectId,
                clock.GetUtcNow());
            db.Pipelines.Add(pipeline);
        }
        else
        {
            pipeline.Update(e.PipelineName, e.PipelinePath, e.ProjectId);
        }

        var run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.PipelineId == pipeline.Id && r.ExternalRunId == e.ExternalRunId && r.Attempt == e.Attempt, ct);
        if (run is null)
        {
            run = new PipelineRun(pipeline, e.ExternalRunId, e.Attempt, e.RunNumber, e.Branch, e.CommitSha, e.Trigger, e.Actor, e.CreatedAt);
            db.PipelineRuns.Add(run);
        }

        var changed = run.Apply(e.Update);
        if (changed && run.Status == RunStatus.Completed)
        {
            await HandleRunOutcomeAsync(pipeline, run, e.DefaultBranch, ct);
        }

        return run;
    }

    public async Task<Deployment> RecordDeploymentAsync(DeploymentEvent e, CancellationToken ct)
    {
        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Source == e.Source && d.ExternalId == e.ExternalId, ct);
        if (deployment is null)
        {
            deployment = new Deployment(e.OrganizationId, e.Source, e.ExternalId, e.Environment, e.Ref, e.CommitSha, e.Description, e.Creator,
                e.RepositoryId, e.ProjectId, e.CreatedAt);
            db.Deployments.Add(deployment);
        }

        if (e.Status is { } status && deployment.ApplyStatus(status, e.Url, e.StatusAt))
        {
            await HandleDeploymentOutcomeAsync(deployment, ct);
        }

        return deployment;
    }

    private async Task HandleRunOutcomeAsync(Pipeline pipeline, PipelineRun run, string? defaultBranch, CancellationToken ct)
    {
        // Failures are alerts only on the default branch (or when the branch is unknown); feature-branch failures are routine.
        var watched = defaultBranch is null || run.Branch is null || string.Equals(run.Branch, defaultBranch, StringComparison.Ordinal);
        var fingerprint = $"pipeline:{pipeline.Id}:{run.Branch ?? "-"}";
        var label = $"{pipeline.Name}{(run.RunNumber is { } n ? $" #{n}" : string.Empty)}";

        if (run.IsFailed)
        {
            audit.Activity(run.OrganizationId, run.ProjectId, "pipeline.failed", $"falló el pipeline {label} en {run.Branch ?? "rama desconocida"}",
                "pipeline_run", run.Id);
            if (!watched)
            {
                return;
            }

            var incident = await incidents.OpenOrRecurAsync(run.OrganizationId, fingerprint, $"Pipeline fallido: {pipeline.Name} ({run.Branch ?? "?"})",
                IncidentSeverity.High, IncidentSource.PipelineFailure, run.ProjectId,
                $"La ejecución {label} terminó con resultado {run.Conclusion}. Commit {run.CommitSha?[..Math.Min(7, run.CommitSha.Length)] ?? "?"}.",
                ct);
            incident.LinkRun(run.Id);
            await NotifyAsync(run.OrganizationId, run.ProjectId, NotificationKinds.PipelineFailed, $"Falló {label}",
                $"Rama {run.Branch ?? "?"}: {run.Conclusion}", $"/incidents/{incident.Id}", $"run-failed:{run.Id}", ct);
        }
        else if (run.Conclusion == RunConclusion.Success && watched)
        {
            await incidents.MarkRecoveredAsync(fingerprint, $"La ejecución {label} volvió a pasar.", ct);
        }
    }

    private async Task HandleDeploymentOutcomeAsync(Deployment deployment, CancellationToken ct)
    {
        var where = $"{deployment.Environment}{(deployment.Ref is null ? string.Empty : $" ({deployment.Ref})")}";
        switch (deployment.Status)
        {
            case DeploymentStatus.Success:
                audit.Activity(deployment.OrganizationId, deployment.ProjectId, "deployment.succeeded", $"desplegó en {where}", "deployment", deployment.Id);
                if (deployment.IsProduction)
                {
                    await NotifyAsync(deployment.OrganizationId, deployment.ProjectId, NotificationKinds.DeploymentSucceeded,
                        $"Despliegue correcto en {deployment.Environment}", deployment.Description, "/deployments", $"deploy-ok:{deployment.Id}", ct);
                    await incidents.MarkRecoveredAsync(DeploymentFingerprint(deployment), "Un despliegue posterior terminó correctamente.", ct);
                }

                break;
            case DeploymentStatus.Failure or DeploymentStatus.Error:
                audit.Activity(deployment.OrganizationId, deployment.ProjectId, "deployment.failed", $"falló el despliegue en {where}", "deployment", deployment.Id);
                var severity = deployment.IsProduction ? IncidentSeverity.Critical : IncidentSeverity.Medium;
                var incident = await incidents.OpenOrRecurAsync(deployment.OrganizationId, DeploymentFingerprint(deployment),
                    $"Despliegue fallido en {deployment.Environment}", severity, IncidentSource.DeploymentFailure, deployment.ProjectId,
                    $"Despliegue {deployment.ExternalId} ({deployment.Ref ?? "sin ref"}) terminó con estado {deployment.Status}. {deployment.Description}", ct);
                incident.LinkDeployment(deployment.Id);
                await NotifyAsync(deployment.OrganizationId, deployment.ProjectId, NotificationKinds.DeploymentFailed,
                    $"Falló el despliegue en {deployment.Environment}", deployment.Description, $"/incidents/{incident.Id}", $"deploy-failed:{deployment.Id}", ct);
                break;
        }
    }

    private static string DeploymentFingerprint(Deployment d) => $"deploy:{d.Environment.ToLowerInvariant()}:{d.ProjectId?.ToString() ?? d.RepositoryId?.ToString() ?? "org"}";

    /// <summary>Alerts go to organization admins/owners and, when known, to the members of the affected project.</summary>
    private async Task NotifyAsync(Guid orgId, Guid? projectId, string kind, string title, string? body, string link, string dedupeKey, CancellationToken ct)
    {
        await notifier.NotifyRoleAsync(orgId, OrgRole.Admin, kind, title, body, link, dedupeKey, ct);
        if (projectId is { } pid)
        {
            var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == pid).Select(m => m.UserId).ToListAsync(ct);
            var already = await db.Notifications.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.OrganizationId == orgId && n.DedupeKey == dedupeKey).Select(n => n.UserId).ToListAsync(ct);
            foreach (var userId in members.Except(already))
            {
                notifier.Notify(orgId, userId, kind, title, body, link, dedupeKey);
            }
        }
    }
}
