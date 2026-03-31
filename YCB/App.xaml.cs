using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace YCB;

public partial class App : Application
{
    private const string MutexName = "YCBBrowserSingleInstance";
    private const string PipeName  = "YCBBrowserPipe";
    private Mutex? _mutex;

    private static readonly string LogDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YCB-Browser");
    private static readonly string LogFile = Path.Combine(LogDir, "startup.log");

    // Watchdog state
    private static volatile int  _lastHeartbeatTick = 0;
    private static volatile bool _watchdogShown     = false;
    private const  int           FreezeThresholdMs  = 8000;  // 8 s freeze = show diag

    // ── Trace helper ──────────────────────────────────────────────────────────
    internal static void WriteTrace(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    // ── Entry point ───────────────────────────────────────────────────────────
    protected override void OnStartup(StartupEventArgs e)
    {
        // ErrorReporter must be the very first thing that runs so that every
        // subsequent error — including startup failures — can be captured.
        ErrorReporter.Initialize();

        // Register extended code-page encodings (e.g. CP1252) needed for
        // fixing UTF-8 mojibake in copilot CLI output on .NET 5+.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        base.OnStartup(e);

        // Stamp log immediately
        WriteTrace("=== YCB starting ===");
        WriteTrace($"OS:  {Environment.OSVersion}");
        WriteTrace($"CPU: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process");
        WriteTrace($"Dir: {AppContext.BaseDirectory}");

        // Global crash handlers
        DispatcherUnhandledException += (s, ex) =>
        {
            ex.Handled = true;
            WriteTrace($"[CRASH] Dispatcher: {ex.Exception?.GetType().Name}: {ex.Exception?.Message}");
            ErrorReporter.Report("UnhandledException", ex.Exception?.Message ?? "Unknown dispatcher error", exception: ex.Exception);
            ShowDiagnostics("Unhandled Error", ex.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            var exc = ex.ExceptionObject as Exception;
            WriteTrace($"[CRASH] AppDomain: {exc?.GetType().Name}: {exc?.Message}");
            ErrorReporter.Report("FatalException", exc?.Message ?? "Unknown fatal error", exception: exc);
            try { Dispatcher.Invoke(() => ShowDiagnostics("Fatal Error", exc)); }
            catch { ShowDiagnosticsOnNewThread("Fatal Error", exc); }
        };

        try
        {
            string? urlArg = e.Args.Length > 0 ? e.Args[0] : null;

            // Single-instance check
            _mutex = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                WriteTrace("Another instance running — forwarding and exiting");
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(2000);
                    using var writer = new StreamWriter(client);
                    writer.WriteLine(urlArg ?? "__focus__");
                    writer.Flush();
                }
                catch { }
                Shutdown();
                return;
            }

            // Pre-launch diagnostics
            WriteTrace("Running pre-launch diagnostics...");
            var diag = RunDiagnostics();
            WriteTrace(diag.passed ? "Diagnostics passed" : $"Diagnostics FAILED: {diag.reason}");

            if (!diag.passed)
            {
                ShowDiagnostics(diag.reason!, null, diag.detail);
                Shutdown();
                return;
            }

            // Start background watchdog BEFORE creating MainWindow
            StartWatchdog();

            WriteTrace("Starting pipe server");
            StartPipeServer();

            WriteTrace("Creating MainWindow");
            var window = new MainWindow(startupUrl: urlArg);
            WriteTrace("Showing MainWindow");
            window.Show();
            WriteTrace("Startup complete");

            // Send a lightweight startup telemetry event (no personal data)
            string? wv2Ver = null;
            try { wv2Ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            ErrorReporter.ReportStartup(wv2Ver);
        }
        catch (Exception ex)
        {
            WriteTrace($"[CRASH] Startup: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            ErrorReporter.Report("StartupError", ex.Message, exception: ex);
            ShowDiagnostics("Startup Error", ex);
        }
    }

    // ── Watchdog ──────────────────────────────────────────────────────────────
    // Heartbeat: the UI thread stamps a tick every 500 ms via a DispatcherTimer.
    // The watchdog background thread checks that stamp. If it hasn't updated in
    // FreezeThresholdMs, the UI thread is frozen → show diagnostics on a new STA thread.

    private void StartWatchdog()
    {
        _lastHeartbeatTick = Environment.TickCount;

        // DispatcherTimer runs on the UI thread — it keeps the heartbeat alive
        var heartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        heartbeat.Tick += (_, _) =>
        {
            _lastHeartbeatTick = Environment.TickCount;
        };
        heartbeat.Start();
        WriteTrace("Watchdog heartbeat timer started");

        // Background thread watches the heartbeat
        var watchThread = new Thread(() =>
        {
            // Give the app a moment to fully start before monitoring
            Thread.Sleep(3000);
            WriteTrace("Watchdog monitoring started");

            while (true)
            {
                Thread.Sleep(1000);

                int elapsed = Environment.TickCount - _lastHeartbeatTick;
                if (elapsed > FreezeThresholdMs && !_watchdogShown)
                {
                    _watchdogShown = true;
                    WriteTrace($"[WATCHDOG] UI thread frozen for {elapsed}ms — showing diagnostics");
                    ErrorReporter.Report("WatchdogFreeze", $"UI thread unresponsive for {elapsed / 1000} seconds.");
                    ShowDiagnosticsOnNewThread("App Frozen",
                        new Exception($"The UI thread has not responded for {elapsed / 1000} seconds."));
                }
                else if (elapsed < 2000 && _watchdogShown)
                {
                    // Recovered — reset so it can fire again if it freezes again
                    _watchdogShown = false;
                    WriteTrace("[WATCHDOG] UI thread recovered");
                }
            }
        });
        watchThread.IsBackground = true;
        watchThread.Name = "YCB-Watchdog";
        watchThread.Start();
    }

    // Shows diagnostics on a brand-new STA thread (needed when UI thread is dead/frozen)
    private static void ShowDiagnosticsOnNewThread(string title, Exception? ex)
    {
        var t = new Thread(() =>
        {
            ShowDiagnostics(title, ex);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    // ── Pre-launch diagnostics ────────────────────────────────────────────────
    private static (bool passed, string? reason, string? detail) RunDiagnostics()
    {
        var sb = new StringBuilder();

        string exePath = Path.Combine(AppContext.BaseDirectory, "YCB.exe");
        sb.AppendLine($"EXE:       {exePath}  [{(File.Exists(exePath) ? "OK" : "MISSING")}]");

        string wv2Version = "not found";
        bool wv2OK = false;
        try
        {
            wv2Version = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "null";
            wv2OK = true;
        }
        catch (Exception ex)
        {
            wv2Version = $"ERROR: {ex.Message}";
        }
        sb.AppendLine($"WebView2:  {wv2Version}");

        foreach (var dll in new[] { "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll" })
            sb.AppendLine($"DLL {dll,-35} [{(File.Exists(Path.Combine(AppContext.BaseDirectory, dll)) ? "OK" : "MISSING")}]");

        WriteTrace("Diagnostics:\n" + sb);

        if (!wv2OK)
            return (false, "WebView2 Runtime is missing",
                sb + "\nFix: Visit https://go.microsoft.com/fwlink/p/?LinkId=2124703 to install WebView2.");

        return (true, null, null);
    }

    // ── Diagnostic window ─────────────────────────────────────────────────────
    internal static void ShowDiagnostics(string title, Exception? ex, string? extraDetail = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"YCB Browser — {title}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();

        // System info
        sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS:      {Environment.OSVersion}");
        try { sb.AppendLine($"Memory:  {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB used"); } catch { }
        sb.AppendLine();

        if (extraDetail != null) { sb.AppendLine(extraDetail); sb.AppendLine(); }

        if (ex != null)
        {
            var current = ex;
            while (current != null)
            {
                sb.AppendLine($"[{current.GetType().Name}]");
                sb.AppendLine(current.Message);
                sb.AppendLine();
                if (current.StackTrace != null) sb.AppendLine(current.StackTrace);
                current = current.InnerException;
                if (current != null) { sb.AppendLine(); sb.AppendLine("Caused by:"); sb.AppendLine(new string('─', 40)); }
            }
        }

        // Append recent log lines
        sb.AppendLine();
        sb.AppendLine(new string('─', 60));
        sb.AppendLine("Recent log:");
        try
        {
            var lines = File.ReadAllLines(LogFile);
            foreach (var l in lines.TakeLast(30)) sb.AppendLine(l);
        }
        catch { sb.AppendLine("(log unavailable)"); }

        sb.AppendLine();
        sb.AppendLine($"Full log: {LogFile}");

        try { File.AppendAllText(Path.Combine(LogDir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{sb}\n\n"); } catch { }
        WriteTrace($"Showing diagnostics: {title}");

        var textBox = new TextBox
        {
            Text = sb.ToString(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            Foreground = new SolidColorBrush(Color.FromRgb(240, 80, 80)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true
        };

        var win = new Window
        {
            Title = $"YCB Diagnostics — {title}",
            Content = textBox,
            Width = 860,
            Height = 540,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true
        };

        win.ShowDialog();
    }

    internal static void ShowError(string title, Exception? ex) => ShowDiagnostics(title, ex);

    // ── Single-instance pipe server ───────────────────────────────────────────
    private void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? msg = reader.ReadLine();
                    if (msg != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow win)
                            {
                                if (msg == "__focus__") win.BringToFront();
                                else win.OpenUrl(msg);
                            }
                        });
                    }
                }
                catch { Thread.Sleep(500); }
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }
}