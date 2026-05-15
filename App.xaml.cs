using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

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
    private static volatile bool _suspendWatchdog   = false;   // true while system is sleeping
    private const  int           FreezeThresholdMs  = 12000;   // 12 s — generous to avoid false positives

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

        // Global crash handlers — log silently, never show a dialog to the user
        DispatcherUnhandledException += (s, ex) =>
        {
            ex.Handled = true;
            WriteTrace($"[CRASH] Dispatcher: {ex.Exception?.GetType().Name}: {ex.Exception?.Message}");
            ErrorReporter.Report("UnhandledException", ex.Exception?.Message ?? "Unknown dispatcher error", exception: ex.Exception);
            // Log to errors.log but do NOT show a popup — most of these are harmless
            // (WebView2 renderer hiccups, GPU context lost after sleep, cancelled tasks, etc.)
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Unhandled: {ex.Exception?.GetType().Name}");
                sb.AppendLine(ex.Exception?.Message);
                sb.AppendLine(ex.Exception?.StackTrace);
                sb.AppendLine();
                File.AppendAllText(Path.Combine(LogDir, "errors.log"), sb.ToString());
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            var exc = ex.ExceptionObject as Exception;
            WriteTrace($"[CRASH] AppDomain: {exc?.GetType().Name}: {exc?.Message}");
            ErrorReporter.Report("FatalException", exc?.Message ?? "Unknown fatal error", exception: exc);
            // Log to errors.log — only show UI if the runtime is actually terminating
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Fatal: {exc?.GetType().Name}");
                sb.AppendLine(exc?.Message);
                sb.AppendLine(exc?.StackTrace);
                sb.AppendLine();
                File.AppendAllText(Path.Combine(LogDir, "errors.log"), sb.ToString());
            }
            catch { }
            if (ex.IsTerminating)
            {
                try { Dispatcher.Invoke(() => ShowDiagnostics("Fatal Error", exc)); }
                catch { ShowDiagnosticsOnNewThread("Fatal Error", exc); }
            }
        };

        try
        {
            string? urlArg = e.Args.Length > 0 ? e.Args[0] : null;

            // Handle --set-default: register YCB in Default Apps and open the settings deep-link
            if (urlArg == "--set-default")
            {
                RegisterUrlHandler(); // ensure registration is current
                OpenDefaultAppsForYCB();
                Shutdown();
                return;
            }

            // Single-instance check
            _mutex = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                WriteTrace("Another instance running — forwarding and exiting");
                // Retry up to 5 times in case pipe server is briefly between connections
                bool sent = false;
                for (int attempt = 0; attempt < 5 && !sent; attempt++)
                {
                    try
                    {
                        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                        client.Connect(1000);
                        using var writer = new StreamWriter(client);
                        writer.WriteLine(urlArg ?? "__focus__");
                        writer.Flush();
                        sent = true;
                    }
                    catch { Thread.Sleep(300); }
                }
                WriteTrace(sent ? "URL forwarded OK" : "Failed to forward URL after 5 attempts");
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

            // Silently re-register URL handler on every launch (keeps exe path current)
            RegisterUrlHandler();

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

        // Reset heartbeat on power resume (sleep/hibernate wake) so the watchdog
        // doesn't fire just because the device was suspended.
        // Also suspend the watchdog when going TO sleep so it can't fire during the transition.
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _suspendWatchdog = true;
                WriteTrace("[WATCHDOG] System suspending — watchdog paused");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _lastHeartbeatTick = Environment.TickCount;
                _watchdogShown = false;
                _suspendWatchdog = false;
                WriteTrace("[WATCHDOG] System resumed from sleep — heartbeat reset, watchdog resumed");
            }
        };

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
            Thread.Sleep(5000);
            WriteTrace("Watchdog monitoring started");

            while (true)
            {
                Thread.Sleep(1000);

                // Don't check during sleep/resume — the heartbeat timer doesn't tick
                if (_suspendWatchdog)
                {
                    _lastHeartbeatTick = Environment.TickCount;
                    continue;
                }

                int elapsed = Environment.TickCount - _lastHeartbeatTick;

                // Negative elapsed = TickCount wrapped or clock skew from sleep — reset
                if (elapsed < 0)
                {
                    _lastHeartbeatTick = Environment.TickCount;
                    continue;
                }

                if (elapsed > FreezeThresholdMs && !_watchdogShown)
                {
                    _watchdogShown = true;
                    WriteTrace($"[WATCHDOG] UI thread frozen for {elapsed}ms — logging silently");
                    ErrorReporter.Report("WatchdogFreeze", $"UI thread unresponsive for {elapsed / 1000} seconds.");
                    // Log full diagnostics to errors.log — NO popup shown to the user
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] YCB Browser — UI Frozen");
                        sb.AppendLine($"UI thread unresponsive for {elapsed / 1000} seconds.");
                        sb.AppendLine($"OS: {Environment.OSVersion}");
                        try { sb.AppendLine($"Memory: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB"); } catch { }
                        sb.AppendLine($"Full log: {LogFile}");
                        sb.AppendLine();
                        File.AppendAllText(Path.Combine(LogDir, "errors.log"), sb.ToString());
                    }
                    catch { }
                    // Silent — no popup. The user doesn't need to see this.
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

    // Shows a small friendly notice instead of the scary diagnostic window
    private static void ShowFriendlyFreezeNotice()
    {
        var t = new Thread(() =>
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YCB-Browser");
            var msg = new TextBlock
            {
                Text = "⚠  We have detected an issue. If there isn't one going on, disregard this — but if there is, view:\nhttps://ycb.tomcreations.org/auth/login?next=/support",
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 20, 20, 10)
            };

            var btn = new Button
            {
                Content = "OK",
                Width = 80,
                Height = 32,
                Margin = new Thickness(0, 0, 20, 16),
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 115, 232)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13
            };

            var stack = new StackPanel();
            stack.Children.Add(msg);
            stack.Children.Add(btn);

            var win = new Window
            {
                Title = "YCB Browser — Issue Detected",
                Content = stack,
                Width = 420,
                Height = 210,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true
            };

            btn.Click += (_, _) => win.Close();
            win.ShowDialog();
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

    // Opens Windows Default Apps scrolled directly to the YCB Browser entry
    internal static void OpenDefaultAppsForYCB()
    {
        // URL-encode the space: "YCB%20Browser" — this deep-links to YCB's protocol page
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppMachine=YCB%20Browser")
                { UseShellExecute = true });
            return;
        }
        catch { }
        // Fallback: explorer.exe
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                "ms-settings:defaultapps?registeredAppMachine=YCB%20Browser")
                { UseShellExecute = false });
            return;
        }
        catch { }
        // Last fallback: generic page
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "ms-settings:defaultapps")
                { UseShellExecute = false });
        }
        catch { }
    }

    // ── URL protocol handler registration ────────────────────────────────────
    private static void RegisterUrlHandler()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                          ?? System.IO.Path.Combine(AppContext.BaseDirectory, "YCB.exe");
            if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                exePath = exePath[..^4] + ".exe";
            var cmd = $"\"{exePath}\" \"%1\"";

            // ProgID registrations (HKCU — no admin needed, kept current every launch)
            var progIds = new (string key, string name, string value)[]
            {
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBUrl",                          "",             "YCB Browser URL"),
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBUrl",                          "URL Protocol", ""),
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBUrl\DefaultIcon",              "",             $"{exePath},0"),
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBUrl\shell\open\command",       "",             cmd),
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBHtml",                         "",             "YCB Browser HTML Document"),
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBHtml\DefaultIcon",             "",             $"{exePath},0"),
                (@"HKEY_CURRENT_USER\SOFTWARE\Classes\YCBHtml\shell\open\command",      "",             cmd),
            };
            foreach (var (key, name, value) in progIds)
                try { Microsoft.Win32.Registry.SetValue(key, name, value); } catch { }

            // Remove any stale HKCU RegisteredApplications/Capabilities that cause a duplicate entry.
            // The installer writes these under HKLM which is the single authoritative source.
            try
            {
                using var ra = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\RegisteredApplications", writable: true);
                ra?.DeleteValue("YCB", throwOnMissingValue: false);
                ra?.DeleteValue("YCB Browser", throwOnMissingValue: false);
            }
            catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"SOFTWARE\Clients\StartMenuInternet\YCB Browser", throwOnMissingSubKey: false); }
            catch { }

            // Update HKLM StartMenuInternet exe paths (requires the install to exist; silently skipped if no admin)
            try
            {
                using var smiKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Clients\StartMenuInternet\YCB Browser", writable: true);
                if (smiKey != null)
                {
                    // shell\open\command — this is what Windows launches when you click the app in Default Apps
                    Microsoft.Win32.Registry.SetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients\StartMenuInternet\YCB Browser\shell\open\command",
                        "", $"\"{exePath}\"");
                    Microsoft.Win32.Registry.SetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients\StartMenuInternet\YCB Browser\DefaultIcon",
                        "", $"{exePath},0");
                    Microsoft.Win32.Registry.SetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients\StartMenuInternet\YCB Browser\Capabilities",
                        "ApplicationIcon", $"{exePath},0");
                }
            }
            catch { /* no admin — HKLM paths stay as installer set them */ }
        }
        catch { }
    }

    // ── Single-instance pipe server ───────────────────────────────────────────
    private void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    // Allow any user/elevation level to connect — fixes "Access denied"
                    // when the first instance is elevated and the second is not (or vice versa)
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));

                    server = NamedPipeServerStreamAcl.Create(
                        PipeName, PipeDirection.In,
                        maxNumberOfServerInstances: 4,
                        transmissionMode: PipeTransmissionMode.Byte,
                        options: PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 0,
                        pipeSecurity: security);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? msg = reader.ReadLine();
                    server.Dispose();
                    server = null;

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
                catch
                {
                    server?.Dispose();
                    Thread.Sleep(200);
                }
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }
}

// ── COM interfaces for setting default browser ────────────────────────────
[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("1F76A169-F994-40AC-8FC8-0959E8874710")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationAssociationRegistrationUI
{
    void LaunchAdvancedAssociationUI(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        string pszAppRegName);
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("1968106D-F3B5-44CF-890E-116FCAA518D2")]
[System.Runtime.InteropServices.ClassInterface(System.Runtime.InteropServices.ClassInterfaceType.None)]
class ApplicationAssociationRegistrationUICoClass { }
