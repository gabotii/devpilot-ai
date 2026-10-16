using System.Text.Json.Serialization;

namespace DevPilot.Application.Ai;

// Wire contract of the Python AI service (contracts/ai-service.openapi.json). Enumerations are kept as strings so the
// backend tolerates new categories added by the service without a coordinated deploy.

public sealed record AiAnalysisContext(string? ProjectName, string? Environment, string? PipelineName, string? Title, string? Branch);

public sealed record AiAnalysisRequest(string RequestId, string Kind, string Content, AiAnalysisContext Context, string Language = "es");

public sealed record AiClassification(string Category, string Severity, double? Confidence);

public sealed record AiFact(string Statement, string? Evidence, int? Line);

public sealed record AiHypothesis(string Statement, string Rationale, double? Confidence);

public sealed record AiRecommendation(string Action, string Rationale, bool RequiresApproval);

public sealed record AiPattern(string Signature, int Occurrences, string Example);

public sealed record AiRedactions(int Count, Dictionary<string, int> Kinds);

public sealed record AiAnalysisResult(
    string RequestId,
    string Provider,
    string Model,
    string Summary,
    AiClassification Classification,
    IReadOnlyList<AiFact> Facts,
    IReadOnlyList<AiHypothesis> Hypotheses,
    IReadOnlyList<AiRecommendation> Recommendations,
    IReadOnlyList<AiPattern> Patterns,
    AiRedactions Redactions,
    bool Truncated,
    int LatencyMs,
    IReadOnlyList<string> Warnings);

public sealed record AiContextDocument(string Type, string Id, string Title, string Content);

public sealed record AiAssistantRequest(string RequestId, string Question, string OrganizationName, IReadOnlyList<AiContextDocument> Documents,
    string Language = "es");

public sealed record AiCitation(string Id, string Type, string Title);

public sealed record AiAssistantResult(
    string RequestId,
    string Provider,
    string Model,
    string Answer,
    IReadOnlyList<AiCitation> Citations,
    bool Grounded,
    AiRedactions Redactions,
    int LatencyMs,
    IReadOnlyList<string> Warnings);

public sealed record AiActivityEvent(string At, string? Actor, string Summary);

public sealed record AiActivitySummaryRequest(string RequestId, string ProjectName, int PeriodDays, IReadOnlyList<AiActivityEvent> Events,
    Dictionary<string, int> Stats, string Language = "es");

public sealed record AiActivitySummaryResult(
    string RequestId,
    string Provider,
    string Model,
    string Summary,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<string> Risks,
    int LatencyMs,
    IReadOnlyList<string> Warnings);

public sealed record AiServiceHealth(string Status, string Provider, string Model, bool ExternalProviderConfigured);

public sealed record AiErrorBody(string Code, string Message, int? RetryAfterSeconds);

public sealed record AiErrorResponse([property: JsonPropertyName("error")] AiErrorBody Error);

/// <summary>The AI service answered with an error (provider timeout, rate limit, refusal...) or could not be reached.</summary>
public sealed class AiServiceException(string code, string message, int statusCode, TimeSpan? retryAfter = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IAiServiceClient
{
    Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken ct);

    Task<AiAssistantResult> AnswerAsync(AiAssistantRequest request, CancellationToken ct);

    Task<AiActivitySummaryResult> SummarizeActivityAsync(AiActivitySummaryRequest request, CancellationToken ct);

    /// <summary>Returns null when the service is unreachable (never throws).</summary>
    Task<AiServiceHealth?> GetHealthAsync(CancellationToken ct);
}
