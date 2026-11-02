using System.Linq;
using Adapter;
using Hl7.Fhir.Model;

namespace Adapter.Tests;

internal static class RuleRows
{
    public static SourceRecord Row(
        string apptRef = "AB100001",
        string patNo = "P/0030060-W",
        string file = "appts_2026-W22.csv",
        string status = "BK",
        string? sex = "m",
        string start = "12.01.2001 20:44",
        string dur = "30",
        string clinic = "OPD-GEN",
        string apptType = "Encounter for check up (procedure)",
        string name = "Testgiven TESTFAMILY",
        string dob = "07.03.2001",
        string booked = "2001-01-05")
    {
        return new SourceRecord
        {
            ["ApptRef"] = apptRef,
            ["PatNo"] = patNo,
            ["PatName"] = name,
            ["Geb.Dat"] = dob,
            ["Sex"] = sex,
            ["ApptStart"] = start,
            ["DurMin"] = dur,
            ["Clinic"] = clinic,
            ["ApptType"] = apptType,
            ["Status"] = status,
            ["Booked"] = booked,
            ["_file"] = file,
        };
    }

    public static SourceData Data(params SourceRecord[] rows)
    {
        var d = new SourceData();
        d.Tables["rows"] = rows.ToList();
        return d;
    }
}

public class DataRulesTests
{
    [Fact]
    public void LatestEventWins_OneEncounterWithLatestStatus()
    {
        var data = Mapper.Map(RuleRows.Data(
            RuleRows.Row(file: "appts_2026-W22.csv", status: "BK"),
            RuleRows.Row(file: "appts_2026-W23.csv", status: "DN")));
        var e = Assert.Single(data.Encounters);
        Assert.Equal(Encounter.EncounterStatus.Finished, e.Status);
        Assert.True(data.RuleCounts["latest_event_wins"] > 0);
    }

    [Fact]
    public void LatestEventWins_LatestWinsEvenIfListedFirst()
    {
        var data = Mapper.Map(RuleRows.Data(
            RuleRows.Row(file: "appts_2026-W30.csv", status: "CX"),
            RuleRows.Row(file: "appts_2026-W25.csv", status: "BK")));
        var e = Assert.Single(data.Encounters);
        Assert.Equal(Encounter.EncounterStatus.Cancelled, e.Status);
    }

    [Fact]
    public void DropDuplicates_IdenticalRowsKeepOne()
    {
        var data = Mapper.Map(RuleRows.Data(
            RuleRows.Row(file: "appts_2026-W22.csv"),
            RuleRows.Row(file: "appts_2026-W23.csv")));
        Assert.Single(data.Encounters);
        Assert.Single(data.Patients);
        Assert.True(data.RuleCounts["drop_duplicates"] > 0);
    }

    [Fact]
    public void NormaliseIdentifier_StripsPrefixSuffixAndZeros()
    {
        var data = Mapper.Map(RuleRows.Data(RuleRows.Row(patNo: "P/0000042-A")));
        var p = Assert.Single(data.Patients);
        Assert.Equal("42", p.Id);
        Assert.Contains(p.Identifier, i => i.Value == "P/0000042-A");
        Assert.True(data.RuleCounts["normalise_identifier"] > 0);
    }

    [Fact]
    public void NormaliseIdentifier_UnparsableIdKeepsRecord()
    {
        var data = Mapper.Map(RuleRows.Data(RuleRows.Row(patNo: "UNKNOWN")));
        var p = Assert.Single(data.Patients);
        Assert.Equal("UNKNOWN", p.Id);
        Assert.Single(data.Encounters);
    }

    [Fact]
    public void TimezoneAssumption_UtcWithZ()
    {
        var data = Mapper.Map(RuleRows.Data(RuleRows.Row(start: "12.01.2001 20:44")));
        var e = Assert.Single(data.Encounters);
        Assert.EndsWith("Z", e.Period.Start);
        Assert.True(data.RuleCounts["timezone_assumption"] > 0);
    }

    [Fact]
    public void DateFormat_AllThreeFormatsParsed()
    {
        var data = Mapper.Map(RuleRows.Data(RuleRows.Row(dob: "07.03.2001", start: "12.01.2001 20:44", booked: "2001-01-05")));
        var p = Assert.Single(data.Patients);
        var e = Assert.Single(data.Encounters);
        Assert.Equal("2001-03-07", p.BirthDate);
        Assert.Equal("2001-01-12T20:44:00Z", e.Period.Start);
        Assert.Contains(e.Extension, x => x.Value is Date d && d.Value == "2001-01-05");
        Assert.True(data.RuleCounts["date_format"] > 0);
    }

    [Fact]
    public void DurMin_OverMaximumIsFlaggedButKept()
    {
        var data = Mapper.Map(RuleRows.Data(RuleRows.Row(start: "12.01.2001 08:00", dur: "354")));
        var e = Assert.Single(data.Encounters);
        Assert.Equal("2001-01-12T13:54:00Z", e.Period.End);
        Assert.True(data.RuleCounts["other"] > 0);
    }

    [Fact]
    public void DurMin_TextWithVaryingWidthIsCast()
    {
        var data = Mapper.Map(RuleRows.Data(RuleRows.Row(start: "12.01.2001 08:00", dur: "45")));
        var e = Assert.Single(data.Encounters);
        Assert.Equal("2001-01-12T08:45:00Z", e.Period.End);
    }

    [Fact]
    public void SameApptStartDifferentApptRef_NotMerged()
    {
        var data = Mapper.Map(RuleRows.Data(
            RuleRows.Row(apptRef: "AB100001", patNo: "P/0030060-W", file: "appts_2026-W22.csv"),
            RuleRows.Row(apptRef: "AB100002", patNo: "P/0000077-B", file: "appts_2026-W23.csv", name: "Testother TESTLAST")));
        Assert.Equal(2, data.Encounters.Count);
        Assert.Equal(2, data.Patients.Count);
        Assert.True(data.RuleCounts["other"] > 0);
    }

    [Fact]
    public void SamePatientInTwoAppointments_OnePatient()
    {
        var data = Mapper.Map(RuleRows.Data(
            RuleRows.Row(apptRef: "AB100001", start: "12.01.2001 08:00"),
            RuleRows.Row(apptRef: "AB100002", start: "13.01.2001 09:00")));
        Assert.Equal(2, data.Encounters.Count);
        Assert.Single(data.Patients);
    }
}
