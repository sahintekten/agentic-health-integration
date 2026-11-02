# FHIR adapter for lab-db

This adapter is made by the Generate agent from `workspace/b/mapping.yaml`.

- `src/Mapper.cs`, `tests/MappingTests.cs` and `tests/DataRulesTests.cs` are written by the LLM.
- Everything else comes from templates in `agents/templates/dotnet/`.

It reads the source with `LabDbReader` and serves FHIR R4 JSON:
- `GET /fhir/Patient` (search with `?patient=`) and `GET /fhir/Patient/{id}`
- `GET /fhir/Observation` (search with `?patient=`) and `GET /fhir/Observation/{id}`
- `GET /fhir/metadata` and `GET /health`

Run the tests (the mock systems must be up):

```bash
dotnet test generated/b/tests
```
