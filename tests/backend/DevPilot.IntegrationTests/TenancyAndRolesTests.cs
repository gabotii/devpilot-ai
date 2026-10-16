using System.Net;
using DevPilot.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DevPilot.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class TenancyAndRolesTests(ApiFactory factory)
{
    /// <summary>Creates an org owned by "owner" and adds "member" with the given role via a real invitation.</summary>
    private async Task<(ApiClient Owner, ApiClient Member, Guid OrgId)> OrgWithMemberAsync(string role)
    {
        var owner = await factory.NewUserAsync("owner");
        var orgId = await owner.CreateOrgAsync();
        var member = await factory.NewUserAsync("member");
        var invitation = await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = member.Email, role }, HttpStatusCode.Created);
        Assert.False(invitation.GetProperty("emailSent").GetBoolean());
        await member.PostJsonAsync("/api/invitations/accept", new { token = invitation.GetProperty("acceptToken").GetString() });
        return (owner, member, orgId);
    }

    [Fact]
    public async Task Other_tenants_resources_are_invisible()
    {
        var alice = await factory.NewUserAsync("alice");
        var aliceOrg = await alice.CreateOrgAsync("Alice Org");
        var project = await alice.CreateProjectAsync(aliceOrg);
        var projectId = project.GetProperty("id").GetGuid();
        await alice.CreateItemAsync(aliceOrg, projectId, new { type = "Task", title = "Secret task", priority = "Medium" });

        var bob = await factory.NewUserAsync("bob");
        var bobOrg = await bob.CreateOrgAsync("Bob Org");

        // Direct access to the other org: 404 (existence not revealed).
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/orgs/{aliceOrg}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/orgs/{aliceOrg}/projects")).StatusCode);

        // Alice's project id under Bob's own org route: the tenant filter hides it.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/orgs/{bobOrg}/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/orgs/{bobOrg}/projects/{projectId}/items/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/orgs/{bobOrg}/projects/{projectId}/items",
            new { type = "Task", title = "Injected", priority = "Low" })).StatusCode);

        var bobSearch = await bob.GetJsonAsync($"/api/orgs/{bobOrg}/work-items?q=Secret");
        Assert.Equal(0, bobSearch.GetProperty("totalCount").GetInt32());
        var bobDashboard = await bob.GetJsonAsync($"/api/orgs/{bobOrg}/dashboard");
        Assert.Equal(0, bobDashboard.GetProperty("work").GetProperty("openItems").GetInt32());
        Assert.Equal(0, bobDashboard.GetProperty("recentActivity").EnumerateArray().Count(a => a.GetProperty("summary").GetString()!.Contains("Secret", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Viewer_is_read_only()
    {
        var (owner, viewer, orgId) = await OrgWithMemberAsync("Viewer");
        var project = await owner.CreateProjectAsync(orgId);
        var projectId = project.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/orgs/{orgId}/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/orgs/{orgId}/projects", new { name = "Nope", key = "NOPE" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/orgs/{orgId}/projects/{projectId}/items",
            new { type = "Task", title = "x", priority = "Low" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/orgs/{orgId}/invitations", new { email = "x@example.com", role = "Viewer" })).StatusCode);
    }

    [Fact]
    public async Task Developer_needs_project_membership_to_edit_items()
    {
        var (owner, dev, orgId) = await OrgWithMemberAsync("Developer");
        var projectId = (await owner.CreateProjectAsync(orgId)).GetProperty("id").GetGuid();
        var item = new { type = "Task", title = "Build checkout", priority = "Medium" };

        var forbidden = await dev.PostAsync($"/api/orgs/{orgId}/projects/{projectId}/items", item);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // A developer cannot add themselves (not a lead).
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PostAsync($"/api/orgs/{orgId}/projects/{projectId}/members", new { userId = dev.UserId, role = "Member" })).StatusCode);

        await owner.PostJsonAsync($"/api/orgs/{orgId}/projects/{projectId}/members", new { userId = dev.UserId, role = "Member" });
        var created = await dev.CreateItemAsync(orgId, projectId, item);
        Assert.Equal("PAY-1", created.GetProperty("item").GetProperty("key").GetString());
        Assert.True(created.GetProperty("permissions").GetProperty("canEditItems").GetBoolean());
        Assert.False(created.GetProperty("permissions").GetProperty("canManage").GetBoolean());
    }

    [Fact]
    public async Task Developer_can_create_project_and_becomes_lead()
    {
        var (_, dev, orgId) = await OrgWithMemberAsync("Developer");
        var project = await dev.CreateProjectAsync(orgId, "DEV", "Dev project");
        var member = Assert.Single(project.GetProperty("members").EnumerateArray());
        Assert.Equal("Lead", member.GetProperty("role").GetString());
        Assert.True(project.GetProperty("permissions").GetProperty("canManage").GetBoolean());
        Assert.False(project.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
    }

    [Fact]
    public async Task Admin_role_management_rules_are_enforced()
    {
        var (owner, admin, orgId) = await OrgWithMemberAsync("Admin");

        // Admins cannot demote the owner, nor promote anyone to Owner/Admin.
        var demoteOwner = await admin.PutAsync($"/api/orgs/{orgId}/members/{owner.UserId}/role", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, demoteOwner.StatusCode);

        var dev = await factory.NewUserAsync("dev");
        var invite = await admin.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = dev.Email, role = "Developer" }, HttpStatusCode.Created);
        await dev.PostJsonAsync("/api/invitations/accept", new { token = invite.GetProperty("acceptToken").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsync($"/api/orgs/{orgId}/members/{dev.UserId}/role", new { role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/orgs/{orgId}/invitations", new { email = "a@example.com", role = "Admin" })).StatusCode);

        // But they can manage developers.
        var changed = await admin.PutJsonAsync($"/api/orgs/{orgId}/members/{dev.UserId}/role", new { role = "Viewer" });
        Assert.Equal("Viewer", changed.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Last_owner_cannot_leave_but_members_can()
    {
        var (owner, member, orgId) = await OrgWithMemberAsync("Developer");
        var ownerLeaves = await owner.DeleteAsync($"/api/orgs/{orgId}/members/{owner.UserId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ownerLeaves.StatusCode);
        Assert.Equal("org.last_owner", await ownerLeaves.ProblemCodeAsync());

        await (await member.DeleteAsync($"/api/orgs/{orgId}/members/{member.UserId}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/orgs/{orgId}")).StatusCode);
    }

    [Fact]
    public async Task Invitation_must_match_email_and_can_be_revoked()
    {
        var owner = await factory.NewUserAsync("owner");
        var orgId = await owner.CreateOrgAsync();
        var invite = await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = "someone-else@example.com", role = "Developer" }, HttpStatusCode.Created);
        var token = invite.GetProperty("acceptToken").GetString();

        var stranger = await factory.NewUserAsync("stranger");
        var preview = await stranger.PostJsonAsync("/api/invitations/preview", new { token });
        Assert.False(preview.GetProperty("emailMatchesCurrentUser").GetBoolean());
        var accept = await stranger.PostAsync("/api/invitations/accept", new { token });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, accept.StatusCode);
        Assert.Equal("invitation.email_mismatch", await accept.ProblemCodeAsync());

        var invitationId = invite.GetProperty("invitation").GetProperty("id").GetGuid();
        await (await owner.DeleteAsync($"/api/orgs/{orgId}/invitations/{invitationId}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        var list = await owner.GetJsonAsync($"/api/orgs/{orgId}/invitations");
        Assert.Equal("Revoked", list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invitationId).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pending_invitations_are_listed_for_the_invitee_and_acceptable_by_id()
    {
        var owner = await factory.NewUserAsync("owner");
        var orgId = await owner.CreateOrgAsync("Inviting Org");
        var invitee = await factory.NewUserAsync("invitee");
        await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = invitee.Email, role = "Viewer" }, HttpStatusCode.Created);

        var me = await invitee.GetJsonAsync("/api/auth/me");
        Assert.Equal(1, me.GetProperty("pendingInvitations").GetInt32());
        var mine = await invitee.GetJsonAsync("/api/invitations/mine");
        var id = Assert.Single(mine.EnumerateArray()).GetProperty("id").GetGuid();
        var joined = await invitee.PostJsonAsync($"/api/invitations/{id}/accept");
        Assert.Equal("Viewer", joined.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await invitee.GetAsync($"/api/orgs/{orgId}")).StatusCode);
    }

    [Fact]
    public async Task Audit_trail_records_sensitive_actions_without_secrets()
    {
        var (owner, _, orgId) = await OrgWithMemberAsync("Developer");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevPilot.Infrastructure.Persistence.AppDbContext>();
        var actions = db.AuditEntries.Where(a => a.OrganizationId == orgId).Select(a => a.Action).ToList();
        Assert.Contains("org.created", actions);
        Assert.Contains("org.invitation_created", actions);
        Assert.Contains("org.invitation_accepted", actions);
        Assert.Contains(db.AuditEntries.Where(a => a.ActorUserId == owner.UserId).Select(a => a.Action), a => a == "auth.registered");
    }
}
