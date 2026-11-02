"""Design agent: turn a system profile into a FHIR R4 mapping, an adapter API spec and an ID strategy.

Reads workspace/<system>/profile.json and inventory.json. Writes mapping.yaml,
openapi.yaml and id_strategy.md in the same folder.
"""

import json
from datetime import datetime, timezone

import yaml

from agents import config
from agents.llm import LLMError, complete

MAX_PROMPT_CHARS = 24000

MAPPING_PROMPT = """You design the mapping from one legacy hospital system to HL7 FHIR R4.
You get a profile of the system made by another agent, and a FHIR R4 reference.

Return one JSON object with these keys:
- "system": the system name
- "interface": one line about how the system is reached
- "fhir_version": "R4"
- "identifier_systems": object, short name -> a URI you choose under http://hospital.example.org/fhir/sid/
- "known_traps": list of short sentences about things a naive mapping would get wrong
- "patient_link": {"rule": how to get the shared hospital number from this system's patient id, "example": text}
- "mappings": the field mappings.
    For a database: an object keyed by TABLE name. Each value is a list of mapping items. "source" is the column name.
    For anything else: one list of mapping items. "source" is the field name exactly as in the profile.
- "unit_map": object, legacy unit -> UCUM code. Only if the system stores units.
- "events": object, message event -> what it means for the resources. Only for message feeds.

A mapping item is ONE of these:
  {"source": X, "resource": "Patient" | "Observation" | "Encounter" | "Practitioner",
   "path": a FHIR path like Patient.name[0].family, "transform": short text,
   "value_map": {source code: FHIR code}  (only for coded fields)}
  {"source": X, "ignore": reason}  for data that is not stored in any resource
     (paging fields, message control data, audit columns, unused columns)
  {"source": X, "lookup": purpose}  for columns of a lookup table that are only used to fill other elements
A source may have more than one mapping item when it fills more than one FHIR element.
Every field in the profile must appear at least once. Do not invent fields."""

ID_PROMPT = """You write short technical notes for a hospital integration team. Plain English, short sentences.
Write a markdown note titled "Patient ID strategy: <system name>" with these sections:
## Source identifier, ## Normalisation steps (numbered), ## Linking to the other systems, ## Edge cases.
Keep it under 300 words. Use only the facts you are given."""


def _profile_view(profile, inventory, samples=2):
    fields = []
    for f in profile["fields"]:
        inv = inventory["fields"].get(f["name"], {})
        entry = {k: f.get(k) for k in ("name", "meaning", "data_type", "format", "notes") if f.get(k)}
        for k in ("table", "location", "events", "component_of"):
            if inv.get(k):
                entry[k] = inv[k]
        entry["samples"] = inv.get("samples", [])[:samples]
        if inv.get("value_counts"):
            entry["values"] = list(inv["value_counts"])[:10]
        fields.append(entry)
    view = {
        "system": profile["name"],
        "kind": profile["kind"],
        "summary": profile.get("summary"),
        "patient_identifier": profile.get("patient_identifier"),
        "issues": profile.get("issues", []) + profile.get("inventory_findings", []),
        "fields": fields,
    }
    for key in ("message_types", "join_candidates", "endpoints"):
        if key in inventory:
            view[key] = inventory[key]
    if "tables" in inventory:
        view["tables"] = {
            t: {"rows": v["row_count"], "duplicates": v["duplicates"]} for t, v in inventory["tables"].items()
        }
    return view


def _fit(view_fn):
    for samples in (2, 1, 0):
        text = json.dumps(view_fn(samples), ensure_ascii=False, default=str)
        if len(text) <= MAX_PROMPT_CHARS:
            return text
    return text


def _other_identifiers(system):
    others = {}
    for other in config.SYSTEMS:
        path = config.workspace() / other / "profile.json"
        if other != system and path.exists():
            others[config.SYSTEMS[other]] = json.loads(path.read_text()).get("patient_identifier")
    return others


def _mapping_items(mappings):
    if isinstance(mappings, dict):
        for table, items in mappings.items():
            for item in items or []:
                yield table, item
    else:
        for item in mappings or []:
            yield None, item


def unmapped_fields(profile, mapping):
    mapped = set()
    for table, item in _mapping_items(mapping.get("mappings")):
        source = str(item.get("source", "")).lower()
        mapped.add(f"{table}.{source}".lower() if table else source)
    return [f["name"] for f in profile["fields"] if f["name"].lower() not in mapped]


