using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevPilot.Application.Common;
using DevPilot.Application.Incidents;
using DevPilot.Domain.Incidents;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Operations;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Application.Operations;

public sealed record IngestLogEvent(DateTimeOffset? Timestamp, string Level, string Message, string? Exception, string? Source, string? Environment,
    Dictionary<string, string?>? Attributes);

public sealed record IngestLogBatch(IReadOnlyList<IngestLogEvent> Events);

public sealed record IngestDeploymentRequest(string ExternalId, string Environment, string Status, string? Ref, string? CommitSha, string? Description,
    string? Creator, string? Url, DateTimeOffset? Timestamp);

public sealed record IngestPipelineRunRequest(string PipelineKey, string PipelineName, string RunId, int? Attempt, long? RunNumber, string? Branch,
    string? DefaultBranch, string? CommitSha, string Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Url,
    string? Trigger, string? Actor);

public sealed record IngestResult(int Accepted, IReadOnlyList<string> Warnings);

/// <summary>Raised when the ingestion key is missing, invalid, revoked or lacks the required scope.</summary>
public sealed class IngestAuthException(string message) : Exception(message);

public sealed partial class IngestService(
    IAppDbContext db,
    ITenantContext tenant,
    OperationsRecorder recorder,
    IncidentService incidents,
    TimeProvider clock)
{
    public const int MaxBatch = 500;

    public async Task<IngestKey> AuthenticateAsync(string? authorizationHeader, IngestScopes required, CancellationToken ct)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestAuthException("Missing bearer ingestion key.");
        }

        var raw = authorizationHeader[prefix.Length..].Trim();
        if (!raw.StartsWith("dpk_", StringComparison.Ordinal) || raw.Length > 100)
        {
            throw new IngestAuthException("Invalid ingestion key.");
        }

        var hash = SecureTokens.Hash(raw);
        var key = await db.IngestKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (key is null || !key.IsActive)
        {
            throw new IngestAuthException("Invalid ingestion key.");
        }

        if ((key.Scopes & required) != required)
        {
            throw new IngestAuthException($"The key lacks the '{required}' scope.");
        }

        tenant.EnterSystemScope(key.OrganizationId);
        key.Touch(clock.GetUtcNow());
        return key;
    }

    public async Task<IngestResult> IngestLogsAsync(IngestKey key, IngestLogBatch batch, CancellationToken ct)
    {
        if (batch.Events is null || batch.Events.Count == 0 || batch.Events.Count > MaxBatch)
        {
            throw new ValidationException([new ValidationFailure("events", $"Send between 1 and {MaxBatch} events per batch.")]);
        }

        var now = clock.GetUtcNow();
        var warnings = new List<string>();
        var criticalGroups = new Dictionary<string, LogEvent>();
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var e = batch.Events[i];
            if (string.IsNullOrWhiteSpace(e.Message) || !Enum.TryParse<Domain.Operations.LogLevel>(e.Level, ignoreCase: true, out var level))
            {
                throw new ValidationException([new ValidationFailure($"events[{i}]", "Each event needs a message and a level (Trace..Critical).")]);
            }

            var timestamp = e.Timestamp ?? now;
            if (timestamp > now.AddMinutes(5))
            {
                warnings.Add($"events[{i}]: timestamp in the future was replaced by the receive time.");
                timestamp = now;
            }

            var attributes = (e.Attributes ?? []).Take(20).ToDictionary(a => a.Key.Length > 60 ? a.Key[..60] : a.Key,
                a => a.Value is { Length: > 300 } v ? v[..300] : a.Value);
            var log = new LogEvent(key.OrganizationId, key.ProjectId, e.Source ?? "unknown", level, e.Message, e.Exception,
                Fingerprint(e.Message, e.Exception), e.Environment, timestamp, now, AuditLog.Sanitize(attributes));
            db.LogEvents.Add(log);
            if (level == Domain.Operations.LogLevel.Critical)
            {
                criticalGroups.TryAdd(log.Fingerprint, log);
            }
        }

        // Critical errors raise (or recur) an incident per error group.
        foreach (var (fingerprint, sample) in criticalGroups)
        {
            await incidents.OpenOrRecurAsync(key.OrganizationId, $"log:{fingerprint}", $"Error crítico: {Clip(sample.Message, 120)}", IncidentSeverity.High,
                IncidentSource.ErrorGroup, key.ProjectId, $"Error crítico recibido desde {sample.Source}: {Clip(sample.Message, 400)}", ct);
        }

        await db.SaveChangesAsync(ct);
        return new IngestResult(batch.Events.Count, warnings);
    }

    public async Task<IngestResult> IngestDeploymentAsync(IngestKey key, IngestDeploymentRequest request, CancellationToken ct)
    {
        var errors = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(request.ExternalId) || request.ExternalId.Length > 100)
        {
            errors.Add(new ValidationFailure(nameof(request.ExternalId), "externalId is required (max 100)."));
        }

        if (string.IsNullOrWhiteSpace(request.Environment) || request.Environment.Length > 50)
        {
            errors.Add(new ValidationFailure(nameof(request.Environment), "environment is required (max 50)."));
        }

        var status = ParseDeploymentStatus(request.Status);
        if (status is null)
        {
            errors.Add(new ValidationFailure(nameof(request.Status), "status must be one of pending, queued, in_progress, success, failure, error, inactive."));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var at = request.Timestamp ?? clock.GetUtcNow();
        await recorder.RecordDeploymentAsync(new DeploymentEvent(key.OrganizationId, EventSource.Api, $"api:{key.Id}:{request.ExternalId}",
            request.Environment, request.Ref, request.CommitSha, request.Description, request.Creator, null, key.ProjectId, at, status, request.Url, at), ct);
        await db.SaveChangesAsync(ct);
        return new IngestResult(1, []);
    }

    public async Task<IngestResult> IngestPipelineRunAsync(IngestKey key, IngestPipelineRunRequest request, CancellationToken ct)
    {
        var errors = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(request.PipelineKey) || request.PipelineKey.Length > 100)
        {
            errors.Add(new ValidationFailure(nameof(request.PipelineKey), "pipelineKey is required (max 100)."));
        }

        if (string.IsNullOrWhiteSpace(request.PipelineName))
        {
            errors.Add(new ValidationFailure(nameof(request.PipelineName), "pipelineName is required."));
        }

        if (string.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 100)
        {
            errors.Add(new ValidationFailure(nameof(request.RunId), "runId is required (max 100)."));
        }

        RunStatus? status = request.Status switch
        {
            "queued" => RunStatus.Queued,
            "in_progress" => RunStatus.InProgress,
            "completed" => RunStatus.Completed,
            _ => null,
        };
        if (status is null)
        {
            errors.Add(new ValidationFailure(nameof(request.Status), "status must be queued, in_progress or completed."));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var now = clock.GetUtcNow();
        var conclusion = status == RunStatus.Completed ? Integrations.GitHubEventProcessor.ParseConclusion(request.Conclusion) : (RunConclusion?)null;
        await recorder.RecordRunAsync(new RunEvent(key.OrganizationId, EventSource.Api, $"api:{key.Id}:{request.PipelineKey}", request.PipelineName, null, null,
            key.ProjectId, request.DefaultBranch, request.RunId, request.Attempt ?? 1, request.RunNumber, request.Branch, request.CommitSha, request.Trigger,
            request.Actor, request.StartedAt ?? now,
            new PipelineRunUpdate(status!.Value, conclusion, request.StartedAt, request.CompletedAt, request.CompletedAt ?? request.StartedAt ?? now, request.Url)), ct);
        await db.SaveChangesAsync(ct);
        return new IngestResult(1, []);
    }

    /// <summary>Groups recurring errors: normalized message (numbers, ids, hex removed) + exception type.</summary>
    public static string Fingerprint(string message, string? exception)
    {
        var normalized = Volatile().Replace(message.Trim().ToLowerInvariant(), "#");
        var exceptionType = exception is null ? string.Empty : ExceptionType().Match(exception).Value;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{exceptionType}|{(normalized.Length > 300 ? normalized[..300] : normalized)}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static DeploymentStatus? ParseDeploymentStatus(string? value) => value switch
    {
        "pending" => DeploymentStatus.Pending,
        "queued" => DeploymentStatus.Queued,
        "in_progress" => DeploymentStatus.InProgress,
        "success" => DeploymentStatus.Success,
        "failure" => DeploymentStatus.Failure,
        "error" => DeploymentStatus.Error,
        "inactive" => DeploymentStatus.Inactive,
        _ => null,
    };

    private static string Clip(string value, int max) => value.Length > max ? value[..(max - 1)] + "…" : value;

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|0x[0-9a-f]+|\b[0-9a-f]{12,}\b|\d+")]
    private static partial Regex Volatile();

    [GeneratedRegex(@"[A-Za-z_][\w.]*(Exception|Error)")]
    private static partial Regex ExceptionType();
}
