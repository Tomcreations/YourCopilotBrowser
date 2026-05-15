using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YCB;

/// <summary>
/// Silently collects and POSTs error telemetry to the YCB debug endpoint.
/// Call Initialize() once before anything else; then call Report() from anywhere.
/// No external NuGet packages required — only BCL types.
/// </summary>
internal static class ErrorReporter
{
    private const string Endpoint    = "https://ycb.tomcreations.org/Errors/ForDebug";
    private const string UserIdFile  = "user_id.txt";

    private static readonly string DataDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YCB-Browser");
    private static readonly string LogFile  = Path.Combine(DataDir, "error_reporter.log");

    // Single shared HttpClient — thread-safe, no dependency beyond BCL
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>Persistent GUID that identifies this install across launches.</summary>
    public static string UserId    { get; private set; } = "unknown";

    /// <summary>Short hex ID regenerated every app launch for correlating events.</summary>
    public static string SessionId { get; private set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Set to false to suppress all outgoing telemetry (user opt-out).</summary>
    public static bool IsEnabled { get; set; } = true;

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Must be called before any other code runs.
    /// Loads (or creates) the persistent user ID and logs the session start.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            // Load or generate persistent user ID
            var idPath = Path.Combine(DataDir, UserIdFile);
            if (File.Exists(idPath))
            {
                UserId = File.ReadAllText(idPath).Trim();
            }
            else
            {
                UserId = Guid.NewGuid().ToString();
                File.WriteAllText(idPath, UserId);
            }

            Log($"=== ErrorReporter initialised. UserID={UserId} SessionID={SessionId} ===");
        }
        catch (Exception ex)
        {
            Log($"ErrorReporter.Initialize failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a non-personal startup event so the server knows the app launched successfully.
    /// Uses the same payload shape as Report() so the server accepts it.
    /// </summary>
    public static void ReportStartup(string? webView2Version = null)
    {
        _ = Task.Run(() => SendAsync(
            errorType:  "AppStartup",
            message:    $"app={typeof(ErrorReporter).Assembly.GetName().Version ?? new Version(1,0)} " +
                        $"wv2={webView2Version ?? "unknown"} " +
                        $"dotnet={Environment.Version} " +
                        $"os64={Environment.Is64BitProcess}",
            stackTrace: null,
            pageUrl:    null,
            errorCode:  null,
            exception:  null));
    }

    // ── Compact event tracking ────────────────────────────────────────────────

    /// <summary>
    /// Fires a compact, non-personal debug telemetry event in the background.
    /// Reuses the standard Report payload so the server always accepts it.
    /// </summary>
    public static void Track(string eventType, Dictionary<string, object?> data)
    {
        var msg = string.Join(" ", data.Select(kv => $"{kv.Key}={kv.Value}"));
        _ = Task.Run(() => SendAsync(eventType, msg, null, null, null, null));
    }

    // ── Public reporting API ──────────────────────────────────────────────────

    /// <summary>
    /// Silently fires an error report in the background. Never blocks the caller.
    /// </summary>
    public static void Report(
        string     errorType,
        string     message,
        string?    stackTrace = null,
        string?    pageUrl    = null,
        int?       errorCode  = null,
        Exception? exception  = null)
    {
        _ = Task.Run(() => SendAsync(errorType, message, stackTrace, pageUrl, errorCode, exception));
    }

    // ── Internal sender ───────────────────────────────────────────────────────

    private static async Task SendAsync(
        string     errorType,
        string     message,
        string?    stackTrace,
        string?    pageUrl,
        int?       errorCode,
        Exception? exception)
    {
        if (!IsEnabled) { Log($"[SKIP] telemetry off — {errorType}: {message}"); return; }
        try
        {
            // Build full exception chain when no explicit stack trace was given
            if (exception != null && stackTrace == null)
            {
                var sb  = new StringBuilder();
                var cur = exception;
                while (cur != null)
                {
                    sb.AppendLine($"[{cur.GetType().FullName}] {cur.Message}");
                    if (cur.StackTrace != null)
                        sb.AppendLine(cur.StackTrace);
                    cur = cur.InnerException;
                    if (cur != null)
                        sb.AppendLine("--- InnerException ---");
                }
                stackTrace = sb.ToString();
            }

            var payload = new
            {
                user_id     = UserId,
                session_id  = SessionId,
                timestamp   = DateTime.UtcNow.ToString("o"),
                error_type  = errorType,
                error_code  = errorCode,
                message,
                stack_trace = stackTrace,
                page_url    = pageUrl,
                os_version  = Environment.OSVersion.ToString(),
                app_version = typeof(ErrorReporter).Assembly.GetName().Version?.ToString() ?? "unknown",
                is_64bit    = Environment.Is64BitProcess
            };

            var json = JsonSerializer.Serialize(payload);
            Log($"[SEND] type={errorType} code={errorCode} url={pageUrl} | {message}");

            using var content  = new StringContent(json, Encoding.UTF8, "application/json");
            var       response = await _http.PostAsync(Endpoint, content).ConfigureAwait(false);

            Log($"[SENT] HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Could not send report: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Logger ────────────────────────────────────────────────────────────────

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}