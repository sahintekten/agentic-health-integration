"""Validate agent: check a generated adapter and repair it in a loop (max 5 rounds).

Each round runs: build, unit + integration tests, a contract check of the
running adapter (Firely validator + the generated openapi.yaml), a PHI scan and
a reconciliation of resource counts against the source. When something fails,
the LLM gets the failing output and patches src/Mapper.cs. It may fix compile
errors in the generated tests, but it may never delete or weaken a test.

Reports go to workspace/<system>/validate/round_<n>.json and .md.
"""

import json
import os
import re
import signal
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import httpx
import psycopg
import yaml

from agents import config, generate
from agents.inventory.values import SUSPICIOUS
from agents.llm import LLMError, complete

MAX_ROUNDS = 5
PORTS = {"a": 9001, "b": 9002, "c": 9003}
MLLP_TEST_PORT = 9013
EDITABLE = {"src/Mapper.cs", "tests/MappingTests.cs", "tests/DataRulesTests.cs"}
TEST_FILES = ["tests/MappingTests.cs", "tests/DataRulesTests.cs"]
ERROR_LINE = re.compile(r"(?P<file>[^\s(]+\.cs)\((?P<line>\d+),\d+\): error (?P<code>CS\d+): (?P<msg>.*?)(?: \[|$)")

FIX_PROMPT = f"""You repair a generated .NET 8 FHIR adapter (C# 12, Firely SDK 6.8).
You get the failing check, its output, the current files and the approved mapping.yaml.

Return one JSON object: {{"files": {{"src/Mapper.cs": "<the full new file>"}}, "notes": "one short sentence"}}
- Always fix the adapter code in src/Mapper.cs.
- Only when the check is "build" and a compile error is inside a test file, you may also return that test
  file ("tests/MappingTests.cs" or "tests/DataRulesTests.cs") with the compile error fixed.
- Only when the check is "phi" and a test file holds real patient values, you may also return that test file
  with those values replaced by invented ones (and the expected values changed to match). Keep every test.
- You must NOT delete tests, remove or loosen assertions, or add Skip. Tests describe the wanted behaviour.
- Return full files, not diffs.

{generate.RULES}"""


def _env():
    env = dict(os.environ)
    env["PATH"] = env.get("PATH", "") + ":/usr/local/share/dotnet:" + str(Path.home() / ".dotnet")
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    return env


def _run(cmd, cwd, timeout=900):
    proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout, env=_env())
    return proc.returncode, proc.stdout + proc.stderr


# ---------- checks ----------


def check_build(out):
    code, text = _run(["dotnet", "build", "tests/Adapter.Tests.csproj", "-nologo", "-v", "q", "-clp:NoSummary"], out)
    errors, seen = [], set()
    for m in ERROR_LINE.finditer(text):
        key = (m["file"], m["line"], m["code"])
        if key not in seen:
            seen.add(key)
            file = m["file"].split(f"generated/{out.name}/")[-1]
            errors.append({"file": file, "line": int(m["line"]), "code": m["code"], "message": m["msg"][:300]})
    if code != 0 and not errors:
        errors.append({"file": "?", "line": 0, "code": "build", "message": text.strip()[-800:]})
    return {"ok": code == 0, "errors": errors[:40], "error_count": len(errors)}


def check_tests(out, work):
    trx_dir = work / "validate" / "trx"
    for old in trx_dir.glob("*.trx"):
        old.unlink()
    _run(
        [
            "dotnet",
            "test",
            "tests/Adapter.Tests.csproj",
            "--no-build",
            "--logger",
            "trx;LogFileName=results.trx",
            "--results-directory",
            str(trx_dir),
        ],
        out,
    )
    trx = trx_dir / "results.trx"
    if not trx.exists():
        return {"ok": False, "total": 0, "passed": 0, "failed": 0, "failures": [{"test": "?", "message": "no results"}]}
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    root = ET.parse(trx).getroot()
    counters = root.find(".//t:ResultSummary/t:Counters", ns).attrib
    failures = []
    for result in root.findall(".//t:UnitTestResult", ns):
        if result.get("outcome") != "Passed":
            msg = result.find(".//t:Message", ns)
            failures.append(
                {"test": result.get("testName"), "message": (msg.text or "")[:500] if msg is not None else ""}
            )
    total, passed = int(counters.get("total", 0)), int(counters.get("passed", 0))
    return {
        "ok": total > 0 and total == passed,
        "total": total,
        "passed": passed,
        "failed": total - passed,
        "failures": failures[:25],
    }


