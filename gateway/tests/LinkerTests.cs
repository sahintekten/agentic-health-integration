using Gateway;

namespace Gateway.Tests;

public class LinkerTests
{
    private static SourcePatient Master(string id, string family, string given, string dob) => new("a", id, family, given, dob);

    [Fact]
    public void Same_hospital_number_is_an_exact_link()
    {
        var links = Linker.Build(new[] { Master("42", "Testfamily", "Testgiven", "2001-02-03"), new SourcePatient("b", "42", "TESTFAMILY", "TESTGIVEN", "2001-02-03") }, "a");
        var b = links.Single(l => l.System == "b");
        Assert.Equal("exact", b.LinkType);
        Assert.Equal("42", b.PersonId);
        Assert.True(b.Merged);
    }

    [Fact]
    public void Different_id_with_same_name_and_birth_date_is_a_pending_probable_link()
    {
        var links = Linker.Build(new[] { Master("42", "Testfamily", "Testgiven Middle", "2001-02-03"), new SourcePatient("b", "900042", "TESTFAMILY", "TESTGIV", "2001-02-03") }, "a");
        var b = links.Single(l => l.System == "b");
        Assert.Equal("probable", b.LinkType);
        Assert.Equal("pending", b.Status);
        Assert.Equal("42", b.PersonId);
        Assert.False(b.Merged);
    }

    [Fact]
    public void Different_birth_date_is_not_linked()
    {
        var links = Linker.Build(new[] { Master("42", "Testfamily", "Testgiven", "2001-02-03"), new SourcePatient("b", "900042", "TESTFAMILY", "TESTGIVEN", "2001-02-04") }, "a");
        Assert.Equal("unlinked", links.Single(l => l.System == "b").LinkType);
    }

    [Fact]
    public void Accents_and_case_do_not_matter()
    {
        Assert.True(Linker.SamePerson(
            new SourcePatient("a", "1", "Chavarría", "Amalia", "2001-02-03"),
            new SourcePatient("b", "2", "CHAVARRIA", "AMALIA", "2001-02-03")));
    }

    [Fact]
    public void Two_possible_matches_stay_unlinked()
    {
        var links = Linker.Build(new[]
        {
            Master("42", "Testfamily", "Testgiven", "2001-02-03"),
            Master("43", "Testfamily", "Testgiven", "2001-02-03"),
            new SourcePatient("b", "900042", "TESTFAMILY", "TESTGIVEN", "2001-02-03"),
        }, "a");
        var b = links.Single(l => l.System == "b");
        Assert.Equal("unlinked", b.LinkType);
        Assert.Equal("more than one possible match", b.Reason);
    }

    [Fact]
    public void Review_decision_is_kept_after_a_rebuild()
    {
        var previous = new Dictionary<(string, string), string> { [("b", "900042")] = "approved" };
        var links = Linker.Build(new[] { Master("42", "Testfamily", "Testgiven", "2001-02-03"), new SourcePatient("b", "900042", "TESTFAMILY", "TESTGIVEN", "2001-02-03") }, "a", previous);
        var b = links.Single(l => l.System == "b");
        Assert.Equal("approved", b.Status);
        Assert.True(b.Merged);
    }

    [Fact]
    public void Link_store_keeps_review_decisions_in_sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"index-{Guid.NewGuid():N}.db");
        var store = new LinkStore(path);
        store.ReplaceAll(new[] { new Link("b", "900042", "42", "probable", "pending", "test") });
        Assert.True(store.SetStatus("b", "900042", "rejected"));
        Assert.Equal("rejected", new LinkStore(path).ReviewDecisions()[("b", "900042")]);
    }
}
