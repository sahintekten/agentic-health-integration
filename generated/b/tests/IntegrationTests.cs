using System.Text.Json;
using Adapter;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Configuration;

namespace Adapter.Tests;

/// <summary>Reads the real mock system, runs the mapper and checks the result. Needs `make up`.</summary>
public class IntegrationTests
{
    private static async System.Threading.Tasks.Task<MappedData> MapLiveSource()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var source = await new LabDbReader(config).ReadAsync();
        Assert.NotEmpty(source.Tables.Values.SelectMany(rows => rows));
        return Mapper.Map(source);
    }

    [Fact]
    public async System.Threading.Tasks.Task Live_source_maps_to_patients()
    {
        var data = await MapLiveSource();
        Assert.NotEmpty(data.Patients);
        Assert.NotEmpty(data.Observations);
    }

    [Fact]
    public async System.Threading.Tasks.Task Patient_ids_follow_the_id_policy()
    {
        var data = await MapLiveSource();
        Assert.All(data.Patients, p => Assert.Matches("^[1-9][0-9]*$", p.Id));
        Assert.Equal(data.Patients.Count, data.Patients.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async System.Threading.Tasks.Task Every_subject_points_to_a_known_patient()
    {
        var data = await MapLiveSource();
        var ids = data.Patients.Select(p => "Patient/" + p.Id).ToHashSet();
        Assert.All(data.Observations, o => Assert.Contains(o.Subject?.Reference, ids));
        Assert.All(data.Encounters, e => Assert.Contains(e.Subject?.Reference, ids));
    }

    [Fact]
    public async System.Threading.Tasks.Task Mapped_resources_survive_a_json_round_trip()
    {
        var data = await MapLiveSource();
        var all = data.Patients.Cast<Resource>().Concat(data.Observations).Concat(data.Encounters).Take(300);
        foreach (var resource in all)
        {
            var json = JsonSerializer.Serialize(resource, FhirStore.Json);
            var back = (Resource)JsonSerializer.Deserialize(json, resource.GetType(), FhirStore.Json)!;
            Assert.Equal(resource.Id, back.Id);
        }
    }
}
