"""Deterministic log analysis: rule catalog, fact extraction and frequent-pattern detection.

This engine never invents information: facts are extracted verbatim from the input, hypotheses come from explicit
signature rules and carry a confidence derived from how much evidence matched. It is the development/fallback
provider and also pre-processes input for LLM providers (error-dense excerpt selection).
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass

from app.schemas import ErrorCategory, Fact, Hypothesis, Pattern, Recommendation, Severity


@dataclass(frozen=True)
class Rule:
    key: str
    category: ErrorCategory
    pattern: re.Pattern[str]
    weight: float
    hypothesis_es: str
    hypothesis_en: str
    recommendations: tuple[tuple[str, str, bool], ...]  # (action_es, rationale_es, requires_approval)
    severity: Severity = Severity.MEDIUM


def _r(expr: str) -> re.Pattern[str]:
    return re.compile(expr, re.IGNORECASE)


RULES: tuple[Rule, ...] = (
    Rule(
        "oom",
        ErrorCategory.RESOURCE_EXHAUSTION,
        _r(r"out ?of ?memory|OutOfMemoryError|OOMKilled|heap out of memory|exit code 137|std::bad_alloc|MemoryError"),
        3.0,
        "El proceso se quedó sin memoria (OOM) y fue terminado.",
        "The process ran out of memory (OOM) and was killed.",
        (
            (
                "Revisar el consumo de memoria del proceso (perfilado o métricas del contenedor) antes de cambiar límites.",
                "Confirma si es una fuga o un pico legítimo.",
                False,
            ),
            (
                "Aumentar temporalmente el límite de memoria del contenedor/runner.",
                "Mitiga mientras se investiga; cambia infraestructura.",
                True,
            ),
        ),
        Severity.HIGH,
    ),
    Rule(
        "disk_full",
        ErrorCategory.RESOURCE_EXHAUSTION,
        _r(r"no space left on device|disk quota exceeded|ENOSPC"),
        3.0,
        "El disco del entorno de ejecución está lleno.",
        "The execution environment ran out of disk space.",
        (
            (
                "Liberar espacio (cachés, artefactos, imágenes antiguas) en el runner o volumen afectado.",
                "Sin espacio libre fallan builds, logs y bases de datos.",
                True,
            ),
        ),
        Severity.HIGH,
    ),
    Rule(
        "connection_refused",
        ErrorCategory.NETWORK,
        _r(r"ECONNREFUSED|connection refused|could not connect to|failed to connect|actively refused|No connection could be made"),
        2.0,
        "Un servicio dependiente no aceptaba conexiones (caído, puerto incorrecto o aún arrancando).",
        "A dependency was not accepting connections (down, wrong port, or still starting).",
        (
            (
                "Verificar que el servicio destino está levantado y escuchando en el host/puerto configurado.",
                "La conexión fue rechazada activamente.",
                False,
            ),
            (
                "Añadir espera/health check de dependencias antes de arrancar o ejecutar tests.",
                "Evita carreras de arranque en pipelines y compose.",
                False,
            ),
        ),
    ),
    Rule(
        "timeout",
        ErrorCategory.NETWORK,
        _r(r"timed? ?out|timeout expired|deadline exceeded|ETIMEDOUT|TaskCanceledException|504 Gateway"),
        1.5,
        "Una operación de red o dependencia superó su tiempo límite.",
        "A network or dependency operation exceeded its time limit.",
        (
            (
                "Identificar qué llamada expiró y medir su latencia real antes de ajustar timeouts.",
                "Subir timeouts sin causa raíz oculta el problema.",
                False,
            ),
        ),
    ),
    Rule(
        "dns",
        ErrorCategory.INFRASTRUCTURE,
        _r(r"name or service not known|getaddrinfo|ENOTFOUND|could not resolve host|NXDOMAIN|No such host"),
        2.5,
        "Falla la resolución DNS de un host.",
        "DNS resolution failed for a host.",
        (
            (
                "Comprobar el nombre de host configurado y la resolución DNS desde el entorno afectado.",
                "Un error tipográfico o una red distinta suele causar este fallo.",
                False,
            ),
        ),
    ),
    Rule(
        "auth",
        ErrorCategory.AUTHENTICATION,
        _r(
            r"\b401\b|unauthori[sz]ed|authentication failed|invalid credentials|permission denied|\b403\b|forbidden|access denied|bad credentials"
        ),
        1.5,
        "Credenciales inválidas, caducadas o sin permisos suficientes.",
        "Credentials are invalid, expired or lack permissions.",
        (
            (
                "Verificar que el secreto usado existe, no ha caducado y tiene los permisos mínimos necesarios.",
                "Rotar el secreto si pudo exponerse.",
                True,
            ),
        ),
    ),
    Rule(
        "missing_dependency",
        ErrorCategory.DEPENDENCY,
        _r(
            r"ModuleNotFoundError|No module named|Cannot find module|Could not resolve dependency|ERESOLVE|NU1101|package .* not found|unable to resolve|ClassNotFoundException|could not load file or assembly"
        ),
        2.5,
        "Falta una dependencia o no se pudo resolver su versión.",
        "A dependency is missing or its version could not be resolved.",
        (
            (
                "Restaurar dependencias desde el lockfile y comprobar que la versión requerida existe en el registro.",
                "Los lockfiles evitan resoluciones distintas entre entornos.",
                False,
            ),
        ),
    ),
    Rule(
        "null_reference",
        ErrorCategory.CODE_DEFECT,
        _r(
            r"NullReferenceException|NullPointerException|TypeError: Cannot read propert|undefined is not|AttributeError: 'NoneType'|nil pointer dereference"
        ),
        2.5,
        "Un valor nulo/indefinido se usó sin comprobarlo (defecto de código).",
        "A null/undefined value was dereferenced (code defect).",
        (
            (
                "Localizar la línea del primer frame de la aplicación en el stack trace y añadir el caso nulo a un test.",
                "Reproducir con un test evita regresiones.",
                False,
            ),
        ),
        Severity.HIGH,
    ),
    Rule(
        "code_exception",
        ErrorCategory.CODE_DEFECT,
        _r(r"KeyError|IndexError|ValueError|InvalidOperationException|ArgumentException|IllegalStateException|unhandled exception|panic:"),
        1.0,
        "Una excepción no controlada en el código de la aplicación.",
        "An unhandled exception in application code.",
        (
            (
                "Revisar el primer frame propio del stack trace y el input que la provocó.",
                "Las excepciones no controladas suelen indicar validación ausente.",
                False,
            ),
        ),
    ),
    Rule(
        "test_failure",
        ErrorCategory.TEST_FAILURE,
        _r(r"AssertionError|assert(?:ion)? failed|Tests? failed|\bFAILED\b|expected .* (?:but|received|got)|\d+ failed"),
        2.0,
        "Fallaron una o más pruebas automatizadas.",
        "One or more automated tests failed.",
        (
            (
                "Ejecutar localmente la prueba fallida indicada y comparar valor esperado y obtenido.",
                "Distingue entre regresión real y test inestable.",
                False,
            ),
        ),
    ),
    Rule(
        "build_failure",
        ErrorCategory.BUILD_FAILURE,
        _r(r"error CS\d{4}|error TS\d{4}|SyntaxError|compilation failed|Build FAILED|error: could not compile|tsc.*error"),
        2.5,
        "La compilación falló por un error de código o tipos.",
        "Compilation failed due to a code or type error.",
        (
            (
                "Corregir el primer error de compilación reportado; los siguientes suelen ser consecuencia.",
                "Los compiladores encadenan errores derivados.",
                False,
            ),
        ),
    ),
    Rule(
        "configuration",
        ErrorCategory.CONFIGURATION,
        _r(
            r"environment variable .* (?:not set|missing)|missing (?:required )?(?:config|setting|environment)|is not configured|ConfigurationException|KeyError: '[A-Z_]{3,}'|invalid configuration"
        ),
        2.5,
        "Falta o es inválida una configuración/variable de entorno.",
        "A configuration value or environment variable is missing or invalid.",
        (
            (
                "Comparar la configuración del entorno afectado con la documentada (.env.example) y añadir el valor faltante.",
                "Cambiar configuración de un entorno desplegado requiere aprobación.",
                True,
            ),
        ),
    ),
    Rule(
        "database",
        ErrorCategory.DATA,
        _r(
            r"relation .* does not exist|deadlock detected|duplicate key value|violates (?:foreign key|unique) constraint|migration .* failed|SqlException|Npgsql|could not serialize access"
        ),
        2.0,
        "Error de base de datos (esquema, migración o restricción).",
        "Database error (schema, migration or constraint).",
        (
            (
                "Verificar que las migraciones del entorno están al día antes de reintentar.",
                "Aplicar migraciones en un entorno real requiere aprobación y backup.",
                True,
            ),
        ),
        Severity.HIGH,
    ),
    Rule(
        "upstream_5xx",
        ErrorCategory.INFRASTRUCTURE,
        _r(r"\b50[23]\b|service unavailable|bad gateway|upstream connect error"),
        1.5,
        "Un servicio upstream respondió con error 5xx (no disponible).",
        "An upstream service returned a 5xx error (unavailable).",
        (
            (
                "Revisar el estado y los logs del servicio upstream en el mismo intervalo de tiempo.",
                "El fallo puede originarse fuera de este componente.",
                False,
            ),
        ),
    ),
)

_ERROR_LINE = _r(r"\b(error|fatal|exception|traceback|panic|failed|failure|critical)\b")
_STACK_FRAME = re.compile(r"^\s+(at\s+\S+|File \".+\", line \d+)|^\s+at .+\(.+:\d+:\d+\)")
_EXCEPTION_TYPE = re.compile(r"\b([A-Z][A-Za-z0-9]*(?:Exception|Error))\b")
_EXIT_CODE = re.compile(r"(?:exit(?:ed)? (?:with )?code|exit status|Process completed with exit code)\s*[:=]?\s*(-?\d+)", re.IGNORECASE)

_NORMALIZERS: tuple[tuple[re.Pattern[str], str], ...] = (
    (re.compile(r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+\-]\d{2}:?\d{2})?"), "<ts>"),
    (re.compile(r"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", re.I), "<uuid>"),
    (re.compile(r"\b0x[0-9a-f]+\b|\b[0-9a-f]{12,}\b", re.I), "<hex>"),
    (re.compile(r"(?:[A-Za-z]:)?(?:[\\/][\w.\-]+){2,}"), "<path>"),
    (re.compile(r"\b\d+(?:\.\d+)*\b"), "<n>"),
)


def normalize_line(line: str) -> str:
    signature = line.strip()
    for pattern, token in _NORMALIZERS:
        signature = pattern.sub(token, signature)
    return re.sub(r"\s+", " ", signature)[:200]


@dataclass
class HeuristicFindings:
    category: ErrorCategory
    severity: Severity
    confidence: float | None
    facts: list[Fact]
    hypotheses: list[Hypothesis]
    recommendations: list[Recommendation]
    patterns: list[Pattern]
    error_line_count: int
    matched_rules: list[str]


def analyze(text: str, *, language: str = "es", environment: str | None = None) -> HeuristicFindings:
    lines = text.splitlines()
    error_lines = [(i + 1, line) for i, line in enumerate(lines) if _ERROR_LINE.search(line) and not _STACK_FRAME.match(line)]

    scores: Counter[str] = Counter()
    first_match: dict[str, tuple[int, str]] = {}
    for number, line in enumerate(lines, start=1):
        for rule in RULES:
            if rule.pattern.search(line):
                scores[rule.key] += 1
                first_match.setdefault(rule.key, (number, line.strip()))

    ranked = sorted(
        (rule for rule in RULES if scores[rule.key]),
        key=lambda r: (r.weight * min(scores[r.key], 5), r.weight),
        reverse=True,
    )

    facts = _facts(lines, error_lines, first_match, ranked, language)
    hypotheses: list[Hypothesis] = []
    for rule in ranked[:3]:
        hits = scores[rule.key]
        line_no, _sample = first_match[rule.key]
        strength = rule.weight * min(hits, 5)
        rule_confidence = round(min(0.9, 0.25 + strength / 20), 2)
        hypotheses.append(
            Hypothesis(
                statement=rule.hypothesis_es if language == "es" else rule.hypothesis_en,
                rationale=(
                    f"Coincidencia con la firma '{rule.key}' en {hits} línea(s); primera en la línea {line_no}."
                    if language == "es"
                    else f"Matched signature '{rule.key}' on {hits} line(s); first at line {line_no}."
                ),
                confidence=rule_confidence,
            )
        )

    recommendations: list[Recommendation] = []
    seen: set[str] = set()
    for rule in ranked[:3]:
        for action, rationale, approval in rule.recommendations:
            if action not in seen:
                seen.add(action)
                recommendations.append(Recommendation(action=action, rationale=rationale, requires_approval=approval))
    if not recommendations:
        recommendations.append(
            Recommendation(
                action="Ampliar el log alrededor del primer error (nivel DEBUG) y reproducir el fallo en un entorno controlado."
                if language == "es"
                else "Capture more log context around the first error (DEBUG) and reproduce in a controlled environment.",
                rationale="No se identificó una firma conocida en los datos."
                if language == "es"
                else "No known signature matched the data.",
                requires_approval=False,
            )
        )

    top = ranked[0] if ranked else None
    category = top.category if top else ErrorCategory.UNKNOWN
    severity = _severity(top, error_lines, environment)
    confidence = hypotheses[0].confidence if hypotheses else None
    return HeuristicFindings(
        category=category,
        severity=severity,
        confidence=confidence,
        facts=facts,
        hypotheses=hypotheses,
        recommendations=recommendations,
        patterns=frequent_patterns(error_lines),
        error_line_count=len(error_lines),
        matched_rules=[r.key for r in ranked],
    )


def frequent_patterns(error_lines: list[tuple[int, str]], limit: int = 5) -> list[Pattern]:
    counts: Counter[str] = Counter()
    examples: dict[str, str] = {}
    for _, line in error_lines:
        signature = normalize_line(line)
        if not signature:
            continue
        counts[signature] += 1
        examples.setdefault(signature, line.strip()[:300])
    return [Pattern(signature=s, occurrences=n, example=examples[s]) for s, n in counts.most_common(limit)]


def error_dense_excerpt(text: str, max_chars: int) -> tuple[str, bool]:
    """Selects the most informative part of a long log: windows around error lines plus the tail.

    When the budget is exceeded, later lines win (the final failure is usually at the end). Gaps are marked "[...]".
    """
    if len(text) <= max_chars:
        return text, False
    lines = text.splitlines()
    keep: set[int] = set(range(max(0, len(lines) - 60), len(lines)))
    for i, line in enumerate(lines):
        if _ERROR_LINE.search(line):
            keep.update(range(max(0, i - 3), min(len(lines), i + 8)))

    chosen: list[int] = []
    size = 0
    for i in sorted(keep, reverse=True):
        cost = min(len(lines[i]), 2000) + 1
        if size + cost > max_chars:
            break
        chosen.append(i)
        size += cost

    out: list[str] = []
    previous: int | None = None
    for i in sorted(chosen):
        if (previous is None and i > 0) or (previous is not None and i - previous > 1):
            out.append("[...]")
        out.append(lines[i][:2000])
        previous = i
    return "\n".join(out), True


def _facts(
    lines: list[str],
    error_lines: list[tuple[int, str]],
    first_match: dict[str, tuple[int, str]],
    ranked: list[Rule],
    language: str,
) -> list[Fact]:
    es = language == "es"
    facts: list[Fact] = [
        Fact(
            statement=(
                f"El log tiene {len(lines)} líneas, {len(error_lines)} con indicadores de error."
                if es
                else f"The log has {len(lines)} lines, {len(error_lines)} with error indicators."
            ),
        )
    ]
    if error_lines:
        number, line = error_lines[0]
        facts.append(
            Fact(statement="Primer error registrado." if es else "First recorded error.", evidence=line.strip()[:500], line=number)
        )
        last_number, last_line = error_lines[-1]
        if last_number != number:
            facts.append(
                Fact(
                    statement="Último error registrado." if es else "Last recorded error.",
                    evidence=last_line.strip()[:500],
                    line=last_number,
                )
            )

    exception_types = Counter(m.group(1) for line in lines for m in _EXCEPTION_TYPE.finditer(line))
    if exception_types:
        listed = ", ".join(f"{name} ×{count}" for name, count in exception_types.most_common(5))
        facts.append(Fact(statement=(f"Tipos de excepción presentes: {listed}." if es else f"Exception types present: {listed}.")))

    for line_number, line in enumerate(lines, start=1):
        exit_match = _EXIT_CODE.search(line)
        if exit_match:
            code = exit_match.group(1)
            facts.append(
                Fact(
                    statement=(f"El proceso terminó con código de salida {code}." if es else f"The process exited with code {code}."),
                    evidence=line.strip()[:300],
                    line=line_number,
                )
            )
            break

    for rule in ranked[:3]:
        line_number, line = first_match[rule.key]
        facts.append(
            Fact(
                statement=(f"Se detectó la firma '{rule.key}'." if es else f"Signature '{rule.key}' detected."),
                evidence=line[:500],
                line=line_number,
            )
        )
    return facts


def _severity(top: Rule | None, error_lines: list[tuple[int, str]], environment: str | None) -> Severity:
    order = [Severity.LOW, Severity.MEDIUM, Severity.HIGH, Severity.CRITICAL]
    base = (Severity.MEDIUM if error_lines else Severity.LOW) if top is None else top.severity
    if any(re.search(r"\b(fatal|panic|critical)\b", line, re.I) for _, line in error_lines):
        base = max(base, Severity.HIGH, key=order.index)
    if environment and environment.lower() in {"prod", "production", "produccion", "producción"}:
        base = order[min(order.index(base) + 1, len(order) - 1)]
    return base
