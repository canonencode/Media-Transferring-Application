using MediaTransfer.Core;
using Microsoft.Data.Sqlite;

namespace MediaTransfer.App;

/// <summary>
/// Everything the transfer page needs before a byte moves: where it could go,
/// where it would go, and whether it fits.
///
/// The last one is the reason this exists. The first real measurement against
/// the test phone wanted 22.14 GB against 20 GB free - a transfer that would
/// have run for the better part of an hour and then failed with the destination
/// full and no account of what had made it across. Finding that out first is
/// cheap; finding it out at 18 GB is exactly this project's own failure mode.
/// </summary>
public sealed class TransferSetup(string databasePath)
{
    /// <param name="Ready">The drive is present and writable, so the figures mean something.</param>
    public readonly record struct Drive(string Root, string Label, long Free, long Total, bool Ready);

    /// <param name="Fits">Free space covers the transfer with room to spare.</param>
    public readonly record struct Preflight(
        string Destination, long RequiredBytes, int Files, int Renamed,
        long FreeBytes, bool Fits, string? Problem);

    /// <summary>Fixed drives with their free space. Removable ones are included: a USB disk is a fine destination.</summary>
    public static List<Drive> Drives()
    {
        var list = new List<Drive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                list.Add(d.IsReady
                    ? new Drive(d.RootDirectory.FullName, d.VolumeLabel, d.AvailableFreeSpace, d.TotalSize, true)
                    : new Drive(d.RootDirectory.FullName, "", 0, 0, false));
            }
            catch (IOException)
            {
                // A drive that disappears between listing and asking is not an
                // error worth stopping for; it simply is not an option.
            }
        }
        return list;
    }

    /// <summary>
    /// The folder name offered when the page opens: the phone's own name with
    /// "yedek" after it, cleaned up enough to be a folder. The user can change
    /// it; this only has to be a sensible thing to accept without thinking.
    /// </summary>
    public static string DefaultFolderName(string? deviceName)
    {
        string name = string.IsNullOrWhiteSpace(deviceName) ? "Telefon" : deviceName.Trim();
        return TransferPlan.SafeFileName(name + " yedek");
    }

    /// <summary>
    /// Builds the plan for a scan and measures it against the chosen
    /// destination.
    ///
    /// The plan is built here rather than guessed at from the scan row's totals,
    /// because the two can differ: the row counts every file the walk saw,
    /// including the app data it set aside, while the transfer only carries
    /// media and documents.
    /// </summary>
    /// <param name="sources">
    /// Comma separated MediaSource ids, or "all". The figures have to be
    /// measured against the same selection the copier will be given, or the page
    /// promises one thing and the transfer does another.
    /// </param>
    public Preflight Check(long scanId, string root, string? folderName, bool groupInOneFolder,
        string sources = "all")
    {
        var items = Items(scanId);

        // Built whole and filtered after, exactly as the copier does it. Planning
        // over a subset would number a file differently depending on which
        // groups were ticked, and a second transfer with a different selection
        // would then copy it again under a new name.
        var plan = TransferPlan.Build(items);
        var copies = TransferPlan.ForSources(plan.Copies, sources);

        string destination = root;
        if (groupInOneFolder && !string.IsNullOrWhiteSpace(folderName))
        {
            destination = Path.Combine(root, TransferPlan.SafeFileName(folderName.Trim()));
        }

        long free = 0;
        string? problem = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
            if (!drive.IsReady) problem = "Sürücü hazır değil.";
            else free = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            problem = "Bu konum okunamadı: " + ex.Message;
        }

        long bytes = copies.Sum(c => Math.Max(0, c.Item.Size));

        // A margin rather than an exact fit. A filesystem needs room for its own
        // bookkeeping, and a transfer that ends with a full disk leaves the
        // machine in a worse state than one that refuses to start.
        long needed = bytes + Math.Max(256L * 1024 * 1024, bytes / 50);
        bool fits = problem is null && free >= needed;

        if (problem is null && !fits)
        {
            problem = "Bu sürücüde yeterli yer yok.";
        }

        return new Preflight(destination, bytes, copies.Count,
            copies.Count(c => c.RenamedFrom is not null), free, fits, problem);
    }


    /// <summary>The files a transfer would carry: media and documents, never the app data.</summary>
    public List<TransferItem> Items(long scanId)
    {
        var items = new List<TransferItem>();

        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();

        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT object_id, path, name, size, modified_raw
            FROM file WHERE scan_id = $s AND kind IN ('MediaFile', 'Document');
            """;
        q.Parameters.AddWithValue("$s", scanId);

        using var r = q.ExecuteReader();
        while (r.Read())
        {
            items.Add(new TransferItem(
                ObjectId: r.GetString(0),
                DevicePath: r.GetString(1),
                Name: r.GetString(2),
                Size: r.IsDBNull(3) ? 0 : r.GetInt64(3),
                ModifiedRaw: r.IsDBNull(4) ? null : r.GetString(4)));
        }
        return items;
    }
}
