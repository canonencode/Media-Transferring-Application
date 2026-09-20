/// <summary>
/// Prints a scan as an indented tree, and keeps the running totals the summary
/// reports. This is the probe's own view of a scan; the SQLite writer will be a
/// sibling of it, not a replacement for the walk.
/// </summary>
sealed class ConsoleScanSink : IScanSink
{
    public int MediaFiles { get; private set; }
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
}
