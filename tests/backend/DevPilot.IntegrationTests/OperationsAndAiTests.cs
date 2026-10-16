using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevPilot.Application.Admin;
using DevPilot.Application.Ai;
using DevPilot.Domain.Operations;
using DevPilot.Infrastructure.Persistence;
using DevPilot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevPilot.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class OperationsAndAiTests(ApiFactory factory)
{
    private async Task<(ApiClient Owner, Guid OrgId, Guid ProjectId)> OrgAsync()
    {
        var owner = await factory.NewUserAsync("owner");
        var orgId = await owner.CreateOrgAsync();
        var projectId = (await owner.CreateProjectAsync(orgId)).GetProperty("id").GetGuid();
        return (owner, orgId, projectId);
    }

    private static async Task<string> KeyAsync(ApiClient owner, Guid orgId, Guid? projectId, string scopes = "Logs, Deployments, Pipelines")
    {
        var created = await owner.PostJsonAsync($"/api/orgs/{orgId}/ingest-keys", new { name = "ci", projectId, scopes }, HttpStatusCode.Created);
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("dpk_", secret, StringComparison.Ordinal);
        return secret;
    }

    private HttpClient Ingest(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public async Task Log_ingestion_groups_errors_and_critical_opens_incident()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        var client = Ingest(await KeyAsync(owner, orgId, projectId));
        var events = Enumerable.Range(1, 3).Select(i => new
        {
            timestamp = DateTimeOffset.UtcNow.AddMinutes(-i),
            level = "Error",
            message = $"Order {1000 + i} failed: timeout calling payments",
            source = "orders-api",
            environment = "production",
            attributes = new Dictionary<string, string> { ["orderId"] = $"{1000 + i}", ["apiToken"] = "should-not-be-stored" },
        }).ToList<object>();
        events.Add(new { timestamp = DateTimeOffset.UtcNow, level = "Critical", message = "Database pool exhausted", source = "orders-api", environment = "production", attributes = (Dictionary<string, string>?)null });

        var response = await client.PostAsJsonAsync("/api/ingest/logs", new { events });
        await response.EnsureStatusAsync(HttpStatusCode.Accepted);
        Assert.Equal(4, (await response.ReadJsonAsync()).GetProperty("accepted").GetInt32());

        var groups = await owner.GetJsonAsync($"/api/orgs/{orgId}/logs/groups");
        var timeoutGroup = groups.EnumerateArray().First(g => g.GetProperty("sampleMessage").GetString()!.Contains("timeout", StringComparison.Ordinal));
        Assert.Equal(3, timeoutGroup.GetProperty("count").GetInt32());

        var logs = await owner.GetJsonAsync($"/api/orgs/{orgId}/logs?minLevel=Error&q=payments");
        Assert.Equal(3, logs.GetProperty("totalCount").GetInt32());

        var incidents = await owner.GetJsonAsync($"/api/orgs/{orgId}/incidents?open=true");
        Assert.Equal("ErrorGroup", incidents.GetProperty("items")[0].GetProperty("source").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.LogEvents.IgnoreQueryFilters().Where(l => l.OrganizationId == orgId && l.Source == "orders-api").ToListAsync();
        Assert.All(stored.Where(l => l.Attributes.ContainsKey("apiToken")), l => Assert.Equal("[redacted]", l.Attributes["apiToken"]));
    }

    [Fact]
    public async Task Ingestion_keys_enforce_scope_revocation_and_validation()
    {
        var (owner, orgId, _) = await OrgAsync();
        var logsOnly = await KeyAsync(owner, orgId, null, "Logs");
        var client = Ingest(logsOnly);
        var deploy = await client.PostAsJsonAsync("/api/ingest/deployments", new { externalId = "d1", environment = "staging", status = "success" });
        Assert.Equal(HttpStatusCode.Unauthorized, deploy.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/ingest/logs", new { events = new[] { new { level = "Error", message = "x" } } })).StatusCode);

        var invalid = await client.PostAsJsonAsync("/api/ingest/logs", new { events = new[] { new { level = "Loud", message = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var keys = await owner.GetJsonAsync($"/api/orgs/{orgId}/ingest-keys");
        await (await owner.DeleteAsync($"/api/orgs/{orgId}/ingest-keys/{keys[0].GetProperty("id").GetGuid()}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        var revoked = await client.PostAsJsonAsync("/api/ingest/logs", new { events = new[] { new { level = "Error", message = "x" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task Generic_ci_can_report_runs_and_deployments_idempotently()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        var client = Ingest(await KeyAsync(owner, orgId, projectId));
        object Run(string status, string? conclusion) => new
        {
            pipelineKey = "jenkins-main",
            pipelineName = "Jenkins main",
            runId = "77",
            branch = "main",
            defaultBranch = "main",
            status,
            conclusion,
            startedAt = "2026-10-11T09:00:00Z",
            completedAt = status == "completed" ? "2026-10-11T09:10:00Z" : null,
        };
        await (await client.PostAsJsonAsync("/api/ingest/pipeline-runs", Run("in_progress", null))).EnsureStatusAsync(HttpStatusCode.Accepted);
        await (await client.PostAsJsonAsync("/api/ingest/pipeline-runs", Run("completed", "failure"))).EnsureStatusAsync(HttpStatusCode.Accepted);
        await (await client.PostAsJsonAsync("/api/ingest/pipeline-runs", Run("completed", "failure"))).EnsureStatusAsync(HttpStatusCode.Accepted);

        var runs = await owner.GetJsonAsync($"/api/orgs/{orgId}/pipeline-runs?projectId={projectId}");
        Assert.Equal(1, runs.GetProperty("totalCount").GetInt32());
        Assert.Equal(600, runs.GetProperty("items")[0].GetProperty("durationSeconds").GetDouble());
        Assert.Equal(1, (await owner.GetJsonAsync($"/api/orgs/{orgId}/incidents")).GetProperty("totalCount").GetInt32());

        await (await client.PostAsJsonAsync("/api/ingest/deployments", new { externalId = "rel-1", environment = "production", status = "in_progress" })).EnsureStatusAsync(HttpStatusCode.Accepted);
        await (await client.PostAsJsonAsync("/api/ingest/deployments", new { externalId = "rel-1", environment = "production", status = "success", url = "javascript:alert(1)" })).EnsureStatusAsync(HttpStatusCode.Accepted);
        var deployments = await owner.GetJsonAsync($"/api/orgs/{orgId}/deployments");
        Assert.Equal(1, deployments.GetProperty("totalCount").GetInt32());
        var deployment = deployments.GetProperty("items")[0];
        Assert.Equal("Success", deployment.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, deployment.GetProperty("url").ValueKind);
        Assert.Contains("production", (await owner.GetJsonAsync($"/api/orgs/{orgId}/deployments/environments")).EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Incident_lifecycle_with_notes_versioning_and_permissions()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        var created = await owner.PostJsonAsync($"/api/orgs/{orgId}/incidents",
            new { title = "Checkout latency", severity = "High", projectId, description = "p95 > 3s", logExcerpt = "ERROR timeout" }, HttpStatusCode.Created);
        var id = created.GetProperty("incident").GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetUInt32();

        await owner.PostJsonAsync($"/api/orgs/{orgId}/incidents/{id}/notes", new { body = "Rolled back gateway config" });
        var updated = await owner.PutJsonAsync($"/api/orgs/{orgId}/incidents/{id}",
            new { title = "Checkout latency", description = "p95 > 3s", severity = "High", status = "Resolved", assigneeId = owner.UserId, version });
        Assert.Equal("Resolved", updated.GetProperty("incident").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, updated.GetProperty("incident").GetProperty("resolvedAt").ValueKind);
        Assert.Contains(updated.GetProperty("updates").EnumerateArray(), u => u.GetProperty("kind").GetString() == "StatusChange");

        var stale = await owner.PutAsync($"/api/orgs/{orgId}/incidents/{id}",
            new { title = "x", severity = "Low", status = "Open", version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var viewer = await factory.NewUserAsync("viewer");
        var invite = await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = viewer.Email, role = "Viewer" }, HttpStatusCode.Created);
        await viewer.PostJsonAsync("/api/invitations/accept", new { token = invite.GetProperty("acceptToken").GetString() });
        Assert.False((await viewer.GetJsonAsync($"/api/orgs/{orgId}/incidents/{id}")).GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/orgs/{orgId}/incidents/{id}/notes", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/orgs/{orgId}/ai/analyses", new { content = "ERROR x" })).StatusCode);
    }

    [Fact]
    public async Task Ai_analysis_is_recorded_and_failures_degrade_gracefully()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        factory.Ai.Failure = null;
        var analysis = await owner.PostJsonAsync($"/api/orgs/{orgId}/ai/analyses",
            new { content = "ERROR connection refused\nERROR connection refused", kind = "pipeline_failure", projectId, title = "CI #12" });
        Assert.Equal("Succeeded", analysis.GetProperty("status").GetString());
        Assert.Equal("network", analysis.GetProperty("result").GetProperty("classification").GetProperty("category").GetString());
        Assert.Equal("PipelineFailure", analysis.GetProperty("kind").GetString());
        var sent = factory.Ai.Last<AiAnalysisRequest>()!;
        Assert.Equal("Payments", sent.Context.ProjectName);

        factory.Ai.Failure = new AiServiceException("provider_rate_limited", "Rate limited", 503, TimeSpan.FromSeconds(30));
        try
        {
            var failed = await owner.PostAsync($"/api/orgs/{orgId}/ai/analyses", new { content = "ERROR boom" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("ai.provider_rate_limited", await failed.ProblemCodeAsync());
            Assert.Equal("30", failed.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The rest of the platform keeps working while AI is down.
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/orgs/{orgId}/dashboard")).StatusCode);
        }
        finally
        {
            factory.Ai.Failure = null;
        }

        var history = await owner.GetJsonAsync($"/api/orgs/{orgId}/ai/analyses");
        Assert.Equal(2, history.GetProperty("totalCount").GetInt32());
        Assert.Contains(history.GetProperty("items").EnumerateArray(), a => a.GetProperty("status").GetString() == "Failed"
            && a.GetProperty("errorCode").GetString() == "provider_rate_limited");
    }

    [Fact]
    public async Task Assistant_only_receives_data_of_the_callers_organization()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        await owner.CreateItemAsync(orgId, projectId, new { type = "Issue", title = "Our visible bug", priority = "High" });

        var other = await factory.NewUserAsync("other");
        var otherOrg = await other.CreateOrgAsync("Secret Org");
        var otherProject = (await other.CreateProjectAsync(otherOrg, "SEC", "Secret project")).GetProperty("id").GetGuid();
        await other.CreateItemAsync(otherOrg, otherProject, new { type = "Task", title = "Confidential roadmap", priority = "High" });

        var answer = await owner.PostJsonAsync($"/api/orgs/{orgId}/ai/assistant", new { question = "¿Qué incidencias hay?" });
        Assert.Equal("Succeeded", answer.GetProperty("status").GetString());
        var request = factory.Ai.Last<AiAssistantRequest>()!;
        Assert.Contains(request.Documents, d => d.Title.Contains("Our visible bug", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Documents, d => d.Title.Contains("Confidential", StringComparison.Ordinal) || d.Content.Contains("Secret", StringComparison.Ordinal));
        Assert.Equal("Acme", request.OrganizationName);
    }

    [Fact]
    public async Task Incident_analysis_and_activity_summary_use_ai_service()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        var incident = await owner.PostJsonAsync($"/api/orgs/{orgId}/incidents", new { title = "Queue backlog", severity = "Medium", projectId }, HttpStatusCode.Created);
        var incidentId = incident.GetProperty("incident").GetProperty("id").GetGuid();
        await owner.PostJsonAsync($"/api/orgs/{orgId}/incidents/{incidentId}/log", new { log = "ERROR consumer lag 120000" });
        var analysis = await owner.PostJsonAsync($"/api/orgs/{orgId}/incidents/{incidentId}/analyze");
        Assert.Equal(incidentId, analysis.GetProperty("incidentId").GetGuid());
        Assert.Contains("consumer lag", factory.Ai.Last<AiAnalysisRequest>()!.Content, StringComparison.Ordinal);
        Assert.Equal(1, (await owner.GetJsonAsync($"/api/orgs/{orgId}/ai/analyses?incidentId={incidentId}")).GetProperty("totalCount").GetInt32());

        var summary = await owner.PostJsonAsync($"/api/orgs/{orgId}/projects/{projectId}/activity-summary?days=7");
        Assert.Equal("ActivitySummary", summary.GetProperty("kind").GetString());
        Assert.Equal("Payments", factory.Ai.Last<AiActivitySummaryRequest>()!.ProjectName);

        var status = await owner.GetJsonAsync($"/api/orgs/{orgId}/ai/status");
        Assert.Equal("deterministic", status.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Admin_audit_and_overview_are_admin_only()
    {
        var (owner, orgId, _) = await OrgAsync();
        var audit = await owner.GetJsonAsync($"/api/orgs/{orgId}/admin/audit?action=project.");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("action").GetString() == "project.created");
        Assert.All(audit.GetProperty("items").EnumerateArray(), a => Assert.StartsWith("project.", a.GetProperty("action").GetString(), StringComparison.Ordinal));

        var overview = await owner.GetJsonAsync($"/api/orgs/{orgId}/admin/overview");
        Assert.Equal(1, overview.GetProperty("members").GetInt32());
        Assert.True(overview.GetProperty("ai").GetProperty("reachable").GetBoolean());

        var dev = await factory.NewUserAsync("dev");
        var invite = await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = dev.Email, role = "Developer" }, HttpStatusCode.Created);
        await dev.PostJsonAsync("/api/invitations/accept", new { token = invite.GetProperty("acceptToken").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.GetAsync($"/api/orgs/{orgId}/admin/audit")).StatusCode);
    }

    [Fact]
    public async Task Retention_removes_only_expired_operational_data()
    {
        var (owner, orgId, projectId) = await OrgAsync();
        var client = Ingest(await KeyAsync(owner, orgId, projectId));
        await (await client.PostAsJsonAsync("/api/ingest/logs", new { events = new[] { new { level = "Error", message = "recent" } } })).EnsureStatusAsync(HttpStatusCode.Accepted);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LogEvents.Add(new LogEvent(orgId, projectId, "old", Domain.Operations.LogLevel.Error, "ancient", null, "f", null,
            DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow.AddDays(-90), []));
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(CancellationToken.None);
        Assert.True(result.LogEvents >= 1);
        var remaining = await db.LogEvents.IgnoreQueryFilters().Where(l => l.OrganizationId == orgId).Select(l => l.Message).ToListAsync();
        Assert.Equal(["recent"], remaining);
        Assert.Equal(1, await db.Projects.IgnoreQueryFilters().CountAsync(p => p.Id == projectId));
    }
}
