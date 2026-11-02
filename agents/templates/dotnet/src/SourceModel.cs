namespace Adapter;

/// <summary>One source row or message: field name -> raw text (null when empty). Keys ignore case.</summary>
public sealed class SourceRecord : Dictionary<string, string?>
{
    public SourceRecord() : base(StringComparer.OrdinalIgnoreCase) { }

    public string? Get(string key) => TryGetValue(key, out var value) ? value : null;
}

/// <summary>Everything read from the source system, grouped by table name.</summary>
public sealed class SourceData
{
    public Dictionary<string, List<SourceRecord>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SourceRecord> Table(string name) => Tables.TryGetValue(name, out var rows) ? rows : new List<SourceRecord>();
}

/// <summary>FHIR resources made by the mapper, plus how often each data rule fired.</summary>
public sealed class MappedData
{
    public List<Hl7.Fhir.Model.Patient> Patients { get; } = new();
    public List<Hl7.Fhir.Model.Observation> Observations { get; } = new();
    public List<Hl7.Fhir.Model.Encounter> Encounters { get; } = new();
    public Dictionary<string, int> RuleCounts { get; } = new();

    public void CountRule(string ruleId, int times = 1) =>
        RuleCounts[ruleId] = RuleCounts.GetValueOrDefault(ruleId) + times;
}

public interface ISourceReader
{
    Task<SourceData> ReadAsync(CancellationToken ct = default);
}
