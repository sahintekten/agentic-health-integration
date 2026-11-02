"""Dashboard for the agent pipeline, the adapters and the gateway.

Pages:
  /        overview per system: steps, F1 scores, validation, time and tokens, gateway health
  /run     live pipeline view (reads workspace/progress.jsonl)
  /links   review page for probable patient links found by the gateway

Run with: make dashboard  (http://localhost:8050)
"""

import asyncio
import html
import json
import os
from pathlib import Path

import httpx
from fastapi import FastAPI, Form
from fastapi.responses import HTMLResponse, RedirectResponse, StreamingResponse

ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = Path(os.environ.get("AGENTS_WORKSPACE", ROOT / "workspace"))
RESULTS = ROOT / "eval" / "results"
GATEWAY = os.environ.get("GATEWAY_URL", "http://localhost:9000")
SYSTEMS = {"a": "patient-registry-api", "b": "lab-db", "c": "adt-feed", "d": "appointments-export"}
STEPS = ["discover", "design", "generate", "validate"]

app = FastAPI(title="Agentic health integration dashboard")

STYLE = """
body { font-family: system-ui, sans-serif; margin: 0; background: #f6f7f9; color: #1d2329; }
header { background: #1d2329; color: #fff; padding: 12px 24px; display: flex; gap: 24px; align-items: center; }
header a { color: #cfd8e3; text-decoration: none; } header a:hover { color: #fff; }
main { padding: 16px 24px; max-width: 1200px; }
table { border-collapse: collapse; width: 100%; background: #fff; margin-bottom: 20px; }
th, td { border-bottom: 1px solid #e3e6ea; padding: 6px 8px; text-align: left; font-size: 14px; }
th { background: #eef1f4; }
.ok { color: #1a7f37; font-weight: 600; } .bad { color: #cf222e; font-weight: 600; } .wait { color: #9a6700; }
.cards { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 20px; }
.card { background: #fff; border: 1px solid #e3e6ea; border-radius: 6px; padding: 10px 14px; min-width: 150px; }
.card b { display: block; font-size: 22px; }
pre { background: #0d1117; color: #c9d1d9; padding: 12px; height: 60vh; overflow: auto; font-size: 13px; }
button { padding: 4px 10px; cursor: pointer; }
"""


def page(title, body, refresh=None):
    meta = f'<meta http-equiv="refresh" content="{refresh}">' if refresh else ""
    return HTMLResponse(
        f"<!doctype html><html><head><meta charset='utf-8'><title>{title}</title>{meta}"
        f"<style>{STYLE}</style></head><body><header><b>Agentic health integration</b>"
        f"<a href='/'>Overview</a><a href='/run'>Pipeline run</a><a href='/links'>Patient links</a></header>"
        f"<main><h2>{title}</h2>{body}</main></body></html>"
    )


def read_json(path):
    try:
        return json.loads(Path(path).read_text())
    except (OSError, ValueError):
        return None


def badge(ok, yes="ok", no="failed"):
    if ok is None:
        return "<span class='wait'>-</span>"
    return f"<span class='ok'>{yes}</span>" if ok else f"<span class='bad'>{no}</span>"


def gateway_health():
    try:
        return httpx.get(f"{GATEWAY}/health", timeout=30).json()
    except (httpx.HTTPError, ValueError):
        return None


@app.get("/", response_class=HTMLResponse)
def overview():
    metrics = read_json(WORKSPACE / "metrics.json") or {}
    rows = []
    for s, name in SYSTEMS.items():
        work = WORKSPACE / s
        if not work.exists():
            continue
        reviews = read_json(work / "review.json") or {}
        steps = " ".join(
            f"<span class='{'ok' if step in reviews else 'wait'}' title='{step}'>{step[:3]}</span>" for step in STEPS
        )
        score = read_json(RESULTS / f"{s}.json") or {}
        summary = read_json(work / "validate" / "summary.json") or {}
        m = metrics.get(s, {})
        llm = m.get("llm", {})
        rows.append(
            f"<tr><td><b>{s}</b> {name}</td><td>{steps}</td>"
            f"<td>{score.get('f1', '-')}</td><td>{(score.get('rules') or {}).get('f1', '-')}</td>"
            f"<td>{badge(summary.get('status') == 'passed' if summary else None, 'passed')}</td>"
            f"<td>{summary.get('rounds', '-')} / {summary.get('repairs', '-')}</td>"
            f"<td>{summary.get('tests_passed', '-')}/{summary.get('tests_total', '-')}</td>"
            f"<td>{m.get('total_seconds', '-')}</td><td>{llm.get('calls', '-')}</td>"
            f"<td>{llm.get('prompt_tokens', '-')} / {llm.get('completion_tokens', '-')}</td></tr>"
        )
    table = (
        "<table><tr><th>System</th><th>Steps approved</th><th>Field F1</th><th>Rules F1</th><th>Validation</th>"
        "<th>Rounds / repairs</th><th>Tests</th><th>Time (s)</th><th>LLM calls</th><th>Tokens in / out</th></tr>"
        + "".join(rows)
        + "</table>"
    )

    health = gateway_health()
    if health is None:
        gw = (
            f"<p class='bad'>Gateway not reachable at {html.escape(GATEWAY)}. "
            "Start it with <code>make up-adapters</code>.</p>"
        )
    else:
        idx = health["index"]
        cards = "".join(
            f"<div class='card'>{label}<b>{idx.get(key, 0)}</b></div>"
            for label, key in [
                ("Persons", "persons"),
                ("Exact links", "exact"),
                ("Probable (pending)", "probable_pending"),
                ("Probable (approved)", "probable_approved"),
                ("Unlinked", "unlinked"),
            ]
        )
        adapter_rows = []
        for name, a in health["adapters"].items():
            d = a["health"].get("details") or {}
            last = a.get("last_validation") or {}
            recon = last.get("reconciliation") or {}
            adapter_rows.append(
                f"<tr><td><b>{name}</b> {html.escape(a['system'])}</td>"
                f"<td>{badge(a['health']['status'] == 'UP', 'UP', 'DOWN')}</td>"
                f"<td>{d.get('patients', '-')}</td><td>{d.get('observations', '-')}</td>"
                f"<td>{d.get('encounters', '-')}</td>"
                f"<td>{badge(recon.get('ok'), 'matches source')}</td>"
                f"<td>{html.escape(json.dumps(recon.get('expected', {})))}</td></tr>"
            )
        gw = (
            f"<div class='cards'>{cards}</div><table><tr><th>Adapter</th><th>Status</th><th>Patients</th>"
            "<th>Observations</th><th>Encounters</th><th>Last reconciliation</th><th>Expected from source</th></tr>"
            + "".join(adapter_rows)
            + "</table>"
        )
    return page("Overview", f"<h3>Agent pipeline</h3>{table}<h3>Gateway</h3>{gw}", refresh=15)


