from dataclasses import dataclass


class LLMError(RuntimeError):
    pass


class QuotaError(LLMError):
    pass


@dataclass
class Result:
    text: str
    prompt_tokens: int = 0
    completion_tokens: int = 0
    model: str = ""


class Provider:
    name = ""
    models = {"strong": "", "fast": ""}

    def call(self, messages, model, json_mode):
        raise NotImplementedError


def strip_fences(text):
    """Remove a ```json ... ``` wrapper if the model added one."""
    text = text.strip()
    if text.startswith("```"):
        text = text.split("\n", 1)[1] if "\n" in text else ""
        if text.rstrip().endswith("```"):
            text = text.rstrip()[:-3]
    return text.strip()
