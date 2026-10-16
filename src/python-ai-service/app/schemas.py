"""Public contract of the AI service (v1). Mirrored by the .NET client; see contracts/ai-service.openapi.json."""

from __future__ import annotations

from enum import StrEnum

from pydantic import BaseModel, ConfigDict, Field


def _camel(name: str) -> str:
    head, *tail = name.split("_")
    return head + "".join(part.title() for part in tail)


class ApiModel(BaseModel):
    """camelCase on the wire, snake_case in Python; unknown fields are rejected."""

    model_config = ConfigDict(alias_generator=_camel, populate_by_name=True, extra="forbid", frozen=True)


class AnalysisKind(StrEnum):
    LOG_ANALYSIS = "log_analysis"
    PIPELINE_FAILURE = "pipeline_failure"
    INCIDENT_SUMMARY = "incident_summary"


class ErrorCategory(StrEnum):
    DEPENDENCY = "dependency"
    CONFIGURATION = "configuration"
    CODE_DEFECT = "code_defect"
    INFRASTRUCTURE = "infrastructure"
    NETWORK = "network"
    AUTHENTICATION = "authentication"
    RESOURCE_EXHAUSTION = "resource_exhaustion"
    TEST_FAILURE = "test_failure"
    BUILD_FAILURE = "build_failure"
    DATA = "data"
    UNKNOWN = "unknown"


class Severity(StrEnum):
    CRITICAL = "critical"
    HIGH = "high"
    MEDIUM = "medium"
    LOW = "low"


class AnalysisContext(ApiModel):
    project_name: str | None = Field(default=None, max_length=200)
    environment: str | None = Field(default=None, max_length=50)
    pipeline_name: str | None = Field(default=None, max_length=200)
    title: str | None = Field(default=None, max_length=300)
    branch: str | None = Field(default=None, max_length=200)


class AnalysisRequest(ApiModel):
    request_id: str = Field(min_length=1, max_length=64)
    kind: AnalysisKind
    content: str = Field(min_length=1, max_length=1_000_000, description="Logs / stack traces. Treated as untrusted data.")
    context: AnalysisContext = AnalysisContext()
    language: str = Field(default="es", pattern="^(es|en)$")


class Classification(ApiModel):
    category: ErrorCategory
    severity: Severity
    confidence: float | None = Field(default=None, ge=0, le=1, description="Null when there is no reasonable basis for an estimate.")


class Fact(ApiModel):
    statement: str
    evidence: str | None = Field(default=None, description="Verbatim (redacted) excerpt that supports the statement.")
    line: int | None = None


class Hypothesis(ApiModel):
    statement: str
    rationale: str
    confidence: float | None = Field(default=None, ge=0, le=1)


class Recommendation(ApiModel):
    action: str
    rationale: str
    requires_approval: bool = Field(
        description="True when the action changes infrastructure, data or deployments; never executed automatically."
    )


class Pattern(ApiModel):
    signature: str
    occurrences: int
    example: str


class RedactionReport(ApiModel):
    count: int
    kinds: dict[str, int]


class AnalysisResponse(ApiModel):
    request_id: str
    provider: str
    model: str
    summary: str
    classification: Classification
    facts: list[Fact]
    hypotheses: list[Hypothesis]
    recommendations: list[Recommendation]
    patterns: list[Pattern]
    redactions: RedactionReport
    truncated: bool
    latency_ms: int
    warnings: list[str]


class DocumentType(StrEnum):
    PROJECT = "project"
    WORK_ITEM = "work_item"
    INCIDENT = "incident"
    PIPELINE_RUN = "pipeline_run"
    DEPLOYMENT = "deployment"
    ACTIVITY = "activity"


class ContextDocument(ApiModel):
    type: DocumentType
    id: str = Field(max_length=100)
    title: str = Field(max_length=300)
    content: str = Field(max_length=5000)


class AssistantRequest(ApiModel):
    request_id: str = Field(min_length=1, max_length=64)
    question: str = Field(min_length=1, max_length=2000)
    organization_name: str = Field(max_length=200)
    documents: list[ContextDocument] = Field(default_factory=list, max_length=200)
    language: str = Field(default="es", pattern="^(es|en)$")


class Citation(ApiModel):
    id: str
    type: DocumentType
    title: str


class AssistantResponse(ApiModel):
    request_id: str
    provider: str
    model: str
    answer: str
    citations: list[Citation]
    grounded: bool = Field(description="False when the provided documents did not contain an answer.")
    redactions: RedactionReport
    latency_ms: int
    warnings: list[str]


class ActivityEvent(ApiModel):
    at: str = Field(max_length=40)
    actor: str | None = Field(default=None, max_length=200)
    summary: str = Field(max_length=500)


class ActivitySummaryRequest(ApiModel):
    request_id: str = Field(min_length=1, max_length=64)
    project_name: str = Field(max_length=200)
    period_days: int = Field(ge=1, le=90)
    events: list[ActivityEvent] = Field(max_length=500)
    stats: dict[str, int] = Field(default_factory=dict)
    language: str = Field(default="es", pattern="^(es|en)$")


class ActivitySummaryResponse(ApiModel):
    request_id: str
    provider: str
    model: str
    summary: str
    highlights: list[str]
    risks: list[str]
    latency_ms: int
    warnings: list[str]


class ErrorBody(ApiModel):
    code: str
    message: str
    retry_after_seconds: int | None = None


class ErrorResponse(ApiModel):
    error: ErrorBody


class HealthResponse(ApiModel):
    status: str
    provider: str
    model: str
    external_provider_configured: bool
