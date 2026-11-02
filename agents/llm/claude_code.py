"""Local `claude -p` CLI in headless mode. For development only.

Needs the Claude Code CLI installed and logged in. It runs with no tools in
an empty temp folder, so it cannot read files from this repo.
"""

import json
import os
import shutil
import subprocess
import tempfile

from agents.llm.base import LLMError, Provider, Result


class ClaudeCode(Provider):
    name = "claude_code"

    def __init__(self):
        self.models = {
            "strong": os.environ.get("CLAUDE_CODE_MODEL_STRONG", "sonnet"),
            "fast": os.environ.get("CLAUDE_CODE_MODEL_FAST", "haiku"),
        }
        if shutil.which("claude") is None:
            raise LLMError("The claude CLI is not on PATH. Install Claude Code and log in first.")

    def call(self, messages, model, json_mode):
        system = "\n\n".join(m["content"] for m in messages if m["role"] == "system")
        prompt = "\n\n".join(m["content"] for m in messages if m["role"] != "system")
        if json_mode:
            prompt += "\n\nReply with one JSON object only. No markdown and no code fences."
        cmd = ["claude", "-p", "--output-format", "json", "--model", model, "--tools", "", "--no-session-persistence"]
        if system:
            cmd += ["--system-prompt", system]
        with tempfile.TemporaryDirectory() as empty_dir:
            proc = subprocess.run(cmd, input=prompt, capture_output=True, text=True, cwd=empty_dir, timeout=600)
        if proc.returncode != 0:
            raise LLMError(f"claude CLI failed (exit {proc.returncode}): {proc.stderr.strip()[:300]}")
        try:
            data = json.loads(proc.stdout)
        except json.JSONDecodeError:
            return Result(text=proc.stdout)
        if data.get("is_error"):
            raise LLMError(f"claude CLI error: {str(data.get('result'))[:300]}")
        usage = data.get("usage") or {}
        return Result(
            text=data.get("result", ""),
            # most of the prompt is reported under the cache fields, so add them up
            prompt_tokens=usage.get("input_tokens", 0)
            + usage.get("cache_creation_input_tokens", 0)
            + usage.get("cache_read_input_tokens", 0),
            completion_tokens=usage.get("output_tokens", 0),
        )
