namespace MediaTransfer.Core;

/// <summary>
/// Prints a scan as an indented tree, then the summary. This is the probe's own
/// view of a scan; the SQLite writer is a sibling of it, not a replacement for
/// the walk.
///
/// It no longer counts files. It used to, and the walk then read those counters
/// back to build its <see cref="ScanOutcome"/> - which quietly made this class
/// the scan's memory and meant no second sink could be added beside it. The
/// counts now arrive on the outcome, which is also what the SQLite sink stores,
/// so the screen and the database cannot disagree about what was found.
///
/// Errors and skipped folders are still kept here, because grouping them for
/// display is this sink's own rendering job; the SQLite sink writes each one as
/// a row on arrival and keeps nothing.
/// </summary>
public sealed class ConsoleScanSink : IScanSink
{
    public List<ScanError> Errors { get; } = new();
    public List<string> SkippedFolders { get; } = new();

    // Indentation comes from the path rather than a depth parameter passed
    // through the walk. The walk already knows the path and has to be correct
    // about it; making it also carry a display concern is how the retry pass
    // ended up printing recovered folders at a hardcoded depth of 1.
    static string Indent(string path)
    {
        int depth = 0;
        foreach (char c in path)
        {
            if (c == '/') depth++;
        }
        return new string(' ', Math.Max(0, depth - 1) * 2);
    }

    public void OnFile(DeviceObject obj, string path, FileKind kind, bool recovered)
    {
        string indent = Indent(path);
        switch (kind)
        {
            case FileKind.MediaFile:
                Console.WriteLine($"{indent}{(recovered ? "[RECOVERED-MEDIA]" : "[MEDIA]")} {obj.Name}");
                break;
            case FileKind.AudioFile:
                Console.WriteLine($"{indent}{(recovered ? "[RECOVERED-AUDIO]" : "[AUDIO]")} {obj.Name}");
                break;
            case FileKind.Document:
                Console.WriteLine($"{indent}{(recovered ? "[RECOVERED-DOC]" : "[DOC]")} {obj.Name}");
                break;
            case FileKind.Undetermined:
                // Printed, unlike Unknown. These were never examined, so any of
                // them could be a photo; leaving them silent is how a scan can
                // look complete while quietly skipping thousands of files.
                Console.WriteLine($"{indent}[UNCHECKED] {obj.Name}");
                break;

            // Unknown files print nothing - on a real phone they are thousands
            // of app data files nobody wants to read past. They are still
            // counted by the walk and still stored, which is the difference
            // between "not shown" and "not seen".
        }
    }

    public void OnFolder(DeviceObject obj, string path, bool recovered)
    {
        Console.WriteLine($"{Indent(path)}[DIR]  {obj.Name}{(recovered ? " (recovered, now walking its contents)" : "")}");
    }

    public void OnFolderSkipped(string path, string reason)
    {
        Console.WriteLine($"{Indent(path)}[SKIP] {path[(path.LastIndexOf('/') + 1)..]} ({reason})");
        SkippedFolders.Add($"{path} - {reason}");
    }

    public void OnError(ScanError error) => Errors.Add(error);

    public void OnScanStarted(DeviceIdentity device, bool cameraMode)
    {
        if (cameraMode)
        {
            Console.WriteLine("[WARNING] Scanning in camera (PTP) mode - videos and documents are hidden " +
                "by the phone itself, so these results cannot be complete.");
        }
    }

    public void OnRetryStarted(int objectCount)
    {
        Console.WriteLine($"\nRetrying {objectCount} previously-failed object(s)...");
    }

    public void OnRetryFinished(RetryOutcome outcome)
    {
        if (outcome.Skipped)
        {
            Console.WriteLine($"\nSkipping the retry pass: {outcome.SkipReason}");
            return;
        }

        Console.WriteLine($"Retry result: {outcome.RecoveredFiles} file(s) and {outcome.RecoveredFolders} folder(s) recovered, " +
            $"{outcome.StillUnreadable} still unreadable.");
        if (outcome.HiddenSubtrees > 0)
        {
            Console.WriteLine($"[IMPORTANT] {outcome.HiddenSubtrees} of the unreadable object(s) could still be ENUMERATED, " +
                "which means they are folders whose entire contents were silently lost from this scan.");
        }
        if (outcome.NewFailures > 0)
        {
            Console.WriteLine($"({outcome.NewFailures} new failure(s) occurred during the retry pass itself and were not retried again.)");
        }
    }

    public void OnScanFinished(ScanOutcome outcome)
    {
        PrintVerdict(outcome);
        PrintCensus(outcome);
        PrintSkippedFolders();
        PrintErrors();
    }

