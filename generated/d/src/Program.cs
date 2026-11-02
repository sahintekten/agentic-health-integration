using System.Text.Json;
using Adapter;
using Hl7.Fhir.Model;

var builder = WebApplication.CreateBuilder(args);
// framework request logs include query strings such as ?patient=<id>, so keep them quiet
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<ISourceReader, CsvFileReader>();
builder.Services.AddSingleton<FhirStore>();

var app = builder.Build();

const string FhirJson = "application/fhir+json";
string[] types = { "Patient", "Encounter" };

IResult Fhir(Resource resource, int status = 200) =>
    Results.Text(JsonSerializer.Serialize(resource, FhirStore.Json), FhirJson, statusCode: status);

IResult NotFound(string message) => Fhir(new OperationOutcome
{
    Issue = { new OperationOutcome.IssueComponent
    {
        Severity = OperationOutcome.IssueSeverity.Error,
        Code = OperationOutcome.IssueType.NotFound,
        Diagnostics = message,
    } },
}, 404);

app.MapGet("/health", async (FhirStore store, CancellationToken ct) =>
{
    try
    {
        var data = await store.GetAsync(ct);
        return Results.Json(new
        {
            status = "UP",
            patients = data.Patients.Count,
            observations = data.Observations.Count,
            encounters = data.Encounters.Count,
            rules = data.RuleCounts,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "DOWN", error = ex.GetType().Name }, statusCode: 503);
    }
});

app.MapGet("/fhir/metadata", () =>
{
    var rest = new CapabilityStatement.RestComponent { Mode = CapabilityStatement.RestfulCapabilityMode.Server };
    foreach (var type in types)
    {
        var resource = new CapabilityStatement.ResourceComponent { Type = type };
        resource.Interaction.Add(new CapabilityStatement.ResourceInteractionComponent
            { Code = CapabilityStatement.TypeRestfulInteraction.Read });
        resource.Interaction.Add(new CapabilityStatement.ResourceInteractionComponent
            { Code = CapabilityStatement.TypeRestfulInteraction.SearchType });
        rest.Resource.Add(resource);
    }
    return Fhir(new CapabilityStatement
    {
        Description = "FHIR R4 adapter made by the Generate agent",
        Software = new CapabilityStatement.SoftwareComponent { Name = "Adapter", Version = "0.1.0" },
        Implementation = new CapabilityStatement.ImplementationComponent { Description = "appointments-export FHIR adapter" },
        Status = PublicationStatus.Active,
        Date = "2026-01-01",
        Kind = CapabilityStatementKind.Instance,
        FhirVersion = FHIRVersion.N4_0_1,
        Format = new[] { "json" },
        Rest = { rest },
    });
});

app.MapGet("/fhir/{type}", async (string type, string? patient, string? identifier, int? _count, HttpRequest request, FhirStore store, CancellationToken ct) =>
{
    if (!types.Contains(type))
        return NotFound($"Resource type {type} is not supported");
    var data = await store.GetAsync(ct);
    var matches = FhirStore.Search(data, type, patient, identifier)!;
    var baseUrl = $"{request.Scheme}://{request.Host}/fhir";
    var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = matches.Count };
    foreach (var resource in matches.Take(Math.Clamp(_count ?? 50, 0, 1000)))
        bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = $"{baseUrl}/{type}/{resource.Id}", Resource = resource });
    return Fhir(bundle);
});

app.MapGet("/fhir/{type}/{id}", async (string type, string id, FhirStore store, CancellationToken ct) =>
{
    if (!types.Contains(type))
        return NotFound($"Resource type {type} is not supported");
    var resource = FhirStore.Read(await store.GetAsync(ct), type, id);
    return resource is null ? NotFound($"{type}/{id} not found") : Fhir(resource);
});

app.Run();

public partial class Program { }
