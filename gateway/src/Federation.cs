using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;

namespace Gateway;

public sealed record AdapterInfo(string Name, string System, string Url, List<string> Resources, bool Master = false);

/// <summary>Talks to the adapters and keeps the patient index.</summary>
public sealed class Federation
{
    public static readonly JsonSerializerOptions Fhir = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);
    private static readonly JsonSerializerOptions Plain = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly LinkStore _store;
    private readonly string _adaptersFile;
    private readonly ILogger<Federation> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, Patient> _patients = new();
    private Dictionary<(string, string), Link> _links = new();

    public Federation(HttpClient http, LinkStore store, IConfiguration config, ILogger<Federation> log)
    {
        _http = http;
        _store = store;
        _log = log;
        _adaptersFile = config["ADAPTERS_FILE"] ?? "adapters.json";
    }

    public List<AdapterInfo> Adapters { get; private set; } = new();
    public DateTime? IndexedAt { get; private set; }

    /// <summary>Reads the adapter list. A file that is still being written is read again.</summary>
    public List<AdapterInfo> LoadAdapters()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<List<AdapterInfo>>(File.ReadAllText(_adaptersFile), Plain) ?? new();
            }
            catch (JsonException) when (attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    /// <summary>Reads every Patient from every adapter and rebuilds the index.</summary>
    public async Task<object> ReindexAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Adapters = LoadAdapters();
            var patients = new Dictionary<string, Patient>();
            var sources = new List<SourcePatient>();
            foreach (var adapter in Adapters.Where(a => a.Resources.Contains("Patient")))
            {
                try
                {
                    var bundle = await SearchAsync(adapter, "Patient", "_count=1000", ct);
                    foreach (var p in bundle.Entry.Select(e => e.Resource).OfType<Patient>())
                    {
                        patients[$"{adapter.Name}/{p.Id}"] = p;
                        var name = p.Name.FirstOrDefault();
                        sources.Add(new SourcePatient(adapter.Name, p.Id, name?.Family,
                            string.Join(' ', name?.Given ?? Enumerable.Empty<string>()), p.BirthDate));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Adapter {Adapter} could not be read: {Error}", adapter.Name, ex.GetType().Name);
                }
            }
            var master = Adapters.FirstOrDefault(a => a.Master)?.Name ?? Adapters.First().Name;
            var links = Linker.Build(sources, master, _store.ReviewDecisions());
            _store.ReplaceAll(links);
            _patients = patients;
            _links = links.ToDictionary(l => (l.System, l.SourceId));
            IndexedAt = DateTime.UtcNow;
            return Stats();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (IndexedAt is null)
            await ReindexAsync(ct);
    }

    public bool SetLinkStatus(string system, string sourceId, string status)
    {
        if (!_store.SetStatus(system, sourceId, status))
            return false;
        _links = _store.All().ToDictionary(l => (l.System, l.SourceId));
        return true;
    }

    public IEnumerable<Link> Links => _links.Values;

    public object Stats() => new
    {
        persons = Persons().Count(),
        exact = _links.Values.Count(l => l.LinkType == "exact"),
        probable = _links.Values.Count(l => l.LinkType == "probable"),
        probable_pending = _links.Values.Count(l => l.LinkType == "probable" && l.Status == "pending"),
        probable_approved = _links.Values.Count(l => l.LinkType == "probable" && l.Status == "approved"),
        probable_rejected = _links.Values.Count(l => l.LinkType == "probable" && l.Status == "rejected"),
        unlinked = _links.Values.Count(l => l.LinkType == "unlinked"),
        indexed_at = IndexedAt,
    };

    /// <summary>The person a source record belongs to. Pending or rejected matches stay separate.</summary>
    public string PersonOf(string system, string sourceId) =>
        _links.TryGetValue((system, sourceId), out var link) && link.Merged ? link.PersonId : $"{system}-{sourceId}";

    public IEnumerable<string> Persons() =>
        _links.Values.Select(l => PersonOf(l.System, l.SourceId)).Distinct().OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>One merged Patient for a person: the master record plus identifiers and links of the others.</summary>
    public Patient? Person(string personId)
    {
        var members = _links.Values.Where(l => PersonOf(l.System, l.SourceId) == personId).ToList();
        if (members.Count == 0)
            return null;
        var main = members.OrderBy(l => l.Reason == "master record" ? 0 : 1).First();
        if (!_patients.TryGetValue($"{main.System}/{main.SourceId}", out var source))
            return null;
        var person = (Patient)source.DeepCopy();
        person.Id = personId;
        foreach (var member in members.Where(m => m != main))
        {
            if (_patients.TryGetValue($"{member.System}/{member.SourceId}", out var other))
            {
                foreach (var id in other.Identifier.Where(i => person.Identifier.All(x => x.Value != i.Value || x.System != i.System)))
                    person.Identifier.Add((Identifier)id.DeepCopy());
            }
            person.Link.Add(LinkTo(member, $"{member.LinkType}, {member.Status}"));
        }
        // matches that wait for review are shown, but their data is not merged
        foreach (var pending in _links.Values.Where(l => l.LinkType == "probable" && !l.Merged))
        {
            if (pending.PersonId == personId && pending.Status == "pending")
                person.Link.Add(LinkTo(pending, "probable match, waiting for review"));
            if ($"{pending.System}-{pending.SourceId}" == personId && pending.Status == "pending")
                person.Link.Add(new Patient.LinkComponent
                {
                    Other = new ResourceReference($"Patient/{pending.PersonId}") { Display = "probable match, waiting for review" },
                    Type = Patient.LinkType.Seealso,
                });
        }
        return person;
    }

    private static Patient.LinkComponent LinkTo(Link link, string note) => new()
    {
        Other = new ResourceReference($"Patient/{link.System}-{link.SourceId}") { Display = $"system {link.System}: {link.SourceId} ({note})" },
        Type = Patient.LinkType.Seealso,
    };

    /// <summary>Observations or Encounters, from every adapter that serves them, with subjects rewritten to persons.</summary>
    public async Task<(int Total, List<Resource> Items)> SearchClinicalAsync(string type, string? personId, int count, CancellationToken ct)
    {
        var total = 0;
        var items = new List<Resource>();
        foreach (var adapter in Adapters.Where(a => a.Resources.Contains(type)))
        {
            IEnumerable<string> queries = personId is null
                ? new[] { $"_count={count}" }
                : _links.Values.Where(l => l.System == adapter.Name && PersonOf(l.System, l.SourceId) == personId)
                    .Select(l => $"patient={Uri.EscapeDataString(l.SourceId)}&_count=1000");
            foreach (var query in queries)
            {
                var bundle = await SearchAsync(adapter, type, query, ct);
                total += bundle.Total ?? 0;
                foreach (var resource in bundle.Entry.Select(e => e.Resource).Where(r => r is not null))
                {
                    resource.Id = $"{adapter.Name}-{resource.Id}";
                    var subject = resource switch
                    {
                        Observation o => o.Subject,
                        Encounter e => e.Subject,
                        _ => null,
                    };
                    if (subject?.Reference is { } reference)
                        subject.Reference = $"Patient/{PersonOf(adapter.Name, reference.Split('/').Last())}";
                    items.Add(resource);
                }
            }
        }
        return (total, items);
    }

    public async Task<object> AdapterHealthAsync(AdapterInfo adapter, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{adapter.Url}/health", ct);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            return new { status = response.IsSuccessStatusCode ? "UP" : "DOWN", details = body };
        }
        catch (Exception ex)
        {
            return new { status = "DOWN", details = (object)new { error = ex.GetType().Name } };
        }
    }

    private async Task<Bundle> SearchAsync(AdapterInfo adapter, string type, string query, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{adapter.Url}/fhir/{type}?{query}", ct);
        return JsonSerializer.Deserialize<Bundle>(json, Fhir)!;
    }
}
