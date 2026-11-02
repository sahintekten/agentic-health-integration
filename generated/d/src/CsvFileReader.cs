using System.Text;

namespace Adapter;

/// <summary>Reads every .csv file in the export folder. Table name: "rows".</summary>
/// <remarks>
/// Files are read oldest first (sorted by name) with encoding windows-1252 and delimiter ';'.
/// Keys are the header names. "_file" holds the file name. Lines with fewer fields than the
/// header (like footers) are skipped. Empty values become null.
/// </remarks>
public sealed class CsvFileReader : ISourceReader
{
    private const char Delimiter = ';';
    private readonly string _folder;

    public CsvFileReader(IConfiguration config)
    {
        _folder = config["EXPORT_DIR"] ?? FindExport();
    }

    public async Task<SourceData> ReadAsync(CancellationToken ct = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding("windows-1252");
        var rows = new List<SourceRecord>();
        foreach (var file in Directory.GetFiles(_folder, "*.csv").OrderBy(f => f, StringComparer.Ordinal))
        {
            var lines = (await File.ReadAllTextAsync(file, encoding, ct)).Split('\n');
            var header = Split(lines[0].TrimEnd('\r')).Where(h => h.Length > 0).ToList();
            foreach (var line in lines.Skip(1))
            {
                var parts = Split(line.TrimEnd('\r'));
                if (parts.Count < header.Count)
                    continue;
                var record = new SourceRecord();
                for (var i = 0; i < header.Count; i++)
                    record[header[i]] = parts[i].Length == 0 ? null : parts[i];
                record["_file"] = Path.GetFileName(file);
                rows.Add(record);
            }
        }
        var data = new SourceData();
        data.Tables["rows"] = rows;
        return data;
    }

    private static List<string> Split(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
                quoted = !quoted;
            else if (c == Delimiter && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static string FindExport()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "mock-systems", "appointments-export", "out");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return "export";
    }
}
