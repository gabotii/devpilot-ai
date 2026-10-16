using DevPilot.Application.Admin;
using DevPilot.Application.Common;
using DevPilot.Application.Integrations;
using DevPilot.Domain.Integrations;
using DevPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.Workers;

public sealed class WorkerOptions
{
    public const string SectionName = "Workers";

    public bool Enabled { get; set; } = true;
    public int WebhookRetryIntervalSeconds { get; set; } = 15;
    public int RetentionIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Retries failed or stuck webhook deliveries with backoff. Rows are claimed with FOR UPDATE SKIP LOCKED so several API
/// instances can run this worker without processing the same delivery twice.
/// </summary>
public sealed partial class WebhookRetryWorker(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock,
    ILogger<WebhookRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.WebhookRetryIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailure(logger, ex);
            }

            await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var processed = 0;
        for (var i = 0; i < 50; i++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var stuckBefore = now.AddMinutes(-2);
            var delivery = await db.WebhookDeliveries
                .FromSql($"""
                    SELECT * FROM webhook_deliveries
                    WHERE (status = 'Failed' AND next_attempt_at IS NOT NULL AND next_attempt_at <= {now})
                       OR (status = 'Received' AND received_at <= {stuckBefore})
                    ORDER BY received_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(ct);
            if (delivery is null)
            {
                return processed;
            }

            var integration = await db.Integrations.IgnoreQueryFilters().FirstAsync(x => x.Id == delivery.IntegrationId, ct);
            scope.ServiceProvider.GetRequiredService<ITenantContext>().EnterSystemScope(delivery.OrganizationId);
            if (integration.Status != IntegrationStatus.Active)
            {
                delivery.MarkIgnored("integration_disabled", now);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                await scope.ServiceProvider.GetRequiredService<WebhookReceiver>().ReprocessAsync(integration, delivery, ct);
            }

            await tx.CommitAsync(ct);
            processed++;
        }

        return processed;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook retry iteration failed")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}

public sealed partial class RetentionWorker(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<RetentionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so startup (and migrations) settle first.
        await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var interval = TimeSpan.FromMinutes(Math.Max(5, options.Value.RetentionIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailure(logger, ex);
            }

            await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention run failed")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}
