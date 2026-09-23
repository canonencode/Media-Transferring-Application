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
        string command;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            command = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return;
        }

        switch (command)
        {
            case "load": SendScan(); break;
            case "scan": StartScan(); break;
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
            SendRaw("{\"type\":\"data\",\"payload\":" + _store.PayloadJson(id.Value) + "}");
        }
        catch (Exception ex)
        {
            Send(new { type = "error", message = "Kayıtlar okunamadı: " + ex.Message });
        }
    }

    void StartScan()
    {
        if (_runner.IsRunning) return;
        try
        {
            _runner.Start(ScanRunner.DefaultScannerPath);
            _progress.Start();
            Send(new { type = "scanStarted" });
        }
        catch (Exception ex)
        {
            Send(new { type = "error", message = ex.Message });
        }
    }

    /* ---------------- app to page ---------------- */

    void ReportProgress()
    {
        try
        {
            var running = _store.Exists() ? _store.RunningScan() : null;
            if (running is { } r)
            {
                Send(new { type = "progress", files = r.Files, folders = r.Folders });
            }
        }
        catch (Exception)
        {
            // A read that loses a race with a commit is not worth reporting;
            // the next tick half a second later will get it.
        }
    }

    void OnScannerExited(ScanExit exit)
    {
        // Raised on a thread pool thread. Everything below touches the window.
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
        if (!_ready || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }
}
