using System.Net;
using System.Text.Json;
using DevPilot.IntegrationTests.Infrastructure;
using DevPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevPilot.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class IntegrationsTests(ApiFactory factory)
{
    private sealed record Setup(ApiClient Owner, Guid OrgId, Guid ProjectId, Guid IntegrationId, string Secret, HttpClient Anonymous);

    private async Task<Setup> SetupAsync(bool authorizeRepo = true)
    {
        var owner = await factory.NewUserAsync("owner");
        var orgId = await owner.CreateOrgAsync();
        var projectId = (await owner.CreateProjectAsync(orgId)).GetProperty("id").GetGuid();
        var created = await owner.PostJsonAsync($"/api/orgs/{orgId}/integrations/github", new { name = "GitHub acme" }, HttpStatusCode.Created);
        var integrationId = created.GetProperty("integration").GetProperty("id").GetGuid();
        var secret = created.GetProperty("secret").GetString()!;
        Assert.Equal($"https://devpilot.test/api/webhooks/github/{integrationId}", created.GetProperty("webhookUrl").GetString());

        var anonymous = factory.CreateClient();
        var ping = await anonymous.SendAsync(GitHub.Webhook(integrationId, secret, "ping", new { zen = "Keep it simple", repository = GitHub.Repo() }));
        Assert.Equal(HttpStatusCode.Accepted, ping.StatusCode);

        if (authorizeRepo)
        {
            var repos = await owner.GetJsonAsync($"/api/orgs/{orgId}/repositories");
            var repoId = repos[0].GetProperty("id").GetGuid();
            await owner.PutJsonAsync($"/api/orgs/{orgId}/repositories/{repoId}", new { status = "Authorized", projectId });
        }

        return new Setup(owner, orgId, projectId, integrationId, secret, anonymous);
    }

    [Fact]
    public async Task Ping_verifies_integration_and_registers_pending_repository()
    {
        var s = await SetupAsync(authorizeRepo: false);
        var integrations = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/integrations");
        var integration = integrations[0];
        Assert.NotEqual(JsonValueKind.Null, integration.GetProperty("verifiedAt").ValueKind);
        Assert.Equal(1, integration.GetProperty("eventCount").GetInt64());
        Assert.False(integration.TryGetProperty("secret", out _), "the secret must never be returned after creation");

        var repos = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/repositories?status=Pending");
        Assert.Equal("acme/checkout", Assert.Single(repos.EnumerateArray()).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_and_recorded()
    {
        var s = await SetupAsync();
        var bad = await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, "wrong-secret", "push", new { repository = GitHub.Repo() }));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var missing = await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, "push", new { }, signatureOverride: ""));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var unknown = await s.Anonymous.SendAsync(GitHub.Webhook(Guid.NewGuid(), s.Secret, "ping", new { }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var integration = (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/integrations"))[0];
        Assert.Contains("signature", integration.GetProperty("lastError").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_deliveries_are_idempotent()
    {
        var s = await SetupAsync();
        var deliveryId = Guid.NewGuid().ToString();
        var payload = GitHub.WorkflowRun(1001, "completed", "failure");
        Assert.Equal(HttpStatusCode.Accepted, (await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, "workflow_run", payload, deliveryId))).StatusCode);
        var again = await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, "workflow_run", payload, deliveryId));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Duplicate", (await again.ReadJsonAsync()).GetProperty("status").GetString());

        var runs = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/pipeline-runs");
        Assert.Equal(1, runs.GetProperty("totalCount").GetInt32());
        var incidents = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/incidents?open=true");
        Assert.Equal(1, incidents.GetProperty("items")[0].GetProperty("occurrenceCount").GetInt32());
    }

    [Fact]
    public async Task Failed_run_on_default_branch_opens_incident_alerts_and_recovers()
    {
        var s = await SetupAsync();
        await Send(s, "workflow_run", GitHub.WorkflowRun(2001, "in_progress", null, updatedAt: "2026-10-11T10:01:00Z"));
        await Send(s, "workflow_run", GitHub.WorkflowRun(2001, "completed", "failure", updatedAt: "2026-10-11T10:05:00Z"));
        // Late, out-of-order "in_progress" must not regress the completed run.
        await Send(s, "workflow_run", GitHub.WorkflowRun(2001, "in_progress", null, updatedAt: "2026-10-11T10:02:00Z"));

        var run = (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/pipeline-runs")).GetProperty("items")[0];
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal("Failure", run.GetProperty("conclusion").GetString());
        Assert.Equal(290, run.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(s.ProjectId, run.GetProperty("projectId").GetGuid());

        var incident = (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/incidents?open=true")).GetProperty("items")[0];
        Assert.Equal("PipelineFailure", incident.GetProperty("source").GetString());
        Assert.Equal("INC-1", incident.GetProperty("key").GetString());
        var notifications = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/notifications");
        var alert = Assert.Single(notifications.GetProperty("page").GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "pipeline.failed");
        Assert.StartsWith("/incidents/", alert.GetProperty("link").GetString(), StringComparison.Ordinal);

        // Feature-branch failures are recorded but do not open incidents.
        await Send(s, "workflow_run", GitHub.WorkflowRun(2002, "completed", "failure", branch: "feature/x"));
        Assert.Equal(1, (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/incidents")).GetProperty("totalCount").GetInt32());

        // A later green run on main mitigates the incident automatically (a human resolves it).
        await Send(s, "workflow_run", GitHub.WorkflowRun(2003, "completed", "success"));
        var detail = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/incidents/{incident.GetProperty("id").GetGuid()}");
        Assert.Equal("Mitigated", detail.GetProperty("incident").GetProperty("status").GetString());
        Assert.Contains(detail.GetProperty("updates").EnumerateArray(), u => u.GetProperty("body").GetString()!.Contains("volvió a pasar", StringComparison.Ordinal));

        var summary = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/operations/summary");
        Assert.Equal(3, summary.GetProperty("runs7d").GetInt32());
        Assert.Equal(2, summary.GetProperty("failedRuns7d").GetInt32());
        Assert.True(summary.GetProperty("hasOperationalData").GetBoolean());
    }

    [Fact]
    public async Task Production_deployment_failure_opens_critical_incident()
    {
        var s = await SetupAsync();
        await Send(s, "deployment_status", GitHub.DeploymentStatus(9001, "in_progress", at: "2026-10-11T11:00:00Z"));
        await Send(s, "deployment_status", GitHub.DeploymentStatus(9001, "failure", at: "2026-10-11T11:03:00Z"));

        var deployment = (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/deployments?environment=production")).GetProperty("items")[0];
        Assert.Equal("Failure", deployment.GetProperty("status").GetString());
        Assert.Equal("https://checkout.example.com/", deployment.GetProperty("url").GetString());
        var incident = (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/incidents?severity=Critical")).GetProperty("items")[0];
        Assert.Equal("DeploymentFailure", incident.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Events_of_unauthorized_repositories_are_ignored()
    {
        var s = await SetupAsync(authorizeRepo: false);
        var response = await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, "workflow_run", GitHub.WorkflowRun(3001, "completed", "failure")));
        Assert.Equal("Ignored", (await response.ReadJsonAsync()).GetProperty("status").GetString());
        Assert.Equal(0, (await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/pipeline-runs")).GetProperty("totalCount").GetInt32());

        var deliveries = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/integrations/{s.IntegrationId}/deliveries?status=Ignored");
        Assert.Equal("repository_pending_authorization", deliveries.GetProperty("items")[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Javascript_urls_from_payloads_are_never_stored()
    {
        var s = await SetupAsync();
        var payload = new
        {
            action = "opened",
            pull_request = new { number = 7, title = "Add feature", html_url = "javascript:alert(1)", state = "open", updated_at = "2026-10-11T10:00:00Z", user = new { login = "mallory" }, head = new { @ref = "feat" } },
            repository = GitHub.Repo(),
        };
        await Send(s, "pull_request", payload);
        var events = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/code-events");
        var pr = events.GetProperty("items")[0];
        Assert.Equal("#7 Add feature", pr.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, pr.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task Integrations_are_admin_only_and_tenant_isolated()
    {
        var s = await SetupAsync();
        var other = await factory.NewUserAsync("other");
        var otherOrg = await other.CreateOrgAsync("Other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/orgs/{otherOrg}/integrations/{s.IntegrationId}/deliveries")).StatusCode);
        Assert.Equal(0, (await other.GetJsonAsync($"/api/orgs/{otherOrg}/repositories")).GetArrayLength());

        var dev = await factory.NewUserAsync("dev");
        var invite = await s.Owner.PostJsonAsync($"/api/orgs/{s.OrgId}/invitations", new { email = dev.Email, role = "Developer" }, HttpStatusCode.Created);
        await dev.PostJsonAsync("/api/invitations/accept", new { token = invite.GetProperty("acceptToken").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.GetAsync($"/api/orgs/{s.OrgId}/integrations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await dev.GetAsync($"/api/orgs/{s.OrgId}/pipeline-runs")).StatusCode);
    }

    [Fact]
    public async Task Secret_rotation_invalidates_the_old_secret_and_secrets_are_encrypted_at_rest()
    {
        var s = await SetupAsync();
        var rotated = await s.Owner.PostJsonAsync($"/api/orgs/{s.OrgId}/integrations/{s.IntegrationId}/rotate-secret");
        var newSecret = rotated.GetProperty("secret").GetString()!;
        Assert.NotEqual(s.Secret, newSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, "ping", new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, newSecret, "ping", new { }))).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Integrations.IgnoreQueryFilters().Where(i => i.Id == s.IntegrationId).Select(i => i.ProtectedSecret).SingleAsync();
        Assert.DoesNotContain(newSecret, stored, StringComparison.Ordinal);
        Assert.StartsWith("k1:", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_payload_is_recorded_as_failed_and_can_be_retried()
    {
        var s = await SetupAsync();
        var broken = await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, "workflow_run", new { workflow_run = new { status = "completed" }, repository = GitHub.Repo() }));
        Assert.Equal("Failed", (await broken.ReadJsonAsync()).GetProperty("status").GetString());

        var deliveries = await s.Owner.GetJsonAsync($"/api/orgs/{s.OrgId}/integrations/{s.IntegrationId}/deliveries?status=Failed");
        var delivery = deliveries.GetProperty("items")[0];
        Assert.Equal(1, delivery.GetProperty("attempts").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, delivery.GetProperty("nextAttemptAt").ValueKind);
        var retried = await s.Owner.PostJsonAsync($"/api/orgs/{s.OrgId}/integrations/{s.IntegrationId}/deliveries/{delivery.GetProperty("id").GetGuid()}/retry");
        Assert.Equal("Failed", retried.GetProperty("status").GetString()); // still malformed, but the retry path works
    }

    private static async Task Send(Setup s, string eventType, object payload)
    {
        var response = await s.Anonymous.SendAsync(GitHub.Webhook(s.IntegrationId, s.Secret, eventType, payload));
        await response.EnsureStatusAsync(HttpStatusCode.Accepted);
        var status = (await response.ReadJsonAsync()).GetProperty("status").GetString();
        Assert.True(status is "Processed", $"delivery {eventType} ended as {status}: {(await response.ReadJsonAsync())}");
    }
}
