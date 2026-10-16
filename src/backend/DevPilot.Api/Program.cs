using System.Text.Json.Serialization;
using DevPilot.Api.Endpoints;
using DevPilot.Api.Infrastructure;
using DevPilot.Api.Security;
using DevPilot.Application;
using DevPilot.Application.Common;
using DevPilot.Application.Identity;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Logging: structured JSON outside Development; never log request bodies (they may contain credentials).
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
}
else
{
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<SessionCookieOptions>(builder.Configuration.GetSection(SessionCookieOptions.SectionName));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddApiForwardedHeaders(builder.Configuration);
builder.Services.AddApiObservability(builder.Configuration);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    // Same-origin deployment (SPA behind the same host) needs no CORS. Origins are opt-in per environment.
    if (allowedOrigins.Length > 0)
    {
        p.WithOrigins(allowedOrigins).AllowCredentials().WithMethods("GET", "POST", "PUT", "DELETE").WithHeaders("Content-Type", SessionCookieOptions.CsrfHeaderName);
    }
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres", tags: ["ready"]);

builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "DevPilot AI API";
    doc.Info.Description = "Organizations, projects, work items, integrations and AI-assisted incident analysis. "
        + "Authentication uses an HttpOnly session cookie; unsafe requests must send the X-CSRF-Token header.";
    return Task.CompletedTask;
}));

var app = builder.Build();

if (args.Contains("--migrate"))
{
    return await app.RunMigrationsAsync();
}

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.RunMigrationsAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseApiSecurityHeaders();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<CsrfMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status200OK },
});

app.MapAuthEndpoints();
app.MapExternalEndpoints();
var org = app.MapOrganizationScope();
app.MapOrganizationEndpoints(org);
org.MapProjectEndpoints();
org.MapOperationsEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
