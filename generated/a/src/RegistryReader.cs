using System.Text.Json;

namespace Adapter;

/// <summary>Reads every record from the REST source, page by page. Table name: "patients".</summary>
public sealed class RegistryReader : ISourceReader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _baseUrl;

    public RegistryReader(IConfiguration config)
    {
        _baseUrl = (config["REGISTRY_URL"] ?? "http://localhost:8001").TrimEnd('/');
    }

    public async Task<SourceData> ReadAsync(CancellationToken ct = default)
    {
        var data = new SourceData();
        var rows = new List<SourceRecord>();
        data.Tables["patients"] = rows;
        for (var page = 1; page < 10_000; page++)
        {
            var url = $"{_baseUrl}/patients?pg={page}&sz=100";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var list = doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
            if (list.Value.ValueKind != JsonValueKind.Array || list.Value.GetArrayLength() == 0)
                break;
            foreach (var item in list.Value.EnumerateArray())
            {
                var record = new SourceRecord();
                foreach (var prop in item.EnumerateObject())
                {
                    record[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => prop.Value.GetString(),
                        _ => prop.Value.GetRawText(),
                    };
                }
                rows.Add(record);
            }
        }
        return data;
    }
}
