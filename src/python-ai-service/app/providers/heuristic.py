"""Deterministic provider: rule-based analysis and keyword retrieval. No model, no network, reproducible output."""

from __future__ import annotations

import re
import unicodedata
from collections import Counter

from app.analysis.heuristics import HeuristicFindings
from app.providers.base import ActivityDraft, AnalysisDraft, AssistantDraft
from app.schemas import ActivitySummaryRequest, AnalysisKind, AnalysisRequest, Classification, ContextDocument

_CATEGORY_ES = {
    "dependency": "dependencias",
    "configuration": "configuración",
    "code_defect": "defecto de código",
    "infrastructure": "infraestructura",
    "network": "red",
    "authentication": "autenticación/permisos",
    "resource_exhaustion": "agotamiento de recursos",
    "test_failure": "pruebas fallidas",
    "build_failure": "compilación",
    "data": "base de datos",
    "unknown": "sin clasificar",
}

_STOPWORDS = set(
    [
        "el",
        "la",
        "los",
        "las",
        "un",
        "una",
        "unos",
        "unas",
        "de",
        "del",
        "al",
        "a",
        "en",
        "y",
        "o",
        "que",
        "qué",
        "cual",
        "cuál",
        "cuales",
        "cuáles",
        "como",
        "cómo",
        "es",
        "son",
        "esta",
        "este",
        "estos",
        "estas",
        "hay",
        "por",
        "para",
        "con",
        "sin",
        "se",
        "su",
        "sus",
        "mi",
        "mis",
        "me",
        "lo",
        "le",
        "les",
        "ya",
        "muy",
        "mas",
        "más",
        "ha",
        "han",
        "hemos",
        "tiene",
        "tienen",
        "sobre",
        "entre",
        "cuando",
        "donde",
        "the",
        "a",
        "an",
        "of",
        "in",
        "on",
        "and",
        "or",
        "what",
        "which",
        "how",
        "is",
        "are",
        "this",
        "that",
        "these",
        "those",
        "there",
        "for",
        "with",
        "without",
        "to",
        "my",
        "our",
        "is",
        "it",
        "be",
    ]
)


def _tokens(text: str) -> list[str]:
    plain = unicodedata.normalize("NFKD", text.lower())
    plain = "".join(c for c in plain if not unicodedata.combining(c))
    return [t for t in re.findall(r"[a-z0-9][a-z0-9_\-]{1,}", plain) if t not in _STOPWORDS]


class HeuristicProvider:
    name = "heuristic"
    model = "devpilot-rules-v1"

    async def analyze(self, request: AnalysisRequest, excerpt: str, findings: HeuristicFindings) -> AnalysisDraft:
        es = request.language == "es"
        category = findings.category.value
        subject = request.context.pipeline_name or request.context.title or request.context.project_name
        if findings.hypotheses:
            lead = findings.hypotheses[0].statement
            summary = (
                f"Análisis determinista{f' de {subject}' if subject else ''}: categoría probable «{_CATEGORY_ES[category]}». {lead} "
                f"Se encontraron {findings.error_line_count} líneas con indicadores de error."
                if es
                else f"Deterministic analysis{f' of {subject}' if subject else ''}: likely category '{category}'. {lead} "
                f"{findings.error_line_count} lines contain error indicators."
            )
        else:
            summary = (
                f"Análisis determinista: no se reconoció ninguna firma de error conocida en {findings.error_line_count} líneas con errores. "
                "Se requiere revisión manual."
                if es
                else f"Deterministic analysis: no known error signature matched ({findings.error_line_count} error lines). Manual review required."
            )
        if request.kind == AnalysisKind.PIPELINE_FAILURE and es:
            summary = "Fallo de pipeline. " + summary

        return AnalysisDraft(
            summary=summary,
            classification=Classification(category=findings.category, severity=findings.severity, confidence=findings.confidence),
            facts=findings.facts,
            hypotheses=findings.hypotheses,
            recommendations=findings.recommendations,
        )

    async def answer(self, question: str, organization: str, documents: list[ContextDocument], language: str) -> AssistantDraft:
        es = language == "es"
        query = Counter(_tokens(question))
        scored: list[tuple[float, ContextDocument]] = []
        for doc in documents:
            title_tokens = Counter(_tokens(doc.title))
            body_tokens = Counter(_tokens(doc.content))
            score = sum(min(count, 3) * (2.0 * title_tokens[t] + min(body_tokens[t], 3)) for t, count in query.items())
            if score > 0:
                scored.append((score, doc))
        scored.sort(key=lambda pair: pair[0], reverse=True)
        top = [doc for _, doc in scored[:5]]

        if not top:
            return AssistantDraft(
                answer=(
                    f"No encontré información relacionada con tu pregunta en los datos autorizados de {organization}. "
                    "Prueba con el nombre de un proyecto, una clave de tarea (p. ej. API-12) o un pipeline."
                    if es
                    else f"I found nothing related to your question in {organization}'s authorized data."
                ),
                citation_ids=[],
                grounded=False,
            )

        intro = (
            f"Modo determinista (sin modelo de IA): estos son los elementos de {organization} más relacionados con tu pregunta:"
            if es
            else f"Deterministic mode (no AI model): these {organization} items best match your question:"
        )
        bullets = []
        for doc in top:
            snippet = re.sub(r"\s+", " ", doc.content).strip()
            if len(snippet) > 220:
                snippet = snippet[:219] + "…"
            bullets.append(f"- [{doc.type.value}] {doc.title}: {snippet}")
        return AssistantDraft(answer=intro + "\n" + "\n".join(bullets), citation_ids=[d.id for d in top], grounded=True)

    async def summarize_activity(self, request: ActivitySummaryRequest) -> ActivityDraft:
        es = request.language == "es"
        verbs: Counter[str] = Counter()
        completed: list[str] = []
        for event in request.events:
            first = event.summary.split(" ", 1)[0].lower()
            verbs[first] += 1
            if first in {"completó", "completed"}:
                completed.append(event.summary.split(" ", 1)[1] if " " in event.summary else event.summary)

        top_actors = Counter(e.actor for e in request.events if e.actor).most_common(3)
        summary = (
            f"En los últimos {request.period_days} días hubo {len(request.events)} eventos en {request.project_name}: "
            f"{verbs.get('creó', 0)} creaciones, {verbs.get('completó', 0)} completadas, {verbs.get('movió', 0)} cambios de estado "
            f"y {verbs.get('comentó', 0)} comentarios."
            if es
            else f"In the last {request.period_days} days {request.project_name} had {len(request.events)} events."
        )
        if top_actors and es:
            summary += " Más activos: " + ", ".join(f"{a} ({n})" for a, n in top_actors) + "."

        risks: list[str] = []
        stats = request.stats
        if stats.get("overdue", 0) > 0:
            risks.append(f"{stats['overdue']} tarea(s) vencida(s)." if es else f"{stats['overdue']} overdue item(s).")
        if stats.get("openCritical", 0) > 0:
            risks.append(
                f"{stats['openCritical']} tarea(s) crítica(s) abierta(s)." if es else f"{stats['openCritical']} open critical item(s)."
            )
        if stats.get("failedPipelines", 0) > 0:
            risks.append(
                f"{stats['failedPipelines']} ejecución(es) de pipeline fallida(s)."
                if es
                else f"{stats['failedPipelines']} failed pipeline run(s)."
            )
        if not completed and request.events:
            risks.append("No se completó ninguna tarea en el periodo." if es else "No items were completed in the period.")
        return ActivityDraft(summary=summary, highlights=completed[:5], risks=risks)
