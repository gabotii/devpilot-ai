"""Orchestrates redaction, deterministic pre-analysis, the provider call and output validation."""

from __future__ import annotations

import asyncio
import logging
import re
import time
from collections.abc import Awaitable, Callable

from app.analysis import heuristics
from app.config import Settings
from app.providers.base import Provider, ProviderError, ProviderTimeout
from app.providers.heuristic import HeuristicProvider
from app.redaction import redact
from app.schemas import (
    ActivitySummaryRequest,
    ActivitySummaryResponse,
    AnalysisRequest,
    AnalysisResponse,
    AssistantRequest,
    AssistantResponse,
    Citation,
    ContextDocument,
    Fact,
    Recommendation,
    RedactionReport,
)

logger = logging.getLogger("devpilot.ai")

# Actions that change systems always require human approval, whatever the provider says.
_RISKY_ACTION = re.compile(
    r"\b(deploy|desplegar|despliegue|rollback|revert|restart|reiniciar|scale|escalar|delete|eliminar|borrar|drop|truncate|"
    r"migrat|migrar|kubectl|terraform|helm|rotate|rotar|aumentar|increase|cambiar la configuraci|modify config|force[- ]push)",
    re.IGNORECASE,
)


def _squash(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip().lower()


class AiService:
    def __init__(self, settings: Settings, primary: Provider, fallback: HeuristicProvider | None = None) -> None:
        self._settings = settings
        self._primary = primary
        self._fallback = fallback or HeuristicProvider()

    @property
    def provider_name(self) -> str:
        return self._primary.name

    @property
    def model(self) -> str:
        return self._primary.model

    async def analyze(self, request: AnalysisRequest) -> AnalysisResponse:
        started = time.perf_counter()
        warnings: list[str] = []
        content = request.content
        truncated = len(content) > self._settings.max_input_chars
        if truncated:
            content = content[-self._settings.max_input_chars :]
            warnings.append("input_truncated")

        redacted = redact(content)
        findings = heuristics.analyze(redacted.text, language=request.language, environment=request.context.environment)
        excerpt, excerpted = heuristics.error_dense_excerpt(redacted.text, self._settings.max_prompt_chars)
        if excerpted:
            truncated = True
            warnings.append("input_excerpted")

        draft, provider = await self._call(lambda p: p.analyze(request, excerpt, findings), warnings)

        # Facts must be verifiable: drop any whose quoted evidence is not present in the (redacted) input.
        haystack = _squash(redacted.text)
        facts: list[Fact] = []
        dropped = 0
        for fact in draft.facts:
            if fact.evidence and _squash(fact.evidence) not in haystack:
                dropped += 1
                continue
            facts.append(fact)
        if dropped:
            warnings.append(f"unverified_facts_dropped:{dropped}")

        recommendations = [
            Recommendation(
                action=r.action,
                rationale=r.rationale,
                requires_approval=r.requires_approval or bool(_RISKY_ACTION.search(r.action)),
            )
            for r in draft.recommendations
        ]

        response = AnalysisResponse(
            request_id=request.request_id,
            provider=provider.name,
            model=provider.model,
            summary=draft.summary,
            classification=draft.classification,
            facts=facts,
            hypotheses=draft.hypotheses,
            recommendations=recommendations,
            patterns=findings.patterns,
            redactions=RedactionReport(count=redacted.count, kinds=dict(redacted.kinds)),
            truncated=truncated,
            latency_ms=int((time.perf_counter() - started) * 1000),
            warnings=warnings + draft.warnings,
        )
        logger.info(
            "analysis completed",
            extra={
                "request_id": request.request_id,
                "kind": request.kind.value,
                "provider": provider.name,
                "category": response.classification.category.value,
                "input_chars": len(request.content),
                "redactions": redacted.count,
                "latency_ms": response.latency_ms,
            },
        )
        return response

    async def answer(self, request: AssistantRequest) -> AssistantResponse:
        started = time.perf_counter()
        warnings: list[str] = []
        question = redact(request.question)
        total_redactions = question.kinds.copy()
        documents: list[ContextDocument] = []
        for doc in request.documents:
            content = redact(doc.content)
            title = redact(doc.title)
            total_redactions.update(content.kinds)
            total_redactions.update(title.kinds)
            documents.append(ContextDocument(type=doc.type, id=doc.id, title=title.text, content=content.text))

        draft, provider = await self._call(
            lambda p: p.answer(question.text, request.organization_name, documents, request.language), warnings
        )
        by_id = {d.id: d for d in documents}
        unknown = [cid for cid in draft.citation_ids if cid not in by_id]
        if unknown:
            warnings.append(f"unknown_citations_dropped:{len(unknown)}")
        citations = [Citation(id=d.id, type=d.type, title=d.title) for cid in dict.fromkeys(draft.citation_ids) if (d := by_id.get(cid))]
        return AssistantResponse(
            request_id=request.request_id,
            provider=provider.name,
            model=provider.model,
            answer=draft.answer,
            citations=citations,
            grounded=draft.grounded and bool(citations),
            redactions=RedactionReport(count=sum(total_redactions.values()), kinds=dict(total_redactions)),
            latency_ms=int((time.perf_counter() - started) * 1000),
            warnings=warnings + draft.warnings,
        )

    async def summarize_activity(self, request: ActivitySummaryRequest) -> ActivitySummaryResponse:
        started = time.perf_counter()
        warnings: list[str] = []
        redacted_events = [e.model_copy(update={"summary": redact(e.summary).text}) for e in request.events]
        safe_request = request.model_copy(update={"events": redacted_events})
        draft, provider = await self._call(lambda p: p.summarize_activity(safe_request), warnings)
        return ActivitySummaryResponse(
            request_id=request.request_id,
            provider=provider.name,
            model=provider.model,
            summary=draft.summary,
            highlights=draft.highlights,
            risks=draft.risks,
            latency_ms=int((time.perf_counter() - started) * 1000),
            warnings=warnings + draft.warnings,
        )

    async def _call[T](self, operation: Callable[[Provider], Awaitable[T]], warnings: list[str]) -> tuple[T, Provider]:
        primary = self._primary
        try:
            result = await asyncio.wait_for(operation(primary), timeout=self._settings.provider_timeout_seconds)
            return result, primary
        except TimeoutError as exc:
            error: ProviderError = ProviderTimeout("The AI provider did not answer in time.")
            error.__cause__ = exc
        except ProviderError as exc:
            error = exc

        logger.warning("provider call failed", extra={"provider": primary.name, "code": error.code, "detail": str(error)})
        if primary is self._fallback or not self._settings.fallback_to_heuristic:
            raise error
        warnings.append(f"provider_fallback:{error.code}")
        return await operation(self._fallback), self._fallback
