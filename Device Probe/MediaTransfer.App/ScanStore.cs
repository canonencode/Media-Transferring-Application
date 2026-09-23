using System.Text.Json;
using MediaTransfer.Core;
using Microsoft.Data.Sqlite;

namespace MediaTransfer.App;

/// <summary>
/// Reads scans back out of the database the scanner writes.
///
/// Opened read-only, and that is load-bearing rather than cautious: the scanner
/// may be running right now, and a second writer on the same file would block
/// it. WAL lets a reader see committed rows while a scan is still going, which
/// is what makes the progress display possible without inventing a channel
/// between the two processes.
/// </summary>
public sealed class ScanStore(string databasePath)
{
    public string DatabasePath { get; } = databasePath;

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public bool Exists() => File.Exists(DatabasePath);

    /// <summary>
    /// The scan the window should show: the newest one that finished. A scan
    /// still marked 'running' is deliberately not chosen - its rows are real but
    /// incomplete, and showing them as the current picture is the mistake this
    /// whole project is built to avoid.
    /// </summary>
    public long? LatestFinishedScanId()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT scan_id FROM scan WHERE status <> 'running' ORDER BY scan_id DESC LIMIT 1;";
        return q.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Rows written so far by a scan that is still going, for the progress line.</summary>
    public (long ScanId, int Files, int Folders)? RunningScan()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT scan_id FROM scan WHERE status = 'running' ORDER BY scan_id DESC LIMIT 1;";
        if (q.ExecuteScalar() is not long id) return null;

        using var counts = c.CreateCommand();
        counts.CommandText =
            "SELECT (SELECT COUNT(*) FROM file WHERE scan_id = $s), (SELECT COUNT(*) FROM folder WHERE scan_id = $s);";
        counts.Parameters.AddWithValue("$s", id);
        using var r = counts.ExecuteReader();
        r.Read();
        return (id, r.GetInt32(0), r.GetInt32(1));
    }

    /// <summary>Everything the window needs for one scan, shaped for the page.</summary>
    public string PayloadJson(long scanId)
    {
        using var c = Open();

        var agg = new Dictionary<string, Bucket>();
        var labels = new Dictionary<string, string>();
        var files = new List<object>();

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT path, name, size, kind, modified_raw FROM file WHERE scan_id = $s;";
            q.Parameters.AddWithValue("$s", scanId);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                string path = r.GetString(0);
                string name = r.GetString(1);
                long? size = r.IsDBNull(2) ? null : r.GetInt64(2);
                string kind = r.GetString(3);
                string modified = r.IsDBNull(4) ? "" : r.GetString(4);

                var (id, label) = MediaSource.Classify(path);
                labels[id] = label;

                if (!agg.TryGetValue(id, out Bucket? b)) agg[id] = b = new Bucket();
                if (kind == "MediaFile") b.media++;
                else if (kind == "Document") b.doc++;
                else b.other++;
                b.bytes += size ?? 0;

                // Unknown files are counted above but not listed. They are app
                // data the walk examined and set aside; keeping them in the
                // total is what makes "13,466 of 13,791" an honest sentence,
                // and leaving them out of the list is what makes the list
                // readable.
                if (kind is "MediaFile" or "Document")
                {
                    files.Add(new
                    {
                        n = name,
                        p = StripStorageRoot(path),
                        s = size ?? 0,
                        k = kind,
                        d = modified.Length >= 10 ? modified[..10] : modified,
                        src = id,
                    });
                }
            }
        }

        var payload = new
        {
            device = Row(c, "SELECT * FROM device LIMIT 1;"),
            scans = Rows(c, """
                SELECT scan_id, status, started_utc, finished_utc, media_files, documents,
                       total_files_seen, subtree_losses, stalled, completed,
                       signature_checks_run, caught_by_signature_only, file_property_misses,
                       hidden_subtrees, still_unreadable
                FROM scan ORDER BY scan_id DESC;
                """),
            skipped = Rows(c, "SELECT path, reason FROM skipped_folder WHERE scan_id = " + scanId + " ORDER BY path;"),
            agg = agg.ToDictionary(kv => kv.Key, kv => (object)new { media = kv.Value.media, doc = kv.Value.doc, other = kv.Value.other, bytes = kv.Value.bytes }),
            labels,
            files,
            scanId,
            totalRows = agg.Values.Sum(b => b.media + b.doc + b.other),
        };

        return JsonSerializer.Serialize(payload);
    }

    sealed class Bucket { public int media, doc, other; public long bytes; }

    /// <summary>
    /// Drops the storage name from a path. It is localised - "Dahili depolama",
    /// "Phone" and "Dahili depolama birimi" across three measured devices - so
    /// showing it tells the user nothing and costs a line of width.
    /// </summary>
    static string StripStorageRoot(string path)
    {
        int second = path.IndexOf('/', 1);
        return second > 0 ? path[second..] : path;
    }

    static Dictionary<string, object?>? Row(SqliteConnection c, string sql)
    {
        var rows = Rows(c, sql);
        return rows.Count > 0 ? rows[0] : null;
    }

    static List<Dictionary<string, object?>> Rows(SqliteConnection c, string sql)
    {
        using var q = c.CreateCommand();
        q.CommandText = sql;
        using var r = q.ExecuteReader();

        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++)
            {
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            }
            list.Add(row);
        }
        return list;
    }
}
