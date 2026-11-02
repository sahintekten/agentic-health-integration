"""System A seed data: mock-systems/patient-registry-api/data/patients.json"""

import json
import random

from common import MOCK_SYSTEMS, SEED

OUT = MOCK_SYSTEMS / "patient-registry-api" / "data" / "patients.json"


def full_name(p, rng):
    forenames = " ".join(x for x in (p["FIRST"], p["MIDDLE"]) if x)
    name = f"{p['LAST']}, {forenames}".upper()
    # some records were typed by hand and have extra spaces
    if rng.random() < 0.05:
        name = name.replace(", ", ",  ") + " "
    return name


def post_code(zip_code, rng):
    if not zip_code or zip_code == "00000":
        return None
    # records migrated from the old system lost the leading zero
    if rng.random() < 0.3:
        return zip_code.lstrip("0")
    return zip_code


def build(ctx):
    rng = random.Random(SEED + 1)
    records = []
    ids = {}
    for p in ctx.patients:
        pt_id = f"MRN-{ctx.patient_no[p['Id']]:06d}"
        ids[p["Id"]] = pt_id
        year, month, day = p["BIRTHDATE"][:10].split("-")
        sex = "U" if rng.random() < 0.03 else p["GENDER"]
        records.append(
            {
                "pt_id": pt_id,
                "nm_full": full_name(p, rng),
                "dob": f"{day}/{month}/{year}",
                "sx": sex,
                "addr_line": p["ADDRESS"],
                "town": p["CITY"],
                "post_cd": post_code(p["ZIP"], rng),
                "mrtl": p["MARITAL"],
                "dcsd": p["DEATHDATE"][:10] or None,
            }
        )
    records.sort(key=lambda r: r["pt_id"])
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(records, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"registry: {len(records)} patients -> {OUT.relative_to(MOCK_SYSTEMS.parent)}")
    return ids
