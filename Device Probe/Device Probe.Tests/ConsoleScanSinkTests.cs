/// <summary>
/// Tests for <c>ConsoleScanSink</c> - the probe's own rendering of a scan, and
/// the running totals its end-of-scan summary reports.
///
/// The sink is pure apart from writing to Console, so every test here redirects
/// Console.Out to a StringWriter for its duration and restores it afterwards.
/// That is safe under xunit's class-level parallelism only because no other
/// test class writes to the console; keep it that way.
///
/// Why the counters deserve pinning: the summary is the only place a user
/// learns whether a scan can be trusted, and it is built from these numbers. A
/// counter that drifts - Unknown files left out of the total, Undetermined
/// folded into "not media" - is exactly the "scan looks complete while quietly
/// skipping files" failure this project keeps running into.
/// </summary>
public class ConsoleScanSinkTests
{
    // ---- Helpers -----------------------------------------------------------

    static DeviceObject FileObject(string name, string? displayName = null) =>
        new(ObjectId: "o:" + name, Name: name, DisplayName: displayName ?? name,
            IsContainer: false, Size: null, PersistentId: null, ModifiedRaw: null);

    static DeviceObject FolderObject(string name) =>
        new(ObjectId: "o:" + name, Name: name, DisplayName: name,
            IsContainer: true, Size: null, PersistentId: null, ModifiedRaw: null);

    static ScanOutcome CleanOutcome() =>
        new(Completed: true, Stalled: false, Faulted: false, CameraMode: false,
            MediaFiles: 0, Documents: 0, UndeterminedFiles: 0, TotalFilesSeen: 0,
            SubtreeLosses: 0, SignatureChecksRun: 0, CaughtBySignatureOnly: 0,
            SignatureCheckErrors: 0, FilesSkippedByBreaker: 0,
            SignatureCheckingDisabled: false, FilePropertyMisses: 0);

    static DeviceIdentity Device(string friendlyName = "Galaxy A56") =>
        new(WpdId: "dev-id", FriendlyName: friendlyName, SerialNumber: "SER123",
            Manufacturer: "samsung", Model: "SM-A566B");

    static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>
    /// Redirects Console.Out for the lifetime of the object. Lines are split on
    /// the writer's own NewLine so the tests do not depend on the platform's.
    /// </summary>
    sealed class ConsoleCapture : IDisposable
    {
        readonly TextWriter _original = Console.Out;
        readonly StringWriter _writer = new();

        public ConsoleCapture() => Console.SetOut(_writer);

        public string Text => _writer.ToString();

        /// <summary>Every completed line, without the trailing newline of the last one.</summary>
        public string[] Lines => Text.Split(_writer.NewLine).SkipLast(1).ToArray();

        public string NewLine => _writer.NewLine;

        public void Dispose() => Console.SetOut(_original);
    }

    // ---- Starting state ----------------------------------------------------

    [Fact]
    public void FreshSink_HasRecordedNothing()
    {
        var sink = new ConsoleScanSink();

        Assert.Empty(sink.Errors);
        Assert.Empty(sink.SkippedFolders);
    }

    [Fact]
    public void SinksAreIndependentOfEachOther()
    {
        // No static state: two scans in one process must not share totals.
        using var console = new ConsoleCapture();
        var first = new ConsoleScanSink();
        var second = new ConsoleScanSink();

        first.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        first.OnFolderSkipped("/P/.thumbnails", "cache");
        first.OnError(new ScanError("o1", "/P", ScanStage.Properties, unchecked((int)0x80070005), "denied"));

        Assert.Empty(second.SkippedFolders);
        Assert.Empty(second.Errors);
    }

    // ---- Indentation derived from the path ---------------------------------

