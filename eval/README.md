# Evaluation

**The agents must never read this folder.** It is the answer key. We only use it to score how well the agents map the three mock systems to FHIR.

## ground_truth/

| File | What it has |
| --- | --- |
| `patient_registry_api.yaml` | Correct FHIR mapping for every field of System A |
| `lab_db.yaml` | Correct FHIR mapping for every column of System B, plus a unit map |
| `adt_feed.yaml` | Correct FHIR mapping for every HL7 field of System C |
| `lab_db_test_codes.csv` | The true LOINC code, UCUM unit and category for every lab test code |
| `patient_crosswalk.csv` | Links the patient ids of A, B and C to the Synthea patient id |
| `encounter_crosswalk.csv` | Links the ADT visit numbers to the Synthea encounter id |

Each YAML also has a `known_traps` list. These are the problems we put in the systems on purpose.

The CSV files are made by `data/scripts/transform_all.py`, so they always match the seed data. The YAML files are written by hand.

The identifier system URLs (`http://hospital.example.org/...`) are our own choice. When scoring, compare values and types, not the exact URL.

## Answer key history

- **v1 (phase 1):** field mappings and crosswalks for systems A, B and C.
- **v1.1 (phase 3):** added the expected `data_rules` for each system.
- **v2 (phase 4):** added two rules for system C after we reviewed the phase 3 output:
  - `trim_whitespace`: the inventory showed extra spaces in `ZEN-3.2`, so the rule is needed. We missed it when we wrote v1.1.
  - `drop_duplicates`: every ADT message repeats the patient, so the adapter must keep one Patient per hospital number. This is a real rule, not just a detail of the code.

  We only add a rule when the source data proves it is needed. We do not add rules just because an agent suggested them.
