"""The agents must never read the answer key in eval/ground_truth.

1. Static check: no file in agents/ mentions the folder name.
2. Runtime check: run discover, design and generate for all three systems with the
   offline LLM provider while an audit hook watches every open() call. Any
   open of a file under eval/ground_truth by code in agents/ fails the test.

The runtime check needs the mock systems (make up). It is skipped locally when
they are down, but never skipped in CI.
"""

import os
import socket
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
AGENTS = str(ROOT / "agents") + os.sep
ANSWER_KEY = os.path.realpath(ROOT / "eval" / "ground_truth") + os.sep

_state = {"on": False, "violations": []}


def _audit(event, args):
    if not _state["on"] or event != "open" or not args:
        return
    path = args[0]
    if not isinstance(path, (str, bytes, os.PathLike)):
        return
    real = os.path.realpath(os.fsdecode(path))
    if not (real + os.sep).startswith(ANSWER_KEY):
        return
    frame = sys._getframe(1)
    while frame is not None:
        if frame.f_code.co_filename.startswith(AGENTS):
            _state["violations"].append(f"{real} opened by {frame.f_code.co_filename}:{frame.f_lineno}")
            return
        frame = frame.f_back


sys.addaudithook(_audit)


def _systems_up():
    try:
        socket.create_connection(("localhost", 8001), timeout=2).close()
        return True
    except OSError:
        return False


def test_agents_code_does_not_mention_the_answer_key():
    hits = [
        str(p.relative_to(ROOT))
        for p in (ROOT / "agents").rglob("*")
        if p.is_file() and "__pycache__" not in p.parts and "ground_truth" in p.read_text(errors="ignore")
    ]
    assert hits == []


def test_audit_hook_catches_a_read_from_agents_code():
    target = next((ROOT / "eval" / "ground_truth").glob("*.yaml"))
    probe = compile("open(target).close()", AGENTS + "_probe.py", "exec")
    _state.update(on=True, violations=[])
    try:
        exec(probe, {"target": str(target)})
    finally:
        _state["on"] = False
    assert _state["violations"], "the audit hook did not see the read"


def test_agents_never_open_the_answer_key(tmp_path, monkeypatch):
    if not _systems_up():
        if os.environ.get("CI"):
            pytest.fail("mock systems are not running in CI")
        pytest.skip("mock systems are not running (make up)")
    monkeypatch.setenv("LLM_PROVIDER", "fake")
    monkeypatch.setenv("LLM_CACHE", "0")
    monkeypatch.setenv("AGENTS_WORKSPACE", str(tmp_path))
    monkeypatch.setenv("AGENTS_GENERATED", str(tmp_path / "generated"))
    from agents.__main__ import main

    _state.update(on=True, violations=[])
    try:
        codes = [
            main(["run", "--system", "all", "--step", step, "--yes"]) for step in ("discover", "design", "generate")
        ]
    finally:
        _state["on"] = False
    assert codes == [0, 0, 0]
    assert (tmp_path / "c" / "mapping.yaml").exists()
    assert (tmp_path / "generated" / "c" / "src" / "Mapper.cs").exists()
    assert _state["violations"] == []
