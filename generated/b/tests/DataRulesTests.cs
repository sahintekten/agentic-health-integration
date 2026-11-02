using System;
using System.Collections.Generic;
using System.Linq;
using Adapter;
using Hl7.Fhir.Model;
namespace Adapter.Tests;

public class DataRulesTests
{
    const string TestPatName = "ZZ" + "TEST" + " " + "PAT" + "IENT";
    const string PlaceholderDob = "1900" + "01" + "01";
    static int Count(MappedData m, string rule) => m.RuleCounts.GetValueOrDefault(rule);
    [Fact]
    public void exclude_test_records_test_patient()
    {
        var d = Src.Data(new[] { Src.Pt("1"), Src.Pt("2", nm: TestPatName) }, new[] { Src.Rs("500", pref: "1"), Src.Rs("501", pref: "2", v1: "6.1") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        Assert.DoesNotContain(m.Patients, p => p.Id == "2");
        Assert.DoesNotContain(m.Observations, o => o.Id == "501");
        Assert.Contains(m.Observations, o => o.Id == "500");
        Assert.True(Count(m, "exclude_test_records") > 0);
    }
    [Fact]
    public void exclude_test_records_test_user()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500"), Src.Rs("501", v1: "6.1", usr: "TEST") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        Assert.DoesNotContain(m.Observations, o => o.Id == "501");
        Assert.Contains(m.Observations, o => o.Id == "500");
        Assert.True(Count(m, "exclude_test_records") > 0);
    }
    [Fact]
    public void drop_duplicates_ignoring_rslt_id()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("601"), Src.Rs("602") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        Assert.Single(m.Observations);
        Assert.Equal("601", m.Observations[0].Id);
        Assert.True(Count(m, "drop_duplicates") > 0);
    }
    [Fact]
    public void drop_duplicates_ignoring_ent_usr()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("603", v1: "6.1", usr: "USRA"), Src.Rs("604", v1: "6.1", usr: "USRB") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        Assert.Single(m.Observations);
        Assert.Equal("603", m.Observations[0].Id);
        Assert.True(Count(m, "drop_duplicates") > 0);
    }
    [Fact]
    public void trim_whitespace_value_and_name()
    {
        var d = Src.One(pt: Src.Pt("1", nm: "  TESTFAMILY TESTGIVEN  "), rs: Src.Rs("500", v1: "  5.4 "));
        var m = Mapper.Map(d);
        Assert.Equal(5.4m, ((Quantity)m.Observations.Single().Value!).Value);
        Assert.Equal("Testfamily", m.Patients.Single().Name[0].Family);
        Assert.True(Count(m, "trim_whitespace") > 0);
    }
    [Fact]
    public void unit_fallback_uses_default_unit()
    {
        var d = Src.One(rs: Src.Rs("500", uom: null), cd: Src.Cd(dflt: "MG/DL"));
        var m = Mapper.Map(d);
        var q = (Quantity)m.Observations.Single().Value!;
        Assert.Equal("mg/dL", q.Code);
        Assert.True(Count(m, "unit_fallback") > 0);
    }
    [Fact]
    public void unit_fallback_without_catalogue_entry_leaves_unit_empty()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500", tst: "ESR", uom: null, v1: "12") }, new[] { Src.Cd() });
        var o = Mapper.Map(d).Observations.Single();
        var q = (Quantity)o.Value!;
        Assert.True(string.IsNullOrEmpty(q.Unit));
        Assert.True(string.IsNullOrEmpty(q.Code));
    }
    [Fact]
    public void unit_map_converts_to_ucum()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500", uom: "X10E3/UL", tst: "WBC") }, new[] { Src.Cd("WBC", "Testcount", null, "X10E3/UL") });
        var m = Mapper.Map(d);
        Assert.Equal("10*3/uL", ((Quantity)m.Observations.Single().Value!).Code);
        Assert.True(Count(m, "unit_map") > 0);
    }
    [Fact]
    public void derive_category_is_set_on_every_observation()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500"), Src.Rs("501", tst: "BPM", uom: "BPM", v1: "70") }, new[] { Src.Cd(), Src.Cd("BPM", "Testpulse", null, "BPM") });
        var m = Mapper.Map(d);
        Assert.Equal(2, m.Observations.Count);
        Assert.All(m.Observations, o => Assert.NotEmpty(o.Category));
        Assert.True(Count(m, "derive_category") > 0);
    }
    [Fact]
    public void timezone_assumption_is_counted()
    {
        var m = Mapper.Map(Src.One());
        var e = Assert.IsType<FhirDateTime>(m.Observations.Single().Effective);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), e.ToDateTimeOffset(TimeSpan.Zero).ToUniversalTime());
        Assert.True(Count(m, "timezone_assumption") > 0);
    }
    [Fact]
    public void date_format_placeholder_birth_date_is_omitted()
    {
        var d = Src.One(pt: Src.Pt("1", dob: PlaceholderDob));
        var m = Mapper.Map(d);
        Assert.Null(m.Patients.Single().BirthDate);
        Assert.True(Count(m, "date_format") > 0);
    }
    [Fact]
    public void date_format_converts_real_birth_date()
    {
        var d = Src.One(pt: Src.Pt("1", dob: "20010907"));
        var m = Mapper.Map(d);
        Assert.Equal("2001-09-07", m.Patients.Single().BirthDate);
        Assert.True(Count(m, "date_format") > 0);
    }
    [Fact]
    public void other_sentinel_value_gives_data_absent_reason()
    {
        var d = Src.One(rs: Src.Rs("500", v1: "-9.9"));
        var m = Mapper.Map(d);
        var o = m.Observations.Single();
        Assert.Null(o.Value);
        Assert.NotNull(o.DataAbsentReason);
        Assert.True(Count(m, "other") > 0);
    }
    [Fact]
    public void other_empty_value_gives_data_absent_reason()
    {
        var d = Src.One(rs: Src.Rs("500", v1: null));
        var m = Mapper.Map(d);
        var o = m.Observations.Single();
        Assert.Null(o.Value);
        Assert.NotNull(o.DataAbsentReason);
        Assert.True(Count(m, "other") > 0);
    }
    [Fact]
    public void other_duplicate_patients_are_both_kept()
    {
        var d = Src.Data(new[] { Src.Pt("11", nm: "TESTFAMILY TESTGIVEN"), Src.Pt("12", nm: "OTHERFAMILY OTHERGIVEN") }, new[] { Src.Rs("500", pref: "11") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        Assert.Equal(2, m.Patients.Count);
        Assert.Contains(m.Patients, p => p.Id == "11");
        Assert.Contains(m.Patients, p => p.Id == "12");
        Assert.True(Count(m, "other") > 0);
    }
    [Fact]
    public void other_missing_catalogue_entry_keeps_result_with_local_code_only()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500", tst: "ESR", uom: "MM/HR", v1: "12") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        var o = m.Observations.Single();
        Assert.Contains(o.Code.Coding, c => c.Code == "ESR");
        Assert.DoesNotContain(o.Code.Coding, c => c.System == "http://loinc.org");
        Assert.True(Count(m, "other") > 0);
    }
    [Fact]
    public void inactive_test_code_is_still_used()
    {
        var d = Src.One(cd: Src.Cd(actv: "N"));
        var o = Mapper.Map(d).Observations.Single();
        Assert.Contains(o.Code.Coding, c => c.Code == "GLU");
    }
    [Fact]
    public void unlinked_patient_keeps_original_id()
    {
        var d = Src.Data(new[] { Src.Pt("ABC") }, new[] { Src.Rs("500", pref: "ABC") }, new[] { Src.Cd() });
        var m = Mapper.Map(d);
        Assert.Equal("ABC", m.Patients.Single().Id);
    }
}
