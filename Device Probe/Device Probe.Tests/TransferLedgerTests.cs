using Microsoft.Data.Sqlite;

/// <summary>
/// Tests for the copy ledger and the rule it feeds.
///
/// This is the answer to the complaint the project started from: a transfer
/// broke halfway and nobody could say what had made it across. So the tests are
/// written around interruption rather than around the happy path - what a
/// pulled cable leaves behind, what a resumed run does with it, and what
/// happens when the phone's copy is no longer the one that was taken.
/// </summary>
public class TransferLedgerTests
{
    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (string s in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(Path + s); } catch (IOException) { }
            }
        }
    }

    static TransferItem Item(string path, long size = 1000, string? modified = "2026-09-22") =>
        new("o:" + path, path, path[(path.LastIndexOf('/') + 1)..], size, modified);

    const string Device = "SER123";

    // ---- What an interrupted transfer leaves behind -------------------------

    [Fact]
    public void ARowOpensAsCopying_BeforeAnyByteIsRead()
    {
        // Written first on purpose. If the process dies during the read, this
        // row is the only thing that says the file was ever attempted.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        ledger.Begin(Device, Item("/P/DCIM/Camera/a.jpg"), @"D:\out\Kamera\a.jpg");

        var record = ledger.Latest(Device, "/P/DCIM/Camera/a.jpg");
        Assert.NotNull(record);
        Assert.Equal("copying", record!.Status);
        Assert.Null(record.Sha256);
    }

    [Fact]
    public void AnUnfinishedCopy_IsCountedSeparately_NotAsSuccessOrFailure()
    {
        // Three states, not two. "Interrupted" is its own answer and the one a
        // resumed run acts on.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        long a = ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");
        long b = ledger.Begin(Device, Item("/P/b.jpg"), @"D:\b.jpg");
        ledger.Begin(Device, Item("/P/c.jpg"), @"D:\c.jpg");   // cable pulled here
        ledger.Complete(a, 1000, "hash-a");
        ledger.Fail(b, "device unreachable");

        var counts = ledger.Counts(Device);
        Assert.Equal(1, counts.Done);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.Unfinished);
        Assert.Equal(1000, counts.Bytes);
    }

    [Fact]
    public void AFailedCopy_IsKept_NotForgotten()
    {
        // A file that could not be taken is still on the phone, and a transfer
        // that quietly drops its failures reports success while leaving things
        // behind.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        long id = ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");
        ledger.Fail(id, "0x80070141 device unreachable");

        var record = ledger.Latest(Device, "/P/a.jpg");
        Assert.Equal("failed", record!.Status);
        Assert.Equal(1, ledger.Counts(Device).Failed);
    }

    [Fact]
    public void CompletingARowThatIsNotThere_FailsLoudly()
    {
        // A silent no-op would mean a file reported as copied with nothing in
        // the ledger to prove it - the exact shape of bug this project keeps
        // finding.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        Assert.Throws<InvalidOperationException>(() => ledger.Complete(999, 10, "hash"));
    }

    // ---- Resuming -----------------------------------------------------------

    [Fact]
    public void TheMostRecentAttemptWins_EvenIfAnEarlierOneSucceeded()
    {
        // Something went back to that file for a reason; an older success is no
        // longer evidence about the state of things.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        long first = ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");
        ledger.Complete(first, 1000, "hash");
        ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");   // retried, interrupted

        Assert.Equal("copying", ledger.Latest(Device, "/P/a.jpg")!.Status);
    }

    [Fact]
    public void AllFor_GivesTheSameAnswerAsAskingOneByOne()
    {
        // The bulk read a transfer uses before it starts. If it disagreed with
        // Latest, a resumed run would make different decisions depending on
        // which way it happened to ask.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        foreach (string p in new[] { "/P/a.jpg", "/P/b.jpg", "/P/c.jpg" })
        {
            long id = ledger.Begin(Device, Item(p), @"D:\x.jpg");
            if (p != "/P/c.jpg") ledger.Complete(id, 1000, "h");
        }
        ledger.Begin(Device, Item("/P/a.jpg"), @"D:\x.jpg");   // a second attempt at a

        var all = ledger.AllFor(Device);
        foreach (string p in new[] { "/P/a.jpg", "/P/b.jpg", "/P/c.jpg" })
        {
            Assert.Equal(ledger.Latest(Device, p)!.Status, all[p].Status);
        }
    }

    [Fact]
    public void OneDevicesLedger_DoesNotAnswerForAnother()
    {
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        long id = ledger.Begin("PHONE-A", Item("/P/a.jpg"), @"D:\a.jpg");
        ledger.Complete(id, 1000, "h");

        Assert.Null(ledger.Latest("PHONE-B", "/P/a.jpg"));
        Assert.Equal(0, ledger.Counts("PHONE-B").Done);
    }

    [Fact]
    public void TheLedgerSurvivesBeingClosedAndReopened()
    {
        // It is the record a transfer resumes from days later, on a machine
        // that has been restarted since.
        using var db = new TempDatabase();
        using (var ledger = new TransferLedger(db.Path))
        {
            long id = ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");
            ledger.Complete(id, 1000, "hash");
        }

        using var reopened = new TransferLedger(db.Path);
        Assert.Equal("done", reopened.Latest(Device, "/P/a.jpg")!.Status);
    }

    [Fact]
    public void TheLedgerSharesTheScannersDatabase_WithoutDisturbingIt()
    {
        // One file, two writers. The copier opening it must not cost the
        // scanner its rows or its schema version.
        using var db = new TempDatabase();
        using (var sink = new SqliteScanSink(db.Path))
        {
            sink.OnScanStarted(new DeviceIdentity("wpd", "A56", Device, "samsung", "SM-A566B"), false);
            sink.OnScanFinished(new ScanOutcome(true, false, false, false, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, false, 0));
        }

        using (var ledger = new TransferLedger(db.Path))
        {
            long id = ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");
            ledger.Complete(id, 1000, "hash");
        }

        using var c = new SqliteConnection($"Data Source={db.Path}");
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT (SELECT COUNT(*) FROM scan), (SELECT COUNT(*) FROM copy);";
        using var r = q.ExecuteReader();
        r.Read();
        Assert.Equal(1, r.GetInt32(0));
        Assert.Equal(1, r.GetInt32(1));
    }

    [Fact]
    public void ALedgerOpenedOnAnEmptyPath_CreatesTheWholeSchema()
    {
        // The copier can be the first thing to touch a database - a transfer
        // resumed onto a machine where no scan has run.
        using var db = new TempDatabase();
        using var ledger = new TransferLedger(db.Path);

        ledger.Begin(Device, Item("/P/a.jpg"), @"D:\a.jpg");

        using var c = new SqliteConnection($"Data Source={db.Path}");
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('scan','file','copy');";
        Assert.Equal(3L, (long)q.ExecuteScalar()!);
    }

    // ---- The decision itself ------------------------------------------------

    static CopyRecord Done(long size = 1000, string? modified = "2026-09-22") =>
        new("/P/a.jpg", size, modified, @"D:\a.jpg", "done", "hash");

    [Fact]
    public void NeverCopied_MeansCopy()
    {
        Assert.Equal(CopyAction.Copy,
            CopyDecision.Decide(null, 1000, "2026-09-22", destinationExists: false, destinationSize: 0));
    }

    [Theory]
    [InlineData("copying")]
    [InlineData("failed")]
    public void AnAttemptThatDidNotFinish_MeansCopyAgain(string status)
    {
        // The .part it left is a prefix of a file, and a prefix of a photograph
        // is not a photograph.
        var previous = Done() with { Status = status };

        Assert.Equal(CopyAction.Copy,
            CopyDecision.Decide(previous, 1000, "2026-09-22", destinationExists: true, destinationSize: 1000));
    }

    [Fact]
    public void CopiedAndStillThereAtTheRightSize_MeansSkip()
    {
        Assert.Equal(CopyAction.Skip,
            CopyDecision.Decide(Done(), 1000, "2026-09-22", destinationExists: true, destinationSize: 1000));
    }

    [Fact]
    public void CopiedButTheFileIsGone_MeansCopyAgain()
    {
        // The ledger records what happened, not what is on disk now. Someone
        // deleted it, or the drive is not attached. The disk wins.
        Assert.Equal(CopyAction.Copy,
            CopyDecision.Decide(Done(), 1000, "2026-09-22", destinationExists: false, destinationSize: 0));
    }

    [Fact]
    public void CopiedButTheFileIsTheWrongLength_MeansCopyAgain()
    {
        // A truncated write, a disk that filled, a crash between writing and
        // renaming. Whatever it was, it is not the file.
        Assert.Equal(CopyAction.Copy,
            CopyDecision.Decide(Done(), 1000, "2026-09-22", destinationExists: true, destinationSize: 640));
    }

    [Theory]
    [InlineData(2000, "2026-09-22")]   // edited: same date, new size
    [InlineData(1000, "2026-09-30")]   // replaced: same size, new date
    [InlineData(2000, "2026-09-30")]
    [InlineData(1000, null)]           // the device stopped reporting a date
    public void ThePhonesCopyChanged_MeansKeepBoth(long size, string? modified)
    {
        // Deciding which version the person wanted is not this program's call,
        // and overwriting is the one answer that can lose something.
        Assert.Equal(CopyAction.CopyAsNewVersion,
            CopyDecision.Decide(Done(), size, modified, destinationExists: true, destinationSize: 1000));
    }

    [Fact]
    public void AMissingDateOnBothSides_IsNotAChange()
    {
        // Some objects have no modified date at all. Treating absent as
        // "different" would recopy them on every single run.
        var previous = Done(modified: null);

        Assert.Equal(CopyAction.Skip,
            CopyDecision.Decide(previous, 1000, null, destinationExists: true, destinationSize: 1000));
    }

    [Fact]
    public void AZeroByteFile_IsHandledLikeAnyOther()
    {
        // Empty files exist on phones, and "size 0" must not read as "missing".
        Assert.Equal(CopyAction.Skip,
            CopyDecision.Decide(Done(size: 0), 0, "2026-09-22", destinationExists: true, destinationSize: 0));
    }
}
