"""Service configuration (environment variables, prefix AI_)."""

from __future__ import annotations

from functools import lru_cache
from typing import Literal

from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="AI_", env_file=".env", extra="ignore")

    environment: Literal["development", "test", "production"] = "development"

    # Shared secret the .NET backend sends in X-Internal-Api-Key. Required outside development.
    internal_api_key: SecretStr | None = None

    # "auto" uses Anthropic when an API key is configured, otherwise the deterministic heuristic provider.
    provider: Literal["auto", "heuristic", "anthropic"] = "auto"
    anthropic_api_key: SecretStr | None = Field(default=None, validation_alias="ANTHROPIC_API_KEY")
    anthropic_model: str = "claude-opus-5"
    anthropic_effort: Literal["low", "medium", "high", "xhigh", "max"] = "high"
    anthropic_server_fallbacks: bool = True
    anthropic_max_tokens: int = 16000

    # When the external provider fails, answer with the heuristic engine (labelled as such) instead of erroring.
    fallback_to_heuristic: bool = True

    provider_timeout_seconds: float = Field(default=60.0, gt=0, le=600)
    max_input_chars: int = Field(default=200_000, gt=1000)
    max_prompt_chars: int = Field(default=40_000, gt=1000)

    log_level: str = "INFO"

    def require_secure(self) -> None:
        if self.environment == "production" and (self.internal_api_key is None or len(self.internal_api_key.get_secret_value()) < 32):
            raise RuntimeError("AI_INTERNAL_API_KEY must be set to at least 32 characters in production.")


@lru_cache
def get_settings() -> Settings:
    return Settings()