class RunningAdapter:
    """Starts the adapter with `dotnet run` for the contract and reconciliation checks."""

    def __init__(self, system, out, work):
        self.url = f"http://127.0.0.1:{PORTS[system]}"
        self.log_path = work / "validate" / "adapter.log"
        env = _env()
        env.update(ASPNETCORE_URLS=self.url, MLLP_PORT=str(MLLP_TEST_PORT), CACHE_SECONDS="600")
        self.log = self.log_path.open("w")
        self.proc = subprocess.Popen(
            ["dotnet", "run", "--project", "src/Adapter.csproj", "--no-build", "--no-launch-profile"],
            cwd=out,
            env=env,
            stdout=self.log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )

    def wait(self, seconds=120):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                return False
            try:
                if httpx.get(self.url + "/health", timeout=30).status_code == 200:
                    return True
            except httpx.HTTPError:
                pass
            time.sleep(1)
        return False

    def stop(self):
        if self.proc.poll() is None:
            os.killpg(self.proc.pid, signal.SIGTERM)
            try:
                self.proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                os.killpg(self.proc.pid, signal.SIGKILL)
        self.log.close()
        return self.log_path.read_text(errors="replace")


def check_contract(adapter, work, openapi, resources):
    problems, files = [], []
    saved = work / "validate" / "responses"
    saved.mkdir(parents=True, exist_ok=True)
    for old in saved.glob("*.json"):
        old.unlink()

    def get(path, expect=200, resource_type=None, name=None):
        r = httpx.get(adapter.url + path, timeout=120)
        if r.status_code != expect:
            problems.append(f"GET {path}: HTTP {r.status_code}, expected {expect}")
            return None
        if path.startswith("/fhir") and "application/fhir+json" not in r.headers.get("content-type", ""):
            problems.append(f"GET {path}: content-type {r.headers.get('content-type')}")
        body = r.json()
        if resource_type and body.get("resourceType") != resource_type:
            problems.append(f"GET {path}: resourceType {body.get('resourceType')}, expected {resource_type}")
        if name:
            f = saved / f"{name}.json"
            f.write_text(r.text)
            files.append(str(f))
        return body

    get("/fhir/metadata", resource_type="CapabilityStatement", name="metadata")
    documented = set((openapi.get("paths") or {}).keys())
    patient_id = None
    for res in resources:
        if f"/fhir/{res}" not in documented:
            problems.append(f"/fhir/{res} is served but not in openapi.yaml")
        bundle = get(f"/fhir/{res}?_count=20", resource_type="Bundle", name=f"{res}_search")
        if not bundle:
            continue
        if not isinstance(bundle.get("total"), int):
            problems.append(f"/fhir/{res}: Bundle.total missing")
        entries = bundle.get("entry") or []
        if not entries:
            problems.append(f"/fhir/{res}: no resources returned")
            continue
        wrong = [e["resource"].get("resourceType") for e in entries if e["resource"].get("resourceType") != res]
        if wrong:
            problems.append(f"/fhir/{res}: bundle contains {sorted(set(wrong))}")
        first = entries[0]["resource"]
        get(f"/fhir/{res}/{first['id']}", resource_type=res, name=f"{res}_read")
        get(f"/fhir/{res}/does-not-exist", expect=404, resource_type="OperationOutcome")
        if res == "Patient":
            patient_id = first["id"]
        elif patient_id:
            get(f"/fhir/{res}?patient={patient_id}", resource_type="Bundle")
    for path in documented:
        if path.startswith("/fhir/") and "{" not in path and path.split("/")[2] not in resources + ["metadata"]:
            problems.append(f"{path} is in openapi.yaml but the adapter does not serve it")

    code, text = _run(
        ["dotnet", "run", "--project", "tools/FhirCheck", "--no-build", "--", *files], config.generated_dir()
    )
    validator = []
    for line in text.splitlines():
        if line.startswith("{"):
            row = json.loads(line)
            validator.append({"file": Path(row["file"]).name, "resources": row["resources"], "errors": row["errors"]})
            problems += [f"FHIR validation ({Path(row['file']).name}): {e}" for e in row["errors"][:5]]
    if not validator:
        problems.append("FhirCheck did not run: " + text.strip()[-300:])
    return {"ok": not problems, "problems": problems[:40], "validator": validator}


