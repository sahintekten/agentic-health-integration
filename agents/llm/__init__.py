"""LLM layer. The agents only use complete().

The provider comes from LLM_PROVIDER in .env: github_models (default),
claude_code, anthropic, azure_openai, or fake (offline, for tests).
"""

import os
import time

from agents.llm import cache, runlog
from agents.llm.base import LLMError, QuotaError, strip_fences

__all__ = ["complete", "get_provider", "LLMError", "QuotaError", "strip_fences"]


def get_provider(name=None):
    name = (name or os.environ.get("LLM_PROVIDER") or "github_models").strip()
    if name == "github_models":
        from agents.llm.github_models import GitHubModels

        return GitHubModels()
    if name == "claude_code":
        from agents.llm.claude_code import ClaudeCode

        return ClaudeCode()
    if name == "anthropic":
        from agents.llm.anthropic import Anthropic

        return Anthropic()
    if name == "azure_openai":
        from agents.llm.azure_openai import AzureOpenAI

        return AzureOpenAI()
    if name == "fake":
        from agents.llm.fake import Fake

        return Fake()
    raise LLMError(f"unknown LLM_PROVIDER {name!r}")


def complete(messages, model_tier="strong", json_mode=False, purpose=""):
    """Send chat messages to the configured provider and return the reply text."""
    if model_tier not in ("strong", "fast"):
        raise ValueError("model_tier must be 'strong' or 'fast'")
    provider = get_provider()
    model = provider.models[model_tier]
    key = cache.make_key(provider.name, model, messages, json_mode)

    hit = cache.get(key)
    if hit is not None:
        runlog.log(provider=provider.name, model=model, tier=model_tier, purpose=purpose, cached=True)
        return hit["text"]

    start = time.monotonic()
    result = provider.call(messages, model, json_mode)
    text = strip_fences(result.text) if json_mode else result.text
    cache.put(key, {"text": text, "prompt_tokens": result.prompt_tokens, "completion_tokens": result.completion_tokens})
    runlog.log(
        provider=provider.name,
        model=model,
        tier=model_tier,
        purpose=purpose,
        cached=False,
        prompt_tokens=result.prompt_tokens,
        completion_tokens=result.completion_tokens,
        seconds=round(time.monotonic() - start, 2),
    )
    return text
