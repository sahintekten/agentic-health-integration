"""Shared helpers for the transform scripts.

The scripts read the Synthea CSV export and write seed files for the three
mock systems, plus the ID crosswalks used for scoring in eval/ground_truth.
"""

import csv
import random
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SYNTHEA_CSV = ROOT / "data" / "synthea" / "output" / "csv"
MOCK_SYSTEMS = ROOT / "mock-systems"
GROUND_TRUTH = ROOT / "eval" / "ground_truth"

SEED = 20260101
# the lab system and the ADT feed only hold data from their go-live date
GO_LIVE = "2016-01-01"


def read_csv(name):
    path = SYNTHEA_CSV / f"{name}.csv"
    if not path.exists():
        raise SystemExit(f"{path} not found. Run 'make data' first.")
    with path.open(newline="", encoding="utf-8") as f:
        yield from csv.DictReader(f)


def write_csv(path, fieldnames, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


@dataclass
class Context:
    patients: list
    encounters: list
    providers: dict
    # Synthea patient id -> hospital number shared by all three systems
    patient_no: dict


def load_context():
    # sort everything so the output does not depend on Synthea's row order
    patients = sorted(read_csv("patients"), key=lambda p: p["Id"])
    rng = random.Random(SEED)
    numbers = rng.sample(range(1000, 100000), len(patients))
    return Context(
        patients=patients,
        encounters=sorted(read_csv("encounters"), key=lambda e: (e["START"], e["Id"])),
        providers={p["Id"]: p for p in read_csv("providers")},
        patient_no={p["Id"]: n for p, n in zip(patients, numbers)},
    )
