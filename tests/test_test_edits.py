"""A repair may change a failing test only with a person's approval, and never remove tests or assertions."""

import pytest
from agents import validate

OLD = """public class T
{
    [Fact]
    public void A()
    {
        Assert.Equal(50m, x);
        Assert.NotNull(y);
    }
}
"""
NEW_SAME = OLD.replace("50m", "5m")
NEW_FEWER = OLD.replace("        Assert.NotNull(y);\n", "")
FILE = "tests/DataRulesTests.cs"


@pytest.fixture
def out(tmp_path):
    (tmp_path / "tests").mkdir()
    (tmp_path / FILE).write_text(OLD)
    return tmp_path


def apply(out, text, approve, bad=frozenset(), reviewable=frozenset({FILE})):
    return validate.apply_repair(out, {FILE: text}, {FILE: OLD}, set(bad), set(reviewable), approve, "input was odd")


def test_approved_change_is_written_and_recorded(out):
    asked = []
    changed, refused, edits = apply(out, NEW_SAME, lambda *a: asked.append(a) or True)
    assert changed == [FILE] and refused == []
    assert "5m" in (out / FILE).read_text()
    assert edits[0]["file"] == FILE and edits[0]["reason"] == "input was odd" and edits[0]["approved_by"]
    assert asked[0][0] == FILE


def test_declined_change_is_not_written(out):
    changed, refused, edits = apply(out, NEW_SAME, lambda *a: False)
    assert changed == [] and edits == []
    assert "not approved" in refused[0]
    assert (out / FILE).read_text() == OLD


def test_no_approver_means_no_change(out):
    changed, refused, _ = apply(out, NEW_SAME, None)
    assert changed == [] and "needs a person" in refused[0]


def test_removing_an_assertion_is_refused_before_asking(out):
    asked = []
    changed, refused, _ = apply(out, NEW_FEWER, lambda *a: asked.append(a) or True)
    assert changed == [] and "remove tests or assertions" in refused[0]
    assert asked == []


def test_skip_is_refused(out):
    changed, refused, _ = apply(out, NEW_SAME.replace("[Fact]", '[Fact(Skip = "x")]'), lambda *a: True)
    assert changed == [] and refused


def test_test_that_does_not_fail_cannot_change(out):
    changed, refused, _ = apply(out, NEW_SAME, lambda *a: True, reviewable=frozenset())
    assert changed == [] and "tests can only change" in refused[0]


def test_compile_error_fix_needs_no_approval(out):
    changed, _, edits = apply(out, NEW_SAME, None, bad={FILE}, reviewable=frozenset())
    assert changed == [FILE] and edits == []


def test_failing_test_names_map_to_files():
    problems = {"problems": [{"test": "Adapter.Tests.DataRulesTests.TrimWhitespace", "message": "x"}, {"test": "?"}]}
    assert validate._failing_test_files(problems) == {FILE}
    assert validate._failing_test_files([{"file": "a"}]) == set()
