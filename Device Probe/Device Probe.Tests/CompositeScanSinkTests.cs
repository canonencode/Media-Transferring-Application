/// <summary>
/// Tests for <c>CompositeScanSink</c> - the fan-out that lets one walk feed the
/// console and a database at once.
///
/// The behaviour worth pinning is not the fan-out; it is what happens when a
/// sink throws. A scan is 14,000 round trips over hardware already known to
/// wedge, so a full disk must not be able to abort it - but a database that
/// quietly stopped receiving files halfway through is indistinguishable from a
/// phone that only had that many, which is exactly the failure this project
/// exists to prevent. Both halves of that are asserted here.
/// </summary>
public class CompositeScanSinkTests
{
    static DeviceObject FileObject(string name) =>
        new(ObjectId: "o:" + name, Name: name, DisplayName: name,
            IsContainer: false, Size: 10, PersistentId: "p:" + name, ModifiedRaw: null);

    static DeviceIdentity Device() =>
        new("wpd-1", "Galaxy A56", "SER123", "samsung", "SM-A566B");

    static ScanOutcome Outcome() =>
        new(Completed: true, Stalled: false, Faulted: false, CameraMode: false,
            MediaFiles: 1, Documents: 0, UndeterminedFiles: 0, TotalFilesSeen: 1,
            SubtreeLosses: 0, SignatureChecksRun: 0, CaughtBySignatureOnly: 0,
            SignatureCheckErrors: 0, FilesSkippedByBreaker: 0,
            SignatureCheckingDisabled: false, FilePropertyMisses: 0);

    /// <summary>Records every call it receives, and can be told to throw on one of them.</summary>
    sealed class RecordingSink : IScanSink, IDisposable
    {
        public List<string> Calls { get; } = new();
        public string? ThrowOn { get; set; }
        public bool Disposed { get; private set; }

        void Record(string call)
        {
            Calls.Add(call);
            if (ThrowOn == call) throw new InvalidOperationException($"boom in {call}");
        }

        public void OnScanStarted(DeviceIdentity device, bool cameraMode) => Record(nameof(OnScanStarted));
        public void OnScanFinished(ScanOutcome outcome) => Record(nameof(OnScanFinished));
        public void OnFile(DeviceObject obj, string path, FileKind kind, bool recovered) => Record(nameof(OnFile));
        public void OnFolder(DeviceObject obj, string path, bool recovered) => Record(nameof(OnFolder));
        public void OnFolderSkipped(string path, string reason) => Record(nameof(OnFolderSkipped));
        public void OnError(ScanError error) => Record(nameof(OnError));
        public void OnRetryStarted(int objectCount) => Record(nameof(OnRetryStarted));
        public void OnRetryFinished(RetryOutcome outcome) => Record(nameof(OnRetryFinished));
        public void Dispose() => Disposed = true;
    }

    /// <summary>Swallows stderr, which the composite writes its failure notices to.</summary>
    sealed class ErrorCapture : IDisposable
    {
        readonly TextWriter _original = Console.Error;
        readonly StringWriter _writer = new();

        public ErrorCapture() => Console.SetError(_writer);
        public string Text => _writer.ToString();
        public void Dispose() => Console.SetError(_original);
    }

    // ---- Fan-out ------------------------------------------------------------

    [Fact]
    public void EverySinkSeesEveryCall()
    {
        var a = new RecordingSink();
        var b = new RecordingSink();
        using var composite = new CompositeScanSink(a, b);

        composite.OnScanStarted(Device(), cameraMode: false);
        composite.OnFolder(FileObject("DCIM"), "/P/DCIM", recovered: false);
        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        composite.OnFolderSkipped("/P/.thumbnails", "cache");
        composite.OnError(new ScanError("o1", "/P", ScanStage.Properties, 1, "x"));
        composite.OnRetryStarted(2);
        composite.OnRetryFinished(RetryOutcome.WasSkipped(2, "device gone"));
        composite.OnScanFinished(Outcome());

        string[] expected =
        {
            "OnScanStarted", "OnFolder", "OnFile", "OnFolderSkipped",
            "OnError", "OnRetryStarted", "OnRetryFinished", "OnScanFinished"
        };
        Assert.Equal(expected, a.Calls);
        Assert.Equal(expected, b.Calls);
    }