@app.get("/run", response_class=HTMLResponse)
def run_view():
    body = """
<p>Live steps from <code>workspace/progress.jsonl</code>. Start a run in a terminal, for example
<code>make demo-add-system SYSTEM=d</code>, and watch it here.</p>
<pre id="log"></pre>
<script>
const log = document.getElementById("log");
const source = new EventSource("/events");
source.onmessage = (e) => {
  const ev = JSON.parse(e.data);
  const detail = ev.detail ? "  - " + ev.detail : "";
  const line = `${ev.ts.slice(11, 19)}  [${ev.system}] ${ev.step.padEnd(9)} ${ev.event}${detail}`;
  log.textContent += line + "\\n";
  log.scrollTop = log.scrollHeight;
};
</script>"""
    return page("Pipeline run", body)


@app.get("/events")
async def events():
    path = WORKSPACE / "progress.jsonl"

    async def stream():
        lines = path.read_text().splitlines() if path.exists() else []
        for line in lines[-40:]:
            yield f"data: {line}\n\n"
        position = path.stat().st_size if path.exists() else 0
        while True:
            await asyncio.sleep(0.5)
            if not path.exists():
                continue
            size = path.stat().st_size
            if size < position:
                position = 0
            if size > position:
                with path.open() as f:
                    f.seek(position)
                    for line in f.read().splitlines():
                        if line.strip():
                            yield f"data: {line}\n\n"
                position = size

    return StreamingResponse(stream(), media_type="text/event-stream")


def _patient_summary(patient_id):
    try:
        p = httpx.get(f"{GATEWAY}/fhir/Patient/{patient_id}", timeout=30).json()
    except (httpx.HTTPError, ValueError):
        return "-"
    name = (p.get("name") or [{}])[0]
    text = f"{' '.join(name.get('given', []))} {name.get('family', '')}".strip()
    return f"{html.escape(text)} &middot; {p.get('birthDate', '?')}"


@app.get("/links", response_class=HTMLResponse)
def links_view():
    try:
        links = httpx.get(f"{GATEWAY}/admin/links", params={"type": "probable"}, timeout=30).json()
    except (httpx.HTTPError, ValueError):
        return page("Patient links", "<p class='bad'>Gateway not reachable.</p>")
    rows = []
    for link in links:
        record = f"{link['system']}-{link['sourceId']}" if link["status"] != "approved" else link["personId"]
        buttons = "".join(
            f"<form method='post' action='/links/{link['system']}/{link['sourceId']}' style='display:inline'>"
            f"<input type='hidden' name='status' value='{status}'><button>{label}</button></form> "
            for status, label in [("approved", "Approve"), ("rejected", "Reject"), ("pending", "Reset")]
            if status != link["status"]
        )
        rows.append(
            f"<tr><td>system {link['system']}: {link['sourceId']}<br><small>{_patient_summary(record)}</small></td>"
            f"<td>registry: {link['personId']}<br><small>{_patient_summary(link['personId'])}</small></td>"
            f"<td>{html.escape(link['reason'])}</td><td><b>{link['status']}</b></td><td>{buttons}</td></tr>"
        )
    body = (
        "<p>The gateway links records across systems by hospital number. When a record has another id but the "
        "same name and birth date, it is a <i>probable</i> link. Its data is only merged after you approve it.</p>"
        "<table><tr><th>Record</th><th>Probable match</th><th>Why</th><th>Status</th><th>Action</th></tr>"
        + ("".join(rows) or "<tr><td colspan=5>No probable links.</td></tr>")
        + "</table>"
    )
    return page("Patient links", body)


@app.post("/links/{system}/{source_id}")
def set_link(system: str, source_id: str, status: str = Form(...)):
    httpx.post(f"{GATEWAY}/admin/links/{system}/{source_id}", params={"status": status}, timeout=30)
    return RedirectResponse("/links", status_code=303)