    // Said plainly, because every one of these means "do not treat this as a
    // full picture of the device" - and a scan that silently looks complete is
    // worse than one that fails loudly.
    static void PrintVerdict(ScanOutcome outcome)
    {
        if (outcome.IsTrustworthy)
        {
            Console.WriteLine("\nScan is COMPLETE: every folder was listed and every file was identified.");
            return;
        }

        Console.WriteLine("\nScan is PARTIAL - this is not a full picture of the device:");
        if (!outcome.Completed) Console.WriteLine("  - the walk did not reach the end");
        if (outcome.Stalled) Console.WriteLine("  - the device stopped responding and the scan was cancelled");
        if (outcome.Faulted) Console.WriteLine("  - the scan stopped on an error");
        if (outcome.CameraMode) Console.WriteLine("  - the phone was in camera (PTP) mode, which hides videos and documents");
        if (outcome.SubtreeLosses > 0) Console.WriteLine($"  - {outcome.SubtreeLosses} folder(s) could not be listed, losing everything beneath them");
        if (outcome.UnresolvedObjects > 0) Console.WriteLine($"  - {outcome.UnresolvedObjects} object(s) could not be identified at all, so any that were folders took their contents with them");
        if (outcome.UndeterminedFiles > 0) Console.WriteLine($"  - {outcome.UndeterminedFiles} file(s) were never examined (listed above as [UNCHECKED])");
    }

    static void PrintCensus(ScanOutcome outcome)
    {
        Console.WriteLine($"\nDone. {outcome.MediaFiles} media file(s), {outcome.AudioFiles} audio file(s) " +
            $"and {outcome.Documents} document(s) found out of {outcome.TotalFilesSeen} file(s) seen.");
        Console.WriteLine($"({outcome.CaughtBySignatureOnly} of those were caught only by file signature - their extension wasn't recognized.)");
        Console.WriteLine($"Expensive signature check actually ran on {outcome.SignatureChecksRun} file(s) (out of {outcome.TotalFilesSeen} total).");
        Console.WriteLine($"Signature check itself errored (not just 'no match') on {outcome.SignatureCheckErrors} file(s).");

        if (outcome.FilesSkippedByBreaker > 0)
        {
            Console.WriteLine($"[IMPORTANT] {outcome.FilesSkippedByBreaker} file(s) with an unrecognized extension went " +
                "UNCHECKED because the circuit breaker had already disabled signature checking. Any of them could be a " +
                "real photo or video that this scan did not count.");
        }

        Console.WriteLine(outcome.SignatureCheckingDisabled
            ? "Session health: signature checking was DISABLED partway through this scan (see warning above)."
            : "Session health: OK, signature checking ran normally for the whole scan.");

        if (outcome.FilePropertyMisses > 0)
        {
            Console.WriteLine($"\n[NOTE] {outcome.FilePropertyMisses} file(s) were missing a property (name, size or date) that " +
                "this device otherwise supplies for every file. Containers are not counted here, so each of these is " +
                "a genuine gap worth a look.");
        }
    }

    void PrintSkippedFolders()
    {
        if (SkippedFolders.Count == 0) return;

        Console.WriteLine($"\n{SkippedFolders.Count} folder(s) deliberately not walked:");
        foreach (var skipped in SkippedFolders)
        {
            Console.WriteLine($"  {skipped}");
        }
    }

    // Errors grouped by which call failed and why, instead of a flat count plus
    // the first ten messages. The breakdown is the point: an Enumerate/Next
    // failure loses a whole subtree while a Properties failure loses one object,
    // and one HResult repeated 1,400 times is a very different problem from five
    // different HResults - neither of which the old summary could express.
    void PrintErrors()
    {
        if (Errors.Count == 0) return;

        Console.WriteLine($"\n{Errors.Count} device error(s) during this scan, by call site and cause:");
        foreach (var group in Errors
            .GroupBy(e => (e.Stage, e.HResult))
            .OrderByDescending(g => g.Count()))
        {
            var (stage, hresult) = group.Key;
            string impact = stage switch
            {
                ScanStage.Enumerate => "lost the folder's entire contents",
                ScanStage.EnumerateNext => "lost the rest of that folder's contents",
                ScanStage.Properties => "lost one object (and its subtree, if it was a folder)",
                _ => "lost one file's signature check"
            };
            Console.WriteLine($"  {group.Count(),6} x [{stage}] 0x{hresult:X8} - {impact}");
            Console.WriteLine($"         e.g. \"{group.First().Message.Trim()}\" at {group.First().ParentPath}/");
        }

        // Counted from the raw errors, NOT from ScanOutcome.SubtreeLosses: this
        // block describes what went wrong during the walk, while the outcome
        // reports what was still lost after the retry pass. When they differ,
        // the difference is exactly what the retry recovered.
        int subtreeLosses = Errors.Count(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext);
        if (subtreeLosses > 0)
        {
            Console.WriteLine($"\n[IMPORTANT] {subtreeLosses} of those errors happened while LISTING a folder, so an " +
                "unknown number of files below those points were never seen at all. Affected folders:");
            foreach (var path in Errors
                .Where(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext)
                .Select(e => e.ParentPath)
                .Distinct()
                .Take(20))
            {
                Console.WriteLine($"  {(path.Length == 0 ? "/ (device root)" : path)}");
            }
        }
    }
}
