using System.Globalization;
using System.Text.Json;
using DevPilot.Application.Common;
using DevPilot.Application.Operations;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Integrations;

/// <summary>
/// Turns stored GitHub deliveries into domain records. Payloads are untrusted: only the fields we need are read,
/// strings are length-limited by the entities, URLs are restricted to https, and nothing from the payload is executed.
/// Events of repositories that an admin has not authorized are recorded as ignored.
/// </summary>
public sealed class GitHubEventProcessor(IAppDbContext db, OperationsRecorder recorder, AuditLog audit, TimeProvider clock)
{
    private static readonly HashSet<string> RepositoryEvents = ["push", "pull_request", "workflow_run", "deployment", "deployment_status"];

    public async Task ProcessAsync(Integration integration, WebhookDelivery delivery, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (delivery.Payload is null)
        {
            delivery.MarkFailed("payload_missing", now);
            return;
        }

        using var doc = JsonDocument.Parse(delivery.Payload);
        var root = doc.RootElement;

        if (delivery.EventType == "ping")
        {
            integration.MarkVerified(now);
            if (root.TryGetProperty("repository", out var pingRepo))
            {
                var repo = await UpsertRepositoryAsync(integration, pingRepo, ct);
                delivery.SetRepository(repo.FullName);
            }

            delivery.MarkProcessed(now);
            return;
        }

        if (!RepositoryEvents.Contains(delivery.EventType))
        {
            delivery.MarkIgnored($"unsupported_event:{delivery.EventType}", now);
            return;
        }

        if (!root.TryGetProperty("repository", out var repoElement))
        {
            delivery.MarkIgnored("missing_repository", now);
            return;
        }

        var repository = await UpsertRepositoryAsync(integration, repoElement, ct);
        delivery.SetRepository(repository.FullName);
        if (repository.Status != RepositoryStatus.Authorized)
        {
            delivery.MarkIgnored(repository.Status == RepositoryStatus.Pending ? "repository_pending_authorization" : "repository_ignored", now);
            return;
        }

        switch (delivery.EventType)
        {
            case "push":
                await HandlePushAsync(repository, root, ct);
                break;
            case "pull_request":
                await HandlePullRequestAsync(repository, root, ct);
                break;
            case "workflow_run":
                await HandleWorkflowRunAsync(repository, root, ct);
                break;
            case "deployment":
            case "deployment_status":
                await HandleDeploymentAsync(repository, root, delivery.EventType == "deployment_status", ct);
                break;
        }

        delivery.MarkProcessed(now);
    }

    private async Task<Repository> UpsertRepositoryAsync(Integration integration, JsonElement repo, CancellationToken ct)
    {
        var externalId = repo.GetProperty("id").GetInt64();
        var fullName = Str(repo, "full_name") ?? $"repo-{externalId}";
        var repository = db.Repositories.Local.FirstOrDefault(r => r.IntegrationId == integration.Id && r.ExternalId == externalId)
            ?? await db.Repositories.FirstOrDefaultAsync(r => r.IntegrationId == integration.Id && r.ExternalId == externalId, ct);
        var now = clock.GetUtcNow();
        if (repository is null)
        {
            repository = new Repository(integration.OrganizationId, integration.Id, externalId, fullName, Str(repo, "default_branch"), Str(repo, "html_url"), now);
            db.Repositories.Add(repository);
            audit.Activity(integration.OrganizationId, null, "repository.detected", $"detectó el repositorio {fullName} (pendiente de autorización)",
                "repository", repository.Id);
        }
        else
        {
            repository.Touch(fullName, Str(repo, "default_branch"), now);
        }

        return repository;
    }

