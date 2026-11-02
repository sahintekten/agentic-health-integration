"""System B seed data: mock-systems/lab-db/init/02_seed.sql

Fills the legacy tables LB_PT, LB_TST_CODES and LB_RSLT from Synthea
observations (vital signs and common labs). Also writes the true code table
to eval/ground_truth/lab_db_test_codes.csv.
"""

import random
from datetime import datetime

from common import GO_LIVE, GROUND_TRUTH, MOCK_SYSTEMS, SEED, read_csv, write_csv

OUT = MOCK_SYSTEMS / "lab-db" / "init" / "02_seed.sql"
CODES_OUT = GROUND_TRUTH / "lab_db_test_codes.csv"

# LOINC -> (local code, legacy description, legacy unit, LOINC filled in LB_TST_CODES)
TESTS = {
    "8302-2": ("V100", "HEIGHT", "CM", True),
    "29463-7": ("V110", "WEIGHT", "KG", True),
    "39156-5": ("V120", "BMI", "KG/M2", False),
    "8480-6": ("V200", "BP SYS", "MMHG", True),
    "8462-4": ("V201", "BP DIA", "MMHG", True),
    "8867-4": ("V300", "PULSE", "BPM", True),
    "9279-1": ("V310", "RESP", "BR/MIN", False),
    "2708-6": ("V400", "SPO2 ART", "PCT", True),
    "2339-0": ("GLU", "GLUCOSE BLD", "MG/DL", True),
    "2345-7": ("GLUS", "GLUCOSE SER", "MG/DL", False),
    "6299-2": ("BUN", "UREA N", "MG/DL", True),
    "38483-4": ("CREA", "CREATININE", "MG/DL", True),
    "49765-1": ("CA", "CALCIUM", "MG/DL", True),
    "2947-0": ("NA", "SODIUM", "MMOL/L", True),
    "6298-4": ("K", "POTASSIUM", "MMOL/L", True),
    "2069-3": ("CL", "CHLORIDE", "MMOL/L", False),
    "20565-8": ("CO2", "CO2 TOTAL", "MMOL/L", True),
    "4548-4": ("HBA1C", "HGB A1C", "%", True),
    "2093-3": ("CHOL", "CHOLESTEROL", "MG/DL", True),
    "2571-8": ("TRIG", "TRIGLYCERIDE", "MG/DL", False),
    "18262-6": ("LDLD", "LDL DIRECT", "MG/DL", True),
    "2085-9": ("HDL", "HDL CHOL", "MG/DL", True),
    "718-7": ("HGB", "HAEMOGLOBIN", "G/DL", False),
    "6690-2": ("WBC", "WCC", "X10E3/UL", True),
    "777-3": ("PLT", "PLATELETS", "X10E3/UL", False),
    "33914-3": ("EGFR", "EGFR", "ML/MIN", False),
}

# old codes that are still in the code table but have no results
INACTIVE = [
    # local code, description, LOINC, legacy unit, display, UCUM unit, category
    ("V105", "HEIGHT IN", "8302-2", "IN", "Body Height", "[in_i]", "vital-signs"),
    ("ESR", "ESR", "30341-2", "MM/HR", "Erythrocyte sedimentation rate", "mm/h", "laboratory"),
]

DECIMALS = {
    "8480-6": 0,
    "8462-4": 0,
    "8867-4": 0,
    "9279-1": 0,
    "2708-6": 0,
    "38483-4": 2,
    "777-3": 0,
    "33914-3": 0,
}

RANGES = {
    "39156-5": (18.5, 25),
    "8480-6": (90, 130),
    "8462-4": (60, 85),
    "8867-4": (60, 100),
    "9279-1": (12, 20),
    "2708-6": (94, 100),
    "2339-0": (70, 99),
    "2345-7": (70, 99),
    "6299-2": (7, 20),
    "38483-4": (0.6, 1.3),
    "49765-1": (8.5, 10.5),
    "2947-0": (135, 145),
    "6298-4": (3.5, 5.1),
    "2069-3": (98, 107),
    "20565-8": (22, 29),
    "4548-4": (4.0, 5.6),
    "2093-3": (0, 200),
    "2571-8": (0, 150),
    "18262-6": (0, 130),
    "2085-9": (40, 999),
    "718-7": (12, 17.5),
    "6690-2": (4.0, 11.0),
    "777-3": (150, 400),
    "33914-3": (60, 999),
}

TEST_PATIENT = 999999


def epoch(iso):
    return int(datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp())


def flag(loinc, value):
    if loinc not in RANGES:
        return None
    low, high = RANGES[loinc]
    if value < low:
        return "L"
    if value > high:
        return "H"
    return "N"


def copy_block(table, columns, rows):
    def cell(v):
        if v is None:
            return r"\N"
        return str(v).replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n")

    lines = [f"COPY {table} ({', '.join(columns)}) FROM stdin;"]
    lines += ["\t".join(cell(v) for v in row) for row in rows]
    lines.append("\\.")
    return "\n".join(lines) + "\n"


