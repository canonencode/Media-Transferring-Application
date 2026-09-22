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
