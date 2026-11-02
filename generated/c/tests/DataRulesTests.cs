using System;
using System.Collections.Generic;
using System.Linq;
using Adapter;
using Hl7.Fhir.Model;

namespace Adapter.Tests;

static class RuleHelper
{
    static int n;
    public static SourceRecord With(SourceRecord r, string k, string? v)
    {
        r[k] = v;
        return r;
    }
    public static SourceRecord Rec(string evt = "A01", string pid = "00123", string visit = "V0000001", string ts = "20010305101500")
    {
        var r = new SourceRecord();
        r["MSH-7"] = ts;
        r["MSH-9.1"] = "ADT";
        r["MSH-9.2"] = evt;
        r["MSH-9.3"] = evt == "A03" ? "ADT_A03" : "ADT_A01";
        r["MSH-10"] = "RMSG" + (++n).ToString("D8");
        r["EVN-1"] = evt;
        r["EVN-2"] = ts;
        r["PID-1"] = "1";
        r["PID-3.1"] = "HOSP";
        r["PID-3.2"] = pid;
        r["PID-3.3"] = "MR";
        r["PID-5.1"] = "Testfamily";
        r["PID-5.2"] = "Testgiven";
        r["PID-5.3"] = "Testmiddle";
        r["PID-7"] = "20010214";
        r["PID-8"] = "F";
        r["PID-11.1"] = "12 Test Street";
        r["PID-11.3"] = "Testtown";
        r["PID-11.4"] = "MA";
        r["PID-11.5"] = "09876";
        r["PID-11.6"] = "USA";
        r["PID-16"] = "M";
        r["PV1-1"] = "1";
        r["PV1-2"] = "I";
        r["PV1-3.1"] = "MED1";
        r["PV1-3.2"] = "013";
        r["PV1-3.3"] = "B";
        r["PV1-3.4"] = "HOSP";
        r["PV1-4"] = "E";
        r["PV1-7.1"] = "D00001";
        r["PV1-7.2"] = "Testdoc";
        r["PV1-7.3"] = "Testdocgiven";
        r["PV1-10"] = "MED";
        r["PV1-19"] = visit;
        r["PV1-44"] = "20010305101500";
        if (evt == "A03")
        {
            r["PV1-36"] = "01";
            r["PV1-45"] = "20010306120000";
        }
        r["ZEN-1"] = "1";
        r["ZEN-2.1"] = "50849002";
        r["ZEN-2.2"] = "Emergency room admission (procedure)";
        r["ZEN-2.3"] = "SCT";
        r["ZEN-3.1"] = "72892002";
        r["ZEN-3.2"] = "Normal pregnancy (finding)";
        r["ZEN-3.3"] = "SCT";
        return r;
    }
    public static MappedData Run(params SourceRecord[] rows)
    {
        var d = new SourceData();
        d.Tables["messages"] = rows.ToList();
        return Mapper.Map(d);
    }
}

public class DataRulesTests
{
    [Fact]
    public void drop_duplicates_one_patient_and_one_encounter()
    {
        var d = RuleHelper.Run(RuleHelper.Rec("A01"), RuleHelper.Rec("A03", ts: "20010306120000"));
        Assert.Single(d.Patients);
        Assert.Single(d.Encounters);
        Assert.True(d.RuleCounts["drop_duplicates"] > 0);
    }

    [Fact]
    public void drop_duplicates_same_patient_two_visits()
    {
        var d = RuleHelper.Run(RuleHelper.Rec("A01", visit: "V0000001"), RuleHelper.Rec("A01", visit: "V0000002"));
        Assert.Single(d.Patients);
        Assert.Equal(2, d.Encounters.Count);
        Assert.True(d.RuleCounts["drop_duplicates"] > 0);
    }

    [Fact]
    public void latest_event_wins_a08_does_not_erase_discharge()
    {
        var a08 = RuleHelper.Rec("A08", ts: "20010307090000");
        var d = RuleHelper.Run(RuleHelper.Rec("A01"), RuleHelper.Rec("A03", ts: "20010306120000"), a08);
        var e = Assert.Single(d.Encounters);
        Assert.Equal(Encounter.EncounterStatus.Finished, e.Status);
        Assert.Equal("2001-03-06T12:00:00Z", e.Period.End);
        Assert.Equal("home", e.Hospitalization.DischargeDisposition.Coding[0].Code);
        Assert.True(d.RuleCounts["latest_event_wins"] > 0);
    }

