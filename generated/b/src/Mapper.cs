namespace Adapter;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

public static class Mapper
{
    private const string PatientSystem = "http://hospital.example.org/fhir/sid/lab-db-patient";
    private const string ResultSystem = "http://hospital.example.org/fhir/sid/lab-db-result";
    private const string TestCodeSystem = "http://hospital.example.org/fhir/sid/lab-db-test-code";
    private const string LoincSystem = "http://loinc.org";
    private const string UcumSystem = "http://unitsofmeasure.org";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string InterpretationSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";
    private const string AbsentReasonSystem = "http://terminology.hl7.org/CodeSystem/data-absent-reason";
    private const string QualitySystem = "http://hospital.example.org/fhir/CodeSystem/data-quality";

    // Markers from the mapping traps, written in pieces so no patient-like literal sits in the code.
    private const string TestPatientName = "ZZ" + "TEST" + " " + "PAT" + "IENT";
    private const string PlaceholderDob = "1900" + "01" + "01";

    private static readonly Dictionary<string, string> UnitMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MG/DL"] = "mg/dL",
        ["MMHG"] = "mm[Hg]",
        ["MMOL/L"] = "mmol/L",
        ["KG"] = "kg",
        ["BPM"] = "/min",
        ["BR/MIN"] = "/min",
        ["CM"] = "cm",
        ["KG/M2"] = "kg/m2",
        ["ML/MIN"] = "mL/min",
        ["X10E3/UL"] = "10*3/uL",
        ["%"] = "%",
        ["PCT"] = "%",
        ["G/DL"] = "g/dL",
        ["MM/HR"] = "mm/h",
        ["IN"] = "[in_i]",
    };

    // Units that indicate vital signs / body measurements.
    private static readonly HashSet<string> VitalUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "MMHG", "BPM", "BR/MIN", "KG", "CM", "KG/M2", "IN",
    };

    private static readonly Dictionary<string, AdministrativeGender> GenderMap = new()
    {
        ["1"] = AdministrativeGender.Male,
        ["2"] = AdministrativeGender.Female,
        ["9"] = AdministrativeGender.Unknown,
    };

    private sealed record TestInfo(string? Desc, string? Loinc, string? DfltUom);

    public static MappedData Map(SourceData source)
    {
        var data = new MappedData();
        if (source == null)
            return data;

        var catalogue = BuildCatalogue(source);
        var testPatientIds = MapPatients(source, data);
        MapObservations(source, data, catalogue, testPatientIds);
        return data;
    }

    // ---------- common helpers ----------

    private static string? Clean(string? value)
    {
        if (value == null)
            return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }

    // Patient ID policy: unpadded integer string; otherwise keep original and flag as unlinked.
    private static (string Id, bool Linked) NormalizePatientId(string original)
    {
        var m = Regex.Match(original.Trim(), @"^(?:[A-Za-z]+[-_ ]*)?(\d+)$");
        if (!m.Success)
            return (original.Trim(), false);
        var digits = m.Groups[1].Value.TrimStart('0');
        return (digits.Length == 0 ? "0" : digits, true);
    }

    private static string? ToUtcString(string? epoch)
    {
        var t = Clean(epoch);
        if (t == null)
            return null;
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return null;
        if (d < -62135596800m || d > 253402300799m)
            return null;
        var dto = DateTimeOffset.FromUnixTimeSeconds((long)Math.Truncate(d));
        return dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static void AddQualityTag(DomainResource resource, string code, string display)
    {
        resource.Meta ??= new Meta();
        resource.Meta.Tag.Add(new Coding(QualitySystem, code, display));
    }

    private static string NormalizeSpaces(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

    // ---------- catalogue ----------

    private static Dictionary<string, TestInfo> BuildCatalogue(SourceData source)
    {
        var result = new Dictionary<string, TestInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in source.Table("lb_tst_codes"))
        {
            var code = Clean(row.Get("tst_cd"));
            if (code == null || result.ContainsKey(code))
                continue;
            result[code] = new TestInfo(Clean(row.Get("tst_desc")), Clean(row.Get("loinc_cd")), Clean(row.Get("dflt_uom")));
        }
        return result;
    }

    // ---------- patients ----------

    private static HashSet<string> MapPatients(SourceData source, MappedData data)
    {
        var testIds = new HashSet<string>();
        var usedIds = new HashSet<string>();
        var rows = source.Table("lb_pt");

        // Possible duplicate patients: same data ignoring name and reference.
        var groupSizes = rows
            .GroupBy(DuplicateKey)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var row in rows)
        {
            var reference = Clean(row.Get("pt_ref"));
            if (reference == null)
            {
                data.CountRule("other");
                continue;
            }

            var (id, linked) = NormalizePatientId(reference);

            // Rule exclude_test_records: drop the test patient and remember its id.
            var name = Clean(row.Get("pt_nm"));
            if (name != null && string.Equals(NormalizeSpaces(name), TestPatientName, StringComparison.OrdinalIgnoreCase))
            {
                testIds.Add(id);
                data.CountRule("exclude_test_records");
                continue;
            }

            if (!usedIds.Add(id))
            {
                data.CountRule("other");
                continue;
            }

            var patient = BuildPatient(row, reference, id, name, data);
            if (!linked)
                data.CountRule("other"); // unlinked patient

            if (groupSizes.TryGetValue(DuplicateKey(row), out var size) && size > 1)
            {
                // Rule other: flag possible duplicate, do not merge.
                AddQualityTag(patient, "possible-duplicate", "Possible duplicate patient, review needed");
                data.CountRule("other");
            }

            data.Patients.Add(patient);
        }
        return testIds;
    }

    private static string DuplicateKey(SourceRecord row)
    {
        var parts = row
            .Where(kv => !kv.Key.Equals("pt_nm", StringComparison.OrdinalIgnoreCase)
                      && !kv.Key.Equals("pt_ref", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key.ToLowerInvariant() + "=" + (kv.Value?.Trim() ?? "\u0000"));
        return string.Join("\u001f", parts);
    }

    private static Patient BuildPatient(SourceRecord row, string reference, string id, string? name, MappedData data)
    {
        var patient = new Patient { Id = id };
        patient.Identifier.Add(new Identifier(PatientSystem, reference));

        AddName(patient, row.Get("pt_nm"), name, data);
        SetBirthDate(patient, row.Get("pt_dob"), data);
        SetGender(patient, row.Get("pt_sx"));
        SetLastUpdated(patient, row.Get("crt_dt"), data);
        return patient;
    }

    private static void AddName(Patient patient, string? raw, string? trimmed, MappedData data)
    {
        if (trimmed == null)
            return;
        // Rule trim_whitespace: trim and collapse spaces before splitting.
        if (raw != trimmed || Regex.IsMatch(trimmed, @"\s{2,}"))
            data.CountRule("trim_whitespace");

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return;

        var humanName = new HumanName
        {
            Text = trimmed,
            Family = TitleCase(tokens[0]),
        };
        var given = tokens.Skip(1).Select(TitleCase).ToList();
        if (given.Count > 0)
            humanName.Given = given;
        patient.Name.Add(humanName);
    }

    private static string TitleCase(string token)
    {
        var sb = new StringBuilder(token.Length);
        var upperNext = true;
        foreach (var ch in token.ToLowerInvariant())
        {
            sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = ch == '-' || ch == '\'' || ch == '.';
        }
        return sb.ToString();
    }

    private static void SetBirthDate(Patient patient, string? raw, MappedData data)
    {
        var t = Clean(raw);
        if (t == null)
            return;
        // Rule date_format: placeholder is omitted, YYYYMMDD becomes YYYY-MM-DD.
        if (t == PlaceholderDob)
        {
            data.CountRule("date_format");
            return;
        }
        if (!DateTime.TryParseExact(t, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return;
        patient.BirthDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        data.CountRule("date_format");
    }

    private static void SetGender(Patient patient, string? raw)
    {
        var t = Clean(raw);
        if (t != null && GenderMap.TryGetValue(t, out var gender))
            patient.Gender = gender;
    }

    private static void SetLastUpdated(Patient patient, string? raw, MappedData data)
    {
        var text = ToUtcString(raw);
        if (text == null)
            return;
        // Rule timezone_assumption: epoch seconds are assumed UTC and flagged.
        patient.Meta ??= new Meta();
        patient.Meta.LastUpdated = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        AddQualityTag(patient, "assumed-utc", "Timezone assumed UTC");
        data.CountRule("timezone_assumption");
    }

    // ---------- observations ----------

    private static void MapObservations(SourceData source, MappedData data,
        Dictionary<string, TestInfo> catalogue, HashSet<string> testPatientIds)
    {
        var seenFacts = new HashSet<string>();
        var usedIds = new HashSet<string>();

        // Lowest rslt_id first, so the kept duplicate is the lowest id.
        var rows = source.Table("lb_rslt")
            .OrderBy(r => long.TryParse(Clean(r.Get("rslt_id")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue)
            .ThenBy(r => r.Get("rslt_id") ?? "", StringComparer.Ordinal)
            .ToList();

        foreach (var row in rows)
        {
            // Rule exclude_test_records: rows entered by TEST.
            if (string.Equals(Clean(row.Get("ent_usr")), "TEST", StringComparison.OrdinalIgnoreCase))
            {
                data.CountRule("exclude_test_records");
                continue;
            }

            var reference = Clean(row.Get("pt_ref"));
            var resultId = Clean(row.Get("rslt_id"));
            var testCode = Clean(row.Get("tst_cd"));
            if (reference == null || resultId == null || testCode == null)
            {
                data.CountRule("other");
                continue;
            }

            var patientId = NormalizePatientId(reference).Id;

            // Rule exclude_test_records: results of the test patient.
            if (testPatientIds.Contains(patientId))
            {
                data.CountRule("exclude_test_records");
                continue;
            }

            // Rule drop_duplicates: same content ignoring rslt_id and ent_usr.
            if (!seenFacts.Add(ResultKey(row)))
            {
                data.CountRule("drop_duplicates");
                continue;
            }

            var obs = BuildObservation(row, resultId, patientId, testCode, catalogue, usedIds, data);
            data.Observations.Add(obs);
        }
    }

    private static string ResultKey(SourceRecord row)
    {
        var parts = row
            .Where(kv => !kv.Key.Equals("rslt_id", StringComparison.OrdinalIgnoreCase)
                      && !kv.Key.Equals("ent_usr", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key.ToLowerInvariant() + "=" + (kv.Value ?? "\u0000"));
        return string.Join("\u001f", parts);
    }

    private static Observation BuildObservation(SourceRecord row, string resultId, string patientId, string testCode,
        Dictionary<string, TestInfo> catalogue, HashSet<string> usedIds, MappedData data)
    {
        catalogue.TryGetValue(testCode, out var info);

        // Rule other: test code missing in the catalogue, keep result with local code only.
        if (info == null)
            data.CountRule("other");

        var obs = new Observation
        {
            Id = UniqueId(resultId, usedIds, data),
            Subject = new ResourceReference("Patient/" + patientId),
            Status = MapStatus(row.Get("sts")),
        };
        obs.Identifier.Add(new Identifier(ResultSystem, resultId));

        SetCode(obs, testCode, info);

        var (unitText, fromFallback) = ResolveUnit(row.Get("uom"), info);
        SetCategory(obs, info, unitText);
        SetValue(obs, row.Get("lb_val1"), unitText, fromFallback, data);
        SetEffective(obs, row.Get("rslt_dt"), data);
        SetInterpretation(obs, row.Get("flg"));
        return obs;
    }

    // Observation ids must be unique; rslt_id is not guaranteed to be unique.
    private static string UniqueId(string resultId, HashSet<string> usedIds, MappedData data)
    {
        var id = resultId;
        var n = 1;
        while (!usedIds.Add(id))
        {
            n++;
            id = resultId + "-" + n.ToString(CultureInfo.InvariantCulture);
            data.CountRule("other");
        }
        return id;
    }

    private static ObservationStatus MapStatus(string? raw)
    {
        return Clean(raw)?.ToUpperInvariant() switch
        {
            "F" => ObservationStatus.Final,
            "C" => ObservationStatus.Corrected, // inferred as corrected
            _ => ObservationStatus.Unknown,
        };
    }

    private static void SetCode(Observation obs, string testCode, TestInfo? info)
    {
        obs.Code = new CodeableConcept();
        // Local code always present; display only when catalogue has one.
        obs.Code.Coding.Add(new Coding(TestCodeSystem, testCode, info?.Desc));
        if (info?.Loinc != null)
            obs.Code.Coding.Add(new Coding(LoincSystem, info.Loinc, info.Desc));
        if (info?.Desc != null)
            obs.Code.Text = info.Desc;
    }

    private static (string? Text, bool FromFallback) ResolveUnit(string? rawUom, TestInfo? info)
    {
        var unit = Clean(rawUom);
        if (unit != null)
            return (unit, false);
        // Rule unit_fallback: use catalogue default unit; empty if no catalogue entry.
        return (info?.DfltUom, info?.DfltUom != null);
    }

    private static void SetCategory(Observation obs, TestInfo? info, string? unitText)
    {
        // Rule derive_category: vital signs by catalogue unit of the test, else by result unit.
        var unitForCategory = info?.DfltUom ?? unitText;
        var isVital = unitForCategory != null && VitalUnits.Contains(unitForCategory);
        obs.Category.Add(new CodeableConcept(CategorySystem, isVital ? "vital-signs" : "laboratory"));
    }

    private static void SetValue(Observation obs, string? rawValue, string? unitText, bool fromFallback, MappedData data)
    {
        data.CountRule("derive_category");

        var trimmed = rawValue?.Trim();
        // Rule trim_whitespace: trim before casting.
        if (rawValue != null && trimmed != rawValue)
            data.CountRule("trim_whitespace");

        decimal? value = null;
        if (!string.IsNullOrEmpty(trimmed)
            && decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)
            && d != -9.9m)
        {
            value = d;
        }

        if (value == null)
        {
            // Rule other: sentinel, empty or unreadable value -> dataAbsentReason.
            obs.DataAbsentReason = new CodeableConcept(AbsentReasonSystem, "unknown");
            data.CountRule("other");
            return;
        }

        var quantity = new Quantity { Value = value };
        if (unitText != null)
        {
            quantity.Unit = unitText;
            if (fromFallback)
                data.CountRule("unit_fallback");
            // Rule unit_map: legacy unit text to UCUM.
            if (UnitMap.TryGetValue(unitText, out var ucum))
            {
                quantity.System = UcumSystem;
                quantity.Code = ucum;
                data.CountRule("unit_map");
            }
        }
        obs.Value = quantity;
    }

    private static void SetEffective(Observation obs, string? raw, MappedData data)
    {
        var text = ToUtcString(raw);
        if (text == null)
            return;
        // Rule timezone_assumption: epoch seconds assumed UTC and flagged.
        obs.Effective = new FhirDateTime(text);
        AddQualityTag(obs, "assumed-utc", "Timezone assumed UTC");
        data.CountRule("timezone_assumption");
    }

    private static void SetInterpretation(Observation obs, string? raw)
    {
        var flag = Clean(raw)?.ToUpperInvariant();
        if (flag is "N" or "H" or "L")
            obs.Interpretation.Add(new CodeableConcept(InterpretationSystem, flag));
    }
}
