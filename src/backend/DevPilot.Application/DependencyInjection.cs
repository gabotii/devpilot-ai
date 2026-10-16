using DevPilot.Application.Admin;
using DevPilot.Application.Ai;
using DevPilot.Application.Common;
using DevPilot.Application.Incidents;
using DevPilot.Application.Integrations;
using DevPilot.Application.Operations;
using DevPilot.Application.Dashboard;
using DevPilot.Application.Identity;
using DevPilot.Application.Notifications;
using DevPilot.Application.Organizations;
using DevPilot.Application.Projects;
using DevPilot.Application.WorkItems;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DevPilot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, ServiceLifetime.Singleton);
        services.AddScoped<AuditLog>();
        services.AddScoped<Notifier>();
        services.AddScoped<AuthService>();
        services.AddScoped<OrganizationAccess>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectSettingsService>();
        services.AddScoped<WorkItemService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<IncidentService>();
        services.AddScoped<OperationsRecorder>();
        services.AddScoped<OperationsQueryService>();
        services.AddScoped<IngestService>();
        services.AddScoped<GitHubEventProcessor>();
        services.AddScoped<WebhookReceiver>();
        services.AddScoped<IntegrationService>();
        services.AddScoped<AiAnalysisService>();
        services.AddScoped<AdminService>();
        services.AddScoped<RetentionService>();
        return services;
    }
}
