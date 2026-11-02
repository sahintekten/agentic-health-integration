"""Append one line per LLM call to workspace/run_log.jsonl."""

import json
import os
import uuid
from datetime import datetime, timezone

from agents.config import workspace

RUN_ID = os.environ.get("AGENTS_RUN_ID") or (
    datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:6]
)


def log(**entry):
    path = workspace() / "run_log.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    entry = {"ts": datetime.now(timezone.utc).isoformat(timespec="seconds"), "run_id": RUN_ID, **entry}
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")


def summary(run_id=RUN_ID):
    path = workspace() / "run_log.jsonl"
    totals = {"calls": 0, "cached": 0, "prompt_tokens": 0, "completion_tokens": 0}
    if not path.exists():
        return totals
    for line in path.read_text(encoding="utf-8").splitlines():
        entry = json.loads(line)
        if entry.get("run_id") != run_id:
            continue
        if entry.get("cached"):
            totals["cached"] += 1
            continue
        totals["calls"] += 1
        totals["prompt_tokens"] += entry.get("prompt_tokens", 0)
        totals["completion_tokens"] += entry.get("completion_tokens", 0)
    return totals
