namespace Adapter.Tests;

using Adapter;
using Hl7.Fhir.Model;

public class MappingTests
{
    internal static SourceRecord Row(string appt = "AB000001", string pat = "P/0000123-K", string name = "Testgiven TESTFAMILY", string? dob = "07.03.2001", string? sex = "m", string start = "12.01.2001 20:44", string dur = "30", string clinic = "OPD-GEN", string type = "Follow-up encounter (procedure)", string status = "BK", string booked = "2001-01-05", string file = "appointments_2026-W22.csv")
    {
        var r = new SourceRecord();
        r["ApptRef"] = appt;
        r["PatNo"] = pat;
        r["PatName"] = name;
        r["Geb.Dat"] = dob;
        r["Sex"] = sex;
        r["ApptStart"] = start;
        r["DurMin"] = dur;
        r["Clinic"] = clinic;
        r["ApptType"] = type;
        r["Status"] = status;
        r["Booked"] = booked;
        r["_file"] = file;
        return r;
    }

    internal static SourceData Make(params SourceRecord[] rows)
    {
        var d = new SourceData();
        d.Tables["rows"] = rows.ToList();
        return d;
    }

    private static Encounter Enc(MappedData m, string id = "AB000001") => m.Encounters.Single(e => e.Id == id);
    private static Patient Pat(MappedData m, string id = "123") => m.Patients.Single(p => p.Id == id);

    [Fact]
    public void ApptRef()
    {
        var m = Mapper.Map(Make(Row()));
        var e = Enc(m);
        Assert.Equal("AB000001", e.Identifier[0].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/appointments-apptref", e.Identifier[0].System);
    }

    [Fact]
    public void PatNo_PatientId()
    {
        var m = Mapper.Map(Make(Row()));
        Assert.Equal("123", Pat(m).Id);
    }

    [Fact]
    public void PatNo_SourceIdentifier()
    {
        var m = Mapper.Map(Make(Row()));
        var p = Pat(m);
        Assert.Equal("P/0000123-K", p.Identifier[0].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/appointments-patno", p.Identifier[0].System);
    }

    [Fact]
    public void PatNo_HospitalNumberIdentifier()
    {
        var m = Mapper.Map(Make(Row()));
        var p = Pat(m);
        Assert.Equal("123", p.Identifier[1].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/hospital-number", p.Identifier[1].System);
    }

    [Fact]
    public void PatNo_EncounterSubject()
    {
        var m = Mapper.Map(Make(Row()));
        Assert.Equal("Patient/123", Enc(m).Subject.Reference);
    }

    [Fact]
    public void PatName_Text()
    {
        var m = Mapper.Map(Make(Row(name: "  Testgiven TESTFAMILY ")));
        Assert.Equal("Testgiven TESTFAMILY", Pat(m).Name[0].Text);
    }

    [Fact]
    public void PatName_Family()
    {
        var m = Mapper.Map(Make(Row()));
        Assert.Equal("Testfamily", Pat(m).Name[0].Family);
    }

    [Fact]
    public void PatName_Given()
    {
        var m = Mapper.Map(Make(Row(name: "Testgiven Testmiddle TESTFAMILY")));
        var p = Pat(m);
        Assert.Equal(new[] { "Testgiven", "Testmiddle" }, p.Name[0].Given.ToArray());
        Assert.Equal("Testfamily", p.Name[0].Family);
    }

    [Fact]
    public void Geb_Dat()
    {
        var m = Mapper.Map(Make(Row(dob: "07.03.2001")));
        Assert.Equal("2001-03-07", Pat(m).BirthDate);
    }

    [Fact]
    public void Sex_Male()
    {
        var m = Mapper.Map(Make(Row(sex: "m")));
        Assert.Equal(AdministrativeGender.Male, Pat(m).Gender);
    }

    [Fact]
    public void Sex_Female()
    {
        var m = Mapper.Map(Make(Row(sex: "w")));
        Assert.Equal(AdministrativeGender.Female, Pat(m).Gender);
    }

    [Fact]
    public void Sex_NullBecomesUnknown()
    {
        var m = Mapper.Map(Make(Row(sex: null)));
        Assert.Equal(AdministrativeGender.Unknown, Pat(m).Gender);
    }

    [Fact]
    public void ApptStart_PeriodStart()
    {
        var m = Mapper.Map(Make(Row(start: "12.01.2001 20:44")));
        Assert.Equal("2001-01-12T20:44:00Z", Enc(m).Period.Start);
    }

    [Fact]
    public void ApptStart_PeriodEnd()
    {
        var m = Mapper.Map(Make(Row(start: "12.01.2001 20:44", dur: "30")));
        Assert.Equal("2001-01-12T21:14:00Z", Enc(m).Period.End);
    }

    [Fact]
    public void DurMin_Length()
    {
        var m = Mapper.Map(Make(Row(dur: "45")));
        var len = Enc(m).Length;
        Assert.Equal(45m, len.Value);
        Assert.Equal("min", len.Unit);
        Assert.Equal("http://unitsofmeasure.org", len.System);
        Assert.Equal("min", len.Code);
    }

    [Fact]
    public void DurMin_PeriodEnd()
    {
        var m = Mapper.Map(Make(Row(start: "12.01.2001 23:50", dur: "20")));
        Assert.Equal("2001-01-13T00:10:00Z", Enc(m).Period.End);
    }

    [Fact]
    public void Clinic_ServiceType()
    {
        var m = Mapper.Map(Make(Row(clinic: "GP-WELL")));
        var c = Enc(m).ServiceType.Coding[0];
        Assert.Equal("GP-WELL", c.Code);
        Assert.Equal("http://hospital.example.org/fhir/CodeSystem/clinic", c.System);
        Assert.False(string.IsNullOrEmpty(Enc(m).ServiceType.Text));
    }

    [Theory]
    [InlineData("OPD-GEN")]
    [InlineData("GP-WELL")]
    [InlineData("OPD-SPEC")]
    public void Clinic_Class(string clinic)
    {
        var m = Mapper.Map(Make(Row(clinic: clinic)));
        var cls = Enc(m).Class;
        Assert.Equal("AMB", cls.Code);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-ActCode", cls.System);
    }

    [Fact]
    public void ApptType()
    {
        var m = Mapper.Map(Make(Row(type: " Well child visit (procedure) ")));
        var t = Enc(m).Type[0];
        Assert.Equal("Well child visit (procedure)", t.Text);
        Assert.Empty(t.Coding);
    }

    [Theory]
    [InlineData("BK", Encounter.EncounterStatus.Planned)]
    [InlineData("DN", Encounter.EncounterStatus.Finished)]
    [InlineData("NS", Encounter.EncounterStatus.Cancelled)]
    [InlineData("CX", Encounter.EncounterStatus.Cancelled)]
    public void Status(string code, Encounter.EncounterStatus expected)
    {
        var m = Mapper.Map(Make(Row(status: code)));
        Assert.Equal(expected, Enc(m).Status);
    }

    [Fact]
    public void Booked()
    {
        var m = Mapper.Map(Make(Row(booked: "2001-01-05")));
        var ext = Enc(m).Extension[0];
        Assert.Equal("http://hospital.example.org/fhir/StructureDefinition/booked-date", ext.Url);
        Assert.Equal("2001-01-05", ((FhirDateTime)ext.Value).Value);
    }
}
