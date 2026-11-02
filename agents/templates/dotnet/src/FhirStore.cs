using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace Adapter;

/// <summary>Reads the source, runs the mapper and keeps the result for a short time.</summary>
public sealed class FhirStore
{
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    private readonly ISourceReader _reader;
    private readonly ILogger<FhirStore> _log;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<SourceRecord> _pushed = new();
    private MappedData? _data;
    private DateTime _loadedAt;

    public FhirStore(ISourceReader reader, ILogger<FhirStore> log, IConfiguration config)
    {
        _reader = reader;
        _log = log;
        _ttl = TimeSpan.FromSeconds(int.TryParse(config["CACHE_SECONDS"], out var s) ? s : 30);
    }

    public async Task<MappedData> GetAsync(CancellationToken ct = default)
    {
        if (_data is not null && DateTime.UtcNow - _loadedAt < _ttl)
            return _data;
        await _lock.WaitAsync(ct);
        try
        {
            if (_data is null || DateTime.UtcNow - _loadedAt >= _ttl)
            {
                var source = await _reader.ReadAsync(ct);
                lock (_pushed)
                {
                    if (_pushed.Count > 0)
                    {
                        if (!source.Tables.ContainsKey("messages"))
                            source.Tables["messages"] = new List<SourceRecord>();
                        source.Tables["messages"].AddRange(_pushed);
                    }
                }
                _data = Mapper.Map(source);
                _loadedAt = DateTime.UtcNow;
                // counts only: never log patient data
                _log.LogInformation(
                    "Mapped {Patients} patients, {Observations} observations, {Encounters} encounters",
                    _data.Patients.Count, _data.Observations.Count, _data.Encounters.Count);
            }
            return _data;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Adds a message that arrived over MLLP. The next request maps it.</summary>
    public void AddPushed(SourceRecord record)
    {
        lock (_pushed)
            _pushed.Add(record);
        _data = null;
    }

    public static IReadOnlyList<Resource>? Search(MappedData data, string type, string? patient, string? identifier)
    {
        IEnumerable<Resource>? items = type switch
        {
            "Patient" => data.Patients,
            "Observation" => data.Observations,
            "Encounter" => data.Encounters,
            _ => null,
        };
        if (items is null)
            return null;
        if (!string.IsNullOrEmpty(patient))
        {
            var id = patient.Split('/').Last();
            items = items.Where(r => r switch
            {
                Patient p => p.Id == id,
                Observation o => RefId(o.Subject) == id,
                Encounter e => RefId(e.Subject) == id,
                _ => false,
            });
        }
        if (!string.IsNullOrEmpty(identifier))
        {
            var value = identifier.Split('|').Last();
            items = items.Where(r => Identifiers(r).Any(i => i.Value == value));
        }
        return items.ToList();
    }

    public static Resource? Read(MappedData data, string type, string id) =>
        Search(data, type, null, null)?.FirstOrDefault(r => r.Id == id);

    private static string? RefId(ResourceReference? reference) => reference?.Reference?.Split('/').Last();

    private static IEnumerable<Identifier> Identifiers(Resource r) => r switch
    {
        Patient p => p.Identifier,
        Observation o => o.Identifier,
        Encounter e => e.Identifier,
        _ => Enumerable.Empty<Identifier>(),
    };
}
