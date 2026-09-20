/// <summary>
/// The two reasons a folder is not worth walking. Kept in one place so the
/// main walk and the retry pass can never disagree about them - they did once,
/// and a recovered Android/data folder got fully enumerated as a result.
///
/// Pure: takes strings, returns a decision. No COM, no device.
/// </summary>
static class FolderPolicy
{
    // Folders holding only DERIVED copies of files that are scanned elsewhere.
    // Measured on an A56: .thumbnails alone held 7,955 of the 17,478 files a
    // scan counted as "media" - 46% of the result was miniatures of photos the
    // same scan had already found. .Links was worse in a different way: 108
    // usable files but roughly 3,000 objects, and 2,990 of that scan's 3,012
    // errors came from inside it, at ~118ms each.
    //
    // Named explicitly rather than matched by a leading dot. Dot-prefixed only
    // means "hidden" on Android, and two hidden folders on that same phone -
    // .Statuses (WhatsApp statuses) and .Trash - can hold real photos. .Trash
    // is skipped anyway, but for a different reason: its contents are files the
    // user deliberately deleted, and surfacing those in a flattened "all your
    // photos" view would read as a bug rather than a feature.
    static readonly Dictionary<string, string> SkippableFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        [".thumbnails"] = "cache folder - holds only derived copies of files scanned elsewhere",
        [".Links"] = "cache folder - holds only derived copies of files scanned elsewhere",
        [".wamocache"] = "cache folder - holds only derived copies of files scanned elsewhere",
        [".Trash"] = "the gallery's recycle bin - these are photos the user deleted"
    };

    /// <summary>
    /// Decides whether to walk into a folder. <paramref name="parentPath"/> is
    /// the full accumulated path of the folder's parent, not just its name:
    /// matching on the bare name meant any folder called "Android" anywhere in
    /// the tree silently lost its data/obb children.
    /// </summary>
    public static bool ShouldSkip(string parentPath, string name, out string reason)
    {
        // Android blocks external access to these itself from Android 11 on, so
        // this is not our choice so much as reporting the OS's. They are also
        // where the largest irrelevant per-app caches live.
        if (parentPath.EndsWith("/Android", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, "obb", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "blocked from external access by Android itself since Android 11";
            return true;
        }

        if (SkippableFolders.TryGetValue(name, out string? knownReason))
        {
            reason = knownReason;
            return true;
        }

        reason = "";
        return false;
    }
}
