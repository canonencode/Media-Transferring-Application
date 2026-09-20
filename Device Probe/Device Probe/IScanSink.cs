/// <summary>
/// Where a scan's findings go. The walk reports to one of these instead of
/// writing to the console, which is the seam the whole next milestone depends
/// on: a database cannot read the screen. The same walk can then feed a console
/// probe, a SQLite writer, or later a UI, without knowing which.
///
/// Paths are the full accumulated path ("/Dahili depolama/DCIM/Camera"), so a
/// sink that wants indentation can derive it and one that wants rows does not
/// have to care about depth at all.
/// </summary>
interface IScanSink
{
    /// <summary>Called once before the walk begins.</summary>
    void OnScanStarted(string deviceId, string friendlyName, bool cameraMode);

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
}

/// <summary>
/// Whether a finished scan can be trusted as a complete picture of the device.
/// Every field here is a reason it might not be, and each one has been observed
/// on real hardware.
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
/// Failures while LISTING a folder. Each one lost an unknown number of files
/// beneath that point, so anything under those paths is unproven.
/// </param>
record ScanOutcome(
    bool Completed,
    bool Stalled,
    bool Faulted,
    bool CameraMode,
    int UndeterminedFiles,
    int SubtreeLosses);