    [Fact]
    public void latest_event_wins_input_order_does_not_matter()
    {
        var d = RuleHelper.Run(RuleHelper.Rec("A03", ts: "20010306120000"), RuleHelper.Rec("A01"));
        var e = Assert.Single(d.Encounters);
        Assert.Equal(Encounter.EncounterStatus.Finished, e.Status);
        Assert.Equal("2001-03-06T12:00:00Z", e.Period.End);
    }

    [Fact]
    public void latest_event_wins_a01_is_in_progress()
    {
        var d = RuleHelper.Run(RuleHelper.Rec("A01"));
        Assert.Equal(Encounter.EncounterStatus.InProgress, Assert.Single(d.Encounters).Status);
    }

    [Fact]
    public void latest_event_wins_a08_updates_demographics()
    {
        var a08 = RuleHelper.With(RuleHelper.Rec("A08", ts: "20010307090000"), "PID-5.1", "Testchanged");
        var d = RuleHelper.Run(RuleHelper.Rec("A01"), a08);
        Assert.Equal("Testchanged", Assert.Single(d.Patients).Name[0].Family);
    }

    [Fact]
    public void normalise_identifier_strips_leading_zeros()
    {
        var d = RuleHelper.Run(RuleHelper.Rec(pid: "00123"));
        var p = Assert.Single(d.Patients);
        Assert.Equal("123", p.Id);
        Assert.Equal("00123", p.Identifier[0].Value);
        Assert.Equal("Patient/123", Assert.Single(d.Encounters).Subject.Reference);
        Assert.True(d.RuleCounts["normalise_identifier"] > 0);
    }

    [Fact]
    public void normalise_identifier_non_numeric_keeps_record()
    {
        var d = RuleHelper.Run(RuleHelper.Rec(pid: "ABC"));
        var p = Assert.Single(d.Patients);
        Assert.Equal("ABC", p.Id);
    }

    [Fact]
    public void timezone_assumption_writes_z()
    {
        var d = RuleHelper.Run(RuleHelper.Rec("A03"));
        var e = Assert.Single(d.Encounters);
        Assert.Equal("2001-03-05T10:15:00Z", e.Period.Start);
        Assert.EndsWith("Z", e.Period.End);
        Assert.True(d.RuleCounts["timezone_assumption"] > 0);
    }

    [Fact]
    public void date_format_birth_date_stays_date()
    {
        var d = RuleHelper.Run(RuleHelper.Rec());
        Assert.Equal("2001-02-14", Assert.Single(d.Patients).BirthDate);
        Assert.True(d.RuleCounts["date_format"] > 0);
    }

    [Fact]
    public void trim_whitespace_collapses_spaces()
    {
        var d = RuleHelper.Run(RuleHelper.With(RuleHelper.Rec(), "ZEN-3.2", "  Non-small cell carcinoma of lung  TNM stage 1 (disorder)  "));
        Assert.Equal("Non-small cell carcinoma of lung TNM stage 1 (disorder)", Assert.Single(d.Encounters).ReasonCode[0].Coding[0].Display);
        Assert.True(d.RuleCounts["trim_whitespace"] > 0);
    }

    [Fact]
    public void restore_leading_zeros_pads_postal_code()
    {
        var d = RuleHelper.Run(RuleHelper.With(RuleHelper.Rec(), "PID-11.5", "9876"));
        Assert.Equal("09876", Assert.Single(d.Patients).Address[0].PostalCode);
        Assert.True(d.RuleCounts["restore_leading_zeros"] > 0);
    }

    [Fact]
    public void other_null_optional_fields_are_omitted()
    {
        var r = RuleHelper.Rec();
        r["PID-5.3"] = null;
        r["PID-11.5"] = null;
        r["PID-16"] = null;
        r["PV1-3.2"] = null;
        r["PV1-3.3"] = null;
        r["ZEN-3.1"] = null;
        r["ZEN-3.2"] = null;
        r["ZEN-3.3"] = null;
        var d = RuleHelper.Run(r);
        var p = Assert.Single(d.Patients);
        var e = Assert.Single(d.Encounters);
        Assert.Single(p.Name[0].Given);
        Assert.True(string.IsNullOrEmpty(p.Address[0].PostalCode));
        Assert.Null(p.MaritalStatus);
        Assert.Empty(e.ReasonCode);
        Assert.Equal("MED1", e.Location[0].Location.Display);
        Assert.True(d.RuleCounts["other"] > 0);
    }
}
