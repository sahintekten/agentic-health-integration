[![CI](https://github.com/sahintekten/agentic-health-integration/actions/workflows/ci.yml/badge.svg)](https://github.com/sahintekten/agentic-health-integration/actions/workflows/ci.yml)

# Agentic Health Integration

This is our project for the EXL x UCD Data & AI Hackathon 2026, Challenge 01: Agentic healthcare integration.

Hospitals have many old systems that do not talk to each other. Each one stores patients, lab results and visits in its own way. Our goal is a set of AI agents that look at these systems, work out what the data means, map it to HL7 FHIR R4, and then write .NET 8 adapter code with tests.

## Status

- Phase 1 is done: the repo, the synthetic data and three mock hospital systems.
- Phase 2 is done: the LLM layer, the Discover agent, the Design agent, a review step and a scorer.
- Phase 3 is done: data rules in Design, the Generate agent (.NET 8 adapters) and the Validate agent, for systems A, B and C.
- Phase 4 is done: a FHIR gateway with a patient index, a dashboard, a live demo that adds System D, and a harness for a hand-written baseline.

## How the agents work

The full pipeline has five steps:

1. **Discover**: look at a system and describe every field. *(done)*
2. **Design**: map the fields to FHIR R4, list the data rules, write an API spec for the adapter and a patient ID plan. *(done)*
3. **Generate**: write a .NET 8 adapter and its tests. *(done)*
4. **Validate**: build, test and check the adapter, and repair it when something fails. *(done)*
5. **Operate**: run the adapters behind a gateway and watch them on a dashboard. *(done)*

See [docs/architecture.md](docs/architecture.md) for a diagram of the whole system.

Discover first collects facts with plain code (`inventory.json`): field types, null rates, formats, duplicates, doc errors. Then a small LLM turns these facts into a profile (`profile.json`) with a meaning, format, PII flag and confidence for each field. The profile is checked against the inventory, so wrong guesses show up as `checks`.

Design gives the profile, the inventory findings, the patient ID policy and a short FHIR R4 reference (`agents/fhir_ref/`) to a stronger LLM. It writes `mapping.yaml` (field mappings plus `data_rules` such as "drop duplicate rows" or "fall back to the default unit"), `openapi.yaml` and `id_strategy.md`.

Generate builds a .NET 8 minimal API project in `generated/<system>/`. Templates give the boring parts: project files, `Program.cs`, the source reader, the FHIR store and the Dockerfile. The LLM writes only `src/Mapper.cs` (the mapping and data rules) and the unit tests. The adapter uses the Firely SDK and serves `GET /fhir/Patient`, `/fhir/Observation` or `/fhir/Encounter` (with `?patient=`), plus `/fhir/metadata` and `/health`.

Validate runs up to 5 rounds. Each round builds the code, runs the tests, starts the adapter and checks it: FHIR validation with the Firely validator, the generated `openapi.yaml`, a scan for real patient values in code and logs, and a reconciliation of resource counts against the source after the data rules. Each round writes a report to `workspace/<system>/validate/`.

**Self-healing.** When a check fails, Validate sends the failing output to the LLM and asks for a fixed `src/Mapper.cs`. The LLM may fix a compile error inside a test file, but it may not delete a test, remove or loosen an assertion, or skip a test. The agent counts tests and assertions before and after each change and refuses any change that lowers them. It can never edit the templates, the integration tests or the source systems.

After each step you see a summary and answer `approve? [y/n/edit]`. Approved steps get a `reviewed_by` and `reviewed_at` stamp in `workspace/<system>/review.json`. Use `--yes` to skip the questions. Metrics per system (time, LLM calls and tokens, repairs, test pass rate, lines of code) go to `workspace/metrics.json`.

```bash
python -m agents run --system a --step pipeline   # or: make pipeline SYSTEM=a
python eval/score.py --system a                   # or: make score SYSTEM=a
python -m agents metrics                          # or: make metrics
```

All outputs go to `workspace/<system>/`. The LLM cache is in `.cache/llm/` and every call is logged in `workspace/run_log.jsonl`. Both are ignored by git.

## Gateway, dashboard and demo

**Gateway** (`gateway/`, port 9000) is the single FHIR entry point. It serves `GET /fhir/Patient`, `/fhir/Observation`, `/fhir/Encounter` (with `?patient=`) and `/fhir/Patient/{id}/$everything`, and merges the results of all adapters. Its patient index (a small SQLite file) links records by the shared hospital number. When a record has another id but the same name and birth date as a registry patient, the gateway marks it as a *probable* link. The data of a probable link is only merged after a person approves it. `GET /health` shows every adapter, its counts and its last reconciliation.

**Dashboard** (`dashboard/`, port 8050) has three pages: an overview (steps, F1 scores, validation, time and tokens per system, and the gateway health), a live pipeline view, and a review page for probable patient links.

**Live demo.** `make demo-add-system SYSTEM=d` adds System D (a weekly appointments CSV export) from nothing: Discover, Design, Generate and Validate with the real review questions, then it starts the adapter and registers it in the gateway. `make demo-reset SYSTEM=d` removes it again.

```bash
make up && make up-adapters     # mock systems, adapters A-C and the gateway
make dashboard                  # http://localhost:8050
make demo-add-system SYSTEM=d   # the live demo
```

**Baseline.** `baseline/` is for a hand-written adapter for System A, so we can compare it with the generated one. `make validate-baseline SYSTEM=a` runs the same checks on it. See [baseline/README.md](baseline/README.md).

### LLM providers

Set `LLM_PROVIDER` in `.env`:

| Provider | Notes |
| --- | --- |
| `gemini` | The default. Google AI Studio Gemini API with `GEMINI_API_KEY` (free tier). Strong = `gemini-3.8-flash` (falls back to `gemini-3.6-flash` when it is overloaded), fast = `gemini-3.5-flash-lite`. Change them with `GEMINI_MODEL_STRONG` and `GEMINI_MODEL_FAST`. Calls are spaced about 6.5 seconds apart to stay under the free limits. |
| `github_models` | Retired by GitHub on 2026-07-30. Kept only so an old setting gives a clear error. |
| `claude_code` | Runs the local `claude -p` command. For development only. It needs the Claude Code CLI installed and logged in. It runs with no tools in an empty folder. |
| `anthropic` | Uses `ANTHROPIC_API_KEY`. Not tested yet. |
| `azure_openai` | Uses the `AZURE_OPENAI_*` keys. Not tested yet. |

Set `LLM_CACHE=0` to turn the cache off.

## What is in the repo

| Folder | What it has |
| --- | --- |
| `mock-systems/patient-registry-api` | System A. A REST API with patient details (Python, FastAPI). |
| `mock-systems/lab-db` | System B. An old style lab database (PostgreSQL). |
| `mock-systems/adt-feed` | System C. An HL7 v2 ADT feed (MLLP over TCP, plus .hl7 files). |
| `mock-systems/appointments-export` | System D. Weekly appointment exports as CSV files (for the live demo). |
| `data/scripts` | A script that runs Synthea, and Python scripts that turn its CSV files into seed data for each system. |
| `data/synthea` | Raw Synthea output. Not in git. |
| `eval` | The correct mappings for each system (`ground_truth/`) and the scorer (`score.py`). |
| `agents` | Python agents: LLM layer, Discover, Design and the review step. |
| `generated` | The .NET 8 adapters written by the Generate agent, one folder per system, plus a FHIR check tool. |
| `gateway` | The FHIR gateway with the patient index. |
| `dashboard` | The dashboard (FastAPI, plain HTML). |
| `baseline` | The harness for a hand-written adapter. |
| `docs` | The architecture diagram. |
| `tests` | Smoke tests for the three systems, and the eval guard test. |

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

We keep `eval/` public so anyone can check how we measure accuracy. `tests/test_eval_guard.py` runs the agents and fails if any agent code opens a file in `eval/ground_truth`. `eval/score.py` is the only code that reads it.

## Data

All patients come from [Synthea](https://github.com/synthetichealth/synthea), a synthetic patient generator. We use a fixed seed and fixed dates, so `make data` gives the same data every time. No real patient data is used anywhere in this repo.

## Config

Copy `.env.example` to `.env` and fill in the keys you need. `.env` is in `.gitignore`. Never commit it.

## Working on the repo

- Run `make hooks` once. The hooks run ruff, gitleaks and some file checks on every commit.
- Commit messages follow Conventional Commits: `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `refactor:`.
