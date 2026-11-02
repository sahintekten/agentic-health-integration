namespace Adapter;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

public static class Mapper
{
    private const string PatnoSystem = "http://hospital.example.org/fhir/sid/appointments-patno";
    private const string ApptSystem = "http://hospital.example.org/fhir/sid/appointments-apptref";
    private const string HospitalNumberSystem = "http://hospital.example.org/fhir/sid/hospital-number";
    private const string ClinicSystem = "http://hospital.example.org/fhir/CodeSystem/clinic";
    private const string ActCodeSystem = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    private const string BookedUrl = "http://hospital.example.org/fhir/StructureDefinition/booked-date";
    private const string UcumSystem = "http://unitsofmeasure.org";
    private const int DurationOutlierMinutes = 240;

    private static readonly Regex PatNoPattern =
        new(@"^[A-Za-z]/(\d{7})-[A-Za-z]$", RegexOptions.Compiled);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static MappedData Map(SourceData source)
    {
        var data = new MappedData();
        try
        {
            // Oldest file first; stable sort keeps the reader order inside a file.
            var rows = source.Table("rows")
                .OrderBy(r => r.Get("_file") ?? "", StringComparer.Ordinal)
                .ToList();

            MapPatients(rows, data);
            MapEncounters(rows, data);
        }
        catch (Exception)
        {
            // Never throw on bad data; whatever was mapped so far is returned.
            data.CountRule("other");
        }
        return data;
    }

    // ---------- Patients ----------

    private static void MapPatients(List<SourceRecord> rows, MappedData data)
    {
        // Latest row per patient wins (names differ between files).
        var latest = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var row in rows)
        {
            var patNo = Clean(row.Get("PatNo"));
            if (patNo == null)
                continue;
            if (!latest.ContainsKey(patNo))
                order.Add(patNo);
            latest[patNo] = row;
        }

