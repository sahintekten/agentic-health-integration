"""Azure OpenAI chat completions. Thin stub, not tested yet."""

import os

import httpx

from agents.llm.base import LLMError, Provider, Result

API_VERSION = "2024-10-21"


class AzureOpenAI(Provider):
    name = "azure_openai"

    def __init__(self):
        self.endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT", "").rstrip("/")
        self.key = os.environ.get("AZURE_OPENAI_API_KEY", "").strip()
        strong = os.environ.get("AZURE_OPENAI_DEPLOYMENT", "")
        self.models = {"strong": strong, "fast": os.environ.get("AZURE_OPENAI_DEPLOYMENT_MINI") or strong}
        if not (self.endpoint and self.key and strong):
            raise LLMError("Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY and AZURE_OPENAI_DEPLOYMENT in .env.")

    def call(self, messages, model, json_mode):
        body = {"messages": messages, "temperature": 0}
        if json_mode:
            body["response_format"] = {"type": "json_object"}
        url = f"{self.endpoint}/openai/deployments/{model}/chat/completions?api-version={API_VERSION}"
        response = httpx.post(url, json=body, headers={"api-key": self.key}, timeout=180)
        if response.status_code != 200:
            raise LLMError(f"Azure OpenAI HTTP {response.status_code}: {response.text[:300]}")
        data = response.json()
        usage = data.get("usage") or {}
        return Result(
            text=data["choices"][0]["message"]["content"] or "",
            prompt_tokens=usage.get("prompt_tokens", 0),
            completion_tokens=usage.get("completion_tokens", 0),
        )
