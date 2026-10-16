"""Provider abstraction. Providers receive already-redacted input and return drafts; the service layer validates them."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Protocol

from app.analysis.heuristics import HeuristicFindings
from app.schemas import (
    ActivitySummaryRequest,
    AnalysisRequest,
    Classification,
    ContextDocument,
    Fact,
    Hypothesis,
    Recommendation,
)


class ProviderError(Exception):
    """Base class. `code` is stable and exposed to the backend."""

    code = "provider_error"
    status = 502
    retryable = True

    def __init__(self, message: str, *, retry_after: int | None = None) -> None:
        super().__init__(message)
        self.retry_after = retry_after


class ProviderTimeout(ProviderError):
    code = "provider_timeout"
    status = 504


class ProviderRateLimited(ProviderError):
    code = "provider_rate_limited"
    status = 503


class ProviderUnavailable(ProviderError):
    code = "provider_unavailable"
    status = 503


class ProviderMisconfigured(ProviderError):
    code = "provider_misconfigured"
    status = 503
    retryable = False


class ProviderRefused(ProviderError):
    code = "provider_refused"
    status = 422
    retryable = False


class ProviderInvalidOutput(ProviderError):
    code = "provider_invalid_output"
    status = 502


@dataclass
class AnalysisDraft:
    summary: str
    classification: Classification
    facts: list[Fact]
    hypotheses: list[Hypothesis]
    recommendations: list[Recommendation]
    warnings: list[str] = field(default_factory=list)


@dataclass
class AssistantDraft:
    answer: str
    citation_ids: list[str]
    grounded: bool
    warnings: list[str] = field(default_factory=list)


@dataclass
class ActivityDraft:
    summary: str
    highlights: list[str]
    risks: list[str]
    warnings: list[str] = field(default_factory=list)


class Provider(Protocol):
    name: str
    model: str

    async def analyze(self, request: AnalysisRequest, excerpt: str, findings: HeuristicFindings) -> AnalysisDraft: ...

    async def answer(self, question: str, organization: str, documents: list[ContextDocument], language: str) -> AssistantDraft: ...

    async def summarize_activity(self, request: ActivitySummaryRequest) -> ActivityDraft: ...