    [Fact]
    public void WithNoSinksAtAll_EveryCallIsHarmless()
    {
        // The scanner falls back to this when the database cannot be opened and
        // the console sink is the only one left; zero is the degenerate case of
        // the same path and must not throw.
        using var composite = new CompositeScanSink();

        composite.OnScanStarted(Device(), cameraMode: false);
        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        composite.OnScanFinished(Outcome());

        Assert.Equal(0, composite.LiveCount);
        Assert.Empty(composite.Faults);
    }

    // ---- Fault isolation ----------------------------------------------------

    [Fact]
    public void AThrowingSink_DoesNotStopTheOthers()
    {
        using var stderr = new ErrorCapture();
        var broken = new RecordingSink { ThrowOn = "OnFile" };
        var healthy = new RecordingSink();
        using var composite = new CompositeScanSink(broken, healthy);

        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);

        Assert.Equal(new[] { "OnFile" }, healthy.Calls);
    }

    [Fact]
    public void AThrowingSink_IsDroppedRatherThanReEntered()
    {
        // Re-entering a broken sink 14,000 more times turns one failure into a
        // 14,000-line error log and a scan slowed to a crawl.
        using var stderr = new ErrorCapture();
        var broken = new RecordingSink { ThrowOn = "OnFile" };
        using var composite = new CompositeScanSink(broken);

        for (int i = 0; i < 5; i++)
        {
            composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        }

        Assert.Single(broken.Calls);
        Assert.Equal(0, composite.LiveCount);
    }

    [Fact]
    public void AThrowingSink_IsReportedLoudly_NotSwallowed()
    {
        // Silence here is the dangerous outcome: a half-written database that
        // nobody was told about reads exactly like a complete one.
        using var stderr = new ErrorCapture();
        var broken = new RecordingSink { ThrowOn = "OnFile" };
        using var composite = new CompositeScanSink(broken);

        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);

        var fault = Assert.Single(composite.Faults);
        Assert.Equal(nameof(RecordingSink), fault.SinkType);
        Assert.Equal("OnFile", fault.Call);
        Assert.Contains("boom in OnFile", fault.Message);
        Assert.Contains("[SINK FAILED]", stderr.Text);
    }

    [Fact]
    public void AFaultIsRepeatedAfterTheSummary_WhereItCannotBeScrolledPast()
    {
        using var stderr = new ErrorCapture();
        var broken = new RecordingSink { ThrowOn = "OnFile" };
        using var composite = new CompositeScanSink(broken, new RecordingSink());

        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        composite.OnScanFinished(Outcome());

        // Once when it happened, once at the end - a notice printed 14,000
        // files ago is a notice nobody reads.
        Assert.Equal(2, stderr.Text.Split("[SINK FAILED]").Length - 1);
        Assert.Contains("did not record this scan", stderr.Text);
    }

    [Fact]
    public void OneSinkFailingLate_DoesNotRemoveTheOtherFromTheSummary()
    {
        // Removal happens mid-iteration; a forward loop would skip the sink
        // that shifted into the freed slot.
        using var stderr = new ErrorCapture();
        var first = new RecordingSink { ThrowOn = "OnFile" };
        var second = new RecordingSink();
        var third = new RecordingSink();
        using var composite = new CompositeScanSink(first, second, third);

        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        composite.OnScanFinished(Outcome());

        Assert.Equal(2, composite.LiveCount);
        Assert.Equal(new[] { "OnFile", "OnScanFinished" }, second.Calls);
        Assert.Equal(new[] { "OnFile", "OnScanFinished" }, third.Calls);
    }

    [Fact]
    public void ASinkThatThrowsOnScanFinished_IsStillDisposed()
    {
        using var stderr = new ErrorCapture();
        var broken = new RecordingSink { ThrowOn = "OnScanFinished" };
        var composite = new CompositeScanSink(broken);

        composite.OnScanFinished(Outcome());
        composite.Dispose();

        // A faulted sink still owns whatever it had open - an open transaction,
        // a file handle - and leaving it behind is how the next run finds a
        // locked database.
        Assert.True(broken.Disposed);
    }

    [Fact]
    public void Dispose_ReachesEverySink_EvenOnesDroppedEarlier()
    {
        using var stderr = new ErrorCapture();
        var broken = new RecordingSink { ThrowOn = "OnFile" };
        var healthy = new RecordingSink();
        var composite = new CompositeScanSink(broken, healthy);

        composite.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        composite.Dispose();

        Assert.True(broken.Disposed);
        Assert.True(healthy.Disposed);
    }
}
