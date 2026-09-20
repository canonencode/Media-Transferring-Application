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
