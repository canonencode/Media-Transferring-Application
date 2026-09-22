namespace MediaTransfer.Core;

/// <summary>
/// The walk's own running count of what it has seen. Owned by the walk, not by
/// a sink: <see cref="ScanOutcome"/> has to be true regardless of which sinks
/// are attached, and an earlier version built it by reading counters back off
/// the console sink - which quietly made that one sink the scan's memory and
/// left no room for a second one beside it.
///
/// It lives here, in the COM-free library, rather than as a handful of ints in
/// the scanner, because everything in the scanner needs a physical phone to
/// exercise. These are the numbers the user is shown to decide whether a scan
/// can be trusted; they deserve tests that run without hardware.
/// </summary>
public sealed class ScanTally
{
    readonly List<ScanError> errors = new();

    // Containers whose listing failed during the main walk but which the retry
    // pass then walked successfully. Kept as a set of ids rather than a flag on
    // the error, because the walk learns of the recovery in a different place
    // from where it recorded the failure.
    readonly HashSet<string> recoveredContainers = new();

    // Objects whose PROPERTIES the retry pass managed to read. A different
    // question from whether a container could be listed, and answered in a
    // different place, so it gets its own set: an object can have its
    // properties read and still fail to enumerate.
    readonly HashSet<string> resolvedObjects = new();

    public int MediaFiles { get; private set; }
    public int Documents { get; private set; }
    public int UndeterminedFiles { get; private set; }

    /// <summary>
    /// Every file the walk resolved, including the ones it decided were
    /// irrelevant. The difference between this and the three counts above is
    /// the number of files deliberately ignored - which is only meaningful
    /// because they were all actually looked at.
    /// </summary>
    public int TotalFilesSeen { get; private set; }

    public IReadOnlyList<ScanError> Errors => errors;

    /// <summary>
    /// Failures while LISTING a folder, minus the ones the retry pass got back.
    /// Each remaining one hid an unknown number of files, so it is the figure
    /// that decides whether a scan may be called complete.
    ///
    /// Recomputed rather than kept as a counter: the walk records failures and
    /// recoveries in that order, but nothing guarantees it, and a decrement
    /// that arrives for an id that never failed - or twice for one that did -
    /// would drive the count negative and quietly turn a lossy scan into a
    /// clean one.
    /// </summary>
    public int SubtreeLosses => errors.Count(e =>
        e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext &&
        !recoveredContainers.Contains(e.ObjectId));

    /// <summary>
    /// Objects whose properties could never be read, so the walk never learned
    /// their name or even whether they were files or folders - minus the ones
    /// the retry pass got back.
    ///
    /// Not the same as SubtreeLosses, and not harmless. An unresolved object
    /// that happened to be a folder took its entire contents with it, silently:
    /// the walk cannot recurse into something it could not identify. That is
    /// what RetryOutcome.HiddenSubtrees counts, and every one of those is also
    /// counted here - so a verdict that respects this figure covers them too.
    /// </summary>
    public int UnresolvedObjects => errors.Count(e =>
        e.Stage is ScanStage.Properties &&
        !resolvedObjects.Contains(e.ObjectId));

    public void CountFile(FileKind kind)
    {
        TotalFilesSeen++;
        switch (kind)
        {
            case FileKind.MediaFile: MediaFiles++; break;
            case FileKind.Document: Documents++; break;
            case FileKind.Undetermined: UndeterminedFiles++; break;
            // Unknown lands in the total only: examined, and irrelevant. That
            // is a different thing from Undetermined, which was never examined
            // at all and could be anything - including a photograph.
        }
    }

    public void RecordError(ScanError error) => errors.Add(error);

    /// <summary>
    /// The retry pass listed this container after all, so whatever failure was
    /// recorded against it no longer hides anything. Safe to call for an id
    /// that never failed, and safe to call twice.
    ///
    /// PRECONDITION: call it only after the re-walk actually SUCCEEDED. This
    /// clears every recorded failure for that id, including one logged moments
    /// ago by a re-walk that failed again - so calling it optimistically drives
    /// SubtreeLosses to zero for a folder that was never listed and lets the
    /// scan call itself complete. The retry pass used to do exactly that; it
    /// now checks the tally for a fresh failure before calling this.
    /// </summary>
    public void MarkContainerRecovered(string objectId) => recoveredContainers.Add(objectId);

    /// <summary>
    /// The retry pass read this object's properties after all, so the earlier
    /// Properties failure no longer hides anything. Says nothing about whether
    /// the object could then be LISTED - that is MarkContainerRecovered, and a
    /// folder can easily answer the first question and not the second.
    /// </summary>
    public void MarkObjectResolved(string objectId) => resolvedObjects.Add(objectId);

    /// <summary>
    /// Assembles the record the sinks are given. The three arguments are the
    /// figures the walk cannot derive from its own event stream: how much the
    /// signature path was used, and how the session held up.
    /// </summary>
    public ScanOutcome BuildOutcome(
        bool completed,
        bool stalled,
        bool faulted,
        bool cameraMode,
        SignatureStats signatures,
        int filePropertyMisses) => new(
            Completed: completed,
            Stalled: stalled,
            Faulted: faulted,
            CameraMode: cameraMode,
            MediaFiles: MediaFiles,
            Documents: Documents,
            UndeterminedFiles: UndeterminedFiles,
            TotalFilesSeen: TotalFilesSeen,
            SubtreeLosses: SubtreeLosses,
            UnresolvedObjects: UnresolvedObjects,
            SignatureChecksRun: signatures.ChecksRun,
            CaughtBySignatureOnly: signatures.CaughtBySignatureOnly,
            SignatureCheckErrors: signatures.CheckErrors,
            FilesSkippedByBreaker: signatures.SkippedByBreaker,
            SignatureCheckingDisabled: signatures.CheckingDisabled,
            FilePropertyMisses: filePropertyMisses);
}

/// <summary>
/// How much the expensive path - reading a file's actual bytes - was used, and
/// whether the session survived it. Grouped into one record so the walk hands
/// over a set of related figures instead of five loose arguments that are easy
/// to pass in the wrong order.
/// </summary>
public readonly record struct SignatureStats(
    int ChecksRun,
    int CaughtBySignatureOnly,
    int CheckErrors,
    int SkippedByBreaker,
    bool CheckingDisabled);
