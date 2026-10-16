using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevPilot.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API against a real PostgreSQL database (recreated once per test run).
/// Tests isolate themselves by creating their own users and organizations.
/// Override the database with DEVPILOT_TEST_CONNECTION.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly string ConnectionString = Environment.GetEnvironmentVariable("DEVPILOT_TEST_CONNECTION")
        ?? "Host=127.0.0.1;Port=5432;Database=devpilot_test;Username=devpilot;Password=devpilot_dev_pw";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("SessionCookie:Secure", "false");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("RateLimits:AuthPerMinute", "100000");
        builder.UseSetting("RateLimits:ApiPerMinute", "100000");
        builder.UseSetting("RateLimits:AiPerMinute", "100000");
        builder.UseSetting("RateLimits:WebhooksPerMinute", "100000");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Secrets:EncryptionKey", Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("Integrations:PublicBaseUrl", "https://devpilot.test");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DevPilot.Application.Ai.IAiServiceClient>();
            services.AddSingleton(Ai);
            services.AddSingleton<DevPilot.Application.Ai.IAiServiceClient>(Ai);
        });
    }

    /// <summary>Fake AI service shared by all tests (configure per test; requests are captured).</summary>
    public FakeAiClient Ai { get; } = new();

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public ApiClient NewClient() => new(CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false }));

    /// <summary>Registers a new user with a unique email and returns an authenticated client.</summary>
    public async Task<ApiClient> NewUserAsync(string? name = null)
    {
        var client = NewClient();
        var email = $"{(name ?? "user").ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        await client.RegisterAsync(email, name ?? "Test User");
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public HttpClient Http { get; } = http;
    public string Email { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }

    public async Task RegisterAsync(string email, string name, string password = "correct-horse-battery-9")
    {
        var response = await Http.PostAsJsonAsync("/api/auth/register", new { email, displayName = name, password }, Json);
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        CaptureCsrf(response);
        Email = email;
        UserId = (await response.ReadJsonAsync()).GetProperty("user").GetProperty("id").GetGuid();
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        var response = await Http.PostAsJsonAsync("/api/auth/login", new { email, password }, Json);
        if (response.IsSuccessStatusCode)
        {
            CaptureCsrf(response);
            Email = email;
        }

        return response;
    }

    public void CaptureCsrf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return;
        }

        var csrf = cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("devpilot_csrf=", StringComparison.Ordinal));
        if (csrf is not null)
        {
            Http.DefaultRequestHeaders.Remove("X-CSRF-Token");
            Http.DefaultRequestHeaders.Add("X-CSRF-Token", csrf["devpilot_csrf=".Length..]);
        }
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => Http.PostAsJsonAsync(url, body ?? new { }, Json);
    public Task<HttpResponseMessage> PutAsync(string url, object body) => Http.PutAsJsonAsync(url, body, Json);
    public Task<HttpResponseMessage> DeleteAsync(string url) => Http.DeleteAsync(url);

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await GetAsync(url);
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    public async Task<JsonElement> PostJsonAsync(string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await PostAsync(url, body);
        await response.EnsureStatusAsync(expected);
        return await response.ReadJsonAsync();
    }

    public async Task<JsonElement> PutJsonAsync(string url, object body)
    {
        var response = await PutAsync(url, body);
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    public async Task<Guid> CreateOrgAsync(string name = "Acme")
    {
        var org = await PostJsonAsync("/api/orgs", new { name }, HttpStatusCode.Created);
        return org.GetProperty("id").GetGuid();
    }

    public async Task<JsonElement> CreateProjectAsync(Guid orgId, string key = "PAY", string name = "Payments")
    {
        return await PostJsonAsync($"/api/orgs/{orgId}/projects", new { name, key }, HttpStatusCode.Created);
    }

    public async Task<JsonElement> CreateItemAsync(Guid orgId, Guid projectId, object body)
    {
        return await PostJsonAsync($"/api/orgs/{orgId}/projects/{projectId}/items", body, HttpStatusCode.Created);
    }
}

public static class HttpAssertions
{
    public static async Task EnsureStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode} for "
                + $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
    }

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        var json = await response.ReadJsonAsync();
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static Guid StatusId(this JsonElement project, string name) =>
        project.GetProperty("statuses").EnumerateArray().First(s => s.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
}
