namespace Adapter;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

public static class Mapper
{
    private const string SysPatNo = "http://hospital.example.org/fhir/sid/appointments-patno";
    private const string SysApptRef = "http://hospital.example.org/fhir/sid/appointments-apptref";
    private const string SysHospitalNumber = "http://hospital.example.org/fhir/sid/hospital-number";
    private const string SysClinic = "http://hospital.example.org/fhir/CodeSystem/clinic";
    private const string SysSnomed = "http://snomed.info/sct";
    private const string SysActCode = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    private const string UrlBookedDate = "http://hospital.example.org/fhir/StructureDefinition/booked-date";

    private const int DurationReviewLimit = 300;
    private const int DurationMax = 354;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Display text -> SNOMED CT code (display is the text without the parenthesised category).
    private static readonly Dictionary<string, string> SnomedLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Encounter for problem (procedure)"] = "185347001",
        ["Encounter for check up (procedure)"] = "185349003",
        ["General examination of patient (procedure)"] = "162673000",
        ["Well child visit (procedure)"] = "410620009",
        ["Prenatal visit (regime/therapy)"] = "424619006",
        ["Outpatient procedure (procedure)"] = "440655000",
        ["Encounter for symptom (procedure)"] = "185345009",
        ["Follow-up encounter (procedure)"] = "390906007",
        ["Patient encounter procedure (procedure)"] = "308335008",
        ["Consultation for treatment (procedure)"] = "698314001",
        ["Death Certification"] = "308646001",
        ["Postnatal visit (regime/therapy)"] = "169762003",
        ["Asthma follow-up (regime/therapy)"] = "394701000",
        ["Hospital admission (procedure)"] = "32485007",
        ["Ophthalmic examination and evaluation (procedure)"] = "36228007",
        ["Prenatal initial visit (regime/therapy)"] = "424441002",
    };

    private sealed class Appointment
    {
        public SourceRecord Row = new();
        public string Signature = "";
        public List<string> Statuses = new();
    }

    public static MappedData Map(SourceData source)
    {
        var data = new MappedData();
        try
        {
            var rows = OrderByFile(source.Table("rows"));

            var appointments = new Dictionary<string, Appointment>(StringComparer.Ordinal);
            var appointmentOrder = new List<string>();
            var patientRows = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
            var patientOrder = new List<string>();
            var startSeen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                // Footer lines such as '--- END OF EXPORT n ROWS ---' are not records.
                if (IsFooter(row))
                {
                    data.CountRule("other");
                    continue;
                }

                var apptRef = Clean(row.Get("ApptRef"));
                var patNo = Clean(row.Get("PatNo"));
                if (apptRef == null || patNo == null)
                {
                    data.CountRule("other");
                    continue;
                }

                FlagStartConflict(row, apptRef, patNo, startSeen, data);
                CollectAppointment(row, apptRef, appointments, appointmentOrder, data);

                var (patientId, _) = ResolvePatientId(patNo);
                if (!patientRows.ContainsKey(patientId))
                    patientOrder.Add(patientId);
                patientRows[patientId] = row; // latest row wins
            }

            foreach (var id in patientOrder)
                AddPatient(data, patientRows[id], id);

            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in appointmentOrder)
                AddEncounter(data, appointments[key], usedIds);
        }
        catch (Exception)
        {
            data.CountRule("other");
        }
        return data;
    }

    // ---------- reading and grouping ----------

    // Files are read oldest first; sort stably by ISO year/week when the file name has one.
    private static List<SourceRecord> OrderByFile(List<SourceRecord> rows)
    {
        return rows
            .Select((r, i) => (r, i, k: FileKey(r.Get("_file"))))
            .OrderBy(x => x.k)
            .ThenBy(x => x.i)
            .Select(x => x.r)
            .ToList();
    }

    private static int FileKey(string? file)
    {
        if (file == null)
            return 0;
        var m = Regex.Match(file, @"(\d{4})\D{0,3}W?(\d{2})", RegexOptions.IgnoreCase);
        if (!m.Success)
            return 0;
        return int.Parse(m.Groups[1].Value, Inv) * 100 + int.Parse(m.Groups[2].Value, Inv);
    }

    private static bool IsFooter(SourceRecord row)
    {
        foreach (var kv in row)
        {
            if (kv.Key.Equals("_file", StringComparison.OrdinalIgnoreCase) || kv.Value == null)
                continue;
            var v = kv.Value.TrimStart();
            if (v.StartsWith("---", StringComparison.Ordinal) ||
                v.Contains("END OF EXPORT", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string RowSignature(SourceRecord row)
    {
        var parts = row
            .Where(kv => !kv.Key.Equals("_file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key.ToLowerInvariant() + "=" + (kv.Value ?? "").Trim());
        return string.Join("|", parts);
    }

    // Rules latest_event_wins and drop_duplicates: one record per ApptRef.
    private static void CollectAppointment(SourceRecord row, string apptRef,
        Dictionary<string, Appointment> appointments, List<string> order, MappedData data)
    {
        var sig = RowSignature(row);
        var status = Clean(row.Get("Status"));

        if (!appointments.TryGetValue(apptRef, out var appt))
        {
            appt = new Appointment { Row = row, Signature = sig };
            if (status != null)
                appt.Statuses.Add(status);
            appointments[apptRef] = appt;
            order.Add(apptRef);
            return;
        }

        if (appt.Signature == sig)
        {
            data.CountRule("drop_duplicates");
            return;
        }

        data.CountRule("latest_event_wins");
        appt.Row = row;
        appt.Signature = sig;
        if (status != null && !appt.Statuses.Contains(status))
            appt.Statuses.Add(status);
    }

    // Same ApptStart with a different ApptRef/PatNo/PatName/Booked: do not merge, flag for review.
    private static void FlagStartConflict(SourceRecord row, string apptRef, string patNo,
        Dictionary<string, HashSet<string>> seen, MappedData data)
    {
        var start = Clean(row.Get("ApptStart"));
        if (start == null)
            return;
        var tuple = string.Join("|", apptRef, patNo, Clean(row.Get("PatName")) ?? "", Clean(row.Get("Booked")) ?? "");
        if (!seen.TryGetValue(start, out var set))
        {
            seen[start] = new HashSet<string>(StringComparer.Ordinal) { tuple };
            return;
        }
        if (set.Add(tuple))
            data.CountRule("other");
    }

    // ---------- patient ----------

    // Returns the Patient.id and whether it is a real hospital number.
    private static (string Id, bool Linked) ResolvePatientId(string patNo)
    {
        var m = Regex.Match(patNo.Trim(), @"^(?:[A-Za-z]+[/\-]?)?(\d+)(?:-[A-Za-z])?$");
        if (!m.Success)
            return (patNo.Trim(), false);
        var digits = m.Groups[1].Value.TrimStart('0');
        return (digits.Length == 0 ? "0" : digits, true);
    }

    private static void AddPatient(MappedData data, SourceRecord row, string id)
    {
        try
        {
            var patNo = Clean(row.Get("PatNo"))!;
            var (_, linked) = ResolvePatientId(patNo);

            var p = new Patient { Id = id };
            p.Identifier.Add(new Identifier(SysPatNo, patNo));

            if (linked)
            {
                p.Identifier.Add(new Identifier(SysHospitalNumber, id));
                // normalise_identifier: prefix, suffix and leading zeros removed
                if (!string.Equals(id, patNo, StringComparison.Ordinal))
                    data.CountRule("normalise_identifier");
            }
            else
            {
                data.CountRule("other"); // unlinked patient: original id kept
            }

            SetName(p, Clean(row.Get("PatName")));
            SetBirthDate(p, Clean(row.Get("Geb.Dat")), data);
            p.Gender = MapGender(Clean(row.Get("Sex")));

            data.Patients.Add(p);
        }
        catch (Exception)
        {
            data.CountRule("other");
        }
    }

    // 'Given [Given...] SURNAME': last token is the surname.
    private static void SetName(Patient p, string? name)
    {
        if (name == null)
            return;
        var tokens = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return;

        var hn = new HumanName { Text = name };
        hn.Family = ProperCase(tokens[^1]);
        if (tokens.Length > 1)
            hn.Given = tokens.Take(tokens.Length - 1);
        p.Name.Add(hn);
    }

    private static string ProperCase(string token)
    {
        if (token != token.ToUpperInvariant())
            return token; // already mixed case
        var chars = token.ToLowerInvariant().ToCharArray();
        var upperNext = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (upperNext && char.IsLetter(chars[i]))
                chars[i] = char.ToUpperInvariant(chars[i]);
            upperNext = chars[i] == '-' || chars[i] == '\'' || chars[i] == ' ';
        }
        return new string(chars);
    }

    private static void SetBirthDate(Patient p, string? raw, MappedData data)
    {
        if (raw == null)
            return;
        if (DateTime.TryParseExact(raw, "dd.MM.yyyy", Inv, DateTimeStyles.None, out var d))
        {
            p.BirthDate = d.ToString("yyyy-MM-dd", Inv);
            data.CountRule("date_format");
        }
    }

    private static AdministrativeGender MapGender(string? sex)
    {
        return sex?.ToLowerInvariant() switch
        {
            "m" => AdministrativeGender.Male,
            "w" => AdministrativeGender.Female, // 'w' = weiblich
            _ => AdministrativeGender.Unknown,
        };
    }

    // ---------- encounter ----------

    private static void AddEncounter(MappedData data, Appointment appt, HashSet<string> usedIds)
    {
        try
        {
            var row = appt.Row;
            var apptRef = Clean(row.Get("ApptRef"))!;
            var patNo = Clean(row.Get("PatNo"))!;
            var (patientId, _) = ResolvePatientId(patNo);

            var e = new Encounter
            {
                Id = UniqueId(apptRef, usedIds),
                Subject = new ResourceReference("Patient/" + patientId),
                Status = MapStatus(Clean(row.Get("Status"))),
            };
            e.Identifier.Add(new Identifier(SysApptRef, apptRef));

            var start = SetPeriod(e, row, data);
            AddStatusHistory(e, appt, start);
            SetClinic(e, Clean(row.Get("Clinic")));
            SetType(e, Clean(row.Get("ApptType")));
            SetBooked(e, Clean(row.Get("Booked")));

            data.Encounters.Add(e);
        }
        catch (Exception)
        {
            data.CountRule("other");
        }
    }

    private static string UniqueId(string apptRef, HashSet<string> used)
    {
        var baseId = Regex.Replace(apptRef, @"[^A-Za-z0-9\-\.]", "-");
        if (baseId.Length > 60)
            baseId = baseId.Substring(0, 60);
        var id = baseId;
        var n = 2;
        while (!used.Add(id))
            id = baseId + "-" + n++.ToString(Inv);
        return id;
    }

    // Provisional legend; NS (no-show) is never 'finished'.
    private static Encounter.EncounterStatus MapStatus(string? code)
    {
        return code?.ToUpperInvariant() switch
        {
            "BK" => Encounter.EncounterStatus.Planned,
            "DN" => Encounter.EncounterStatus.Finished,
            "NS" => Encounter.EncounterStatus.Cancelled,
            "CX" => Encounter.EncounterStatus.Cancelled,
            _ => Encounter.EncounterStatus.Unknown,
        };
    }

    // Returns the formatted start (or null).
    private static string? SetPeriod(Encounter e, SourceRecord row, MappedData data)
    {
        var rawStart = Clean(row.Get("ApptStart"));
        if (rawStart == null)
            return null;
        if (!DateTime.TryParseExact(rawStart, "dd.MM.yyyy HH:mm", Inv, DateTimeStyles.None, out var start))
            return null;

        // date_format + timezone_assumption: no offset in source, assume UTC
        data.CountRule("date_format");
        data.CountRule("timezone_assumption");

        var period = new Period { Start = FormatUtc(start) };

        var dur = ParseDuration(Clean(row.Get("DurMin")), data);
        if (dur.HasValue)
            period.End = FormatUtc(start.AddMinutes(dur.Value)); // planned end only

        e.Period = period;
        return period.Start;
    }

    private static string FormatUtc(DateTime dt) => dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);

    // DurMin: text to integer minutes; flag long values, drop implausible ones.
    private static int? ParseDuration(string? raw, MappedData data)
    {
        if (raw == null)
            return null;
        if (!int.TryParse(raw, NumberStyles.None, Inv, out var minutes))
            return null;
        if (minutes > DurationReviewLimit)
            data.CountRule("other");
        if (minutes > DurationMax)
            return null;
        return minutes;
    }

    // Each distinct status seen across weekly files; period only carries the planned start.
    private static void AddStatusHistory(Encounter e, Appointment appt, string? start)
    {
        if (appt.Statuses.Count < 2 || start == null)
            return;
        foreach (var code in appt.Statuses)
        {
            e.StatusHistory.Add(new Encounter.StatusHistoryComponent
            {
                Status = MapStatus(code),
                Period = new Period { Start = start },
            });
        }
    }

    private static void SetClinic(Encounter e, string? clinic)
    {
        if (clinic == null)
            return;
        e.ServiceType = new CodeableConcept(SysClinic, clinic);

        // Provisional class mapping, to be confirmed with the source owner
        if (clinic.Equals("OPD-GEN", StringComparison.OrdinalIgnoreCase) ||
            clinic.Equals("OPD-SPEC", StringComparison.OrdinalIgnoreCase) ||
            clinic.Equals("GP-WELL", StringComparison.OrdinalIgnoreCase))
        {
            e.Class = new Coding(SysActCode, "AMB");
        }
    }

    private static void SetType(Encounter e, string? apptType)
    {
        if (apptType == null)
            return;
        var type = new CodeableConcept { Text = apptType };
        var display = Regex.Replace(apptType, @"\s*\([^)]*\)\s*$", "").Trim();

        if (SnomedLookup.TryGetValue(apptType, out var code))
            type.Coding.Add(new Coding(SysSnomed, code, display));
        else
            type.Coding.Add(new Coding { Display = display }); // no match: display only

        e.Type.Add(type);
    }

    // Booked is the booking date, kept as an extension (not the period).
    private static void SetBooked(Encounter e, string? booked)
    {
        if (booked == null)
            return;
        if (!DateTime.TryParseExact(booked, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d))
            return;
        e.Extension.Add(new Extension(UrlBookedDate, new Date(d.ToString("yyyy-MM-dd", Inv))));
    }

    private static string? Clean(string? s)
    {
        if (s == null)
            return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }
}
