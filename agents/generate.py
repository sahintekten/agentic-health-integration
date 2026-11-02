"""Generate agent: build a .NET 8 FHIR adapter for one system in generated/<system>/.

Templates give the boring parts (project files, Program.cs, source reader,
FHIR store, Dockerfile). The LLM writes the mapping code (src/Mapper.cs) and
the unit tests (tests/MappingTests.cs, tests/DataRulesTests.cs) from the
approved mapping.yaml.
"""

import json
import shutil
from pathlib import Path

import yaml

from agents import config, csharp_layout
from agents.llm import LLMError, complete

TEMPLATES = Path(__file__).parent / "templates" / "dotnet"

LLM_FILES = ["src/Mapper.cs", "tests/MappingTests.cs", "tests/DataRulesTests.cs"]

RULES = """- Follow mapping.yaml: implement every mapping item that has a resource and a path,
  with its transform and value_map.
- Apply every item in data_rules, and call data.CountRule("<rule_id>") each time a rule changes or drops something.
- Patient ids follow the patient ID policy. Every subject reference is "Patient/<id>".
- Observation.id and Encounter.id must be unique and stable; build them from source ids.
- Never throw on bad data: skip the value, or skip the record and count it.
- No logging and no Console output. Never put patient values in exception messages.
- Do not use the non-generic type name Task (it clashes with Hl7.Fhir.Model.Task).
- Readable code: one small method per resource and short comments where a data rule is applied."""

MAPPER_PROMPT = f"""You write C# 12 code for .NET 8 with the Firely SDK 6.8 (package Hl7.Fhir.R4).
Write the file Mapper.cs of a FHIR R4 adapter.

The contract below already exists in the project. Do not redefine it.
Write: namespace Adapter; public static class Mapper {{ public static MappedData Map(SourceData source) }}
plus private helper methods in the same class. You may use: System, System.Linq, System.Globalization,
System.Collections.Generic, System.Text.RegularExpressions, Hl7.Fhir.Model.

{RULES}

Reply with the C# file only, no markdown."""

TESTS_PROMPT = """You write xUnit tests (C# 12, .NET 8, Firely SDK 6.8) for Mapper.Map(SourceData).
You do not see the implementation. Test the behaviour that mapping.yaml describes.

Return one JSON object: {"MappingTests.cs": "<code>", "DataRulesTests.cs": "<code>"}
- Both files: namespace Adapter.Tests; using Adapter; using Hl7.Fhir.Model; (xunit is a global using).
- MappingTests.cs: one [Fact] per mapping item that has a resource and a path, named after the source field.
  Build a SourceData with the rows the mapper needs (use the exact table names and keys given),
  call Mapper.Map, find the resource and assert the mapped element, including the transform.
- DataRulesTests.cs: at least one [Fact] per data rule (for example duplicates dropped, test record excluded).
  Where a rule fires, also assert data.RuleCounts["<rule_id>"] > 0.
- Use invented values only: patient numbers below 1000, names like "Testfamily" and "Testgiven",
  dates in 2001. Never copy values from real data.
- Give each row every field the mapper needs to build the resource (a result row needs its patient row
  and its code row, and so on).
- Do not assert identifier system URIs or display texts unless mapping.yaml states them exactly.
- Do not use the non-generic type name Task. Do not use Skip."""