    private async Task HandlePushAsync(Repository repo, JsonElement root, CancellationToken ct)
    {
        if (Bool(root, "deleted"))
        {
            return; // branch deletion
        }

        var after = Str(root, "after") ?? throw new FormatException("push.after is missing");
        var branch = BranchFromRef(Str(root, "ref"));
        var commits = root.TryGetProperty("commits", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
        var head = root.TryGetProperty("head_commit", out var hc) && hc.ValueKind == JsonValueKind.Object ? hc : default;
        var title = head.ValueKind == JsonValueKind.Object ? FirstLine(Str(head, "message")) ?? "(sin mensaje)" : "(sin mensaje)";
        var author = root.TryGetProperty("pusher", out var pusher) ? Str(pusher, "name") : null;
        var at = head.ValueKind == JsonValueKind.Object ? Date(head, "timestamp") ?? clock.GetUtcNow() : clock.GetUtcNow();
        await UpsertCodeEventAsync(repo, CodeEventKind.Push, after, title, author, branch, Str(root, "compare"), "pushed", commits, at, ct);
    }

    private async Task HandlePullRequestAsync(Repository repo, JsonElement root, CancellationToken ct)
    {
        var pr = root.GetProperty("pull_request");
        var number = pr.GetProperty("number").GetInt64().ToString(CultureInfo.InvariantCulture);
        var state = Bool(pr, "merged") ? "merged" : Str(pr, "state");
        var author = pr.TryGetProperty("user", out var user) ? Str(user, "login") : null;
        var branch = pr.TryGetProperty("head", out var head) ? Str(head, "ref") : null;
        await UpsertCodeEventAsync(repo, CodeEventKind.PullRequest, number, $"#{number} {Str(pr, "title")}", author, branch, Str(pr, "html_url"), state,
            pr.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Number ? commits.GetInt32() : 0,
            Date(pr, "updated_at") ?? clock.GetUtcNow(), ct);
    }

    private async Task UpsertCodeEventAsync(Repository repo, CodeEventKind kind, string externalId, string title, string? author, string? branch,
        string? url, string? state, int commitCount, DateTimeOffset at, CancellationToken ct)
    {
        var existing = await db.CodeEvents.FirstOrDefaultAsync(e => e.RepositoryId == repo.Id && e.Kind == kind && e.ExternalId == externalId, ct);
        if (existing is null)
        {
            db.CodeEvents.Add(new CodeEvent(repo.OrganizationId, repo.Id, repo.ProjectId, kind, externalId, title, author, branch, url, state, commitCount, at));
        }
        else if (at >= existing.OccurredAt)
        {
            existing.Update(title, author, branch, url, state, commitCount, at);
        }
    }

    private async Task HandleWorkflowRunAsync(Repository repo, JsonElement root, CancellationToken ct)
    {
        var run = root.GetProperty("workflow_run");
        var status = Str(run, "status") switch
        {
            "completed" => RunStatus.Completed,
            "in_progress" => RunStatus.InProgress,
            _ => RunStatus.Queued, // queued, requested, waiting, pending
        };
        RunConclusion? conclusion = status == RunStatus.Completed ? ParseConclusion(Str(run, "conclusion")) : null;
        var workflowId = run.TryGetProperty("workflow_id", out var wf) ? wf.GetInt64().ToString(CultureInfo.InvariantCulture) : Str(run, "name") ?? "workflow";
        var updatedAt = Date(run, "updated_at") ?? clock.GetUtcNow();

        await recorder.RecordRunAsync(new RunEvent(
            repo.OrganizationId,
            EventSource.GitHub,
            $"github:{repo.ExternalId}:{workflowId}",
            Str(run, "name") ?? "workflow",
            Str(run, "path"),
            repo.Id,
            repo.ProjectId,
            repo.DefaultBranch,
            run.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
            run.TryGetProperty("run_attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number ? attempt.GetInt32() : 1,
            run.TryGetProperty("run_number", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt64() : null,
            Str(run, "head_branch"),
            Str(run, "head_sha"),
            Str(run, "event"),
            run.TryGetProperty("actor", out var actor) ? Str(actor, "login") : null,
            Date(run, "created_at") ?? updatedAt,
            new PipelineRunUpdate(status, conclusion, Date(run, "run_started_at"), status == RunStatus.Completed ? updatedAt : null, updatedAt,
                Str(run, "html_url"))), ct);
    }

    private async Task HandleDeploymentAsync(Repository repo, JsonElement root, bool isStatus, CancellationToken ct)
    {
        var deployment = root.GetProperty("deployment");
        DeploymentStatus? status = null;
        string? url = null;
        var statusAt = Date(deployment, "updated_at") ?? clock.GetUtcNow();
        if (isStatus)
        {
            var ds = root.GetProperty("deployment_status");
            status = ParseDeploymentState(Str(ds, "state"));
            url = Str(ds, "environment_url") ?? Str(ds, "target_url") ?? Str(ds, "log_url");
            statusAt = Date(ds, "created_at") ?? statusAt;
        }

        await recorder.RecordDeploymentAsync(new DeploymentEvent(
            repo.OrganizationId,
            EventSource.GitHub,
            $"github:{deployment.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture)}",
            Str(deployment, "environment") ?? "unknown",
            Str(deployment, "ref"),
            Str(deployment, "sha"),
            Str(deployment, "description"),
            deployment.TryGetProperty("creator", out var creator) ? Str(creator, "login") : null,
            repo.Id,
            repo.ProjectId,
            Date(deployment, "created_at") ?? statusAt,
            status ?? DeploymentStatus.Pending,
            url,
            statusAt), ct);
    }

    public static RunConclusion ParseConclusion(string? value) => value switch
    {
        "success" => RunConclusion.Success,
        "failure" => RunConclusion.Failure,
        "cancelled" => RunConclusion.Cancelled,
        "timed_out" => RunConclusion.TimedOut,
        "skipped" => RunConclusion.Skipped,
        "action_required" => RunConclusion.ActionRequired,
        "neutral" => RunConclusion.Neutral,
        "stale" => RunConclusion.Stale,
        _ => RunConclusion.Unknown,
    };

    public static DeploymentStatus ParseDeploymentState(string? value) => value switch
    {
        "success" => DeploymentStatus.Success,
        "failure" => DeploymentStatus.Failure,
        "error" => DeploymentStatus.Error,
        "inactive" => DeploymentStatus.Inactive,
        "in_progress" => DeploymentStatus.InProgress,
        "queued" => DeploymentStatus.Queued,
        _ => DeploymentStatus.Pending,
    };

    private static string? BranchFromRef(string? reference) =>
        reference is not null && reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    private static string? FirstLine(string? text) => text?.Split('\n', 2)[0].Trim();

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime()
            : null;
}
