using Microsoft.Data.Sqlite;

/// <summary>
/// Tests for <c>SqliteScanSink</c> - the scan as it is stored rather than
/// printed.
///
/// The question these pin down is "what does a half-finished scan look like in
/// the database". A row that says 'complete' is a promise that the device was
/// fully enumerated; anything later comparing two scans will read files absent
/// from the newer one as deleted from the phone. So the status must be written
/// last, must survive nothing else being written, and must never say 'complete'
/// for a walk that was cut short.
/// </summary>
public class SqliteScanSinkTests
{
    // ---- Harness ------------------------------------------------------------

    /// <summary>A database file of its own per test, removed afterwards.</summary>
    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scan-test-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            // The connection pool can hold the file open past Dispose, which on
            // Windows makes the delete fail rather than the test.
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(Path + suffix); } catch (IOException) { }
            }
        }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path}");
            connection.Open();
            return connection;
        }

        public object? Scalar(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public long Count(string table, string where = "1=1") =>
            (long)(Scalar($"SELECT COUNT(*) FROM {table} WHERE {where}") ?? 0L);

        public string? Text(string sql) => Scalar(sql) as string;

        public List<string> Column(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
            {
                values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetValue(0).ToString()!);
            }
            return values;
        }
    }

    static DeviceObject FileObject(string name, ulong? size = 1234, string? persistentId = "pid") =>
        new(ObjectId: "o:" + name, Name: name, DisplayName: name,
            IsContainer: false, Size: size, PersistentId: persistentId, ModifiedRaw: "2026-09-22T10:00:00");

    static DeviceObject FolderObject(string name) =>
        new(ObjectId: "o:" + name, Name: name, DisplayName: name,
            IsContainer: true, Size: null, PersistentId: "pid:" + name, ModifiedRaw: null);

    static DeviceIdentity Device(string? serial = "SER123") =>
        new(WpdId: "wpd-1", FriendlyName: "Galaxy A56", SerialNumber: serial,
            Manufacturer: "samsung", Model: "SM-A566B");

    static ScanOutcome CleanOutcome() =>
        new(Completed: true, Stalled: false, Faulted: false, CameraMode: false,
            MediaFiles: 0, Documents: 0, UndeterminedFiles: 0, TotalFilesSeen: 0,
            SubtreeLosses: 0, SignatureChecksRun: 0, CaughtBySignatureOnly: 0,
            SignatureCheckErrors: 0, FilesSkippedByBreaker: 0,
            SignatureCheckingDisabled: false, FilePropertyMisses: 0);

    // ---- The scan row's status ---------------------------------------------

    [Fact]
    public void ANewScan_IsMarkedRunningBeforeAnyFileArrives()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);

            Assert.Equal("running", db.Text("SELECT status FROM scan"));
            Assert.Null(db.Text("SELECT finished_utc FROM scan"));
        }
    }

    [Fact]
    public void AScanThatNeverFinishes_StaysRunning()
    {
        // The watchdog can kill this process outright. Rows already committed
        // are real and worth keeping, but nothing may read them as a complete
        // picture of the device - the status is how that is said.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            // No OnScanFinished: the walk died here.
        }

        Assert.Equal("running", db.Text("SELECT status FROM scan"));
        Assert.Equal(1, db.Count("file"));
    }

    [Fact]
    public void ACleanScan_BecomesComplete()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal("complete", db.Text("SELECT status FROM scan"));
        Assert.NotNull(db.Text("SELECT finished_utc FROM scan"));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Stalled")]
    [InlineData("Faulted")]
    [InlineData("CameraMode")]
    [InlineData("UndeterminedFiles")]
    [InlineData("SubtreeLosses")]
    public void AScanWithAnyDoubt_BecomesPartial_NeverComplete(string field)
    {
        var outcome = field switch
        {
            "Completed" => CleanOutcome() with { Completed = false },
            "Stalled" => CleanOutcome() with { Stalled = true },
            "Faulted" => CleanOutcome() with { Faulted = true },
            "CameraMode" => CleanOutcome() with { CameraMode = true },
            "UndeterminedFiles" => CleanOutcome() with { UndeterminedFiles = 1 },
            "SubtreeLosses" => CleanOutcome() with { SubtreeLosses = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: outcome.CameraMode);
            sink.OnScanFinished(outcome);
        }

        Assert.Equal("partial", db.Text("SELECT status FROM scan"));
    }

    // ---- The device row -----------------------------------------------------

    [Fact]
    public void TheDeviceIsKeyedOnItsSerial_NotTheWpdId()
    {
        // The WPD id embeds the USB port, so keying on it would file the same
        // phone twice for being plugged in elsewhere - and each history would
        // then look like a device that lost every file in the other.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(serial: "SER123"), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal("SER123", db.Text("SELECT device_key FROM device"));
        Assert.Equal(0L, (long)db.Scalar("SELECT key_is_fallback FROM device")!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ADeviceWithNoSerial_FallsBackToTheWpdId_AndSaysSo(string? serial)
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(serial), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal("wpd-1", db.Text("SELECT device_key FROM device"));
        Assert.Equal(1L, (long)db.Scalar("SELECT key_is_fallback FROM device")!);
    }

    [Fact]
    public void ASecondScanOfOneDevice_ReusesItsRow_AndKeepsTheFirstSighting()
    {
        using var db = new TempDatabase();
        string firstSeen;

        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }
        firstSeen = db.Text("SELECT first_seen_utc FROM device")!;

        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device() with { FriendlyName = "Renamed Phone" }, cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("device"));
        Assert.Equal(2, db.Count("scan"));
        // The name is the phone's current one; the first sighting is the only
        // record of how long it has been known and must not be overwritten.
        Assert.Equal("Renamed Phone", db.Text("SELECT friendly_name FROM device"));
        Assert.Equal(firstSeen, db.Text("SELECT first_seen_utc FROM device"));
    }

    [Fact]
    public void TwoDevices_GetSeparateRowsAndScans()
    {
        using var db = new TempDatabase();
        foreach (string serial in new[] { "AAA", "BBB" })
        {
            using var sink = new SqliteScanSink(db.Path);
            sink.OnScanStarted(Device(serial), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(2, db.Count("device"));
        Assert.Equal(2, db.Count("scan"));
    }

    // ---- What gets stored ---------------------------------------------------

    [Fact]
    public void AFileIsStoredWithEverythingNeededToFindItAgain()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("IMG_0001.jpg"), "/P/DCIM/IMG_0001.jpg", FileKind.MediaFile, recovered: true);
            sink.OnScanFinished(CleanOutcome());
        }

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, name, object_id, persistent_id, size, modified_raw, kind, recovered FROM file";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("/P/DCIM/IMG_0001.jpg", reader.GetString(0));
        Assert.Equal("IMG_0001.jpg", reader.GetString(1));
        Assert.Equal("o:IMG_0001.jpg", reader.GetString(2));
        Assert.Equal("pid", reader.GetString(3));
        Assert.Equal(1234L, reader.GetInt64(4));
        Assert.Equal("2026-09-22T10:00:00", reader.GetString(5));
        Assert.Equal("MediaFile", reader.GetString(6));
        Assert.Equal(1L, reader.GetInt64(7));
    }

    [Fact]
    public void KindIsStoredAsText_SoReorderingTheEnumCannotReinterpretOldRows()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnFile(FileObject("b.pdf"), "/P/b.pdf", FileKind.Document, recovered: false);
            sink.OnFile(FileObject("c.db"), "/P/c.db", FileKind.Unknown, recovered: false);
            sink.OnFile(FileObject("d.xyz"), "/P/d.xyz", FileKind.Undetermined, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(
            new[] { "MediaFile", "Document", "Unknown", "Undetermined" },
            db.Column("SELECT kind FROM file ORDER BY file_id"));
    }

    [Fact]
    public void EveryKindIsStored_IncludingTheOnesTheConsoleDoesNotPrint()
    {
        // Not showing a file is a display choice; not storing it is data loss.
        // Unknown files are the bulk of a real phone and the only proof that
        // the walk looked at them at all.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("c.db"), "/P/c.db", FileKind.Unknown, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("file", "kind = 'Unknown'"));
    }

    [Fact]
    public void AMissingSizeIsStoredAsNull_NotZero()
    {
        // "The device did not tell us" and "the file is empty" are different
        // facts, and a zero would let the second be inferred from the first.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg", size: null, persistentId: null), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("file", "size IS NULL AND persistent_id IS NULL"));
    }

    [Fact]
    public void TwoObjectsSharingOnePath_AreBothKept()
    {
        // An upsert keyed on the path would collapse these into one row, and
        // silently dropping a file is the failure this project exists to
        // prevent. A device that reports two objects at one path is an oddity
        // worth seeing, not one worth hiding.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg") with { ObjectId = "o:1" }, "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnFile(FileObject("a.jpg") with { ObjectId = "o:2" }, "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(2, db.Count("file", "path = '/P/a.jpg'"));
        Assert.Equal(new[] { "o:1", "o:2" }, db.Column("SELECT object_id FROM file ORDER BY file_id"));
    }

    [Fact]
    public void FoldersSkippedFoldersAndErrors_EachGetTheirOwnRows()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFolder(FolderObject("DCIM"), "/P/DCIM", recovered: false);
            sink.OnFolderSkipped("/P/.thumbnails", "cache folder");
            sink.OnError(new ScanError("o:bad", "/P/x", ScanStage.EnumerateNext,
                unchecked((int)0x800703E3), "The I/O operation has been aborted"));
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("folder", "path = '/P/DCIM'"));
        Assert.Equal(1, db.Count("skipped_folder", "reason = 'cache folder'"));
        // The stage is the difference between losing one object and losing a
        // whole subtree, so it is stored by name rather than as an ordinal.
        Assert.Equal("EnumerateNext", db.Text("SELECT stage FROM scan_error"));
        Assert.Equal(unchecked((int)0x800703E3), (long)db.Scalar("SELECT hresult FROM scan_error")!);
    }

    [Fact]
    public void TheCensusAndTheSignatureFiguresLandOnTheScanRow()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnScanFinished(CleanOutcome() with
            {
                MediaFiles = 13466,
                Documents = 164,
                TotalFilesSeen = 13791,
                SignatureChecksRun = 5,
                CaughtBySignatureOnly = 0,
                FilePropertyMisses = 3,
            });
        }

        Assert.Equal(1, db.Count("scan",
            "media_files = 13466 AND documents = 164 AND total_files_seen = 13791 " +
            "AND signature_checks_run = 5 AND file_property_misses = 3"));
    }

    // ---- The retry pass -----------------------------------------------------

    [Fact]
    public void ASkippedRetryPass_RecordsWhyItDidNotRun()
    {
        // "The retry did not run" and "the retry found nothing" are different
        // facts about how much of the device is unaccounted for.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnRetryFinished(RetryOutcome.WasSkipped(4, "the device stopped responding"));
            sink.OnScanFinished(CleanOutcome() with { Completed = false, Stalled = true });
        }

        Assert.Equal(0L, (long)db.Scalar("SELECT retry_ran FROM scan")!);
        Assert.Equal(4L, (long)db.Scalar("SELECT retry_attempted FROM scan")!);
        Assert.Contains("stopped responding", db.Text("SELECT retry_skip_reason FROM scan")!);
    }

    [Fact]
    public void ARetryPassThatRan_RecordsWhatItGotBack()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnRetryStarted(9);
            sink.OnRetryFinished(new RetryOutcome(
                Skipped: false, SkipReason: null, Attempted: 9,
                RecoveredFiles: 5, RecoveredFolders: 2,
                StillUnreadable: 2, HiddenSubtrees: 1, NewFailures: 3));
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("scan",
            "retry_ran = 1 AND retry_skip_reason IS NULL AND retry_attempted = 9 " +
            "AND recovered_files = 5 AND recovered_folders = 2 AND still_unreadable = 2 " +
            "AND hidden_subtrees = 1 AND retry_new_failures = 3"));
    }

    [Fact]
    public void AScanWithNoFailures_LeavesTheRetryColumnsNull()
    {
        // NULL means "there was nothing to retry", which is not the same as a
        // pass that ran and recovered zero.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("scan", "retry_ran IS NULL AND recovered_files IS NULL"));
    }

    // ---- Batching and durability -------------------------------------------

    [Fact]
    public void MoreRowsThanOneTransactionHolds_AreAllWritten()
    {
        // The batch boundary re-points every prepared statement at a fresh
        // transaction; getting that wrong throws on the row after the commit,
        // which a test with a handful of files would never reach.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            for (int i = 0; i < 2_500; i++)
            {
                sink.OnFile(FileObject($"f{i}.jpg"), $"/P/f{i}.jpg", FileKind.MediaFile, recovered: false);
            }
            sink.OnScanFinished(CleanOutcome() with { MediaFiles = 2_500, TotalFilesSeen = 2_500 });
        }

        Assert.Equal(2_500, db.Count("file"));
        Assert.Equal("complete", db.Text("SELECT status FROM scan"));
    }

    [Fact]
    public void RowsCommittedBeforeAKill_SurviveIt()
    {
        // Committing periodically is what makes a killed scan leave evidence
        // instead of nothing. Past the batch size, the earlier rows are on disk
        // even though the scan never finished.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            for (int i = 0; i < 1_200; i++)
            {
                sink.OnFile(FileObject($"f{i}.jpg"), $"/P/f{i}.jpg", FileKind.MediaFile, recovered: false);
            }
            // Killed here: no OnScanFinished. Dispose stands in for the process
            // ending; the rows from the committed batch must already be safe.
        }

        Assert.True(db.Count("file") >= 1_000);
        Assert.Equal("running", db.Text("SELECT status FROM scan"));
    }

    // ---- Misuse -------------------------------------------------------------

    [Fact]
    public void AFileReportedBeforeTheScanStarts_FailsLoudly()
    {
        // Silently dropping it would leave a file on the phone with no row and
        // no error - the shape of bug this project keeps finding.
        using var db = new TempDatabase();
        using var sink = new SqliteScanSink(db.Path);

        Assert.Throws<InvalidOperationException>(() =>
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false));
    }

    [Fact]
    public void TheDatabaseFileIsCreatedWithItsFolder()
    {
        using var db = new TempDatabase();
        string nested = Path.Combine(Path.GetTempPath(), $"scan-test-{Guid.NewGuid():N}", "nested", "scans.db");
        try
        {
            using (var sink = new SqliteScanSink(nested))
            {
                sink.OnScanStarted(Device(), cameraMode: false);
                sink.OnScanFinished(CleanOutcome());
            }

            Assert.True(File.Exists(nested));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string? root = Directory.GetParent(Path.GetDirectoryName(nested)!)?.FullName;
            if (root is not null && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void ReopeningAnExistingDatabase_AddsToItRatherThanReplacingIt()
    {
        using var db = new TempDatabase();
        for (int run = 0; run < 3; run++)
        {
            using var sink = new SqliteScanSink(db.Path);
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome() with { MediaFiles = 1, TotalFilesSeen = 1 });
        }

        Assert.Equal(3, db.Count("scan"));
        Assert.Equal(3, db.Count("file"));
        Assert.Equal(1, db.Count("device"));
        // Each file row belongs to exactly one scan; the history is a series of
        // snapshots, not one table being overwritten.
        Assert.Equal(3, db.Count("(SELECT DISTINCT scan_id FROM file)"));
    }
}
