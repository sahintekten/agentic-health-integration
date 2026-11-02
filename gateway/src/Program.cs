using System.Text.Json;
using Gateway;
using Hl7.Fhir.Model;

var builder = WebApplication.CreateBuilder(args);
// framework request logs include query strings such as ?patient=<id>, so keep them quiet
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddSingleton(sp => new LinkStore(builder.Configuration["INDEX_DB"] ?? "data/index.db"));
builder.Services.AddSingleton<Federation>();
var app = builder.Build();

const string FhirJson = "application/fhir+json";
var reconDir = app.Configuration["RECON_DIR"] ?? "../workspace";

IResult Fhir(Resource resource, int status = 200) =>
    Results.Text(JsonSerializer.Serialize(resource, Federation.Fhir), FhirJson, statusCode: status);

IResult NotFound(string message) => Fhir(new OperationOutcome
{
    Issue = { new OperationOutcome.IssueComponent
    {
        Severity = OperationOutcome.IssueSeverity.Error,
        Code = OperationOutcome.IssueType.NotFound,
        Diagnostics = message,
    } },
}, 404);

Bundle Searchset(HttpRequest request, int total, IEnumerable<Resource> items, int count)
{
    var baseUrl = $"{request.Scheme}://{request.Host}/fhir";
    var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = total };
    foreach (var r in items.Take(count))
        bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = $"{baseUrl}/{r.TypeName}/{r.Id}", Resource = r });
    return bundle;
}

object? LastReconciliation(string adapter)
{
    var dir = Path.Combine(reconDir, adapter, "validate");
    var summary = Path.Combine(dir, "summary.json");
    if (!File.Exists(summary))
        return null;
    var info = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(summary));
    var round = Path.Combine(dir, $"round_{info.GetProperty("rounds").GetInt32()}.json");
    object? recon = null;
    if (File.Exists(round) && JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(round)).TryGetProperty("reconciliation", out var r))
        recon = new { ok = r.GetProperty("ok").GetBoolean(), expected = r.GetProperty("expected"), actual = r.GetProperty("actual") };
    return new { validation = info.GetProperty("status").GetString(), reconciliation = recon };
}

app.MapGet("/health", async (Federation fed, CancellationToken ct) =>
{
    await fed.EnsureIndexAsync(ct);
    var adapters = new Dictionary<string, object?>();
    foreach (var adapter in fed.Adapters)
    {
        adapters[adapter.Name] = new
        {
            adapter.System,
            adapter.Url,
            adapter.Resources,
            health = await fed.AdapterHealthAsync(adapter, ct),
            last_validation = LastReconciliation(adapter.Name),
        };
    }
    return Results.Json(new { status = "UP", index = fed.Stats(), adapters });
});

app.MapGet("/fhir/metadata", () =>
{
    var rest = new CapabilityStatement.RestComponent { Mode = CapabilityStatement.RestfulCapabilityMode.Server };
    foreach (var type in new[] { "Patient", "Observation", "Encounter" })
    {
        var resource = new CapabilityStatement.ResourceComponent { Type = type };
        resource.Interaction.Add(new CapabilityStatement.ResourceInteractionComponent { Code = CapabilityStatement.TypeRestfulInteraction.SearchType });
        rest.Resource.Add(resource);
    }
    return Fhir(new CapabilityStatement
    {
        Description = "FHIR gateway over the generated adapters",
        Implementation = new CapabilityStatement.ImplementationComponent { Description = "agentic-health-integration gateway" },
        Status = PublicationStatus.Active,
        Date = "2026-01-01",
        Kind = CapabilityStatementKind.Instance,
        FhirVersion = FHIRVersion.N4_0_1,
        Format = new[] { "json" },
        Rest = { rest },
    });
});

app.MapGet("/fhir/Patient", async (string? identifier, int? _count, HttpRequest request, Federation fed, CancellationToken ct) =>
{
    await fed.EnsureIndexAsync(ct);
    var persons = fed.Persons().Select(fed.Person).OfType<Patient>();
    if (!string.IsNullOrEmpty(identifier))
    {
        var value = identifier.Split('|').Last();
        persons = persons.Where(p => p.Identifier.Any(i => i.Value == value));
    }
    var list = persons.ToList();
    return Fhir(Searchset(request, list.Count, list, Math.Clamp(_count ?? 50, 0, 1000)));
});

app.MapGet("/fhir/Patient/{id}", async (string id, Federation fed, CancellationToken ct) =>
{
    await fed.EnsureIndexAsync(ct);
    return fed.Person(id) is { } person ? Fhir(person) : NotFound($"Patient/{id} not found");
});

app.MapGet("/fhir/Patient/{id}/$everything", async (string id, HttpRequest request, Federation fed, CancellationToken ct) =>
{
    await fed.EnsureIndexAsync(ct);
    if (fed.Person(id) is not { } person)
        return NotFound($"Patient/{id} not found");
    var items = new List<Resource> { person };
    var total = 1;
    foreach (var type in new[] { "Observation", "Encounter" })
    {
        var (n, found) = await fed.SearchClinicalAsync(type, id, 1000, ct);
        total += n;
        items.AddRange(found);
    }
    return Fhir(Searchset(request, total, items, 5000));
});

foreach (var type in new[] { "Observation", "Encounter" })
{
    app.MapGet($"/fhir/{type}", async (string? patient, int? _count, HttpRequest request, Federation fed, CancellationToken ct) =>
    {
        await fed.EnsureIndexAsync(ct);
        var count = Math.Clamp(_count ?? 50, 0, 1000);
        var (total, items) = await fed.SearchClinicalAsync(type, patient?.Split('/').Last(), count, ct);
        return Fhir(Searchset(request, total, items, count));
    });
}

app.MapGet("/admin/links", async (string? type, Federation fed, CancellationToken ct) =>
{
    await fed.EnsureIndexAsync(ct);
    var links = fed.Links.Where(l => type is null || l.LinkType == type)
        .Select(l => new { l.System, l.SourceId, l.PersonId, l.LinkType, l.Status, l.Reason });
    return Results.Json(links);
});

app.MapPost("/admin/links/{system}/{sourceId}", async (string system, string sourceId, string status, Federation fed, CancellationToken ct) =>
{
    await fed.EnsureIndexAsync(ct);
    if (status is not ("approved" or "rejected" or "pending"))
        return Results.BadRequest(new { error = "status must be approved, rejected or pending" });
    return fed.SetLinkStatus(system, sourceId, status) ? Results.Ok(fed.Stats()) : Results.NotFound();
});

app.MapPost("/admin/reindex", async (Federation fed, CancellationToken ct) => Results.Json(await fed.ReindexAsync(ct)));

app.Run();

public partial class Program { }
