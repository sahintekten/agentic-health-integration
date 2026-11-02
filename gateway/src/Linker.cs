using System.Globalization;
using System.Text;

namespace Gateway;

/// <summary>A patient record as one adapter returns it.</summary>
public sealed record SourcePatient(string System, string Id, string? Family, string? Given, string? BirthDate);

/// <summary>How one source record belongs to a person.</summary>
/// <param name="LinkType">exact, probable or unlinked</param>
/// <param name="Status">auto, pending, approved or rejected</param>
public sealed record Link(string System, string SourceId, string PersonId, string LinkType, string Status, string Reason)
{
    /// <summary>Exact links and approved probable links merge data into the person.</summary>
    public bool Merged => LinkType == "exact" || (LinkType == "probable" && Status == "approved");
}

/// <summary>Deterministic patient linking across systems.</summary>
public static class Linker
{
    /// <summary>
    /// 1. A record of the master system is its own person (id = hospital number).
    /// 2. A record with the same hospital number as a master record is an exact link.
    /// 3. A record with another id but the same family name, a compatible first name and the same
    ///    birth date as exactly one master record is a probable link. It waits for review.
    /// 4. Anything else is unlinked.
    /// Review decisions from earlier runs (previous) are kept.
    /// </summary>
    public static List<Link> Build(
        IReadOnlyList<SourcePatient> patients,
        string masterSystem,
        IReadOnlyDictionary<(string System, string SourceId), string>? previous = null)
    {
        previous ??= new Dictionary<(string, string), string>();
        var masters = patients.Where(p => p.System == masterSystem).ToList();
        var masterIds = masters.Select(p => p.Id).ToHashSet();
        var links = new List<Link>();
        foreach (var p in patients)
        {
            if (p.System == masterSystem)
            {
                links.Add(new Link(p.System, p.Id, p.Id, "exact", "auto", "master record"));
                continue;
            }
            if (masterIds.Contains(p.Id))
            {
                links.Add(new Link(p.System, p.Id, p.Id, "exact", "auto", "same hospital number"));
                continue;
            }
            var matches = masters.Where(m => SamePerson(m, p)).ToList();
            if (matches.Count == 1)
            {
                var status = previous.TryGetValue((p.System, p.Id), out var s) ? s : "pending";
                links.Add(new Link(p.System, p.Id, matches[0].Id, "probable", status,
                    "same family name, first name and birth date, different id"));
                continue;
            }
            links.Add(new Link(p.System, p.Id, $"{p.System}-{p.Id}", "unlinked", "auto",
                matches.Count > 1 ? "more than one possible match" : "no match"));
        }
        return links;
    }

    public static bool SamePerson(SourcePatient a, SourcePatient b)
    {
        if (string.IsNullOrEmpty(a.BirthDate) || a.BirthDate != b.BirthDate)
            return false;
        var familyA = Normalise(a.Family);
        var familyB = Normalise(b.Family);
        if (familyA.Length < 2 || familyA != familyB)
            return false;
        var givenA = Normalise(a.Given).Split(' ')[0];
        var givenB = Normalise(b.Given).Split(' ')[0];
        // one system cuts names at 20 characters, so a cut first name still counts
        var shorter = givenA.Length <= givenB.Length ? givenA : givenB;
        var longer = givenA.Length <= givenB.Length ? givenB : givenA;
        return shorter.Length >= 2 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var decomposed = value.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
