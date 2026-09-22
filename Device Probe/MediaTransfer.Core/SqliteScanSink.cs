using Microsoft.Data.Sqlite;

namespace MediaTransfer.Core;

/// <summary>
/// Writes a scan into SQLite as it happens. A sibling of
/// <see cref="ConsoleScanSink"/>, not a replacement for it: both see the same
/// events, so what is stored and what is printed cannot drift apart.
///
/// The design question this class answers is "what does a half-finished scan
/// look like in the database". The answer is: a scan row with status
/// 'running'. It is written before the first file and only becomes 'complete'
/// or 'partial' when the walk actually ends, so a scan killed by the watchdog,
/// a pulled cable or a power cut leaves a row that says so. Nothing else in
/// this project may assume a scan finished; the whole reason the project exists
/// is that an interrupted transfer which LOOKS finished is how files get lost.
///
/// That promise has two holes today, both known and unfixed - findings A1 and
/// A2 in docs/INCELEME-SQLITE-2026-09-22.md. OnScanFinished does not close the
/// sink, so files reported afterwards are still written INTO a row that already
/// says 'complete': a real Process.Kill run recorded 2,600 files reported,
/// 2,000 rows on disk and total_files_seen saying 100, all under status
/// 'complete'. And a second OnScanFinished rewrites the status, so it can
/// promote a 'partial' scan to 'complete'. The scanner guards the second case
/// with Interlocked; this class should not be relying on its caller for that.
/// </summary>
public sealed class SqliteScanSink : IScanSink, IDisposable
{
    // Rows are written inside a transaction because 14,000 individual commits
    // take minutes - the disk syncs on every one. Committing periodically
    // rather than once at the end is the trade: a hard kill loses at most this
    // many rows instead of the entire scan, and the scan row still says
    // 'running', so the gap is never mistaken for a complete census.
    const int RowsPerTransaction = 1000;

    /// <summary>
    /// Scans kept per device before the oldest are deleted. Every scan stores a
    /// full snapshot - 13,791 rows and roughly 4.3 MB on the phone this was
    /// built against - so without a limit a daily scan costs about 1.5 GB a
    /// year to record a handful of changed files.
    ///
    /// Ten is enough to see a week of history and to compare a suspicious scan
    /// against several earlier ones. Pass 0 to keep everything.
    /// </summary>
    public const int DefaultRetainedScansPerDevice = 10;

    readonly SqliteConnection connection;
    readonly int retainedScansPerDevice;
    string? deviceKey;
    SqliteTransaction? transaction;
    SqliteCommand? insertFile;
    SqliteCommand? insertFolder;
    SqliteCommand? insertSkipped;
    SqliteCommand? insertError;
    int rowsSinceCommit;
    RetryOutcome? retry;

    /// <summary>Where scans are recorded when no other path is given.</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaTransfer", "scans.db");

    public string DatabasePath { get; }

    /// <summary>The row this scan is being written to, or 0 before it starts.</summary>
    public long ScanId { get; private set; }

    /// <summary>
    /// Scans deleted by the retention rule when this one finished. Empty when
    /// nothing was old enough. Reported rather than returned quietly: this is
    /// the user's own scan history being removed, and housekeeping that happens
    /// invisibly is how a tool loses data nobody asked it to lose.
    /// </summary>
    public IReadOnlyList<long> PrunedScanIds => prunedScanIds;
    readonly List<long> prunedScanIds = new();

    public SqliteScanSink(string? databasePath = null, int retainedScansPerDevice = DefaultRetainedScansPerDevice)
    {
        DatabasePath = databasePath ?? DefaultDatabasePath;
        this.retainedScansPerDevice = retainedScansPerDevice;

        string? folder = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        // The four statements below run on an already-open connection, and if
        // any of them throws, the constructor escapes without closing it -
        // finding B5, known and not yet fixed. Throwing is the intended
        // behaviour (Program.cs catches it and scans without recording), but
        // the point of throwing is to let the caller fall back, and a leaked
        // connection keeps the file locked until GC, so retrying the same path
        // fails too.

        // WAL so a reader (a UI, later) can look at previous scans while this
        // one is still writing. NORMAL rather than FULL because a scan is
        // reproducible - rerunning it costs seconds, and the scan row's status
        // already marks anything that did not finish.
        Run("PRAGMA journal_mode=WAL;");
        Run("PRAGMA synchronous=NORMAL;");
        Run("PRAGMA foreign_keys=ON;");
        Run(Schema);
    }

