using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

/// <summary>
/// Adversarial tests for <c>SqliteScanSink</c>. Everything here attacks the one
/// promise the class makes: a scan that did not provably finish must never be
/// readable as a complete census, and no file the walk reported may silently
/// fail to land in the database.
/// </summary>
public class SqliteScanSinkStressTests
{
    // ---- Harness ------------------------------------------------------------

    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scan-stress-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
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

    /// <summary>A scan the watchdog cut short - must never be stored as 'complete'.</summary>
    static ScanOutcome StalledOutcome() =>
        CleanOutcome() with { Completed = false, Stalled = true, SubtreeLosses = 3 };

    // ========================================================================
    // 1. Awkward text
    // ========================================================================

    public static TheoryData<string, string> AwkwardText() => new()
    {
        { "turkish", "/Dahili depolama/DCIM/Ayşegül-İĞÜŞÖÇ-ığüşöç.jpg" },
        { "emoji-astral", "/P/DCIM/\U0001F4F8-\U0001F60A-tatil\U0001F3D6.jpg" },
        { "rtl-arabic", "/P/صور/الصورة-١٢٣.jpg" },
        { "rtl-hebrew", "/P/תמונות/תמונה.jpg" },
        { "rtl-override", "/P/photo\u202Egpj.exe" },
        { "cjk", "/P/写真/写真-01.jpg" },
        { "combining", "/P/e\u0301\u0301\u0301\u0301accent.jpg" },
        { "sql-quotes", "/P/it's a \"file\"; DROP TABLE file;--.jpg" },
        { "sql-params", "/P/$path $scan ?1 :name @x.jpg" },
        { "percent-underscore", "/P/100%_done_%_.jpg" },
        { "backslash-newline", "/P/line1\nline2\ttab\\back.jpg" },
        { "control-chars", "/P/bell\u0007esc\u001bdel\u007f.jpg" },
        { "zero-width", "/P/a\u200Bb\u200Dc\uFEFFd.jpg" },
        { "astral-plane-4byte", "/P/\U00020BB7\U0002A6B2\U0001D11E.jpg" },
        { "noncharacter", "/P/x\uFFFEy\uFFFFz.jpg" },
    };

    [Theory]
    [MemberData(nameof(AwkwardText))]
    public void AwkwardPaths_RoundTripByteForByte(string label, string path)
    {
        // A path that comes back different from the one the walk reported is a
        // file the app can no longer find on the phone.
        using var db = new TempDatabase();
        string name = path[(path.LastIndexOf('/') + 1)..];
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject(name) with { Name = name }, path, FileKind.MediaFile, recovered: false);
            sink.OnFolder(FolderObject(name), path + "/sub", recovered: false);
            sink.OnFolderSkipped(path, "reason " + path);
            sink.OnError(new ScanError("o:" + path, path, ScanStage.Enumerate, -1, "msg " + path));
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(path, db.Column("SELECT path FROM file").Single());
        Assert.Equal(name, db.Column("SELECT name FROM file").Single());
        Assert.Equal(path + "/sub", db.Column("SELECT path FROM folder").Single());
        Assert.Equal(path, db.Column("SELECT path FROM skipped_folder").Single());
        Assert.Equal("msg " + path, db.Column("SELECT message FROM scan_error").Single());

        // And it must still be findable by an exact-match query, which is how a
        // later comparison decides whether a file is still on the device.
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM file WHERE path = $p";
        command.Parameters.AddWithValue("$p", path);
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void APathContainingNul_RoundTripsWhole()
    {
        // SQLite's C API is NUL-terminated in places. If the path is truncated
        // at the NUL, the row points at a file that does not exist and the real
        // one has no row at all.
        using var db = new TempDatabase();
        string path = "/P/DCIM/before\0after.jpg";
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("before\0after.jpg"), path, FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        string stored = db.Column("SELECT path FROM file").Single();
        Assert.Equal(path, stored);
        Assert.Equal(path.Length, stored.Length);
    }

