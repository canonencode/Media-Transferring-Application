namespace MediaTransfer.Core;

// One object as the device describes it. Only ObjectId, Name and IsContainer
// are guaranteed; the rest are nullable because "the device did not tell us"
// is a real and common answer, and pretending otherwise (0 for an unknown
// size, DateTime.MinValue for an unknown date) would let missing data pass
// silently into the manifest as though it were measured.
public record DeviceObject(
    string ObjectId,
    string Name,
    string DisplayName,
    bool IsContainer,
    ulong? Size,
    string? PersistentId,
    string? ModifiedRaw);

// FileKind now lives in FileKind.cs - it is returned by FileClassifier, which
// is pure, so the enum has to be compilable without this file's COM interop.

// Which WPD call failed. The distinction is the whole point: Enumerate and
// EnumerateNext lose a folder's contents (an unknown number of files, possibly
// thousands), while Properties loses a single object. A flat "N objects
// skipped" count cannot tell those apart, so it cannot tell you whether a scan
// that finished "successfully" actually saw your photos.
public enum ScanStage { Enumerate, EnumerateNext, Properties }

public record ScanError(string ObjectId, string ParentPath, ScanStage Stage, int HResult, string Message);

// Who the device is. The serial is the identity a store should key on, not the
// WPD id: the WPD id embeds the USB port the phone happens to be plugged into,
// so the same phone in a different port is a different id and would look like a
// different device. The serial is nullable because not every device supplies
// one - see DeviceKey for what happens then.
public record DeviceIdentity(
    string WpdId,
    string FriendlyName,
    string? SerialNumber,
    string? Manufacturer,
    string? Model)
{
    /// <summary>
    /// What a store should file this device under. Falls back to the WPD id
    /// when the device supplies no serial: that key changes if the user moves
    /// the cable to another port, which splits one phone's history in two -
    /// bad, but strictly better than merging two serial-less devices into one
    /// history, which would report the other phone's files as deleted.
    /// </summary>
    public string DeviceKey =>
        string.IsNullOrWhiteSpace(SerialNumber) ? WpdId : SerialNumber;

    /// <summary>True when the key is a port-dependent fallback, not a serial.</summary>
    public bool KeyIsFallback => string.IsNullOrWhiteSpace(SerialNumber);
}

/// <summary>
/// What the post-scan retry pass managed to get back. Trust-relevant in both
/// directions: recovered objects are files that would otherwise have been
/// missing from a scan that called itself complete, and HiddenSubtrees are
/// folders whose contents are still entirely unaccounted for.
/// </summary>
/// <param name="Skipped">
/// The pass did not run at all. Happens when the device stopped responding -
/// re-asking a wedged device only waits for answers that are not coming.
/// </param>
/// <param name="SkipReason">Why it did not run, or null when it did.</param>
/// <param name="Attempted">Objects the pass was given to re-read.</param>
/// <param name="HiddenSubtrees">
/// Objects that still could not be read but DID answer an enumerate probe, so
/// they are folders - and everything beneath them was silently lost.
/// </param>
/// <param name="NewFailures">
/// Objects that failed during the retry pass itself. Not retried again; a
/// second pass over the same wedged session is how the walk used to corrupt
/// its own enumerator state.
/// </param>
public record RetryOutcome(
    bool Skipped,
    string? SkipReason,
    int Attempted,
    int RecoveredFiles,
    int RecoveredFolders,
    int StillUnreadable,
    int HiddenSubtrees,
    int NewFailures)
{
    public static RetryOutcome WasSkipped(int attempted, string reason) =>
        new(true, reason, attempted, 0, 0, 0, 0, 0);
}
