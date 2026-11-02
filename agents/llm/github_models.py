"""GitHub Models: OpenAI-compatible chat completions, authenticated with GITHUB_TOKEN."""

import os
import time

import httpx

from agents.llm.base import LLMError, Provider, QuotaError, Result

URL = "https://models.github.ai/inference/chat/completions"


def _error_text(response):
    try:
        err = response.json().get("error", {})
        return err.get("message") or response.text
    except ValueError:
        return response.text


def _is_daily_limit(response, message):
    retry_after = float(response.headers.get("retry-after") or 0)
    return "86400" in message or "ByDay" in message or retry_after > 3600


class GitHubModels(Provider):
    name = "github_models"

    def __init__(self):
        self.models = {
            "strong": os.environ.get("GITHUB_MODELS_STRONG", "openai/gpt-4o"),
            "fast": os.environ.get("GITHUB_MODELS_FAST", "openai/gpt-4o-mini"),
        }
        self.token = os.environ.get("GITHUB_TOKEN", "").strip()
        if not self.token:
            raise LLMError("GITHUB_TOKEN is not set. Put it in .env.")

    def call(self, messages, model, json_mode, max_retries=5):
        body = {"model": model, "messages": messages, "temperature": 0}
        if json_mode:
            body["response_format"] = {"type": "json_object"}
        headers = {
            "Authorization": f"Bearer {self.token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        }
        delay = 5.0
        for attempt in range(max_retries + 1):
            try:
                response = httpx.post(URL, json=body, headers=headers, timeout=180)
            except httpx.HTTPError as exc:
                if attempt == max_retries:
                    raise LLMError(f"GitHub Models network error: {exc.__class__.__name__}") from None
                time.sleep(delay)
                delay = min(delay * 2, 60)
                continue

            if response.status_code == 429:
                message = _error_text(response)
                if _is_daily_limit(response, message):
                    raise QuotaError(
                        f"GitHub Models daily quota reached for {model}. Wait until tomorrow, "
                        f"rerun with the cache on, or switch LLM_PROVIDER. ({message})"
                    )
                wait = float(response.headers.get("retry-after") or delay)
                if attempt == max_retries or wait > 120:
                    raise QuotaError(f"GitHub Models rate limit for {model}, retry after {wait:.0f}s. ({message})")
                time.sleep(wait)
                delay = min(delay * 2, 60)
                continue
            if response.status_code >= 500 and attempt < max_retries:
                time.sleep(delay)
                delay = min(delay * 2, 60)
                continue
            if response.status_code in (401, 403):
                raise LLMError(
                    f"GitHub Models refused the token (HTTP {response.status_code}). "
                    f"It needs the models:read permission. ({_error_text(response)})"
                )
            if response.status_code != 200:
                raise LLMError(f"GitHub Models HTTP {response.status_code}: {_error_text(response)[:300]}")

            if "json" not in response.headers.get("content-type", ""):
                raise LLMError(
                    f"GitHub Models sent a non-JSON reply ({response.text.strip()[:40]!r}). The GitHub Models "
                    "API was retired on 2026-07-30, so this provider no longer works. Switch LLM_PROVIDER."
                )
            data = response.json()
            usage = data.get("usage") or {}
            return Result(
                text=data["choices"][0]["message"]["content"] or "",
                prompt_tokens=usage.get("prompt_tokens", 0),
                completion_tokens=usage.get("completion_tokens", 0),
            )
        raise LLMError("GitHub Models: no response after retries")
