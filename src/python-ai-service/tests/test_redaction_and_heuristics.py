from __future__ import annotations

import pytest

from app.analysis.heuristics import analyze, error_dense_excerpt, normalize_line
from app.redaction import redact
from app.schemas import ErrorCategory, Severity

SECRETS = {
    "github_token": "token ghp_" + "a" * 36,
    "aws_access_key": "AKIAABCDEFGHIJKLMNOP",
    "anthropic_key": "sk-ant-api03-" + "x" * 30,
    "jwt": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
    "bearer": "Authorization: Bearer abcdefghijklmnop12345",
    "url_credentials": "postgres://admin:hunter2@db.internal:5432/app",
    "connection_string_password": "Host=db;Username=app;Password=Sup3rS3cret!;",
    "secret_assignment": "STRIPE_SECRET_KEY=sk_live_51abcdefghijk",
    "email": "contact ana.lopez@example.com now",
    "private_key": "-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----",
    "devpilot_key": "dpk_live_ABCDEFGHIJKLMNOPQRSTUV",
}


@pytest.mark.parametrize(("kind", "text"), list(SECRETS.items()))
def test_redacts_known_secret_formats(kind: str, text: str) -> None:
    result = redact(text)
    assert result.count >= 1, kind
    for fragment in (
        "ghp_aaaa",
        "AKIAABCD",
        "sk-ant-api03",
        "dozjgNry",
        "abcdefghijklmnop12345",
        "hunter2",
        "Sup3rS3cret",
        "sk_live_51",
        "ana.lopez",
        "MIIEow",
    ):
        assert fragment not in result.text


def test_credit_cards_require_luhn() -> None:
    assert redact("card 4111 1111 1111 1111 used").kinds["card_number"] == 1
    plain = "build 1234567890123 finished at 20261011123456"
    assert redact(plain).text == plain


def test_ordinary_logs_are_untouched() -> None:
    log = "2026-10-11T10:00:00Z ERROR OrderService failed: connection refused to 10.0.0.5:5432 (attempt 3)"
    assert redact(log).text == log


def test_oom_is_classified_as_resource_exhaustion() -> None:
    log = "\n".join(
        ["Starting worker", "Processing batch 1", "FATAL: JavaScript heap out of memory", "Process completed with exit code 137"]
    )
    findings = analyze(log)
    assert findings.category == ErrorCategory.RESOURCE_EXHAUSTION
    assert findings.severity in {Severity.HIGH, Severity.CRITICAL}
    assert findings.confidence is not None
    assert 0 < findings.confidence <= 0.9
    statements = " ".join(f.statement for f in findings.facts)
    assert "137" in statements
    assert any(r.requires_approval for r in findings.recommendations)


def test_connection_refused_and_patterns() -> None:
    lines = [f"2026-10-11T10:00:0{i}Z ERROR Npgsql: connection refused to 10.0.0.{i}:5432 attempt {i}" for i in range(5)]
    findings = analyze("\n".join(lines))
    assert findings.category == ErrorCategory.NETWORK
    assert findings.patterns[0].occurrences == 5
    assert "<ts>" in findings.patterns[0].signature


def test_test_and_build_failures() -> None:
    assert analyze("FAILED tests/test_api.py::test_login - AssertionError: expected 200 but got 500").category == ErrorCategory.TEST_FAILURE
    assert analyze("src/app.ts(3,7): error TS2322: Type 'string' is not assignable").category == ErrorCategory.BUILD_FAILURE


def test_unknown_input_has_no_confidence() -> None:
    findings = analyze("all good\nnothing to see")
    assert findings.category == ErrorCategory.UNKNOWN
    assert findings.confidence is None
    assert findings.hypotheses == []
    assert findings.recommendations, "a generic diagnostic step is always suggested"


def test_production_bumps_severity() -> None:
    log = "ERROR request failed: connection refused"
    assert analyze(log, environment="production").severity == Severity.HIGH
    assert analyze(log, environment="staging").severity == Severity.MEDIUM


def test_english_output() -> None:
    findings = analyze("ERROR connection refused", language="en")
    assert findings.hypotheses[0].statement.startswith("A dependency")


def test_normalize_line() -> None:
    assert normalize_line("2026-10-11T10:00:01Z id=3f2b8c1e-1111-2222-3333-444455556666 took 35 ms at /var/app/x.py") == (
        "<ts> id=<uuid> took <n> ms at <path>"
    )


def test_error_dense_excerpt_prefers_errors_and_tail() -> None:
    noise = [f"info line {i}" for i in range(5000)]
    noise[1000] = "ERROR something broke here"
    text = "\n".join([*noise, "FATAL final failure"])
    excerpt, cut = error_dense_excerpt(text, max_chars=3000)
    assert cut
    assert len(excerpt) <= 3000 + 200
    assert "FATAL final failure" in excerpt
    assert "[...]" in excerpt
    short, cut_short = error_dense_excerpt("tiny", 3000)
    assert (short, cut_short) == ("tiny", False)
