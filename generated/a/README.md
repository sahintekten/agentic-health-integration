# FHIR adapter for patient-registry-api

This adapter is made by the Generate agent from `workspace/a/mapping.yaml`.

- `src/Mapper.cs`, `tests/MappingTests.cs` and `tests/DataRulesTests.cs` are written by the LLM.
- Everything else comes from templates in `agents/templates/dotnet/`.

It reads the source with `RegistryReader` and serves FHIR R4 JSON:
- `GET /fhir/Patient` (search with `?patient=`) and `GET /fhir/Patient/{id}`
- `GET /fhir/metadata` and `GET /health`

Run the tests (the mock systems must be up):

```bash
dotnet test generated/a/tests
```