def _profile(system):
    """Per system: reader class, tables and the keys the mapper will see (no sample values)."""
    inventory = json.loads((config.system_dir(system) / "inventory.json").read_text())
    kind = inventory["kind"]
    tables = {}
    for name, f in inventory["fields"].items():
        if kind == "postgresql":
            table, key = f["table"], name.split(".", 1)[1]
        elif kind == "rest-json":
            if f.get("location") == "response envelope":
                continue
            table, key = "patients", name
        else:
            table, key = "messages", name
        info = {"formats": f.get("formats"), "shapes": f.get("shapes", [])[:2], "null_rate": f.get("null_rate")}
        if f.get("value_counts") and kind != "rest-json":
            info["values"] = list(f["value_counts"])[:30]
        if f.get("value_counts") and kind == "rest-json" and len(f["value_counts"]) <= 6:
            info["values"] = list(f["value_counts"])
        tables.setdefault(table, {})[key] = {k: v for k, v in info.items() if v not in (None, [], 0)}
    if kind == "hl7v2":
        note = (
            'One record per message. Keys are "SEG-n" for a whole field and "SEG-n.c" for a component, '
            'with HL7 numbering ("MSH-9" is the message type). Escapes are decoded. Empty values are null. '
            'The key "_file" holds the file name.'
        )
        reader, extra = "Hl7FileReader", {"mllp": True}
    elif kind == "postgresql":
        note = "One record per row. Keys are lower case column names. All values are raw text; null stays null."
        reader, extra = "LabDbReader", {"tables": list(inventory["tables"])}
    else:
        note = "One record per patient record from the API. Values are raw text; JSON null becomes null."
        ep = inventory.get("list_endpoint") or {"path": "/patients", "page_param": "pg", "size_param": "sz"}
        reader, extra = "RegistryReader", {"endpoint": ep}
    return {"kind": kind, "reader": reader, "note": note, "tables": tables, **extra}


def _resources(mapping):
    found = {"Patient"}
    mappings = mapping.get("mappings") or []
    items = [i for rows in mappings.values() for i in rows] if isinstance(mappings, dict) else mappings
    for item in items:
        if item.get("resource") in ("Observation", "Encounter"):
            found.add(item["resource"])
    return [r for r in ("Patient", "Observation", "Encounter") if r in found]


def _write_template(src, dest, subs):
    text = (TEMPLATES / src).read_text()
    for key, value in subs.items():
        text = text.replace("{{" + key + "}}", value)
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(text)


def write_templates(system, profile, mapping):
    out = config.generated_dir() / system
    for name in ("global.json", "Directory.Build.props", ".editorconfig"):
        shutil.copy(TEMPLATES / name, config.generated_dir() / name)
    tool = config.generated_dir() / "tools" / "FhirCheck"
    tool.mkdir(parents=True, exist_ok=True)
    for name in ("FhirCheck.csproj", "Program.cs"):
        shutil.copy(TEMPLATES / "tools" / "FhirCheck" / name, tool / name)

    resources = _resources(mapping)
    subs = {
        "READER": profile["reader"],
        "SYSTEM_NAME": config.SYSTEMS[system],
        "RESOURCE_TYPES": ", ".join(f'"{r}"' for r in resources),
        "EXTRA_SERVICES": "builder.Services.AddHostedService<MllpListener>();" if profile.get("mllp") else "",
        "EXTRA_PACKAGES": '    <PackageReference Include="Npgsql" Version="8.0.9" />\n'
        if profile["reader"] == "LabDbReader"
        else "",
        "TABLES": ", ".join(f'"{t}"' for t in profile.get("tables_list", profile.get("tables", {}))),
        "TABLE": "patients",
        "EXTRA_ASSERTS": "".join(f"        Assert.NotEmpty(data.{r}s);\n" for r in resources if r != "Patient"),
    }
    if "endpoint" in profile:
        ep = profile["endpoint"]
        subs.update({"LIST_PATH": ep["path"], "PAGE_PARAM": ep["page_param"], "SIZE_PARAM": ep["size_param"]})
    if profile["reader"] == "LabDbReader":
        subs["TABLES"] = ", ".join(f'"{t}"' for t in profile["tables_list"])

    files = ["src/Adapter.csproj", "src/Program.cs", "src/SourceModel.cs", "src/FhirStore.cs"]
    files.append(f"src/{profile['reader']}.cs")
    if profile.get("mllp"):
        files.append("src/MllpListener.cs")
    files += ["tests/Adapter.Tests.csproj", "tests/IntegrationTests.cs", "Dockerfile"]
    for f in files:
        _write_template(f, out / f, subs)
    for name in ("global.json", "Directory.Build.props"):
        shutil.copy(TEMPLATES / name, out / name)
    return files + ["global.json", "Directory.Build.props"], resources


