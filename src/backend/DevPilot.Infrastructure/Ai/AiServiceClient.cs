using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevPilot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.Ai;

public sealed class AiServiceOptions
{
    public const string SectionName = "AiService";

    public string BaseUrl { get; set; } = "http://127.0.0.1:8100";

    /// <summary>Shared secret sent in X-Internal-Api-Key. Must match AI_INTERNAL_API_KEY of the Python service.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Upper bound for one AI request (the service enforces its own provider timeout below this).</summary>
    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>
/// Typed HTTP client for the Python AI service. Every failure becomes an <see cref="AiServiceException"/> with a stable code,
/// so callers can record it and the API can answer 503/504 without affecting the rest of the platform.
/// </summary>
public sealed partial class AiServiceClient(HttpClient http, ILogger<AiServiceClient> logger) : IAiServiceClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken ct) =>
        PostAsync<AiAnalysisRequest, AiAnalysisResult>("/v1/analyses", request, ct);

    public Task<AiAssistantResult> AnswerAsync(AiAssistantRequest request, CancellationToken ct) =>
        PostAsync<AiAssistantRequest, AiAssistantResult>("/v1/assistant/answers", request, ct);

    public Task<AiActivitySummaryResult> SummarizeActivityAsync(AiActivitySummaryRequest request, CancellationToken ct) =>
        PostAsync<AiActivitySummaryRequest, AiActivitySummaryResult>("/v1/summaries/activity", request, ct);

    public async Task<AiServiceHealth?> GetHealthAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            return await http.GetFromJsonAsync<AiServiceHealth>("/health/ready", Json, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(path, request, Json, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LogFailure(logger, ex, path);
            throw new AiServiceException("ai_service_timeout", "The AI service did not answer in time.", 504);
        }
        catch (HttpRequestException ex)
        {
            LogFailure(logger, ex, path);
            throw new AiServiceException("ai_service_unreachable", "The AI service is not reachable.", 503);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TResponse>(Json, ct)
                    ?? throw new AiServiceException("ai_service_invalid_response", "The AI service returned an empty response.", 502);
            }

            AiErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<AiErrorResponse>(Json, ct);
            }
            catch (JsonException)
            {
                // Non-JSON error body (proxy, crash): fall through to a generic error.
            }

            var retryAfter = response.Headers.RetryAfter?.Delta ?? (error?.Error.RetryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
            var status = response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity => 422,
                HttpStatusCode.GatewayTimeout => 504,
                _ => 503,
            };
            throw new AiServiceException(error?.Error.Code ?? $"ai_service_http_{(int)response.StatusCode}",
                error?.Error.Message ?? $"The AI service returned HTTP {(int)response.StatusCode}.", status, retryAfter);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI service call to {Path} failed")]
    private static partial void LogFailure(ILogger logger, Exception exception, string path);
}
