"""System A: patient registry REST API (mock).

Serves patients from data/patients.json in the registry's own JSON shape.
The OpenAPI document is a hand-written file (app/openapi.yaml), not the one
FastAPI would generate, so it can be out of date like a real one.
"""

import json
import os
from pathlib import Path

import yaml
from fastapi import FastAPI, Query
from fastapi.openapi.docs import get_swagger_ui_html
from fastapi.responses import JSONResponse

DATA_FILE = Path(os.environ.get("REGISTRY_DATA", Path(__file__).parent.parent / "data" / "patients.json"))
SPEC_FILE = Path(__file__).parent / "openapi.yaml"

if not DATA_FILE.exists():
    raise SystemExit(f"{DATA_FILE} not found. Run 'make data' first.")

PATIENTS = json.loads(DATA_FILE.read_text(encoding="utf-8"))
BY_ID = {p["pt_id"]: p for p in PATIENTS}
SPEC = yaml.safe_load(SPEC_FILE.read_text(encoding="utf-8"))

app = FastAPI(openapi_url=None, docs_url=None, redoc_url=None)


@app.get("/health")
def health():
    return {"status": "UP", "recs": len(PATIENTS)}


@app.get("/patients")
def list_patients(pg: int = Query(1, ge=1), sz: int = Query(20, ge=1, le=100)):
    start = (pg - 1) * sz
    return {"pg": pg, "sz": sz, "tot": len(PATIENTS), "recs": PATIENTS[start : start + sz]}


@app.get("/patients/{pt_id}")
def get_patient(pt_id: str):
    patient = BY_ID.get(pt_id)
    if patient is None:
        return JSONResponse(status_code=404, content={"err": "PT_NOT_FOUND", "msg": f"no patient with id {pt_id}"})
    return patient


@app.get("/openapi.json", include_in_schema=False)
def openapi():
    return SPEC


@app.get("/docs", include_in_schema=False)
def docs():
    return get_swagger_ui_html(openapi_url="/openapi.json", title="Patient Registry API")
