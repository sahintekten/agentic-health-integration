"""Inventory for System A (REST API): compare the OpenAPI document with real responses."""

import re

import httpx

from agents.inventory.values import profile

PROBE_PATHS = ["/health", "/patients", "/openapi.json", "/docs"]
DOC_FORMAT = re.compile(r"\b([DMY]{2,4}[/-][DMY]{2,4}[/-][DMY]{2,4})\b")
OBSERVED_TO_DOC = {
    "date_dd/mm/yyyy": "DD/MM/YYYY",
    "date_mm/dd/yyyy": "MM/DD/YYYY",
    "date_iso": "YYYY-MM-DD",
}
JSON_TYPES = {
    "str": "string",
    "int": "integer",
    "float": "number",
    "bool": "boolean",
    "list": "array",
    "dict": "object",
}


def _schemas(spec):
    return (spec.get("components") or {}).get("schemas") or {}


def _best_schema(spec, keys):
    best, best_overlap = {}, 0
    for schema in _schemas(spec).values():
        props = schema.get("properties") or {}
        overlap = len(set(props) & set(keys))
        if overlap > best_overlap:
            best, best_overlap = props, overlap
    return best


def build(base_url, sample_size=20):
    with httpx.Client(base_url=base_url, timeout=15) as client:
        spec_resp = client.get("/openapi.json")
        spec = spec_resp.json() if spec_resp.status_code == 200 else {}
        endpoints = {}
        for path in PROBE_PATHS:
            r = client.get(path)
            endpoints[path] = r.status_code

        first = client.get("/patients", params={"pg": 1, "sz": 100}).json()
        list_key = next(k for k, v in first.items() if isinstance(v, list))
        records = list(first[list_key])
        page = 1
        while records and len(records) < first.get("tot", len(records)):
            page += 1
            more = client.get("/patients", params={"pg": page, "sz": 100}).json()[list_key]
            if not more:
                break
            records.extend(more)
        one_id = records[0][next(iter(records[0]))] if records else None
        if one_id is not None:
            endpoints["/patients/{id}"] = client.get(f"/patients/{one_id}").status_code

    doc_paths = sorted((spec.get("paths") or {}).keys())
    keys = list(dict.fromkeys(k for r in records for k in r))
    doc_fields = _best_schema(spec, keys)
    envelope_doc = _best_schema(spec, list(first.keys()))

    fields = {}
    for key in keys:
        values = [r.get(key) for r in records]
        doc = doc_fields.get(key)
        fields[key] = {
            "location": f"record field (inside '{list_key}')",
            "documented": doc is not None,
            "doc": {k: doc[k] for k in ("type", "description", "enum", "example") if k in doc} if doc else None,
            **profile(values),
        }
    for key, value in first.items():
        fields[key if key != list_key else list_key] = {
            "location": "response envelope",
            "documented": key in envelope_doc,
            "types": [type(value).__name__],
            "samples": [] if isinstance(value, list) else [value],
        }

    findings = []
    for path, status in endpoints.items():
        documented = any(re.sub(r"\{[^}]+\}", "{id}", p) == path for p in doc_paths) or path in doc_paths
        if status == 200 and not documented and path not in ("/openapi.json", "/docs"):
            findings.append(f"endpoint {path} works (HTTP 200) but is not in the OpenAPI document")
    for key in keys:
        doc = doc_fields.get(key)
        if doc is None:
            findings.append(f"field {key} is in the responses but not in the OpenAPI document")
            continue
        observed_types = {JSON_TYPES.get(t, t) for t in fields[key]["types"]}
        if doc.get("type") and observed_types and doc["type"] not in observed_types:
            findings.append(f"field {key}: doc type is {doc['type']} but responses have {sorted(observed_types)}")
        m = DOC_FORMAT.search(doc.get("description", "") + " " + str(doc.get("example", "")))
        observed = [OBSERVED_TO_DOC[f] for f in fields[key]["formats"] if f in OBSERVED_TO_DOC]
        if m and observed and m.group(1).upper() != observed[0]:
            findings.append(f"field {key}: doc says format {m.group(1)} but the values are {observed[0]}")
        if doc.get("enum"):
            extra = set(fields[key].get("value_counts", {})) - {str(e) for e in doc["enum"]}
            if extra:
                findings.append(f"field {key}: values {sorted(extra)} are not in the documented enum")
    for key in doc_fields:
        if key not in keys:
            findings.append(f"field {key} is in the OpenAPI document but never appears in responses")

    step = max(1, len(records) // sample_size)
    return {
        "system": "a",
        "kind": "rest-json",
        "source": base_url,
        "openapi": {"title": (spec.get("info") or {}).get("title"), "version": (spec.get("info") or {}).get("version")},
        "endpoints": endpoints,
        "documented_paths": doc_paths,
        "record_count": len(records),
        "fields": fields,
        "doc_vs_data": findings,
        "sample_records": records[::step][:sample_size],
    }