# ---------- source facts for PHI and reconciliation ----------


def _hospital_number(value):
    digits = re.findall(r"\d+", str(value or ""))
    return str(int(max(digits, key=len))) if digits else str(value)


def _mapping_items(mapping):
    mappings = mapping.get("mappings") or []
    if isinstance(mappings, dict):
        return [(t, i) for t, rows in mappings.items() for i in rows or []]
    return [(None, i) for i in mappings]


def _rules(mapping):
    return {str(r.get("rule_id")) for r in mapping.get("data_rules") or [] if isinstance(r, dict)}


def _registry_records():
    rows, page = [], 1
    while True:
        body = httpx.get(f"{config.REGISTRY_URL}/patients", params={"pg": page, "sz": 100}, timeout=30).json()
        batch = next(v for v in body.values() if isinstance(v, list))
        if not batch:
            return rows
        rows += batch
        page += 1


def _hl7_records():
    records = []
    for path in sorted(config.ADT_OUTBOX.glob("*.hl7")):
        rec = {}
        for seg in path.read_bytes().decode().replace("\n", "\r").split("\r"):
            fields = seg.split("|")
            if len(fields) < 2:
                continue
            offset = 1 if fields[0] == "MSH" else 0
            for i, value in enumerate(fields[1:], start=1):
                key = f"{fields[0]}-{i + offset}"
                rec.setdefault(key, value)
                for c, comp in enumerate(value.split("^"), start=1):
                    rec.setdefault(f"{key}.{c}", comp)
        records.append(rec)
    return records


def _lab_tables():
    with psycopg.connect(**config.lab_db_settings()) as conn:
        out = {}
        for table in ("lb_pt", "lb_rslt", "lb_tst_codes"):
            cur = conn.execute(f"SELECT * FROM {table}")
            cols = [d.name for d in cur.description]
            out[table] = [dict(zip(cols, row)) for row in cur.fetchall()]
        return out


def pii_values(system, profile):
    """Real patient values from the source: name parts, birth dates and patient ids."""
    pii = [f["name"] for f in profile["fields"] if f.get("pii")]
    values = set()
    if system == "a":
        rows = [{k: r.get(k) for k in pii} for r in _registry_records()]
    elif system == "b":
        tables = _lab_tables()
        rows = [{f"lb_pt.{k}": v for k, v in r.items()} for r in tables["lb_pt"]]
    else:
        rows = _hl7_records()
    for row in rows:
        for key in pii:
            v = row.get(key)
            if v in (None, ""):
                continue
            text = str(v).strip()
            for token in re.findall(r"[A-Za-z][A-Za-z'-]{4,}", text):
                values.add(token)
            if re.fullmatch(r"[\d/-]{8,10}", text):
                values.add(text)
            if re.fullmatch(r"[A-Z]*-?\d{4,}", text):
                values.add(text)
    ignore = {"HOSP", "Testfamily", "Testgiven", "Unknown", "Street", "Avenue", "Suite"}
    return {v for v in values if v not in ignore}


