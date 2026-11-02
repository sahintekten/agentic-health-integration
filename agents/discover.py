"""Discover agent: inventory one system, then ask the LLM to describe every field.

Writes workspace/<system>/inventory.json (deterministic) and profile.json
(LLM guesses plus automatic checks against the inventory).
"""

import json
from datetime import datetime, timezone

from agents import config
from agents.inventory import adt, csvfiles, labdb, registry
from agents.llm import LLMError, complete

MAX_PROMPT_CHARS = 24000  # keeps prompts well under 8k tokens

SYSTEM_PROMPT = """You document legacy hospital systems before they are integrated with HL7 FHIR R4.
You get a machine-made inventory of one system. Describe every field in it.
Use only the evidence in the inventory. When you are unsure, lower the confidence.

Return one JSON object with these keys:
- "summary": 2 or 3 short sentences about the system.
- "patient_identifier": {"field": name from the inventory, "format": text, "example": a real sample,
  "normalised": how to turn it into a plain hospital number, "notes": text}
- "fields": a list with one entry for EVERY field in the inventory, in the same order. Each entry:
  {"name": exactly as in the inventory, "meaning": short text,
   "data_type": one of string, integer, decimal, date, datetime, code, identifier, text, boolean, array, object,
   "format": for example DD/MM/YYYY, YYYYMMDD, epoch seconds, HL7 TS, free text, or "",
   "pii": true or false, "confidence": number from 0 to 1, "notes": short text or ""}
- "issues": a list of short sentences about data quality or documentation problems to know before mapping."""

FORMAT_TOKENS = {
    "date_dd/mm/yyyy": "DD/MM/YYYY",
    "date_mm/dd/yyyy": "MM/DD/YYYY",
    "date_yyyymmdd": "YYYYMMDD",
    "datetime_yyyymmddhhmmss": "YYYYMMDDHHMMSS",
    "date_iso": "YYYY-MM-DD",
    "date_dd.mm.yyyy": "DD.MM.YYYY",
    "datetime_dd.mm.yyyy_hh:mm": "DD.MM.YYYY",
    "epoch_seconds": "EPOCH",
}


def build_inventory(system):
    if system == "a":
        return registry.build(config.REGISTRY_URL)
    if system == "b":
        return labdb.build(config.lab_db_settings())
    if system == "c":
        return adt.build(config.ADT_OUTBOX, config.ADT_HOST, config.ADT_PORT)
    if system == "d":
        return csvfiles.build(config.APPT_EXPORT)
    raise ValueError(f"unknown system {system!r}")


def llm_view(inventory, samples=4):
    """A compact copy of the inventory for the prompt."""
    keep = ("location", "table", "db_type", "types", "null_rate", "distinct", "shapes", "formats", "min", "max")
    keep += ("documented", "doc", "events", "component_of", "values_with_extra_spaces", "suspicious_values")
    fields = []
    for name, f in inventory["fields"].items():
        entry = {"name": name, **{k: f[k] for k in keep if k in f and f[k] not in (None, [], {})}}
        entry["samples"] = f.get("samples", [])[:samples]
        if "value_counts" in f:
            entry["value_counts"] = dict(list(f["value_counts"].items())[:8])
        fields.append(entry)
    view = {k: v for k, v in inventory.items() if k not in ("fields", "sample_records", "tables")}
    if "tables" in inventory:
        view["tables"] = {
            t: {"row_count": v["row_count"], "duplicates": v["duplicates"]} for t, v in inventory["tables"].items()
        }
    view["fields"] = fields
    return view


def _prompt(inventory):
    for samples in (4, 3, 2, 1):
        text = json.dumps(llm_view(inventory, samples), ensure_ascii=False, default=str)
        if len(text) <= MAX_PROMPT_CHARS:
            return text
    return text


def check_profile(profile, inventory):
    """Compare the LLM's guesses with the deterministic inventory."""
    names = list(inventory["fields"])
    described = {f.get("name"): f for f in profile.get("fields", []) if isinstance(f, dict)}
    checks = []
    for name in described:
        if name not in inventory["fields"]:
            checks.append({"field": name, "check": "unknown_field", "detail": "not in the inventory"})
    fields = []
    for name in names:
        guess = described.get(name)
        if guess is None:
            checks.append({"field": name, "check": "missing", "detail": "the model did not describe this field"})
            guess = {"name": name, "meaning": "", "confidence": 0.0}
        observed = [FORMAT_TOKENS[x] for x in inventory["fields"][name].get("formats", []) if x in FORMAT_TOKENS]
        said = str(guess.get("format", "")).upper().replace(" ", "")
        if observed and observed[0] not in said:
            checks.append(
                {"field": name, "check": "format_disagrees", "detail": f"inventory {observed[0]}, model {said or '-'}"}
            )
        fields.append(guess)
    pid = (profile.get("patient_identifier") or {}).get("field")
    if pid and pid not in inventory["fields"]:
        checks.append({"field": pid, "check": "unknown_patient_identifier", "detail": "not in the inventory"})
    return fields, checks


def run(system):
    out = config.system_dir(system)
    inventory = build_inventory(system)
    (out / "inventory.json").write_text(json.dumps(inventory, indent=1, ensure_ascii=False, default=str))

    messages = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": f"Inventory of system {system} ({config.SYSTEMS[system]}):\n{_prompt(inventory)}"},
    ]
    reply = complete(messages, model_tier="fast", json_mode=True, purpose=f"discover {system}")
    try:
        profile = json.loads(reply)
    except json.JSONDecodeError as exc:
        raise LLMError(f"the model did not return valid JSON for the profile: {exc}") from None

    fields, checks = check_profile(profile, inventory)
    result = {
        "system": system,
        "name": config.SYSTEMS[system],
        "kind": inventory["kind"],
        "generated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "summary": profile.get("summary", ""),
        "patient_identifier": profile.get("patient_identifier") or {},
        "fields": fields,
        "issues": profile.get("issues", []),
        "inventory_findings": inventory.get("doc_vs_data", []),
        "checks": checks,
    }
    (out / "profile.json").write_text(json.dumps(result, indent=1, ensure_ascii=False))
    return result


def summarise(profile):
    lines = [f"System {profile['system']} ({profile['name']}): {profile['summary']}"]
    pid = profile.get("patient_identifier") or {}
    lines.append(f"Patient id: {pid.get('field')}  format: {pid.get('format')}  example: {pid.get('example')}")
    lines.append(f"{'field':<22} {'type':<11} {'format':<16} pii  conf  meaning")
    for f in profile["fields"]:
        lines.append(
            f"{str(f.get('name'))[:22]:<22} {str(f.get('data_type', ''))[:11]:<11} "
            f"{str(f.get('format', ''))[:16]:<16} {'yes' if f.get('pii') else 'no ':<4} "
            f"{float(f.get('confidence') or 0):.2f}  {str(f.get('meaning', ''))[:60]}"
        )
    for issue in profile.get("issues", []):
        lines.append(f"issue: {issue}")
    for c in profile.get("checks", []):
        lines.append(f"check: {c['field']}: {c['check']} ({c['detail']})")
    return "\n".join(lines)
