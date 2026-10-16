using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DevPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DevPilot.Api.Infrastructure;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Ai = "ai";
    public const string Webhooks = "webhooks";
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int AuthPerMinute { get; set; } = 10;
    public int ApiPerMinute { get; set; } = 600;
    public int AiPerMinute { get; set; } = 20;
    public int WebhooksPerMinute { get; set; } = 600;
}

public static class HostingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await Results.Problem(statusCode: 429, title: "Too Many Requests", detail: "Rate limit exceeded. Try again later.",
                    extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" }).ExecuteAsync(context.HttpContext);
            };

            // Global limiter: per authenticated user, falling back to client IP.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => Window(limits.ApiPerMinute)));
            options.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter($"auth:{ClientIp(http)}", _ => Window(limits.AuthPerMinute)));
            options.AddPolicy(RateLimitPolicies.Ai, http => RateLimitPartition.GetFixedWindowLimiter($"ai:{PartitionKey(http)}", _ => Window(limits.AiPerMinute)));
            options.AddPolicy(RateLimitPolicies.Webhooks, http =>
                RateLimitPartition.GetFixedWindowLimiter($"wh:{http.Request.RouteValues["integrationId"]}", _ => Window(limits.WebhooksPerMinute)));
        });
        return services;

        static FixedWindowRateLimiterOptions Window(int permits) => new()
        {
            PermitLimit = Math.Max(1, permits),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
    }

    public static IServiceCollection AddApiObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("devpilot-api", serviceVersion: typeof(HostingExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    t.AddOtlpExporter();
                }
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentationIfAvailable();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    m.AddOtlpExporter();
                }
            });
        return services;
    }

    private static MeterProviderBuilder AddRuntimeInstrumentationIfAvailable(this MeterProviderBuilder builder) =>
        builder.AddMeter("System.Runtime");

    public static IServiceCollection AddApiForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // Only trust X-Forwarded-* from explicitly configured proxy networks (e.g. the compose network of the web gateway).
            foreach (var cidr in configuration.GetSection("ReverseProxy:TrustedNetworks").Get<string[]>() ?? [])
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });
        return services;
    }

    /// <summary>Conservative headers for a JSON API. The SPA's CSP is set by the web server that serves it.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!context.Request.Path.StartsWithSegments("/scalar"))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });
        await next();
    });

    public static async Task<int> RunMigrationsAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();
        logger.LogInformation("Database is up to date");
        return 0;
    }

    private static string PartitionKey(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId ? $"u:{userId}" : $"ip:{ClientIp(http)}";

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? IPAddress.None.ToString();
}
