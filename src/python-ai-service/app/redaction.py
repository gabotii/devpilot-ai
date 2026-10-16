"""Secret and personal-data redaction applied to every input before analysis or any external call.

Redaction is best-effort pattern matching: it removes the common credential formats and PII, but it cannot guarantee
that arbitrary secrets are detected. The backend additionally avoids sending data the user has not asked to analyze.
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass, field

# Order matters: specific token formats first, generic key=value last.
_RULES: list[tuple[str, re.Pattern[str], str]] = [
    ("private_key", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", re.S), "[REDACTED_PRIVATE_KEY]"),
    ("anthropic_key", re.compile(r"\bsk-ant-[A-Za-z0-9_\-]{20,}"), "[REDACTED_API_KEY]"),
    ("openai_key", re.compile(r"\bsk-(?:proj-)?[A-Za-z0-9_\-]{20,}"), "[REDACTED_API_KEY]"),
    ("github_token", re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})"), "[REDACTED_GITHUB_TOKEN]"),
    ("aws_access_key", re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"), "[REDACTED_AWS_KEY]"),
    ("slack_token", re.compile(r"\bxox[abprs]-[A-Za-z0-9\-]{10,}"), "[REDACTED_SLACK_TOKEN]"),
    ("devpilot_key", re.compile(r"\bdpk_[A-Za-z0-9_\-]{20,}"), "[REDACTED_API_KEY]"),
    ("jwt", re.compile(r"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}"), "[REDACTED_JWT]"),
    ("bearer", re.compile(r"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{12,}"), r"\1 [REDACTED]"),
    ("url_credentials", re.compile(r"(?i)\b([a-z][a-z0-9+.\-]*://)[^\s/:@]+:[^\s/@]+@"), r"\1[REDACTED]@"),
    (
        "connection_string_password",
        re.compile(r"(?i)\b(password|pwd)\s*=\s*[^;\s\"']+"),
        r"\1=[REDACTED]",
    ),
    (
        "secret_assignment",
        re.compile(
            r"(?i)\b([A-Z0-9_\-]*(?:secret|token|api[_\-]?key|access[_\-]?key|passwd|password|credential)[A-Z0-9_\-]*)"
            r"(\s*[:=]\s*[\"']?)([^\s\"',;]{6,})"
        ),
        r"\1\2[REDACTED]",
    ),
    ("email", re.compile(r"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b"), "[EMAIL]"),
    ("card_number", re.compile(r"\b(?:\d[ \-]?){13,19}\b"), "[CARD]"),
]


@dataclass
class RedactionResult:
    text: str
    kinds: Counter[str] = field(default_factory=Counter)

    @property
    def count(self) -> int:
        return sum(self.kinds.values())


# Issuer prefixes of the major card networks (Visa, Mastercard incl. 2-series, Amex, Discover, Diners, JCB).
_CARD_PREFIX = re.compile(r"^(4|5[1-5]|2(22[1-9]|2[3-9]\d|[3-6]\d\d|7[01]\d|720)|3[47]|3(0[0-5]|[68])|6(011|5)|35)")


def _luhn_valid(candidate: str) -> bool:
    digits = [int(c) for c in candidate if c.isdigit()]
    if not 13 <= len(digits) <= 19 or not _CARD_PREFIX.match("".join(map(str, digits))):
        return False
    checksum = 0
    for i, digit in enumerate(reversed(digits)):
        if i % 2 == 1:
            digit *= 2
            if digit > 9:
                digit -= 9
        checksum += digit
    return checksum % 10 == 0


def redact(text: str) -> RedactionResult:
    result = RedactionResult(text=text)
    for kind, pattern, replacement in _RULES:
        if kind == "card_number":
            # Only card-like numbers that pass the Luhn check (avoids redacting timestamps, ids, counters).
            def _card(match: re.Match[str]) -> str:
                if _luhn_valid(match.group(0)):
                    result.kinds["card_number"] += 1
                    return "[CARD]"
                return match.group(0)

            result.text = pattern.sub(_card, result.text)
            continue

        new_text, count = pattern.subn(replacement, result.text)
        if count:
            result.kinds[kind] += count
            result.text = new_text
    return result
