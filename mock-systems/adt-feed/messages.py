"""Load the generated ADT messages and write them to the outbox folder."""

import json
import os
from pathlib import Path

DATA_FILE = Path(os.environ.get("ADT_DATA", "data/messages.jsonl"))
OUTBOX = Path(os.environ.get("ADT_OUTBOX", "outbox"))


def load_messages():
    if not DATA_FILE.exists():
        raise SystemExit(f"{DATA_FILE} not found. Run 'make data' first.")
    with DATA_FILE.open(encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def write_outbox(messages):
    OUTBOX.mkdir(parents=True, exist_ok=True)
    for m in messages:
        (OUTBOX / f"{m['control_id']}.hl7").write_bytes(m["hl7"].encode("utf-8"))
