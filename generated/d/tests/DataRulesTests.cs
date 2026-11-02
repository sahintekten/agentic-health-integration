namespace Adapter.Tests;

using Adapter;
using Hl7.Fhir.Model;

public class DataRulesTests
{
    private static SourceRecord Row(string appt = "AB000001", string pat = "P/0000123-K", string start = "12.01.2001 20:44", string dur = "30", string status = "BK", string file = "appointments_2026-W22.csv")
        => MappingTests.Row(appt: appt, pat: pat, start: start, dur: dur, status: status, file: file);

    private static SourceData Make(params SourceRecord[] rows) => MappingTests.Make(rows);

    [Fact]
    public void DropDuplicates_KeepsOneEncounterPerApptRef()
    {
        var m = Mapper.Map(Make(
            Row(status: "BK", file: "appointments_2026-W22.csv"),
            Row(status: "DN", file: "appointments_2026-W30.csv")));
        Assert.Single(m.Encounters);
        Assert.True(m.RuleCounts["drop_duplicates"] > 0);
    }

    [Fact]
    public void LatestEventWins_LaterFileSetsStatus()
    {
        var m = Mapper.Map(Make(
            Row(status: "BK", file: "appointments_2026-W22.csv"),
            Row(status: "DN", file: "appointments_2026-W30.csv")));
        Assert.Equal(Encounter.EncounterStatus.Finished, m.Encounters.Single().Status);
        Assert.True(m.RuleCounts["latest_event_wins"] > 0);
    }

    [Fact]
    public void LatestEventWins_RowOrderDoesNotMatter()
    {
        var m = Mapper.Map(Make(
            Row(status: "CX", file: "appointments_2026-W31.csv"),
            Row(status: "BK", file: "appointments_2026-W22.csv")));
        Assert.Equal(Encounter.EncounterStatus.Cancelled, m.Encounters.Single().Status);
    }

    [Fact]
    public void NormaliseIdentifier_StripsPrefixAndZeros()
    {
        var m = Mapper.Map(Make(Row(pat: "P/0000123-K")));
        Assert.Equal("123", m.Patients.Single().Id);
        Assert.True(m.RuleCounts["normalise_identifier"] > 0);
    }

    [Fact]
    public void NormaliseIdentifier_UnmatchedIdKeptAsUnlinked()
    {
        var m = Mapper.Map(Make(Row(pat: "UNKNOWN")));
        Assert.Single(m.Patients);
        Assert.Equal("UNKNOWN", m.Patients[0].Id);
        Assert.Equal("Patient/UNKNOWN", m.Encounters.Single().Subject.Reference);
    }

    [Fact]
    public void TimezoneAssumption_WritesZAndCounts()
    {
        var m = Mapper.Map(Make(Row(start: "12.01.2001 20:44")));
        Assert.EndsWith("Z", m.Encounters.Single().Period.Start);
        Assert.True(m.RuleCounts["timezone_assumption"] > 0);
    }

    [Fact]
    public void DateFormat_DayFirstParsing()
    {
        var m = Mapper.Map(Make(Row(start: "03.02.2001 08:15")));
        Assert.Equal("2001-02-03T08:15:00Z", m.Encounters.Single().Period.Start);
        Assert.True(m.RuleCounts["date_format"] > 0);
    }

    [Fact]
    public void Other_DurationOutlierKeptAndFlagged()
    {
        var m = Mapper.Map(Make(Row(dur: "354")));
        Assert.Equal(354m, m.Encounters.Single().Length.Value);
        Assert.True(m.RuleCounts["other"] > 0);
    }

    [Fact]
    public void SameStartDifferentApptRef_NotMerged()
    {
        var m = Mapper.Map(Make(
            Row(appt: "AB000001", pat: "P/0000123-K"),
            Row(appt: "AB000002", pat: "P/0000456-L")));
        Assert.Equal(2, m.Encounters.Count);
        Assert.Equal(2, m.Patients.Count);
    }

    [Fact]
    public void SamePatientTwoAppointments_OnePatient()
    {
        var m = Mapper.Map(Make(
            Row(appt: "AB000001"),
            Row(appt: "AB000002", start: "15.02.2001 09:00")));
        Assert.Equal(2, m.Encounters.Count);
        Assert.Single(m.Patients);
    }
}
