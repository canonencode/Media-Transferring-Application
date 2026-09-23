using Microsoft.Data.Sqlite;

namespace MediaTransfer.Core;

/// <summary>
/// The shape of the database, and the only thing allowed to change it.
///
/// It sits apart from any one writer because two now share the file: the
/// scanner records what a phone holds, and the copier records what was taken
/// off it. If each carried its own idea of the schema they would drift, and the
/// first sign of that would be a column one writes and the other cannot read.
///
/// CREATE TABLE IF NOT EXISTS silently does nothing to a database that already
/// has the table, so a column added to the text below would reach new files
/// only and then fail at INSERT time on somebody's machine - invisibly to the
/// tests, which all open fresh files. That is what the version stamp is for,
/// and why a change needs a step in Apply as well as a line in the text.
/// </summary>
public static class ScanSchema
{
    /// <summary>What this build knows how to read and write.</summary>
    public const int Version = 4;

    const string Tables = """
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
            audio_files INTEGER,
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

        CREATE TABLE IF NOT EXISTS copy (
            copy_id         INTEGER PRIMARY KEY AUTOINCREMENT,
            device_key      TEXT NOT NULL,
            source_path     TEXT NOT NULL,
            source_name     TEXT NOT NULL,
            source_size     INTEGER,
            source_modified TEXT,
            object_id       TEXT,
            destination     TEXT NOT NULL,
            status          TEXT NOT NULL,
            started_utc     TEXT NOT NULL,
            finished_utc    TEXT,
            bytes_copied    INTEGER,
            sha256          TEXT,
            error           TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_copy_source ON copy(device_key, source_path);
        CREATE INDEX IF NOT EXISTS ix_copy_status ON copy(status);
        """;

    /// <summary>
    /// Brings a database up to <see cref="Version"/>, or refuses it.
    ///
    /// The refusal is the half that protects data. An older build opening a
    /// newer file cannot know which columns it is failing to fill, and a scan
    /// or a copy recorded with silent gaps is worse than one not recorded.
    /// </summary>
    public static void Apply(SqliteConnection connection)
    {
        long version = Scalar(connection, "PRAGMA user_version;");
        if (version > Version)
        {
            throw new InvalidOperationException(
                $"Bu veritabanı daha yeni bir sürümle yazılmış (şema {version}, bu sürüm {Version}). " +
                "Eski bir sürümle yazmak, dolduramadığı alanları sessizce boş bırakır.");
        }

        // Version 0 is both a brand new file and every file written before
        // versioning existed; IF NOT EXISTS makes the text safe for both.
        if (version == 0) Run(connection, Tables);

        // 1 -> 2: unresolved_objects, because a scan marked partial for that
        // reason had no way to say why.
        if (version < 2) AddColumn(connection, "scan", "unresolved_objects", "INTEGER");

        // 2 -> 3: the copy ledger. A whole table, unlike a column, can simply
        // be created - IF NOT EXISTS does the right thing for one.
        if (version < 3) Run(connection, Tables);

        // 3 -> 4: audio became a kind of its own, so a scan can say how many
        // recordings it found instead of burying them in the count of files it
        // decided were not worth taking.
        if (version < 4) AddColumn(connection, "scan", "audio_files", "INTEGER");

        Run(connection, $"PRAGMA user_version = {Version};");
    }

    static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is long v ? v : 0;
    }

    /// <summary>Adds a column unless it is already there - ALTER TABLE throws on a repeat.</summary>
    static void AddColumn(SqliteConnection connection, string table, string column, string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;

        Run(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
    }
}
