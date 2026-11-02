"""Anthropic Messages API. Thin stub, not tested yet."""

import os

import httpx

from agents.llm.base import LLMError, Provider, Result


class Anthropic(Provider):
    name = "anthropic"

    def __init__(self):
        self.models = {
            "strong": os.environ.get("ANTHROPIC_MODEL_STRONG", "claude-sonnet-5-5"),
            "fast": os.environ.get("ANTHROPIC_MODEL_FAST", "claude-haiku-5-5"),
        }
        self.key = os.environ.get("ANTHROPIC_API_KEY", "").strip()
        if not self.key:
            raise LLMError("ANTHROPIC_API_KEY is not set. Put it in .env.")

    def call(self, messages, model, json_mode):
        system = "\n\n".join(m["content"] for m in messages if m["role"] == "system")
        if json_mode:
            system += "\n\nReply with one JSON object only."
        body = {
            "model": model,
            "max_tokens": 4096,
            "system": system,
            "messages": [m for m in messages if m["role"] != "system"],
        }
        headers = {"x-api-key": self.key, "anthropic-version": "2023-06-01"}
        response = httpx.post("https://api.anthropic.com/v1/messages", json=body, headers=headers, timeout=180)
        if response.status_code != 200:
            raise LLMError(f"Anthropic HTTP {response.status_code}: {response.text[:300]}")
        data = response.json()
        usage = data.get("usage") or {}
        return Result(
            text="".join(block.get("text", "") for block in data.get("content", [])),
            prompt_tokens=usage.get("input_tokens", 0),
            completion_tokens=usage.get("output_tokens", 0),
        )
