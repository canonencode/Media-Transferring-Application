using System.Globalization;
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

    /// <summary>
    /// What a scan in flight has produced so far.
    ///
    /// A count on its own answers none of the questions someone waiting
    /// actually has. The first scan of a device takes minutes - measured at 327
    /// seconds against 9 for a warm one, because the driver's cache is cold and
    /// every property read costs 20 ms instead of 0.2 - so the wait is long
    /// enough that "is it working" and "how much longer" both need answering.
    /// </summary>
    public ScanProgress? RunningScan()
    {
        using var c = Open();

        using var q = c.CreateCommand();
        q.CommandText = "SELECT scan_id, device_key, started_utc FROM scan WHERE status = 'running' ORDER BY scan_id DESC LIMIT 1;";
        using var head = q.ExecuteReader();
        if (!head.Read()) return null;

        long id = head.GetInt64(0);
        string deviceKey = head.GetString(1);
        string started = head.GetString(2);
        head.Close();

        using var counts = c.CreateCommand();
        counts.CommandText = """
            SELECT (SELECT COUNT(*) FROM file WHERE scan_id = $s),
                   (SELECT COUNT(*) FROM folder WHERE scan_id = $s),
                   (SELECT path FROM folder WHERE scan_id = $s ORDER BY folder_id DESC LIMIT 1),
                   (SELECT total_files_seen FROM scan
                     WHERE device_key = $d AND status = 'complete' AND scan_id <> $s
                     ORDER BY scan_id DESC LIMIT 1);
            """;
        counts.Parameters.AddWithValue("$s", id);
        counts.Parameters.AddWithValue("$d", deviceKey);
        using var r = counts.ExecuteReader();
        r.Read();

        double elapsed = 0;
        if (DateTime.TryParseExact(started, "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime startedAt))
        {
            elapsed = Math.Max(0, (DateTime.UtcNow - startedAt).TotalSeconds);
        }

        return new ScanProgress(
            ScanId: id,
            Files: r.GetInt32(0),
            Folders: r.GetInt32(1),
            CurrentFolder: r.IsDBNull(2) ? null : StripStorageRoot(r.GetString(2)),
            // An estimate, and only ever shown as one: it is what this device
            // held last time, and a phone gains and loses files between scans.
            // Better than no scale at all, which is what a bare count gives.
            Expected: r.IsDBNull(3) ? null : r.GetInt32(3),
            ElapsedSeconds: elapsed);
    }

    /// <summary>
    /// Files committed since the caller last asked, newest first.
    ///
    /// Fetched by id rather than by re-reading the scan, so the cost stays
    /// proportional to what arrived rather than to what has been found so far -
    /// the difference between a few rows and fourteen thousand, twice a second.
    /// </summary>
    public (List<object> Files, long LastId) NewFilesSince(long scanId, long afterFileId, int max)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT file_id, path, name, size, kind, modified_raw
            FROM file WHERE scan_id = $s AND file_id > $after AND kind IN ('MediaFile', 'Document')
            ORDER BY file_id DESC LIMIT $max;
            """;
        q.Parameters.AddWithValue("$s", scanId);
        q.Parameters.AddWithValue("$after", afterFileId);
        q.Parameters.AddWithValue("$max", max);

        var files = new List<object>();
        long last = afterFileId;
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            long fileId = r.GetInt64(0);
            string path = r.GetString(1);
            string modified = r.IsDBNull(5) ? "" : r.GetString(5);
            last = Math.Max(last, fileId);

            files.Add(new
            {
                n = r.GetString(2),
                p = StripStorageRoot(path),
                s = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                k = r.GetString(4),
                d = modified.Length >= 10 ? modified[..10] : modified,
                src = MediaSource.Classify(path).Id,
            });
        }
        return (files, last);
    }

    /// <summary>The phone's own name, used to suggest a folder to back it up into.</summary>
    public string? DeviceName()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT friendly_name FROM device ORDER BY last_seen_utc DESC LIMIT 1;";
        return q.ExecuteScalar() as string;
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
            // SELECT * rather than a column list, and that is the whole
            // schema-compatibility story for the reader.
            //
            // The writer owns the schema and upgrades the file when it opens
            // it; this side only reads, so it can meet a database older than
            // the build - one written before a column existed, on a machine
            // where no scan has run since. A fixed list fails outright on such
            // a file ("no such column"), which is a blank window instead of the
            // scans it does have. Asking for whatever is there leaves the page
            // to check for the fields it wants, and it already does.
            scans = Rows(c, "SELECT * FROM scan ORDER BY scan_id DESC;"),
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

/// <param name="Expected">Files the last complete scan of this device saw, or null on a first scan.</param>
/// <param name="CurrentFolder">The most recent folder the walk entered.</param>
public readonly record struct ScanProgress(
    long ScanId, int Files, int Folders, string? CurrentFolder, int? Expected, double ElapsedSeconds);
