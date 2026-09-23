using System.Globalization;
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
/// The sink closes when the walk ends. Events reported afterwards are refused,
/// and so is a second OnScanFinished - both used to be accepted, and both broke
/// the promise above. A real Process.Kill run once recorded 2,600 files
/// reported, 2,000 rows on disk and total_files_seen saying 100, all under
/// status 'complete'; a second finish call could rewrite a 'partial' scan as a
/// complete census. The scanner also guards the second case with Interlocked,
/// but a class whose whole job is to be trustworthy cannot depend on its caller
/// for that.
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
    bool finished;

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
        string requested = databasePath ?? DefaultDatabasePath;

        // ":memory:" and "file:..." are magic DataSource values for
        // Microsoft.Data.Sqlite, not paths. Passed through, ":memory:" gives a
        // sink that accepts an entire scan, reports no error and throws every
        // row away on Dispose - nothing in the caller's experience would say
        // the scan was never recorded. A caller-supplied path that means "do
        // not actually store anything" is refused outright.
        if (string.Equals(requested, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            requested.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{requested}' is a SQLite in-memory or URI data source, not a file path. " +
                "A scan recorded there would be discarded silently.", nameof(databasePath));
        }

        // Resolved once, so the folder created below and the file opened after
        // it cannot disagree, and so DatabasePath means the same thing to a
        // caller no matter what the working directory was.
        DatabasePath = Path.GetFullPath(requested);
        this.retainedScansPerDevice = retainedScansPerDevice;

        string? folder = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // SQLite allows one writer at a time, and this sink holds a write
            // transaction across a batch of rows, so a second scan writing to
            // the same database has to wait. The default wait is 30 seconds,
            // which a user reads as a freeze - and their obvious response,
            // unplug and try again, walks straight back into the same lock.
            // Three seconds is long enough to ride out a batch commit and short
            // enough to be reported as what it is.
            DefaultTimeout = 3,

            // Pooling returns a disposed connection to a cache instead of
            // closing it, so the database file stays open after a scan ends and
            // after a constructor fails - which is precisely the "the fallback
            // hits a locked file too" problem the dispose-on-failure below
            // exists to prevent. One sink holds one connection for the length of
            // one scan, so there is nothing for a pool to save here anyway.
            Pooling = false,
        }.ToString());
        connection.Open();

        // Everything past Open() runs on a connection this constructor owns,
        // and the point of throwing is to let the caller fall back to another
        // path or to scanning without a record. A leaked connection would hold
        // the file open until GC, so the fallback would hit a locked file and
        // fail too - the failure handling would become the failure.
        try
        {
            // WAL so a reader (a UI, later) can look at previous scans while
            // this one is still writing. NORMAL rather than FULL because a scan
            // is reproducible - rerunning it costs seconds, and the scan row's
            // status already marks anything that did not finish.
            Run("PRAGMA journal_mode=WAL;");
            Run("PRAGMA synchronous=NORMAL;");
            Run("PRAGMA foreign_keys=ON;");
            Migrate();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What this build knows how to write. Stored in the file's own header, so
    /// a database carries its shape with it.
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// Brings the file up to <see cref="SchemaVersion"/>, or refuses it.
    ///
    /// CREATE TABLE IF NOT EXISTS silently does nothing to a database that
    /// already has the table, so adding a column to the schema text would leave
    /// every existing file on the old shape and then fail at INSERT time on a
    /// user's machine. No test would catch it either, because tests open fresh
    /// files. That is what this replaces.
    ///
    /// The refusal matters more than the upgrade. An older build opening a
    /// newer database cannot know which columns it is failing to fill, and a
    /// scan written half-blind is worse than one not written at all.
    /// </summary>
    void Migrate()
    {
        long version = UserVersion();
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Bu veritabanı daha yeni bir sürümle yazılmış (şema {version}, bu sürüm {SchemaVersion}). " +
                "Eski bir sürümle yazmak, dolduramadığı alanları sessizce boş bırakır.");
        }

        // Version 0 is both a brand new file and every file written before
        // versioning existed. IF NOT EXISTS makes running the schema safe for
        // both.
        if (version == 0) Run(Schema);

        // 1 -> 2: unresolved_objects. Added because a scan marked partial for
        // that reason had no way to say so, and an unexplained "incomplete"
        // warning is one a user learns to ignore.
        if (version < 2) AddColumn("scan", "unresolved_objects", "INTEGER");

        Run($"PRAGMA user_version = {SchemaVersion};");
    }

    long UserVersion()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return command.ExecuteScalar() is long v ? v : 0;
    }

    /// <summary>Adds a column unless it is already there - ALTER TABLE throws on a repeat.</summary>
    void AddColumn(string table, string column, string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;

        Run($"ALTER TABLE {table} ADD COLUMN {column} {type};");
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
            unresolved_objects INTEGER,
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
        if (finished) throw AlreadyFinished();

        // Checked before anything is written. A second call used to insert its
        // scan row, commit it, then throw while rebuilding the statements -
        // leaving a 'running' row that no code path could ever finish, and a
        // ScanId pointing at the wrong one.
        if (ScanId != 0)
        {
            throw new InvalidOperationException(
                $"This sink is already recording scan {ScanId}. One sink records one scan; " +
                "create another for the next one.");
        }

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
            upsert.Parameters.AddWithValue("$serial", Nullable(device.TrimmedSerial));
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
        var command = Accepting(insertFile);
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$path"].Value = Announce(path, "file path");
        command.Parameters["$name"].Value = Announce(obj.Name, "file name");
        command.Parameters["$object"].Value = obj.ObjectId;
        command.Parameters["$persistent"].Value = Nullable(obj.PersistentId);
        // SQLite has no unsigned 64-bit integer, and an unchecked cast of a
        // value above long.MaxValue wraps to a negative one. The realistic
        // trigger is not a 9-exabyte file but an MTP stack reporting
        // 0xFFFFFFFFFFFFFFFF for "size unknown", which used to land here as -1
        // and read back as a measured fact. ScanTypes says exactly why that is
        // forbidden: a fabricated value passing for a measured one. A size we
        // cannot represent is a size the device did not usefully tell us, so it
        // is stored the same way as one it never gave at all.
        command.Parameters["$size"].Value =
            obj.Size is { } size && size <= long.MaxValue ? (long)size : DBNull.Value;
        command.Parameters["$modified"].Value = Nullable(obj.ModifiedRaw);
        command.Parameters["$kind"].Value = kind.ToString();
        command.Parameters["$recovered"].Value = recovered ? 1 : 0;
        command.ExecuteNonQuery();
        CountRow();
    }

    public void OnFolder(DeviceObject obj, string path, bool recovered)
    {
        var command = Accepting(insertFolder);
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$path"].Value = Announce(path, "folder path");
        command.Parameters["$name"].Value = Announce(obj.Name, "folder name");
        command.Parameters["$object"].Value = obj.ObjectId;
        command.Parameters["$persistent"].Value = Nullable(obj.PersistentId);
        command.Parameters["$recovered"].Value = recovered ? 1 : 0;
        command.ExecuteNonQuery();
        CountRow();
    }

    public void OnFolderSkipped(string path, string reason)
    {
        var command = Accepting(insertSkipped);
        command.Parameters["$scan"].Value = ScanId;
        command.Parameters["$path"].Value = path;
        command.Parameters["$reason"].Value = reason;
        command.ExecuteNonQuery();
        CountRow();
    }

    public void OnError(ScanError error)
    {
        var command = Accepting(insertError);
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
    public void OnRetryStarted(int objectCount)
    {
        if (finished) throw AlreadyFinished();
    }

    public void OnRetryFinished(RetryOutcome outcome)
    {
        // Deliberately does NOT require a started scan. The retry pass can
        // report that it was skipped on a walk that never got far enough to
        // open a scan row, and turning that into an exception would replace a
        // recoverable situation with a crash. It is held in a field; only
        // OnScanFinished writes it, and that call does demand a scan.
        if (finished) throw AlreadyFinished();
        retry = outcome;
    }

    public void OnScanFinished(ScanOutcome outcome)
    {
        // A second call would rewrite the status, and the later verdict would
        // win - so an outer finally calling this after an inner catch already
        // did could turn a scan recorded as cut short into a complete census.
        if (finished) throw AlreadyFinished();

        // Without a scan row there is nothing to update, and the UPDATE below
        // would match zero rows and return quietly. A caller whose
        // OnScanStarted was skipped would then watch a scan run start to finish
        // with no error while the database held nothing at all.
        if (ScanId == 0) throw NotStarted();

        Commit();

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE scan SET
                finished_utc = $finished,
                status = $status,
                completed = $completed, stalled = $stalled, faulted = $faulted,
                media_files = $media, documents = $documents,
                undetermined_files = $undetermined, total_files_seen = $total,
                subtree_losses = $losses, unresolved_objects = $unresolved,
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
        update.Parameters.AddWithValue("$unresolved", outcome.UnresolvedObjects);
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
        // Checked, because a silent no-op is the one answer this call must
        // never give: it is the moment a scan becomes readable as a census.
        int updated = update.ExecuteNonQuery();
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Finishing scan {ScanId} updated {updated} rows instead of 1. The scan row is " +
                "missing or duplicated, so this scan's status cannot be trusted.");
        }

        finished = true;
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
    ///
    /// The FILE does not shrink, and that is deliberate. SQLite keeps the pages
    /// a delete frees and reuses them for the next scan rather than handing
    /// them back to the filesystem; only VACUUM returns them, and VACUUM
    /// rewrites the whole database to reclaim space that the very next scan was
    /// going to fill anyway. So this caps growth rather than reversing it -
    /// measured on the phone this was built against, the database settles at
    /// about 45 MB for ten scans instead of climbing past a gigabyte in a year.
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

    /// <summary>
    /// True when the text contains a surrogate that has no partner. MTP names
    /// arrive as raw UTF-16 from COM and nothing validates them; an unpaired
    /// surrogate is not encodable as UTF-8, so the binding layer silently
    /// substitutes U+FFFD and the stored path stops matching the device's.
    /// </summary>
    static bool HasLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Announces a name the database cannot store faithfully, and returns it
    /// anyway.
    ///
    /// The row is still written, because dropping it would lose a file the
    /// device really has - the one thing this project refuses to do. But the
    /// stored path no longer matches the device's, so the file cannot be found
    /// again from this row, and that has to be said out loud rather than
    /// discovered later by someone wondering why a copy failed.
    /// </summary>
    string Announce(string value, string what)
    {
        if (!HasLoneSurrogate(value)) return value;

        Console.Error.WriteLine(
            $"[UNSTORABLE NAME] A {what} contains an unpaired UTF-16 surrogate, which cannot be " +
            $"written as text. The row is recorded with a replacement character, so this file " +
            $"cannot be located from the database: {Printable(value)}");
        return value;
    }

    /// <summary>
    /// A form of the text safe to write to a console, which cannot encode an
    /// unpaired surrogate either - so the warning about an unprintable name
    /// would otherwise be unprintable itself.
    /// </summary>
    static string Printable(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '?';
            }
        }
        return new string(chars);
    }

    static object Nullable(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    // Sortable and unambiguous, because started_utc/finished_utc are TEXT and
    // sort lexically, and that ordering decides which scan counts as current.
    //
    // InvariantCulture is the whole point of this method. Without it the format
    // follows the machine's region setting, so ar-SA writes a Hijri year (1448)
    // and th-TH a Buddhist one (2569) - values that sort BEFORE every Gregorian
    // row in the same column. "The newest scan" then resolves to the oldest,
    // and a stale census gets compared against the live phone, reporting files
    // that are still there as deleted. A Windows region setting was enough to
    // trigger it; no code change required.
    //
    // No Z suffix, deliberately, even though the value IS UTC and saying so
    // would be better: the column already holds rows written without one, and
    // changing the format is a data migration rather than a bug fix. It belongs
    // with the user_version work.
    static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// The gate every event-recording call passes through. A scan that has not
    /// started has nowhere to put a row; a scan that has finished has published
    /// a census, and a row accepted afterwards would contradict it. Both are
    /// caller mistakes, and both refuse loudly - the alternative is a database
    /// that disagrees with its own scan row and says nothing about it.
    /// </summary>
    SqliteCommand Accepting(SqliteCommand? command)
    {
        if (finished) throw AlreadyFinished();
        return command ?? throw NotStarted();
    }

    static InvalidOperationException NotStarted() =>
        new("OnScanStarted must be called before any scan events are recorded.");

    static InvalidOperationException AlreadyFinished() =>
        new("This scan has already been finished. Its row records a census, and " +
            "anything accepted now would not be part of it. Start a new scan.");

    /// <summary>
    /// Commits whatever the scan got through and closes the file. A scan that
    /// never reached OnScanFinished keeps its 'running' status on purpose: the
    /// rows are real and worth keeping, but nothing may treat them as a
    /// complete picture of the device.
    ///
    /// If that final commit fails there is nowhere left to save the rows, so
    /// they are lost - but the number lost is reported, and the cleanup below
    /// still runs. Both halves matter: a silent loss cannot be investigated,
    /// and a Dispose that gives up partway leaves the file handle and the
    /// SQLite lock held, which is the "next run finds a locked database"
    /// failure CompositeScanSink says it exists to prevent.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Commit();
        }
        catch (Exception ex)
        {
            // Every exception, not just SqliteException: a connection closed
            // underneath this throws InvalidOperationException, and letting
            // that escape would skip the cleanup in the finally - turning one
            // failed commit into a database nobody can open next time.
            Console.Error.WriteLine(
                $"[SCAN NOT FULLY RECORDED] The last {rowsSinceCommit} row(s) could not be written to " +
                $"{DatabasePath} and are lost. The scan is still marked 'running', so nothing will read " +
                $"it as a complete census - but it is less complete than even that suggests. " +
                $"Cause: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            insertFile?.Dispose();
            insertFolder?.Dispose();
            insertSkipped?.Dispose();
            insertError?.Dispose();
            transaction?.Dispose();
            connection.Dispose();
        }
    }
}
