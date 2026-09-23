using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using MediaTransfer.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MediaTransfer.App;

/// <summary>
/// The window. Almost all of it is the page; this class is the bridge between
/// that page and the two things only a native process can do - start the
/// scanner, and read the database it writes.
/// </summary>
public sealed class MainWindow : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly ScanStore _store = new(SqliteScanSink.DefaultDatabasePath);
    readonly ProbeRunner _runner = new();
    readonly TransferSetup _setup = new(SqliteScanSink.DefaultDatabasePath);
    // 300 ms rather than 500: the rows are inserted into the list rather than
    // redrawn, so a shorter interval costs a small indexed read and buys an
    // arrival that looks continuous instead of stepped.
    readonly System.Windows.Forms.Timer _progress = new() { Interval = 300 };

    bool _ready;
    int _progressFailures;
    long _lastFileId;

    // Set for as long as a transfer is running, and the flag the progress tick
    // reads to know which of the two jobs it is reporting on. The value is the
    // ledger's highest id BEFORE the transfer began: the ledger is cumulative by
    // design, so everything past this mark is what this run did.
    long? _copyBaseline;
    int _copyPlanned;
    long _copyPlannedBytes;

    public MainWindow()
    {
        Text = "Telefon Medya Envanteri";
        MinimumSize = new Size(760, 520);
        Size = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        // The page paints its own background; matching it here stops a white
        // flash while WebView2 starts.
        BackColor = Color.FromArgb(0x2a, 0x2a, 0x2d);

        Controls.Add(_web);
        _progress.Tick += (_, _) => ReportProgress();
        _runner.Exited += OnProbeExited;

        Load += async (_, _) => await StartWebView();
    }

    async Task StartWebView()
    {
        try
        {
            // A user data folder of our own, rather than beside the exe, so the
            // app works when installed somewhere the user cannot write to.
            string profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaTransfer", "webview");
            Directory.CreateDirectory(profile);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ShowStartupFailure(
                "WebView2 başlatılamadı. Windows 11'de kurulu gelir; kurulu değilse " +
                "Microsoft Edge WebView2 Runtime kurulmalı.\n\n" + ex.Message);
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        // Nothing in this app comes from the internet, so nothing is allowed to.
        core.Settings.AreHostObjectsAllowed = false;

        string root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping("app.local", root, CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnMessage;

        _ready = true;
        core.Navigate("https://app.local/index.html");
    }

    void ShowStartupFailure(string message)
    {
        Controls.Remove(_web);
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28),
            ForeColor = Color.FromArgb(0xe8, 0xe8, 0xea),
            Font = new Font("Segoe UI", 10f),
            Text = message,
        });
    }

    /* ---------------- page to app ---------------- */

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string command, text = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            command = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("text", out var t)) text = t.GetString() ?? "";
        }
        catch (JsonException)
        {
            return;
        }

        switch (command)
        {
            case "load": SendScan(); break;
            case "scan": StartScan(); break;
            case "log": Diagnostics.Write(text); break;
            case "drives": SendDrives(); break;
            case "browse": BrowseForFolder(); break;
            case "preflight": SendPreflight(e.WebMessageAsJson); break;
            case "startTransfer": StartTransfer(e.WebMessageAsJson); break;
        }
    }

    void SendScan()
    {
        if (!_store.Exists())
        {
            Send(new { type = "empty", message = "Henüz kayıtlı tarama yok. Telefonu bağlayıp tarayın." });
            return;
        }

        try
        {
            long? id = _store.LatestFinishedScanId();
            if (id is null)
            {
                Send(new { type = "empty", message = "Tamamlanmış tarama yok." });
                return;
            }
            string payload = _store.PayloadJson(id.Value);
            Diagnostics.Write($"tarama #{id} gonderiliyor, {payload.Length} karakter");
            SendRaw("{\"type\":\"data\",\"payload\":" + payload + "}");
        }
        catch (Exception ex)
        {
            Diagnostics.Write("kayitlar okunamadi: " + ex);
            Send(new { type = "error", message = "Kayıtlar okunamadı: " + ex.Message });
        }
    }

    void StartScan()
    {
        if (_runner.IsRunning) return;
        try
        {
            Diagnostics.Write("tarama baslatiliyor: " + ProbeRunner.DefaultScannerPath);
            _runner.Start(ProbeRunner.DefaultScannerPath, ProbeJob.Scan);
            _progressFailures = 0;
            _lastFileId = 0;
            _progress.Start();
            Send(new { type = "scanStarted" });
        }
        catch (Exception ex)
        {
            Diagnostics.Write("tarama baslatilamadi: " + ex.Message);
            Send(new { type = "error", message = ex.Message });
        }
    }

    /* ---------------- transfer setup ---------------- */

    void SendDrives()
    {
        try
        {
            var drives = TransferSetup.Drives().Select(d => new
            {
                root = d.Root, label = d.Label, free = d.Free, total = d.Total, ready = d.Ready,
            });

            string? device = null;
            long? scanId = _store.Exists() ? _store.LatestFinishedScanId() : null;
            if (scanId is not null) device = _store.DeviceName();

            Send(new
            {
                type = "drives",
                drives,
                defaultFolder = TransferSetup.DefaultFolderName(device),
                scanId,
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Write("suruculer okunamadi: " + ex);
            Send(new { type = "error", message = "Sürücüler okunamadı: " + ex.Message });
        }
    }

    void BrowseForFolder()
    {
        // Opened by the host, because a page has no way to ask for a folder and
        // no business knowing the filesystem. The page only ever learns the one
        // path the user chose.
        using var dialog = new FolderBrowserDialog
        {
            Description = "Aktarım klasörünü seçin",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            Send(new { type = "browsed", path = dialog.SelectedPath });
        }
    }

    void SendPreflight(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string path = root.GetProperty("root").GetString() ?? "";
            string folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
            bool group = !root.TryGetProperty("group", out var g) || g.GetBoolean();
            long scanId = root.GetProperty("scanId").GetInt64();
            string sources = root.TryGetProperty("sources", out var sv) ? sv.GetString() ?? "all" : "all";

            var p = _setup.Check(scanId, path, folder, group, sources);
            Send(new
            {
                type = "preflight",
                destination = p.Destination,
                required = p.RequiredBytes,
                files = p.Files,
                renamed = p.Renamed,
                free = p.FreeBytes,
                fits = p.Fits,
                problem = p.Problem,
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Write("preflight hatasi: " + ex);
            Send(new { type = "error", message = "Hesaplanamadı: " + ex.Message });
        }
    }

    /// <summary>
    /// Starts the copier as a child process, the same way a scan is started.
    ///
    /// The preflight is run again here rather than trusting the figures the page
    /// is showing. Those were measured when the page last asked, and a drive can
    /// fill up between looking and pressing - which is precisely the failure
    /// this project exists to prevent, so the last word on whether it fits
    /// belongs to the moment the transfer actually begins.
    /// </summary>
    void StartTransfer(string json)
    {
        if (_runner.IsRunning) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string path = root.GetProperty("root").GetString() ?? "";
            string folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
            bool group = !root.TryGetProperty("group", out var g) || g.GetBoolean();
            long scanId = root.GetProperty("scanId").GetInt64();
            string sources = root.TryGetProperty("sources", out var sv) ? sv.GetString() ?? "all" : "all";

            var check = _setup.Check(scanId, path, folder, group, sources);
            if (check.Files == 0)
            {
                Send(new { type = "error", message = "Seçilen kaynaklarda aktarılacak dosya yok." });
                return;
            }
            if (!check.Fits)
            {
                Send(new { type = "error", message = check.Problem ?? "Bu konuma aktarılamaz." });
                return;
            }

            // Read before the child starts, never after: a row it commits in the
            // meantime would fall on the wrong side of the mark and go missing
            // from the count for the rest of the transfer.
            _copyBaseline = _store.Exists() ? _store.LatestCopyId() : 0;
            _copyPlanned = check.Files;
            _copyPlannedBytes = check.RequiredBytes;

            Diagnostics.Write($"aktarim baslatiliyor: tarama {scanId} -> {check.Destination}");
            _runner.Start(ProbeRunner.DefaultScannerPath, ProbeJob.Copy,
                "--copy", check.Destination, scanId.ToString(CultureInfo.InvariantCulture), sources);

            _progressFailures = 0;
            _progress.Start();
            Send(new
            {
                type = "transferStarted",
                destination = check.Destination,
                files = check.Files,
                bytes = check.RequiredBytes,
            });
        }
        catch (Exception ex)
        {
            _copyBaseline = null;
            Diagnostics.Write("aktarim baslatilamadi: " + ex);
            Send(new { type = "error", message = "Aktarım başlatılamadı: " + ex.Message });
        }
    }

    /* ---------------- app to page ---------------- */



    void ReportProgress()
    {
        if (_copyBaseline is { } baseline)
        {
            ReportCopyProgress(baseline);
            return;
        }

        try
        {
            var running = _store.Exists() ? _store.RunningScan() : null;
            _progressFailures = 0;

            if (running is { } r)
            {
                // The rows that arrived since the last tick, so the wait shows
                // the user their own photographs appearing rather than a number
                // going up. Capped: nobody reads 700 rows a second, and sending
                // them would cost more than the scan.
                var (arrived, lastId) = _store.NewFilesSince(r.ScanId, _lastFileId, 60);
                _lastFileId = lastId;

                Send(new
                {
                    type = "progress",
                    files = r.Files,
                    folders = r.Folders,
                    expected = r.Expected,
                    folder = r.CurrentFolder,
                    elapsed = (int)r.ElapsedSeconds,
                    arrived,
                });
            }
            else if (!_runner.IsRunning)
            {
                // No running row and no child process: the scan is over and the
                // exit notice did not arrive. Ending it here rather than leaving
                // a counter frozen forever - a stuck number with no explanation
                // is the failure this project keeps finding, and the progress
                // loop is not allowed to be the thing that produces one.
                Diagnostics.Write("progress: tarama bitmis ama cikis haberi gelmemis, kapatiliyor");
                _progress.Stop();
                Send(new { type = "scanEnded", crashed = false, exitCode = 0, message = "" });
                SendScan();
            }
        }
        catch (Exception ex)
        {
            // One failed read is a lost race with a commit and the next tick
            // half a second later will get it. A run of them is a real problem,
            // and silence about it would leave the user watching a number that
            // has quietly stopped meaning anything.
            _progressFailures++;
            Diagnostics.Write($"progress hatasi ({_progressFailures}): {ex.GetType().Name}: {ex.Message}");
            if (_progressFailures == 6)
            {
                Send(new
                {
                    type = "progressLost",
                    message = "Tarama sürüyor ama ilerleme okunamıyor: " + ex.Message,
                });
            }
        }
    }

    /// <summary>
    /// The transfer's half of the progress tick, read out of the ledger for the
    /// same reason the scan's is read out of the file table: the child commits
    /// as it goes and WAL lets this connection see it, so there is one channel
    /// between the two processes instead of two that could disagree.
    /// </summary>
    void ReportCopyProgress(long baseline)
    {
        try
        {
            var p = _store.CopyProgressSince(baseline);
            _progressFailures = 0;

            Send(new
            {
                type = "copyProgress",
                done = p.Done,
                failed = p.Failed,
                bytes = p.Bytes,
                file = p.CurrentFile,
                planned = _copyPlanned,
                plannedBytes = _copyPlannedBytes,
            });
        }
        catch (Exception ex)
        {
            // One lost read is a race with a commit and the next tick will get
            // it. A run of them means the figures on screen have quietly stopped
            // moving, and the user is owed that rather than a frozen number.
            _progressFailures++;
            Diagnostics.Write($"aktarim ilerlemesi okunamadi ({_progressFailures}): {ex.Message}");
            if (_progressFailures == 6)
            {
                Send(new
                {
                    type = "progressLost",
                    message = "Aktarım sürüyor ama ilerleme okunamıyor: " + ex.Message,
                });
            }
        }
    }

    void OnProbeExited(ProbeExit exit)
    {
        // Raised on a thread pool thread. Everything below touches the window.
        Diagnostics.Write($"{exit.Job} cikti: kod {exit.ExitCode}, crashed={exit.Crashed}");
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _progress.Stop();

            if (exit.Job == ProbeJob.Copy)
            {
                EndTransfer(exit);
                return;
            }

            Send(new
            {
                type = "scanEnded",
                crashed = exit.Crashed,
                exitCode = exit.ExitCode,
                message = exit.Crashed
                    ? "Tarayıcı beklenmedik şekilde kapandı. Genellikle cihazın kilitlenmesi demektir: " +
                      "kabloyu çıkarıp takın ve tekrar deneyin."
                    : "",
            });
            SendScan();
        });
    }

    /// <summary>
    /// Reports what a finished transfer actually did, read back out of the
    /// ledger rather than from anything the child said.
    ///
    /// That distinction is the point of the ledger. A child that was killed
    /// mid-file printed nothing and exited with a code that explains nothing,
    /// but its rows are on disk and they say exactly how far it got - so the
    /// account given here is the same whether the transfer finished, was
    /// cancelled, or died.
    /// </summary>
    void EndTransfer(ProbeExit exit)
    {
        long baseline = _copyBaseline ?? 0;
        _copyBaseline = null;

        try
        {
            var p = _store.CopyProgressSince(baseline);
            var failures = p.Failed > 0 ? _store.CopyFailuresSince(baseline, 50) : [];
            int notReached = Math.Max(0, _copyPlanned - p.Done - p.Failed);

            Diagnostics.Write($"aktarim bitti: {p.Done} kopyalandi, {p.Failed} basarisiz, " +
                $"{notReached} ulasilmadi, kod {exit.ExitCode}");

            Send(new
            {
                type = "transferEnded",
                done = p.Done,
                failed = p.Failed,
                notReached,
                bytes = p.Bytes,
                planned = _copyPlanned,
                crashed = exit.Crashed,
                failures,
                message = exit.Crashed
                    ? "Aktarım beklenmedik şekilde durdu. Kopyalanan dosyalar yerinde; kabloyu çıkarıp " +
                      "takın ve tekrar başlatın, biten dosyalar ikinci kez kopyalanmaz."
                    : "",
            });
        }
        catch (Exception ex)
        {
            // This is the "say what happened" path, so it is the last one
            // allowed to be the thing that goes wrong. An exception escaping
            // here reaches BeginInvoke with nothing to catch it and closes the
            // window - leaving a finished transfer looking like a crash, which
            // is the exact confusion the ledger exists to prevent. The files are
            // copied and the rows are on disk either way; only the summary is
            // lost, so that is all this admits to.
            Diagnostics.Write("aktarim ozeti okunamadi: " + ex);
            // "unknown" rather than zeros. Sending done = 0 would have the page
            // draw "0 files copied, all present" over a transfer that may have
            // moved thousands - a confident wrong answer, which is worse than
            // admitting the summary could not be read.
            Send(new
            {
                type = "transferEnded",
                unknown = true,
                crashed = exit.Crashed,
                planned = _copyPlanned,
                failures = Array.Empty<object>(),
                message = "Aktarım bitti ama özeti okunamadı: " + ex.Message +
                    " Kopyalanan dosyalar yerinde; listeyi yenilemek doğru sayıyı gösterir.",
            });
        }
    }

    void Send(object message) => SendRaw(JsonSerializer.Serialize(message));

    void SendRaw(string json)
    {
        if (!_ready || _web.CoreWebView2 is null)
        {
            Diagnostics.Write("gonderilemedi, WebView hazir degil: " + json[..Math.Min(60, json.Length)]);
            return;
        }
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // A message that cannot cross the bridge leaves the page showing
            // whatever it last knew, which is worse than showing nothing.
            Diagnostics.Write($"kopru hatasi ({json.Length} karakter): {ex.GetType().Name}: {ex.Message}");
        }
    }
}
