"""Progress events for the dashboard's live pipeline view (workspace/progress.jsonl)."""

import json
from datetime import datetime, timezone

from agents import config


def emit(system, step, event, detail=""):
    path = config.workspace() / "progress.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    entry = {
        "ts": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "system": system,
        "step": step,
        "event": event,
        "detail": detail,
    }
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")
