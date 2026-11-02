"""GitHub Models. Retired by GitHub on 2026-07-30, kept only so old settings fail clearly."""

from agents.llm.base import LLMError, Provider


class GitHubModels(Provider):
    name = "github_models"
    models = {"strong": "openai/gpt-4o", "fast": "openai/gpt-4o-mini"}

    def __init__(self):
        raise LLMError("GitHub Models was retired on 2026-07-30. Set LLM_PROVIDER=gemini (or another provider).")