def adapter_openapi(profile, mapping):
    """Deterministic OpenAPI 3 spec for the FHIR adapter of this system."""
    resources = sorted(
        {item.get("resource") for _, item in _mapping_items(mapping.get("mappings")) if item.get("resource")}
    )
    resources = [r for r in resources if r in ("Patient", "Observation", "Encounter", "Practitioner")]
    search = {
        "Patient": [("identifier", "Patient identifier, as system|value or just value"), ("birthdate", "YYYY-MM-DD")],
        "Observation": [("patient", "Patient id"), ("code", "LOINC code, as system|code"), ("date", "Date filter")],
        "Encounter": [("patient", "Patient id"), ("status", "Encounter status"), ("date", "Date filter")],
        "Practitioner": [("identifier", "Practitioner identifier")],
    }
    fhir_json = {"application/fhir+json": {"schema": {"$ref": "#/components/schemas/Bundle"}}}
    paths = {
        "/fhir/metadata": {
            "get": {
                "summary": "CapabilityStatement of this adapter",
                "responses": {"200": {"description": "CapabilityStatement"}},
            }
        }
    }
    for r in resources:
        params = [{"name": n, "in": "query", "description": d, "schema": {"type": "string"}} for n, d in search[r]]
        params.append({"name": "_count", "in": "query", "schema": {"type": "integer", "default": 20}})
        paths[f"/fhir/{r}"] = {
            "get": {
                "summary": f"Search {r} resources",
                "parameters": params,
                "responses": {"200": {"description": "searchset Bundle", "content": fhir_json}},
            }
        }
        paths[f"/fhir/{r}/{{id}}"] = {
            "get": {
                "summary": f"Read one {r}",
                "parameters": [{"name": "id", "in": "path", "required": True, "schema": {"type": "string"}}],
                "responses": {
                    "200": {
                        "description": r,
                        "content": {"application/fhir+json": {"schema": {"$ref": "#/components/schemas/Resource"}}},
                    },
                    "404": {
                        "description": "Not found",
                        "content": {
                            "application/fhir+json": {"schema": {"$ref": "#/components/schemas/OperationOutcome"}}
                        },
                    },
                },
            }
        }
    return {
        "openapi": "3.0.3",
        "info": {
            "title": f"{profile['name']} FHIR adapter",
            "version": "0.1.0",
            "description": f"Exposes {profile['name']} as FHIR R4 resources: {', '.join(resources)}.",
        },
        "paths": paths,
        "components": {
            "schemas": {
                "Resource": {
                    "type": "object",
                    "required": ["resourceType"],
                    "properties": {"resourceType": {"type": "string"}, "id": {"type": "string"}},
                },
                "Bundle": {
                    "type": "object",
                    "properties": {
                        "resourceType": {"type": "string", "enum": ["Bundle"]},
                        "type": {"type": "string", "enum": ["searchset"]},
                        "total": {"type": "integer"},
                        "entry": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {"resource": {"$ref": "#/components/schemas/Resource"}},
                            },
                        },
                    },
                },
                "OperationOutcome": {
                    "type": "object",
                    "properties": {"resourceType": {"type": "string"}, "issue": {"type": "array", "items": {}}},
                },
            }
        },
    }


def run(system):
    out = config.system_dir(system)
    profile_path = out / "profile.json"
    if not profile_path.exists():
        raise SystemExit(f"{profile_path} not found. Run the discover step first.")
    profile = json.loads(profile_path.read_text())
    inventory = json.loads((out / "inventory.json").read_text())
    reference = (config.FHIR_REF / "r4_reference.md").read_text()

    profile_text = _fit(lambda n: _profile_view(profile, inventory, n))
    messages = [
        {"role": "system", "content": MAPPING_PROMPT},
        {"role": "user", "content": f"FHIR R4 reference:\n{reference}\n\nSystem profile:\n{profile_text}"},
    ]
    reply = complete(messages, model_tier="strong", json_mode=True, purpose=f"design mapping {system}")
    try:
        mapping = json.loads(reply)
    except json.JSONDecodeError as exc:
        raise LLMError(f"the model did not return valid JSON for the mapping: {exc}") from None
    mapping.setdefault("system", profile["name"])
    mapping.setdefault("fhir_version", "R4")
    mapping.setdefault("mappings", [])
    mapping["generated_at"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
    missing = unmapped_fields(profile, mapping)
    if missing:
        mapping["unmapped_fields"] = missing
    (out / "mapping.yaml").write_text(yaml.safe_dump(mapping, sort_keys=False, allow_unicode=True, width=110))

    (out / "openapi.yaml").write_text(
        yaml.safe_dump(adapter_openapi(profile, mapping), sort_keys=False, allow_unicode=True, width=110)
    )

    id_facts = {
        "system": profile["name"],
        "patient_identifier": profile.get("patient_identifier"),
        "patient_link": mapping.get("patient_link"),
        "identifier_mappings": [
            item
            for _, item in _mapping_items(mapping["mappings"])
            if "identifier" in str(item.get("path", "")) or "subject" in str(item.get("path", ""))
        ],
        "issues": [i for i in profile.get("issues", []) if "id" in i.lower() or "patient" in i.lower()],
        "join_candidates": inventory.get("join_candidates"),
        "other_systems": _other_identifiers(system),
    }
    note = complete(
        [
            {"role": "system", "content": ID_PROMPT},
            {"role": "user", "content": json.dumps(id_facts, ensure_ascii=False, default=str)},
        ],
        model_tier="strong",
        purpose=f"design id strategy {system}",
    )
    (out / "id_strategy.md").write_text(note.strip() + "\n")
    return mapping


def summarise(mapping, system):
    counts, ignored = {}, 0
    for _, item in _mapping_items(mapping.get("mappings")):
        if "resource" in item:
            counts[item["resource"]] = counts.get(item["resource"], 0) + 1
        else:
            ignored += 1
    lines = [f"System {system}: mapping for {mapping.get('system')}"]
    lines += [f"  {r}: {n} mapped fields" for r, n in sorted(counts.items())]
    lines.append(f"  ignored or lookup: {ignored}")
    if mapping.get("unmapped_fields"):
        lines.append(f"  not mapped by the model: {', '.join(mapping['unmapped_fields'])}")
    for trap in mapping.get("known_traps", [])[:8]:
        lines.append(f"  trap: {trap}")
    lines.append("  files: mapping.yaml, openapi.yaml, id_strategy.md")
    return "\n".join(lines)
