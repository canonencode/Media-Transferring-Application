/// <summary>
/// Tests for <c>ScanTally</c> - the walk's own count of what it saw, and the
/// arithmetic behind the verdict the user reads.
///
/// These numbers decide whether a scan is called COMPLETE. A counter that
/// drifts here does not crash anything; it produces a scan that claims to have
/// seen the whole phone when it did not, which is the failure this project
/// exists to prevent. The counting used to live in the console sink, where it
/// could only be reached by attaching that sink - and the SubtreeLosses
/// arithmetic lived in the scanner, where it could not be tested at all.
/// </summary>
public class ScanTallyTests
{
    static ScanError Error(string objectId, ScanStage stage, string path = "/P") =>
        new(objectId, path, stage, unchecked((int)0x80070490), "Element not found");

    // ---- Counting -----------------------------------------------------------

    [Fact]
    public void FreshTally_IsAllZeroes()
    {
        var tally = new ScanTally();

        Assert.Equal(0, tally.TotalFilesSeen);
        Assert.Equal(0, tally.MediaFiles);
        Assert.Equal(0, tally.Documents);
        Assert.Equal(0, tally.UndeterminedFiles);
        Assert.Equal(0, tally.SubtreeLosses);
        Assert.Empty(tally.Errors);
    }

    [Fact]
    public void CountFile_CountsEveryKind_InTotalFilesSeen()
    {
        // Including Unknown. TotalFilesSeen is what the other counters are read
        // against; leave Unknown out of it and "17,478 files, 2,300 media"
        // silently becomes "2,300 files, 2,300 media" - a summary that reads as
        // a clean sweep of a phone it barely looked at.
        var tally = new ScanTally();

        tally.CountFile(FileKind.MediaFile);
        tally.CountFile(FileKind.Document);
        tally.CountFile(FileKind.Unknown);
        tally.CountFile(FileKind.Undetermined);

        Assert.Equal(4, tally.TotalFilesSeen);
        Assert.Equal(1, tally.MediaFiles);
        Assert.Equal(1, tally.Documents);
        Assert.Equal(1, tally.UndeterminedFiles);
    }

    [Fact]
    public void CountFile_EachCounterMovesOnlyForItsOwnKind()
    {
        var tally = new ScanTally();

        for (int i = 0; i < 3; i++) tally.CountFile(FileKind.MediaFile);
        for (int i = 0; i < 2; i++) tally.CountFile(FileKind.Document);
        for (int i = 0; i < 5; i++) tally.CountFile(FileKind.Unknown);
        for (int i = 0; i < 7; i++) tally.CountFile(FileKind.Undetermined);

        Assert.Equal(17, tally.TotalFilesSeen);
        Assert.Equal(3, tally.MediaFiles);
        Assert.Equal(2, tally.Documents);
        Assert.Equal(7, tally.UndeterminedFiles);
        // There is no Unknown counter; it is the remainder, and this is how the
        // summary derives it.
        Assert.Equal(5, tally.TotalFilesSeen - tally.MediaFiles - tally.Documents - tally.UndeterminedFiles);
    }

    [Fact]
    public void TalliesAreIndependentOfEachOther()
    {
        // No static state: two scans in one process must not share totals.
        var first = new ScanTally();
        var second = new ScanTally();

        first.CountFile(FileKind.MediaFile);
        first.RecordError(Error("o1", ScanStage.Enumerate));

        Assert.Equal(0, second.TotalFilesSeen);
        Assert.Empty(second.Errors);
    }

    // ---- SubtreeLosses ------------------------------------------------------

    [Theory]
    [InlineData(ScanStage.Enumerate, 1)]
    [InlineData(ScanStage.EnumerateNext, 1)]
    [InlineData(ScanStage.Properties, 0)]
    public void SubtreeLosses_CountsOnlyListingFailures(ScanStage stage, int expected)
    {
        // The distinction is the whole point: failing to LIST a folder loses an
        // unknown number of files beneath it, while failing to read one
        // object's properties loses that object. A flat error count cannot tell
        // the user which of those happened.
        var tally = new ScanTally();

        tally.RecordError(Error("o1", stage));

        Assert.Equal(expected, tally.SubtreeLosses);
        Assert.Single(tally.Errors);
    }

