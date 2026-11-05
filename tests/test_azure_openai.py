"""Unit tests for the Azure OpenAI provider. No network: httpx.post is replaced."""

import httpx
import pytest
from agents.llm import azure_openai
from agents.llm.base import LLMError, QuotaError


class FakeResponse:
    def __init__(self, status=200, payload=None, headers=None):
        self.status_code = status
        self._payload = payload if payload is not None else {}
        self.headers = headers or {}
        self.text = str(self._payload)

    def json(self):
        return self._payload


def ok(text="hello", prompt=11, completion=7):
    return FakeResponse(
        200,
        {
            "choices": [{"message": {"content": text}, "finish_reason": "stop"}],
            "usage": {"prompt_tokens": prompt, "completion_tokens": completion},
        },
    )


@pytest.fixture
def provider(monkeypatch):
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "test-key")
    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT", "gpt-5-mini")
    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT_MINI", "gpt-4-1-mini")
    monkeypatch.delenv("AZURE_OPENAI_REASONING_EFFORT", raising=False)
    monkeypatch.setattr(azure_openai.time, "sleep", lambda s: None)
    return azure_openai.AzureOpenAI()


def script(monkeypatch, replies):
    """Replace httpx.post with a queue of replies; returns the list of requests sent."""
    sent = []
    queue = list(replies)

    def fake_post(url, json, headers, timeout):
        sent.append({"url": url, "json": json, "headers": headers})
        reply = queue.pop(0)
        if isinstance(reply, Exception):
            raise reply
        return reply

    monkeypatch.setattr(azure_openai.httpx, "post", fake_post)
    return sent


def test_tiers_map_to_deployments(provider):
    assert provider.models == {"strong": "gpt-5-mini", "fast": "gpt-4-1-mini"}


def test_missing_settings_raise(monkeypatch):
    monkeypatch.delenv("AZURE_OPENAI_ENDPOINT", raising=False)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "k")
    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT", "d")
    with pytest.raises(LLMError):
        azure_openai.AzureOpenAI()


def test_reasoning_model_has_no_temperature(provider, monkeypatch):
    sent = script(monkeypatch, [ok()])
    result = provider.call([{"role": "user", "content": "hi"}], "gpt-5-mini", json_mode=False)
    body = sent[0]["json"]
    assert "temperature" not in body and "top_p" not in body and "max_tokens" not in body
    assert body["max_completion_tokens"] == azure_openai.MAX_OUTPUT_TOKENS
    assert sent[0]["url"].startswith("https://example.openai.azure.com/openai/deployments/gpt-5-mini/")
    assert sent[0]["headers"] == {"api-key": "test-key"}
    assert (result.text, result.prompt_tokens, result.completion_tokens) == ("hello", 11, 7)


def test_chat_model_keeps_temperature_zero(provider, monkeypatch):
    sent = script(monkeypatch, [ok()])
    provider.call([{"role": "user", "content": "hi"}], "gpt-4-1-mini", json_mode=False)
    body = sent[0]["json"]
    assert body["temperature"] == 0
    assert "reasoning_effort" not in body


def test_reasoning_effort_only_for_reasoning_models(provider, monkeypatch):
    provider.effort = "low"
    sent = script(monkeypatch, [ok(), ok()])
    provider.call([{"role": "user", "content": "hi"}], "gpt-5-mini", False)
    provider.call([{"role": "user", "content": "hi"}], "gpt-4-1-mini", False)
    assert sent[0]["json"]["reasoning_effort"] == "low"
    assert "reasoning_effort" not in sent[1]["json"]


def test_json_mode_sets_format_and_mentions_json(provider, monkeypatch):
    sent = script(monkeypatch, [ok('{"a": 1}'), ok('{"a": 1}')])
    provider.call([{"role": "user", "content": "give me data"}], "gpt-5-mini", json_mode=True)
    body = sent[0]["json"]
    assert body["response_format"] == {"type": "json_object"}
    assert any("json" in m["content"].lower() for m in body["messages"])
    # a prompt that already says JSON is left alone
    messages = [{"role": "user", "content": "answer as JSON"}]
    provider.call(messages, "gpt-5-mini", json_mode=True)
    assert len(sent[1]["json"]["messages"]) == 1
    assert len(messages) == 1


def test_429_is_retried_with_retry_after(provider, monkeypatch):
    waits = []
    monkeypatch.setattr(azure_openai.time, "sleep", waits.append)
    sent = script(monkeypatch, [FakeResponse(429, headers={"retry-after": "12"}), FakeResponse(429), ok("done")])
    result = provider.call([{"role": "user", "content": "hi"}], "gpt-5-mini", False)
    assert result.text == "done"
    assert len(sent) == 3
    assert waits[0] == 12.0
    assert waits[1] == 10.0  # backoff doubled from 5 s


def test_429_forever_raises_quota_error(provider, monkeypatch):
    script(monkeypatch, [FakeResponse(429, {"error": {"message": "slow down"}})] * (azure_openai.MAX_RETRIES + 1))
    with pytest.raises(QuotaError):
        provider.call([{"role": "user", "content": "hi"}], "gpt-5-mini", False)


def test_server_error_and_network_error_are_retried(provider, monkeypatch):
    sent = script(monkeypatch, [FakeResponse(503), httpx.ConnectError("x"), ok("fine")])
    assert provider.call([{"role": "user", "content": "hi"}], "gpt-4-1-mini", False).text == "fine"
    assert len(sent) == 3


def test_client_error_is_not_retried(provider, monkeypatch):
    sent = script(monkeypatch, [FakeResponse(400, {"error": {"message": "bad request"}})])
    with pytest.raises(LLMError, match="400"):
        provider.call([{"role": "user", "content": "hi"}], "gpt-5-mini", False)
    assert len(sent) == 1


def test_empty_reply_from_reasoning_model_raises(provider, monkeypatch):
    empty = FakeResponse(200, {"choices": [{"message": {"content": None}, "finish_reason": "length"}], "usage": {}})
    script(monkeypatch, [empty])
    with pytest.raises(LLMError, match="finish_reason=length"):
        provider.call([{"role": "user", "content": "hi"}], "gpt-5-mini", False)


@pytest.mark.parametrize(
    "name,expected",
    [("gpt-5-mini", True), ("gpt5", True), ("o4-mini", True), ("o3", True), ("gpt-4-1-mini", False), ("gpt-4o", False)],
)
def test_is_reasoning(name, expected):
    assert azure_openai.is_reasoning(name) is expected
