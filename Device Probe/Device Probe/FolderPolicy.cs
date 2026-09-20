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
        reason = "";
        if (parentPath is null || name is null) return false;

        // Android blocks external access to these itself from Android 11 on, so
        // this is not our choice so much as reporting the OS's. They are also
        // where the largest irrelevant per-app caches live.
        //
        // Anchored to the storage root, not merely to a path ENDING in
        // "/Android". The looser rule matched a user's own album: for
        // parentPath "/Phone/DCIM/Android" and name "data" it returned true,
        // so a folder someone had named Android anywhere in their gallery
        // silently lost everything beneath it, with nothing said about it. The
        // real one is always exactly <storage>/Android, i.e. two segments deep.
        // At most two segments: <storage>/Android normally, or /Android alone
        // if a device ever exposes storage as the root. Three or more means it
        // is somebody's own folder, not the OS one.
        string[] segments = parentPath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 1 or 2 &&
            segments[^1].Equals("Android", StringComparison.OrdinalIgnoreCase) &&
            (name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("obb", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "blocked from external access by Android itself since Android 11";
            return true;
        }

        if (SkippableFolders.TryGetValue(name, out string? knownReason))
        {
            reason = knownReason;
            return true;
        }

        return false;
    }

    // Both separators, because the walk builds paths with '/' but nothing
    // guarantees a device name or a future caller will not introduce '\'. The
    // previous rule hard-coded the literal "/Android" and would have stopped
    // firing entirely against backslash paths - failing open, which here means
    // walking into a folder Android itself refuses to serve.
    static readonly char[] PathSeparators = { '/', '\\' };
}
