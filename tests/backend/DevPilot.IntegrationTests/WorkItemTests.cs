using System.Net;
using System.Text.Json;
using DevPilot.IntegrationTests.Infrastructure;

namespace DevPilot.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class WorkItemTests(ApiFactory factory)
{
    private async Task<(ApiClient Owner, Guid OrgId, JsonElement Project, string Base)> SetupAsync()
    {
        var owner = await factory.NewUserAsync("owner");
        var orgId = await owner.CreateOrgAsync();
        var project = await owner.CreateProjectAsync(orgId);
        return (owner, orgId, project, $"/api/orgs/{orgId}/projects/{project.GetProperty("id").GetGuid()}");
    }

    [Fact]
    public async Task Full_lifecycle_with_history_and_activity()
    {
        var (owner, orgId, project, baseUrl) = await SetupAsync();
        var label = await owner.PostJsonAsync($"{baseUrl}/labels", new { name = "backend", color = "#3B82F6" });
        var created = await owner.PostJsonAsync($"{baseUrl}/items",
            new { type = "Issue", title = "Checkout 500", priority = "High", labelIds = new[] { label.GetProperty("id").GetGuid() }, dueDate = "2026-12-01" },
            HttpStatusCode.Created);
        Assert.Equal("#3b82f6", created.GetProperty("item").GetProperty("labels")[0].GetProperty("color").GetString());

        var version = created.GetProperty("version").GetUInt32();
        var updated = await owner.PutJsonAsync($"{baseUrl}/items/1", new
        {
            title = "Checkout returns 500 on retry",
            description = "Steps to reproduce...",
            priority = "Critical",
            assigneeId = owner.UserId,
            dueDate = (string?)null,
            labelIds = Array.Empty<Guid>(),
            version,
        });
        Assert.Equal("Critical", updated.GetProperty("item").GetProperty("priority").GetString());
        Assert.Equal(owner.UserId, updated.GetProperty("item").GetProperty("assignee").GetProperty("id").GetGuid());

        await owner.PostJsonAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("En curso") });
        var done = await owner.PostJsonAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("Hecho") });
        Assert.Equal("Done", done.GetProperty("item").GetProperty("statusCategory").GetString());
        Assert.True(done.GetProperty("item").TryGetProperty("completedAt", out var completedAt) && completedAt.ValueKind != JsonValueKind.Null);

        var history = await owner.GetJsonAsync($"{baseUrl}/items/1/history");
        var fields = history.EnumerateArray().Select(h => h.GetProperty("field").GetString()).ToList();
        Assert.Contains("created", fields);
        Assert.Contains("title", fields);
        Assert.Contains("priority", fields);
        Assert.Contains("labels", fields);
        Assert.Contains("dueDate", fields);
        Assert.Equal(2, fields.Count(f => f == "status"));

        var dashboard = await owner.GetJsonAsync($"/api/orgs/{orgId}/dashboard");
        Assert.Equal(1, dashboard.GetProperty("work").GetProperty("completedLast30Days").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("completionTrend").EnumerateArray().Sum(w => w.GetProperty("completed").GetInt32()));
        Assert.Contains(dashboard.GetProperty("recentActivity").EnumerateArray(), a => a.GetProperty("summary").GetString()!.StartsWith("completó PAY-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_conflict()
    {
        var (owner, _, _, baseUrl) = await SetupAsync();
        var created = await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Concurrent", priority = "Low" }, HttpStatusCode.Created);
        var version = created.GetProperty("version").GetUInt32();
        var body = new { title = "First edit", priority = "Low", labelIds = Array.Empty<Guid>(), version };
        await owner.PutJsonAsync($"{baseUrl}/items/1", body);

        var stale = await owner.PutAsync($"{baseUrl}/items/1", body with { title = "Second edit" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("workitem.stale", await stale.ProblemCodeAsync());
    }

    [Fact]
    public async Task Parent_cannot_complete_with_open_subtasks()
    {
        var (owner, _, project, baseUrl) = await SetupAsync();
        var parent = await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Parent", priority = "Medium" }, HttpStatusCode.Created);
        var parentId = parent.GetProperty("item").GetProperty("id").GetGuid();
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Subtask", title = "Child", priority = "Low", parentId }, HttpStatusCode.Created);

        // Subtask of a subtask is rejected.
        var nested = await owner.PostAsync($"{baseUrl}/items", new { type = "Subtask", title = "Grandchild", priority = "Low", parentId = (await owner.GetJsonAsync($"{baseUrl}/items/2")).GetProperty("item").GetProperty("id").GetGuid() });
        Assert.Equal("workitem.nested_subtask", await nested.ProblemCodeAsync());

        await owner.PostJsonAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("En curso") });
        var blocked = await owner.PostAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("Hecho") });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("workitem.open_subtasks", await blocked.ProblemCodeAsync());

        await owner.PostJsonAsync($"{baseUrl}/items/2/transition", new { statusId = project.StatusId("Cancelado") });
        await owner.PostJsonAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("Hecho") });

        var detail = await owner.GetJsonAsync($"{baseUrl}/items/1");
        Assert.Equal(1, detail.GetProperty("subtasks").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("item").GetProperty("subtaskCount").GetInt32());
        Assert.Equal(0, detail.GetProperty("item").GetProperty("openSubtaskCount").GetInt32());
    }

    [Fact]
    public async Task Concurrent_creates_get_unique_sequential_numbers()
    {
        var (owner, _, _, baseUrl) = await SetupAsync();
        var tasks = Enumerable.Range(0, 15).Select(i => owner.PostAsync($"{baseUrl}/items", new { type = "Task", title = $"Item {i}", priority = "Low" }));
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new List<int>();
        foreach (var response in responses)
        {
            numbers.Add((await response.ReadJsonAsync()).GetProperty("item").GetProperty("number").GetInt32());
        }

        Assert.Equal(Enumerable.Range(1, 15), numbers.Order());
    }

    [Fact]
    public async Task Assignee_must_be_project_member_and_gets_notified()
    {
        var (owner, orgId, _, baseUrl) = await SetupAsync();
        var dev = await factory.NewUserAsync("dev");
        var invite = await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = dev.Email, role = "Developer" }, HttpStatusCode.Created);
        await dev.PostJsonAsync("/api/invitations/accept", new { token = invite.GetProperty("acceptToken").GetString() });

        var rejected = await owner.PostAsync($"{baseUrl}/items", new { type = "Task", title = "Assigned", priority = "Low", assigneeId = dev.UserId });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        await owner.PostJsonAsync($"{baseUrl}/members", new { userId = dev.UserId, role = "Member" });
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Assigned", priority = "Low", assigneeId = dev.UserId }, HttpStatusCode.Created);

        var notifications = await dev.GetJsonAsync($"/api/orgs/{orgId}/notifications");
        Assert.Equal(1, notifications.GetProperty("unreadCount").GetInt32());
        var notification = notifications.GetProperty("page").GetProperty("items")[0];
        Assert.Equal("task.assigned", notification.GetProperty("kind").GetString());
        Assert.StartsWith("/projects/", notification.GetProperty("link").GetString(), StringComparison.Ordinal);

        await dev.PostJsonAsync($"/api/orgs/{orgId}/notifications/read-all");
        Assert.Equal(0, (await dev.GetJsonAsync($"/api/orgs/{orgId}/notifications")).GetProperty("unreadCount").GetInt32());

        // Notifications are per user: the owner sees none.
        Assert.Equal(0, (await owner.GetJsonAsync($"/api/orgs/{orgId}/notifications")).GetProperty("page").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Filters_search_sort_and_paging()
    {
        var (owner, orgId, _, baseUrl) = await SetupAsync();
        await owner.CreateItemAsync(orgId, (await owner.GetJsonAsync(baseUrl)).GetProperty("id").GetGuid(), new { type = "Issue", title = "Login broken", priority = "Critical", assigneeId = owner.UserId });
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Write docs", priority = "Low", dueDate = "2020-01-01" }, HttpStatusCode.Created);
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Refactor login form", priority = "Medium" }, HttpStatusCode.Created);

        Assert.Equal(2, (await owner.GetJsonAsync($"{baseUrl}/items?q=login")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await owner.GetJsonAsync($"{baseUrl}/items?q=PAY-2")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await owner.GetJsonAsync($"{baseUrl}/items?type=Issue")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await owner.GetJsonAsync($"{baseUrl}/items?assignee=me")).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await owner.GetJsonAsync($"{baseUrl}/items?assignee=none")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await owner.GetJsonAsync($"{baseUrl}/items?overdue=true")).GetProperty("totalCount").GetInt32());

        var sorted = await owner.GetJsonAsync($"{baseUrl}/items?sort=-priority&pageSize=2");
        Assert.Equal("Critical", sorted.GetProperty("items")[0].GetProperty("priority").GetString());
        Assert.Equal(2, sorted.GetProperty("items").GetArrayLength());
        Assert.Equal(2, sorted.GetProperty("totalPages").GetInt32());

        var invalidSort = await owner.GetAsync($"{baseUrl}/items?sort=password");
        Assert.Equal(HttpStatusCode.BadRequest, invalidSort.StatusCode);

        var orgWide = await owner.GetJsonAsync($"/api/orgs/{orgId}/work-items?type=Issue&open=true");
        Assert.Equal(1, orgWide.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Board_groups_items_by_status()
    {
        var (owner, _, project, baseUrl) = await SetupAsync();
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "A", priority = "Low" }, HttpStatusCode.Created);
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "B", priority = "High", statusId = project.StatusId("En curso") }, HttpStatusCode.Created);
        var closedStart = await owner.PostAsync($"{baseUrl}/items", new { type = "Task", title = "C", priority = "High", statusId = project.StatusId("Hecho") });
        Assert.Equal(HttpStatusCode.BadRequest, closedStart.StatusCode);

        var board = await owner.GetJsonAsync($"{baseUrl}/board");
        var columns = board.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("status").GetProperty("name").GetString()!, c => c.GetProperty("totalCount").GetInt32());
        Assert.Equal(6, columns.Count);
        Assert.Equal(1, columns["Backlog"]);
        Assert.Equal(1, columns["En curso"]);
        Assert.True(board.GetProperty("transitions").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Comments_permissions()
    {
        var (owner, orgId, _, baseUrl) = await SetupAsync();
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Discuss", priority = "Low" }, HttpStatusCode.Created);
        var comment = await owner.PostJsonAsync($"{baseUrl}/items/1/comments", new { body = "First!" });
        var commentId = comment.GetProperty("id").GetGuid();

        var dev = await factory.NewUserAsync("dev");
        var invite = await owner.PostJsonAsync($"/api/orgs/{orgId}/invitations", new { email = dev.Email, role = "Developer" }, HttpStatusCode.Created);
        await dev.PostJsonAsync("/api/invitations/accept", new { token = invite.GetProperty("acceptToken").GetString() });

        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PutAsync($"{baseUrl}/items/1/comments/{commentId}", new { body = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.DeleteAsync($"{baseUrl}/items/1/comments/{commentId}")).StatusCode);
        await dev.PostJsonAsync($"{baseUrl}/items/1/comments", new { body = "Reply from dev" });

        var edited = await owner.PutJsonAsync($"{baseUrl}/items/1/comments/{commentId}", new { body = "First (edited)" });
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        var list = await owner.GetJsonAsync($"{baseUrl}/items/1/comments");
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        Assert.True(list.GetProperty("items")[1].GetProperty("canDelete").GetBoolean()); // owner can moderate
        Assert.False(list.GetProperty("items")[1].GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Archive_makes_project_read_only_and_delete_requires_archive()
    {
        var (owner, orgId, project, baseUrl) = await SetupAsync();
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "Before archive", priority = "Low" }, HttpStatusCode.Created);

        var notArchived = await owner.DeleteAsync(baseUrl);
        Assert.Equal(HttpStatusCode.Conflict, notArchived.StatusCode);

        await owner.PostJsonAsync($"{baseUrl}/archive");
        var create = await owner.PostAsync($"{baseUrl}/items", new { type = "Task", title = "After archive", priority = "Low" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal("project.archived", await create.ProblemCodeAsync());

        await (await owner.DeleteAsync(baseUrl)).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(baseUrl)).StatusCode);
        Assert.Equal(0, (await owner.GetJsonAsync($"/api/orgs/{orgId}/work-items")).GetProperty("totalCount").GetInt32());
        _ = project;
    }

    [Fact]
    public async Task Duplicate_project_key_is_conflict()
    {
        var (owner, orgId, _, _) = await SetupAsync();
        var duplicate = await owner.PostAsync($"/api/orgs/{orgId}/projects", new { name = "Again", key = "pay" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("project.key_taken", await duplicate.ProblemCodeAsync());
    }

    [Fact]
    public async Task Workflow_configuration_rules()
    {
        var (owner, _, project, baseUrl) = await SetupAsync();
        var workflow = await owner.PostJsonAsync($"{baseUrl}/workflow/statuses", new { name = "QA", category = "InProgress", position = 4 });
        var qa = workflow.GetProperty("statuses").EnumerateArray().First(s => s.GetProperty("name").GetString() == "QA").GetProperty("id").GetGuid();

        var duplicate = await owner.PostAsync($"{baseUrl}/workflow/statuses", new { name = "qa", category = "InProgress", position = 5 });
        Assert.Equal("workflow.duplicate_status", await duplicate.ProblemCodeAsync());

        // Removing the only Done status is refused.
        var removeDone = await owner.DeleteAsync($"{baseUrl}/workflow/statuses/{project.StatusId("Hecho")}");
        Assert.Equal("workflow.requires_done", await removeDone.ProblemCodeAsync());

        // A status with items cannot be deleted.
        await owner.PostJsonAsync($"{baseUrl}/items", new { type = "Task", title = "In QA", priority = "Low", statusId = qa }, HttpStatusCode.Created);
        var inUse = await owner.DeleteAsync($"{baseUrl}/workflow/statuses/{qa}");
        Assert.Equal("workflow.status_in_use", await inUse.ProblemCodeAsync());

        // Replace transitions: only QA -> Done allowed.
        var updated = await owner.PutJsonAsync($"{baseUrl}/workflow/transitions",
            new { transitions = new[] { new { fromStatusId = qa, toStatusId = project.StatusId("Hecho") } } });
        Assert.Equal(1, updated.GetProperty("transitions").GetArrayLength());
        var invalid = await owner.PostAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("Backlog") });
        Assert.Equal("workitem.invalid_transition", await invalid.ProblemCodeAsync());
        await owner.PostJsonAsync($"{baseUrl}/items/1/transition", new { statusId = project.StatusId("Hecho") });

        // Recategorizing a status updates its items' denormalized category.
        await owner.PutJsonAsync($"{baseUrl}/workflow/statuses/{project.StatusId("Hecho")}", new { name = "Shipped", category = "Done", position = 6 });
        var item = await owner.GetJsonAsync($"{baseUrl}/items/1");
        Assert.Equal("Shipped", item.GetProperty("item").GetProperty("statusName").GetString());
    }
}
