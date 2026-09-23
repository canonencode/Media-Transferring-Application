using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaTransfer.Core;

/// <summary>
/// The record of what has been taken off a phone.
///
/// It follows the scan row's discipline exactly, for the same reason. A copy
/// row is written as 'copying' BEFORE the stream is opened and only becomes
/// 'done' after the bytes are on disk, verified and renamed into place. Pull
/// the cable at any moment and what is left says so: a 'copying' row and a
/// .part file, neither of which anything will mistake for a finished copy.
///
/// That is the whole answer to the question this project started from. A
/// transfer that breaks halfway is not a problem as long as it can say where it
/// got to.
/// </summary>
public sealed class TransferLedger : IDisposable
{
    readonly SqliteConnection _connection;

    public TransferLedger(string databasePath)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Same three seconds as the scanner: long enough to ride out a
            // commit, short enough that a conflict is reported rather than
            // looking like a freeze.
            DefaultTimeout = 3,
            Pooling = false,
        }.ToString());
        _connection.Open();

        try
        {
            _connection.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            _connection.ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
            ScanSchema.Apply(_connection);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a row for a file about to be copied, and returns its id.
    ///
    /// Committed immediately rather than batched with the copy itself. A scan
    /// writes fourteen thousand rows and batching pays for itself; a transfer
    /// writes one row per file and takes seconds over each, so the moment a row
    /// costs is nothing against the certainty it buys.
    /// </summary>
    public long Begin(string deviceKey, TransferItem item, string destination)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO copy (device_key, source_path, source_name, source_size, source_modified,
                              object_id, destination, status, started_utc)
            VALUES ($device, $path, $name, $size, $modified, $object, $destination, 'copying', $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$device", deviceKey);
        command.Parameters.AddWithValue("$path", item.DevicePath);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$size", item.Size);
        command.Parameters.AddWithValue("$modified", (object?)item.ModifiedRaw ?? DBNull.Value);
        command.Parameters.AddWithValue("$object", item.ObjectId);
        command.Parameters.AddWithValue("$destination", destination);
        command.Parameters.AddWithValue("$now", Timestamp());
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Marks a copy finished. Called only after the file is on disk under its real name.</summary>
    public void Complete(long copyId, long bytes, string sha256)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE copy SET status = 'done', finished_utc = $now, bytes_copied = $bytes, sha256 = $hash
            WHERE copy_id = $id;
            """;
        command.Parameters.AddWithValue("$now", Timestamp());
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$hash", sha256);
        command.Parameters.AddWithValue("$id", copyId);

        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Kopya satırı {copyId} güncellenemedi. Kayıt kaybolmuş olabilir; bu dosya kopyalandı " +
                "sayılamaz.");
        }
    }

    /// <summary>
    /// Records a copy that failed, with the reason.
    ///
    /// Kept rather than deleted. A file that could not be taken is something the
    /// user needs to see - it is still on the phone, and a transfer that quietly
    /// forgets its failures is one that reports success while leaving things
    /// behind.
    /// </summary>
    public void Fail(long copyId, string error)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE copy SET status = 'failed', finished_utc = $now, error = $error WHERE copy_id = $id;
            """;
        command.Parameters.AddWithValue("$now", Timestamp());
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", copyId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The most recent record for a file, which is what a resumed transfer asks
    /// about. Most recent rather than "the done one", because a later failed or
    /// interrupted attempt means the earlier success can no longer be trusted:
    /// something went back to that file for a reason.
    /// </summary>
    public CopyRecord? Latest(string deviceKey, string sourcePath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, source_size, source_modified, destination, status, sha256
            FROM copy WHERE device_key = $device AND source_path = $path
            ORDER BY copy_id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device", deviceKey);
        command.Parameters.AddWithValue("$path", sourcePath);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new CopyRecord(
            SourcePath: reader.GetString(0),
            SourceSize: reader.IsDBNull(1) ? null : reader.GetInt64(1),
            SourceModified: reader.IsDBNull(2) ? null : reader.GetString(2),
            Destination: reader.GetString(3),
            Status: reader.GetString(4),
            Sha256: reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    /// <summary>
    /// Every remembered copy for a device, keyed by source path.
    ///
    /// Read in one query rather than one per file: a transfer asks about
    /// thirteen thousand of them before it starts, and thirteen thousand round
    /// trips to answer a question about a table with an index on exactly that
    /// column is work for nothing.
    /// </summary>
    public Dictionary<string, CopyRecord> AllFor(string deviceKey)
    {
        var map = new Dictionary<string, CopyRecord>(StringComparer.Ordinal);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, source_size, source_modified, destination, status, sha256
            FROM copy WHERE device_key = $device ORDER BY copy_id;
            """;
        command.Parameters.AddWithValue("$device", deviceKey);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Later rows overwrite earlier ones, which is what makes this the
            // same answer Latest gives for each path.
            map[reader.GetString(0)] = new CopyRecord(
                SourcePath: reader.GetString(0),
                SourceSize: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                SourceModified: reader.IsDBNull(2) ? null : reader.GetString(2),
                Destination: reader.GetString(3),
                Status: reader.GetString(4),
                Sha256: reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return map;
    }

    /// <param name="Unfinished">Rows still saying 'copying' - an interrupted transfer's footprint.</param>
    public readonly record struct LedgerCounts(int Done, int Failed, int Unfinished, long Bytes);

    public LedgerCounts Counts(string deviceKey)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
              SUM(status = 'done'), SUM(status = 'failed'), SUM(status = 'copying'),
              COALESCE(SUM(CASE WHEN status = 'done' THEN bytes_copied ELSE 0 END), 0)
            FROM copy WHERE device_key = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceKey);

        using var reader = command.ExecuteReader();
        reader.Read();
        return new LedgerCounts(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.GetInt64(3));
    }

    // Invariant, like the scanner's. A local-time string sorts wrongly across a
    // DST change, and the machine's region setting must not decide what a
    // recorded date means.
    static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public void Dispose() => _connection.Dispose();
}

static class SqliteConnectionExtensions
{
    public static void ExecuteNonQuery(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
