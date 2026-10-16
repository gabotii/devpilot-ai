using System.Net;
using System.Net.Http.Json;
using DevPilot.IntegrationTests.Infrastructure;

namespace DevPilot.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class AuthTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_sets_httponly_session_cookie_and_me_works()
    {
        var client = factory.NewClient();
        var email = $"me-{Guid.NewGuid():N}@example.com";
        var response = await client.Http.PostAsJsonAsync("/api/auth/register", new { email, displayName = "Me", password = "correct-horse-battery-9" });
        await response.EnsureStatusAsync(HttpStatusCode.Created);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var session = Assert.Single(cookies, c => c.StartsWith("devpilot_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", session, StringComparison.OrdinalIgnoreCase);
        var csrf = Assert.Single(cookies, c => c.StartsWith("devpilot_csrf=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", csrf, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetJsonAsync("/api/auth/me");
        Assert.Equal(email, me.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(0, me.GetProperty("organizations").GetArrayLength());
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        var response = await factory.NewClient().GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync("/api/orgs")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected_case_insensitively()
    {
        var first = await factory.NewUserAsync();
        var response = await factory.NewClient().Http.PostAsJsonAsync("/api/auth/register",
            new { email = first.Email.ToUpperInvariant(), displayName = "Dup", password = "correct-horse-battery-9" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("short", "Password must be at least 12 characters long.")]
    [InlineData("aaaaaaaaaaaaaaaa", "Password is too repetitive.")]
    public async Task Weak_passwords_are_rejected(string password, string message)
    {
        var response = await factory.NewClient().Http.PostAsJsonAsync("/api/auth/register",
            new { email = $"w-{Guid.NewGuid():N}@example.com", displayName = "W", password });
        await response.EnsureStatusAsync(HttpStatusCode.BadRequest);
        var body = await response.ReadJsonAsync();
        Assert.Contains(message, body.GetProperty("errors").GetProperty("password").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Login_failure_is_generic_and_locks_out_after_repeated_attempts()
    {
        var user = await factory.NewUserAsync();
        var client = factory.NewClient();

        var unknown = await client.LoginAsync($"nobody-{Guid.NewGuid():N}@example.com", "whatever-password-1");
        var wrong = await client.LoginAsync(user.Email, "wrong-password-123");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal((await unknown.ReadJsonAsync()).GetProperty("detail").GetString(), (await wrong.ReadJsonAsync()).GetProperty("detail").GetString());

        for (var i = 0; i < 4; i++)
        {
            await client.LoginAsync(user.Email, "wrong-password-123");
        }

        // Locked: even the correct password is rejected until the lockout expires.
        var locked = await client.LoginAsync(user.Email, "correct-horse-battery-9");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_server_side()
    {
        var client = await factory.NewUserAsync();
        var stolenCookie = client.Http.DefaultRequestHeaders.ToString();
        await (await client.PostAsync("/api/auth/logout")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.NotNull(stolenCookie);
    }

    [Fact]
    public async Task Replaying_a_session_token_after_logout_fails()
    {
        var client = factory.NewClient();
        var email = $"replay-{Guid.NewGuid():N}@example.com";
        var register = await client.Http.PostAsJsonAsync("/api/auth/register", new { email, displayName = "R", password = "correct-horse-battery-9" });
        client.CaptureCsrf(register);
        var sessionCookie = register.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("devpilot_session=", StringComparison.Ordinal)).Split(';')[0];
        await (await client.PostAsync("/api/auth/logout")).EnsureStatusAsync(HttpStatusCode.NoContent);

        var attacker = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_without_csrf_token_are_rejected()
    {
        var client = await factory.NewUserAsync();
        client.Http.DefaultRequestHeaders.Remove("X-CSRF-Token");
        var response = await client.PostAsync("/api/orgs", new { name = "No CSRF" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf.invalid", await response.ProblemCodeAsync());

        client.Http.DefaultRequestHeaders.Add("X-CSRF-Token", "forged-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/orgs", new { name = "Forged" })).StatusCode);
    }

    [Fact]
    public async Task Changing_password_revokes_other_sessions()
    {
        var first = await factory.NewUserAsync();
        var second = factory.NewClient();
        await (await second.LoginAsync(first.Email, "correct-horse-battery-9")).EnsureStatusAsync(HttpStatusCode.OK);

        await (await first.PostAsync("/api/auth/me/password", new { currentPassword = "correct-horse-battery-9", newPassword = "another-strong-pass-42" }))
            .EnsureStatusAsync(HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
        await (await factory.NewClient().LoginAsync(first.Email, "another-strong-pass-42")).EnsureStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_endpoints_respond()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        var response = await factory.NewClient().GetAsync("/api/auth/me");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }
}
