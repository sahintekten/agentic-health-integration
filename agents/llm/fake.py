"""Offline provider for tests. Returns a small fixed reply and never uses the network."""

from agents.llm.base import Provider, Result


class Fake(Provider):
    name = "fake"
    models = {"strong": "fake-strong", "fast": "fake-fast"}

    def call(self, messages, model, json_mode):
        if json_mode:
            return Result(text='{"summary": "offline test reply", "fields": [], "mappings": []}')
        return Result(text="offline test reply")
