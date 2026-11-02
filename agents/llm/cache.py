"""Disk cache for LLM calls: one JSON file per (provider, model, messages, json_mode)."""

import hashlib
import json
import os

from agents.config import CACHE_DIR


def enabled():
    return os.environ.get("LLM_CACHE", "1") != "0"


def make_key(provider, model, messages, json_mode):
    raw = json.dumps(
        {"provider": provider, "model": model, "messages": messages, "json_mode": json_mode},
        sort_keys=True,
        ensure_ascii=False,
    )
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()


def get(key):
    path = CACHE_DIR / f"{key}.json"
    if not enabled() or not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def put(key, value):
    if not enabled():
        return
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    (CACHE_DIR / f"{key}.json").write_text(json.dumps(value, ensure_ascii=False, indent=1), encoding="utf-8")
