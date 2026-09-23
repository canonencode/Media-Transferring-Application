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

    /// <param name="Files">Everything the selection covers.</param>
    /// <param name="ToCopy">
    /// How many of those actually need copying. Lower than <paramref name="Files"/>
    /// on a resumed transfer, and the difference is the whole point: quoting the
    /// full figure made the page ask for 22 GB to finish a job with 3 GB left,
    /// refuse the drive that already held most of it, and then call the skipped
    /// files "never reached" once the run had finished them.
    /// </param>
    /// <param name="RequiredBytes">Bytes for <paramref name="ToCopy"/> alone - what the drive must still hold.</param>
    /// <param name="Fits">Free space covers the transfer with room to spare.</param>
    public readonly record struct Preflight(
        string Destination, long RequiredBytes, int Files, int ToCopy, int Renamed,
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

        // The same call the copier makes, file for file. Anything less than the
        // same call is two implementations of one rule, and they drifted apart
        // within an afternoon last time.
        var remembered = Remembered(scanId);
        int toCopy = 0;
        long bytes = 0;
        foreach (var copy in copies)
        {
            string target = Path.Combine(destination,
                copy.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            remembered.TryGetValue(copy.Item.DevicePath, out CopyRecord? previous);

            var action = CopyDecision.ForTarget(previous, copy.Item, target, destination,
                File.Exists, path => new FileInfo(path).Length);
            if (action == CopyAction.Skip) continue;

            toCopy++;
            bytes += Math.Max(0, copy.Item.Size);
        }

        // A margin rather than an exact fit. A filesystem needs room for its own
        // bookkeeping, and a transfer that ends with a full disk leaves the
        // machine in a worse state than one that refuses to start.
        long needed = bytes + Math.Max(256L * 1024 * 1024, bytes / 50);
        bool fits = problem is null && free >= needed;

        if (problem is null && !fits)
        {
            problem = "Bu sürücüde yeterli yer yok.";
        }

        return new Preflight(destination, bytes, copies.Count, toCopy,
            copies.Count(c => c.RenamedFrom is not null), free, fits, problem);
    }

    /// <summary>
    /// What the ledger remembers about this scan's device, so the preflight can
    /// ask the same question the copier will.
    ///
    /// Opened read-only and swallowing failure on purpose: not knowing the
    /// history makes every file look uncopied, which over-estimates the work
    /// and the space. That is the safe direction to be wrong in - it can only
    /// refuse a transfer that would have fitted, never start one that will not.
    /// </summary>
    Dictionary<string, CopyRecord> Remembered(long scanId)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            c.Open();

            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT source_path, source_size, source_modified, destination, status, sha256,
                       bytes_copied
                FROM copy
                WHERE device_key = (SELECT device_key FROM scan WHERE scan_id = $s)
                ORDER BY copy_id;
                """;
            q.Parameters.AddWithValue("$s", scanId);

            var map = new Dictionary<string, CopyRecord>(StringComparer.Ordinal);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                // Later rows overwrite earlier ones - the newest row for a path
                // is the one that speaks for it, as everywhere else.
                map[r.GetString(0)] = new CopyRecord(
                    SourcePath: r.GetString(0),
                    SourceSize: r.IsDBNull(1) ? null : r.GetInt64(1),
                    SourceModified: r.IsDBNull(2) ? null : r.GetString(2),
                    Destination: r.GetString(3),
                    Status: r.GetString(4),
                    Sha256: r.IsDBNull(5) ? null : r.GetString(5),
                    BytesCopied: r.IsDBNull(6) ? null : r.GetInt64(6));
            }
            return map;
        }
        catch (SqliteException)
        {
            return [];
        }
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
            FROM file WHERE scan_id = $s AND kind IN ('MediaFile', 'AudioFile', 'Document');
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