        foreach (var patNo in order)
        {
            var patient = BuildPatient(patNo, latest[patNo], data);
            if (patient != null)
                data.Patients.Add(patient);
        }
    }

    private static Patient? BuildPatient(string patNo, SourceRecord row, MappedData data)
    {
        var linked = TryHospitalNumber(patNo, out var id);
        var patient = new Patient { Id = id };

        patient.Identifier.Add(new Identifier(PatnoSystem, patNo));
        if (linked)
        {
            // Rule normalise_identifier: prefix, check letter and leading zeros removed.
            patient.Identifier.Add(new Identifier(HospitalNumberSystem, id));
            data.CountRule("normalise_identifier");
        }
        else
        {
            // Unlinked patient: original id kept as Patient.id.
            data.CountRule("other");
        }

        var name = BuildName(Clean(row.Get("PatName")));
        if (name != null)
            patient.Name.Add(name);

        patient.Gender = MapGender(Clean(row.Get("Sex")));

        var birth = ParseDate(Clean(row.Get("Geb.Dat")));
        if (birth != null)
        {
            patient.BirthDate = birth.Value.ToString("yyyy-MM-dd", Inv); // Rule date_format
            data.CountRule("date_format");
        }

        return patient;
    }

    private static bool TryHospitalNumber(string patNo, out string id)
    {
        var m = PatNoPattern.Match(patNo);
        if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, Inv, out var number))
        {
            id = number.ToString(Inv);
            return true;
        }
        id = patNo;
        return false;
    }

    private static AdministrativeGender MapGender(string? code)
    {
        return code?.ToLowerInvariant() switch
        {
            "m" => AdministrativeGender.Male,
            "w" => AdministrativeGender.Female,
            _ => AdministrativeGender.Unknown, // null or unexpected code
        };
    }

    // "Given [Middle] FAMILY": trailing upper-case tokens are the family name.
    private static HumanName? BuildName(string? raw)
    {
        if (raw == null)
            return null;
        var name = new HumanName { Text = raw };
        var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var familyStart = tokens.Length;
        while (familyStart > 0 && IsUpperToken(tokens[familyStart - 1]))
            familyStart--;
        if (familyStart == 0 && tokens.Length > 1)
            familyStart = tokens.Length - 1;
        if (familyStart >= tokens.Length)
            return name; // no recognisable surname: keep text only

        name.Family = string.Join(" ", tokens.Skip(familyStart).Select(NormalCase));
        var given = tokens.Take(familyStart).ToList();
        if (given.Count > 0)
            name.Given = given;
        return name;
    }

    private static bool IsUpperToken(string token) =>
        token.Any(char.IsLetter) && token == token.ToUpperInvariant() && token != token.ToLowerInvariant();

    private static string NormalCase(string token)
    {
        var chars = token.ToLowerInvariant().ToCharArray();
        var upperNext = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (upperNext && char.IsLetter(chars[i]))
                chars[i] = char.ToUpperInvariant(chars[i]);
            upperNext = chars[i] == '-' || chars[i] == '\'';
        }
        return new string(chars);
    }

    // ---------- Encounters ----------

    private static void MapEncounters(List<SourceRecord> rows, MappedData data)
    {
        var latest = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var row in rows)
        {
            var apptRef = Clean(row.Get("ApptRef"));
            if (apptRef == null)
            {
                data.CountRule("other"); // record without key skipped
                continue;
            }

            if (latest.TryGetValue(apptRef, out var previous))
            {
                // Rule drop_duplicates: earlier snapshot discarded, latest file wins.
                data.CountRule("drop_duplicates");
                if (!string.Equals(Clean(previous.Get("Status")), Clean(row.Get("Status")), StringComparison.OrdinalIgnoreCase))
                    data.CountRule("latest_event_wins");
            }
            else
            {
                order.Add(apptRef);
            }
            latest[apptRef] = row;
        }

        foreach (var apptRef in order)
        {
            var encounter = BuildEncounter(apptRef, latest[apptRef], data);
            if (encounter != null)
                data.Encounters.Add(encounter);
        }
    }

    private static Encounter? BuildEncounter(string apptRef, SourceRecord row, MappedData data)
    {
        var patNo = Clean(row.Get("PatNo"));
        if (patNo == null)
        {
            data.CountRule("other"); // no patient: record skipped
            return null;
        }
        TryHospitalNumber(patNo, out var patientId);

        var encounter = new Encounter
        {
            Id = apptRef,
            Status = MapStatus(Clean(row.Get("Status")), data),
            Class = new Coding(ActCodeSystem, "AMB", "ambulatory"), // all clinics are outpatient
            Subject = new ResourceReference("Patient/" + patientId),
        };
        encounter.Identifier.Add(new Identifier(ApptSystem, apptRef));

        AddPeriodAndLength(encounter, row, data);
        AddClinic(encounter, Clean(row.Get("Clinic")));

        var apptType = Clean(row.Get("ApptType"));
        if (apptType != null)
            encounter.Type.Add(new CodeableConcept { Text = apptType }); // text only, no code

        AddBooked(encounter, Clean(row.Get("Booked")));
        return encounter;
    }

    // Statuses are inferred: BK booked, DN done, NS no-show, CX cancelled.
    private static Encounter.EncounterStatus MapStatus(string? code, MappedData data)
    {
        switch (code?.ToUpperInvariant())
        {
            case "BK":
                return Encounter.EncounterStatus.Planned;
            case "DN":
                return Encounter.EncounterStatus.Finished;
            case "NS":
            case "CX":
                return Encounter.EncounterStatus.Cancelled;
            default:
                data.CountRule("other");
                return Encounter.EncounterStatus.Unknown;
        }
    }

    private static void AddPeriodAndLength(Encounter encounter, SourceRecord row, MappedData data)
    {
        var startText = Clean(row.Get("ApptStart"));
        DateTime? start = null;
        if (startText != null &&
            DateTime.TryParseExact(startText, "dd.MM.yyyy HH:mm", Inv, DateTimeStyles.None, out var parsed))
        {
            start = parsed;
        }

        int? minutes = null;
        var durText = Clean(row.Get("DurMin"));
        if (durText != null && int.TryParse(durText, NumberStyles.None, Inv, out var dur))
        {
            minutes = dur;
            // Rule other: outliers are kept but flagged for review.
            if (dur > DurationOutlierMinutes)
                data.CountRule("other");
        }

        if (start != null)
        {
            // Rules date_format + timezone_assumption: day-first parse, no zone given, UTC assumed.
            encounter.Period = new Period { Start = FormatUtc(start.Value) };
            data.CountRule("date_format");
            data.CountRule("timezone_assumption");
            if (minutes != null)
                encounter.Period.End = FormatUtc(start.Value.AddMinutes(minutes.Value));
        }

        if (minutes != null)
        {
            encounter.Length = new Duration
            {
                Value = minutes.Value,
                Unit = "min",
                System = UcumSystem,
                Code = "min",
            };
        }
    }

    private static void AddClinic(Encounter encounter, string? clinic)
    {
        if (clinic == null)
            return;
        var serviceType = new CodeableConcept { Text = clinic };
        serviceType.Coding.Add(new Coding(ClinicSystem, clinic));
        encounter.ServiceType = serviceType;
    }

    private static void AddBooked(Encounter encounter, string? booked)
    {
        // Booked is already ISO; it stays a date.
        if (booked == null)
            return;
        if (!DateTime.TryParseExact(booked, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var date))
            return;
        encounter.Extension.Add(new Extension(BookedUrl, new FhirDateTime(date.ToString("yyyy-MM-dd", Inv))));
    }

    // ---------- Helpers ----------

    private static DateTime? ParseDate(string? text)
    {
        if (text == null)
            return null;
        return DateTime.TryParseExact(text, "dd.MM.yyyy", Inv, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string FormatUtc(DateTime value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm':00Z'", Inv);

    private static string? Clean(string? value)
    {
        if (value == null)
            return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
