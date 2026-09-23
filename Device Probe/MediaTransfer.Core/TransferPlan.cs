using System.Text;

namespace MediaTransfer.Core;

/// <summary>One file the device holds, as the plan sees it.</summary>
/// <param name="ObjectId">How the scanner asks the device for its bytes.</param>
/// <param name="DevicePath">Where it sits on the phone. Decides which folder it lands in.</param>
public record TransferItem(
    string ObjectId,
    string DevicePath,
    string Name,
    long Size,
    string? ModifiedRaw);

/// <param name="RelativePath">Folder and filename under the destination root, for display.</param>
/// <param name="RenamedFrom">The name it would have had, when something forced a change.</param>
public record PlannedCopy(
    TransferItem Item,
    string RelativePath,
    string? RenamedFrom);

/// <param name="Renamed">Files whose name had to change, for a clash or an illegal character.</param>
public record TransferPlanResult(
    IReadOnlyList<PlannedCopy> Copies,
    long TotalBytes,
    int Renamed);

/// <summary>
/// Works out where each file goes before anything is copied.
///
/// Pure and, more importantly, DETERMINISTIC: planning the same scan twice must
/// produce the same destination for every file. A resumed transfer re-plans from
/// the same rows and matches what is already on disk against it, so a plan that
/// shuffled its own numbering would copy files a second time under new names -
/// turning an interrupted transfer into a folder full of duplicates, which is a
/// worse version of the problem this project exists to solve.
///
/// The phone's own folder structure is deliberately NOT reproduced. Media
/// scattered across dozens of nested folders is the second thing that started
/// this project, and mirroring it would carry that straight onto the PC.
/// </summary>
public static class TransferPlan
{
    /// <summary>
    /// Names Windows refuses regardless of extension. A file called CON.jpg
    /// cannot be created, and the failure arrives as a bare access error with
    /// nothing to say why.
    /// </summary>
    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static TransferPlanResult Build(IEnumerable<TransferItem> items)
    {
        // Sorted before anything is numbered, so two runs over the same rows
        // hand out the same suffixes. Device path first because it is the one
        // field guaranteed unique within a scan.
        var ordered = items
            .OrderBy(i => i.DevicePath, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var copies = new List<PlannedCopy>(ordered.Count);
        long bytes = 0;
        int renamed = 0;

        foreach (var item in ordered)
        {
            string folder = SafeSegment(MediaSource.Classify(item.DevicePath).Label, "Diger");
            string safeName = SafeFileName(item.Name);
            string finalName = Unique(folder, safeName, used);

            bool changed = !string.Equals(finalName, item.Name, StringComparison.Ordinal);
            if (changed) renamed++;

            copies.Add(new PlannedCopy(item, folder + "/" + finalName, changed ? item.Name : null));
            bytes += Math.Max(0, item.Size);
        }

        return new TransferPlanResult(copies, bytes, renamed);
    }

    /// <summary>
    /// Adds " (2)", " (3)" and so on when a folder already has that name.
    ///
    /// Only for clashes WITHIN the plan. A file already sitting on disk is a
    /// different question - it may be the very file being copied, in which case
    /// the answer is to skip rather than to duplicate - and only the copier can
    /// see the disk, so it decides that one.
    /// </summary>
    static string Unique(string folder, string name, Dictionary<string, int> used)
    {
        string key = folder + "/" + name;
        if (used.TryAdd(key, 1)) return name;

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int n = used[key] + 1; ; n++)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (used.TryAdd(folder + "/" + candidate, 1))
            {
                used[key] = n;
                return candidate;
            }
        }
    }

    /// <summary>
    /// The planned copies a source selection keeps.
    ///
    /// Filtering happens HERE, after a plan has been built over the whole scan,
    /// and never by dropping files before Build sees them. Planning over a
    /// subset would hand a file a different name depending on which groups were
    /// ticked - two files clash, one of them gets " (2)", and which one it lands
    /// on changes with the selection. A second transfer with a different
    /// selection would then copy the same file again under the new name, which
    /// is the folder full of duplicates this project exists to avoid.
    ///
    /// It is also the reason this lives in Core rather than in either caller.
    /// The setup page measures free space against a selection and the copier
    /// then transfers one; if those two ever disagreed about what a selection
    /// means, the page would promise a figure the transfer does not honour.
    /// </summary>
    /// <param name="sources">
    /// Comma separated <see cref="MediaSource"/> ids. "all", empty or
    /// whitespace keeps everything - a caller that means "nothing" has no
    /// reason to be planning a transfer at all.
    /// </param>
    public static IReadOnlyList<PlannedCopy> ForSources(IReadOnlyList<PlannedCopy> copies, string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources) || sources.Trim() == "all") return copies;

        var wanted = new HashSet<string>(
            sources.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return [.. copies.Where(c => wanted.Contains(MediaSource.Classify(c.Item.DevicePath).Id))];
    }

    /// <summary>
    /// The next unused name for a file whose planned one is already taken.
    ///
    /// The sibling of <see cref="Unique"/>, for the other half of the same
    /// question. Unique settles clashes WITHIN a plan, where the answer is known
    /// from the plan alone; this settles clashes against the world outside it -
    /// a file already on disk, or one an earlier file in this same run has
    /// claimed but not yet written. Both cases have to be asked about, and
    /// asking only the disk is the bug: two files decided in one pass have
    /// written nothing yet, so File.Exists would offer both the same name.
    /// </summary>
    /// <param name="isTaken">Whatever the caller counts as taken. Called at least once.</param>
    public static string NextFreeName(string fullPath, Func<string, bool> isTaken)
    {
        string folder = Path.GetDirectoryName(fullPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(fullPath);
        string ext = Path.GetExtension(fullPath);

        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!isTaken(candidate)) return candidate;
        }
    }

    /// <summary>
    /// A filename Windows will accept, changing as little as possible.
    ///
    /// The device's name is not trusted: MTP hands back whatever the phone
    /// stored, which has already been seen to include unpaired UTF-16
    /// surrogates. A name that cannot be written is a file that cannot be
    /// copied, so it is repaired rather than refused - losing the file would be
    /// the worse answer, and the original name is recorded alongside.
    /// </summary>
    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];

            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                sb.Append(c).Append(name[i + 1]);
                i++;
                continue;
            }

            bool illegal =
                char.IsSurrogate(c) ||          // unpaired half: not encodable
                char.IsControl(c) ||
                c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

            sb.Append(illegal ? '_' : c);
        }

        // Windows silently drops trailing dots and spaces, so a name ending in
        // one would be created under a different name than the one recorded -
        // and the record is what a later run matches against.
        string cleaned = sb.ToString().TrimEnd(' ', '.');

        if (cleaned.Length == 0) return "adsiz";

        string stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }
        return cleaned;
    }

    /// <summary>A folder name that is safe and never empty.</summary>
    static string SafeSegment(string name, string fallback)
    {
        string safe = SafeFileName(name);
        return safe is "adsiz" or "" ? fallback : safe;
    }
}
