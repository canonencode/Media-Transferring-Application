using System.Diagnostics;

namespace MediaTransfer.App;

/// <summary>
/// Starts the scanner and watches it finish.
///
/// A separate process on purpose. The scanner calls Environment.FailFast when a
/// device wedges past recovery, which is the right answer when a COM call will
/// never return but takes the whole process with it. Run in-process, a stuck
/// phone would close this window; run as a child, the window stays up, sees the
/// exit code, and can say what happened.
///
/// The child is not asked for progress. It already writes every row to SQLite
/// as it goes, and the reader sees those rows through WAL, so the database is
/// the channel. A second one would be a second thing to keep in step.
/// </summary>
public sealed class ScanRunner
{
    Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Raised when the scanner exits, on a thread pool thread.</summary>
    public event Action<ScanExit>? Exited;

    /// <summary>Where the scanner executable is expected to sit next to this app.</summary>
    public static string DefaultScannerPath
    {
        get
        {
            string here = AppContext.BaseDirectory;
            // Side by side in a published folder, and up-and-across in a dev
            // build, because the two projects have their own bin folders.
            string[] candidates =
            [
                Path.Combine(here, "Device Probe.exe"),
                Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "Device Probe", "bin", "Debug", "net10.0", "Device Probe.exe")),
            ];
            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return candidates[0];
        }
    }

    public void Start(string scannerPath)
    {
        if (IsRunning) throw new InvalidOperationException("Bir tarama zaten sürüyor.");
        if (!File.Exists(scannerPath))
        {
            throw new FileNotFoundException(
                "Tarayıcı bulunamadı. Önce Device Probe projesi derlenmeli.", scannerPath);
        }

        var info = new ProcessStartInfo(scannerPath)
        {
            // The scanner's console output is its own view of the scan and this
            // window has a better one, so it is captured rather than shown - but
            // kept, because when the child dies unexpectedly its last lines are
            // the only explanation there is.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scannerPath)!,
        };

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var tail = new Queue<string>();

        process.OutputDataReceived += (_, e) => Remember(tail, e.Data);
        process.ErrorDataReceived += (_, e) => Remember(tail, e.Data);
        process.Exited += (_, _) =>
        {
            int code = process.ExitCode;
            _process = null;
            // FailFast leaves a non-zero code that is not a normal error exit;
            // the distinction matters because a wedged device needs replugging
            // while a plain failure usually does not.
            Exited?.Invoke(new ScanExit(code, code != 0, string.Join("\n", tail)));
            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    static void Remember(Queue<string> tail, string? line)
    {
        if (line is null) return;
        tail.Enqueue(line);
        while (tail.Count > 12) tail.Dequeue();
    }
}

/// <param name="Crashed">The scanner did not end cleanly - a wedged device, or a fault.</param>
/// <param name="LastOutput">Its final lines, which are the only account of why.</param>
public readonly record struct ScanExit(int ExitCode, bool Crashed, string LastOutput);
