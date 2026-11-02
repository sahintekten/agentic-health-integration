namespace Adapter;

using System.Globalization;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

public static class Mapper
{
    private const string PatientSystem = "http://hospital.example.org/fhir/sid/adt-feed-patient";
    private const string VisitSystem = "http://hospital.example.org/fhir/sid/adt-feed-visit";
    private const string IdTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";
    private const string MaritalSystem = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus";
    private const string ActCodeSystem = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    private const string ActPrioritySystem = "http://terminology.hl7.org/CodeSystem/v3-ActPriority";
    private const string DischargeSystem = "http://terminology.hl7.org/CodeSystem/discharge-disposition";
    private const string ParticipationSystem = "http://terminology.hl7.org/CodeSystem/v3-ParticipationType";
    private const string TzTagSystem = "http://hospital.example.org/fhir/CodeSystem/time-zone-assumption";
    private const string SnomedUri = "http://snomed.info/sct";

    private static readonly Dictionary<string, AdministrativeGender> GenderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F"] = AdministrativeGender.Female,
        ["M"] = AdministrativeGender.Male,
        ["U"] = AdministrativeGender.Unknown,
    };

    private static readonly Dictionary<string, string> MaritalMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["M"] = "M",
        ["S"] = "S",
        ["D"] = "D",
        ["W"] = "W",
    };

    private static readonly Dictionary<string, string> ClassMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["E"] = "EMER",
        ["I"] = "IMP",
    };

    private static readonly Dictionary<string, string> PriorityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["E"] = "EM",
        ["R"] = "R",
    };

    private static readonly Dictionary<string, string> DischargeMap = new() { ["01"] = "home" };

    private static readonly Dictionary<string, string> CodeSystemMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SCT"] = SnomedUri,
    };

    private static readonly string[] OptionalKeys =
    {
        "PID-5.3", "PID-11.5", "PID-16", "PV1-3.2", "PV1-3.3", "PV1-36", "PV1-45",
        "ZEN-3.1", "ZEN-3.2", "ZEN-3.3",
    };

    public static MappedData Map(SourceData source)
    {
        var data = new MappedData();
        var patients = new Dictionary<string, Patient>();
        var encounters = new Dictionary<string, Encounter>();

        // latest_event_wins: process messages oldest first, so later non-null values overwrite earlier ones.
        var ordered = source.Table("messages")
            .OrderBy(r => Clean(r.Get("EVN-2")) ?? Clean(r.Get("MSH-7")) ?? "", StringComparer.Ordinal)
            .ThenBy(r => Clean(r.Get("MSH-10")) ?? "", StringComparer.Ordinal)
            .ToList();

        foreach (var rec in ordered)
        {
            try
            {
                ProcessMessage(rec, data, patients, encounters);
            }
            catch (Exception)
            {
                // Never throw on bad data: skip the record and count it.
                data.CountRule("other");
            }
        }

        return data;
    }

    private static void ProcessMessage(SourceRecord rec, MappedData data,
        Dictionary<string, Patient> patients, Dictionary<string, Encounter> encounters)
    {
        var evt = (Clean(rec.Get("MSH-9.2")) ?? Clean(rec.Get("EVN-1")))?.ToUpperInvariant();
        if (evt is not ("A01" or "A03" or "A08"))
        {
            data.CountRule("other");
            return;
        }

        var original = Clean(rec.Get("PID-3.2"));
        if (original == null)
        {
            data.CountRule("other");
            return;
        }

        CountOmittedOptionals(rec, data);

        var patientId = NormalisePatientId(original, data);
        MapPatient(rec, data, patients, patientId, original);
        MapEncounter(rec, evt, data, encounters, patientId);
    }

    // other: optional fields that are null are omitted from the output; count each one.
    private static void CountOmittedOptionals(SourceRecord rec, MappedData data)
    {
        foreach (var key in OptionalKeys)
            if (Clean(rec.Get(key)) == null)
                data.CountRule("other");
    }

    // ---------- Patient ----------

    private static string NormalisePatientId(string original, MappedData data)
    {
        // normalise_identifier: hospital number = integer without leading zeros.
        if (original.All(char.IsAsciiDigit) && long.TryParse(original, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            var id = n.ToString(CultureInfo.InvariantCulture);
            if (id != original)
                data.CountRule("normalise_identifier");
            return id;
        }
        // Not numeric: keep record with original id, counted as unlinked.
        data.CountRule("normalise_identifier");
        return original;
    }

    private static void MapPatient(SourceRecord rec, MappedData data, Dictionary<string, Patient> patients,
        string id, string original)
    {
        if (!patients.TryGetValue(id, out var p))
        {
            p = new Patient { Id = id };
            patients[id] = p;
            data.Patients.Add(p);
        }
        else
        {
            data.CountRule("drop_duplicates"); // merge repeats instead of emitting duplicates
        }

        SetPatientIdentifier(p, rec, original);
        SetPatientName(p, rec);
        SetBirthDate(p, rec, data);
        SetGender(p, rec);
        SetAddress(p, rec, data);
        SetMaritalStatus(p, rec);
    }

    private static void SetPatientIdentifier(Patient p, SourceRecord rec, string original)
    {
        var ident = new Identifier(PatientSystem, original);
        var assigner = Clean(rec.Get("PID-3.1"));
        if (assigner != null)
            ident.Assigner = new ResourceReference { Display = assigner };
        var type = Clean(rec.Get("PID-3.3"));
        if (type != null && type.Equals("MR", StringComparison.OrdinalIgnoreCase))
            ident.Type = new CodeableConcept(IdTypeSystem, "MR");
        p.Identifier.Clear();
        p.Identifier.Add(ident);
    }

    private static void SetPatientName(Patient p, SourceRecord rec)
    {
        var family = Clean(rec.Get("PID-5.1"));
        var given = Clean(rec.Get("PID-5.2"));
        var middle = Clean(rec.Get("PID-5.3")); // omitted when null
        if (family == null && given == null && middle == null)
            return;

        var name = p.Name.FirstOrDefault();
        if (name == null)
        {
            name = new HumanName();
            p.Name.Add(name);
        }
        if (family != null)
            name.Family = family;
        var list = name.Given.ToList();
        if (given != null)
            SetAt(list, 0, given);
        if (middle != null)
            SetAt(list, 1, middle);
        name.Given = list;
    }

    private static void SetAt(List<string> list, int index, string value)
    {
        if (index < list.Count)
            list[index] = value;
        else
            list.Add(value);
    }

    private static void SetBirthDate(Patient p, SourceRecord rec, MappedData data)
    {
        var raw = Clean(rec.Get("PID-7"));
        if (raw == null)
            return;
        // date_format: YYYYMMDD stays a date.
        if (DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            p.BirthDate = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            data.CountRule("date_format");
        }
    }

    private static void SetGender(Patient p, SourceRecord rec)
    {
        var raw = Clean(rec.Get("PID-8"));
        if (raw != null && GenderMap.TryGetValue(raw, out var g))
            p.Gender = g;
    }

    private static void SetAddress(Patient p, SourceRecord rec, MappedData data)
    {
        var line = Clean(rec.Get("PID-11.1"));
        var city = Clean(rec.Get("PID-11.3"));
        var state = Clean(rec.Get("PID-11.4"));
        var postal = Clean(rec.Get("PID-11.5"));
        var country = Clean(rec.Get("PID-11.6"));
        if (line == null && city == null && state == null && postal == null && country == null)
            return;

        var addr = p.Address.FirstOrDefault();
        if (addr == null)
        {
            addr = new Address();
            p.Address.Add(addr);
        }
        if (line != null)
            addr.Line = new[] { line };
        if (city != null)
            addr.City = city;
        if (state != null)
            addr.State = state;
        if (country != null)
            addr.Country = country;
        if (postal != null)
        {
            // restore_leading_zeros: postal code stays text, left-padded to 5 digits.
            if (postal.Length < 5 && postal.All(char.IsAsciiDigit))
            {
                postal = postal.PadLeft(5, '0');
                data.CountRule("restore_leading_zeros");
            }
            addr.PostalCode = postal;
        }
    }

    private static void SetMaritalStatus(Patient p, SourceRecord rec)
    {
        var raw = Clean(rec.Get("PID-16"));
        if (raw != null && MaritalMap.TryGetValue(raw, out var code))
            p.MaritalStatus = new CodeableConcept(MaritalSystem, code);
    }

    // ---------- Encounter ----------

    private static void MapEncounter(SourceRecord rec, string evt, MappedData data,
        Dictionary<string, Encounter> encounters, string patientId)
    {
        var visit = Clean(rec.Get("PV1-19"));
        if (visit == null)
        {
            data.CountRule("other"); // cannot build a stable Encounter id
            return;
        }

        if (!encounters.TryGetValue(visit, out var e))
        {
            e = new Encounter
            {
                Id = visit,
                Status = Encounter.EncounterStatus.InProgress,
                Subject = new ResourceReference("Patient/" + patientId),
            };
            e.Identifier.Add(new Identifier(VisitSystem, visit));
            encounters[visit] = e;
            data.Encounters.Add(e);
        }
        else
        {
            data.CountRule("drop_duplicates"); // A01/A03/A08 of one visit collapse into one Encounter
            data.CountRule("latest_event_wins"); // later message updates the earlier state, never erasing it
        }

        e.Subject = new ResourceReference("Patient/" + patientId);

        SetStatusAndDischarge(e, rec, evt, data);
        SetClassPriorityService(e, rec);
        SetPeriodStart(e, rec, data);
        SetLocation(e, rec);
        SetParticipant(e, rec);
        SetType(e, rec);
        SetReason(e, rec, data);
    }

    private static void SetStatusAndDischarge(Encounter e, SourceRecord rec, string evt, MappedData data)
    {
        // A03 finishes the visit; A01/A08 never move the status backwards or clear discharge data.
        if (evt != "A03")
            return;

        e.Status = Encounter.EncounterStatus.Finished;

        var end = Clean(rec.Get("PV1-45"));
        if (end != null && TryTimestamp(end, data, out var iso))
        {
            e.Period ??= new Period();
            e.Period.End = iso;
            AddAssumedUtcTag(e);
        }

        var disp = Clean(rec.Get("PV1-36"));
        if (disp != null && DischargeMap.TryGetValue(disp, out var code))
        {
            e.Hospitalization ??= new Encounter.HospitalizationComponent();
            e.Hospitalization.DischargeDisposition = new CodeableConcept(DischargeSystem, code);
        }
    }

    private static void SetClassPriorityService(Encounter e, SourceRecord rec)
    {
        var cls = Clean(rec.Get("PV1-2"));
        if (cls != null && ClassMap.TryGetValue(cls, out var c))
            e.Class = new Coding(ActCodeSystem, c);

        var prio = Clean(rec.Get("PV1-4"));
        if (prio != null && PriorityMap.TryGetValue(prio, out var pr))
            e.Priority = new CodeableConcept(ActPrioritySystem, pr);

        var svc = Clean(rec.Get("PV1-10"));
        if (svc != null)
            e.ServiceType = new CodeableConcept { Coding = new List<Coding> { new Coding { Code = svc } } };

        var facility = Clean(rec.Get("PV1-3.4"));
        if (facility != null)
            e.ServiceProvider = new ResourceReference { Display = facility };
    }

    private static void SetPeriodStart(Encounter e, SourceRecord rec, MappedData data)
    {
        var start = Clean(rec.Get("PV1-44"));
        if (start != null && TryTimestamp(start, data, out var iso))
        {
            e.Period ??= new Period();
            e.Period.Start = iso;
            AddAssumedUtcTag(e);
        }
    }

    private static void SetLocation(Encounter e, SourceRecord rec)
    {
        var point = Clean(rec.Get("PV1-3.1"));
        if (point == null)
            return;
        var room = Clean(rec.Get("PV1-3.2")); // inferred room, skipped when null
        var bed = Clean(rec.Get("PV1-3.3"));  // inferred bed, skipped when null

        var display = point;
        if (room != null && bed != null)
            display += $" {room}-{bed}";
        else if (room != null)
            display += " " + room;
        else if (bed != null)
            display += " " + bed;

        e.Location.Clear();
        e.Location.Add(new Encounter.LocationComponent { Location = new ResourceReference { Display = display } });
    }

    private static void SetParticipant(Encounter e, SourceRecord rec)
    {
        var code = Clean(rec.Get("PV1-7.1"));
        if (code == null)
            return;
        var family = Clean(rec.Get("PV1-7.2"));
        var given = Clean(rec.Get("PV1-7.3"));
        var display = string.Join(", ", new[] { family, given }.Where(s => s != null));

        var reference = new ResourceReference("Practitioner/" + code);
        if (display.Length > 0)
            reference.Display = display;

        var part = new Encounter.ParticipantComponent { Individual = reference };
        part.Type.Add(new CodeableConcept(ParticipationSystem, "ATND"));
        e.Participant.Clear();
        e.Participant.Add(part);
    }

    private static void SetType(Encounter e, SourceRecord rec)
    {
        var code = Clean(rec.Get("ZEN-2.1"));
        if (code == null)
            return;
        var coding = new Coding { Code = code, Display = Clean(rec.Get("ZEN-2.2")) };
        var sys = Clean(rec.Get("ZEN-2.3"));
        if (sys != null && CodeSystemMap.TryGetValue(sys, out var uri))
            coding.System = uri;
        e.Type.Clear();
        e.Type.Add(new CodeableConcept { Coding = new List<Coding> { coding } });
    }

    private static void SetReason(Encounter e, SourceRecord rec, MappedData data)
    {
        // other: no empty reasonCode entries when ZEN-3 is null.
        var code = Clean(rec.Get("ZEN-3.1"));
        if (code == null)
            return;

        var coding = new Coding { Code = code };
        var rawDisplay = rec.Get("ZEN-3.2");
        var display = CollapseSpaces(rawDisplay);
        if (display != null)
        {
            // trim_whitespace: trim and collapse repeated spaces.
            if (display != rawDisplay)
                data.CountRule("trim_whitespace");
            coding.Display = display;
        }
        var sys = Clean(rec.Get("ZEN-3.3"));
        if (sys != null && CodeSystemMap.TryGetValue(sys, out var uri))
            coding.System = uri;

        e.ReasonCode.Clear();
        e.ReasonCode.Add(new CodeableConcept { Coding = new List<Coding> { coding } });
    }

    // ---------- Helpers ----------

    // timezone_assumption + date_format: HL7 TS without offset -> UTC dateTime with Z.
    private static bool TryTimestamp(string raw, MappedData data, out string iso)
    {
        iso = "";
        if (!DateTime.TryParseExact(raw, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        iso = dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        data.CountRule("date_format");
        data.CountRule("timezone_assumption");
        return true;
    }

    // Flag on the resource that the time zone was assumed to be UTC.
    private static void AddAssumedUtcTag(Encounter e)
    {
        e.Meta ??= new Meta();
        if (e.Meta.Tag.Any(t => t.System == TzTagSystem && t.Code == "assumed-utc"))
            return;
        e.Meta.Tag.Add(new Coding(TzTagSystem, "assumed-utc", "Time zone assumed to be UTC"));
    }

    private static string? Clean(string? value)
    {
        if (value == null)
            return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? CollapseSpaces(string? value)
    {
        var t = Clean(value);
        return t == null ? null : Regex.Replace(t, @"\s+", " ");
    }
}