def check_phi(system, out, profile, log_text):
    values = pii_values(system, profile)
    hits = []
    sources = [(str(p.relative_to(out)), p.read_text()) for p in sorted(out.rglob("*.cs")) if "/obj/" not in str(p)]
    sources.append(("adapter.log", log_text))
    for name, text in sources:
        found = sorted(v for v in values if re.search(rf"(?<![\w-]){re.escape(v)}(?![\w-])", text))
        if found:
            hits.append({"file": name, "count": len(found), "values": found[:10]})
    log_calls = []
    pattern = re.compile(r"(Log\w*\(|Console\.Write)")
    risky = re.compile(r"\.(Name|Family|Given|BirthDate|Address|Telecom)\b|Get\(\"|\[\"")
    for p in sorted(out.rglob("*.cs")):
        if "/obj/" in str(p):
            continue
        for n, line in enumerate(p.read_text().splitlines(), 1):
            if pattern.search(line) and risky.search(line):
                log_calls.append(f"{p.relative_to(out)}:{n}")
    problems = [f"{h['file']}: {h['count']} real patient values {h['values']}" for h in hits]
    problems += [f"logging may write patient data at {c}" for c in log_calls]
    return {"ok": not problems, "problems": problems, "values_checked": len(values)}


def expected_counts(system, mapping, inventory):
    """What the adapter should return, computed straight from the source after the data rules."""
    rules = _rules(mapping)
    if system == "a":
        rows = _registry_records()
        id_field = next(
            (i["source"] for _, i in _mapping_items(mapping) if "identifier" in str(i.get("path", ""))), "pt_id"
        )
        ids = [_hospital_number(r.get(id_field)) for r in rows]
        return {"Patient": len(rows)}, {pid: {} for pid in ids}

    if system == "b":
        tables = _lab_tables()
        pt, rs = tables["lb_pt"], tables["lb_rslt"]
        test_ids = set()
        if "exclude_test_records" in rules:
            for row in pt:
                if any(isinstance(v, str) and SUSPICIOUS.search(v) for v in row.values()):
                    test_ids.add(row["pt_ref"])
        if "drop_duplicates" in rules:
            dup = inventory["tables"]["lb_rslt"]["duplicates"] or {}
            skip = set(dup.get("ignoring_columns", [])) | {(dup.get("also_ignoring") or {}).get("column")}
            seen, unique = set(), []
            for row in sorted(rs, key=lambda r: r["rslt_id"]):
                key = tuple((v.strip() if isinstance(v, str) else v) for k, v in row.items() if k not in skip)
                if key not in seen:
                    seen.add(key)
                    unique.append(row)
            rs = unique
        rs = [r for r in rs if r["pt_ref"] not in test_ids]
        patients = [r for r in pt if r["pt_ref"] not in test_ids]
        per = {str(r["pt_ref"]): {"Observation": 0} for r in patients}
        for r in rs:
            per.setdefault(str(r["pt_ref"]), {"Observation": 0})["Observation"] += 1
        return {"Patient": len(patients), "Observation": len(rs)}, per

    records = _hl7_records()
    pid_field = "PID-3.2"
    for _, item in _mapping_items(mapping):
        src = str(item.get("source", ""))
        if item.get("resource") == "Patient" and "identifier" in str(item.get("path", "")) and src.startswith("PID-3"):
            if all(re.fullmatch(r"\d+", r.get(src, "") or "") for r in records[:20]):
                pid_field = src
    visits = {}
    for r in records:
        visits.setdefault(r.get("PV1-19"), _hospital_number(r.get(pid_field)))
    per = {}
    for pid in visits.values():
        per.setdefault(pid, {"Encounter": 0})["Encounter"] += 1
    return {"Patient": len(per), "Encounter": len(visits)}, per