    void Run(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Enums are stored as TEXT, not as their numeric values. Reordering a C#
    // enum is a silent, compiling change that would reinterpret every row
    // already written - 'MediaFile' cannot be misread that way.
    //
    // file has no UNIQUE constraint on (scan_id, path). An upsert there would
    // collapse two genuinely different objects that happen to share a path into
    // one row, and silently dropping a file is precisely the failure this
    // project exists to prevent. Duplicate objects are already prevented
    // upstream by the walk; anything that still arrives twice is a real device
    // oddity worth keeping.
    const string Schema = """
        CREATE TABLE IF NOT EXISTS device (
            device_key      TEXT PRIMARY KEY,
            key_is_fallback INTEGER NOT NULL,
            wpd_id          TEXT NOT NULL,
            friendly_name   TEXT NOT NULL,
            serial          TEXT,
            manufacturer    TEXT,
            model           TEXT,
            first_seen_utc  TEXT NOT NULL,
            last_seen_utc   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS scan (
            scan_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            device_key   TEXT NOT NULL REFERENCES device(device_key),
            started_utc  TEXT NOT NULL,
            finished_utc TEXT,
            status       TEXT NOT NULL,
            camera_mode  INTEGER NOT NULL,
            completed INTEGER, stalled INTEGER, faulted INTEGER,
            media_files INTEGER, documents INTEGER, undetermined_files INTEGER,
            total_files_seen INTEGER, subtree_losses INTEGER,
            signature_checks_run INTEGER, caught_by_signature_only INTEGER,
            signature_check_errors INTEGER, files_skipped_by_breaker INTEGER,
            signature_checking_disabled INTEGER, file_property_misses INTEGER,
            retry_ran INTEGER, retry_skip_reason TEXT, retry_attempted INTEGER,
            recovered_files INTEGER, recovered_folders INTEGER,
            still_unreadable INTEGER, hidden_subtrees INTEGER, retry_new_failures INTEGER
        );

        CREATE TABLE IF NOT EXISTS file (
            file_id       INTEGER PRIMARY KEY AUTOINCREMENT,
            scan_id       INTEGER NOT NULL REFERENCES scan(scan_id),
            path          TEXT NOT NULL,
            name          TEXT NOT NULL,
            object_id     TEXT NOT NULL,
            persistent_id TEXT,
            size          INTEGER,
            modified_raw  TEXT,
            kind          TEXT NOT NULL,
            recovered     INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS folder (
            folder_id     INTEGER PRIMARY KEY AUTOINCREMENT,
            scan_id       INTEGER NOT NULL REFERENCES scan(scan_id),
            path          TEXT NOT NULL,
            name          TEXT NOT NULL,
            object_id     TEXT NOT NULL,
            persistent_id TEXT,
            recovered     INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS skipped_folder (
            scan_id INTEGER NOT NULL REFERENCES scan(scan_id),
            path    TEXT NOT NULL,
            reason  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS scan_error (
            scan_id     INTEGER NOT NULL REFERENCES scan(scan_id),
            object_id   TEXT NOT NULL,
            parent_path TEXT NOT NULL,
            stage       TEXT NOT NULL,
            hresult     INTEGER NOT NULL,
            message     TEXT NOT NULL
        );

        -- MISSING: scan_root. Nothing records WHICH storage roots a scan
        -- covered, so a phone scanned with its SD card removed completes
        -- cleanly and every file on that card later reads as deleted. The J7
        -- exposes three storage objects, so this is a real configuration, not
        -- a hypothetical. Finding A6; it also fixes the localized-storage-name
        -- problem, where changing the phone's language renames every path.
        --
        -- MISSING: PRAGMA user_version. CREATE TABLE IF NOT EXISTS silently
        -- does nothing to an existing database, so adding a column here would
        -- leave older files on the old schema and then fail at INSERT time on
        -- a user's machine. No test can catch it - every test opens a fresh
        -- file. Finding E1, and the one item whose cost grows while it waits.

        CREATE INDEX IF NOT EXISTS ix_file_scan_path ON file(scan_id, path);
        CREATE INDEX IF NOT EXISTS ix_file_scan_kind ON file(scan_id, kind);
        CREATE INDEX IF NOT EXISTS ix_folder_scan_path ON folder(scan_id, path);
        CREATE INDEX IF NOT EXISTS ix_scan_device ON scan(device_key, started_utc);

        -- SQLite does not index a foreign key for you, and these two are read
        -- and deleted per scan. Without them, pruning one old scan scans both
        -- tables end to end. (CREATE INDEX IF NOT EXISTS does apply to an
        -- existing database, unlike an added column - see the user_version note
        -- above for the case that does not.)
        CREATE INDEX IF NOT EXISTS ix_scan_error_scan ON scan_error(scan_id);
        CREATE INDEX IF NOT EXISTS ix_skipped_folder_scan ON skipped_folder(scan_id);
        """;

    public void OnScanStarted(DeviceIdentity device, bool cameraMode)
    {
        deviceKey = device.DeviceKey;
        string now = Timestamp();

        // first_seen_utc is preserved on conflict; last_seen_utc is not. The
        // device row is the phone's identity, not this scan's - overwriting the
        // first sighting would erase the only record of how long it has been
        // known.
        using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO device (device_key, key_is_fallback, wpd_id, friendly_name,
                                    serial, manufacturer, model, first_seen_utc, last_seen_utc)
                VALUES ($key, $fallback, $wpd, $name, $serial, $manufacturer, $model, $now, $now)
                ON CONFLICT(device_key) DO UPDATE SET
                    wpd_id = excluded.wpd_id,
                    friendly_name = excluded.friendly_name,
                    serial = excluded.serial,
                    manufacturer = excluded.manufacturer,
                    model = excluded.model,
                    last_seen_utc = excluded.last_seen_utc;
                """;
            upsert.Parameters.AddWithValue("$key", device.DeviceKey);
            upsert.Parameters.AddWithValue("$fallback", device.KeyIsFallback ? 1 : 0);
            upsert.Parameters.AddWithValue("$wpd", device.WpdId);
            upsert.Parameters.AddWithValue("$name", device.FriendlyName);
            upsert.Parameters.AddWithValue("$serial", Nullable(device.SerialNumber));
            upsert.Parameters.AddWithValue("$manufacturer", Nullable(device.Manufacturer));
            upsert.Parameters.AddWithValue("$model", Nullable(device.Model));
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO scan (device_key, started_utc, status, camera_mode)
                VALUES ($key, $now, 'running', $camera);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$key", device.DeviceKey);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$camera", cameraMode ? 1 : 0);
            ScanId = (long)(insert.ExecuteScalar() ?? 0L);
        }

        PrepareStatements();
        BeginBatch();
    }

    void PrepareStatements()
    {
        insertFile = connection.CreateCommand();
        insertFile.CommandText = """
            INSERT INTO file (scan_id, path, name, object_id, persistent_id, size, modified_raw, kind, recovered)
            VALUES ($scan, $path, $name, $object, $persistent, $size, $modified, $kind, $recovered);
            """;
        foreach (string p in new[] { "$scan", "$path", "$name", "$object", "$persistent", "$size", "$modified", "$kind", "$recovered" })
        {
            insertFile.Parameters.Add(new SqliteParameter(p, null));
        }

        insertFolder = connection.CreateCommand();
        insertFolder.CommandText = """
            INSERT INTO folder (scan_id, path, name, object_id, persistent_id, recovered)
            VALUES ($scan, $path, $name, $object, $persistent, $recovered);
            """;
        foreach (string p in new[] { "$scan", "$path", "$name", "$object", "$persistent", "$recovered" })
        {
            insertFolder.Parameters.Add(new SqliteParameter(p, null));
        }

        insertSkipped = connection.CreateCommand();
        insertSkipped.CommandText = "INSERT INTO skipped_folder (scan_id, path, reason) VALUES ($scan, $path, $reason);";
        foreach (string p in new[] { "$scan", "$path", "$reason" })
        {
            insertSkipped.Parameters.Add(new SqliteParameter(p, null));
        }

        insertError = connection.CreateCommand();
        insertError.CommandText = """
            INSERT INTO scan_error (scan_id, object_id, parent_path, stage, hresult, message)
            VALUES ($scan, $object, $parent, $stage, $hresult, $message);
            """;
        foreach (string p in new[] { "$scan", "$object", "$parent", "$stage", "$hresult", "$message" })
        {
            insertError.Parameters.Add(new SqliteParameter(p, null));
        }
    }

    public void OnFile(DeviceObject obj, string path, FileKind kind, bool recovered)
    {
        var command = insertFile ?? throw NotStarted();
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$path"].Value = path;
        command.Parameters["$name"].Value = obj.Name;
        command.Parameters["$object"].Value = obj.ObjectId;
        command.Parameters["$persistent"].Value = Nullable(obj.PersistentId);
        // BUG, known and not yet fixed - finding B4. Size is a ulong and this
        // cast is unchecked, so a value above long.MaxValue wraps to a negative
        // number. The realistic trigger is not a 9-exabyte file but an MTP
        // stack reporting 0xFFFFFFFFFFFFFFFF as "size unknown", which lands
        // here as -1 and reads back as a measured fact. ScanTypes says exactly
        // why that is forbidden: a fabricated value passes for a measured one.
        // DBNull is the honest answer.
        command.Parameters["$size"].Value = obj.Size is { } size ? (long)size : DBNull.Value;
        command.Parameters["$modified"].Value = Nullable(obj.ModifiedRaw);
        command.Parameters["$kind"].Value = kind.ToString();
        command.Parameters["$recovered"].Value = recovered ? 1 : 0;
        command.ExecuteNonQuery();
        CountRow();
    }

    public void OnFolder(DeviceObject obj, string path, bool recovered)
    {
        var command = insertFolder ?? throw NotStarted();
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$path"].Value = path;
        command.Parameters["$name"].Value = obj.Name;
        command.Parameters["$object"].Value = obj.ObjectId;
        command.Parameters["$persistent"].Value = Nullable(obj.PersistentId);
        command.Parameters["$recovered"].Value = recovered ? 1 : 0;
        command.ExecuteNonQuery();
        CountRow();
    }

    public void OnFolderSkipped(string path, string reason)
    {
        var command = insertSkipped ?? throw NotStarted();
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$path"].Value = path;
        command.Parameters["$reason"].Value = reason;
        command.ExecuteNonQuery();
        CountRow();
    }

    public void OnError(ScanError error)
    {
        var command = insertError ?? throw NotStarted();
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$object"].Value = error.ObjectId;
        command.Parameters["$parent"].Value = error.ParentPath;
        command.Parameters["$stage"].Value = error.Stage.ToString();
        command.Parameters["$hresult"].Value = error.HResult;
        command.Parameters["$message"].Value = error.Message;
        command.ExecuteNonQuery();
        CountRow();
    }

    // The retry pass reports its result before the scan ends, but it belongs on
    // the scan row, which is only written once at the end. Held here rather
    // than issued as an extra UPDATE: if the process dies in between, the row
    // stays 'running' and no retry figures are claimed for a scan that never
    // finished.
    public void OnRetryStarted(int objectCount) { }

    public void OnRetryFinished(RetryOutcome outcome) => retry = outcome;

    public void OnScanFinished(ScanOutcome outcome)
    {
        Commit();

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE scan SET
                finished_utc = $finished,
                status = $status,
                completed = $completed, stalled = $stalled, faulted = $faulted,
                media_files = $media, documents = $documents,
                undetermined_files = $undetermined, total_files_seen = $total,
                subtree_losses = $losses,
                signature_checks_run = $sigRun, caught_by_signature_only = $sigOnly,
                signature_check_errors = $sigErrors, files_skipped_by_breaker = $breakerSkips,
                signature_checking_disabled = $breakerTripped, file_property_misses = $propMisses,
                retry_ran = $retryRan, retry_skip_reason = $retrySkip, retry_attempted = $retryAttempted,
                recovered_files = $recoveredFiles, recovered_folders = $recoveredFolders,
                still_unreadable = $stillUnreadable, hidden_subtrees = $hiddenSubtrees,
                retry_new_failures = $retryNewFailures
            WHERE scan_id = $scan;
            """;
        update.Parameters.AddWithValue("$finished", Timestamp());
        // 'complete' is written only when nothing at all casts doubt on the
        // census - the same rule the console prints COMPLETE under. Anything
        // else is 'partial', and a later comparison against this scan must
        // treat 'partial' as "files may be missing", never as "these files are
        // gone from the phone".
        update.Parameters.AddWithValue("$status", outcome.IsTrustworthy ? "complete" : "partial");
        update.Parameters.AddWithValue("$completed", outcome.Completed ? 1 : 0);
        update.Parameters.AddWithValue("$stalled", outcome.Stalled ? 1 : 0);
        update.Parameters.AddWithValue("$faulted", outcome.Faulted ? 1 : 0);
        update.Parameters.AddWithValue("$media", outcome.MediaFiles);
        update.Parameters.AddWithValue("$documents", outcome.Documents);
        update.Parameters.AddWithValue("$undetermined", outcome.UndeterminedFiles);
        update.Parameters.AddWithValue("$total", outcome.TotalFilesSeen);
        update.Parameters.AddWithValue("$losses", outcome.SubtreeLosses);
        update.Parameters.AddWithValue("$sigRun", outcome.SignatureChecksRun);
        update.Parameters.AddWithValue("$sigOnly", outcome.CaughtBySignatureOnly);
        update.Parameters.AddWithValue("$sigErrors", outcome.SignatureCheckErrors);
        update.Parameters.AddWithValue("$breakerSkips", outcome.FilesSkippedByBreaker);
        update.Parameters.AddWithValue("$breakerTripped", outcome.SignatureCheckingDisabled ? 1 : 0);
        update.Parameters.AddWithValue("$propMisses", outcome.FilePropertyMisses);
        update.Parameters.AddWithValue("$retryRan", retry is null ? DBNull.Value : (retry.Skipped ? 0 : 1));
        update.Parameters.AddWithValue("$retrySkip", Nullable(retry?.SkipReason));
        update.Parameters.AddWithValue("$retryAttempted", (object?)retry?.Attempted ?? DBNull.Value);
        update.Parameters.AddWithValue("$recoveredFiles", (object?)retry?.RecoveredFiles ?? DBNull.Value);
        update.Parameters.AddWithValue("$recoveredFolders", (object?)retry?.RecoveredFolders ?? DBNull.Value);
        update.Parameters.AddWithValue("$stillUnreadable", (object?)retry?.StillUnreadable ?? DBNull.Value);
        update.Parameters.AddWithValue("$hiddenSubtrees", (object?)retry?.HiddenSubtrees ?? DBNull.Value);
        update.Parameters.AddWithValue("$retryNewFailures", (object?)retry?.NewFailures ?? DBNull.Value);
        update.Parameters.AddWithValue("$scan", ScanId);

        // BUG, known and not yet fixed - finding B1. The return value is
        // dropped. If ScanId is 0 - OnScanFinished without OnScanStarted, or
        // ExecuteScalar having come back null - this matches no rows, returns
        // 0 and throws nothing, so a caller sees a scan run start to finish
        // while the database holds nothing at all. OnFile guards that case
        // loudly; the call that can least afford to fail quietly is the one
        // that does. Treating a row count other than 1 as an error fixes it.
        update.ExecuteNonQuery();

        Prune();
    }

    /// <summary>
    /// Deletes this device's oldest scans, keeping the most recent
    /// <see cref="retainedScansPerDevice"/> of them.
    ///
    /// Runs after the status update, not before it, so the scan that just
    /// finished is already one of the ones being counted as recent - otherwise
    /// a retention of 1 would delete the scan it was called from.
    ///
    /// Three things it will not delete, each for a reason that cost something
    /// to learn:
    ///
    /// - A scan still marked 'running'. Another process may be writing it right
    ///   now; its rows are not history, they are in flight. (This does mean a
    ///   scan killed mid-walk is never pruned, because nothing yet marks a
    ///   stale 'running' row abandoned - finding E3.)
    /// - The newest 'complete' scan, even when it falls outside the retained
    ///   window. A phone that keeps wedging produces a run of partial scans,
    ///   and counting alone would quietly delete the last full census of the
    ///   device - which is the one row everything downstream depends on.
    /// - Anything at all when retention is zero or negative, which means keep
    ///   everything.
    ///
    /// Another device's scans are never touched: the window is per device, so
    /// scanning one phone ten times cannot evict another phone's history.
    /// </summary>
    void Prune()
    {
        prunedScanIds.Clear();
        if (retainedScansPerDevice <= 0 || deviceKey is null) return;

        using var doomed = connection.CreateCommand();
        doomed.CommandText = """
            SELECT scan_id FROM scan
            WHERE device_key = $key
              AND status <> 'running'
              AND scan_id NOT IN (
                  SELECT scan_id FROM scan
                  WHERE device_key = $key AND status <> 'running'
                  ORDER BY scan_id DESC LIMIT $keep)
              AND scan_id IS NOT (
                  SELECT MAX(scan_id) FROM scan
                  WHERE device_key = $key AND status = 'complete')
            ORDER BY scan_id;
            """;
        doomed.Parameters.AddWithValue("$key", deviceKey);
        doomed.Parameters.AddWithValue("$keep", retainedScansPerDevice);

        var ids = new List<long>();
        using (var reader = doomed.ExecuteReader())
        {
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        if (ids.Count == 0) return;

        // One transaction for the whole sweep: a half-pruned scan would leave
        // file rows pointing at a scan row that no longer exists, which is the
        // one shape of corruption the foreign key is there to prevent.
        using var sweep = connection.BeginTransaction();
        foreach (long id in ids)
        {
            // Children before the parent, or the foreign key refuses.
            foreach (string table in new[] { "file", "folder", "skipped_folder", "scan_error", "scan" })
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = sweep;
                delete.CommandText = $"DELETE FROM {table} WHERE scan_id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
        }
        sweep.Commit();
        prunedScanIds.AddRange(ids);
    }

    void BeginBatch()
    {
        transaction = connection.BeginTransaction();
        rowsSinceCommit = 0;
        AttachTransaction();
    }

    // Every prepared command has to be re-pointed at the new transaction after
    // each commit; Microsoft.Data.Sqlite rejects a command whose Transaction is
    // a completed one.
    void AttachTransaction()
    {
        if (insertFile is not null) insertFile.Transaction = transaction;
        if (insertFolder is not null) insertFolder.Transaction = transaction;
        if (insertSkipped is not null) insertSkipped.Transaction = transaction;
        if (insertError is not null) insertError.Transaction = transaction;
    }

    void CountRow()
    {
        if (++rowsSinceCommit < RowsPerTransaction) return;
        Commit();
        BeginBatch();
    }

    void Commit()
    {
        if (transaction is null) return;
        transaction.Commit();
        transaction.Dispose();
        transaction = null;
        AttachTransaction();
    }

    static object Nullable(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    // Meant to be sortable and unambiguous, because started_utc/finished_utc are
    // TEXT and sort lexically, and that matters the moment two scans are
    // compared to work out what was added or removed.
    //
    // BUG, known and not yet fixed - finding A5 in
    // docs/INCELEME-SQLITE-2026-09-22.md. There is no CultureInfo here, so the
    // format follows the machine's region setting: ar-SA writes a Hijri year
    // (1448), th-TH a Buddhist one (2569). Those sort BEFORE every Gregorian
    // row, so "the newest scan" resolves to the oldest and a stale census gets
    // compared against the live phone. A Windows region setting is enough to
    // trigger it. The fix is CultureInfo.InvariantCulture, plus a Z suffix so
    // the value says out loud that it is UTC.
    static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");

    static InvalidOperationException NotStarted() =>
        new("OnScanStarted must be called before any scan events are recorded.");

    /// <summary>
    /// Commits whatever the scan got through and closes the file. A scan that
    /// never reached OnScanFinished keeps its 'running' status on purpose: the
    /// rows are real and worth keeping, but nothing may treat them as a
    /// complete picture of the device.
    ///
    /// Two known defects here - findings B2 and B3 in
    /// docs/INCELEME-SQLITE-2026-09-22.md. The catch below discards up to
    /// RowsPerTransaction rows without a word, and there is no finally, so a
    /// Commit that throws anything other than SqliteException skips every
    /// Dispose beneath it and leaves the file handle and the SQLite lock held -
    /// which is the "next run finds a locked database" failure CompositeScanSink
    /// says it exists to prevent.
    /// </summary>
    public void Dispose()
    {
        // Not saving is defensible here: there is nowhere left to save it into,
        // and the scan row still says 'running', so nothing downstream can
        // mistake the gap for a census. Staying SILENT about it is not - every
        // other failure in this layer is announced. See finding B2.
        try { Commit(); }
        catch (SqliteException) { }

        insertFile?.Dispose();
        insertFolder?.Dispose();
        insertSkipped?.Dispose();
        insertError?.Dispose();
        transaction?.Dispose();
        connection.Dispose();
    }
}
