using Microsoft.Data.Sqlite;

namespace Gateway;

/// <summary>Keeps the patient index in a small SQLite file.</summary>
public sealed class LinkStore
{
    private readonly string _connection;

    public LinkStore(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS links (
                system TEXT NOT NULL,
                source_id TEXT NOT NULL,
                person_id TEXT NOT NULL,
                link_type TEXT NOT NULL,
                status TEXT NOT NULL,
                reason TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (system, source_id))
            """);
    }

    public Dictionary<(string System, string SourceId), string> ReviewDecisions()
    {
        var result = new Dictionary<(string, string), string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT system, source_id, status FROM links WHERE link_type = 'probable' AND status <> 'pending'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        return result;
    }

    public void ReplaceAll(IEnumerable<Link> links)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM links";
            clear.ExecuteNonQuery();
        }
        var now = DateTime.UtcNow.ToString("o");
        foreach (var link in links)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO links VALUES ($s, $id, $p, $t, $st, $r, $u)";
            cmd.Parameters.AddWithValue("$s", link.System);
            cmd.Parameters.AddWithValue("$id", link.SourceId);
            cmd.Parameters.AddWithValue("$p", link.PersonId);
            cmd.Parameters.AddWithValue("$t", link.LinkType);
            cmd.Parameters.AddWithValue("$st", link.Status);
            cmd.Parameters.AddWithValue("$r", link.Reason);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<Link> All()
    {
        var links = new List<Link>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT system, source_id, person_id, link_type, status, reason FROM links ORDER BY system, source_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new Link(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return links;
    }

    public bool SetStatus(string system, string sourceId, string status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE links SET status = $st, updated_at = $u " +
                          "WHERE system = $s AND source_id = $id AND link_type = 'probable'";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$s", system);
        cmd.Parameters.AddWithValue("$id", sourceId);
        return cmd.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connection);
        conn.Open();
        return conn;
    }

    private void Execute(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
