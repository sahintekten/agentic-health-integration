using System.Text;

namespace Adapter;

/// <summary>Reads every .hl7 file in the outbox folder. Table name: "messages".</summary>
public sealed class Hl7FileReader : ISourceReader
{
    private readonly string _outbox;

    public Hl7FileReader(IConfiguration config)
    {
        _outbox = config["ADT_OUTBOX"] ?? FindOutbox();
    }

    public async Task<SourceData> ReadAsync(CancellationToken ct = default)
    {
        var rows = new List<SourceRecord>();
        foreach (var file in Directory.GetFiles(_outbox, "*.hl7").OrderBy(f => f, StringComparer.Ordinal))
        {
            var record = Hl7Message.Flatten(await File.ReadAllTextAsync(file, Encoding.UTF8, ct));
            record["_file"] = Path.GetFileName(file);
            rows.Add(record);
        }
        var data = new SourceData();
        data.Tables["messages"] = rows;
        return data;
    }

    private static string FindOutbox()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "mock-systems", "adt-feed", "outbox");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return "outbox";
    }
}

/// <summary>Turns one HL7 v2 message into a flat record.</summary>
/// <remarks>
/// Keys look like "PID-5" for a whole field and "PID-5.1" for a component, with HL7 numbering
/// (so "MSH-9" is the message type). Escape sequences are decoded. Empty values become null.
/// </remarks>
public static class Hl7Message
{
    public static SourceRecord Flatten(string message)
    {
        var record = new SourceRecord();
        foreach (var segment in message.Split('\r', '\n').Where(s => s.Length > 3))
        {
            var fields = segment.Split('|');
            var name = fields[0];
            var offset = 0;
            if (name == "MSH")
            {
                record["MSH-1"] = "|";
                offset = 1;
            }
            for (var i = 1; i < fields.Length; i++)
            {
                var key = $"{name}-{i + offset}";
                if (record.ContainsKey(key))
                    continue;
                var value = fields[i];
                record[key] = value.Length == 0 ? null : Unescape(value);
                if (name == "MSH" && i == 1)
                    continue;
                if (value.Contains('^'))
                {
                    var parts = value.Split('~')[0].Split('^');
                    for (var c = 0; c < parts.Length; c++)
                        record[$"{key}.{c + 1}"] = parts[c].Length == 0 ? null : Unescape(parts[c]);
                }
            }
        }
        return record;
    }

    public static string Unescape(string value) => value
        .Replace(@"\F\", "|").Replace(@"\S\", "^").Replace(@"\T\", "&").Replace(@"\R\", "~").Replace(@"\E\", @"\");
}
