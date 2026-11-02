"""Human review gate. Approved outputs get a reviewed_by / reviewed_at stamp."""

import getpass
import hashlib
import json
import os
import subprocess
import sys
from datetime import datetime, timezone

from agents import config

STEP_FILES = {
    "discover": ["inventory.json", "profile.json"],
    "design": ["mapping.yaml", "openapi.yaml", "id_strategy.md"],
}


def _reviewer():
    try:
        name = subprocess.run(["git", "config", "user.name"], capture_output=True, text=True).stdout.strip()
    except OSError:
        name = ""
    return name or getpass.getuser()


def stamp(system, step, auto):
    out = config.system_dir(system)
    path = out / "review.json"
    reviews = json.loads(path.read_text()) if path.exists() else {}
    reviews[step] = {
        "reviewed_by": "auto (--yes)" if auto else _reviewer(),
        "reviewed_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "files": {
            name: hashlib.sha256((out / name).read_bytes()).hexdigest()[:16]
            for name in STEP_FILES[step]
            if (out / name).exists()
        },
    }
    path.write_text(json.dumps(reviews, indent=1))


def is_approved(system, step):
    path = config.workspace() / system / "review.json"
    return path.exists() and step in json.loads(path.read_text())


def ask(system, step, summary_fn, auto=False):
    """Print the summary and ask for approval. Returns True when approved."""
    print(summary_fn())
    if auto:
        stamp(system, step, auto=True)
        print(f"[{step}] approved automatically (--yes)")
        return True
    if not sys.stdin.isatty():
        raise SystemExit("Not an interactive terminal. Run again with --yes to approve without prompts.")
    main_file = config.system_dir(system) / STEP_FILES[step][-1 if step == "discover" else 0]
    while True:
        answer = input(f"\n[{step}] approve? [y/n/edit] ").strip().lower()
        if answer in ("y", "yes"):
            stamp(system, step, auto=False)
            return True
        if answer in ("n", "no"):
            print(f"[{step}] not approved. Nothing was stamped.")
            return False
        if answer in ("e", "edit"):
            subprocess.run([os.environ.get("EDITOR", "vi"), str(main_file)])
            print(summary_fn())
