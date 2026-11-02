"""Command line entry point.

python -m agents run --system a --step discover|design|all [--yes]
"""

import argparse
import json
import sys

import yaml

from agents import config, design, discover, review
from agents.llm import LLMError
from agents.llm.runlog import RUN_ID, summary


def run_system(system, step, auto):
    out = config.system_dir(system)
    if step in ("discover", "all"):
        print(f"\n=== discover: system {system} ({config.SYSTEMS[system]})")
        discover.run(system)
        profile = lambda: discover.summarise(json.loads((out / "profile.json").read_text()))  # noqa: E731
        if not review.ask(system, "discover", profile, auto):
            return False
    if step in ("design", "all"):
        if not auto and not review.is_approved(system, "discover"):
            raise SystemExit(f"The discover step of system {system} is not approved yet.")
        print(f"\n=== design: system {system} ({config.SYSTEMS[system]})")
        design.run(system)
        mapping = lambda: design.summarise(yaml.safe_load((out / "mapping.yaml").read_text()), system)  # noqa: E731
        if not review.ask(system, "design", mapping, auto):
            return False
    return True


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m agents")
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run", help="run agent steps for one system")
    run.add_argument("--system", required=True, choices=["a", "b", "c", "all"])
    run.add_argument("--step", default="all", choices=["discover", "design", "all"])
    run.add_argument("--yes", action="store_true", help="approve every step without asking")
    args = parser.parse_args(argv)

    systems = ["a", "b", "c"] if args.system == "all" else [args.system]
    ok = True
    try:
        for system in systems:
            ok = run_system(system, args.step, args.yes) and ok
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
