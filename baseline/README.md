# Hand-written baseline

This folder is for the comparison in our business case: a person writes the adapter for System A by hand, and we time it. Then we check it with exactly the same validator as the generated adapters.

`baseline/a/` only has the project files. There is no code yet, on purpose.

## Rules for a fair comparison

- Start a timer when you start reading System A. Stop it when `make validate-baseline SYSTEM=a` passes.
- Use only what the agents can use: the running API (`http://localhost:8001`, including `/openapi.json`). Do not read `eval/`, `mock-systems/`, `data/scripts/` or `generated/`.
- Write your own tests in `tests/`. The validator needs at least one test.

## What the validator expects

The checks are the same as for `generated/a` (see `agents/validate.py`):

1. **Build**: `dotnet build tests/Adapter.Tests.csproj` works.
2. **Tests**: `dotnet test` passes, with at least one test.
3. **Contract**: `dotnet run --project src/Adapter.csproj` starts a web app on the URL in `ASPNETCORE_URLS`, with:
   - `GET /health` (HTTP 200)
   - `GET /fhir/metadata` (a valid CapabilityStatement)
   - `GET /fhir/Patient?_count=20` (a searchset Bundle with `total`)
   - `GET /fhir/Patient/{id}` (HTTP 200, or 404 with an OperationOutcome)
   - content type `application/fhir+json`, and every resource must pass the Firely R4 validator
   - the paths in `workspace/a/openapi.yaml`
4. **PHI**: no real patient values in the code or the logs. Request logs with `?patient=` count as logs.
5. **Reconciliation**: one Patient per registry record (127), and `Patient.id` is the hospital number (`MRN-004521` becomes `4521`).

The registry URL comes from `REGISTRY_URL` (default `http://localhost:8001`).

## Run the checks

```bash
make up                          # the mock systems
make validate-baseline SYSTEM=a  # one round, no repairs
```

The report goes to `workspace/a/baseline_validate/`.