def check_reconciliation(system, adapter, mapping, inventory, resources):
    expected, per_patient = expected_counts(system, mapping, inventory)
    problems, actual = [], {}
    for res in resources:
        actual[res] = httpx.get(f"{adapter.url}/fhir/{res}?_count=0", timeout=120).json().get("total")
        if res in expected and actual[res] != expected[res]:
            problems.append(f"{res}: adapter has {actual[res]}, source after data rules has {expected[res]}")
    bundle = httpx.get(f"{adapter.url}/fhir/Patient?_count=1000", timeout=120).json()
    got_ids = {e["resource"]["id"] for e in bundle.get("entry") or []}
    missing, extra = sorted(set(per_patient) - got_ids), sorted(got_ids - set(per_patient))
    if missing:
        problems.append(f"{len(missing)} expected patient ids are missing, for example {missing[:3]}")
    if extra:
        problems.append(f"{len(extra)} patient ids are not in the source, for example {extra[:3]}")
    mismatched = []
    for pid, counts in per_patient.items():
        for res, n in counts.items():
            got = httpx.get(f"{adapter.url}/fhir/{res}?patient={pid}&_count=0", timeout=60).json().get("total")
            if got != n:
                mismatched.append(f"Patient/{pid} {res}: adapter {got}, source {n}")
    if mismatched:
        problems.append(f"{len(mismatched)} per-patient counts differ, for example {mismatched[:3]}")
    return {
        "ok": not problems,
        "expected": expected,
        "actual": actual,
        "problems": problems,
        "patients_checked": len(per_patient),
    }


# ---------- repair ----------


def _test_strength(text):
    return text.count("[Fact]") + text.count("[Theory]"), text.count("Assert.")


def repair(system, out, stage, problems, round_no):
    files = {"src/Mapper.cs": (out / "src/Mapper.cs").read_text()}
    if stage == "build":
        bad_tests = {e["file"] for e in problems if isinstance(e, dict)} & set(TEST_FILES)
    elif stage == "phi":
        bad_tests = {f for f in TEST_FILES if any(str(p).startswith(f) for p in problems["problems"])}
    else:
        bad_tests = set()
    for f in bad_tests:
        files[f] = (out / f).read_text()
    profile = generate._profile(system)
    context = generate._context(system, profile, (config.system_dir(system) / "mapping.yaml").read_text())
    failing = json.dumps(problems, indent=1)[:9000]
    current = "\n\n".join(f"--- {name} ---\n{text}" for name, text in files.items())
    reply = complete(
        [
            {"role": "system", "content": FIX_PROMPT},
            {"role": "user", "content": f"{context}\n\nFailing check: {stage}\n{failing}\n\nCurrent files:\n{current}"},
        ],
        model_tier="strong",
        json_mode=True,
        purpose=f"validate {system} fix round {round_no} {stage}",
    )
    try:
        new_files = json.loads(reply).get("files") or {}
    except json.JSONDecodeError as exc:
        raise LLMError(f"the repair reply was not valid JSON: {exc}") from None
    changed, refused = [], []
    for name, text in new_files.items():
        if name not in EDITABLE:
            refused.append(f"{name}: not editable")
            continue
        if name in TEST_FILES:
            if name not in bad_tests:
                refused.append(f"{name}: tests can only change to fix a compile error or real patient values")
                continue
            before, after = _test_strength(files[name]), _test_strength(text)
            if after[0] < before[0] or after[1] < before[1] or "Skip" in text:
                refused.append(f"{name}: change would remove tests or assertions")
                continue
        (out / name).write_text(generate._strip_code(text) + "\n")
        changed.append(name)
    return {"changed": changed, "refused": refused}


# ---------- loop ----------


def _first_problems(result):
    if not result["build"]["ok"]:
        return "build", result["build"]["errors"]
    for stage in ("tests", "contract", "phi", "reconciliation"):
        stage_result = result.get(stage) or {}
        if not stage_result.get("ok", True):
            problems = stage_result.get("failures") or stage_result.get("problems")
            others = {
                s: (result.get(s) or {}).get("problems")
                for s in ("contract", "phi", "reconciliation")
                if s != stage and not (result.get(s) or {}).get("ok", True)
            }
            return stage, {"problems": problems, "other_failing_checks": others}
    return None, None


