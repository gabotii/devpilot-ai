using DevPilot.Application.Admin;
using DevPilot.Application.Ai;
using DevPilot.Application.Common;
using DevPilot.Application.Integrations;
using DevPilot.Infrastructure.Ai;
using DevPilot.Infrastructure.Persistence;
using DevPilot.Infrastructure.Security;
using DevPilot.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddScoped<IWorkItemNumberAllocator, WorkItemNumberAllocator>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.SectionName));
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.Configure<IntegrationOptions>(configuration.GetSection(IntegrationOptions.SectionName));
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));

        services.Configure<AiServiceOptions>(configuration.GetSection(AiServiceOptions.SectionName));
        services.AddHttpClient<IAiServiceClient, AiServiceClient>((sp, http) =>
        {
            var ai = sp.GetRequiredService<IOptions<AiServiceOptions>>().Value;
            http.BaseAddress = new Uri(ai.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(ai.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(ai.ApiKey))
            {
                http.DefaultRequestHeaders.Add("X-Internal-Api-Key", ai.ApiKey);
            }
        });

        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        if (configuration.GetValue("Workers:Enabled", true))
        {
            services.AddHostedService<WebhookRetryWorker>();
            services.AddHostedService<RetentionWorker>();
        }

        return services;
    }
}
