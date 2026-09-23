namespace MediaTransfer.Core;

/// <summary>What to do with one file when a transfer runs.</summary>
public enum CopyAction
{
    /// <summary>Copy it to the planned destination.</summary>
    Copy,

    /// <summary>Already there, provably. Leave it alone.</summary>
    Skip,

    /// <summary>
    /// The phone's copy is not the one that was taken before - a different size
    /// or a different date under the same path. Both are kept, because deciding
    /// which one the person wanted is not this program's call.
    /// </summary>
    CopyAsNewVersion,
}

/// <param name="Status">copying, done or failed.</param>
/// <param name="SourceSize">The size the source had WHEN IT WAS COPIED, not now.</param>
public record CopyRecord(
    string SourcePath,
    long? SourceSize,
    string? SourceModified,
    string Destination,
    string Status,
    string? Sha256);

/// <summary>
/// Whether a file still needs copying, given what the ledger remembers and what
/// is on disk now.
///
/// The question this answers is the one the whole project started from: a
/// transfer broke halfway and nobody could say what had made it across. So the
/// rule is not "does a file with that name exist" - it is "does the ledger say
/// this exact file was copied, and is that copy still intact". Anything less
/// certain is copied again, because copying a file twice costs seconds and
/// missing one costs a photograph.
///
/// Note what is NOT compared: the destination's timestamp against the phone's.
/// The device reports dates in its own format (2026/02/18:15:44:29.000 on one
/// measured phone) and parsing it would mean guessing at a format that varies
/// by vendor - a guess that, if wrong, marks every file as changed and copies
/// the entire phone again. The ledger records what it took; that record is the
/// comparison.
/// </summary>
public static class CopyDecision
{
    public static CopyAction Decide(
        CopyRecord? previous,
        long sourceSize,
        string? sourceModified,
        bool destinationExists,
        long destinationSize)
    {
        // Never copied, or last time did not finish. An interrupted attempt
        // leaves a .part file that is worth nothing: it is a prefix of a file,
        // and a prefix of a photograph is not a photograph.
        if (previous is null || previous.Status != "done") return CopyAction.Copy;

        // The phone's file changed under the same path. An edited photo, or a
        // name reused after a delete. Keeping both is the only answer that
        // cannot lose something.
        if (previous.SourceSize != sourceSize ||
            !string.Equals(previous.SourceModified, sourceModified, StringComparison.Ordinal))
        {
            return CopyAction.CopyAsNewVersion;
        }

        // The ledger says it was copied, but the file is not there. Someone
        // moved or deleted it, or it was written to a drive that is not
        // attached. The ledger is a record of what happened, not a promise
        // about what is on disk now, so the disk wins.
        if (!destinationExists) return CopyAction.Copy;

        // There, but the wrong length: a truncated write, a full disk, a crash
        // between writing and renaming. Whatever it was, it is not the file.
        if (destinationSize != sourceSize) return CopyAction.Copy;

        return CopyAction.Skip;
    }
}
