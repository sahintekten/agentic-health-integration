"""System C seed data: mock-systems/adt-feed/data/messages.jsonl

Builds HL7 v2.5 ADT messages (A01 admit, A08 update, A03 discharge) from
Synthea inpatient and emergency encounters. One JSON object per line, with
the HL7 text in the "hl7" field (segments separated by carriage returns).
"""

import json
import random
from datetime import datetime

from common import GO_LIVE, MOCK_SYSTEMS, SEED

OUT = MOCK_SYSTEMS / "adt-feed" / "data" / "messages.jsonl"

CLASSES = {"inpatient": "I", "emergency": "E"}
STATES = {"Massachusetts": "MA"}
WARDS = ["MED1", "MED2", "SURG", "CARD", "ORTH", "RESP"]


def parse_ts(iso):
    return datetime.fromisoformat(iso.replace("Z", "+00:00"))


def hl7_ts(dt):
    # sent without a timezone offset, even though the value is UTC
    return dt.strftime("%Y%m%d%H%M%S")


def esc(value):
    return (
        value.replace("\\", "\\E\\")
        .replace("|", "\\F\\")
        .replace("^", "\\S\\")
        .replace("&", "\\T\\")
        .replace("~", "\\R\\")
    )


def segment(name, fields):
    while fields and fields[-1] == "":
        fields.pop()
    return "|".join([name] + fields)


def pid_segment(p, number, rng):
    family, given, middle = esc(p["LAST"]), esc(p["FIRST"]), esc(p["MIDDLE"])
    name = f"{family}^{given}^{middle}" if middle else f"{family}^{given}"
    if rng.random() < 0.03:
        name = name.upper()
    zip_code = "" if p["ZIP"] == "00000" else p["ZIP"]
    address = f"{esc(p['ADDRESS'])}^^{esc(p['CITY'])}^{STATES.get(p['STATE'], p['STATE'])}^{zip_code}^USA"
    sex = "U" if rng.random() < 0.02 else p["GENDER"]
    fields = [""] * 30
    fields[0] = "1"
    fields[2] = f"HOSP^{number}^MR"
    fields[4] = name
    fields[6] = p["BIRTHDATE"].replace("-", "")
    fields[7] = sex
    fields[10] = address
    fields[15] = p["MARITAL"]
    return segment("PID", fields)


def pv1_segment(enc, cls, location, attending, visit_no, start, end):
    fields = [""] * 45
    fields[0] = "1"
    fields[1] = cls
    fields[2] = location
    fields[3] = "E" if cls == "E" else "R"
    fields[6] = attending
    fields[9] = "EME" if cls == "E" else "MED"
    fields[18] = visit_no
    if end is not None:
        fields[35] = "01"
    fields[43] = hl7_ts(start)
    fields[44] = hl7_ts(end) if end is not None else ""
    return segment("PV1", fields)


def zen_segment(enc):
    reason = ""
    if enc["REASONCODE"]:
        reason = f"{enc['REASONCODE']}^{esc(enc['REASONDESCRIPTION'])}^SCT"
    return segment("ZEN", ["1", f"{enc['CODE']}^{esc(enc['DESCRIPTION'])}^SCT", reason])


def attending_of(enc, providers):
    prov = providers.get(enc["PROVIDER"])
    if not prov:
        return ""
    parts = prov["NAME"].split()
    doctor_id = "D" + str(int(enc["PROVIDER"].replace("-", "")[:8], 16) % 90000 + 10000)
    return f"{doctor_id}^{esc(parts[-1])}^{esc(' '.join(parts[:-1]))}"


def build(ctx):
    rng = random.Random(SEED + 3)
    by_id = {p["Id"]: p for p in ctx.patients}
    encounters = sorted(
        (e for e in ctx.encounters if e["ENCOUNTERCLASS"] in CLASSES and e["START"] >= GO_LIVE),
        key=lambda e: (e["START"], e["Id"]),
    )

    events = []
    visits = []
    for k, enc in enumerate(encounters):
        p = by_id[enc["PATIENT"]]
        number = ctx.patient_no[p["Id"]]
        cls = CLASSES[enc["ENCOUNTERCLASS"]]
        visit_no = f"V{1000001 + k}"
        start, stop = parse_ts(enc["START"]), parse_ts(enc["STOP"])
        if cls == "I":
            ward = rng.choice(WARDS)
            room = rng.randint(1, 30)
            location = f"{ward}^{room:03d}^A^HOSP"
            moved = f"{ward}^{room:03d}^B^HOSP"
        else:
            location = moved = "ED^^^HOSP"
        attending = attending_of(enc, ctx.providers)
        visits.append(
            {
                "synthea_encounter_id": enc["Id"],
                "synthea_patient_id": p["Id"],
                "adt_visit_no": visit_no,
                "patient_class": cls,
            }
        )

        common = (enc, p, number, cls, attending, visit_no, start)
        events.append((start, "A01", common, location, None))
        if rng.random() < 0.25 and stop > start:
            when = start + (stop - start) * rng.uniform(0.2, 0.8)
            events.append((when, "A08", common, moved, None))
        events.append((stop, "A03", common, moved, stop))

    events.sort(key=lambda e: (e[0], e[1]))
    lines = []
    for n, (when, event, common, location, end) in enumerate(events, start=1):
        enc, p, number, cls, attending, visit_no, start = common
        control_id = f"ADT{n:08d}"
        structure = "ADT_A03" if event == "A03" else "ADT_A01"
        segments = [
            segment(
                "MSH",
                [
                    "^~\\&",
                    "PAS",
                    "HOSP",
                    "ADT_FEED",
                    "HOSP",
                    hl7_ts(when),
                    "",
                    f"ADT^{event}^{structure}",
                    control_id,
                    "P",
                    "2.5",
                ],
            ),
            segment("EVN", [event, hl7_ts(when)]),
            pid_segment(p, number, rng),
            pv1_segment(enc, cls, location, attending, visit_no, start, end),
            zen_segment(enc),
        ]
        lines.append(
            json.dumps(
                {
                    "control_id": control_id,
                    "event": event,
                    "ts": when.strftime("%Y-%m-%dT%H:%M:%SZ"),
                    "hl7": "\r".join(segments),
                }
            )
        )

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"adt: {len(encounters)} encounters, {len(lines)} messages -> {OUT.relative_to(MOCK_SYSTEMS.parent)}")
    patients = {v["synthea_patient_id"] for v in visits}
    return {pid: f"HOSP^{ctx.patient_no[pid]}^MR" for pid in patients}, visits
