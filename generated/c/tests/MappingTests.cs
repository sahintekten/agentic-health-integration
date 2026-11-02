using System;
using System.Collections.Generic;
using System.Linq;
using Adapter;
using Hl7.Fhir.Model;

namespace Adapter.Tests;

static class MapHelper
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
        r["MSH-10"] = "MSG" + (++n).ToString("D8");
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
    public static Patient P(MappedData d) => d.Patients.Single();
    public static Encounter E(MappedData d) => d.Encounters.Single();
}

public class MappingTests
{
    [Fact]
    public void PID_3_1()
    {
        Assert.Equal("HOSP", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Identifier[0].Assigner.Display);
    }
    [Fact]
    public void PID_3_2_PatientId()
    {
        Assert.Equal("123", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Id);
    }
    [Fact]
    public void PID_3_2_IdentifierValue()
    {
        Assert.Equal("00123", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Identifier[0].Value);
    }
    [Fact]
    public void PID_3_2_EncounterSubject()
    {
        Assert.Equal("Patient/123", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Subject.Reference);
    }
    [Fact]
    public void PID_3_3()
    {
        Assert.Equal("MR", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Identifier[0].Type.Coding[0].Code);
    }
    [Fact]
    public void PID_5_1()
    {
        Assert.Equal("Testfamily", MapHelper.P(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "PID-5.1", "  Testfamily "))).Name[0].Family);
    }
    [Fact]
    public void PID_5_2()
    {
        Assert.Equal("Testgiven", MapHelper.P(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "PID-5.2", " Testgiven  "))).Name[0].Given.ToList()[0]);
    }
    [Fact]
    public void PID_5_3()
    {
        Assert.Equal("Testmiddle", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Name[0].Given.ToList()[1]);
    }
    [Fact]
    public void PID_7()
    {
        Assert.Equal("2001-02-14", MapHelper.P(MapHelper.Run(MapHelper.Rec())).BirthDate);
    }
    [Fact]
    public void PID_8()
    {
        Assert.Equal(AdministrativeGender.Female, MapHelper.P(MapHelper.Run(MapHelper.Rec())).Gender);
    }
    [Fact]
    public void PID_11_1()
    {
        Assert.Equal("12 Test Street", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Address[0].Line.First());
    }
    [Fact]
    public void PID_11_3()
    {
        Assert.Equal("Testtown", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Address[0].City);
    }
    [Fact]
    public void PID_11_4()
    {
        Assert.Equal("MA", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Address[0].State);
    }
    [Fact]
    public void PID_11_5()
    {
        Assert.Equal("09876", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Address[0].PostalCode);
    }
    [Fact]
    public void PID_11_6()
    {
        Assert.Equal("USA", MapHelper.P(MapHelper.Run(MapHelper.Rec())).Address[0].Country);
    }
    [Fact]
    public void PID_16()
    {
        Assert.Equal("S", MapHelper.P(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "PID-16", "S"))).MaritalStatus.Coding[0].Code);
    }
    [Fact]
    public void PV1_2()
    {
        Assert.Equal("IMP", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Class.Code);
        Assert.Equal("EMER", MapHelper.E(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "PV1-2", "E"))).Class.Code);
    }
    [Fact]
    public void PV1_3_1()
    {
        Assert.Equal("MED1 013-B", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Location[0].Location.Display);
    }
    [Fact]
    public void PV1_3_2()
    {
        Assert.Contains("013", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Location[0].Location.Display);
    }
    [Fact]
    public void PV1_3_3()
    {
        Assert.EndsWith("-B", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Location[0].Location.Display);
    }
    [Fact]
    public void PV1_3_4()
    {
        Assert.Equal("HOSP", MapHelper.E(MapHelper.Run(MapHelper.Rec())).ServiceProvider.Display);
    }
    [Fact]
    public void PV1_4()
    {
        Assert.Equal("EM", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Priority.Coding[0].Code);
        Assert.Equal("R", MapHelper.E(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "PV1-4", "R"))).Priority.Coding[0].Code);
    }
    [Fact]
    public void PV1_7_1_EncounterParticipant()
    {
        Assert.Equal("Practitioner/D00001", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Participant[0].Individual.Reference);
    }
    [Fact]
    public void PV1_10()
    {
        Assert.Equal("MED", MapHelper.E(MapHelper.Run(MapHelper.Rec())).ServiceType.Coding[0].Code);
    }
    [Fact]
    public void PV1_19()
    {
        var e = MapHelper.E(MapHelper.Run(MapHelper.Rec()));
        Assert.Equal("V0000001", e.Identifier[0].Value);
        Assert.Equal("V0000001", e.Id);
    }
    [Fact]
    public void PV1_36()
    {
        Assert.Equal("home", MapHelper.E(MapHelper.Run(MapHelper.Rec("A03"))).Hospitalization.DischargeDisposition.Coding[0].Code);
    }
    [Fact]
    public void PV1_44()
    {
        Assert.Equal("2001-03-05T10:15:00Z", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Period.Start);
    }
    [Fact]
    public void PV1_45()
    {
        Assert.Equal("2001-03-06T12:00:00Z", MapHelper.E(MapHelper.Run(MapHelper.Rec("A03"))).Period.End);
    }
    [Fact]
    public void ZEN_2_1()
    {
        Assert.Equal("50849002", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Type[0].Coding[0].Code);
    }
    [Fact]
    public void ZEN_2_2()
    {
        Assert.Equal("Emergency room admission (procedure)", MapHelper.E(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "ZEN-2.2", " Emergency room admission (procedure) "))).Type[0].Coding[0].Display);
    }
    [Fact]
    public void ZEN_2_3()
    {
        Assert.Equal("http://snomed.info/sct", MapHelper.E(MapHelper.Run(MapHelper.Rec())).Type[0].Coding[0].System);
    }
    [Fact]
    public void ZEN_3_1()
    {
        Assert.Equal("72892002", MapHelper.E(MapHelper.Run(MapHelper.Rec())).ReasonCode[0].Coding[0].Code);
    }
    [Fact]
    public void ZEN_3_2()
    {
        Assert.Equal("Non-small cell carcinoma of lung TNM stage 1 (disorder)", MapHelper.E(MapHelper.Run(MapHelper.With(MapHelper.Rec(), "ZEN-3.2", " Non-small cell carcinoma of lung  TNM stage 1 (disorder) "))).ReasonCode[0].Coding[0].Display);
    }
    [Fact]
    public void ZEN_3_3()
    {
        Assert.Equal("http://snomed.info/sct", MapHelper.E(MapHelper.Run(MapHelper.Rec())).ReasonCode[0].Coding[0].System);
    }
}
