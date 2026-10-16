using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevPilot.Application.Ai;

namespace DevPilot.IntegrationTests.Infrastructure;

/// <summary>In-memory AI service. Answers deterministically unless a test sets <see cref="Failure"/>.</summary>
public sealed class FakeAiClient : IAiServiceClient
{
    public ConcurrentQueue<object> Requests { get; } = new();

    /// <summary>When set, every call throws it (simulates provider/service failures).</summary>
    public AiServiceException? Failure { get; set; }

    public bool Healthy { get; set; } = true;

    public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Failure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new AiAnalysisResult(request.RequestId, "fake", "fake-model", $"Resumen de {request.Kind}",
            new AiClassification("network", "high", 0.6),
            [new AiFact("Se observó un rechazo de conexión", "connection refused", 1)],
            [new AiHypothesis("La base de datos no estaba lista", "Rechazo al arrancar", 0.5)],
            [new AiRecommendation("Revisar el health check", "Evita carreras", false)],
            [new AiPattern("<ts> connection refused", 2, "connection refused")],
            new AiRedactions(0, []), false, 12, []));
    }

    public Task<AiAssistantResult> AnswerAsync(AiAssistantRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Failure is { } failure)
        {
            throw failure;
        }

        var first = request.Documents.Count > 0 ? request.Documents[0] : null;
        return Task.FromResult(new AiAssistantResult(request.RequestId, "fake", "fake-model", $"{request.Documents.Count} documentos revisados.",
            first is null ? [] : [new AiCitation(first.Id, first.Type, first.Title)], first is not null, new AiRedactions(0, []), 5, []));
    }

    public Task<AiActivitySummaryResult> SummarizeActivityAsync(AiActivitySummaryRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Failure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new AiActivitySummaryResult(request.RequestId, "fake", "fake-model", $"{request.Events.Count} eventos", [], [], 3, []));
    }

    public Task<AiServiceHealth?> GetHealthAsync(CancellationToken ct) =>
        Task.FromResult(Healthy ? new AiServiceHealth("ok", "heuristic", "devpilot-rules-v1", false) : null);

    public T? Last<T>() where T : class => Requests.Reverse().OfType<T>().FirstOrDefault();
}

/// <summary>Builds GitHub webhook requests signed like GitHub does (HMAC-SHA256 of the raw body).</summary>
public static class GitHub
{
    public static HttpRequestMessage Webhook(Guid integrationId, string secret, string eventType, object payload, string? deliveryId = null,
        string? signatureOverride = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/github/{integrationId}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventType);
        request.Headers.Add("X-GitHub-Delivery", deliveryId ?? Guid.NewGuid().ToString());
        request.Headers.Add("X-Hub-Signature-256",
            signatureOverride ?? "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
        return request;
    }

    public static object Repo(long id = 42, string fullName = "acme/checkout") =>
        new { id, full_name = fullName, default_branch = "main", html_url = $"https://github.com/{fullName}" };

    public static object WorkflowRun(long runId, string status, string? conclusion, string branch = "main", int attempt = 1, string updatedAt = "2026-10-11T10:05:00Z",
        object? repo = null) => new
        {
            action = status == "completed" ? "completed" : "in_progress",
            workflow_run = new
            {
                id = runId,
                workflow_id = 777,
                name = "CI",
                path = ".github/workflows/ci.yml",
                run_number = runId % 1000,
                run_attempt = attempt,
                head_branch = branch,
                head_sha = "abcdef1234567890",
                @event = "push",
                status,
                conclusion,
                created_at = "2026-10-11T10:00:00Z",
                run_started_at = "2026-10-11T10:00:10Z",
                updated_at = updatedAt,
                html_url = $"https://github.com/acme/checkout/actions/runs/{runId}",
                actor = new { login = "octocat" },
            },
            repository = repo ?? Repo(),
        };

    public static object DeploymentStatus(long deploymentId, string state, string environment = "production", string at = "2026-10-11T11:00:00Z") => new
    {
        action = "created",
        deployment_status = new { state, environment_url = "https://checkout.example.com", created_at = at },
        deployment = new
        {
            id = deploymentId,
            environment,
            @ref = "main",
            sha = "abcdef1234567890",
            description = "Deploy checkout",
            creator = new { login = "octocat" },
            created_at = "2026-10-11T10:58:00Z",
            updated_at = at,
        },
        repository = Repo(),
    };
}
