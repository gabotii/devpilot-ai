"""Writes the AI service contract consumed by the .NET backend: OpenAPI document + example payloads.

Run from src/python-ai-service:  .venv/Scripts/python -m scripts.export_contract
The test suite fails if the committed files drift from what this produces.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from fastapi.testclient import TestClient
from pydantic import SecretStr

from app.config import Settings
from app.main import create_app
from app.providers.heuristic import HeuristicProvider

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts"
KEY = "contract-export-key-0000000000000000"

SAMPLE_ANALYSIS = {
    "requestId": "fixture-analysis",
    "kind": "pipeline_failure",
    "content": "\n".join(
        [
            "2026-10-11T10:00:00Z Run dotnet test",
            "2026-10-11T10:00:05Z ERROR Npgsql.NpgsqlException: Failed to connect to 10.0.0.5:5432",
            "2026-10-11T10:00:05Z ---> System.Net.Sockets.SocketException (10061): Connection refused",
            "2026-10-11T10:00:06Z ERROR Npgsql.NpgsqlException: Failed to connect to 10.0.0.5:5432",
            "Error: Process completed with exit code 1.",
        ]
    ),
    "context": {"projectName": "Checkout", "environment": "ci", "pipelineName": "ci.yml", "branch": "main"},
}
SAMPLE_QUESTION = {
    "requestId": "fixture-assistant",
    "question": "¿Qué incidentes de checkout hay abiertos?",
    "organizationName": "Acme",
    "documents": [
        {"type": "incident", "id": "inc-1", "title": "Checkout timeouts", "content": "Open. Payment gateway timing out after deploy."},
        {"type": "project", "id": "p-1", "title": "Mobile", "content": "iOS app"},
    ],
}
SAMPLE_ACTIVITY = {
    "requestId": "fixture-activity",
    "projectName": "Checkout",
    "periodDays": 7,
    "events": [{"at": "2026-10-10T10:00:00Z", "actor": "Ana", "summary": "completó PAY-1: Fix login"}],
    "stats": {"overdue": 1},
}


def _stable(payload: dict[str, Any]) -> dict[str, Any]:
    return {**payload, "latencyMs": 0}


def build() -> dict[str, str]:
    app = create_app(Settings(environment="test", internal_api_key=SecretStr(KEY), provider="heuristic"), HeuristicProvider())
    client = TestClient(app)
    headers = {"X-Internal-Api-Key": KEY}
    outputs = {
        "ai-service.openapi.json": app.openapi(),
        "fixtures/analysis-request.json": SAMPLE_ANALYSIS,
        "fixtures/analysis-response.json": _stable(client.post("/v1/analyses", json=SAMPLE_ANALYSIS, headers=headers).json()),
        "fixtures/assistant-request.json": SAMPLE_QUESTION,
        "fixtures/assistant-response.json": _stable(client.post("/v1/assistant/answers", json=SAMPLE_QUESTION, headers=headers).json()),
        "fixtures/activity-request.json": SAMPLE_ACTIVITY,
        "fixtures/activity-response.json": _stable(client.post("/v1/summaries/activity", json=SAMPLE_ACTIVITY, headers=headers).json()),
        "fixtures/error-response.json": client.post("/v1/analyses", json=SAMPLE_ANALYSIS).json(),
    }
    return {name: json.dumps(value, indent=2, ensure_ascii=False, sort_keys=True) + "\n" for name, value in outputs.items()}


def main() -> None:
    for name, text in build().items():
        target = CONTRACTS / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {target}")


if __name__ == "__main__":
    main()