    [Theory]
    [InlineData("/Phone", 0)]
    [InlineData("/Phone/DCIM", 2)]
    [InlineData("/Phone/DCIM/Camera", 4)]
    [InlineData("/Phone/DCIM/Camera/2024", 6)]
    [InlineData("/Dahili depolama/Android/media/com.whatsapp/WhatsApp", 8)]
    public void Indent_IsTwoSpacesPerLevelBelowTheStorageRoot(string path, int expectedSpaces)
    {
        // The storage root ("/Phone") sits flush left; every '/' after that is
        // one level. Derived from the path rather than a depth parameter on
        // purpose - see the sink's own comment about the retry pass printing
        // every recovered folder at a hardcoded depth of 1.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolder(FolderObject("x"), path, recovered: false);

        Assert.Equal(expectedSpaces, LeadingSpaces(console.Lines.Single()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Phone")]
    [InlineData("Phone.jpg")]
    public void Indent_PathWithNoSlashAtAll_IsFlushLeft_NotNegative(string path)
    {
        // Zero slashes gives depth -1 before the clamp. Without the Max(0, ..)
        // this would be `new string(' ', -2)` and throw mid-scan on a
        // degenerate path, taking the whole tree with it.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolder(FolderObject("x"), path, recovered: false);

        Assert.Equal(0, LeadingSpaces(console.Lines.Single()));
    }

    [Fact]
    public void Indent_DoubledSlash_CountsAsAnExtraLevel()
    {
        // Separators are counted, not segments, so "//Phone" reads as two
        // levels deep. The walk never builds such a path (it always appends
        // "/{name}" to a parent), so this pins the arithmetic rather than
        // endorsing the input: if Indent ever switches to counting segments,
        // this is the test that should change, deliberately.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();
        sink.OnFolder(FolderObject("x"), "//Phone", recovered: false);
        sink.OnFolder(FolderObject("x"), "/Phone//DCIM", recovered: false);

        Assert.Equal(2, LeadingSpaces(console.Lines[0]));
        Assert.Equal(4, LeadingSpaces(console.Lines[1]));
    }

    [Fact]
    public void Indent_OnlyForwardSlashIsASeparator()
    {
        // Documents an asymmetry rather than a bug: FolderPolicy accepts both
        // separators, the sink counts only '/'. That is fine because the walk
        // builds every path with '/', but it is worth knowing if a caller ever
        // hands the sink a Windows-style path.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolder(FolderObject("x"), @"\Phone\DCIM\Camera", recovered: false);

        Assert.Equal(0, LeadingSpaces(console.Lines.Single()));
    }

    [Fact]
    public void Indent_AppliesToFilesAndSkippedFoldersAsWell()
    {
        // A file's path includes its own name, so a file inside /Phone/DCIM
        // sits one level deeper than the [DIR] line for /Phone/DCIM - which is
        // what makes the output read as a tree.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();
        sink.OnFolder(FolderObject("DCIM"), "/Phone/DCIM", recovered: false);
        sink.OnFile(FileObject("IMG_0001.jpg"), "/Phone/DCIM/IMG_0001.jpg", FileKind.MediaFile, recovered: false);
        sink.OnFolderSkipped("/Phone/DCIM/.thumbnails", "cache");
        sink.OnFile(FileObject("x.xyz"), "/Phone/DCIM/x.xyz", FileKind.Undetermined, recovered: false);

        Assert.Equal(2, LeadingSpaces(console.Lines[0]));
        Assert.Equal(4, LeadingSpaces(console.Lines[1]));
        Assert.Equal(4, LeadingSpaces(console.Lines[2]));
        Assert.Equal(4, LeadingSpaces(console.Lines[3]));
    }

    [Fact]
    public void RecoveredFolder_IsIndentedByItsPath_NotAHardcodedDepth()
    {
        // The bug the path-derived indent replaced: the retry pass printed
        // every recovered folder at depth 1 regardless of where it lived.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolder(FolderObject("data"), "/Phone/Android/data", recovered: true);

        string line = console.Lines.Single();
        Assert.Equal(4, LeadingSpaces(line));
        Assert.Equal("[DIR]  data (recovered, now walking its contents)", line.TrimStart());
    }

    // Counting moved to ScanTally, which the walk owns - see ScanTallyTests.
    // This sink renders what it is told and no longer keeps its own totals,
    // so the screen and the database cannot report different numbers.

    // ---- OnFile: what is printed --------------------------------------------

    [Fact]
    public void OnFile_Unknown_PrintsNothing()
    {
        // Thousands of app-data files on a real phone. Still counted by the
        // walk so the total is honest; not printed so the tree stays readable.
        // Both flag values, because the recovered variant has no tag of its own
        // to fall back to.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFile(FileObject("index.db"), "/P/index.db", FileKind.Unknown, recovered: false);
        sink.OnFile(FileObject("index.db"), "/P/index.db", FileKind.Unknown, recovered: true);

        Assert.Equal("", console.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnFile_Undetermined_IsPrintedAsUnchecked(bool recovered)
    {
        // The opposite of Unknown: never examined, so it MUST be visible - any
        // of these could be a photo. And there is deliberately no
        // "[RECOVERED-UNCHECKED]": recovering the object did not get its bytes
        // read, so the flag changes nothing about what the user should know.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFile(FileObject("mystery.xyz"), "/P/mystery.xyz", FileKind.Undetermined, recovered);

        Assert.Equal("[UNCHECKED] mystery.xyz", console.Lines.Single().TrimStart());
    }

    [Theory]
    [InlineData(false, "[MEDIA] IMG_0001.jpg")]
    [InlineData(true, "[RECOVERED-MEDIA] IMG_0001.jpg")]
    public void OnFile_MediaFile_PrintsTheMediaTag(bool recovered, string expectedLine)
    {
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFile(FileObject("IMG_0001.jpg"), "/P/IMG_0001.jpg", FileKind.MediaFile, recovered);

        Assert.Equal(expectedLine, console.Lines.Single().TrimStart());
    }

    [Theory]
    [InlineData(false, "[DOC] contract.pdf")]
    [InlineData(true, "[RECOVERED-DOC] contract.pdf")]
    public void OnFile_Document_PrintsTheDocTag(bool recovered, string expectedLine)
    {
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFile(FileObject("contract.pdf"), "/P/contract.pdf", FileKind.Document, recovered);

        Assert.Equal(expectedLine, console.Lines.Single().TrimStart());
    }

    [Fact]
    public void OnFile_PrintsTheObjectsName_NotItsDisplayName()
    {
        // DeviceObject.Name is WPD_OBJECT_ORIGINAL_FILE_NAME - the real
        // filename, the one the classifier looked at and the one a transfer
        // would write to disk. DisplayName is WPD_OBJECT_NAME, which is only
        // "usually" the same on Android. The tree must show the real one.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFile(FileObject("IMG_0001.jpg", displayName: "Holiday photo"),
            "/P/DCIM/IMG_0001.jpg", FileKind.MediaFile, recovered: false);

        Assert.Equal("[MEDIA] IMG_0001.jpg", console.Lines.Single().TrimStart());
    }

    [Fact]
    public void OnFile_PrintsExactlyOneLinePerPrintedFile()
    {
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFile(FileObject("a.jpg"), "/P/a.jpg", FileKind.MediaFile, recovered: false);
        sink.OnFile(FileObject("b.pdf"), "/P/b.pdf", FileKind.Document, recovered: false);
        sink.OnFile(FileObject("c.db"), "/P/c.db", FileKind.Unknown, recovered: false);
        sink.OnFile(FileObject("d.xyz"), "/P/d.xyz", FileKind.Undetermined, recovered: false);

        Assert.Equal(3, console.Lines.Length);
    }

    // ---- OnFolder -------------------------------------------------------------

    [Theory]
    [InlineData(false, "[DIR]  Camera")]
    [InlineData(true, "[DIR]  Camera (recovered, now walking its contents)")]
    public void OnFolder_PrintsTheDirTag_WithARecoveredNoteWhenApplicable(bool recovered, string expectedLine)
    {
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolder(FolderObject("Camera"), "/P/DCIM/Camera", recovered);

        Assert.Equal(expectedLine, console.Lines.Single().TrimStart());
    }

    [Fact]
    public void OnFolder_PrintsNoFileLines()
    {
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFolder(FolderObject("DCIM"), "/P/DCIM", recovered: false);
        sink.OnFolder(FolderObject("Camera"), "/P/DCIM/Camera", recovered: true);

        Assert.All(console.Lines, l => Assert.Contains("[DIR]", l));
    }

    // ---- OnFolderSkipped ------------------------------------------------------

    [Fact]
    public void OnFolderSkipped_PrintsTheLastPathSegment_AndTheReason()
    {
        // The [SKIP] line is the only trace a skipped folder leaves in the
        // tree, so it must say which folder and why - "never silent".
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolderSkipped("/Phone/DCIM/.thumbnails", "cache folder - derived copies");

        string line = console.Lines.Single();
        Assert.Equal(4, LeadingSpaces(line));
        Assert.Equal("[SKIP] .thumbnails (cache folder - derived copies)", line.TrimStart());
    }

    [Fact]
    public void OnFolderSkipped_RecordsTheFullPathAndReason_ForTheSummary()
    {
        // The list keeps the FULL path, not the tail the tree showed: two
        // .thumbnails folders under different parents must stay distinguishable
        // in the grouped summary at the end.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFolderSkipped("/Phone/DCIM/.thumbnails", "cache");
        sink.OnFolderSkipped("/Phone/Pictures/.thumbnails", "cache");

        Assert.Equal(
            new[] { "/Phone/DCIM/.thumbnails - cache", "/Phone/Pictures/.thumbnails - cache" },
            sink.SkippedFolders);
    }

    [Fact]
    public void OnFolderSkipped_PathWithNoSlash_DisplaysTheWholePath()
    {
        // LastIndexOf returns -1, and -1 + 1 is 0: the whole string is the
        // "tail". Pinned because that arithmetic is easy to get wrong in a way
        // that throws on the one path shape the walk never produces.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolderSkipped("Phone", "reason");

        Assert.Equal("[SKIP] Phone (reason)", console.Lines.Single());
    }

    [Fact]
    public void OnFolderSkipped_PathEndingInASlash_DisplaysAnEmptyName_WithoutThrowing()
    {
        // The other boundary of the slice: LastIndexOf + 1 == Length, which is
        // a legal empty range, not an out-of-range one.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnFolderSkipped("/Phone/DCIM/", "reason");

        Assert.Equal("[SKIP]  (reason)", console.Lines.Single().TrimStart());
    }

    [Fact]
    public void OnFolderSkipped_IsRecordedForTheSummary()
    {
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnFolderSkipped("/P/.thumbnails", "cache");

        Assert.Single(sink.SkippedFolders);
    }

    // ---- OnError --------------------------------------------------------------

    [Fact]
    public void OnError_IsRecorded_AndPrintsNothingInline()
    {
        // Errors are grouped and reported at the end by the caller; printing
        // them inline is how one measured scan produced ~3,000 interleaved
        // error lines and buried the tree.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();
        var error = new ScanError("oid-42", "/P/.Links", ScanStage.EnumerateNext,
            unchecked((int)0x80070490), "Element not found");

        sink.OnError(error);

        Assert.Equal("", console.Text);
        Assert.Equal(new[] { error }, sink.Errors);
    }

    // ---- OnScanStarted --------------------------------------------------------

    [Fact]
    public void OnScanStarted_InCameraMode_WarnsThatResultsCannotBeComplete()
    {
        // PTP mode hides videos and documents at the phone's end, with zero
        // errors reported - the only way the user finds out is this line.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanStarted(Device(), cameraMode: true);

        string line = console.Lines.Single();
        Assert.StartsWith("[WARNING]", line);
        Assert.Contains("PTP", line);
        Assert.Contains("videos and documents", line);
        Assert.Contains("cannot be complete", line);
    }

    [Fact]
    public void OnScanStarted_InNormalMode_PrintsNothing()
    {
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanStarted(Device(), cameraMode: false);

        Assert.Equal("", console.Text);
    }

    // ---- OnScanFinished -------------------------------------------------------

    [Fact]
    public void OnScanFinished_WhenEveryFieldIsClean_ReportsComplete_WithNoReasons()
    {
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanFinished(CleanOutcome());

        Assert.Contains("Scan is COMPLETE", console.Text);
        Assert.DoesNotContain("PARTIAL", console.Text);
        Assert.DoesNotContain("  - ", console.Text);
    }

    // Each row flips exactly one field away from clean and names the phrase
    // its reason line must carry. Every one of these is a case where
    // "COMPLETE" would be a lie the user has no way to detect. (Primitives
    // rather than a ScanOutcome in MemberData: xunit cannot serialise a record,
    // and would then collapse the rows into a single test case.)
    [Theory]
    [InlineData(false, false, false, false, 0, 0, "did not reach the end")]
    [InlineData(true, true, false, false, 0, 0, "stopped responding")]
    [InlineData(true, false, true, false, 0, 0, "stopped on an error")]
    [InlineData(true, false, false, true, 0, 0, "camera (PTP) mode")]
    [InlineData(true, false, false, false, 0, 3, "3 folder(s) could not be listed")]
    [InlineData(true, false, false, false, 174, 0, "174 file(s) were never examined")]
    public void OnScanFinished_AnySingleProblem_ReportsPartial_WithExactlyThatReason(
        bool completed, bool stalled, bool faulted, bool cameraMode,
        int undeterminedFiles, int subtreeLosses, string expectedReasonFragment)
    {
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanFinished(CleanOutcome() with
        {
            Completed = completed,
            Stalled = stalled,
            Faulted = faulted,
            CameraMode = cameraMode,
            UndeterminedFiles = undeterminedFiles,
            SubtreeLosses = subtreeLosses,
        });

        Assert.Contains("Scan is PARTIAL", console.Text);
        Assert.DoesNotContain("COMPLETE", console.Text);

        string[] reasons = console.Lines.Where(l => l.StartsWith("  - ")).ToArray();
        Assert.Single(reasons);
        Assert.Contains(expectedReasonFragment, reasons[0]);
    }

    [Fact]
    public void OnScanFinished_UndeterminedReason_PointsAtTheUncheckedLinesAbove()
    {
        // The count alone is not actionable; the line has to tell the user
        // where in the output to look for the files it is counting.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanFinished(CleanOutcome() with { UndeterminedFiles = 1 });

        Assert.Contains("[UNCHECKED]", console.Text);
    }

    [Fact]
    public void OnScanFinished_EveryProblemAtOnce_ListsEachReasonExactlyOnce()
    {
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanFinished(CleanOutcome() with
        {
            Completed = false, Stalled = true, Faulted = true, CameraMode = true,
            UndeterminedFiles = 9, SubtreeLosses = 2,
        });

        string[] reasons = console.Lines.Where(l => l.StartsWith("  - ")).ToArray();
        Assert.Equal(6, reasons.Length);
        Assert.Equal(6, reasons.Distinct().Count());
        Assert.Contains("Scan is PARTIAL", console.Text);
    }

    [Fact]
    public void OnScanFinished_CompletedButStalled_IsStillPartial()
    {
        // Completed is necessary, not sufficient: a walk can report having
        // reached its end and still have been cut short by the watchdog on
        // the way. The Stalled reason must appear even though "did not reach
        // the end" does not.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanFinished(CleanOutcome() with { Stalled = true });

        Assert.Contains("PARTIAL", console.Text);
        Assert.Contains("stopped responding", console.Text);
        Assert.DoesNotContain("did not reach the end", console.Text);
    }

    [Fact]
    public void OnScanFinished_ZeroCounts_DoNotProduceCountReasons()
    {
        // "0 folder(s) could not be listed" would be noise that makes real
        // reasons easier to skim past. The count lines appear only when > 0.
        //
        // Asserted against the reason lines rather than the whole output: the
        // census block below always says "0 document(s) ... 0 file(s) seen",
        // which is a different statement and belongs there.
        using var console = new ConsoleCapture();
        new ConsoleScanSink().OnScanFinished(CleanOutcome() with { Faulted = true });

        string[] reasons = console.Lines.Where(l => l.StartsWith("  - ")).ToArray();
        Assert.Single(reasons);
        Assert.Contains("stopped on an error", reasons[0]);
        Assert.DoesNotContain(reasons, r => r.Contains("could not be listed"));
        Assert.DoesNotContain(reasons, r => r.Contains("never examined"));
    }

    // ---- The whole tree, end to end -------------------------------------------

    [Fact]
    public void ASmallScan_RendersAsTheExpectedTree()
    {
        // One golden rendering, so a change to the layout - a tag, a space,
        // the indent step - is a conscious decision rather than a surprise.
        using var console = new ConsoleCapture();
        var sink = new ConsoleScanSink();

        sink.OnScanStarted(Device("Phone"), cameraMode: false);
        sink.OnFolder(FolderObject("Phone"), "/Phone", recovered: false);
        sink.OnFolder(FolderObject("DCIM"), "/Phone/DCIM", recovered: false);
        sink.OnFolderSkipped("/Phone/DCIM/.thumbnails", "cache folder");
        sink.OnFolder(FolderObject("Camera"), "/Phone/DCIM/Camera", recovered: false);
        sink.OnFile(FileObject("IMG_0001.jpg"), "/Phone/DCIM/Camera/IMG_0001.jpg", FileKind.MediaFile, recovered: false);
        sink.OnFile(FileObject("IMG_0002.xyz"), "/Phone/DCIM/Camera/IMG_0002.xyz", FileKind.Undetermined, recovered: false);
        sink.OnFile(FileObject(".nomedia"), "/Phone/DCIM/Camera/.nomedia", FileKind.Unknown, recovered: false);
        sink.OnFolder(FolderObject("Documents"), "/Phone/Documents", recovered: true);
        sink.OnFile(FileObject("cv.pdf"), "/Phone/Documents/cv.pdf", FileKind.Document, recovered: true);
        sink.OnScanFinished(CleanOutcome() with
        {
            MediaFiles = 1, Documents = 1, UndeterminedFiles = 1, TotalFilesSeen = 4,
        });

        string nl = console.NewLine;
        string expected =
            "[DIR]  Phone" + nl +
            "  [DIR]  DCIM" + nl +
            "    [SKIP] .thumbnails (cache folder)" + nl +
            "    [DIR]  Camera" + nl +
            "      [MEDIA] IMG_0001.jpg" + nl +
            "      [UNCHECKED] IMG_0002.xyz" + nl +
            "  [DIR]  Documents (recovered, now walking its contents)" + nl +
            "    [RECOVERED-DOC] cv.pdf" + nl +
            "\nScan is PARTIAL - this is not a full picture of the device:" + nl +
            "  - 1 file(s) were never examined (listed above as [UNCHECKED])" + nl +
            "\nDone. 1 media file(s) and 1 document(s) found out of 4 file(s) seen." + nl +
            "(0 of those were caught only by file signature - their extension wasn't recognized.)" + nl +
            "Expensive signature check actually ran on 0 file(s) (out of 4 total)." + nl +
            "Signature check itself errored (not just 'no match') on 0 file(s)." + nl +
            "Session health: OK, signature checking ran normally for the whole scan." + nl +
            "\n1 folder(s) deliberately not walked:" + nl +
            "  /Phone/DCIM/.thumbnails - cache folder" + nl;

        Assert.Equal(expected, console.Text);
        Assert.Equal(new[] { "/Phone/DCIM/.thumbnails - cache folder" }, sink.SkippedFolders);
    }
}
