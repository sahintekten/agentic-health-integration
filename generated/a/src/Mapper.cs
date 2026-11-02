namespace Adapter;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

public static class Mapper
{
    private const string MrnSystem = "http://hospital.example.org/fhir/sid/prs-mrn";
    private const string MaritalSystem = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus";
    private const string IdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex DigitsOnly = new(@"^\d+$", RegexOptions.Compiled);

    private static readonly Dictionary<string, AdministrativeGender> GenderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["M"] = AdministrativeGender.Male,
            ["F"] = AdministrativeGender.Female,
            ["U"] = AdministrativeGender.Unknown,
        };

    // Meanings are assumed (see mapping.yaml) and need confirmation.
    private static readonly Dictionary<string, string> MaritalMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["M"] = "M",
            ["D"] = "D",
            ["S"] = "S",
            ["W"] = "W",
        };

    private static readonly Dictionary<string, string> MaritalDisplay =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["M"] = "Married",
            ["D"] = "Divorced",
            ["S"] = "Never Married",
            ["W"] = "Widowed",
        };

    public static MappedData Map(SourceData source)
    {
        var data = new MappedData();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in source.Table("patients"))
        {
            try
            {
                var patient = MapPatient(row, data);
                if (patient == null) continue;

                // The same hospital number must not produce two Patient resources.
                if (!seenIds.Add(patient.Id))
                {
                    data.CountRule("duplicate_patient_skipped");
                    continue;
                }
                data.Patients.Add(patient);
            }
            catch (Exception)
            {
                // Never throw on bad data: skip the record and count it.
                data.CountRule("record_skipped_error");
            }
        }

        return data;
    }

    // ---------- Patient ----------

    private static Patient? MapPatient(SourceRecord row, MappedData data)
    {
        var rawId = Clean(row.Get("pt_id"));
        if (rawId == null)
        {
            data.CountRule("record_skipped_missing_id");
            return null;
        }

        var patient = new Patient { Id = ToHospitalNumber(rawId, data) };
        AddIdentifier(patient, rawId);
        AddName(patient, row.Get("nm_full"), data);
        AddBirthDate(patient, row.Get("dob"), data);
        AddGender(patient, row.Get("sx"));
        AddAddress(patient, row, data);
        AddMaritalStatus(patient, row.Get("mrtl"));
        AddDeceased(patient, row.Get("dcsd"), data);
        return patient;
    }

    // Rule normalise_identifier: prefixed, zero-padded id -> integer string without leading zeros.
    private static string ToHospitalNumber(string rawId, MappedData data)
    {
        var digits = rawId;
        if (digits.StartsWith("MRN-", StringComparison.OrdinalIgnoreCase))
            digits = digits.Substring(4).Trim();

        if (DigitsOnly.IsMatch(digits) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var id = number.ToString(CultureInfo.InvariantCulture);
            if (id != rawId) data.CountRule("normalise_identifier");
            return id;
        }

        // Not convertible: keep the record, use the original id, count as unlinked.
        data.CountRule("unlinked_patient");
        return rawId;
    }

    private static void AddIdentifier(Patient patient, string rawId)
    {
        patient.Identifier.Add(new Identifier(MrnSystem, rawId)
        {
            Type = new CodeableConcept(IdentifierTypeSystem, "MR"),
        });
    }

    private static void AddName(Patient patient, string? rawName, MappedData data)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return;

        // Rule trim_whitespace: trim and collapse repeated spaces before splitting.
        var name = Spaces.Replace(rawName.Trim(), " ");
        if (name != rawName) data.CountRule("trim_whitespace");

        var comma = name.IndexOf(',');
        var familyPart = comma >= 0 ? name.Substring(0, comma).Trim() : name;
        var givenPart = comma >= 0 ? name.Substring(comma + 1).Trim() : "";

        var humanName = new HumanName();
        if (familyPart.Length > 0) humanName.Family = TitleCase(familyPart);

        var givens = givenPart
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(TitleCase)
            .ToList();
        if (givens.Count > 0) humanName.Given = givens;

        if (humanName.Family != null || givens.Count > 0)
            patient.Name.Add(humanName);
    }

    // Rule date_format: source is DD/MM/YYYY (documented wrongly as MM/DD/YYYY).
    private static void AddBirthDate(Patient patient, string? raw, MappedData data)
    {
        var text = Clean(raw);
        if (text == null) return;

        if (DateTime.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            patient.BirthDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            data.CountRule("date_format");
        }
        else
        {
            data.CountRule("invalid_dob_skipped");
        }
    }

    private static void AddGender(Patient patient, string? raw)
    {
        var code = Clean(raw);
        if (code != null && GenderMap.TryGetValue(code, out var gender))
            patient.Gender = gender; // U maps to unknown, not other
    }

    private static void AddAddress(Patient patient, SourceRecord row, MappedData data)
    {
        var line = Clean(row.Get("addr_line"));
        var city = Clean(row.Get("town"));
        var postal = FixPostalCode(Clean(row.Get("post_cd")), data);

        if (line == null && city == null && postal == null) return;

        var address = new Address();
        if (line != null) address.Line = new[] { line };
        if (city != null) address.City = city;
        if (postal != null) address.PostalCode = postal;
        patient.Address.Add(address);
    }

    // Rule restore_leading_zeros: pad short numeric ZIP codes to 5 digits (US assumed).
    private static string? FixPostalCode(string? postal, MappedData data)
    {
        if (postal == null) return null;
        if (DigitsOnly.IsMatch(postal) && postal.Length < 5)
        {
            data.CountRule("restore_leading_zeros");
            return postal.PadLeft(5, '0');
        }
        return postal;
    }

    private static void AddMaritalStatus(Patient patient, string? raw)
    {
        var code = Clean(raw);
        if (code == null || !MaritalMap.TryGetValue(code, out var mapped)) return;

        var coding = new Coding(MaritalSystem, mapped, MaritalDisplay[code]);
        patient.MaritalStatus = new CodeableConcept { Coding = new List<Coding> { coding } };
    }

    // dcsd meaning (date of death) is inferred and needs confirmation.
    // Null dcsd leaves deceased absent (never deceasedBoolean = false).
    private static void AddDeceased(Patient patient, string? raw, MappedData data)
    {
        var text = Clean(raw);
        if (text == null) return;

        if (IsoDate.IsMatch(text) &&
            DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            patient.Deceased = new FhirDateTime(text);
        }
        else
        {
            data.CountRule("invalid_dcsd_skipped");
        }
    }

    // ---------- Helpers ----------

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    // Upper case -> Title Case; also capitalises after hyphens and apostrophes.
    private static string TitleCase(string word)
    {
        var chars = word.ToLowerInvariant().ToCharArray();
        var capitalise = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (capitalise && char.IsLetter(chars[i]))
                chars[i] = char.ToUpperInvariant(chars[i]);
            capitalise = chars[i] == '-' || chars[i] == '\'' || chars[i] == ' ';
        }
        return new string(chars);
    }
}
