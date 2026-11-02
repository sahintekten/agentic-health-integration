"""System D seed data: weekly appointment exports in mock-systems/appointments-export/out/.

Each week has one CSV file. An appointment is first exported as booked in the
week before it happens, then again with its final status in its own week.
"""

import random
import string
from datetime import datetime, timedelta

from common import MOCK_SYSTEMS, SEED

OUT = MOCK_SYSTEMS / "appointments-export" / "out"
FIRST_DAY = "2026-06-01"
CLINICS = {"wellness": "GP-WELL", "ambulatory": "OPD-GEN", "outpatient": "OPD-SPEC"}
HEADER = [
    "ApptRef",
    "PatNo",
    "PatName",
    "Geb.Dat",
    "Sex",
    "ApptStart",
    "DurMin",
    "Clinic",
    "ApptType",
    "Status",
    "Booked",
]


def pat_no(number):
    check = string.ascii_uppercase[number % 23]
    return f"P/{number:07d}-{check}"


def week_of(dt):
    year, week, _ = dt.isocalendar()
    return f"{year}-W{week:02d}"


def cell(value):
    value = str(value)
    return f'"{value}"' if ";" in value or '"' in value else value


def build(ctx):
    rng = random.Random(SEED + 4)
    by_id = {p["Id"]: p for p in ctx.patients}
    encounters = [e for e in ctx.encounters if e["ENCOUNTERCLASS"] in CLINICS and e["START"] >= FIRST_DAY]

    files, appts, ids = {}, [], {}
    for k, enc in enumerate(encounters):
        p = by_id[enc["PATIENT"]]
        number = ctx.patient_no[p["Id"]]
        ids[p["Id"]] = pat_no(number)
        start = datetime.fromisoformat(enc["START"].replace("Z", ""))
        stop = datetime.fromisoformat(enc["STOP"].replace("Z", ""))
        booked = start - timedelta(days=rng.randint(8, 40))
        given = " ".join(x for x in (p["FIRST"], p["MIDDLE"]) if x)
        sex = {"M": "m", "F": "w"}[p["GENDER"]] if rng.random() > 0.03 else ""
        final = rng.choices(["DN", "NS", "CX"], [85, 8, 7])[0]
        ref = f"AP{300001 + k}"
        base = [
            ref,
            pat_no(number),
            f"{given} {p['LAST'].upper()}",
            datetime.fromisoformat(p["BIRTHDATE"]).strftime("%d.%m.%Y"),
            sex,
            start.strftime("%d.%m.%Y %H:%M"),
            max(5, int((stop - start).total_seconds() // 60)),
            CLINICS[enc["ENCOUNTERCLASS"]],
            enc["DESCRIPTION"],
        ]
        files.setdefault(week_of(start - timedelta(days=7)), []).append(base + ["BK", booked.strftime("%Y-%m-%d")])
        files.setdefault(week_of(start), []).append(base + [final, booked.strftime("%Y-%m-%d")])
        appts.append({"synthea_encounter_id": enc["Id"], "appt_ref": ref, "final_status": final})

    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.csv"):
        old.unlink()
    for week, rows in sorted(files.items()):
        lines = [";".join(HEADER) + ";"]
        lines += [";".join(cell(v) for v in row) + ";" for row in rows]
        lines.append(f"--- END OF EXPORT {len(rows)} ROWS ---")
        (OUT / f"appointments_{week}.csv").write_bytes(("\r\n".join(lines) + "\r\n").encode("cp1252", "replace"))
    print(
        f"appointments: {len(appts)} appointments, {len(files)} weekly files -> {OUT.relative_to(MOCK_SYSTEMS.parent)}"
    )
    return ids, appts
