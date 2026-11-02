"""Metrics for the business case: time, LLM use, repairs, test pass rate and lines of code per system."""

import json
from datetime import datetime, timezone

from agents import config

STEPS = ["discover", "design", "generate", "validate"]


def record_step(system, step, started, finished):
    path = config.system_dir(system) / "timings.json"
    timings = json.loads(path.read_text()) if path.exists() else {}
    timings[step] = {
        "started": started.isoformat(timespec="seconds"),
        "finished": finished.isoformat(timespec="seconds"),
        "seconds": round((finished - started).total_seconds(), 1),
    }
    path.write_text(json.dumps(timings, indent=1))


def _system_of(purpose):
    return next((t for t in str(purpose).split()[1:] if t in config.SYSTEMS), None)


def _llm_usage(system, timings):
    path = config.workspace() / "run_log.jsonl"
    windows = [(datetime.fromisoformat(t["started"]), datetime.fromisoformat(t["finished"])) for t in timings.values()]
    usage = {"calls": 0, "cached": 0, "prompt_tokens": 0, "completion_tokens": 0}
    if not path.exists():
        return usage
    for line in path.read_text().splitlines():
        entry = json.loads(line)
        if _system_of(entry.get("purpose")) != system:
            continue
        ts = datetime.fromisoformat(entry["ts"])
        if not any(start <= ts <= end for start, end in windows):
            continue
        if entry.get("cached"):
            usage["cached"] += 1
        else:
            usage["calls"] += 1
            usage["prompt_tokens"] += entry.get("prompt_tokens", 0)
            usage["completion_tokens"] += entry.get("completion_tokens", 0)
    return usage


def _loc(system):
    out = config.generated_dir() / system
    meta_path = config.system_dir(system) / "generate.json"
    if not meta_path.exists():
        return {}
    meta = json.loads(meta_path.read_text())

    def count(files):
        return sum(
            sum(1 for line in (out / f).read_text().splitlines() if line.strip()) for f in files if (out / f).exists()
        )

    llm, template = count(meta["llm_files"]), count(meta["template_files"])
    return {"llm_written": llm, "from_templates": template, "total": llm + template}


def collect(system):
    work = config.system_dir(system)
    timings = json.loads((work / "timings.json").read_text()) if (work / "timings.json").exists() else {}
    summary_path = work / "validate" / "summary.json"
    validation = json.loads(summary_path.read_text()) if summary_path.exists() else {}
    metrics = {
        "system": system,
        "name": config.SYSTEMS[system],
        "seconds": {s: timings[s]["seconds"] for s in STEPS if s in timings},
        "total_seconds": round(sum(timings[s]["seconds"] for s in STEPS if s in timings), 1),
        "llm": _llm_usage(system, timings),
        "self_heal_repairs": validation.get("repairs"),
        "validation_rounds": validation.get("rounds"),
        "validation_status": validation.get("status"),
        "test_pass_rate": validation.get("test_pass_rate"),
        "tests": f"{validation.get('tests_passed', 0)}/{validation.get('tests_total', 0)}",
        "lines_of_code": _loc(system),
        "updated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
    path = config.workspace() / "metrics.json"
    all_metrics = json.loads(path.read_text()) if path.exists() else {}
    all_metrics[system] = metrics
    path.write_text(json.dumps(all_metrics, indent=1))
    return all_metrics


def table(all_metrics):
    head = (
        f"{'sys':<4}{'discover':>9}{'design':>8}{'generate':>9}{'validate':>9}{'total s':>9}"
        f"{'calls':>7}{'tokens in':>11}{'tokens out':>11}{'repairs':>8}{'tests':>9}{'LOC llm/tpl':>13}  status"
    )
    lines = [head, "-" * len(head)]
    for s, m in sorted(all_metrics.items()):
        sec = m["seconds"]
        loc = m["lines_of_code"]
        loc_text = f"{loc.get('llm_written', '-')}/{loc.get('from_templates', '-')}"
        lines.append(
            f"{s:<4}{sec.get('discover', 0):>9.0f}{sec.get('design', 0):>8.0f}{sec.get('generate', 0):>9.0f}"
            f"{sec.get('validate', 0):>9.0f}{m['total_seconds']:>9.0f}{m['llm']['calls']:>7}"
            f"{m['llm']['prompt_tokens']:>11}{m['llm']['completion_tokens']:>11}"
            f"{str(m['self_heal_repairs']):>8}{m['tests']:>9}"
            f"{loc_text:>13}  {m['validation_status']}"
        )
    return "\n".join(lines)
