[![CI](https://github.com/sahintekten/agentic-health-integration/actions/workflows/ci.yml/badge.svg)](https://github.com/sahintekten/agentic-health-integration/actions/workflows/ci.yml)

# Agentic Health Integration

This is our project for the EXL x UCD Data & AI Hackathon 2026, Challenge 01: Agentic healthcare integration.

Hospitals have many old systems that do not talk to each other. Each one stores patients, lab results and visits in its own way. Our goal is a set of AI agents that look at these systems, work out what the data means, map it to HL7 FHIR R4, and then write .NET 8 adapter code with tests.

## Status

Phase 1 is done. It has the repo, the synthetic data and three mock hospital systems for the agents to work on. There are no agents yet. They come in phase 2.

## What is in the repo

| Folder | What it has |
| --- | --- |
| `mock-systems/patient-registry-api` | System A. A REST API with patient details (Python, FastAPI). |
| `mock-systems/lab-db` | System B. An old style lab database (PostgreSQL). |
| `mock-systems/adt-feed` | System C. An HL7 v2 ADT feed (MLLP over TCP, plus .hl7 files). |
| `data/scripts` | A script that runs Synthea, and Python scripts that turn its CSV files into seed data for each system. |
| `data/synthea` | Raw Synthea output. Not in git. |
| `eval/ground_truth` | The correct mappings for each system. Only for scoring. |
| `agents` | Python agents (phase 2). Empty for now. |
| `generated` | .NET code that the agents will write. Empty for now. |
| `tests/smoke` | Quick tests that check the three systems are up and have data. |

## What you need

- Docker Desktop
- Python 3.11 or newer
- Java 17 or newer, but only if you want to make the data again with Synthea
- .NET 8 SDK (for later phases)

## Quick start

```bash
make data    # run Synthea and build the seed files
make up      # start the three systems
make smoke   # check that they work
```

The seed files are already in git. If you do not have Java, you can skip `make data`.

Other commands:

| Command | What it does |
| --- | --- |
| `make down` | Stop everything and remove the database volume |
| `make seed` | Build the seed files again from the Synthea output and reload the systems |
| `make replay-adt` | Send every ADT message over MLLP |
| `make send-adt N=5` | Send the next 5 ADT messages, one per second (for a demo) |
| `make lint` | Run all pre-commit checks |
| `make hooks` | Install the git hooks |

## The three systems

### System A: patient registry

- Runs on http://localhost:8001
- `GET /patients?pg=1&sz=20` gives one page of patients
- `GET /patients/{pt_id}` gives one patient
- `GET /health`
- The OpenAPI document is at `/openapi.json` and Swagger UI is at `/docs`

### System B: lab database

- PostgreSQL on `localhost:5433`, database `labdb`
- User `labuser`, password `labpass`. These are only for the local mock. You can change them in `.env`.
- Tables: `LB_PT`, `LB_TST_CODES`, `LB_RSLT`

### System C: ADT feed

- MLLP listener on `localhost:2575`. It sends back an HL7 ACK for every message.
- When it starts, it writes every ADT message (A01, A03, A08) to `mock-systems/adt-feed/outbox/` as one `.hl7` file per message.
- `make replay-adt` and `make send-adt` push messages over MLLP. By default they go to the feed's own listener, so you can see the full send and ACK loop. Later we will set `ADT_TARGET` to our adapter.

## Messy on purpose

The three systems use different formats for the same things, like ids, dates, gender codes and units. Some docs are wrong and some rows are dirty. We did this on purpose, because real hospital systems look like this.

The correct answers are in `eval/ground_truth`. **The agents must never read `eval/` or the source code in `mock-systems/` and `data/scripts/`.** They should only use the system interfaces: HTTP, SQL, MLLP and the outbox files. We use the ground truth only to score the agents.

We keep `eval/` public so anyone can check how we measure accuracy. In phase 2 we will add a test that fails if an agent tries to open anything under `eval/`.

## Data

All patients come from [Synthea](https://github.com/synthetichealth/synthea), a synthetic patient generator. We use a fixed seed and fixed dates, so `make data` gives the same data every time. No real patient data is used anywhere in this repo.

## Config

Copy `.env.example` to `.env` and fill in the keys you need. `.env` is in `.gitignore`. Never commit it.

## Working on the repo

- Run `make hooks` once. The hooks run ruff, gitleaks and some file checks on every commit.
- Commit messages follow Conventional Commits: `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `refactor:`.
