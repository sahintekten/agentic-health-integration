"""Google AI Studio Gemini API (generateContent), authenticated with GEMINI_API_KEY.

Free tier limits are low, so calls are spaced out (GEMINI_MIN_INTERVAL seconds,
about 10 per minute by default) and 429 replies are retried with backoff.
"""

import os
import re
import time

import httpx

from agents import config
from agents.llm.base import LLMError, Provider, QuotaError, Result

URL = "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
_last_call = [0.0]
_overloaded = set()


class _Overloaded(Exception):
    pass


def _error(response):
    try:
        return response.json().get("error", {})
    except ValueError:
        return {"message": response.text}


def _retry_delay(err):
    for detail in err.get("details", []):
        if "retryDelay" in detail:
            m = re.match(r"([\d.]+)s", str(detail["retryDelay"]))
            if m:
                return float(m.group(1))
    return None


def _is_daily_limit(err):
    text = str(err)
    return "PerDay" in text or "per day" in text.lower()


class Gemini(Provider):
    name = "gemini"

    def __init__(self):
        self.models = {"strong": config.GEMINI_MODEL_STRONG, "fast": config.GEMINI_MODEL_FAST}
        self.key = os.environ.get("GEMINI_API_KEY", "").strip()
        if not self.key:
            raise LLMError("GEMINI_API_KEY is not set. Put it in .env.")
        self.min_interval = float(os.environ.get("GEMINI_MIN_INTERVAL", "6.5"))

    def _wait_turn(self):
        wait = _last_call[0] + self.min_interval - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        _last_call[0] = time.monotonic()

    def call(self, messages, model, json_mode):
        """model can be a comma list; an overloaded model (503) falls back to the next one."""
        names = [m.strip() for m in model.split(",") if m.strip()]
        # once a model was overloaded in this run, go straight to the next one
        live = [n for n in names if n not in _overloaded] or names[-1:]
        for i, name in enumerate(live):
            last = i == len(live) - 1
            try:
                return self._call_one(messages, name, json_mode, overload_retries=6 if last else 1)
            except _Overloaded:
                _overloaded.add(name)
                continue
        raise LLMError(f"Gemini models {names} are all overloaded (HTTP 503). Try again later.")

    def _call_one(self, messages, model, json_mode, overload_retries, max_retries=6):
        system = "\n\n".join(m["content"] for m in messages if m["role"] == "system")
        contents = [
            {"role": "model" if m["role"] == "assistant" else "user", "parts": [{"text": m["content"]}]}
            for m in messages
            if m["role"] != "system"
        ]
        generation = {"temperature": 0, "maxOutputTokens": 32768}
        if json_mode:
            generation["responseMimeType"] = "application/json"
        body = {"contents": contents, "generationConfig": generation}
        if system:
            body["systemInstruction"] = {"parts": [{"text": system}]}

        delay = 10.0
        for attempt in range(max_retries + 1):
            self._wait_turn()
            try:
                response = httpx.post(
                    URL.format(model=model), json=body, headers={"x-goog-api-key": self.key}, timeout=180
                )
            except httpx.HTTPError as exc:
                if isinstance(exc, httpx.TimeoutException) and attempt >= overload_retries:
                    raise _Overloaded() from None
                if attempt == max_retries:
                    raise LLMError(f"Gemini network error: {exc.__class__.__name__}") from None
                time.sleep(delay)
                delay = min(delay * 2, 120)
                continue

            if response.status_code == 429:
                err = _error(response)
                if _is_daily_limit(err):
                    raise QuotaError(
                        f"Gemini daily quota reached for {model}. Wait until tomorrow (Pacific time), "
                        f"rerun with the cache on, or change the model in .env. ({err.get('message', '')[:200]})"
                    )
                wait = max(_retry_delay(err) or 0, delay)
                if attempt == max_retries:
                    raise QuotaError(f"Gemini rate limit for {model} after {max_retries} retries.")
                time.sleep(wait)
                delay = min(delay * 2, 120)
                continue
            # a new key sometimes gets a short burst of 403s while it spreads; retry a little
            transient_403 = response.status_code == 403 and attempt < 2
            if response.status_code == 503 and attempt >= overload_retries:
                raise _Overloaded()
            if (response.status_code in (500, 502, 503, 504) or transient_403) and attempt < max_retries:
                time.sleep(delay)
                delay = min(delay * 2, 120)
                continue
            if response.status_code in (401, 403):
                raise LLMError(
                    f"Gemini refused the key (HTTP {response.status_code}): {_error(response).get('message')}"
                )
            if response.status_code != 200:
                raise LLMError(f"Gemini HTTP {response.status_code}: {str(_error(response).get('message'))[:300]}")

            data = response.json()
            candidates = data.get("candidates") or []
            if not candidates:
                raise LLMError(f"Gemini returned no answer: {str(data.get('promptFeedback'))[:200]}")
            parts = (candidates[0].get("content") or {}).get("parts") or []
            text = "".join(p.get("text", "") for p in parts if not p.get("thought"))
            if candidates[0].get("finishReason") == "MAX_TOKENS":
                raise LLMError("Gemini stopped at the output token limit; the answer is cut off.")
            usage = data.get("usageMetadata") or {}
            return Result(
                text=text,
                prompt_tokens=usage.get("promptTokenCount", 0),
                completion_tokens=usage.get("candidatesTokenCount", 0) + usage.get("thoughtsTokenCount", 0),
                model=model,
            )
        raise LLMError("Gemini: no response after retries")
