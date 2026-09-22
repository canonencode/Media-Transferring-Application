namespace MediaTransfer.Core;

/// <summary>
/// Watches the failure rate of content-stream reads and gives up on them once
/// it is clear the session itself is broken rather than the files being
/// uninteresting.
///
/// Why this exists: a WPD session's stream-reading channel can break silently
/// while property queries keep working perfectly. Measured once at 1,606 of
/// 1,607 signature checks failing with "device unreachable" after a previous
/// run had not closed its session cleanly - and because a failed check and a
/// genuine "this file is not media" look identical unless counted separately,
/// the scan reported success while quietly missing 174 real files.
///
/// Pure: it is fed booleans and returns a decision. No COM, no console. That
/// makes the thing that decides whether to stop looking at file contents
/// testable without a phone, which it previously was not.
/// </summary>
public sealed class SessionHealthMonitor
{
    // A rolling batch rather than a running total: a session usually breaks
    // partway through, so a total would be diluted by all the successes that
    // came before and would take far too long to cross any sensible threshold.
    const int BatchSize = 20;
    const double ErrorThreshold = 0.75;

    int _checksInBatch;
    int _errorsInBatch;

    /// <summary>True once the session looks broken and checks have been abandoned.</summary>
    public bool CheckingDisabled { get; private set; }

    /// <summary>
    /// Files that reached the check after it had been disabled. Each one is a
    /// file whose type was never determined - a possible missed photo, and
    /// worth reporting as such rather than silently counting as "not media".
    /// </summary>
    public int SkippedBecauseDisabled { get; private set; }

    public void RecordSkippedCheck() => SkippedBecauseDisabled++;

    /// <summary>
    /// Records one completed check. Returns the error rate that tripped the
    /// breaker, or null if it did not trip on this call.
    /// </summary>
    public double? RecordResult(bool errored)
    {
        // Once tripped, stay quiet. Reporting again would reprint the whole
        // multi-line warning; today that is masked only because the caller
        // happens to check CheckingDisabled first, which is the kind of
        // coupling that breaks the moment someone adds a second caller.
        if (CheckingDisabled) return null;

        _checksInBatch++;
        if (errored) _errorsInBatch++;
        if (_checksInBatch < BatchSize) return null;

        double errorRate = (double)_errorsInBatch / _checksInBatch;
        _checksInBatch = 0;
        _errorsInBatch = 0;

        if (errorRate < ErrorThreshold) return null;

        // NOT attempted here: reconnecting mid-scan. It was implemented and
        // tested, and it corrupted the walk - a run that should have seen
        // ~19,000 files saw ~17,500 - because PrintTree's recursion holds
        // enumerators from the old session on outer stack frames, and swapping
        // the session out from under them breaks the rest of the tree. A live
        // reconnect is only safe between whole scans, never inside one.
        CheckingDisabled = true;
        return errorRate;
    }
}
