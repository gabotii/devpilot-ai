"""FastAPI entry point. Internal service: only the DevPilot backend calls it (shared-key auth, private network)."""

from __future__ import annotations

import hmac
import json
import logging
import sys
import time
import uuid
from collections.abc import AsyncIterator, Awaitable, Callable
from contextlib import asynccontextmanager
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response

from app.config import Settings, get_settings
from app.providers.base import Provider, ProviderError
from app.providers.heuristic import HeuristicProvider
from app.schemas import (
    ActivitySummaryRequest,
    ActivitySummaryResponse,
    AnalysisRequest,
    AnalysisResponse,
    AssistantRequest,
    AssistantResponse,
    ErrorBody,
    ErrorResponse,
    HealthResponse,
)
from app.service import AiService

_RESERVED = set(logging.LogRecord("", 0, "", 0, "", None, None).__dict__) | {"message", "asctime"}


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, Any] = {
            "ts": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        payload.update({k: v for k, v in record.__dict__.items() if k not in _RESERVED})
        if record.exc_info:
            payload["exception"] = self.formatException(record.exc_info)
        return json.dumps(payload, default=str, ensure_ascii=False)


def configure_logging(level: str) -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())
    root = logging.getLogger()
    root.handlers[:] = [handler]
    root.setLevel(level)


def build_provider(settings: Settings) -> Provider:
    wants_external = settings.provider == "anthropic" or (settings.provider == "auto" and settings.anthropic_api_key is not None)
    if wants_external:
        from app.providers.anthropic_provider import AnthropicProvider

        return AnthropicProvider(settings)
    return HeuristicProvider()


def create_app(settings: Settings | None = None, provider: Provider | None = None) -> FastAPI:
    settings = settings or get_settings()
    settings.require_secure()
    configure_logging(settings.log_level)
    service = AiService(settings, provider or build_provider(settings))
    logger = logging.getLogger("devpilot.ai")

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        logger.info(
            "ai service started", extra={"provider": service.provider_name, "model": service.model, "environment": settings.environment}
        )
        if settings.internal_api_key is None:
            logger.warning("AI_INTERNAL_API_KEY is not set: requests are accepted without authentication (development only)")
        yield

    is_prod = settings.environment == "production"
    app = FastAPI(
        title="DevPilot AI Service",
        version="1.0.0",
        description="Log analysis, incident summaries and an assistant over authorized project data. Internal API.",
        lifespan=lifespan,
        docs_url=None if is_prod else "/docs",
        redoc_url=None,
    )

    def require_internal_key(x_internal_api_key: Annotated[str | None, Header()] = None) -> None:
        expected = settings.internal_api_key
        if expected is None:
            return
        if x_internal_api_key is None or not hmac.compare_digest(x_internal_api_key.encode(), expected.get_secret_value().encode()):
            raise _Unauthorized()

    secured = [Depends(require_internal_key)]

    @app.middleware("http")
    async def request_context(request: Request, call_next: Callable[[Request], Awaitable[Response]]) -> Response:
        request_id = request.headers.get("x-request-id") or uuid.uuid4().hex
        started = time.perf_counter()
        response = await call_next(request)
        response.headers["X-Request-Id"] = request_id
        if not request.url.path.startswith("/health"):
            logger.info(
                "request",
                extra={
                    "request_id": request_id,
                    "method": request.method,
                    "path": request.url.path,
                    "status": response.status_code,
                    "duration_ms": int((time.perf_counter() - started) * 1000),
                },
            )
        return response

    @app.exception_handler(_Unauthorized)
    async def unauthorized(_: Request, __: _Unauthorized) -> JSONResponse:
        return _error(401, "unauthorized", "Missing or invalid internal API key.")

    @app.exception_handler(RequestValidationError)
    async def validation(_: Request, exc: RequestValidationError) -> JSONResponse:
        first = exc.errors()[0] if exc.errors() else {}
        location = ".".join(str(p) for p in first.get("loc", []) if p != "body")
        return _error(400, "validation", f"{location}: {first.get('msg', 'invalid request')}".strip(": "))

    @app.exception_handler(ProviderError)
    async def provider_error(_: Request, exc: ProviderError) -> JSONResponse:
        return _error(exc.status, exc.code, str(exc), exc.retry_after)

    @app.exception_handler(Exception)
    async def unexpected(_: Request, exc: Exception) -> JSONResponse:
        logger.exception("unhandled error", exc_info=exc)
        return _error(500, "internal", "Unexpected error.")

    @app.get("/health/live", response_model=HealthResponse, tags=["health"])
    async def live() -> HealthResponse:
        return _health()

    @app.get("/health/ready", response_model=HealthResponse, tags=["health"])
    async def ready() -> HealthResponse:
        return _health()

    def _health() -> HealthResponse:
        return HealthResponse(
            status="ok",
            provider=service.provider_name,
            model=service.model,
            external_provider_configured=settings.anthropic_api_key is not None,
        )

    error_responses: dict[int | str, dict[str, Any]] = {
        400: {"model": ErrorResponse},
        401: {"model": ErrorResponse},
        503: {"model": ErrorResponse},
        504: {"model": ErrorResponse},
    }

    @app.post(
        "/v1/analyses",
        response_model=AnalysisResponse,
        dependencies=secured,
        tags=["analysis"],
        responses=error_responses,
        response_model_by_alias=True,
    )
    async def analyze(request: AnalysisRequest) -> AnalysisResponse:
        return await service.analyze(request)

    @app.post(
        "/v1/assistant/answers",
        response_model=AssistantResponse,
        dependencies=secured,
        tags=["assistant"],
        responses=error_responses,
        response_model_by_alias=True,
    )
    async def answer(request: AssistantRequest) -> AssistantResponse:
        return await service.answer(request)

    @app.post(
        "/v1/summaries/activity",
        response_model=ActivitySummaryResponse,
        dependencies=secured,
        tags=["summaries"],
        responses=error_responses,
        response_model_by_alias=True,
    )
    async def activity_summary(request: ActivitySummaryRequest) -> ActivitySummaryResponse:
        return await service.summarize_activity(request)

    return app


class _Unauthorized(Exception):
    pass


def _error(status: int, code: str, message: str, retry_after: int | None = None) -> JSONResponse:
    body = ErrorResponse(error=ErrorBody(code=code, message=message, retry_after_seconds=retry_after))
    headers = {"Retry-After": str(retry_after)} if retry_after else None
    return JSONResponse(status_code=status, content=body.model_dump(by_alias=True), headers=headers)


def get_app() -> FastAPI:
    """Factory for `uvicorn app.main:get_app --factory`."""
    return create_app()
