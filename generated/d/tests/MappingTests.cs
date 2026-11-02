using System.Linq;
using Adapter;
using Hl7.Fhir.Model;

namespace Adapter.Tests;

internal static class MapRows
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

    public static Encounter Enc(MappedData m, string apptRef = "AB100001") =>
        m.Encounters.Single(e => e.Identifier.Any(i => i.Value == apptRef));

    public static Patient Pat(MappedData m, string id = "30060") =>
        m.Patients.Single(p => p.Id == id);
}

public class MappingTests
{
    [Fact]
    public void ApptRef()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row()));
        var e = MapRows.Enc(m);
        Assert.Equal("AB100001", e.Identifier[0].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/appointments-apptref", e.Identifier[0].System);
        Assert.False(string.IsNullOrEmpty(e.Id));
    }

    [Fact]
    public void PatNo_PatientId()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row()));
        Assert.Equal("30060", MapRows.Pat(m).Id);
    }

    [Fact]
    public void PatNo_OriginalIdentifier()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row()));
        var p = MapRows.Pat(m);
        Assert.Equal("P/0030060-W", p.Identifier[0].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/appointments-patno", p.Identifier[0].System);
    }

    [Fact]
    public void PatNo_HospitalNumberIdentifier()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row()));
        var p = MapRows.Pat(m);
        Assert.Equal("30060", p.Identifier[1].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/hospital-number", p.Identifier[1].System);
    }

    [Fact]
    public void PatNo_EncounterSubject()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row()));
        Assert.Equal("Patient/30060", MapRows.Enc(m).Subject.Reference);
    }

    [Fact]
    public void PatName_Family()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(name: "Testgiven Testmiddle TESTFAMILY")));
        Assert.Equal("Testfamily", MapRows.Pat(m).Name[0].Family);
    }

    [Fact]
    public void PatName_Given()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(name: "Testgiven Testmiddle TESTFAMILY")));
        Assert.Equal(new[] { "Testgiven", "Testmiddle" }, MapRows.Pat(m).Name[0].Given.ToArray());
    }

    [Fact]
    public void PatName_Text()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(name: "Testgiven Testmiddle TESTFAMILY")));
        Assert.Equal("Testgiven Testmiddle TESTFAMILY", MapRows.Pat(m).Name[0].Text);
    }

    [Fact]
    public void GebDat()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(dob: "07.03.2001")));
        Assert.Equal("2001-03-07", MapRows.Pat(m).BirthDate);
    }

    [Fact]
    public void Sex_Male()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(sex: "m")));
        Assert.Equal(AdministrativeGender.Male, MapRows.Pat(m).Gender);
    }

    [Fact]
    public void Sex_W_IsFemale()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(sex: "w")));
        Assert.Equal(AdministrativeGender.Female, MapRows.Pat(m).Gender);
    }

    [Fact]
    public void Sex_Null_IsUnknown()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(sex: null)));
        Assert.Equal(AdministrativeGender.Unknown, MapRows.Pat(m).Gender);
    }

    [Fact]
    public void ApptStart_PeriodStart()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(start: "12.01.2001 20:44")));
        Assert.Equal("2001-01-12T20:44:00Z", MapRows.Enc(m).Period.Start);
    }

    [Fact]
    public void ApptStart_PeriodEnd()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(start: "12.01.2001 20:44", dur: "30")));
        Assert.Equal("2001-01-12T21:14:00Z", MapRows.Enc(m).Period.End);
    }

    [Fact]
    public void DurMin_PeriodEnd()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(start: "12.01.2001 10:00", dur: "120")));
        Assert.Equal("2001-01-12T12:00:00Z", MapRows.Enc(m).Period.End);
    }

    [Fact]
    public void Clinic_ServiceType()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(clinic: "OPD-SPEC")));
        var c = MapRows.Enc(m).ServiceType.Coding[0];
        Assert.Equal("OPD-SPEC", c.Code);
        Assert.Equal("http://hospital.example.org/fhir/CodeSystem/clinic", c.System);
    }

    [Theory]
    [InlineData("OPD-GEN")]
    [InlineData("OPD-SPEC")]
    [InlineData("GP-WELL")]
    public void Clinic_Class(string clinic)
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(clinic: clinic)));
        Assert.Equal("AMB", MapRows.Enc(m).Class.Code);
    }

    [Fact]
    public void ApptType_Text()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(apptType: "Encounter for check up (procedure)")));
        Assert.Equal("Encounter for check up (procedure)", MapRows.Enc(m).Type[0].Text);
    }

    [Fact]
    public void ApptType_SnomedCoding()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(apptType: "Encounter for check up (procedure)")));
        var c = MapRows.Enc(m).Type[0].Coding[0];
        Assert.Equal("http://snomed.info/sct", c.System);
        Assert.False(string.IsNullOrEmpty(c.Code));
    }

    [Theory]
    [InlineData("BK", Encounter.EncounterStatus.Planned)]
    [InlineData("DN", Encounter.EncounterStatus.Finished)]
    [InlineData("NS", Encounter.EncounterStatus.Cancelled)]
    [InlineData("CX", Encounter.EncounterStatus.Cancelled)]
    public void Status(string code, Encounter.EncounterStatus expected)
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(status: code)));
        Assert.Equal(expected, MapRows.Enc(m).Status);
    }

    [Fact]
    public void Status_History_CurrentIsLatest()
    {
        var m = Mapper.Map(MapRows.Data(
            MapRows.Row(file: "appts_2026-W22.csv", status: "BK"),
            MapRows.Row(file: "appts_2026-W23.csv", status: "DN")));
        Assert.Equal(Encounter.EncounterStatus.Finished, MapRows.Enc(m).Status);
    }

    [Fact]
    public void Booked_Extension()
    {
        var m = Mapper.Map(MapRows.Data(MapRows.Row(booked: "2001-01-05")));
        var ext = MapRows.Enc(m).Extension.Single(x => x.Url == "http://hospital.example.org/fhir/StructureDefinition/booked-date");
        var d = Assert.IsType<Date>(ext.Value);
        Assert.Equal("2001-01-05", d.Value);
    }
}