    [Fact]
    public void SubtreeLosses_ExcludesContainersTheRetryPassGotBack()
    {
        // The bug this pins: a folder that failed during the main walk and was
        // then successfully re-walked was still reported as "1 folder(s) could
        // not be listed, losing everything beneath them" - telling the user
        // photographs were gone while those very files were listed on screen.
        var tally = new ScanTally();
        tally.RecordError(Error("lost", ScanStage.Enumerate));
        tally.RecordError(Error("recovered", ScanStage.EnumerateNext));

        Assert.Equal(2, tally.SubtreeLosses);

        tally.MarkContainerRecovered("recovered");

        Assert.Equal(1, tally.SubtreeLosses);
        // The error itself is NOT forgotten - it happened, and the grouped
        // error report still shows it. Only the loss count changes.
        Assert.Equal(2, tally.Errors.Count);
    }

    [Fact]
    public void MarkContainerRecovered_IsIdempotent_AndSafeForUnknownIds()
    {
        // The walk records failures and recoveries from different places, and
        // nothing orders them. A decrementing counter would go negative on a
        // double recovery and turn a lossy scan into a clean one; recomputing
        // from a set cannot.
        var tally = new ScanTally();
        tally.RecordError(Error("a", ScanStage.Enumerate));

        tally.MarkContainerRecovered("a");
        tally.MarkContainerRecovered("a");
        tally.MarkContainerRecovered("never-failed");

        Assert.Equal(0, tally.SubtreeLosses);
    }

    [Fact]
    public void SubtreeLosses_RecoveryBeforeTheFailureIsRecorded_StillCounts()
    {
        // Order-independent on purpose. The retry pass can report a container
        // recovered for an id whose failure is recorded by another path, and a
        // one-shot decrement applied too early would simply be lost.
        var tally = new ScanTally();

        tally.MarkContainerRecovered("a");
        tally.RecordError(Error("a", ScanStage.Enumerate));

        Assert.Equal(0, tally.SubtreeLosses);
    }

    [Fact]
    public void SubtreeLosses_SameContainerFailingTwice_BothAreExcludedOnRecovery()
    {
        // Enumerate then EnumerateNext on one folder is a real pattern: the
        // listing starts and dies partway. Recovering it clears both.
        var tally = new ScanTally();
        tally.RecordError(Error("a", ScanStage.Enumerate));
        tally.RecordError(Error("a", ScanStage.EnumerateNext));

        Assert.Equal(2, tally.SubtreeLosses);

        tally.MarkContainerRecovered("a");

        Assert.Equal(0, tally.SubtreeLosses);
    }

    // ---- BuildOutcome -------------------------------------------------------

    static SignatureStats Stats() => new(
        ChecksRun: 5, CaughtBySignatureOnly: 2, CheckErrors: 1,
        SkippedByBreaker: 3, CheckingDisabled: true);

    [Fact]
    public void BuildOutcome_CarriesEveryFigureTheSinksNeed()
    {
        var tally = new ScanTally();
        tally.CountFile(FileKind.MediaFile);
        tally.CountFile(FileKind.Document);
        tally.CountFile(FileKind.Undetermined);
        tally.CountFile(FileKind.Unknown);
        tally.RecordError(Error("o1", ScanStage.Enumerate));

        var outcome = tally.BuildOutcome(
            completed: true, stalled: false, faulted: false, cameraMode: true,
            signatures: Stats(), filePropertyMisses: 7);

        Assert.True(outcome.Completed);
        Assert.False(outcome.Stalled);
        Assert.False(outcome.Faulted);
        Assert.True(outcome.CameraMode);
        Assert.Equal(1, outcome.MediaFiles);
        Assert.Equal(1, outcome.Documents);
        Assert.Equal(1, outcome.UndeterminedFiles);
        Assert.Equal(4, outcome.TotalFilesSeen);
        Assert.Equal(1, outcome.SubtreeLosses);
        Assert.Equal(5, outcome.SignatureChecksRun);
        Assert.Equal(2, outcome.CaughtBySignatureOnly);
        Assert.Equal(1, outcome.SignatureCheckErrors);
        Assert.Equal(3, outcome.FilesSkippedByBreaker);
        Assert.True(outcome.SignatureCheckingDisabled);
        Assert.Equal(7, outcome.FilePropertyMisses);
    }

