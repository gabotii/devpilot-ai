using System.Security.Cryptography;
using System.Text;
using DevPilot.Application.Common;
using DevPilot.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Integrations;

public enum WebhookOutcome
{
    Accepted,
    Duplicate,
    NotFound,
    InvalidSignature,
    BadRequest,
}

public sealed record WebhookResult(WebhookOutcome Outcome, DeliveryStatus? Status = null, string? Detail = null);

/// <summary>
/// Receives GitHub webhooks: verifies the HMAC-SHA256 signature over the raw body, stores the delivery once (the GitHub
/// delivery id is unique per integration, so retries are no-ops), then processes it. Processing failures are stored and
/// retried by <c>WebhookRetryWorker</c>; the endpoint still acknowledges receipt so GitHub does not hammer us.
/// </summary>
public sealed partial class WebhookReceiver(
    IAppDbContext db,
    ITenantContext tenant,
    ISecretProtector protector,
    GitHubEventProcessor processor,
    TimeProvider clock,
    ILogger<WebhookReceiver> logger)
{
    public const int MaxPayloadBytes = 5 * 1024 * 1024;

    public async Task<WebhookResult> ReceiveGitHubAsync(Guid integrationId, string? eventType, string? deliveryId, string? signature, byte[] body,
        CancellationToken ct)
    {
        var integration = await db.Integrations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == integrationId && i.Provider == IntegrationProvider.GitHubWebhook && i.Status == IntegrationStatus.Active, ct);
        if (integration?.ProtectedSecret is null)
        {
            return new WebhookResult(WebhookOutcome.NotFound);
        }

        if (!VerifySignature(protector.Unprotect(integration.ProtectedSecret), body, signature))
        {
            integration.RecordError("Invalid webhook signature (check the secret configured in GitHub).", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            LogInvalidSignature(logger, integrationId);
            return new WebhookResult(WebhookOutcome.InvalidSignature);
        }

        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 100 || eventType.Length > 60)
        {
            return new WebhookResult(WebhookOutcome.BadRequest, Detail: "Missing X-GitHub-Event or X-GitHub-Delivery header.");
        }

        tenant.EnterSystemScope(integration.OrganizationId);
        return await StoreAndProcessAsync(integration, eventType, deliveryId, body, ct);
    }

    /// <summary>Re-runs processing for a stored delivery (manual retry or background retry worker).</summary>
    public async Task ReprocessAsync(Integration integration, WebhookDelivery delivery, CancellationToken ct)
    {
        await ProcessSafelyAsync(integration, delivery, ct);
        await db.SaveChangesAsync(ct);
    }

    public static bool VerifySignature(string secret, byte[] body, string? signatureHeader)
    {
        const string prefix = "sha256=";
        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHeader.AsSpan(prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private async Task<WebhookResult> StoreAndProcessAsync(Integration integration, string eventType, string deliveryId, byte[] body, CancellationToken ct)
    {
        if (await db.WebhookDeliveries.AnyAsync(d => d.IntegrationId == integration.Id && d.DeliveryId == deliveryId, ct))
        {
            return new WebhookResult(WebhookOutcome.Duplicate);
        }

        string payload;
        string? action;
        try
        {
            payload = Encoding.UTF8.GetString(body);
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            action = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("action", out var a)
                     && a.ValueKind == System.Text.Json.JsonValueKind.String
                ? a.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return new WebhookResult(WebhookOutcome.BadRequest, Detail: "Body is not valid JSON.");
        }

        var now = clock.GetUtcNow();
        var delivery = new WebhookDelivery(integration.OrganizationId, integration.Id, deliveryId, eventType, action is { Length: > 60 } ? action[..60] : action,
            payload, Convert.ToHexStringLower(SHA256.HashData(body)), now);
        db.WebhookDeliveries.Add(delivery);
        integration.RecordEvent(now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ix_webhook_deliveries_integration_id_delivery_id", StringComparison.Ordinal) == true)
        {
            return new WebhookResult(WebhookOutcome.Duplicate); // concurrent retry of the same delivery won the race
        }

        await ProcessSafelyAsync(integration, delivery, ct);
        await db.SaveChangesAsync(ct);
        return new WebhookResult(WebhookOutcome.Accepted, delivery.Status, delivery.Error);
    }

    private async Task ProcessSafelyAsync(Integration integration, WebhookDelivery delivery, CancellationToken ct)
    {
        try
        {
            await processor.ProcessAsync(integration, delivery, ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Malformed or unexpected payload shape: keep the delivery for inspection and retry; never crash the endpoint.
            DiscardPendingChanges(delivery, integration);
            delivery.MarkFailed($"{ex.GetType().Name}: {ex.Message}", clock.GetUtcNow());
            integration.RecordError($"Delivery {delivery.DeliveryId} ({delivery.EventType}) failed: {ex.Message}", clock.GetUtcNow());
            LogProcessingFailed(logger, ex, delivery.DeliveryId, delivery.EventType);
        }
    }

    /// <summary>Drops entities added by a failed processing attempt so a partial result is never committed.</summary>
    private void DiscardPendingChanges(WebhookDelivery delivery, Integration integration)
    {
        if (db is DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity != delivery && e.Entity != integration).ToList())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Reload();
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected webhook with invalid signature for integration {IntegrationId}")]
    private static partial void LogInvalidSignature(ILogger logger, Guid integrationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing of delivery {DeliveryId} ({EventType}) failed")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string deliveryId, string eventType);
}