def _markdown(system, n, result):
    lines = [f"# Validation report: system {system}, round {n}", ""]
    b = result["build"]
    lines.append(f"- build: {'ok' if b['ok'] else 'failed'} ({b['error_count']} errors)")
    if "tests" in result:
        t = result["tests"]
        lines.append(f"- tests: {t['passed']}/{t['total']} passed")
    for stage in ("contract", "phi", "reconciliation"):
        if stage in result:
            s = result[stage]
            lines.append(f"- {stage}: {'ok' if s['ok'] else 'failed'} ({len(s.get('problems', []))} problems)")
    if result.get("repair"):
        lines.append(f"- repair: changed {result['repair']['changed']}, refused {result['repair']['refused']}")
    lines.append("")
    for stage in ("contract", "phi", "reconciliation"):
        for p in (result.get(stage) or {}).get("problems", [])[:10]:
            lines.append(f"  - {stage}: {p}")
    for f in (result.get("tests") or {}).get("failures", [])[:10]:
        lines.append(f"  - test failed: {f['test']}")
    return "\n".join(lines) + "\n"


def run(system):
    work = config.system_dir(system)
    out = config.generated_dir() / system
    if not (out / "src" / "Mapper.cs").exists():
        raise SystemExit(f"{out} has no generated code. Run the generate step first.")
    report_dir = work / "validate"
    report_dir.mkdir(parents=True, exist_ok=True)
    mapping = yaml.safe_load((work / "mapping.yaml").read_text())
    openapi = yaml.safe_load((work / "openapi.yaml").read_text())
    inventory = json.loads((work / "inventory.json").read_text())
    profile = json.loads((work / "profile.json").read_text())
    resources = json.loads((work / "generate.json").read_text())["resources"]
    _run(["dotnet", "build", "tools/FhirCheck", "-nologo", "-v", "q"], config.generated_dir())

    rounds = []
    for n in range(1, MAX_ROUNDS + 1):
        result = {"round": n, "build": check_build(out)}
        if result["build"]["ok"]:
            result["tests"] = check_tests(out, work)
            adapter = RunningAdapter(system, out, work)
            try:
                if adapter.wait():
                    result["contract"] = check_contract(adapter, work, openapi, resources)
                    result["reconciliation"] = check_reconciliation(system, adapter, mapping, inventory, resources)
                else:
                    result["contract"] = {"ok": False, "problems": ["adapter did not start or /health failed"]}
            finally:
                log_text = adapter.stop()
            if not result["contract"]["ok"] and "did not start" in result["contract"]["problems"][0]:
                result["contract"]["problems"].append(log_text[-1500:])
            result["phi"] = check_phi(system, out, profile, log_text)
        stage, problems = _first_problems(result)
        result["passed"] = stage is None
        if stage and n < MAX_ROUNDS:
            result["repair"] = {"stage": stage, **repair(system, out, stage, problems, n)}
        (report_dir / f"round_{n}.json").write_text(json.dumps(result, indent=1, default=str))
        (report_dir / f"round_{n}.md").write_text(_markdown(system, n, result))
        rounds.append(result)
        print(f"round {n}:", "; ".join(line[2:] for line in _markdown(system, n, result).splitlines()[2:8] if line))
        if result["passed"]:
            break

    final = rounds[-1]
    tests = final.get("tests") or {"total": 0, "passed": 0}
    summary = {
        "system": system,
        "status": "passed" if final["passed"] else "failed",
        "rounds": len(rounds),
        "repairs": sum(1 for r in rounds if r.get("repair")),
        "tests_total": tests["total"],
        "tests_passed": tests["passed"],
        "test_pass_rate": round(tests["passed"] / tests["total"], 3) if tests["total"] else 0.0,
        "checks": {s: (final.get(s) or {}).get("ok") for s in ("build", "tests", "contract", "phi", "reconciliation")},
    }
    (report_dir / "summary.json").write_text(json.dumps(summary, indent=1))
    return summary


def summarise(summary):
    checks = ", ".join(f"{k} {'ok' if v else 'FAILED' if v is False else '-'}" for k, v in summary["checks"].items())
    return (
        f"System {summary['system']}: validation {summary['status']} after {summary['rounds']} round(s), "
        f"{summary['repairs']} repair(s)\n  tests {summary['tests_passed']}/{summary['tests_total']}\n  {checks}"
    )