    [Fact]
    public void BuildOutcome_TwiceFromOneTally_AgreesWithItself()
    {
        // Reported from two places - the watchdog just before it kills the
        // process, and the normal finally. Those two used to assemble the
        // record separately and disagreed: only one of them excluded recovered
        // folders, so the same scan was lossy or clean depending on which
        // thread got there first.
        var tally = new ScanTally();
        tally.CountFile(FileKind.MediaFile);
        tally.RecordError(Error("a", ScanStage.Enumerate));
        tally.MarkContainerRecovered("a");

        var fromWatchdog = tally.BuildOutcome(false, true, false, false, Stats(), 0);
        var fromFinally = tally.BuildOutcome(false, true, false, false, Stats(), 0);

        Assert.Equal(fromWatchdog, fromFinally);
        Assert.Equal(0, fromWatchdog.SubtreeLosses);
    }

    // ---- IsTrustworthy ------------------------------------------------------

    static ScanOutcome Clean(ScanTally? tally = null) =>
        (tally ?? new ScanTally()).BuildOutcome(
            completed: true, stalled: false, faulted: false, cameraMode: false,
            signatures: new SignatureStats(0, 0, 0, 0, false), filePropertyMisses: 0);

    [Fact]
    public void IsTrustworthy_OnlyWhenNothingCastsDoubt()
    {
        Assert.True(Clean().IsTrustworthy);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Stalled")]
    [InlineData("Faulted")]
    [InlineData("CameraMode")]
    [InlineData("UndeterminedFiles")]
    [InlineData("SubtreeLosses")]
    public void IsTrustworthy_AnySingleDoubt_MakesItFalse(string field)
    {
        // Deliberately strict. "Mostly complete" is what lets a later
        // comparison conclude that files it never looked at were deleted from
        // the phone.
        var outcome = field switch
        {
            "Completed" => Clean() with { Completed = false },
            "Stalled" => Clean() with { Stalled = true },
            "Faulted" => Clean() with { Faulted = true },
            "CameraMode" => Clean() with { CameraMode = true },
            "UndeterminedFiles" => Clean() with { UndeterminedFiles = 1 },
            "SubtreeLosses" => Clean() with { SubtreeLosses = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.False(outcome.IsTrustworthy);
    }

    [Fact]
    public void IsTrustworthy_IgnoresFiguresThatAreNotDoubts()
    {
        // A scan that read a lot of signatures, or found files missing an
        // optional property, is still a complete scan. Folding these in would
        // mark healthy scans partial and teach the user to ignore the verdict.
        var outcome = Clean() with
        {
            SignatureChecksRun = 710,
            CaughtBySignatureOnly = 620,
            FilePropertyMisses = 3,
            MediaFiles = 620,
            TotalFilesSeen = 772,
        };

        Assert.True(outcome.IsTrustworthy);
    }

    [Fact]
    public void IsTrustworthy_IsFalse_WhenTheBreakerTripped_ButOnlyViaTheFilesItCost()
    {
        // The breaker tripping is not itself a doubt: what matters is the files
        // it left unchecked, and those are already counted as Undetermined by
        // the walk. Pinned so that a future change does not quietly start
        // treating a tripped breaker as harmless when files went unexamined.
        Assert.True((Clean() with { SignatureCheckingDisabled = true }).IsTrustworthy);
        Assert.False((Clean() with { SignatureCheckingDisabled = true, UndeterminedFiles = 4 }).IsTrustworthy);
    }
}
