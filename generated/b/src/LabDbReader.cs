using System.Globalization;
using Npgsql;

namespace Adapter;

/// <summary>Reads whole tables from PostgreSQL. Column names are lower case, values are raw text.</summary>
public sealed class LabDbReader : ISourceReader
{
    private static readonly string[] TableNames = { "lb_pt", "lb_rslt", "lb_tst_codes" };
    private readonly string _connection;

    public LabDbReader(IConfiguration config)
    {
        _connection = config["LAB_DB_CONNECTION"] ?? new NpgsqlConnectionStringBuilder
        {
            Host = config["LAB_DB_HOST"] ?? "localhost",
            Port = int.TryParse(config["LAB_DB_PORT"], out var port) ? port : 5433,
            Database = config["LAB_DB_NAME"] ?? "labdb",
            Username = string.IsNullOrEmpty(config["LAB_DB_USER"]) ? "labuser" : config["LAB_DB_USER"],
            Password = string.IsNullOrEmpty(config["LAB_DB_PASSWORD"]) ? "labpass" : config["LAB_DB_PASSWORD"],
        }.ConnectionString;
    }

    public async Task<SourceData> ReadAsync(CancellationToken ct = default)
    {
        var data = new SourceData();
        await using var conn = new NpgsqlConnection(_connection);
        await conn.OpenAsync(ct);
        foreach (var table in TableNames)
        {
            var rows = new List<SourceRecord>();
            await using var cmd = new NpgsqlCommand($"SELECT * FROM {table}", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var record = new SourceRecord();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    record[reader.GetName(i).ToLowerInvariant()] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
                rows.Add(record);
            }
            data.Tables[table] = rows;
        }
        return data;
    }
}
