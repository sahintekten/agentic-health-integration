// Validates FHIR R4 JSON files against the base R4 profiles with the Firely validator.
// Usage: FhirCheck file1.json file2.json ...  Prints one JSON line per file.
using System.Text.Json;
using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;

var options = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);
var source = new CachedResolver(ZipSource.CreateValidationSource());
var validator = new Validator(source.AsAsync(), new LocalTerminologyService(source.AsAsync()));
var failed = 0;

foreach (var file in args)
{
    var errors = new List<string>();
    var checkedResources = 0;
    try
    {
        var resource = JsonSerializer.Deserialize<Resource>(File.ReadAllText(file), options)!;
        var resources = resource is Bundle bundle
            ? bundle.Entry.Select(e => e.Resource).Where(r => r is not null).Prepend(bundle).ToList()
            : new List<Resource> { resource };
        foreach (var r in resources)
        {
            checkedResources++;
            var outcome = validator.Validate(r);
            errors.AddRange(outcome.Issue
                .Where(i => i.Severity is OperationOutcome.IssueSeverity.Error or OperationOutcome.IssueSeverity.Fatal)
                .Select(i => $"{r.TypeName}/{r.Id}: {i.Details?.Text ?? i.Diagnostics}"));
        }
    }
    catch (Exception ex)
    {
        errors.Add($"parse error: {ex.Message}");
    }
    if (errors.Count > 0)
        failed++;
    Console.WriteLine(JsonSerializer.Serialize(new { file, resources = checkedResources, errors = errors.Take(20) }));
}
return failed == 0 ? 0 : 1;
