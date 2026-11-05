"""Azure OpenAI chat completions (Azure AI Foundry resource, key authentication).

Two kinds of deployments are handled:
- reasoning models (gpt-5 family, o-series): no temperature, `max_completion_tokens`
  and an optional `reasoning_effort`
- normal chat models (gpt-4.1 and older): `temperature` 0

Settings in .env: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY,
AZURE_OPENAI_DEPLOYMENT (strong tier), AZURE_OPENAI_DEPLOYMENT_MINI (fast tier).
Optional: AZURE_OPENAI_API_VERSION, AZURE_OPENAI_REASONING_EFFORT (minimal|low|medium|high).
"""

import os
import re
import time

import httpx

from agents.llm.base import LLMError, Provider, QuotaError, Result

API_VERSION = "2025-04-01-preview"
MAX_OUTPUT_TOKENS = 32000
MAX_RETRIES = 6
REASONING = re.compile(r"^(gpt-?5|o\d)", re.IGNORECASE)


def is_reasoning(deployment):
    """Deployments are named by the user, so this goes by the usual model names."""
    return bool(REASONING.match(deployment))


def _retry_after(response, default):
    value = response.headers.get("retry-after") or response.headers.get("retry-after-ms")
    try:
        seconds = float(value)
    except (TypeError, ValueError):
        return default
    if "retry-after" not in response.headers:
        seconds /= 1000
    return max(seconds, default)


def _message(response):
    try:
        return response.json().get("error", {}).get("message", "")[:300]
    except ValueError:
        return response.text[:300]


class AzureOpenAI(Provider):
    name = "azure_openai"

    def __init__(self):
        self.endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT", "").rstrip("/")
        self.key = os.environ.get("AZURE_OPENAI_API_KEY", "").strip()
        strong = os.environ.get("AZURE_OPENAI_DEPLOYMENT", "")
        self.models = {"strong": strong, "fast": os.environ.get("AZURE_OPENAI_DEPLOYMENT_MINI") or strong}
        self.api_version = os.environ.get("AZURE_OPENAI_API_VERSION") or API_VERSION
        self.effort = os.environ.get("AZURE_OPENAI_REASONING_EFFORT", "").strip().lower()
        if not (self.endpoint and self.key and strong):
            raise LLMError("Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY and AZURE_OPENAI_DEPLOYMENT in .env.")

    def _body(self, messages, model, json_mode):
        messages = [dict(m) for m in messages]
        body = {"messages": messages, "max_completion_tokens": MAX_OUTPUT_TOKENS}
        if is_reasoning(model):
            if self.effort:
                body["reasoning_effort"] = self.effort
        else:
            body["temperature"] = 0
        if json_mode:
            body["response_format"] = {"type": "json_object"}
            # JSON mode is refused when no message mentions JSON
            if not any("json" in str(m.get("content", "")).lower() for m in messages):
                messages.append({"role": "user", "content": "Reply with one JSON object."})
        return body

    def call(self, messages, model, json_mode):
        url = f"{self.endpoint}/openai/deployments/{model}/chat/completions?api-version={self.api_version}"
        body = self._body(messages, model, json_mode)
        delay = 5.0
        for attempt in range(MAX_RETRIES + 1):
            try:
                response = httpx.post(url, json=body, headers={"api-key": self.key}, timeout=600)
            except httpx.HTTPError as exc:
                if attempt == MAX_RETRIES:
                    raise LLMError(f"Azure OpenAI network error: {exc.__class__.__name__}") from None
                time.sleep(delay)
                delay = min(delay * 2, 60)
                continue
            if response.status_code == 429 or response.status_code >= 500:
                if attempt == MAX_RETRIES:
                    kind = QuotaError if response.status_code == 429 else LLMError
                    raise kind(
                        f"Azure OpenAI HTTP {response.status_code} after {MAX_RETRIES} retries: {_message(response)}"
                    )
                time.sleep(_retry_after(response, delay))
                delay = min(delay * 2, 60)
                continue
            if response.status_code != 200:
                raise LLMError(f"Azure OpenAI HTTP {response.status_code}: {_message(response)}")
            return self._result(response.json(), model)

    @staticmethod
    def _result(data, model):
        choice = data["choices"][0]
        text = choice["message"].get("content") or ""
        if not text:
            # a reasoning model can spend the whole output budget on thinking
            raise LLMError(f"Azure OpenAI returned no text (finish_reason={choice.get('finish_reason')}) from {model}.")
        usage = data.get("usage") or {}
        return Result(
            text=text,
            prompt_tokens=usage.get("prompt_tokens", 0),
            completion_tokens=usage.get("completion_tokens", 0),
            model=model,
        )
