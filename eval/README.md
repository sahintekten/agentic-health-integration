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
