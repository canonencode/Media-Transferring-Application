/// <summary>
/// Tests for <c>SessionHealthMonitor</c>, the rolling-batch circuit breaker
/// that decides when signature checks have stopped being worth attempting.
///
/// The two failure modes it sits between:
///   - trip too eagerly, and every unrecognised-extension file for the rest of
///     the scan is written off as "not media" without ever being looked at;
///   - never trip, and a broken session costs ~118ms per doomed read across
///     thousands of files while reporting success.
///
/// The constants under test (batch of 20, threshold 0.75) are private, so these
/// tests pin them through behaviour, which is also the only way a caller could
/// ever have observed them.
/// </summary>
public class SessionHealthMonitorTests
{
    const int BatchSize = 20;

    /// <summary>Feeds n results and returns the rate from the call that tripped, if any.</summary>
    static double? Feed(SessionHealthMonitor monitor, int count, bool errored)
    {
        double? tripped = null;
        for (int i = 0; i < count; i++)
        {
            double? result = monitor.RecordResult(errored);
            if (result is not null) tripped = result;
        }
        return tripped;
    }

    // ---- Starting state ----------------------------------------------------

    [Fact]
    public void FreshMonitor_IsHealthyAndHasSkippedNothing()
    {
        var monitor = new SessionHealthMonitor();

        Assert.False(monitor.CheckingDisabled);
        Assert.Equal(0, monitor.SkippedBecauseDisabled);
    }

    [Fact]
    public void NothingIsReportedBeforeAFullBatchCompletes()
    {
        var monitor = new SessionHealthMonitor();

        // Nineteen straight failures - every single check in the batch so far
        // has errored - and the breaker still says nothing, because a batch is
        // not a batch until it is full.
        for (int i = 0; i < BatchSize - 1; i++)
        {
            Assert.Null(monitor.RecordResult(errored: true));
            Assert.False(monitor.CheckingDisabled);
        }
    }

    // ---- Below the threshold: must NOT trip --------------------------------

    [Theory]
    [InlineData(0)]    // perfect session
    [InlineData(1)]
    [InlineData(10)]   // 50%
    [InlineData(13)]   // 65%
    [InlineData(14)]   // 70% - the last rate that is still tolerated
    public void BelowThreshold_DoesNotTrip(int errorsInBatch)
    {
        var monitor = new SessionHealthMonitor();

        double? tripped = null;
        for (int i = 0; i < BatchSize; i++)
        {
            double? result = monitor.RecordResult(errored: i < errorsInBatch);
            if (result is not null) tripped = result;
        }

        Assert.Null(tripped);
        Assert.False(monitor.CheckingDisabled);
    }

    [Fact]
    public void FourteenOutOfTwenty_IsTheHighestRateStillTolerated()
    {
        // 14/20 = 0.70. Pinned as its own test because it is the exact edge:
        // one more error in the same batch and the breaker fires.
        var monitor = new SessionHealthMonitor();

        Assert.Null(Feed(monitor, 14, errored: true));
        Assert.Null(Feed(monitor, 6, errored: false));
        Assert.False(monitor.CheckingDisabled);
    }

    // ---- At or above the threshold: must trip ------------------------------

    [Fact]
    public void FifteenOutOfTwenty_TripsExactlyAtTheThreshold()
    {
        // 15/20 = 0.75, which is the threshold itself - "at or above", not
        // "above". 0.75 is exactly representable as a double, so this boundary
        // is a real one and not a rounding accident.
        var monitor = new SessionHealthMonitor();

        Assert.Null(Feed(monitor, 15, errored: true));
        Assert.Null(Feed(monitor, 4, errored: false));   // 19 calls so far
        Assert.False(monitor.CheckingDisabled);

        double? rate = monitor.RecordResult(errored: false); // the 20th

        Assert.NotNull(rate);
        Assert.Equal(0.75, rate!.Value, precision: 10);
        Assert.True(monitor.CheckingDisabled);
    }

