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
/// <param name="Destination">
/// The file this record is about. Not necessarily where the plan would put the
/// file today - a version kept alongside an older one lives under a numbered
/// name, and the plan keeps choosing the bare one.
/// </param>
/// <param name="BytesCopied">
/// What actually arrived. Different from <paramref name="SourceSize"/> exactly
/// when the device did not report a size: the scan stores NULL, both readers
/// turn that into 0, and comparing a real file on disk against 0 says "wrong
/// length" forever. What arrived is the honest figure to check against.
/// </param>
public record CopyRecord(
    string SourcePath,
    long? SourceSize,
    string? SourceModified,
    string Destination,
    string Status,
    string? Sha256,
    long? BytesCopied = null);

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
/// WHICH FILE THE CALLER MUST MEASURE: the one <see cref="CopyRecord.Destination"/>
/// names, not the one the plan would choose today. They are the same for almost
/// every file and different for exactly the ones that matter - a version kept
/// alongside an older one sits under a numbered name while the plan, being
/// deterministic, goes on pointing at the bare one. Measuring the plan's choice
/// against a record about a different file answers a question nobody asked, and
/// the answers it gives are Skip for a file that was never copied and Copy for
/// one whose older version is then deleted to make room.
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

        // Asked BEFORE the changed-file check, and the order is the whole point.
        // CopyAsNewVersion means "keep both", and there is no both when nothing
        // is on the disk to keep. Answering it anyway sent the file through a
        // naming helper that starts at " (2)" and never offers the bare name, so
        // an empty destination folder received a photograph called "a (2).jpg"
        // with no "a.jpg" anywhere - and the ledger then recorded a destination
        // the deterministic plan would never choose again, which is how the
        // record and the plan drift apart for good.
        if (!destinationExists) return CopyAction.Copy;

        // The phone's file changed under the same path. An edited photo, or a
        // name reused after a delete. Keeping both is the only answer that
        // cannot lose something.
        if (previous.SourceSize != sourceSize ||
            !string.Equals(previous.SourceModified, sourceModified, StringComparison.Ordinal))
        {
            return CopyAction.CopyAsNewVersion;
        }

        // Compared against what ARRIVED, falling back to what the device claimed
        // when there is no such figure. For an ordinary file the two are equal.
        // They differ when the device never reported a size: the scan stores
        // NULL, the readers turn it into 0, and checking a real 3 MB file
        // against 0 says "wrong length" on every run forever - each one deleting
        // the verified copy before pulling it down again.
        long expected = previous.BytesCopied ?? sourceSize;

        // There, but the wrong length: a truncated write, a full disk, a crash
        // between writing and renaming. Whatever it was, it is not the file.
        if (destinationSize != expected) return CopyAction.Copy;

        return CopyAction.Skip;
    }
}
