using System.Collections.Generic;
using System.Linq;
using Adapter;
using Hl7.Fhir.Model;

namespace Adapter.Tests;

public class DataRulesTests
{
    private static SourceRecord Row(string id = "MRN-000123", string name = "TESTFAMILY, TESTGIVEN", string dob = "07/03/2001", string? post = "01234")
    {
        return new SourceRecord
        {
            ["pt_id"] = id,
            ["nm_full"] = name,
            ["dob"] = dob,
            ["sx"] = "M",
            ["addr_line"] = "12 Test Street",
            ["town"] = "Testtown",
            ["post_cd"] = post,
            ["mrtl"] = null,
            ["dcsd"] = null,
        };
    }

    private static MappedData Run(params SourceRecord[] rows)
    {
        var d = new SourceData();
        d.Tables["patients"] = rows.ToList();
        return Mapper.Map(d);
    }

    [Fact]
    public void NormaliseIdentifier_StripsPrefixAndZeros()
    {
        var data = Run(Row(id: "MRN-000045"));
        var p = Assert.Single(data.Patients);
        Assert.Equal("45", p.Id);
        Assert.Equal("MRN-000045", p.Identifier[0].Value);
        Assert.True(data.RuleCounts["normalise_identifier"] > 0);
    }

    [Fact]
    public void NormaliseIdentifier_NonNumericIdKeepsRecordWithOriginalId()
    {
        var data = Run(Row(id: "MRN-ABCDEF"));
        var p = Assert.Single(data.Patients);
        Assert.Equal("MRN-ABCDEF", p.Id);
    }

    [Fact]
    public void DateFormat_ParsesDayFirst()
    {
        var data = Run(Row(dob: "03/07/2001"));
        var p = Assert.Single(data.Patients);
        Assert.Equal("2001-07-03", p.BirthDate);
        Assert.True(data.RuleCounts["date_format"] > 0);
    }

    [Fact]
    public void RestoreLeadingZeros_PadsShortPostalCode()
    {
        var data = Run(Row(post: "5678"));
        var p = Assert.Single(data.Patients);
        Assert.Equal("05678", p.Address[0].PostalCode);
        Assert.True(data.RuleCounts["restore_leading_zeros"] > 0);
    }

    [Fact]
    public void RestoreLeadingZeros_FullLengthCodeDoesNotFire()
    {
        var data = Run(Row(post: "12345"));
        var p = Assert.Single(data.Patients);
        Assert.Equal("12345", p.Address[0].PostalCode);
        Assert.Equal(0, data.RuleCounts.GetValueOrDefault("restore_leading_zeros"));
    }

    [Fact]
    public void TrimWhitespace_CollapsesSpacesInName()
    {
        var data = Run(Row(name: "  TESTFAMILY,   TESTGIVEN   TESTMIDDLE  "));
        var p = Assert.Single(data.Patients);
        Assert.Equal("Testfamily", p.Name[0].Family);
        Assert.Equal(new[] { "Testgiven", "Testmiddle" }, p.Name[0].Given.ToArray());
        Assert.True(data.RuleCounts["trim_whitespace"] > 0);
    }

    [Fact]
    public void TrimWhitespace_CleanNameDoesNotFire()
    {
        var data = Run(Row(name: "TESTFAMILY, TESTGIVEN"));
        Assert.Single(data.Patients);
        Assert.Equal(0, data.RuleCounts.GetValueOrDefault("trim_whitespace"));
    }
}
