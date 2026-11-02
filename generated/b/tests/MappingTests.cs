using System;
using System.Collections.Generic;
using System.Linq;
using Adapter;
using Hl7.Fhir.Model;
namespace Adapter.Tests;

public static class Src
{
    public static SourceRecord Pt(string pref, string? nm = "TESTFAMILY TESTGIVEN", string? dob = "20010315", string? sx = "1", string? crt = "978307200")
    {
        var r = new SourceRecord();
        r["pt_ref"] = pref;
        r["pt_nm"] = nm;
        r["pt_dob"] = dob;
        r["pt_sx"] = sx;
        r["crt_dt"] = crt;
        return r;
    }
    public static SourceRecord Rs(string id, string pref = "1", string tst = "GLU", string? v1 = "5.4", string? uom = "MG/DL", string? dt = "978307200", string? flg = "N", string? sts = "F", string? usr = "USR1")
    {
        var r = new SourceRecord();
        r["rslt_id"] = id;
        r["pt_ref"] = pref;
        r["tst_cd"] = tst;
        r["lb_val1"] = v1;
        r["lb_val2"] = null;
        r["uom"] = uom;
        r["rslt_dt"] = dt;
        r["flg"] = flg;
        r["sts"] = sts;
        r["ent_usr"] = usr;
        r["crt_dt"] = "978307200";
        return r;
    }
    public static SourceRecord Cd(string tst = "GLU", string? desc = "Testglucose", string? loinc = "2339-0", string? dflt = "MG/DL", string actv = "Y")
    {
        var r = new SourceRecord();
        r["tst_cd"] = tst;
        r["tst_desc"] = desc;
        r["loinc_cd"] = loinc;
        r["dflt_uom"] = dflt;
        r["actv_flg"] = actv;
        return r;
    }
    public static SourceData Data(IEnumerable<SourceRecord> pts, IEnumerable<SourceRecord> rs, IEnumerable<SourceRecord> cds)
    {
        var d = new SourceData();
        d.Tables["lb_pt"] = pts.ToList();
        d.Tables["lb_rslt"] = rs.ToList();
        d.Tables["lb_tst_codes"] = cds.ToList();
        return d;
    }
    public static SourceData One(SourceRecord? pt = null, SourceRecord? rs = null, SourceRecord? cd = null) => Data(new[] { pt ?? Pt("1") }, new[] { rs ?? Rs("500") }, new[] { cd ?? Cd() });
}
public class MappingTests
{
    const string TestCodeSys = "http://hospital.example.org/fhir/sid/lab-db-test-code";
    static Observation Obs(SourceData d, string id) => Mapper.Map(d).Observations.Single(o => o.Id == id);
    static Patient Pat(SourceData d, string id) => Mapper.Map(d).Patients.Single(p => p.Id == id);
    static DateTimeOffset Jan2001 => new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    [Fact]
    public void pt_ref_to_Patient_id()
    {
        var d = Src.One(pt: Src.Pt("123"), rs: Src.Rs("500", pref: "123"));
        Assert.Equal("123", Pat(d, "123").Id);
    }
    [Fact]
    public void pt_ref_to_Patient_identifier()
    {
        var d = Src.One(pt: Src.Pt("123"), rs: Src.Rs("500", pref: "123"));
        var p = Pat(d, "123");
        Assert.Equal("123", p.Identifier[0].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/lab-db-patient", p.Identifier[0].System);
    }
    [Fact]
    public void pt_nm_to_family()
    {
        var d = Src.One(pt: Src.Pt("1", nm: "  TESTFAMILY TESTGIVEN "));
        Assert.Equal("Testfamily", Pat(d, "1").Name[0].Family);
    }
    [Fact]
    public void pt_nm_to_given()
    {
        var d = Src.One(pt: Src.Pt("1", nm: "  TESTFAMILY TESTGIVEN "));
        Assert.Equal("Testgiven", Pat(d, "1").Name[0].Given.First());
    }
    [Fact]
    public void pt_nm_to_text()
    {
        var d = Src.One(pt: Src.Pt("1", nm: "  TESTFAMILY TESTGIVEN "));
        Assert.Equal("TESTFAMILY TESTGIVEN", Pat(d, "1").Name[0].Text);
    }
    [Fact]
    public void pt_dob_to_birthDate()
    {
        var d = Src.One(pt: Src.Pt("1", dob: "20010315"));
        Assert.Equal("2001-03-15", Pat(d, "1").BirthDate);
    }
    [Fact]
    public void pt_sx_to_gender()
    {
        var d = Src.Data(new[] { Src.Pt("1", sx: "1"), Src.Pt("2", sx: "2"), Src.Pt("3", sx: "9") }, new[] { Src.Rs("500") }, new[] { Src.Cd() });
        Assert.Equal(AdministrativeGender.Male, Pat(d, "1").Gender);
        Assert.Equal(AdministrativeGender.Female, Pat(d, "2").Gender);
        Assert.Equal(AdministrativeGender.Unknown, Pat(d, "3").Gender);
    }
    [Fact]
    public void crt_dt_to_meta_lastUpdated()
    {
        var d = Src.One(pt: Src.Pt("1", crt: "978307200"));
        var lu = Pat(d, "1").Meta.LastUpdated;
        Assert.NotNull(lu);
        Assert.Equal(Jan2001, lu!.Value.ToUniversalTime());
    }
    [Fact]
    public void rslt_id_to_Observation_id()
    {
        Assert.Equal("500", Obs(Src.One(), "500").Id);
    }
    [Fact]
    public void rslt_id_to_Observation_identifier()
    {
        var o = Obs(Src.One(), "500");
        Assert.Equal("500", o.Identifier[0].Value);
        Assert.Equal("http://hospital.example.org/fhir/sid/lab-db-result", o.Identifier[0].System);
    }
    [Fact]
    public void pt_ref_to_Observation_subject()
    {
        var d = Src.One(pt: Src.Pt("123"), rs: Src.Rs("500", pref: "123"));
        Assert.Equal("Patient/123", Obs(d, "500").Subject.Reference);
    }
    [Fact]
    public void tst_cd_to_code_coding()
    {
        var o = Obs(Src.One(), "500");
        var c = o.Code.Coding.Single(x => x.System == TestCodeSys);
        Assert.Equal("GLU", c.Code);
        Assert.Equal("Testglucose", c.Display);
    }
    [Fact]
    public void tst_cd_to_category()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500", tst: "GLU"), Src.Rs("501", tst: "BPM", uom: "BPM", v1: "70") }, new[] { Src.Cd(), Src.Cd("BPM", "Testpulse", null, "BPM") });
        var lab = Obs(d, "500").Category[0].Coding[0];
        Assert.Equal("laboratory", lab.Code);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/observation-category", lab.System);
        Assert.Equal("vital-signs", Obs(d, "501").Category[0].Coding[0].Code);
    }
    [Fact]
    public void lb_val1_to_valueQuantity_value()
    {
        var d = Src.One(rs: Src.Rs("500", v1: "  5.4  "));
        var q = Assert.IsType<Quantity>(Obs(d, "500").Value);
        Assert.Equal(5.4m, q.Value);
    }
    [Fact]
    public void lb_val1_sentinel_gives_dataAbsentReason()
    {
        var d = Src.One(rs: Src.Rs("500", v1: "-9.9"));
        var o = Obs(d, "500");
        Assert.Null(o.Value);
        Assert.NotNull(o.DataAbsentReason);
    }
    [Fact]
    public void uom_to_valueQuantity_unit()
    {
        var d = Src.One(rs: Src.Rs("500", uom: "MG/DL"));
        Assert.Equal("MG/DL", ((Quantity)Obs(d, "500").Value!).Unit);
    }
    [Fact]
    public void uom_null_uses_default_unit()
    {
        var d = Src.One(rs: Src.Rs("500", uom: null), cd: Src.Cd(dflt: "G/DL"));
        Assert.Equal("G/DL", ((Quantity)Obs(d, "500").Value!).Unit);
    }
    [Fact]
    public void uom_to_valueQuantity_code()
    {
        var d = Src.One(rs: Src.Rs("500", uom: "MG/DL"));
        var q = (Quantity)Obs(d, "500").Value!;
        Assert.Equal("mg/dL", q.Code);
        Assert.Equal("http://unitsofmeasure.org", q.System);
    }
    [Fact]
    public void rslt_dt_to_effectiveDateTime()
    {
        var d = Src.One(rs: Src.Rs("500", dt: "978307200"));
        var e = Assert.IsType<FhirDateTime>(Obs(d, "500").Effective);
        Assert.Equal(Jan2001, e.ToDateTimeOffset(TimeSpan.Zero).ToUniversalTime());
    }
    [Fact]
    public void flg_to_interpretation()
    {
        var d = Src.One(rs: Src.Rs("500", flg: "H"));
        Assert.Equal("H", Obs(d, "500").Interpretation[0].Coding[0].Code);
    }
    [Fact]
    public void flg_null_gives_no_interpretation()
    {
        var d = Src.One(rs: Src.Rs("500", flg: null));
        Assert.Empty(Obs(d, "500").Interpretation);
    }
    [Fact]
    public void sts_to_status()
    {
        var d = Src.Data(new[] { Src.Pt("1") }, new[] { Src.Rs("500", sts: "F"), Src.Rs("501", sts: "C", v1: "6.2") }, new[] { Src.Cd() });
        Assert.Equal(ObservationStatus.Final, Obs(d, "500").Status);
        Assert.Equal(ObservationStatus.Corrected, Obs(d, "501").Status);
    }
}