def _context(system, profile, mapping_text):
    cheatsheet = (config.FHIR_REF / "firely_cheatsheet.cs").read_text()
    id_policy = (config.FHIR_REF / "id_policy.md").read_text() + "\n" + (config.FHIR_REF / "data_policy.md").read_text()
    contract = (TEMPLATES / "src" / "SourceModel.cs").read_text()
    source = {"tables": profile["tables"], "how_records_look": profile["note"]}
    return (
        f"Contract (SourceModel.cs):\n{contract}\n\nFirely examples:\n{cheatsheet}\n\n{id_policy}\n\n"
        f"Source tables and keys (formats and shapes, no real values):\n{json.dumps(source, indent=1)}\n\n"
        f"mapping.yaml (approved):\n{mapping_text}"
    )


def run(system):
    work = config.system_dir(system)
    mapping_path = work / "mapping.yaml"
    if not mapping_path.exists():
        raise SystemExit(f"{mapping_path} not found. Run the design step first.")
    mapping_text = mapping_path.read_text()
    mapping = yaml.safe_load(mapping_text)
    profile = _profile(system)
    if profile["reader"] == "LabDbReader":
        profile["tables_list"] = profile["tables_list"] if "tables_list" in profile else list(profile["tables"])
    out = config.generated_dir() / system
    out.mkdir(parents=True, exist_ok=True)
    template_files, resources = write_templates(system, profile, mapping)
    context = _context(system, profile, mapping_text)

    mapper = complete(
        [{"role": "system", "content": MAPPER_PROMPT}, {"role": "user", "content": context}],
        model_tier="strong",
        purpose=f"generate {system} mapper",
    )
    (out / "src" / "Mapper.cs").write_text(_strip_code(mapper) + "\n")

    reply = complete(
        [{"role": "system", "content": TESTS_PROMPT}, {"role": "user", "content": context}],
        model_tier="strong",
        json_mode=True,
        purpose=f"generate {system} tests",
    )
    try:
        tests = json.loads(reply)
    except json.JSONDecodeError as exc:
        raise LLMError(f"the model did not return valid JSON for the tests: {exc}") from None
    for name in ("MappingTests.cs", "DataRulesTests.cs"):
        if name not in tests:
            raise LLMError(f"the model did not return {name}")
        (out / "tests" / name).write_text(_strip_code(tests[name]) + "\n")

    csharp_layout.tidy(out, LLM_FILES)
    (out / "README.md").write_text(_readme(system, profile, resources))
    meta = {
        "system": system,
        "resources": resources,
        "template_files": template_files + ["README.md"],
        "llm_files": LLM_FILES,
    }
    (work / "generate.json").write_text(json.dumps(meta, indent=1))
    return meta


def _strip_code(text):
    text = text.strip()
    if text.startswith("```"):
        text = text.split("\n", 1)[1] if "\n" in text else ""
        if text.rstrip().endswith("```"):
            text = text.rstrip()[:-3]
    return text.strip()


def _readme(system, profile, resources):
    name = config.SYSTEMS[system]
    return f"""# FHIR adapter for {name}

This adapter is made by the Generate agent from `workspace/{system}/mapping.yaml`.

- `src/Mapper.cs`, `tests/MappingTests.cs` and `tests/DataRulesTests.cs` are written by the LLM.
- Everything else comes from templates in `agents/templates/dotnet/`.

It reads the source with `{profile["reader"]}` and serves FHIR R4 JSON:
{chr(10).join(f"- `GET /fhir/{r}` (search with `?patient=`) and `GET /fhir/{r}/{{id}}`" for r in resources)}
- `GET /fhir/metadata` and `GET /health`

Run the tests (the mock systems must be up):

```bash
dotnet test generated/{system}/tests
```
"""


def summarise(meta, system):
    out = config.generated_dir() / system
    loc = {f: sum(1 for line in (out / f).read_text().splitlines() if line.strip()) for f in LLM_FILES}
    tests = sum((out / f).read_text().count("[Fact]") for f in LLM_FILES[1:])
    return (
        f"System {system}: adapter in generated/{system} for {', '.join(meta['resources'])}\n"
        f"  LLM files: " + ", ".join(f"{f} ({n} lines)" for f, n in loc.items()) + f"\n  unit tests: {tests}"
    )