    [Theory]
    [InlineData(15, 0.75)]
    [InlineData(16, 0.80)]
    [InlineData(18, 0.90)]
    [InlineData(20, 1.00)]
    public void AtOrAboveThreshold_TripsAndReportsTheRate(int errorsInBatch, double expectedRate)
    {
        var monitor = new SessionHealthMonitor();

        double? tripped = null;
        for (int i = 0; i < BatchSize; i++)
        {
            double? result = monitor.RecordResult(errored: i < errorsInBatch);
            if (result is not null) tripped = result;
        }

        Assert.NotNull(tripped);
        Assert.Equal(expectedRate, tripped!.Value, precision: 10);
        Assert.True(monitor.CheckingDisabled);
    }

    [Fact]
    public void TheTripIsReportedOnTheTwentiethCall_NotEarlierOrLater()
    {
        var monitor = new SessionHealthMonitor();

        for (int i = 0; i < BatchSize - 1; i++)
        {
            Assert.Null(monitor.RecordResult(errored: true));
        }

        Assert.NotNull(monitor.RecordResult(errored: true));
    }

    // ---- The batch really is rolling ---------------------------------------

    [Fact]
    public void CountersResetBetweenBatches()
    {
        // The proof that the window rolls rather than accumulating: batch one
        // is twenty clean successes, batch two is fifteen errors. If the
        // monitor kept a running total, the rate on call forty would be
        // 15/40 = 0.375 and nothing would happen. It must instead see 15/20.
        var monitor = new SessionHealthMonitor();

        Assert.Null(Feed(monitor, BatchSize, errored: false));   // calls 1-20
        Assert.False(monitor.CheckingDisabled);

        Assert.Null(Feed(monitor, 15, errored: true));           // calls 21-35
        Assert.Null(Feed(monitor, 4, errored: false));           // calls 36-39
        Assert.False(monitor.CheckingDisabled);

        double? rate = monitor.RecordResult(errored: false);     // call 40

        Assert.NotNull(rate);
        Assert.Equal(0.75, rate!.Value, precision: 10);
    }

    [Fact]
    public void ErrorsDoNotCarryOverIntoTheNextBatch()
    {
        // The other direction: fourteen errors in batch one (70%, tolerated)
        // must not be added to batch two. If they were, one more error early in
        // batch two would push a cumulative count over the line.
        var monitor = new SessionHealthMonitor();

        Assert.Null(Feed(monitor, 14, errored: true));
        Assert.Null(Feed(monitor, 6, errored: false));           // batch one closes at 70%

        Assert.Null(Feed(monitor, 14, errored: true));
        Assert.Null(Feed(monitor, 6, errored: false));           // batch two also 70%

        Assert.False(monitor.CheckingDisabled);
    }

    // ---- Once tripped, it stays tripped ------------------------------------

    [Fact]
    public void OnceTripped_StaysTrippedEvenIfEverythingStartsSucceeding()
    {
        // Deliberate: reconnecting mid-scan was tried and corrupted the walk,
        // so a broken session is not given a second chance within one scan.
        var monitor = new SessionHealthMonitor();
        Feed(monitor, BatchSize, errored: true);
        Assert.True(monitor.CheckingDisabled);

        Feed(monitor, BatchSize * 3, errored: false);

        Assert.True(monitor.CheckingDisabled);
    }

    [Fact]
    public void OnceTripped_ARecoveredBatchIsNotReportedAsATrip()
    {
        var monitor = new SessionHealthMonitor();
        Feed(monitor, BatchSize, errored: true);

        double? afterRecovery = Feed(monitor, BatchSize, errored: false);

        Assert.Null(afterRecovery);
        Assert.True(monitor.CheckingDisabled);
    }

    [Fact]
    public void AnAlreadyTrippedMonitor_StaysQuiet()
    {
        // It used to report a rate every time another failing batch arrived,
        // and the caller prints a multi-line warning whenever a rate comes
        // back. That was harmless only because the single caller happens to
        // check CheckingDisabled first - coupling that breaks the moment a
        // second caller appears.
        var monitor = new SessionHealthMonitor();
        Feed(monitor, BatchSize, errored: true);

        Assert.Null(Feed(monitor, BatchSize, errored: true));
        Assert.True(monitor.CheckingDisabled);
    }

