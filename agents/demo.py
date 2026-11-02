"""Live demo helpers: add a new system end to end, register it in the gateway, and reset it again."""

import json
import shutil
import subprocess
import time
from datetime import datetime, timezone

import httpx

from agents import config

ADAPTERS_FILE = config.ROOT / "gateway" / "config" / "adapters.json"
GATEWAY = "http://localhost:9000"


def register(system):
    resources = json.loads((config.system_dir(system) / "generate.json").read_text())["resources"]
    adapters = [a for a in json.loads(ADAPTERS_FILE.read_text()) if a["name"] != system]
    adapters.append(
        {
            "name": system,
            "system": config.SYSTEMS[system],
            "url": f"http://adapter-{system}:8080",
            "resources": resources,
        }
    )
    ADAPTERS_FILE.write_text(json.dumps(adapters, indent=2) + "\n")
    return _reindex()


def unregister(system):
    adapters = [a for a in json.loads(ADAPTERS_FILE.read_text()) if a["name"] != system]
    ADAPTERS_FILE.write_text(json.dumps(adapters, indent=2) + "\n")
    return _reindex()


def _reindex(attempts=5):
    """Ask the gateway to rebuild its index. Docker file sharing can be a moment behind, so retry."""
    for attempt in range(attempts):
        try:
            response = httpx.post(f"{GATEWAY}/admin/reindex", timeout=120)
            if response.status_code == 200:
                return response.json()
        except httpx.HTTPError as exc:
            if attempt == attempts - 1:
                return {"error": f"gateway not reachable ({exc.__class__.__name__}); start it with make up-adapters"}
        time.sleep(1 + attempt)
    return {"error": "the gateway could not rebuild its index"}


def start_adapter(system):
    cmd = ["docker", "compose", "--profile", "demo", "up", "-d", "--build", "--wait", f"adapter-{system}"]
    proc = subprocess.run(cmd, cwd=config.ROOT, capture_output=True, text=True)
    if proc.returncode != 0:
        raise SystemExit(f"adapter-{system} did not start:\n{proc.stderr[-1500:]}")


def reset(system):
    subprocess.run(
        ["docker", "compose", "--profile", "demo", "rm", "-sf", f"adapter-{system}"],
        cwd=config.ROOT,
        capture_output=True,
    )
    result = unregister(system)
    for path in (
        config.workspace() / system,
        config.generated_dir() / system,
        config.ROOT / "eval" / "results" / f"{system}.json",
    ):
        if path.is_dir():
            shutil.rmtree(path)
        elif path.exists():
            path.unlink()
    metrics_path = config.workspace() / "metrics.json"
    if metrics_path.exists():
        data = json.loads(metrics_path.read_text())
        data.pop(system, None)
        metrics_path.write_text(json.dumps(data, indent=1))
    return result


def save_timing(system, seconds, parts):
    path = config.workspace() / "demo_timing.json"
    data = json.loads(path.read_text()) if path.exists() else {}
    data[system] = {
        "finished_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "seconds": round(seconds, 1),
        "parts": {k: round(v, 1) for k, v in parts.items()},
    }
    path.write_text(json.dumps(data, indent=1))


class Stopwatch:
    def __init__(self):
        self.start = time.monotonic()
        self.parts, self._last = {}, self.start

    def lap(self, name):
        now = time.monotonic()
        self.parts[name] = now - self._last
        self._last = now

    @property
    def total(self):
        return time.monotonic() - self.start
