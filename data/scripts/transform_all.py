"""Turn the Synthea CSV export into seed data for the three mock systems.

Also writes the patient and encounter crosswalks to eval/ground_truth.
Run from the repo root:  python3 data/scripts/transform_all.py
"""

import build_adt
import build_lab
import build_registry
from common import GROUND_TRUTH, load_context, write_csv


def main():
    ctx = load_context()
    registry_ids = build_registry.build(ctx)
    lab_ids = build_lab.build(ctx)
    adt_ids, visits = build_adt.build(ctx)

    rows = [
        {
            "synthea_id": p["Id"],
            "registry_pt_id": registry_ids.get(p["Id"], ""),
            "lab_pt_ref": lab_ids.get(p["Id"], ""),
            "adt_pid3": adt_ids.get(p["Id"], ""),
        }
        for p in ctx.patients
    ]
    write_csv(GROUND_TRUTH / "patient_crosswalk.csv", list(rows[0]), rows)
    write_csv(GROUND_TRUTH / "encounter_crosswalk.csv", list(visits[0]), visits)
    print(f"crosswalk: {len(rows)} patients, {len(visits)} encounters -> eval/ground_truth/")


if __name__ == "__main__":
    main()