    // ---- Skipped checks -----------------------------------------------------

    [Fact]
    public void SkippedChecksAreCounted()
    {
        // Each one is a file whose type was never determined - a possible
        // missed photo. Counting them is what stops a degraded scan from
        // reporting itself as a clean one.
        var monitor = new SessionHealthMonitor();

        for (int i = 0; i < 174; i++) monitor.RecordSkippedCheck();

        Assert.Equal(174, monitor.SkippedBecauseDisabled);
    }

    [Fact]
    public void SkippedCountIsIndependentOfTheBreakerState()
    {
        // The two counters must not interfere: recording skips does not move
        // the batch along, and recording results does not inflate the skip
        // count. Otherwise a long tail of skipped files could either re-trip
        // the breaker or hide how many files were really missed.
        var monitor = new SessionHealthMonitor();

        Feed(monitor, 19, errored: true);
        for (int i = 0; i < 100; i++) monitor.RecordSkippedCheck();

        Assert.False(monitor.CheckingDisabled);   // skips did not complete the batch
        Assert.Equal(100, monitor.SkippedBecauseDisabled);

        Assert.NotNull(monitor.RecordResult(errored: true));  // the 20th real result
        Assert.True(monitor.CheckingDisabled);
        Assert.Equal(100, monitor.SkippedBecauseDisabled);    // unchanged by the trip
    }

    [Fact]
    public void SkipsRecordedBeforeAnyResult_LeaveTheBreakerHealthy_AndTheBatchEmpty()
    {
        // RecordSkippedCheck is only meaningful after a trip, but nothing stops
        // a caller invoking it first. It must neither trip the breaker nor
        // pre-fill the batch: after 50 skips, the first 19 real results must
        // still be silent and the 20th must be the one that decides.
        var monitor = new SessionHealthMonitor();
        for (int i = 0; i < 50; i++) monitor.RecordSkippedCheck();

        Assert.False(monitor.CheckingDisabled);
        Assert.Equal(50, monitor.SkippedBecauseDisabled);

        Assert.Null(Feed(monitor, BatchSize - 1, errored: true));
        Assert.False(monitor.CheckingDisabled);

        Assert.NotNull(monitor.RecordResult(errored: true));
        Assert.True(monitor.CheckingDisabled);
        Assert.Equal(50, monitor.SkippedBecauseDisabled);
    }

    [Fact]
    public void MonitorsAreIndependentOfEachOther()
    {
        // No static state: two scans in one process must not share a breaker.
        var first = new SessionHealthMonitor();
        var second = new SessionHealthMonitor();

        Feed(first, BatchSize, errored: true);
        first.RecordSkippedCheck();

        Assert.True(first.CheckingDisabled);
        Assert.False(second.CheckingDisabled);
        Assert.Equal(1, first.SkippedBecauseDisabled);
        Assert.Equal(0, second.SkippedBecauseDisabled);
    }

    // ---- The realistic scenario this class was written for ------------------

    [Fact]
    public void TheMeasuredFailureScenario_TripsWithinTheFirstBatch()
    {
        // Reproduces the recorded incident in miniature: a session whose
        // stream-reading channel is dead returns an error for essentially every
        // check (measured: 1,606 of 1,607). The breaker must give up after the
        // first batch of 20 rather than after all 1,607, and every file that
        // arrives afterwards must be counted as skipped rather than quietly
        // classified as "not media".
        var monitor = new SessionHealthMonitor();

        double? rate = Feed(monitor, BatchSize, errored: true);
        Assert.NotNull(rate);
        Assert.Equal(1.0, rate!.Value, precision: 10);

        int remaining = 1607 - BatchSize;
        for (int i = 0; i < remaining; i++) monitor.RecordSkippedCheck();

        Assert.True(monitor.CheckingDisabled);
        Assert.Equal(remaining, monitor.SkippedBecauseDisabled);
    }
}
