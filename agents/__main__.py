"""Command line entry point.

python -m agents run --system a --step discover|design|generate|validate|pipeline [--yes]
python -m agents metrics
"""

import argparse
import json
import sys
from datetime import datetime, timezone

import yaml

from agents import config, demo, design, discover, generate, metrics, progress, review, validate
from agents.llm import LLMError
from agents.llm.runlog import RUN_ID, summary

PIPELINE = ["discover", "design", "generate", "validate"]


def _timed(system, step, fn):
    started = datetime.now(timezone.utc)
    progress.emit(system, step, "started")
    ok = False
    try:
        result = fn()
        ok = True
        return result
    finally:
        finished = datetime.now(timezone.utc)
        metrics.record_step(system, step, started, finished)
        seconds = (finished - started).total_seconds()
        progress.emit(system, step, "finished" if ok else "failed", f"{seconds:.0f} s")


def run_system(system, steps, auto):
    out = config.system_dir(system)
    for step in steps:
        print(f"\n=== {step}: system {system} ({config.SYSTEMS[system]})")
        previous = PIPELINE[PIPELINE.index(step) - 1] if step != "discover" else None
        if previous and not auto and not review.is_approved(system, previous):
            raise SystemExit(f"The {previous} step of system {system} is not approved yet.")
        if step == "discover":
            _timed(system, step, lambda: discover.run(system))
            show = lambda: discover.summarise(json.loads((out / "profile.json").read_text()))  # noqa: E731
        elif step == "design":
            _timed(system, step, lambda: design.run(system))
            show = lambda: design.summarise(yaml.safe_load((out / "mapping.yaml").read_text()), system)  # noqa: E731
        elif step == "generate":
            meta = _timed(system, step, lambda: generate.run(system))
            print(generate.summarise(meta, system))
            review.stamp(system, "generate", auto=True)
            continue
        else:
            result = _timed(system, step, lambda: validate.run(system, auto=auto))
            show = lambda: validate.summarise(result)  # noqa: E731
        progress.emit(system, step, "waiting for review", show().splitlines()[0][:160])
        if not review.ask(system, step, show, auto):
            progress.emit(system, step, "rejected")
            return False
        progress.emit(system, step, "approved", "auto (--yes)" if auto else "by reviewer")
    return True


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m agents")
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run", help="run agent steps for one system")
    run.add_argument("--system", required=True, choices=[*config.SYSTEMS, "all"])
    run.add_argument("--step", default="pipeline", choices=PIPELINE + ["pipeline", "all"])
    run.add_argument("--yes", action="store_true", help="approve every step without asking")
    sub.add_parser("metrics", help="print the metrics table")
    for name, text in [
        ("demo", "run the pipeline with review prompts, start the adapter and register it"),
        ("demo-reset", "remove a demo system again"),
        ("register", "register an adapter in the gateway"),
    ]:
        p = sub.add_parser(name, help=text)
        p.add_argument("--system", required=True, choices=list(config.SYSTEMS))
    args = parser.parse_args(argv)

    if args.command == "register":
        print(demo.register(args.system))
        return 0
    if args.command == "demo-reset":
        print(demo.reset(args.system))
        return 0
    if args.command == "demo":
        watch = demo.Stopwatch()
        if not run_system(args.system, PIPELINE, auto=False):
            return 1
        watch.lap("pipeline")
        progress.emit(args.system, "deploy", "started", f"adapter-{args.system}")
        demo.start_adapter(args.system)
        watch.lap("start adapter")
        stats = demo.register(args.system)
        watch.lap("register in gateway")
        progress.emit(args.system, "deploy", "finished", json.dumps(stats)[:160])
        demo.save_timing(args.system, watch.total, watch.parts)
        print(f"\ngateway index: {stats}")
        print(
            f"demo finished in {watch.total / 60:.1f} minutes "
            + json.dumps({k: round(v) for k, v in watch.parts.items()})
        )
        print("\n" + metrics.table(metrics.collect(args.system)))
        return 0

    if args.command == "metrics":
        collected = {}
        for system in config.SYSTEMS:
            if (config.workspace() / system / "timings.json").exists():
                collected = metrics.collect(system)
        print(metrics.table(collected))
        return 0

    systems = list(config.SYSTEMS) if args.system == "all" else [args.system]
    steps = PIPELINE if args.step in ("pipeline", "all") else [args.step]
    ok = True
    try:
        for system in systems:
            ok = run_system(system, steps, args.yes) and ok
            if any(s in steps for s in ("generate", "validate")):
                print("\n" + metrics.table(metrics.collect(system)))
    except LLMError as exc:
        print(f"\nLLM error: {exc}", file=sys.stderr)
        ok = False
    totals = summary()
    print(
        f"\nrun {RUN_ID}: {totals['calls']} LLM calls, {totals['cached']} cache hits, "
        f"{totals['prompt_tokens']} prompt tokens, {totals['completion_tokens']} completion tokens"
    )
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
