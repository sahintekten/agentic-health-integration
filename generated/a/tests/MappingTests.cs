using System.Collections.Generic;
using System.Linq;
using Adapter;
using Hl7.Fhir.Model;

namespace Adapter.Tests;

public class MappingTests
{
    private static SourceRecord Row(string id = "MRN-000123", string name = "TESTFAMILY, TESTGIVEN", string dob = "07/03/2001", string sx = "F", string? post = "01234", string? mrtl = null, string? dcsd = null)
    {
        return new SourceRecord
        {
            ["pt_id"] = id,
            ["nm_full"] = name,
            ["dob"] = dob,
            ["sx"] = sx,
            ["addr_line"] = "  12 Test Street ",
            ["town"] = " Testtown ",
            ["post_cd"] = post,
            ["mrtl"] = mrtl,
            ["dcsd"] = dcsd,
        };
    }

    private static Patient MapOne(SourceRecord r)
    {
        var d = new SourceData();
        d.Tables["patients"] = new List<SourceRecord> { r };
        var result = Mapper.Map(d);
        return Assert.Single(result.Patients);
    }

    [Fact]
    public void Pt_id_BecomesPatientIdWithoutPrefixAndZeros()
    {
        var p = MapOne(Row(id: "MRN-000123"));
        Assert.Equal("123", p.Id);
    }

    [Fact]
    public void Pt_id_KeptAsIdentifier()
    {
        var p = MapOne(Row(id: "MRN-000123"));
        var ident = p.Identifier[0];
        Assert.Equal("MRN-000123", ident.Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/prs-mrn", ident.System);
        Assert.Contains(ident.Type.Coding, c => c.Code == "MR");
    }

    [Fact]
    public void Nm_full_Family()
    {
        var p = MapOne(Row(name: "TESTFAMILY, TESTGIVEN"));
        Assert.Equal("Testfamily", p.Name[0].Family);
    }

    [Fact]
    public void Nm_full_Given()
    {
        var p = MapOne(Row(name: "TESTFAMILY, TESTGIVEN TESTMIDDLE"));
        Assert.Equal(new[] { "Testgiven", "Testmiddle" }, p.Name[0].Given.ToArray());
    }

    [Fact]
    public void Dob_ParsedDayFirst()
    {
        var p = MapOne(Row(dob: "25/12/2001"));
        Assert.Equal("2001-12-25", p.BirthDate);
    }

    [Fact]
    public void Sx_Female()
    {
        var p = MapOne(Row(sx: "F"));
        Assert.Equal(AdministrativeGender.Female, p.Gender);
    }

    [Fact]
    public void Sx_Male()
    {
        var p = MapOne(Row(sx: "M"));
        Assert.Equal(AdministrativeGender.Male, p.Gender);
    }

    [Fact]
    public void Sx_UnknownIsUnknownNotOther()
    {
        var p = MapOne(Row(sx: "U"));
        Assert.Equal(AdministrativeGender.Unknown, p.Gender);
    }

    [Fact]
    public void Addr_line_Trimmed()
    {
        var p = MapOne(Row());
        Assert.Equal("12 Test Street", p.Address[0].Line.First());
    }

    [Fact]
    public void Town_Trimmed()
    {
        var p = MapOne(Row());
        Assert.Equal("Testtown", p.Address[0].City);
    }

    [Fact]
    public void Post_cd_PaddedToFiveDigits()
    {
        var p = MapOne(Row(post: "1234"));
        Assert.Equal("01234", p.Address[0].PostalCode);
    }

    [Fact]
    public void Post_cd_NullOmitted()
    {
        var p = MapOne(Row(post: null));
        Assert.True(p.Address.Count == 0 || string.IsNullOrEmpty(p.Address[0].PostalCode));
    }

    [Fact]
    public void Mrtl_MappedToCoding()
    {
        var p = MapOne(Row(mrtl: "W"));
        var coding = p.MaritalStatus.Coding[0];
        Assert.Equal("W", coding.Code);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", coding.System);
    }

    [Fact]
    public void Mrtl_NullOmitted()
    {
        var p = MapOne(Row(mrtl: null));
        Assert.True(p.MaritalStatus == null || p.MaritalStatus.Coding.Count == 0);
    }

    [Fact]
    public void Dcsd_MappedToDeceasedDateTime()
    {
        var p = MapOne(Row(dcsd: "2001-09-15"));
        var dt = Assert.IsType<FhirDateTime>(p.Deceased);
        Assert.Equal("2001-09-15", dt.Value);
    }

    [Fact]
    public void Dcsd_NullLeavesDeceasedAbsent()
    {
        var p = MapOne(Row(dcsd: null));
        Assert.Null(p.Deceased);
    }
}