    [Fact]
    public void APathContainingALoneSurrogate_RoundTripsUnchanged()
    {
        // MTP names arrive as raw UTF-16 from COM and are not validated. An
        // unpaired surrogate cannot be encoded as UTF-8, so something has to
        // give - silently substituting a different character rewrites the path.
        using var db = new TempDatabase();
        string path = "/P/DCIM/lone\uD83Dsurrogate.jpg";
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("x.jpg"), path, FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(path, db.Column("SELECT path FROM file").Single());
    }

    [Fact]
    public void AnExtremelyLongPathAndName_AreStoredWhole()
    {
        using var db = new TempDatabase();
        string name = new string('n', 32_000) + ".jpg";
        string path = "/P/" + new string('d', 32_000) + "/" + name;
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject(name) with { Name = name }, path, FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(path.Length, db.Column("SELECT path FROM file").Single().Length);
        Assert.Equal(name.Length, db.Column("SELECT name FROM file").Single().Length);
    }

    [Fact]
    public void EmptyStringsForPathAndName_AreStoredAsEmptyNotNull()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("") with { Name = "", ObjectId = "" }, "", FileKind.Unknown, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1, db.Count("file", "path = '' AND name = '' AND object_id = ''"));
        Assert.Equal(0, db.Count("file", "path IS NULL"));
    }

    // ========================================================================
    // 2. Numeric extremes
    // ========================================================================

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData((ulong)long.MaxValue)]
    public void SizesThatFitInAnInt64_AreStoredExactly(ulong size)
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg", size), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal((long)size, (long)db.Scalar("SELECT size FROM file")!);
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData((ulong)long.MaxValue + 1)]
    public void ASizeTooBigForInt64_IsNeverStoredAsANegativeNumber(ulong size)
    {
        // OnFile casts ulong -> long unchecked. A device reporting a sentinel
        // like 0xFFFFFFFFFFFFFFFF for "unknown" would land in the database as
        // -1, a size that is not merely wrong but impossible - and nothing
        // downstream has any reason to test for it. NULL ("not told") or a
        // thrown error would both be honest; a negative number is not.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg", size), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        object stored = db.Scalar("SELECT size FROM file")!;
        Assert.True(stored is DBNull || (long)stored >= 0,
            $"size {size} was stored as {stored}");
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(unchecked((int)0x80070005))]
    public void ExtremeHResults_AreStoredExactly(int hresult)
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnError(new ScanError("o:1", "/P", ScanStage.Properties, hresult, "m"));
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal((long)hresult, (long)db.Scalar("SELECT hresult FROM scan_error")!);
    }

    [Fact]
    public void ExtremeCountsOnTheOutcome_AreStoredExactly()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnScanFinished(CleanOutcome() with
            {
                Completed = false,
                MediaFiles = int.MaxValue,
                TotalFilesSeen = int.MaxValue,
                UndeterminedFiles = int.MaxValue,
                SubtreeLosses = int.MinValue,
            });
        }

        Assert.Equal((long)int.MaxValue, (long)db.Scalar("SELECT total_files_seen FROM scan")!);
        Assert.Equal((long)int.MinValue, (long)db.Scalar("SELECT subtree_losses FROM scan")!);
        Assert.Equal("partial", db.Text("SELECT status FROM scan"));
    }

    // ========================================================================
    // 3. The timestamp
    // ========================================================================

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("tr-TR")]
    public void TheTimestampIsTheSameInEveryCulture(string culture)
    {
        // started_utc is the sort key for a device's scan history and the field
        // any two-scan comparison orders on. Timestamp() formats with the
        // AMBIENT culture, so on a machine whose culture uses a non-Gregorian
        // calendar the year is not the Gregorian one. Two scans written under
        // different cultures then sort against each other wrongly, and "the
        // newest scan" can resolve to the older one - which is exactly how a
        // stale census gets compared against the phone and reports live files
        // as deleted.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            using var db = new TempDatabase();
            using (var sink = new SqliteScanSink(db.Path))
            {
                sink.OnScanStarted(Device(), cameraMode: false);
                sink.OnScanFinished(CleanOutcome());
            }

            string started = db.Text("SELECT started_utc FROM scan")!;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", started);
            Assert.StartsWith(
                DateTime.UtcNow.ToString("yyyy", CultureInfo.InvariantCulture),
                started);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    // ========================================================================
    // 4. Lifecycle misuse
    // ========================================================================

    [Fact]
    public void OnScanFinished_WithoutOnScanStarted_FailsLoudly()
    {
        // OnFile throws when the scan was never started. OnScanFinished does
        // not - ScanId is 0, so the UPDATE matches no rows and returns quietly.
        // A caller whose OnScanStarted was skipped (an early return, a swallowed
        // exception) therefore sees a scan run to completion with no error at
        // all, while the database holds nothing. A silent no-op is the one
        // response this class must never give.
        using var db = new TempDatabase();
        using var sink = new SqliteScanSink(db.Path);

        Assert.Throws<InvalidOperationException>(() => sink.OnScanFinished(CleanOutcome()));
    }

    [Fact]
    public void OnScanFinished_Twice_CannotUpgradeAPartialScanToComplete()
    {
        // An outer finally calling OnScanFinished after an inner catch already
        // did is an ordinary mistake. If the second call wins, a scan that was
        // recorded as cut short is rewritten as a complete census - the precise
        // failure this project exists to prevent.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(StalledOutcome());
            try { sink.OnScanFinished(CleanOutcome()); }
            catch (InvalidOperationException) { /* refusing is the safe answer */ }
        }

        Assert.Equal("partial", db.Text("SELECT status FROM scan"));
    }

    [Fact]
    public void FilesReportedAfterOnScanFinished_CannotLandInACompletedScan()
    {
        // Once the scan row says 'complete' its census is frozen: it is a
        // promise that the device held exactly these files. Rows accepted after
        // that promise are outside any transaction the status covers - abandon
        // the process here and the scan still reads 'complete' while missing
        // the files it took after finishing.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome() with { MediaFiles = 1, TotalFilesSeen = 1 });

            try
            {
                sink.OnFile(FileObject("b.jpg"), "/P/b.jpg", FileKind.MediaFile, recovered: false);
                sink.OnFolder(FolderObject("late"), "/P/late", recovered: false);
            }
            catch (InvalidOperationException) { /* refusing is the safe answer */ }
        }

        Assert.Equal("complete", db.Text("SELECT status FROM scan"));
        Assert.Equal(1, db.Count("scan", "total_files_seen = 1"));
        Assert.Equal(1L, db.Count("file"));
    }

    [Fact]
    public void OnScanStarted_Twice_LeavesNoOrphanRunningScan()
    {
        // Whatever the second call does, it must not leave a scan row that can
        // never be finished. A 'running' row is honest, but one that no code
        // path will ever close is permanent noise in a device's history - and
        // the sink is left half-rebuilt on top of it.
        using var db = new TempDatabase();
        Exception? thrown = null;
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            try { sink.OnScanStarted(Device(), cameraMode: false); }
            catch (Exception e) { thrown = e; }

            if (thrown is null) sink.OnScanFinished(CleanOutcome());
        }

        Assert.True(db.Count("scan") == 1 || db.Count("scan", "status = 'running'") == 0,
            $"scans={db.Count("scan")} running={db.Count("scan", "status = 'running'")} " +
            $"thrown={thrown?.GetType().Name}: {thrown?.Message}");
    }

    [Fact]
    public void OnScanStarted_Twice_DoesNotLoseFilesFromTheFirstScan()
    {
        // Whatever else the second call breaks, rows already reported must not
        // vanish. The first batch is still uncommitted when the second call
        // arrives.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            try { sink.OnScanStarted(Device(), cameraMode: false); } catch (Exception) { }
        }

        Assert.Equal(1L, db.Count("file"));
    }

    [Fact]
    public void EventsAfterDispose_FailLoudlyRatherThanDisappear()
    {
        // A sink used past its lifetime must never accept a file and drop it.
        using var db = new TempDatabase();
        var sink = new SqliteScanSink(db.Path);
        sink.OnScanStarted(Device(), cameraMode: false);
        sink.Dispose();

        Assert.ThrowsAny<Exception>(() =>
            sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false));
        Assert.ThrowsAny<Exception>(() => sink.OnFolder(FolderObject("d"), "/P/d", recovered: false));
        Assert.ThrowsAny<Exception>(() => sink.OnFolderSkipped("/P/x", "r"));
        Assert.ThrowsAny<Exception>(() =>
            sink.OnError(new ScanError("o", "/P", ScanStage.Enumerate, -1, "m")));
        Assert.ThrowsAny<Exception>(() => sink.OnScanFinished(CleanOutcome()));
    }

    [Fact]
    public void DisposeTwice_IsHarmless()
    {
        using var db = new TempDatabase();
        var sink = new SqliteScanSink(db.Path);
        sink.OnScanStarted(Device(), cameraMode: false);
        sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        sink.OnScanFinished(CleanOutcome());

        sink.Dispose();
        sink.Dispose();

        Assert.Equal(1L, db.Count("file"));
        Assert.Equal("complete", db.Text("SELECT status FROM scan"));
    }

    [Fact]
    public void DisposeWithoutEverStartingAScan_IsHarmless()
    {
        using var db = new TempDatabase();
        var sink = new SqliteScanSink(db.Path);
        sink.Dispose();

        Assert.Equal(0L, db.Count("scan"));
    }

    [Fact]
    public void ARetryOutcomeReportedWithoutAScan_DoesNotSilentlyVanish()
    {
        using var db = new TempDatabase();
        using var sink = new SqliteScanSink(db.Path);
        sink.OnRetryFinished(RetryOutcome.WasSkipped(4, "device wedged"));

        Assert.Throws<InvalidOperationException>(() => sink.OnScanFinished(StalledOutcome()));
    }

    // ========================================================================
    // 5. Null fields arriving from COM
    // ========================================================================

    [Fact]
    public void ANullFriendlyName_FailsLoudlyRatherThanWritingAHalfDeviceRow()
    {
        // DeviceIdentity is built from WPD property reads, which return null far
        // more often than the nullable annotations suggest.
        using var db = new TempDatabase();
        using var sink = new SqliteScanSink(db.Path);

        Assert.ThrowsAny<Exception>(() =>
            sink.OnScanStarted(Device() with { FriendlyName = null! }, cameraMode: false));

        // And nothing half-written is left behind for a later scan to inherit.
        Assert.Equal(0L, db.Count("device"));
        Assert.Equal(0L, db.Count("scan"));
    }

    [Fact]
    public void ANullFileName_FailsLoudlyRatherThanDroppingTheFile()
    {
        using var db = new TempDatabase();
        using var sink = new SqliteScanSink(db.Path);
        sink.OnScanStarted(Device(), cameraMode: false);

        Assert.ThrowsAny<Exception>(() =>
            sink.OnFile(FileObject("a.jpg") with { Name = null! }, "/P/a.jpg", FileKind.MediaFile, recovered: false));
    }

    [Fact]
    public void ANullPath_FailsLoudlyRatherThanDroppingTheFile()
    {
        using var db = new TempDatabase();
        using var sink = new SqliteScanSink(db.Path);
        sink.OnScanStarted(Device(), cameraMode: false);

        Assert.ThrowsAny<Exception>(() =>
            sink.OnFile(FileObject("a.jpg"), null!, FileKind.MediaFile, recovered: false));
    }

    [Fact]
    public void AFileThatFailedToBindDoesNotPoisonTheNextFile()
    {
        // The prepared statements are reused and their parameter values are
        // sticky. A row that throws half-way through binding leaves the command
        // holding a mixture of the failed file's values and the previous one's.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            sink.OnFile(FileObject("good1.jpg"), "/P/good1.jpg", FileKind.MediaFile, recovered: false);
            try
            {
                sink.OnFile(FileObject("bad.jpg") with { Name = null! }, "/P/bad.jpg", FileKind.MediaFile, recovered: false);
            }
            catch (Exception) { }
            sink.OnFile(FileObject("good2.jpg"), "/P/good2.jpg", FileKind.MediaFile, recovered: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(
            new[] { "/P/good1.jpg", "/P/good2.jpg" },
            db.Column("SELECT path FROM file ORDER BY file_id"));
    }

    // ========================================================================
    // 6. Filesystem hostility - the constructor must throw, not half-initialise
    // ========================================================================

    [Fact]
    public void ADatabasePathThatIsADirectory_Throws()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"scan-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.ThrowsAny<Exception>(() => new SqliteScanSink(dir));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ADatabasePathOnAMissingDrive_Throws()
    {
        string missing = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(c => $"{(char)c}:\\")
            .FirstOrDefault(root => !Directory.Exists(root)) ?? "Q:\\";

        Assert.ThrowsAny<Exception>(() =>
            new SqliteScanSink(Path.Combine(missing, "MediaTransfer", "scans.db")));
    }

    [Fact]
    public void AnEmptyDatabasePath_Throws()
    {
        Assert.ThrowsAny<Exception>(() => new SqliteScanSink(""));
    }

    [Fact]
    public void AWhitespaceDatabasePath_Throws()
    {
        Assert.ThrowsAny<Exception>(() => new SqliteScanSink("   "));
    }

    [Fact]
    public void ADatabasePathWithIllegalCharacters_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            new SqliteScanSink(Path.Combine(Path.GetTempPath(), "bad|name<>.db")));
    }

    [Fact]
    public void AReadOnlyDatabaseFile_ThrowsAndDoesNotKeepTheFileLocked()
    {
        // The caller's contract is that the constructor throws so it can fall
        // back to another path. A constructor that throws after opening the
        // connection leaks that handle, and on Windows the failed file stays
        // locked - so the fallback can fail too.
        string path = Path.Combine(Path.GetTempPath(), $"scan-ro-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(path, Array.Empty<byte>());
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => new SqliteScanSink(path));

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                foreach (string s in new[] { "", "-wal", "-shm" }) File.Delete(path + s);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void AFileThatIsNotADatabase_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"scan-garbage-{Guid.NewGuid():N}.db");
        File.WriteAllText(path, new string('x', 8192));
        try
        {
            Assert.ThrowsAny<Exception>(() => new SqliteScanSink(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string s in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + s); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void TheMemoryKeyword_IsNotSilentlyAcceptedAsARealDatabase()
    {
        // ":memory:" is a magic DataSource for Microsoft.Data.Sqlite. Passed
        // through as a path it would give a sink that accepts a whole scan,
        // reports no error, and discards every row on Dispose. Nothing about
        // the caller's experience would say the scan was not recorded.
        SqliteScanSink? sink = null;
        try
        {
            sink = new SqliteScanSink(":memory:");
            Assert.True(File.Exists(sink.DatabasePath),
                $"accepted ':memory:' and wrote nothing to disk; DatabasePath={sink.DatabasePath}");
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            // Throwing is the safe answer.
        }
        finally
        {
            sink?.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(":memory:"); } catch (Exception) { }
        }
    }

    // ========================================================================
    // 7. Concurrency
    // ========================================================================

    [Fact]
    public void AReaderCanReadPreviousScansWhileOneIsWriting()
    {
        // WAL is enabled specifically so a UI can read while a scan runs. The
        // 'running' row in particular must be visible to a reader the instant
        // the scan starts, not only once it commits.
        using var db = new TempDatabase();
        using (var first = new SqliteScanSink(db.Path))
        {
            first.OnScanStarted(Device(), cameraMode: false);
            first.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
            first.OnScanFinished(CleanOutcome() with { MediaFiles = 1, TotalFilesSeen = 1 });
        }

        using var writer = new SqliteScanSink(db.Path);
        writer.OnScanStarted(Device(), cameraMode: false);
        for (int i = 0; i < 50; i++)
        {
            writer.OnFile(FileObject($"n{i}.jpg"), $"/P/n{i}.jpg", FileKind.MediaFile, recovered: false);
        }

        // A reader, mid-scan.
        Assert.Equal(2L, db.Count("scan"));
        Assert.Equal(1L, db.Count("scan", "status = 'complete'"));
        Assert.Equal(1L, db.Count("scan", "status = 'running'"));
        Assert.Equal(1L, db.Count("file"));   // the in-flight batch is not yet visible

        writer.OnScanFinished(CleanOutcome() with { MediaFiles = 50, TotalFilesSeen = 50 });
        Assert.Equal(51L, db.Count("file"));
    }

    [Fact]
    public void TwoSinksOnOneDatabase_TheSecondFailsFastRatherThanHanging()
    {
        // Two phones plugged in, or the app started twice. The first sink holds
        // an open write transaction for up to 1,000 rows. Whatever the second
        // one does it must not block the UI thread for tens of seconds before
        // reporting it.
        using var db = new TempDatabase();
        using var first = new SqliteScanSink(db.Path);
        first.OnScanStarted(Device("AAA"), cameraMode: false);
        for (int i = 0; i < 10; i++)
        {
            first.OnFile(FileObject($"a{i}.jpg"), $"/P/a{i}.jpg", FileKind.MediaFile, recovered: false);
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? thrown = null;
        try
        {
            using var second = new SqliteScanSink(db.Path);
            second.OnScanStarted(Device("BBB"), cameraMode: false);
            second.OnFile(FileObject("b.jpg"), "/P/b.jpg", FileKind.MediaFile, recovered: false);
            second.OnScanFinished(CleanOutcome() with { MediaFiles = 1, TotalFilesSeen = 1 });
        }
        catch (Exception e) { thrown = e; }
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"second sink took {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"({(thrown is null ? "succeeded" : thrown.GetType().Name + ": " + thrown.Message)})");
    }

    [Fact]
    public void TwoSinksScanningTwoDevicesAtOnce_BothRecordEveryFile()
    {
        // Interleaved, as two scan threads would be.
        using var db = new TempDatabase();
        using var a = new SqliteScanSink(db.Path);
        using var b = new SqliteScanSink(db.Path);
        a.OnScanStarted(Device("AAA"), cameraMode: false);
        b.OnScanStarted(Device("BBB"), cameraMode: false);

        for (int i = 0; i < 20; i++)
        {
            a.OnFile(FileObject($"a{i}.jpg"), $"/A/a{i}.jpg", FileKind.MediaFile, recovered: false);
            b.OnFile(FileObject($"b{i}.jpg"), $"/B/b{i}.jpg", FileKind.MediaFile, recovered: false);
        }

        a.OnScanFinished(CleanOutcome() with { MediaFiles = 20, TotalFilesSeen = 20 });
        b.OnScanFinished(CleanOutcome() with { MediaFiles = 20, TotalFilesSeen = 20 });

        Assert.Equal(40L, db.Count("file"));
        Assert.Equal(2L, db.Count("scan", "status = 'complete'"));
    }

    [Fact]
    public void ManyThreadsReportingToOneSink_LoseNothing()
    {
        // The walk is single-threaded today, but nothing on IScanSink says so
        // and a sink that silently drops rows under concurrent use would be
        // found only in production. Either it is thread-safe or it throws.
        using var db = new TempDatabase();
        const int threads = 4, perThread = 250;
        Exception? thrown = null;

        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            try
            {
                Parallel.For(0, threads, t =>
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        sink.OnFile(FileObject($"f{t}-{i}.jpg"), $"/P/f{t}-{i}.jpg",
                            FileKind.MediaFile, recovered: false);
                    }
                });
            }
            catch (Exception e) { thrown = e; }

            if (thrown is null)
            {
                sink.OnScanFinished(CleanOutcome() with
                {
                    MediaFiles = threads * perThread,
                    TotalFilesSeen = threads * perThread,
                });
            }
        }

        if (thrown is not null)
        {
            // Refusing concurrent use is acceptable - but then the scan must
            // not be left claiming to be complete.
            Assert.NotEqual("complete", db.Text("SELECT status FROM scan"));
            return;
        }

        Assert.Equal((long)(threads * perThread), db.Count("file"));
    }

    // ========================================================================
    // 8. Durability
    // ========================================================================

    [Fact]
    public void AbandonedMidScanWithoutDispose_KeepsCommittedRowsAndStaysRunning()
    {
        // Closest this process can get to a power cut: the sink is dropped with
        // an open transaction and never disposed.
        using var db = new TempDatabase();
        Abandon(db.Path, 2_500);

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long files = db.Count("file");
        Assert.Equal("running", db.Text("SELECT status FROM scan"));
        Assert.True(files >= 2_000, $"only {files} of the first 2,000 committed rows survived");
        Assert.True(files <= 2_500, $"{files} rows present but only 2,500 were reported");
        Assert.Null(db.Text("SELECT finished_utc FROM scan"));
    }

    [MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Abandon(string path, int rows)
    {
        var sink = new SqliteScanSink(path);
        sink.OnScanStarted(Device(), cameraMode: false);
        for (int i = 0; i < rows; i++)
        {
            sink.OnFile(FileObject($"f{i}.jpg"), $"/P/f{i}.jpg", FileKind.MediaFile, recovered: false);
        }
        // Dropped on the floor. No Dispose, no OnScanFinished.
    }

    [Fact]
    public void AScanAbandonedInThisProcess_DoesNotBlockTheNextScanForHalfAMinute()
    {
        // A scan whose sink was leaked rather than disposed - an unhandled
        // exception that skips the using, a UI that drops the reference - keeps
        // its write transaction open. The user's obvious response is to unplug
        // and rescan, and that rescan is what this measures.
        using var db = new TempDatabase();
        Abandon(db.Path, 1_200);

        var stopwatch = Stopwatch.StartNew();
        Exception? thrown = null;
        try
        {
            using var next = new SqliteScanSink(db.Path);
            next.OnScanStarted(Device(), cameraMode: false);
            next.OnScanFinished(CleanOutcome());
        }
        catch (Exception e) { thrown = e; }
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"rescan took {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"({(thrown is null ? "succeeded" : thrown.GetType().Name + ": " + thrown.Message)})");
    }

    [Fact]
    public void NoScanRowIsEverCompleteWithoutAFinishedTimestamp()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device("AAA"), cameraMode: false);
            sink.OnScanFinished(StalledOutcome());
        }
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device("BBB"), cameraMode: false);
            sink.OnScanFinished(CleanOutcome() with { CameraMode = true });
        }
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device("CCC"), cameraMode: false);
            sink.OnScanFinished(CleanOutcome() with { Faulted = true });
        }

        Assert.Equal(0L, db.Count("scan", "status = 'complete'"));
        Assert.Equal(0L, db.Count("scan", "status = 'complete' AND finished_utc IS NULL"));
        Assert.Equal(0L, db.Count("scan", "status = 'complete' AND completed = 0"));
        Assert.Equal(0L, db.Count("scan", "status = 'complete' AND (stalled = 1 OR faulted = 1)"));
        Assert.Equal(0L, db.Count("scan", "status = 'complete' AND subtree_losses != 0"));
    }

    [Fact]
    public void EveryFileRowBelongsToARealScan()
    {
        // foreign_keys is ON, but only for the connection that set it. An
        // orphaned file row is a file attributed to no device at all.
        using var db = new TempDatabase();
        Abandon(db.Path, 1_100);

        Assert.Equal(0L, db.Count("file", "scan_id NOT IN (SELECT scan_id FROM scan)"));
        Assert.Equal(0L, db.Count("file", "scan_id = 0"));
    }

    // ========================================================================
    // 9. Volume
    // ========================================================================

    [Fact]
    public void FiftyThousandRows_AreAllWrittenAndQuickly()
    {
        using var db = new TempDatabase();
        var stopwatch = Stopwatch.StartNew();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            for (int i = 0; i < 50_000; i++)
            {
                sink.OnFile(FileObject($"f{i}.jpg"), $"/P/DCIM/Camera/f{i}.jpg",
                    FileKind.MediaFile, recovered: false);
            }
            for (int i = 0; i < 5_000; i++)
            {
                sink.OnFolder(FolderObject($"d{i}"), $"/P/d{i}", recovered: false);
            }
            sink.OnScanFinished(CleanOutcome() with { MediaFiles = 50_000, TotalFilesSeen = 50_000 });
        }
        stopwatch.Stop();

        Assert.Equal(50_000L, db.Count("file"));
        Assert.Equal(5_000L, db.Count("folder"));
        Assert.Equal("complete", db.Text("SELECT status FROM scan"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"55,000 rows took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void ARowLandingExactlyOnTheBatchBoundary_IsNotLost()
    {
        // 1,000 rows is the commit point. Stopping on it, one before it and one
        // after it each exercise a different branch of CountRow.
        foreach (int count in new[] { 999, 1_000, 1_001, 2_000 })
        {
            using var db = new TempDatabase();
            using (var sink = new SqliteScanSink(db.Path))
            {
                sink.OnScanStarted(Device(), cameraMode: false);
                for (int i = 0; i < count; i++)
                {
                    sink.OnFile(FileObject($"f{i}.jpg"), $"/P/f{i}.jpg", FileKind.MediaFile, recovered: false);
                }
                sink.OnScanFinished(CleanOutcome() with { MediaFiles = count, TotalFilesSeen = count });
            }

            Assert.Equal((long)count, db.Count("file"));
            Assert.Equal("complete", db.Text("SELECT status FROM scan"));
        }
    }

    [Fact]
    public void MixedEventTypesShareTheBatchCounterWithoutLosingAny()
    {
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(), cameraMode: false);
            for (int i = 0; i < 1_200; i++)
            {
                sink.OnFile(FileObject($"f{i}.jpg"), $"/P/f{i}.jpg", FileKind.MediaFile, recovered: false);
                sink.OnFolder(FolderObject($"d{i}"), $"/P/d{i}", recovered: false);
                sink.OnFolderSkipped($"/P/s{i}", "cache");
                sink.OnError(new ScanError($"o{i}", "/P", ScanStage.Properties, -2147024891, "denied"));
            }
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1_200L, db.Count("file"));
        Assert.Equal(1_200L, db.Count("folder"));
        Assert.Equal(1_200L, db.Count("skipped_folder"));
        Assert.Equal(1_200L, db.Count("scan_error"));
    }

    // ========================================================================
    // 10. Device identity edges
    // ========================================================================

    [Fact]
    public void ASerialDifferingOnlyByWhitespace_DoesNotSplitOneDeviceIntoTwo()
    {
        // MTP serials arrive with padding on some devices. Two histories for one
        // phone means a comparison against the wrong one reports every file the
        // other history holds as deleted.
        using var db = new TempDatabase();
        foreach (string serial in new[] { "SER123", "SER123 ", " SER123" })
        {
            using var sink = new SqliteScanSink(db.Path);
            sink.OnScanStarted(Device(serial), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1L, db.Count("device"));
    }

    [Fact]
    public void ASerialDifferingOnlyByCase_DoesNotSplitOneDeviceIntoTwo()
    {
        using var db = new TempDatabase();
        foreach (string serial in new[] { "ser123", "SER123" })
        {
            using var sink = new SqliteScanSink(db.Path);
            sink.OnScanStarted(Device(serial), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(1L, db.Count("device"));
    }

    [Fact]
    public void ATurkishDottedISerial_IsNotFoldedIntoADifferentDevice()
    {
        // The opposite risk, and the one that actually loses data: "İ" and "I"
        // must stay two devices whatever the ambient culture does to casing.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            using var db = new TempDatabase();
            foreach (string serial in new[] { "SERI123", "SERİ123", "SERı123", "SERi123" })
            {
                using var sink = new SqliteScanSink(db.Path);
                sink.OnScanStarted(Device(serial), cameraMode: false);
                sink.OnScanFinished(CleanOutcome());
            }

            Assert.Equal(4L, db.Count("device"));
            Assert.Equal(4L, db.Count("scan"));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void AnExtremelyLongSerial_IsStoredWhole()
    {
        using var db = new TempDatabase();
        string serial = new string('S', 32_000);
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(Device(serial), cameraMode: false);
            sink.OnScanFinished(CleanOutcome());
        }

        Assert.Equal(serial.Length, db.Text("SELECT device_key FROM device")!.Length);
    }
}
