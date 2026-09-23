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
    readonly ScanRunner _runner = new();
    readonly System.Windows.Forms.Timer _progress = new() { Interval = 500 };

    bool _ready;
    int _progressFailures;

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
        _runner.Exited += OnScannerExited;

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
            Diagnostics.Write("tarama baslatiliyor: " + ScanRunner.DefaultScannerPath);
            _runner.Start(ScanRunner.DefaultScannerPath);
            _progressFailures = 0;
            _progress.Start();
            Send(new { type = "scanStarted" });
        }
        catch (Exception ex)
        {
            Diagnostics.Write("tarama baslatilamadi: " + ex.Message);
            Send(new { type = "error", message = ex.Message });
        }
    }

    /* ---------------- app to page ---------------- */

    void ReportProgress()
    {
        try
        {
            var running = _store.Exists() ? _store.RunningScan() : null;
            _progressFailures = 0;

            if (running is { } r)
            {
                Send(new { type = "progress", files = r.Files, folders = r.Folders });
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

    void OnScannerExited(ScanExit exit)
    {
        // Raised on a thread pool thread. Everything below touches the window.
        Diagnostics.Write($"tarayici cikti: kod {exit.ExitCode}, crashed={exit.Crashed}");
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _progress.Stop();
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
