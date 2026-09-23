namespace MediaTransfer.App;

/// <summary>
/// A plain text log beside the database.
///
/// The bridge between the page and the host has no console to fail into: a
/// message that never arrives leaves a frozen counter and no account of why.
/// This is the account. It is small, append-only, and written on every bridge
/// event rather than only on errors, because the useful question afterwards is
/// usually which step did NOT happen.
/// </summary>
public static class Diagnostics
{
    static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaTransfer", "app.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // A log that cannot be written must not become the failure it was
            // added to explain.
        }
    }
}
