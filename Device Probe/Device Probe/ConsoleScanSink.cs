/// <summary>
/// Prints a scan as an indented tree, and keeps the running totals the summary
/// reports. This is the probe's own view of a scan; the SQLite writer will be a
/// sibling of it, not a replacement for the walk.
/// </summary>
sealed class ConsoleScanSink : IScanSink
{
    public int MediaFiles { get; private set; }
    public int UndeterminedFiles { get; private set; }
    public int Documents { get; private set; }
    public int TotalFilesSeen { get; private set; }
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
        TotalFilesSeen++;

        string indent = Indent(path);
        switch (kind)
        {
            case FileKind.MediaFile:
                MediaFiles++;
                Console.WriteLine($"{indent}{(recovered ? "[RECOVERED-MEDIA]" : "[MEDIA]")} {obj.Name}");
                break;
            case FileKind.Document:
                Documents++;
                Console.WriteLine($"{indent}{(recovered ? "[RECOVERED-DOC]" : "[DOC]")} {obj.Name}");
                break;
            case FileKind.Undetermined:
                // Printed, unlike Unknown. These were never examined, so any of
                // them could be a photo; leaving them silent is how a scan can
                // look complete while quietly skipping thousands of files.
                UndeterminedFiles++;
                Console.WriteLine($"{indent}[UNCHECKED] {obj.Name}");
                break;

            // Unknown files are counted but not printed - on a real phone they
            // are thousands of app data files nobody wants to read past.
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

    public void OnScanStarted(string deviceId, string friendlyName, bool cameraMode)
    {
        if (cameraMode)
        {
            Console.WriteLine("[WARNING] Scanning in camera (PTP) mode - videos and documents are hidden " +
                "by the phone itself, so these results cannot be complete.");
        }
    }

    public void OnScanFinished(ScanOutcome outcome)
    {
        // Said plainly, because every one of these means "do not treat this as
        // a full picture of the device" - and a scan that silently looks
        // complete is worse than one that fails loudly.
        if (outcome.Completed && !outcome.Stalled && !outcome.Faulted &&
            !outcome.CameraMode && outcome.UndeterminedFiles == 0 && outcome.SubtreeLosses == 0)
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
        if (outcome.UndeterminedFiles > 0) Console.WriteLine($"  - {outcome.UndeterminedFiles} file(s) were never examined (listed above as [UNCHECKED])");
    }
}
