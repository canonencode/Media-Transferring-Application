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

    /// <summary>
    /// How much time the breaker will let checks burn without finding anything
    /// before it stops making them.
    ///
    /// Budgeting WASTED time rather than total time is the whole point. A
    /// content read costs about 13 ms on one measured phone and 278 ms on
    /// another - a twentyfold spread - so a flat time cap would cut off the
    /// slow device, which is exactly the one where extension matching is most
    /// likely to be failing too. On an e-reader we measured, 710 of 772 files
    /// needed a content read and every one of the 620 media files was found
    /// that way; a total-time cap would have stopped that scan partway and
    /// reported the device as nearly empty.
    ///
    /// So a check that identifies something clears the meter: the path is
    /// paying for itself and may keep going for as long as it keeps doing so.
    /// Only a run of reads that find nothing runs the budget down.
    /// </summary>
    public static readonly TimeSpan WastedTimeBudget = TimeSpan.FromSeconds(30);

    int _checksInBatch;
    int _errorsInBatch;
    TimeSpan _wasted;

    /// <summary>Time spent on checks since the last one that identified a file.</summary>
    public TimeSpan WastedTime => _wasted;

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
    /// Records one completed check. Returns why the breaker tripped, or null if
    /// it did not trip on this call.
    /// </summary>
    /// <param name="errored">The read failed, as opposed to finding no match.</param>
    /// <param name="cost">How long the read took.</param>
    /// <param name="identifiedFile">
    /// The check worked out what the file was - media or a document. This is
    /// what "the read earned its cost" means; finding nothing is a legitimate
    /// answer but not a productive one.
    /// </param>
    public BreakerTrip? RecordResult(bool errored, TimeSpan cost, bool identifiedFile)
    {
        // Once tripped, stay quiet. Reporting again would reprint the whole
        // multi-line warning; today that is masked only because the caller
        // happens to check CheckingDisabled first, which is the kind of
        // coupling that breaks the moment someone adds a second caller.
        if (CheckingDisabled) return null;

        // A failed read costs time too, but its time is not what is wrong with
        // it - the failure is, and that is the error rule's business. Letting
        // failures fill the time budget would trip the wrong breaker on a
        // device that is both slow and broken, and tell the user the reads are
        // "not finding anything" when in fact they are not working at all.
        if (identifiedFile) _wasted = TimeSpan.Zero;
        else if (!errored && cost > TimeSpan.Zero) _wasted += cost;

        _checksInBatch++;
        if (errored) _errorsInBatch++;

        // The error rate is checked first because it is the more useful
        // diagnosis when both apply: it means the session is broken and tells
        // the user to replug the device, where the time budget only means the
        // reads are not paying off.
        if (_checksInBatch >= BatchSize)
        {
            double errorRate = (double)_errorsInBatch / _checksInBatch;
            _checksInBatch = 0;
            _errorsInBatch = 0;

            if (errorRate >= ErrorThreshold)
            {
                // NOT attempted here: reconnecting mid-scan. It was implemented
                // and tested, and it corrupted the walk - a run that should
                // have seen ~19,000 files saw ~17,500 - because PrintTree's
                // recursion holds enumerators from the old session on outer
                // stack frames, and swapping the session out from under them
                // breaks the rest of the tree. A live reconnect is only safe
                // between whole scans, never inside one.
                CheckingDisabled = true;
                return new BreakerTrip(BreakerCause.Errors, errorRate, _wasted);
            }
        }

        if (_wasted < WastedTimeBudget) return null;

        CheckingDisabled = true;
        return new BreakerTrip(BreakerCause.WastedTime, 0, _wasted);
    }
}

/// <summary>Why <see cref="SessionHealthMonitor"/> stopped reading file contents.</summary>
public enum BreakerCause
{
    /// <summary>
    /// The reads are failing rather than finding nothing. The session's stream
    /// channel is broken while property queries keep working, which is a state
    /// only replugging the device or restarting WPDBusEnum clears.
    /// </summary>
    Errors,

    /// <summary>
    /// The reads work and keep finding nothing, having cost more time than they
    /// are worth. Says nothing bad about the device - only that on this one,
    /// content detection is not earning its keep.
    /// </summary>
    WastedTime
}

/// <param name="ErrorRate">The rate that tripped it, or 0 when time did.</param>
/// <param name="Wasted">Time spent on checks since the last one that identified a file.</param>
public sealed record BreakerTrip(BreakerCause Cause, double ErrorRate, TimeSpan Wasted);