def build(ctx):
    rng = random.Random(SEED + 2)
    by_id = {p["Id"]: p for p in ctx.patients}

    observations = []
    meta = {}
    for r in read_csv("observations"):
        if r["CODE"] in TESTS and r["TYPE"] == "numeric" and r["DATE"] >= GO_LIVE:
            observations.append(r)
            meta.setdefault(r["CODE"], (r["DESCRIPTION"], r["UNITS"], r["CATEGORY"]))
    observations.sort(key=lambda r: (r["DATE"], r["PATIENT"], r["CODE"], r["ENCOUNTER"], r["VALUE"]))

    # same hospital number as the other systems, but three patients were
    # registered again in the lab system and got a new number
    lab_patients = sorted({r["PATIENT"] for r in observations}, key=lambda pid: ctx.patient_no[pid])
    pt_ref = {pid: ctx.patient_no[pid] for pid in lab_patients}
    for pid in rng.sample(lab_patients, 3):
        pt_ref[pid] = 900000 + rng.randrange(1000, 9999)

    results = []
    for r in observations:
        loinc = r["CODE"]
        code, _, unit, _ = TESTS[loinc]
        value = float(r["VALUE"])
        text = f"{value:.{DECIMALS.get(loinc, 1)}f}"
        if rng.random() < 0.02:
            text = f"{text:>8}"
        results.append(
            [
                pt_ref[r["PATIENT"]],
                code,
                text,
                "0" if rng.random() < 0.01 else None,
                None if rng.random() < 0.03 else unit,
                epoch(r["DATE"]),
                flag(loinc, value),
                "C" if rng.random() < 0.005 else "F",
                rng.choices(["LABSYS", "INTFC", "JMUR", "AKEL"], [80, 15, 3, 2])[0],
            ]
        )

    first_seen = min(row[5] for row in results)
    for i, (test_code, value) in enumerate(
        [("GLU", "100.0"), ("NA", "140.0"), ("K", "4.0"), ("GLU", "999.0"), ("NA", "0.0"), ("K", "4.0")]
    ):
        results.append([TEST_PATIENT, test_code, value, None, None, first_seen + i * 86400 * 90, None, "F", "TEST"])

    results.sort(key=lambda row: (row[5], row[0], row[1]))
    lb_rslt = []
    next_id = 500001
    duplicates = 0
    for row in results:
        lb_rslt.append([next_id] + row)
        next_id += 1
        # the old interface sometimes sent the same result twice
        if row[0] != TEST_PATIENT and rng.random() < 0.015:
            lb_rslt.append([next_id] + row[:-1] + ["INTFC"])
            next_id += 1
            duplicates += 1

    created = {}
    for row in lb_rslt:
        created.setdefault(row[1], row[6])
    lb_pt = []
    for pid in lab_patients:
        p = by_id[pid]
        sex = 9 if rng.random() < 0.02 else {"M": 1, "F": 2}[p["GENDER"]]
        name = f"{p['LAST']} {p['FIRST']}".upper()[:20]
        lb_pt.append([pt_ref[pid], name, int(p["BIRTHDATE"].replace("-", "")), sex, created[pt_ref[pid]]])
    lb_pt.append([TEST_PATIENT, "ZZTEST PATIENT", 19000101, 9, first_seen])
    lb_pt.sort()

    lb_codes = [
        [code, desc, loinc if has_loinc else None, unit, "Y"] for loinc, (code, desc, unit, has_loinc) in TESTS.items()
    ]
    lb_codes += [[code, desc, None if code == "V105" else loinc, unit, "N"] for code, desc, loinc, unit, *_ in INACTIVE]
    lb_codes.sort()

    sql = "TRUNCATE LB_PT, LB_TST_CODES, LB_RSLT;\n\n"
    sql += copy_block("LB_PT", ["PT_REF", "PT_NM", "PT_DOB", "PT_SX", "CRT_DT"], lb_pt) + "\n"
    sql += copy_block("LB_TST_CODES", ["TST_CD", "TST_DESC", "LOINC_CD", "DFLT_UOM", "ACTV_FLG"], lb_codes) + "\n"
    sql += copy_block(
        "LB_RSLT",
        ["RSLT_ID", "PT_REF", "TST_CD", "LB_VAL1", "LB_VAL2", "UOM", "RSLT_DT", "FLG", "STS", "ENT_USR"],
        lb_rslt,
    )
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(sql, encoding="utf-8")

    code_rows = []
    for loinc, (code, _, _, has_loinc) in TESTS.items():
        display, ucum, category = meta[loinc]
        code_rows.append(
            {
                "tst_cd": code,
                "loinc": loinc,
                "display": display,
                "ucum_unit": ucum,
                "category": category,
                "loinc_in_source": "yes" if has_loinc else "no",
                "active": "Y",
            }
        )
    for code, _, loinc, _, display, ucum, category in INACTIVE:
        code_rows.append(
            {
                "tst_cd": code,
                "loinc": loinc,
                "display": display,
                "ucum_unit": ucum,
                "category": category,
                "loinc_in_source": "no" if code == "V105" else "yes",
                "active": "N",
            }
        )
    code_rows.sort(key=lambda r: r["tst_cd"])
    write_csv(CODES_OUT, list(code_rows[0]), code_rows)

    print(
        f"lab: {len(lb_pt)} patients, {len(lb_codes)} test codes, {len(lb_rslt)} results "
        f"({duplicates} duplicates) -> {OUT.relative_to(MOCK_SYSTEMS.parent)}"
    )
    return {pid: str(ref) for pid, ref in pt_ref.items()}
