"""Small offline tests for helpers that make weaker models' output usable."""

from agents import design, validate


def test_relative_paths_get_the_resource_name():
    mapping = {
        "mappings": [
            {"source": "ApptRef", "resource": "Encounter", "path": "identifier[0].value"},
            {"source": "Sex", "resource": "Patient", "path": "Patient.gender"},
            {"source": "Name", "resource": "Patient", "path": "name[0].family and name[0].given"},
            {"source": "Note", "ignore": "footer"},
        ]
    }
    design.qualify_paths(mapping)
    paths = [m.get("path") for m in mapping["mappings"]]
    assert paths == [
        "Encounter.identifier[0].value",
        "Patient.gender",
        "Patient.name[0].family and Patient.name[0].given",
        None,
    ]


def test_qualify_handles_table_style_mappings():
    mapping = {"mappings": {"rows": [{"source": "x", "resource": "Encounter", "path": "status"}]}}
    design.qualify_paths(mapping)
    assert mapping["mappings"]["rows"][0]["path"] == "Encounter.status"


def test_build_error_source_line(tmp_path):
    file = tmp_path / "A.cs"
    file.write_text("class A\n{\n    var x = 1;\n}\n")
    assert validate._source_line(file, 3) == "var x = 1;"
    assert validate._source_line(file, 99) == ""
    assert validate._source_line(tmp_path / "missing.cs", 1) == ""
