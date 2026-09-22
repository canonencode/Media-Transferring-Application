namespace MediaTransfer.Core;

/// <summary>
/// Where a scan's findings go. The walk reports to one of these instead of
/// writing to the console, which is the seam the whole next milestone depends
/// on: a database cannot read the screen. The same walk can then feed a console
/// probe, a SQLite writer, or later a UI, without knowing which.
///
/// Paths are the full accumulated path ("/Dahili depolama/DCIM/Camera"), so a
/// sink that wants indentation can derive it and one that wants rows does not
/// have to care about depth at all.
///
/// Every figure a sink needs to render or store arrives through these calls or
/// on <see cref="ScanOutcome"/>. That rule is what makes a second sink
/// possible: an earlier version had the walk reading counters back off the
/// console sink, which meant the console sink WAS the scan's memory and nothing
/// else could be plugged in beside it.
/// </summary>
public interface IScanSink
{
    /// <summary>Called once before the walk begins.</summary>
    void OnScanStarted(DeviceIdentity device, bool cameraMode);

    /// <summary>
    /// Called once when the walk ends, however it ends. Without this a sink
    /// cannot tell a complete census from a scan the watchdog cut short - and
    /// writing a partial scan into a manifest as though it were complete is
    /// how an app "forgets" files that are really still on the phone, which is
    /// the exact failure this project exists to prevent.
    /// </summary>
    void OnScanFinished(ScanOutcome outcome);

    /// <summary>
    /// A file was resolved. Reported for EVERY file including
    /// <see cref="FileKind.Unknown"/> ones - a sink deciding not to show
    /// something is a display choice, whereas the walk dropping it would be
    /// data loss, and this project has already been bitten by exactly that.
    /// </summary>
    void OnFile(DeviceObject obj, string path, FileKind kind, bool recovered);

    void OnFolder(DeviceObject obj, string path, bool recovered);

    /// <summary>A folder deliberately not walked. Never silent - see FolderPolicy.</summary>
    void OnFolderSkipped(string path, string reason);

    void OnError(ScanError error);

    /// <summary>
    /// The retry pass is about to re-read <paramref name="objectCount"/>
    /// objects that failed during the main walk. Separate from
    /// <see cref="OnRetryFinished"/> because anything the pass recovers is
    /// reported through OnFile/OnFolder in between, and a sink that renders a
    /// running log needs the header before those lines, not after them.
    /// </summary>
    void OnRetryStarted(int objectCount);

    /// <summary>Called once per scan that had failures, whether or not the pass ran.</summary>
    void OnRetryFinished(RetryOutcome outcome);
}

/// <summary>
/// Whether a finished scan can be trusted as a complete picture of the device,
/// and the census it produced. Everything needed to render or store a scan's
/// summary is here, because the alternative - a sink reading it back off
/// another sink - is what stopped a second sink from existing at all.
/// </summary>
/// <param name="Completed">The walk reached the end under its own power.</param>
/// <param name="Stalled">The device stopped responding and the watchdog cancelled it.</param>
/// <param name="Faulted">The walk threw and was abandoned.</param>
/// <param name="CameraMode">
/// The phone was connected in PTP mode, which hides videos and documents
/// entirely - measured at 289 files versus 222 on one device, with zero errors
/// reported either way.
/// </param>
/// <param name="UndeterminedFiles">
/// Files whose type could not be established because the content read was
/// unavailable. Not "not media" - simply never looked at.
/// </param>
/// <param name="SubtreeLosses">
/// Failures while LISTING a folder, excluding any the retry pass later walked
/// successfully. Each remaining one lost an unknown number of files beneath
/// that point, so anything under those paths is unproven.
/// </param>
/// <param name="SignatureChecksRun">
/// How many files were expensive enough to need their bytes read. Near zero on
/// phones (5 of 13,791 on one device) and near total on others (710 of 772 on
/// an e-reader), so it says as much about the device as about the scan.
/// </param>
/// <param name="CaughtBySignatureOnly">
/// Files that turned out to be media despite an unrecognised extension. These
/// would have been missed entirely by extension matching alone.
/// </param>
/// <param name="SignatureCheckErrors">Reads that failed outright, rather than simply not matching.</param>
/// <param name="FilesSkippedByBreaker">
/// Files with an unrecognised extension that went unchecked because the circuit
/// breaker had already given up on signature checking. Any of them could be a
/// photo, so this is a count of genuine unknowns, not of skipped work.
/// </param>
/// <param name="SignatureCheckingDisabled">The breaker tripped at some point during this scan.</param>
/// <param name="FilePropertyMisses">
/// Files missing a property the device supplies for every other file.
/// Containers are excluded: a storage root has no filename, size or modified
/// date, and counting it reported a constant "3 errors" on every healthy scan.
/// </param>
public record ScanOutcome(
    bool Completed,
    bool Stalled,
    bool Faulted,
    bool CameraMode,
    int MediaFiles,
    int Documents,
    int UndeterminedFiles,
    int TotalFilesSeen,
    int SubtreeLosses,
    int SignatureChecksRun,
    int CaughtBySignatureOnly,
    int SignatureCheckErrors,
    int FilesSkippedByBreaker,
    bool SignatureCheckingDisabled,
    int FilePropertyMisses)
{
    /// <summary>
    /// Whether this scan may be read as a full census. Deliberately strict
    /// about what it does check: a scan is either provably complete or it is
    /// partial, because "mostly complete" is what lets a store conclude that
    /// files it never looked at have been deleted from the phone.
    ///
    /// INCOMPLETE, known and not yet fixed - finding A4 in
    /// docs/INCELEME-SQLITE-2026-09-22.md. Two kinds of loss get past it:
    ///
    /// - Properties-stage errors. ScanTally.SubtreeLosses counts only
    ///   Enumerate/EnumerateNext, but a Properties failure loses one object
    ///   AND its subtree when that object was a folder - which is what
    ///   ConsoleScanSink prints about it. ScanOutcome carries no error count
    ///   at all, so nothing here can see them.
    /// - RetryOutcome.HiddenSubtrees, documented as folders whose entire
    ///   contents were silently lost. It is stored on the scan row and never
    ///   reaches this predicate.
    ///
    /// So a scan can satisfy this and still have lost photographs. The console
    /// at least prints the error breakdown underneath; the database qualifies
    /// its status with nothing.
    /// </summary>
    public bool IsTrustworthy =>
        Completed && !Stalled && !Faulted && !CameraMode &&
        UndeterminedFiles == 0 && SubtreeLosses == 0;
}
