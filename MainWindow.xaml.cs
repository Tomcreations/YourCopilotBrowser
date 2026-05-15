using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;

using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Shell;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Automation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows.Media.Animation;
using WpfPath = System.Windows.Shapes.Path;
using IoPath = System.IO.Path;

namespace YCB;

public partial class MainWindow : Window
{
    private readonly List<BrowserTab> _tabs = new();
    private int _activeTabIndex = -1;
    private bool _isDarkMode = true;
    private bool _copilotVisible = false;
    private double _aiSidebarWidth = 340;
    private bool _isFullscreen = false;
    // Rapid-close: while mouse is over the tab strip, don't resize tabs (Chrome behaviour)
    private bool _tabStripMouseOver = false;
    private bool _tabsClosedWhileOver = false;
    private bool _suppressTabWidthUpdate = false;
    private System.Windows.Threading.DispatcherTimer? _tabCloseResizeTimer;
    private readonly HashSet<string> _adBlockLoggedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _installedBrowserExtensionProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<WebView2> _loadingTabs = new();
    private bool _refreshSpinnerActive = false;
    private readonly string _userDataFolder;
    private readonly string _incognitoUserDataFolder;
    private readonly string _profilesIndexPath;
    private string _profileFolder = "";
    private string _profileNameKey = "";
    private Window? _extensionPopupWindow;
    private string _settingsPath = "";
    // When "Start fresh" is chosen, track which tab was the startup tab so its URL isn't
    // saved as a "last tab" (prevents the restore prompt appearing again next session).
    private BrowserTab? _freshStartTab = null;
    private string?     _freshStartTabInitialUrl = null;
    private string _historyPath = "";
    private string _downloadsPath = "";
    private string _bookmarksPath = "";
    private string _passwordsPath = "";
    private string _permissionsPath = "";
    private string _extensionsStatePath = "";
    private string _bitwardenCliAppDataDir = "";
    private string _sessionPath = "";
    private const string EmbeddedContentStamp = "2026-05-16-searchbar-icons-v23";
    private const string InternalHostName = "ycb.local";
    private const string InternalOrigin = "https://ycb.local/";
    private Settings _settings = new();
    private LauncherState _launcherState = new();
    private string _searchEngine = "google";
    private double _zoomFactor = 1.0;
    private readonly bool _isIncognito;
    private readonly List<ChatMessage> _chatHistory = new();
    private static readonly bool _installAiEnabled =
        (Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\YCB", "AIOption", "on") as string ?? "on") != "off";
    private Process? _copilotProcess;
    private string? _bitwardenSession;
    private TextBlock? _currentResponseBlock;
    private string? _startupUrl;
    private readonly Dictionary<WebView2, string> _autofillShownForTab = new();
    private readonly Dictionary<WebView2, DateTime> _navStartTimes = new();
    private CoreWebView2Environment? _webViewEnvironment;
    private CoreWebView2Environment? _incognitoWebViewEnvironment;
    private bool _aiWebViewReady = false;
    private string? _attachedImagePath;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;
    private WindowState _savedWindowState;
    private bool _manuallyMaximized = false;
    private DateTime _historyClearedAt = DateTime.MinValue;
    private CancellationTokenSource? _suggestCts;
    private System.Windows.Threading.DispatcherTimer? _suggestCloseTimer;
    private bool _userEditingUrl = false;
    private readonly Dictionary<WebView2, string> _lastRealPageUrl = new(); // for learning
    private readonly bool _skipProfileChooser;
    private readonly bool _startEmpty;

    private sealed class CookieImportSourceProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Browser { get; set; } = "";
        public string RootPath { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsDefault { get; set; }
    }
    
    public MainWindow() : this(false, null, null, false, false) { }

    public MainWindow(bool incognito) : this(incognito, null, null, false, false) { }

    public MainWindow(string? startupUrl) : this(false, startupUrl, null, false, false) { }

    public MainWindow(bool incognito, string? startupUrl = null, string? profileName = null)
        : this(incognito, startupUrl, profileName, false, false)
    {
    }

    public MainWindow(bool incognito, string? startupUrl = null, string? profileName = null, bool skipProfileChooser = false, bool startEmpty = false)
    {
        InitializeComponent();
        _isIncognito = incognito;
        _startupUrl  = startupUrl;
        _skipProfileChooser = skipProfileChooser;
        _startEmpty = startEmpty;
        
        _userDataFolder = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YCB-Browser");
        _incognitoUserDataFolder = IoPath.Combine(IoPath.GetTempPath(), "YCB-Incognito-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDataFolder);
        EnsureRendererExtracted();
        EnsureUBlockOriginExtracted();
        _profilesIndexPath = IoPath.Combine(_userDataFolder, "profiles.json");
        LoadLauncherState();
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            _launcherState.ActiveProfileName = profileName.Trim();
        }
        ConfigureProfileStorage(_launcherState.ActiveProfileName);
        LoadSettings();
        PruneCurrentProfileWebViewCaches();
        if (_isIncognito)
        {
            Directory.CreateDirectory(_incognitoUserDataFolder);
        }
        
        Loaded += MainWindow_Loaded;
        LocationChanged += (_, _) => { if (!_isFullscreen && Top < 0) Top = 0; };
        StateChanged += MainWindow_StateChanged;
        KeyDown += MainWindow_KeyDown;
        SizeChanged += (_, _) => QueueTabWidthUpdate();
        // Track mouse over the ENTIRE tab strip — only resize on leave if tabs were actually closed
        Loaded += (_, _) =>
        {
            TabStrip.MouseEnter += (s, e) => _tabStripMouseOver = true;
            TabStrip.MouseLeave += (s, e) =>
            {
                _tabStripMouseOver = false;
                if (_tabsClosedWhileOver)
                {
                    _tabsClosedWhileOver = false;
                    QueueTabWidthUpdate();
                }
            };
        };
        if (_isIncognito)
        {
            Closed += IncognitoWindow_Closed;
        }
    }
    
    // Returns the renderer folder path (in AppData, extracted from embedded resources)
    private string RendererPath => IoPath.Combine(_userDataFolder, "renderer");
    private string BrowserExtensionsPath => IoPath.Combine(_userDataFolder, "browser_extensions");
    private string UBlockExtensionPath => IoPath.Combine(BrowserExtensionsPath, "uBlock0.chromium");

    private void EnsureRendererExtracted()
    {
        ExtractEmbeddedFolder("renderer/", RendererPath, force: false);
    }

    private void EnsureUBlockOriginExtracted()
    {
        ExtractEmbeddedFolder("ublock/", BrowserExtensionsPath, force: false);
    }

    private void ExtractEmbeddedFolder(string resourcePrefix, string outDir, bool force)
    {
        Directory.CreateDirectory(outDir);
        var stampPath = IoPath.Combine(outDir, ".embedded_stamp");
        if (!force)
        {
            try
            {
                if (File.Exists(stampPath) &&
                    string.Equals(File.ReadAllText(stampPath).Trim(), EmbeddedContentStamp, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch { }
        }

        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(resourcePrefix, StringComparison.Ordinal)) continue;
            // name like "renderer/newtab.html" or "ublock/uBlock0.chromium/manifest.json"
            var relative = name.Substring(resourcePrefix.Length).Replace('/', IoPath.DirectorySeparatorChar);
            var destPath = IoPath.Combine(outDir, relative);
            Directory.CreateDirectory(IoPath.GetDirectoryName(destPath)!);
            using var src = asm.GetManifestResourceStream(name)!;
            // Always overwrite so updates ship on next run
            using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write);
            src.CopyTo(dst);
        }

        try
        {
            File.WriteAllText(stampPath, EmbeddedContentStamp, Encoding.UTF8);
        }
        catch { }
    }

    private void IncognitoWindow_Closed(object? sender, EventArgs e)
    {
        // Clean up incognito temp data
        try
        {
            if (Directory.Exists(_incognitoUserDataFolder))
            {
                Directory.Delete(_incognitoUserDataFolder, true);
            }
        }
        catch { }
    }
    
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var showProfileChooser = !_isIncognito && !_skipProfileChooser && GetSavedProfiles().Count > 1;
        var startEmpty = _startEmpty;
        var hasStartupUrl = !string.IsNullOrWhiteSpace(_startupUrl);

        // Setup incognito mode
        if (_isIncognito)
        {
            Title = "YCB (Incognito)";
            IncognitoPill.Visibility = Visibility.Visible;
        }
        else
        {
            Title = $"YCB - {(_settings.ProfileName ?? Environment.UserName).Trim()}";
        }
        
        ApplyTheme();
        ApplyAllSettings();
        ApplyWindowPositionFromSettings();

        // Hide all AI UI if AI was disabled during install
        ApplyAiVisibility();

        // Profile picker should win on startup when multiple profiles exist.
        if (showProfileChooser)
        {
            Hide();
            var picked = await ShowProfileChooserAsync(switchOnSelect: false);
            if (!picked)
            {
                Close();
                return;
            }
            Show();
            Activate();

            if (hasStartupUrl && _tabs.Count == 0)
            {
                await CreateTab(_startupUrl!);
            }
            else if (!_isIncognito && !startEmpty && _tabs.Count == 0)
            {
                if (_settings.StartupMode == "continue" && _settings.LastTabs?.Count > 0)
                {
                    var tabsToRestore = _settings.LastTabs.ToList();
                    foreach (var url in tabsToRestore)
                        await CreateTab(url);
                    _settings.LastTabs = null;
                    SaveSettings();
                }
                else if (_settings.StartupMode == "ask" && _settings.LastTabs?.Count > 0)
                {
                    await CreateTab(_settings.HomePage ?? "ycb://newtab");
                    await Task.Delay(300);
                    ShowRestorePrompt();
                }
                else
                {
                    await CreateTab(_settings.HomePage ?? "ycb://newtab");
                }
            }
        }
        // Restore tabs from last session only when requested.
        else if (hasStartupUrl)
        {
            await CreateTab(_startupUrl!);
        }
        else if (!_isIncognito && _settings.StartupMode == "continue" && _settings.LastTabs?.Count > 0)
        {
            var tabsToRestore = _settings.LastTabs.ToList();
            foreach (var url in tabsToRestore)
                await CreateTab(url);
            _settings.LastTabs = null;
        }
        else if (!_isIncognito && _settings.StartupMode == "ask" && _settings.LastTabs?.Count > 0)
        {
            await CreateTab(_settings.HomePage ?? "ycb://newtab");
            await Task.Delay(300);
            ShowRestorePrompt();
        }
        else if (!startEmpty)
        {
            await CreateTab(_settings.HomePage ?? "ycb://newtab");
        }

        // Show guide on first launch
        if (!_isIncognito && !_settings.HasSeenGuide && !showProfileChooser && !startEmpty && !hasStartupUrl)
        {
            await CreateTab("ycb://guide");
            _settings.HasSeenGuide = true;
            SaveSettings();
        }
    }

    // Called by App.xaml.cs pipe server when another instance sends a URL
    public async void OpenUrl(string url)
    {
        // Normalize bare URLs (e.g. "google.com" → "https://google.com")
        if (!string.IsNullOrWhiteSpace(url) &&
            !url.StartsWith("ycb://") &&
            !url.StartsWith("http://") &&
            !url.StartsWith("https://") &&
            !url.StartsWith("file://") &&
            !url.StartsWith("about:"))
        {
            url = "https://" + url;
        }

        BringToFront();
        try { await CreateTab(url); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenUrl] Failed to open '{url}': {ex.Message}");
        }
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
    
    private void ApplyAllSettings()
    {
        // Apply bookmarks bar visibility
        if (_settings.BookmarksBarVisible)
        {
            BookmarksBar.Visibility = Visibility.Visible;
            BookmarksBarRow.Height = new GridLength(32);
            LoadBookmarksBar();
        }
    }
    
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // System maximize (Aero snap, Win+Up) — cancel it and use manual maximize
            WindowState = WindowState.Normal;
            ManualMaximize();
        }
        else if (!_isFullscreen && !_manuallyMaximized)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness   = new Thickness(-1),
                CornerRadius          = new CornerRadius(0)
            });
            ShowMaximizeIcon();
        }

        ApplyWindowMemoryTarget();
    }

    private void ApplyWindowMemoryTarget()
    {
        var minimized = WindowState == WindowState.Minimized;
        for (int i = 0; i < _tabs.Count; i++)
        {
            try
            {
                var core = _tabs[i].WebView.CoreWebView2;
                if (core == null) continue;
                SetWebViewMemoryTarget(core, minimized || i != _activeTabIndex ? "Low" : "Normal");
            }
            catch { }
        }
    }

    private void QueueActiveTabMemoryTrim(WebView2 webView)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(8000);
            try
            {
                if (webView.CoreWebView2 == null) return;
                if (GetTabIndexForWebView(webView) != _activeTabIndex) return;
                if (_loadingTabs.Contains(webView)) return;
                SetWebViewMemoryTarget(webView.CoreWebView2, "Low");
            }
            catch { }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowRestoreIcon()
    {
        var iconColor = _isDarkMode ? "#9aa0a6" : "#5f6368";
        var canvas = new Canvas { Width = 10, Height = 10 };
        var rect1 = new Rectangle { Width = 7, Height = 7, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!), StrokeThickness = 1.2, Fill = Brushes.Transparent };
        var rect2 = new Rectangle { Width = 7, Height = 7, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!), StrokeThickness = 1.2, Fill = Brushes.Transparent };
        Canvas.SetLeft(rect1, 0); Canvas.SetTop(rect1, 3);
        Canvas.SetLeft(rect2, 3); Canvas.SetTop(rect2, 0);
        canvas.Children.Add(rect1);
        canvas.Children.Add(rect2);
        MaxRestoreBtn.Content = canvas;
    }

    private void ShowMaximizeIcon()
    {
        var iconColor = _isDarkMode ? "#9aa0a6" : "#5f6368";
        MaxRestoreBtn.Content = new Rectangle
        {
            Width = 8.5, Height = 8.5,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent
        };
    }
    
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        // Keyboard shortcuts
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.N:
                    // Open new incognito window
                    OpenIncognitoWindow();
                    e.Handled = true;
                    break;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.T:
                    _ = CreateTab();
                    e.Handled = true;
                    break;
                case Key.N:
                    // Open new window
                    OpenNewWindow();
                    e.Handled = true;
                    break;
                case Key.W:
                    if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                        CloseTab(_activeTabIndex);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    if (_tabs.Count > 1)
                    {
                        var next = (_activeTabIndex + 1) % _tabs.Count;
                        SwitchToTab(next);
                    }
                    e.Handled = true;
                    break;
                case Key.L:
                    UrlBox.Focus();
                    UrlBox.SelectAll();
                    e.Handled = true;
                    break;
                case Key.D:
                    // Quick Download keyboard shortcut removed (feature is always-on)
                    break;
                case Key.H:
                    _ = CreateTab("ycb://history");
                    e.Handled = true;
                    break;
                case Key.J:
                    _ = CreateTab("ycb://downloads");
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    ZoomIn_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomOut_Click(sender, e);
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F5)
        {
            Refresh_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }
    
    private void OpenNewWindow()
    {
        ErrorReporter.Track("NewWin", new() { ["incognito"] = false });
        var newWindow = new MainWindow(false);
        newWindow.Show();
    }
    
    private void OpenIncognitoWindow()
    {
        ErrorReporter.Track("NewWin", new() { ["incognito"] = true });
        var incognitoWindow = new MainWindow(true);
        incognitoWindow.Show();
    }
    
    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        var hwnd = new WindowInteropHelper(this).Handle;

        if (_isFullscreen)
        {
            _savedWindowState = WindowState;
            _savedLeft = Left; _savedTop = Top; _savedWidth = Width; _savedHeight = Height;

            // Hide browser UI
            TabStrip.Visibility = Visibility.Collapsed;
            Toolbar.Visibility  = Visibility.Collapsed;
            MainGrid.RowDefinitions[0].Height = new GridLength(0);
            MainGrid.RowDefinitions[1].Height = new GridLength(0);

            // Capture exact monitor rect in physical pixels BEFORE style changes
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            int fsX = mi.rcMonitor.Left,  fsY = mi.rcMonitor.Top;
            int fsW = mi.rcMonitor.Right  - mi.rcMonitor.Left;
            int fsH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

            // Strip chrome and borders
            WindowChrome.SetWindowChrome(this, null);
            if (_manuallyMaximized) _manuallyMaximized = false;
            ResizeMode  = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                SetWindowPos(hwnd, HWND_TOPMOST, fsX, fsY, fsW, fsH, SWP_FRAMECHANGED);
            }));
        }
        else
        {
            // Show browser UI
            TabStrip.Visibility = Visibility.Visible;
            Toolbar.Visibility  = Visibility.Visible;
            MainGrid.RowDefinitions[0].Height = new GridLength(36);
            MainGrid.RowDefinitions[1].Height = new GridLength(46);

            // Restore chrome
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness   = new Thickness(-1),
                CornerRadius          = new CornerRadius(0)
            });
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode  = ResizeMode.CanResize;

            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_FRAMECHANGED | 0x0001 | 0x0002);

            if (_savedWindowState == WindowState.Maximized || _manuallyMaximized)
            {
                ManualMaximize();
            }
            else
            {
                Left = _savedLeft; Top = _savedTop;
                Width = _savedWidth; Height = _savedHeight;
            }
        }
    }
    
    private void LoadLauncherState()
    {
        try
        {
            if (File.Exists(_profilesIndexPath))
            {
                var json = File.ReadAllText(_profilesIndexPath);
                _launcherState = JsonSerializer.Deserialize<LauncherState>(json) ?? new LauncherState();
            }
        }
        catch { }

        _launcherState.Profiles ??= new List<ProfileItem>();
        if (_launcherState.Profiles.Count == 0)
        {
            _launcherState.Profiles.Add(new ProfileItem
            {
                Name = Environment.UserName,
                Initial = GetProfileInitial(Environment.UserName),
                Color = "#5b9bf9",
                Icon = ""
            });
        }

        if (string.IsNullOrWhiteSpace(_launcherState.ActiveProfileName))
        {
            _launcherState.ActiveProfileName = _launcherState.Profiles[0].Name;
        }
    }

    private void SaveLauncherState()
    {
        try
        {
            _launcherState.Profiles ??= new List<ProfileItem>();
            var json = JsonSerializer.Serialize(_launcherState, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_profilesIndexPath, json);
        }
        catch { }
    }

    private void ConfigureProfileStorage(string? profileName)
    {
        var resolved = string.IsNullOrWhiteSpace(profileName) ? Environment.UserName : profileName.Trim();
        _profileNameKey = resolved;
        var safe = MakeSafeProfileFolderName(resolved);
        _profileFolder = IoPath.Combine(_userDataFolder, "profiles", safe);
        Directory.CreateDirectory(_profileFolder);
        _webViewEnvironment = null;
        _installedBrowserExtensionProfiles.Clear();
        _settingsPath = IoPath.Combine(_profileFolder, "settings.json");
        _historyPath = IoPath.Combine(_profileFolder, "history.json");
        _downloadsPath = IoPath.Combine(_profileFolder, "downloads.json");
        _bookmarksPath = IoPath.Combine(_profileFolder, "bookmarks.json");
        _passwordsPath = IoPath.Combine(_profileFolder, "passwords.json");
        _permissionsPath = IoPath.Combine(_profileFolder, "permissions.json");
        _extensionsStatePath = IoPath.Combine(_profileFolder, "extensions-state.json");
        _bitwardenCliAppDataDir = IoPath.Combine(_profileFolder, "bitwarden-cli");
        _sessionPath = IoPath.Combine(_profileFolder, "session.json");
        _settings.ProfileName = resolved;
        _settings.ProfileInitial = GetProfileInitial(resolved);
        if (string.IsNullOrWhiteSpace(_settings.ProfileColor)) _settings.ProfileColor = "#5b9bf9";
        if (string.IsNullOrWhiteSpace(_settings.ProfileIcon)) _settings.ProfileIcon = "";
    }

    private void PruneCurrentProfileWebViewCaches()
    {
        if (_isIncognito || string.IsNullOrWhiteSpace(_profileFolder)) return;

        var defaultProfile = IoPath.Combine(_profileFolder, "Default");
        var cacheFolders = new[]
        {
            "Cache",
            "Code Cache",
            "GPUCache",
            "DawnCache",
            "ShaderCache",
            "GrShaderCache",
            IoPath.Combine("Service Worker", "CacheStorage"),
            IoPath.Combine("Service Worker", "ScriptCache")
        };

        foreach (var relative in cacheFolders)
        {
            SafeDeleteDirectory(IoPath.Combine(defaultProfile, relative), _profileFolder);
        }

        SafeDeleteDirectory(IoPath.Combine(_profileFolder, "Crashpad", "reports"), _profileFolder);
        SafeDeleteDirectory(IoPath.Combine(_profileFolder, "Crashpad", "attachments"), _profileFolder);
    }

    private static void SafeDeleteDirectory(string path, string requiredParent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(requiredParent)) return;
            if (!Directory.Exists(path)) return;

            var fullPath = IoPath.GetFullPath(path);
            var fullParent = IoPath.GetFullPath(requiredParent);
            if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase)) return;

            Directory.Delete(fullPath, recursive: true);
        }
        catch (Exception ex)
        {
            App.WriteTrace($"Cache prune skipped {path}: {ex.Message}");
        }
    }

    private static string MakeSafeProfileFolderName(string name)
    {
        var cleaned = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Profile";
        cleaned = cleaned.Replace(' ', '_');
        return cleaned;
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch { }

        _settings.AiProvider = NormalizeAiProvider(_settings.AiProvider);
        if (string.IsNullOrWhiteSpace(_settings.AiProvider))
        {
            _settings.AiProvider = "chatgpt";
        }
        if (_settings.AiSidebarWidth.HasValue && _settings.AiSidebarWidth.Value > 0)
        {
            _aiSidebarWidth = Math.Max(240, Math.Min(_settings.AiSidebarWidth.Value, 720));
        }
        if (string.IsNullOrWhiteSpace(_settings.ProfileName))
            _settings.ProfileName = _profileNameKey;
        if (string.IsNullOrWhiteSpace(_settings.ProfileInitial))
            _settings.ProfileInitial = GetProfileInitial(_settings.ProfileName);
        if (string.IsNullOrWhiteSpace(_settings.ProfileIcon))
            _settings.ProfileIcon = "";
        if (string.IsNullOrWhiteSpace(_settings.ProfileColor))
            _settings.ProfileColor = "#5b9bf9";
        ApplySavedProfileDefaults();
        
        _isDarkMode = _settings.DarkMode;
        _searchEngine = _settings.SearchEngine ?? "google";
        ErrorReporter.IsEnabled = _settings.TelemetryEnabled;
        _historyClearedAt = _settings.HistoryClearedAt ?? DateTime.MinValue;
        UpdateAdBlockButton();
    }
    
    private void SaveSettings()
    {
        try
        {
            _settings.DarkMode = _isDarkMode;
            // Only save actual websites — no ycb:// internal pages.
            // Also exclude the startup tab if "Start fresh" was chosen and the user hasn't
            // navigated it away from its original URL (avoids a re-prompt next session).
            _settings.LastTabs = _tabs
                .Where(t => !string.IsNullOrEmpty(t.Url) &&
                            (t.Url!.StartsWith("http://") || t.Url.StartsWith("https://")) &&
                            !(t == _freshStartTab && t.Url == _freshStartTabInitialUrl))
                .Select(t => t.Url!)
                .ToList();
            // Save window bounds/state (use RestoreBounds to get normal geometry)
            try
            {
                bool isMax = _manuallyMaximized || WindowState == WindowState.Maximized;
                Rect bounds = isMax && _savedWidth > 0
                    ? new Rect(_savedLeft, _savedTop, _savedWidth, _savedHeight)
                    : RestoreBounds;
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    _settings.WindowLeft   = bounds.Left;
                    _settings.WindowTop    = bounds.Top;
                    _settings.WindowWidth  = bounds.Width;
                    _settings.WindowHeight = bounds.Height;
                }
                _settings.WindowState = isMax ? "Maximized" : "Normal";
            }
            catch { }
            _settings.AiSidebarWidth = _copilotVisible ? _aiSidebarWidth : (_settings.AiSidebarWidth ?? _aiSidebarWidth);
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    private void ApplyWindowPositionFromSettings()
    {
        try
        {
            if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
            {
                var work = SystemParameters.WorkArea;
                double w = _settings.WindowWidth.Value;
                double h = _settings.WindowHeight.Value;
                double left = _settings.WindowLeft ?? (work.Left + (work.Width - w) / 2);
                double top = _settings.WindowTop ?? (work.Top + (work.Height - h) / 2);
                // Clamp to work area
                if (left + w > work.Right) left = work.Right - w;
                if (top + h > work.Bottom) top = work.Bottom - h;
                if (left < work.Left) left = work.Left;
                if (top < work.Top) top = work.Top;
                Width = Math.Max(300, Math.Min(w, work.Width));
                Height = Math.Max(200, Math.Min(h, work.Height));
                Left = left;
                Top = top;
            }
            if (_settings.WindowState == "Maximized")
            {
                Dispatcher.InvokeAsync(() => ManualMaximize(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        catch { }
    }

    private bool IsRestorableUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.StartsWith("http://") || url.StartsWith("https://")) return true;
        if (url.StartsWith("ycb://"))
        {
            var page = url.Substring("ycb://".Length).Split(new[] {'/', '?'}, StringSplitOptions.RemoveEmptyEntries)[0];
            var disallowed = new[] { "settings", "guide", "passwords" };
            return !disallowed.Contains(page);
        }
        return false;
    }

    private async System.Threading.Tasks.Task CreateTab(string url = "ycb://newtab")
    {
        if (url.StartsWith("ycb://profiles", StringComparison.OrdinalIgnoreCase))
        {
            if (GetSavedProfiles().Count > 1 || string.IsNullOrWhiteSpace(_settings.ProfileName))
            {
                await ShowProfileChooserAsync(switchOnSelect: true);
            }
            return;
        }

        // Handle internal URLs
        var displayUrl = url;
        if (url.StartsWith("ycb://"))
        {
            displayUrl = url;
        }
        
        var webView = new WebView2();
        webView.Visibility = Visibility.Collapsed;
        
        // Create tab button with Chrome-like structure
        var tabButton = CreateTabButton(_tabs.Count);

        // Extract tab UI element references for compact-mode management
        Image? tabFavicon = null;
        Button? tabCloseBtn = null;
        TextBlock? tabTitle = null;
        if (tabButton.Content is Grid _contentGrid)
        {
            tabFavicon = _contentGrid.Children.OfType<Image>().FirstOrDefault();
            tabCloseBtn = _contentGrid.Children.OfType<Button>().FirstOrDefault();
            tabTitle = _contentGrid.Children.OfType<TextBlock>().FirstOrDefault();
        }

        TabsPanel.Children.Add(tabButton);
        WebViewContainer.Children.Add(webView);
        
        // Recompute tab widths now that count changed
        Dispatcher.InvokeAsync(UpdateTabWidths, System.Windows.Threading.DispatcherPriority.Loaded);
        
        var tab = new BrowserTab
        {
            WebView = webView,
            TabButton = tabButton,
            Url = url,
            Title = "New Tab",
            TabFavicon = tabFavicon,
            TabCloseBtn = tabCloseBtn,
            TabTitle = tabTitle,
        };
        _tabs.Add(tab);
        ErrorReporter.Track("TabOpen", new() { ["tabs"] = _tabs.Count });
        
        // Initialize WebView2 with a profile-specific data folder so cookies, history,
        // extensions and session state stay attached to the active YCB profile.
        var dataFolder = _isIncognito ? _incognitoUserDataFolder : _profileFolder;
        if (_isIncognito)
        {
            _incognitoWebViewEnvironment ??= await CreateWebViewEnvironment(dataFolder);
            await webView.EnsureCoreWebView2Async(_incognitoWebViewEnvironment);
        }
        else
        {
            _webViewEnvironment ??= await CreateWebViewEnvironment(dataFolder);
            await webView.EnsureCoreWebView2Async(_webViewEnvironment);
        }
        
        // Set default background color based on theme
        webView.DefaultBackgroundColor = _isDarkMode 
            ? System.Drawing.Color.FromArgb(255, 32, 33, 36)  // #202124
            : System.Drawing.Color.FromArgb(255, 255, 255, 255);  // white

        // Make sure internal pages can send messages back to the host.
        if (webView.CoreWebView2?.Settings != null)
        {
            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
        }

        await EnsureUBlockOriginInstalledAsync(webView, dataFolder);
        
        // Setup event handlers
        SetupWebViewEvents(webView, _tabs.Count - 1);
        
        // Navigate to URL
        if (url.StartsWith("ycb://"))
        {
            NavigateToInternalPage(webView, url);
        }
        else
        {
            webView.CoreWebView2.Navigate(url);
        }
        
        // Switch to new tab
        SwitchToTab(_tabs.Count - 1);
        
        // Focus URL bar for new tabs
        if (url == "https://www.google.com" || url.StartsWith("ycb://newtab"))
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
        }
    }

    private void UpdateTabWidths()
    {
        if (_tabs.Count == 0) return;
        // WinControls = 3 × 46px = 138px (fixed by WinBtnStyle).
        // NewTabBtn = 26px + 4px margin = 30px. DragArea minimum = 60px.
        // TabStrip spans the full window width reliably once rendered.
        const double kWinCtrl  = 138; // always fixed
        const double kNewTab   = 30;  // always fixed
        const double kDragMin  = 60;
        double stripW   = TabStrip.ActualWidth > 0 ? TabStrip.ActualWidth : ActualWidth;
        double available = Math.Max(0, stripW - kWinCtrl - kNewTab - kDragMin);
        // Hard cap the scroller width so tabs can never push the + button or window controls away
        TabsScroller.MaxWidth = Math.Max(20, available);
        // Allow tabs to shrink down to 20px (favicon-only, like Chrome)
        double tabWidth = Math.Min(220, Math.Max(20, available / _tabs.Count));

        // Three tiers matching Chrome behaviour:
        //   tiny    : favicon only; the active tab swaps to centered X while hovered
        //   compact : favicon + X, no title
        //   normal  : favicon + title + X
        bool tiny = tabWidth <= 36;
        bool compact  = !tiny && tabWidth <= 80;

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            tab.TabButton.Width = tabWidth;
            bool isActive = i == _activeTabIndex;

            if (tab.TabFavicon != null && tab.TabCloseBtn != null && tab.TabTitle != null)
            {
                if (tiny)
                {
                    bool showTinyClose = isActive && tab.TabButton.IsMouseOver;
                    tab.TabButton.Padding         = new Thickness(0);
                    tab.TabButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    tab.TabTitle.Visibility       = Visibility.Collapsed;
                    Grid.SetColumn(tab.TabFavicon, 0);
                    Grid.SetColumnSpan(tab.TabFavicon, 4);
                    Grid.SetColumn(tab.TabCloseBtn, 0);
                    Grid.SetColumnSpan(tab.TabCloseBtn, 4);
                    if (showTinyClose)
                    {
                        tab.TabFavicon.Visibility  = Visibility.Collapsed;
                        tab.TabCloseBtn.Visibility = Visibility.Visible;
                        tab.TabCloseBtn.Opacity    = 1;
                        tab.TabCloseBtn.HorizontalAlignment = HorizontalAlignment.Center;
                        tab.TabCloseBtn.VerticalAlignment = VerticalAlignment.Center;
                        tab.TabCloseBtn.Margin = new Thickness(0);
                    }
                    else
                    {
                        tab.TabFavicon.Visibility  = Visibility.Visible;
                        tab.TabFavicon.HorizontalAlignment = HorizontalAlignment.Center;
                        tab.TabFavicon.VerticalAlignment = VerticalAlignment.Center;
                        tab.TabFavicon.Margin = new Thickness(0);
                        tab.TabCloseBtn.Visibility = Visibility.Collapsed;
                        tab.TabCloseBtn.Opacity    = 0;
                    }
                }
                else if (compact)
                {
                    Grid.SetColumn(tab.TabFavicon, 0);
                    Grid.SetColumnSpan(tab.TabFavicon, 1);
                    Grid.SetColumn(tab.TabCloseBtn, 3);
                    Grid.SetColumnSpan(tab.TabCloseBtn, 1);
                    tab.TabButton.Padding         = new Thickness(5, 0, 3, 0);
                    tab.TabButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    tab.TabTitle.Visibility       = Visibility.Collapsed;
                    tab.TabCloseBtn.Visibility    = Visibility.Visible;
                    tab.TabCloseBtn.Opacity       = 1;
                    tab.TabFavicon.Visibility     = Visibility.Visible;
                    tab.TabFavicon.HorizontalAlignment = HorizontalAlignment.Left;
                    tab.TabFavicon.Margin         = new Thickness(0, 0, 4, 0);
                    tab.TabCloseBtn.Margin        = new Thickness(0);
                    tab.TabCloseBtn.HorizontalAlignment = HorizontalAlignment.Right;
                }
                else
                {
                    Grid.SetColumn(tab.TabFavicon, 0);
                    Grid.SetColumnSpan(tab.TabFavicon, 1);
                    Grid.SetColumn(tab.TabCloseBtn, 3);
                    Grid.SetColumnSpan(tab.TabCloseBtn, 1);
                    // Normal: full padding, favicon + title + X on every tab.
                    tab.TabButton.Padding         = new Thickness(10, 0, 8, 0);
                    tab.TabButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    tab.TabFavicon.Visibility     = Visibility.Visible;
                    tab.TabFavicon.HorizontalAlignment = HorizontalAlignment.Left;
                    tab.TabFavicon.Margin         = new Thickness(0, 0, 6, 0);
                    tab.TabTitle.Visibility       = Visibility.Visible;
                    tab.TabCloseBtn.Visibility    = Visibility.Visible;
                    tab.TabCloseBtn.Opacity       = 1;
                    tab.TabCloseBtn.Margin        = new Thickness(4, 0, 0, 0);
                    tab.TabCloseBtn.HorizontalAlignment = HorizontalAlignment.Right;
                }
            }
        }
    }

    private Button CreateTabButton(int index)
    {
        var button = new Button
        {
            Style = (Style)FindResource("TabStyle"),
            Tag = index
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Audio icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        // Favicon
        var favicon = new Image
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(favicon, 0);
        grid.Children.Add(favicon);
        
        // Audio icon (speaker) - hidden by default
        var audioIcon = new Canvas
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 4, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Tab is playing audio"
        };
        // Speaker icon paths
        var speakerBody = new WpfPath
        {
            Data = Geometry.Parse("M3 9 L3 15 L7 15 L11 18 L11 6 L7 9 Z"),
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            Stretch = Stretch.Uniform,
            Width = 10,
            Height = 10
        };
        Canvas.SetLeft(speakerBody, 0);
        Canvas.SetTop(speakerBody, 2);
        audioIcon.Children.Add(speakerBody);
        var soundWave = new WpfPath
        {
            Data = Geometry.Parse("M13 8 Q15 12 13 16"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            StrokeThickness = 1.2,
            Fill = Brushes.Transparent,
            Width = 6,
            Height = 10
        };
        Canvas.SetLeft(soundWave, 8);
        Canvas.SetTop(soundWave, 2);
        audioIcon.Children.Add(soundWave);
        Grid.SetColumn(audioIcon, 1);
        grid.Children.Add(audioIcon);
        
        // Title
        var title = new TextBlock
        {
            Text = "New Tab",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#202124")!),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);
        
        // Close button
        var closeBtn = new Button
        {
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = index,
            Opacity = 1,
            Style = (Style)FindResource("TabCloseBtnStyle")
        };
        closeBtn.Content = new WpfPath
        {
            Data = Geometry.Parse("M1 1l8 8M9 1l-8 8"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
            StrokeThickness = 1.5,
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform
        };
        closeBtn.Click += CloseTab_Click;
        Grid.SetColumn(closeBtn, 3);
        grid.Children.Add(closeBtn);
        
        button.Content = grid;
        
        // Tiny tabs swap favicon <-> centered X only while hovering the active tab.
        button.MouseEnter += (s, e) => UpdateTabWidths();
        button.MouseLeave += (s, e) => UpdateTabWidths();
        
        button.Click += (s, e) =>
        {
            if (s is Button btn && btn.Tag is int idx)
            {
                SwitchToTab(idx);
            }
        };
        
        // Middle click to close
        button.MouseDown += (s, e) =>
        {
            if (e.MiddleButton == MouseButtonState.Pressed && s is Button btn && btn.Tag is int idx)
            {
                CloseTab(idx);
            }
        };
        
        return button;
    }
    
    private bool IsActiveTab(int index) => _activeTabIndex == index;
    
    private void SetupWebViewEvents(WebView2 webView, int tabIndex)
    {
        // uBlock Origin is the primary blocker. Avoid attaching the large host-level
        // WebResourceRequested fallback on every tab because it adds memory/CPU overhead.

        // Permission handling for camera, microphone, screen capture
        webView.CoreWebView2.PermissionRequested += (s, e) =>
        {
            var uri2 = new Uri(e.Uri);
            var origin2 = uri2.Host;
            var permName2 = GetPermissionName(e.PermissionKind);
            var saved = LoadSitePermissions();
            if (saved.TryGetValue(origin2, out var domainPerms) && domainPerms.TryGetValue(permName2, out var savedState))
            {
                e.State = savedState == "allow" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                return; // already decided — don't ask again
            }
            ShowPermissionDialog(webView, e);
        };
        
        webView.GotFocus += (s, e) => SuggestPopup.IsOpen = false;
        try
        {
            webView.PreviewMouseDown += (s, e) => SuggestPopup.IsOpen = false;
            webView.MouseDown += (s, e) => SuggestPopup.IsOpen = false;
        }
        catch { /* ignore if host doesn't surface these events */ }

        webView.NavigationStarting += (s, e) =>
        {
            var startingIdx = GetTabIndexForWebView(webView);
            if (startingIdx >= 0 && startingIdx < _tabs.Count)
            {
                _tabs[startingIdx].IsSuspended = false;
                var startingTitle = GetLoadingTabTitle(e.Uri);
                _tabs[startingIdx].Title = startingTitle;
                UpdateTabTitle(startingIdx, startingTitle);
            }
            SuggestPopup.IsOpen = false;
            _navStartTimes[webView] = DateTime.UtcNow;
            _autofillShownForTab.Remove(webView);
            _loadingTabs.Add(webView);
            if (!string.IsNullOrEmpty(e.Uri) && e.Uri.StartsWith("ycb://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                _ = Dispatcher.InvokeAsync(() => NavigateToInternalPage(webView, e.Uri));
                return;
            }
            // Reset bar state for the active tab
            var navIdx = GetTabIndexForWebView(webView);
            // bar state reset handled by JS on next navigation
            var idx = GetTabIndexForWebView(webView);
            if (idx == _activeTabIndex)
            {
                UrlBox.Text = GetDisplayUrl(e.Uri);
                UpdateUrlPlaceholder();
                UpdateSecurityIcon(e.Uri);
                UpdateRefreshButton();
                UpdateUrlFaviconForActiveTab(clearIfMissing: true);
            }

            // Silent login: intercept the support site login page,
            // cancel navigation, POST user ID, inject cookie, navigate to /support.
            if (!string.IsNullOrEmpty(e.Uri) &&
                e.Uri.Contains("ycb.tomcreations.org") &&
                (e.Uri.Contains("/auth/ycbuseridlogin") || e.Uri.Contains("/auth/login")))
            {
                // Don't cancel — let the page load so JS runs in same-origin context
            }
        };
        
        webView.NavigationCompleted += (s, e) =>
        {
            _loadingTabs.Remove(webView);
            QueueInactiveTabTrim();
            QueueActiveTabMemoryTrim(webView);
            if (GetTabIndexForWebView(webView) == _activeTabIndex)
                Dispatcher.InvokeAsync(UpdateRefreshButton);
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
            {
                
                // Apply zoom
                webView.ZoomFactor = _zoomFactor;

                // Sync bookmark star for the active tab
                if (idx == _activeTabIndex)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        UrlBox.Text = GetDisplayUrl(webView.Source?.ToString());
                        UpdateUrlPlaceholder();
                        TryRefreshActiveUrlFavicon();
                        RefreshBookmarkStar();
                    });
                }

                // Inject password detection script on real websites
                if (e.IsSuccess)
                {
                    var src2 = webView.Source?.ToString() ?? "";
                    if (!src2.StartsWith("file:///") && !IsInternalVirtualUrl(src2) && !string.IsNullOrEmpty(src2))
                    {
                        _ = webView.ExecuteScriptAsync(BitwardenSiteScript);
                        _ = PushBitwardenMatchesAsync(webView, src2);

                        // Silent login: if on the YCB support login page, POST user ID via JS
                        // so the browser handles cookies natively (same-origin fetch)
                        if (src2.Contains("ycb.tomcreations.org") &&
                            (src2.Contains("/auth/login") || src2.Contains("/auth/ycbuseridlogin")))
                        {
                            _ = TrySilentSupportLogin(webView);
                        }
                        // Track nav success — host only, no full URL
                        var navHost = GetDomain(src2);
                        var navMs   = _navStartTimes.TryGetValue(webView, out var t0)
                                        ? (int)(DateTime.UtcNow - t0).TotalMilliseconds : -1;
                        _navStartTimes.Remove(webView);
                        ErrorReporter.Track("NavOk", new() { ["host"] = navHost, ["ms"] = navMs });

                        // Track last real page URL for download learning
                        if (!src2.StartsWith("about:") && !src2.StartsWith("ycb://") && !IsInternalVirtualUrl(src2))
                            _lastRealPageUrl[webView] = src2;
                    }
                    else
                    {
                        _navStartTimes.Remove(webView);
                    }
                }
                else
                {
                    // Silently report navigation failures — no dialog shown to user
                    var failUrl  = webView.Source?.ToString();
                    var errCode  = (int)e.WebErrorStatus;
                    var errName  = e.WebErrorStatus.ToString();
                    _navStartTimes.Remove(webView);
                    App.WriteTrace($"[NAV ERROR] {errName} ({errCode}) @ {failUrl}");
                    ErrorReporter.Report(
                        errorType: "NavigationError",
                        message:   $"Navigation failed: {errName}",
                        pageUrl:   failUrl,
                        errorCode: errCode);
                    if (!string.IsNullOrWhiteSpace(failUrl) &&
                        (failUrl.StartsWith("http://") || failUrl.StartsWith("https://")) &&
                        ShouldShowNavigationError(errName, errCode, failUrl))
                    {
                        var title = "This site can’t be reached";
                        var detail = GetFriendlyNavigationError(errName, errCode, failUrl);
                        _ = Dispatcher.InvokeAsync(() => webView.CoreWebView2?.NavigateToString(
                            BuildNavigationErrorPage(title, detail, failUrl)));
                    }
                }
            }
        };
        
        // Handle quickdownload:open messages from injected bars on regular pages
        webView.CoreWebView2.WebMessageReceived += async (s, msgArgs) =>
        {
            try
            {
                var msgDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(msgArgs.WebMessageAsJson);
                if (msgDict == null) return;
                if (!msgDict.TryGetValue("type", out var typeEl)) return;
                var msgType = typeEl.GetString() ?? "";
                if (msgType == "quickdownload:open")
                {
                    if (!msgDict.TryGetValue("url", out var urlEl)) return;
                    var dlUrl = urlEl.GetString();
                    if (string.IsNullOrEmpty(dlUrl)) return;
                    // Trigger the download via a hidden <a> click so the user stays on the search page
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                        {
                            var activeWv = _tabs[_activeTabIndex].WebView;
                            var safe = dlUrl.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "");
                            await activeWv.ExecuteScriptAsync(
                                $"(function(){{var a=document.createElement('a');a.href='{safe}';a.download='';document.body.appendChild(a);a.click();document.body.removeChild(a);}})();");
                        }
                    });
                    return;
                }

                if (msgType == "bitwarden:saveCandidate")
                {
                    var kind = msgDict.TryGetValue("kind", out var kindEl) ? kindEl.GetString() ?? "password" : "password";
                    var url = msgDict.TryGetValue("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                    var itemEl = msgDict.TryGetValue("item", out var payloadEl) ? payloadEl : default;
                    if (itemEl.ValueKind == JsonValueKind.Object)
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(itemEl.GetRawText()) ?? new Dictionary<string, string>();
                        if (!await BitwardenAlreadyExistsAsync(kind, url, data))
                        {
                            await Dispatcher.InvokeAsync(() =>
                                ShowBitwardenSavePrompt(kind, url, data));
                        }
                    }
                    return;
                }

                if (msgType == "adblock:whitelist")
                {
                    var url = msgDict.TryGetValue("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var allowUri))
                    {
                        SaveSitePermission(allowUri.Host, "adblock", "allow");
                        if (webView.CoreWebView2 != null)
                        {
                            try
                            {
                                webView.CoreWebView2.Navigate(url);
                            }
                            catch { }
                        }
                    }
                    return;
                }
            }
            catch { /* not a quickdownload message */ }
        };
        webView.CoreWebView2.SourceChanged += (s, e) =>
        {
            var src = webView.CoreWebView2.Source ?? "";
            if (string.IsNullOrEmpty(src) || src.StartsWith("file:///") || src.StartsWith("about:")) return;
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
                _tabs[idx].Url = IsInternalVirtualUrl(src) ? VirtualInternalUrlToYcbUrl(src) : src;
            if (idx >= 0 && idx < _tabs.Count && string.IsNullOrWhiteSpace(_tabs[idx].Title))
            {
                _tabs[idx].Title = GetLoadingTabTitle(src);
                UpdateTabTitle(idx, _tabs[idx].Title);
            }
        };
        
        webView.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
            {
                var title = webView.CoreWebView2.DocumentTitle;
                if (string.IsNullOrWhiteSpace(title))
                    title = GetLoadingTabTitle(webView.Source?.ToString(), useHostFallback: false);
                title = NormalizeTabTitle(title, webView.Source?.ToString());
                _tabs[idx].Title = title;
                UpdateTabTitle(idx, title);
                AddToHistory(webView.Source?.ToString(), title);
            }
        };
        
        webView.CoreWebView2.FaviconChanged += async (s, e) =>
        {
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
            {
                try
                {
                    var faviconUri = webView.CoreWebView2.FaviconUri;
                    if (!string.IsNullOrEmpty(faviconUri))
                    {
                        await Dispatcher.InvokeAsync(() => UpdateTabFavicon(idx, faviconUri));
                    }
                }
                catch { }
            }
        };
        
        webView.CoreWebView2.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            _ = CreateTab(e.Uri);
        };
        
        webView.CoreWebView2.DownloadStarting += (s, e) =>
        {
            e.Handled = true;
            var dlExt0  = IoPath.GetExtension(e.ResultFilePath)?.TrimStart('.').ToLowerInvariant() ?? "";
            var dlKb0   = (int)((e.DownloadOperation.TotalBytesToReceive ?? 0) / 1024);
            ErrorReporter.Track("DlStart", new() { ["ext"] = dlExt0, ["kb"] = dlKb0 });
            var download = new DownloadItem
            {
                Url = e.DownloadOperation.Uri,
                Filename = IoPath.GetFileName(e.ResultFilePath),
                FilePath = e.ResultFilePath,
                SavePath = e.ResultFilePath,
                StartTime = DateTime.Now,
                Status = "Downloading",
                State = "downloading",
                TotalBytes = (long)(e.DownloadOperation.TotalBytesToReceive ?? 0)
            };
            ShowDownloadShelf(download);
            
            e.DownloadOperation.StateChanged += (sender, args) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                    {
                        download.Status = "Complete";
                        download.State = "completed";
                        download.CompletedAt = DateTime.Now;
                        download.TotalBytes = (long)(e.DownloadOperation.TotalBytesToReceive ?? 0);
                        UpdateDownloadItem(download);
                        SaveDownload(download);  // Save only when complete
                        var dlExt = IoPath.GetExtension(download.FilePath)?.TrimStart('.').ToLowerInvariant() ?? "";
                        var dlKb  = (int)(download.TotalBytes / 1024);
                        var dlDur = (int)(DateTime.Now - download.StartTime).TotalSeconds;
                        ErrorReporter.Track("DlDone", new() { ["ext"] = dlExt, ["kb"] = dlKb, ["dur"] = dlDur });
                    }
                    else if (e.DownloadOperation.State == CoreWebView2DownloadState.Interrupted)
                    {
                        download.Status = "Failed";
                        download.State = "failed";
                        download.CompletedAt = DateTime.Now;
                        UpdateDownloadItem(download);
                        var dlExtF = IoPath.GetExtension(download.FilePath)?.TrimStart('.').ToLowerInvariant() ?? "";
                        ErrorReporter.Track("DlFail", new() { ["ext"] = dlExtF });
                    }
                });
            };
        };
        
        // Audio playing indicator
        webView.CoreWebView2.IsDocumentPlayingAudioChanged += (s, e) =>
        {
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateTabAudioIcon(idx, webView.CoreWebView2.IsDocumentPlayingAudio);
                });
            }
        };

        // Silently report renderer / browser process crashes
        webView.CoreWebView2.ProcessFailed += (s, e) =>
        {
            var pageUrl  = webView.Source?.ToString();
            var details  = $"ProcessFailed: kind={e.ProcessFailedKind} reason={e.Reason} exitCode={e.ExitCode}";
            App.WriteTrace($"[PROCESS FAILED] {details} @ {pageUrl}");
            ErrorReporter.Report(
                errorType: "ProcessFailed",
                message:   details,
                pageUrl:   pageUrl,
                errorCode: e.ExitCode);
        };
    }
    
    private int GetTabIndexForWebView(WebView2 webView)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].WebView == webView) return i;
        }
        return -1;
    }
    
    private void UpdateTabTitle(int index, string title)
    {
        if (index >= 0 && index < _tabs.Count)
        {
            title = NormalizeTabTitle(title, _tabs[index].Url);
            var button = _tabs[index].TabButton;
            if (button?.Content is Grid grid)
            {
                var titleBlock = grid.Children.OfType<TextBlock>().FirstOrDefault();
                if (titleBlock != null)
                {
                    titleBlock.Text = string.IsNullOrEmpty(title) ? "New Tab" : title;
                }
            }
        }
    }
    
    private void UpdateTabFavicon(int index, string faviconUrl)
    {
        if (index >= 0 && index < _tabs.Count)
        {
            var button = _tabs[index].TabButton;
            if (button?.Content is Grid grid)
            {
                var image = grid.Children.OfType<Image>().FirstOrDefault();
                if (image != null)
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(faviconUrl);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        if (bitmap.CanFreeze) bitmap.Freeze();
                        image.Source = bitmap;
                        if (index == _activeTabIndex)
                            UpdateUrlFaviconForActiveTab();
                    }
                    catch { }
                }
            }
        }
    }

    private void QueueTabWidthUpdate()
    {
        if (_tabs.Count == 0) return;
        _tabCloseResizeTimer ??= CreateTabCloseResizeTimer();
        _tabCloseResizeTimer.Stop();
        _tabCloseResizeTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateTabCloseResizeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_suppressTabWidthUpdate)
                UpdateTabWidths();
        };
        return timer;
    }

    private string NormalizeTabTitle(string? title, string? url)
    {
        var clean = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean) || LooksLikeUrl(clean) || string.Equals(clean, GetHostLabel(url), StringComparison.OrdinalIgnoreCase))
            return GetLoadingTabTitle(url, useHostFallback: false);
        return clean;
    }

    private void TryRefreshActiveUrlFavicon()
    {
        try
        {
            if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
            var core = _tabs[_activeTabIndex].WebView.CoreWebView2;
            var faviconUri = core?.FaviconUri;
            if (!string.IsNullOrWhiteSpace(faviconUri))
            {
                UpdateTabFavicon(_activeTabIndex, faviconUri);
                return;
            }
        }
        catch { }

        UpdateUrlFaviconForActiveTab(clearIfMissing: true);
    }

    private void UpdateUrlFaviconForActiveTab(bool clearIfMissing = false)
    {
        try
        {
            if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count)
            {
                UrlFaviconImage.Source = null;
                UrlFaviconImage.Visibility = Visibility.Collapsed;
                return;
            }

            var source = _tabs[_activeTabIndex].TabFavicon?.Source;
            if (source != null)
            {
                UrlFaviconImage.Source = source;
                UrlFaviconImage.Visibility = Visibility.Visible;
                return;
            }

            if (clearIfMissing)
            {
                UrlFaviconImage.Source = null;
                UrlFaviconImage.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            UrlFaviconImage.Source = null;
            UrlFaviconImage.Visibility = Visibility.Collapsed;
        }
    }
    
    private void UpdateTabAudioIcon(int index, bool isPlaying)
    {
        if (index >= 0 && index < _tabs.Count)
        {
            var button = _tabs[index].TabButton;
            if (button?.Content is Grid grid)
            {
                var audioIcon = grid.Children.OfType<Canvas>().FirstOrDefault();
                if (audioIcon != null)
                {
                    audioIcon.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }
    
    private void UpdateSecurityIcon(string url)
    {
        try
        {
            var uri = new Uri(url);
            var fallbackColor = _isDarkMode ? "#9aa0a6" : "#5f6368";
            var color = uri.Scheme == "https" ? (_isDarkMode ? "#81c995" : "#188038") : fallbackColor;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
            SecurityShield.Stroke = brush;
            SecurityCheck.Stroke = brush;
        }
        catch
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!);
            SecurityShield.Stroke = brush;
            SecurityCheck.Stroke = brush;
        }
    }
    
    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        
        string inactiveStyle = _isDarkMode ? "TabStyle" : "LightTabStyle";
        string activeStyle   = _isDarkMode ? "ActiveTabStyle" : "LightActiveTabStyle";
        string inactiveTitleColor = _isDarkMode ? "#9aa0a6" : "#202124";
        string activeTitleColor   = _isDarkMode ? "#e8eaed" : "#202124";
        string inactiveIconColor  = _isDarkMode ? "#9aa0a6" : "#5f6368";
        string activeIconColor    = _isDarkMode ? "#9aa0a6" : "#5f6368";
        
        // Defensively reset every tab to inactive — prevents any stale ActiveTabStyle on other tabs
        for (int i = 0; i < _tabs.Count; i++)
        {
            var t = _tabs[i];
            if (i != index)
            {
                t.WebView.Visibility = Visibility.Collapsed;
                t.TabButton.Style    = (Style)FindResource(inactiveStyle);
                if (t.TabTitle != null)
                    t.TabTitle.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(inactiveTitleColor)!);
                if (t.TabCloseBtn?.Content is WpfPath inactivePath)
                    inactivePath.Stroke = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(inactiveIconColor)!);
            }
        }
        
        // Activate target tab
        _activeTabIndex = index;
        try { SetWebViewMemoryTarget(_tabs[index].WebView.CoreWebView2, "Normal"); } catch { }
        ResumeTab(_tabs[index]);
        _tabs[index].WebView.Visibility  = Visibility.Visible;
        _tabs[index].TabButton.Style     = (Style)FindResource(activeStyle);
        if (_tabs[index].TabTitle != null)
            _tabs[index].TabTitle.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(activeTitleColor)!);
        if (_tabs[index].TabCloseBtn?.Content is WpfPath activePath)
            activePath.Stroke = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(activeIconColor)!);
        
        // Update URL bar
        if (_tabs[index].WebView.Source != null)
        {
            var url = _tabs[index].WebView.Source.ToString();
            UrlBox.Text = GetDisplayUrl(url);
            UpdateUrlPlaceholder();
            UpdateSecurityIcon(url);
        }
        else
        {
            UrlBox.Text = GetDisplayUrl(_tabs[index].Url);
            UpdateUrlPlaceholder();
        }
        
        UpdateNavButtons();
        UpdateRefreshButton();
        UpdateUrlFaviconForActiveTab(clearIfMissing: true);
        RefreshBookmarkStar();
        if (!_suppressTabWidthUpdate)
            UpdateTabWidths();
        QueueInactiveTabTrim();
    }

    private void QueueInactiveTabTrim()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1500);
            await SuspendInactiveTabsAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task SuspendInactiveTabsAsync()
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i == _activeTabIndex) continue;
            var tab = _tabs[i];
            if (tab.IsSuspended) continue;
            if (tab.WebView.Visibility == Visibility.Visible) continue;
            if (_loadingTabs.Contains(tab.WebView)) continue;

            try
            {
                var core = tab.WebView.CoreWebView2;
                if (core == null) continue;
                SetWebViewMemoryTarget(core, "Low");
                if (await TrySuspendCoreWebViewAsync(core))
                    tab.IsSuspended = true;
            }
            catch { }
        }
    }

    private static async Task<bool> TrySuspendCoreWebViewAsync(CoreWebView2 core)
    {
        var method = typeof(CoreWebView2).GetMethod("TrySuspendAsync", Type.EmptyTypes);
        if (method == null) return false;

        var result = method.Invoke(core, null);
        if (result is Task<bool> boolTask)
            return await boolTask;
        if (result is Task task)
        {
            await task;
            return true;
        }
        return false;
    }

    private static void ResumeTab(BrowserTab tab)
    {
        if (!tab.IsSuspended) return;
        try
        {
            var core = tab.WebView.CoreWebView2;
            if (core != null) SetWebViewMemoryTarget(core, "Normal");
            var method = typeof(CoreWebView2).GetMethod("Resume", Type.EmptyTypes);
            method?.Invoke(core, null);
        }
        catch { }
        tab.IsSuspended = false;
    }

    private static void SetWebViewMemoryTarget(CoreWebView2 core, string target)
    {
        try
        {
            var property = typeof(CoreWebView2).GetProperty("MemoryUsageTargetLevel");
            if (property == null || property.PropertyType == null) return;
            var value = Enum.Parse(property.PropertyType, target, ignoreCase: true);
            property.SetValue(core, value);
        }
        catch { }
    }
    
    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is int index)
        {
            CloseTab(index);
        }
    }
    
    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (_tabs.Count == 1)
        {
            SaveSettings();
            Close();
            return;
        }
        
        var tab = _tabs[index];
        TabsPanel.Children.Remove(tab.TabButton);
        WebViewContainer.Children.Remove(tab.WebView);
        _loadingTabs.Remove(tab.WebView);
        _navStartTimes.Remove(tab.WebView);
        _autofillShownForTab.Remove(tab.WebView);
        _lastRealPageUrl.Remove(tab.WebView);
        try { tab.WebView.CoreWebView2?.Stop(); } catch { }
        tab.WebView.Dispose();
        _tabs.RemoveAt(index);
        ErrorReporter.Track("TabClose", new() { ["tabs"] = _tabs.Count });
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].TabButton.Tag = i;
            if (_tabs[i].TabButton.Content is Grid grid)
            {
                var closeBtn = grid.Children.OfType<Button>().FirstOrDefault();
                if (closeBtn != null) closeBtn.Tag = i;
            }
        }
        
        // Switch to another tab
        if (_activeTabIndex >= _tabs.Count)
            _activeTabIndex = _tabs.Count - 1;
        else if (_activeTabIndex == index)
            _activeTabIndex = Math.Max(0, index - 1);
        
        _suppressTabWidthUpdate = true;
        try
        {
            SwitchToTab(_activeTabIndex);
        }
        finally
        {
            _suppressTabWidthUpdate = false;
        }

        // Chrome-like rapid close: keep tab widths stable while the user is
        // repeatedly clicking X, then resize 1.5s after the last close.
        if (_tabStripMouseOver) _tabsClosedWhileOver = true;
        QueueTabWidthUpdate();
    }
    
    private void UpdateNavButtons()
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var webView = _tabs[_activeTabIndex].WebView;
            if (webView.CoreWebView2 != null)
            {
                BackBtn.IsEnabled = webView.CoreWebView2.CanGoBack;
                ForwardBtn.IsEnabled = webView.CoreWebView2.CanGoForward;
            }
        }
    }
    
    private string GetSearchUrl(string query)
    {
        var q = Uri.EscapeDataString(query);
        return _searchEngine switch
        {
            "bing"       => $"https://www.bing.com/search?q={q}",
            "duckduckgo" => $"https://duckduckgo.com/?q={q}",
            "ecosia"     => $"https://www.ecosia.org/search?q={q}",
            "brave"      => $"https://search.brave.com/search?q={q}",
            "yahoo"      => $"https://search.yahoo.com/search?p={q}",
            _            => $"https://www.google.com/search?q={q}"
        };
    }

    private void Navigate(string input)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        SuggestPopup.IsOpen = false;
        
        var url = input.Trim();
        
        // Handle internal URLs
        if (url.StartsWith("ycb://") || url.StartsWith("chrome://"))
        {
            NavigateToInternalPage(_tabs[_activeTabIndex].WebView, url);
            return;
        }
        
        // Check if it's a URL or search query
        if (!url.Contains(".") || url.Contains(" "))
        {
            url = GetSearchUrl(url);
        }
        else if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            url = "https://" + url;
        }
        
        _tabs[_activeTabIndex].WebView.CoreWebView2?.Navigate(url);
    }
    
    private void NavigateToInternalPage(WebView2 webView, string url)
    {
        var pageName = url.Replace("ycb://", "").Replace("chrome://", "").ToLower();
        var sectionName = "";
        var hashIndex = pageName.IndexOf('#');
        if (hashIndex >= 0)
        {
            sectionName = pageName[(hashIndex + 1)..];
            pageName = pageName[..hashIndex];
        }

        if (pageName is "adblock-test" or "adblock-selftest")
        {
            webView.CoreWebView2?.NavigateToString(BuildAdBlockSelfTestPage());
            return;
        }
        
        // Get the path to the renderer folder
        var rendererPath = RendererPath;
        
        string htmlFile;
        switch (pageName)
        {
            case "history":
                htmlFile = IoPath.Combine(rendererPath, "history.html");
                break;
            case "apps":
                htmlFile = IoPath.Combine(rendererPath, "apps.html");
                break;
            case "support":
                htmlFile = IoPath.Combine(rendererPath, "support.html");
                break;
            case "downloads":
                htmlFile = IoPath.Combine(rendererPath, "downloads.html");
                break;
            case "settings":
                htmlFile = IoPath.Combine(rendererPath, "settings.html");
                break;
            case "extensions":
                htmlFile = IoPath.Combine(rendererPath, "extensions.html");
                break;
            case "profiles":
                htmlFile = IoPath.Combine(rendererPath, "profiles.html");
                break;
            case "passwords":
                htmlFile = IoPath.Combine(rendererPath, "passwords.html");
                break;
            case "guide":
                htmlFile = IoPath.Combine(rendererPath, "guide.html");
                break;
            case "newtab":
            case "new-tab-page":
            default:
                htmlFile = IoPath.Combine(rendererPath, "newtab.html");
                break;
        }
        
        if (File.Exists(htmlFile))
        {
            var fileName = IoPath.GetFileName(htmlFile);
            try
            {
                SetupInternalPageMessageHandler(webView, pageName);
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    InternalHostName,
                    rendererPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                var targetUrl = InternalOrigin + fileName + (string.IsNullOrWhiteSpace(sectionName) ? "" : "#" + Uri.EscapeDataString(sectionName));
                webView.CoreWebView2.Navigate(targetUrl);
                return;
            }
            catch
            {
                webView.CoreWebView2?.NavigateToString(BuildNavigationErrorPage(
                    "Page not found",
                    $"YCB could not load the internal page '{pageName}'.",
                    htmlFile));
                return;
            }
        }
        else
        {
            // Fallback to a browser-style internal error page
            webView.CoreWebView2?.NavigateToString(BuildNavigationErrorPage(
                "Page not found",
                $"YCB could not load the internal page '{pageName}'.",
                htmlFile));
        }
    }

    private static string BuildAdBlockSelfTestPage() => """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <title>YCB AdBlock Self Test</title>
          <style>
            body { font-family: Arial, sans-serif; padding: 24px; background: #111; color: #f2f2f2; }
            .note { opacity: 0.75; margin-top: 12px; line-height: 1.4; }
          </style>
        </head>
        <body>
          <h1>YCB AdBlock Self Test</h1>
          <div id="status">Dispatching test requests...</div>
          <div class="note">This page intentionally loads known ad and tracker hosts so YCB can prove its blocker is active.</div>
          <script>
            (function() {
              const urls = [
                'https://ads-api.twitter.com/fakepage.html',
                'https://analytics.s3.amazonaws.com/fakepage.html',
                'https://an.facebook.com/fakepage.html',
                'https://adtech.yahooinc.com/fakepage.html',
                'https://ads.tiktok.com/fakepage.html',
                'https://adserver.unityads.unity3d.com/fakepage.html',
                'https://data.mistat.xiaomi.com/fakepage.html',
                'https://api-adservices.apple.com/fakepage.html'
              ];
              for (const url of urls) {
                const s = document.createElement('script');
                s.src = url;
                document.body.appendChild(s);
                const i = document.createElement('img');
                i.src = url;
                document.body.appendChild(i);
              }
              document.getElementById('status').textContent = 'Test requests sent; check logs for [ADBLOCK] entries.';
            })();
          </script>
        </body>
        </html>
        """;
    
    private void SetupInternalPageMessageHandler(WebView2 webView, string pageName)
    {
        // Remove any existing handlers first
        webView.CoreWebView2.WebMessageReceived -= InternalPage_WebMessageReceived;
        webView.CoreWebView2.WebMessageReceived += InternalPage_WebMessageReceived;
        
        // Inject data after page loads
        webView.NavigationCompleted += async (s, e) =>
        {
            if (!e.IsSuccess) return;
            
            try
            {
                var currentThemeJson = JsonSerializer.Serialize(_isDarkMode ? "dark" : "light");
                await webView.ExecuteScriptAsync($@"
                    (function() {{
                        var theme = {currentThemeJson};
                        window.__ycbTheme = theme;
                        document.documentElement.classList.remove('dark', 'light');
                        document.documentElement.classList.add(theme);
                        document.body && document.body.classList.toggle('dark', theme === 'dark');
                        if (window.setTheme) window.setTheme(theme);
                    }})();
                ");
                
                switch (pageName)
                {
                    case "history":
                        var history = LoadHistory();
                        var historyJson = JsonSerializer.Serialize(history);
                        await webView.ExecuteScriptAsync($"window.loadHistory && window.loadHistory({historyJson})");
                        break;
                        
                    case "downloads":
                        var downloads = LoadDownloads();
                        var downloadsJson = JsonSerializer.Serialize(downloads);
                        await webView.ExecuteScriptAsync($"window.setDownloadHistory && window.setDownloadHistory({downloadsJson})");
                        break;
                        
                    case "settings":
                        // Check and inject default browser status
                        var isDefault = CheckIsDefaultBrowser();
                        await webView.ExecuteScriptAsync($@"
                            (function() {{
                                var status = document.getElementById('default-status');
                                var btn = document.getElementById('btn-set-default');
                                if (status && btn) {{
                                    if ({(isDefault ? "true" : "false")}) {{
                                        status.textContent = 'YCB is already your default browser';
                                        status.className = 'row-desc success';
                                        btn.textContent = 'Already default';
                                        btn.disabled = true;
                                    }}
                                }}
                            }})();
                        ");
                        
                        // Inject current settings so the page shows persisted values
                        var settingsData = new {
                            bookmarks_bar = _settings.BookmarksBarVisible ? "on" : "off",
                            search_engine = _settings.SearchEngine ?? "google",
                            startup_mode = _settings.StartupMode ?? "newtab",
                            ai_provider = GetAiProviderKey(),
                            ai_provider_label = GetAiProviderLabel(),
                            incognito_ai_enabled = (_settings.IncognitoAIEnabled ?? false).ToString().ToLower(),
                            browser_theme = _settings.DarkMode ? "dark" : "light",
                            telemetry_enabled = _settings.TelemetryEnabled.ToString().ToLower(),
                            user_id = ErrorReporter.UserId,
                            ai_enabled = IsAiEnabledForProfile ? "on" : "off",
                            profile_name = _settings.ProfileName,
                            profile_initial = _settings.ProfileInitial,
                            profile_color = _settings.ProfileColor,
                            ad_blocker_enabled = _settings.AdBlockerEnabled ? "on" : "off",
                            home_page = _settings.HomePage ?? "ycb://newtab"
                        };
                        var settingsDataJson = JsonSerializer.Serialize(settingsData);
                        await webView.ExecuteScriptAsync($"window.loadSettings && window.loadSettings({settingsDataJson})");
                        await RefreshBrowserExtensionsUiAsync(webView.CoreWebView2);
                        
                        // Directly inject user ID into the About section element
                        var safeUid = JsonSerializer.Serialize(ErrorReporter.UserId);
                        await webView.ExecuteScriptAsync($@"
                            (function() {{
                                var el = document.getElementById('about-user-id');
                                if (!el) return;
                                el.textContent = {safeUid};
                                el.onclick = function() {{
                                    navigator.clipboard.writeText({safeUid}).then(function() {{
                                        el.textContent = 'Copied!';
                                        el.style.color = 'var(--green, #81c995)';
                                        setTimeout(function() {{ el.textContent = {safeUid}; el.style.color = ''; }}, 1500);
                                    }});
                                }};
                            }})();
                        ");
                        break;

                    case "newtab":
                    case "new-tab-page":
                        {
                            var profileJson = JsonSerializer.Serialize(GetProfileInfo());
                            await webView.ExecuteScriptAsync($"window.setProfileInfo && window.setProfileInfo({profileJson})");
                        }
                        break;

                    case "profiles":
                        {
                            var profileJson = JsonSerializer.Serialize(GetProfileInfo());
                            await webView.ExecuteScriptAsync($"window.setProfileInfo && window.setProfileInfo({profileJson})");
                            await webView.ExecuteScriptAsync($"window.setProfilesState && window.setProfilesState({profileJson})");
                        }
                        break;

                    case "apps":
                        {
                            var profileJson = JsonSerializer.Serialize(GetProfileInfo());
                            await webView.ExecuteScriptAsync($"window.setProfileInfo && window.setProfileInfo({profileJson})");
                        }
                        break;

                    case "extensions":
                        await RefreshBrowserExtensionsUiAsync(webView.CoreWebView2);
                        break;
                        
                    case "passwords":
                        {
                            var themeJson = JsonSerializer.Serialize(_isDarkMode ? "dark" : "light");
                            await webView.ExecuteScriptAsync($@"
                                (function() {{
                                    window.__ycbTheme = {themeJson};
                                    document.documentElement.classList.remove('dark', 'light');
                                    document.documentElement.classList.add({themeJson});
                                }})();
                            ");
                            var statusJson = await GetBitwardenStatusJsonAsync();
                            await webView.ExecuteScriptAsync($"window.setBitwardenState && window.setBitwardenState({statusJson})");
                            var vault = await LoadBitwardenVaultAsync();
                            var vaultJson = JsonSerializer.Serialize(vault);
                            await webView.ExecuteScriptAsync($"window.setBitwardenItems && window.setBitwardenItems({vaultJson})");
                        }
                        break;

                    case "guide":
                        if (!IsAiEnabledForProfile)
                        {
                            await webView.ExecuteScriptAsync(@"
                                (function() {
                                    var el = document.getElementById('ai-setup-section');
                                    if (el) el.style.display = 'none';
                                })();
                            ");
                        }
                        break;
                }
            }
            catch { }
        };
    }
    
    private async void ConsoleMessage_Received(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(e.ParameterObjectAsJson);
            if (!json.TryGetProperty("args", out var args) || args.GetArrayLength() == 0) return;
            
            var firstArg = args[0];
            if (!firstArg.TryGetProperty("value", out var valueElement)) return;
            
            var message = valueElement.GetString();
            if (string.IsNullOrEmpty(message)) return;
            
            // Handle bookmark messages from newtab
            // Handle password messages
            if (message.StartsWith("__passwords__:"))
            {
                var parts = message.Substring(14).Split(new[] { ':' }, 2);
                var action = parts[0];
                var data = parts.Length > 1 ? parts[1] : "";
                
                switch (action)
                {
                    case "GET_ALL":
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                            {
                                var webView = _tabs[_activeTabIndex].WebView;
                                var passwords = LoadPasswordsDecrypted();
                                var passwordsJson = JsonSerializer.Serialize(passwords);
                                await webView.ExecuteScriptAsync($"window.setPasswords && window.setPasswords({passwordsJson})");
                            }
                        });
                        break;
                        
                    case "ADD_MANUAL":
                        try
                        {
                            var manualData = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
                            if (manualData != null)
                            {
                                var manualUrl  = manualData.GetValueOrDefault("url", "").Trim();
                                var manualUser = manualData.GetValueOrDefault("username", "").Trim();
                                var manualPass = manualData.GetValueOrDefault("password", "").Trim();
                                if (!string.IsNullOrEmpty(manualUrl) && !string.IsNullOrEmpty(manualPass))
                                {
                                    if (!manualUrl.StartsWith("http://") && !manualUrl.StartsWith("https://"))
                                        manualUrl = "https://" + manualUrl;
                                    SavePassword(manualUrl, manualUser, manualPass);
                                    await Dispatcher.InvokeAsync(async () =>
                                    {
                                        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                                        {
                                            var wv = _tabs[_activeTabIndex].WebView;
                                            var pws = LoadPasswordsDecrypted();
                                            var pwsJson = JsonSerializer.Serialize(pws);
                                            await wv.ExecuteScriptAsync($"window.setPasswords && window.setPasswords({pwsJson})");
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                        break;

                    case "ADD_CARD":
                        try
                        {
                            var cardData = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
                            if (cardData != null)
                            {
                                var label   = cardData.GetValueOrDefault("label", "").Trim();
                                var site    = cardData.GetValueOrDefault("site", "").Trim();
                                var holder  = cardData.GetValueOrDefault("holder", "").Trim();
                                var number  = cardData.GetValueOrDefault("number", "").Trim();
                                var expiry  = cardData.GetValueOrDefault("expiry", "").Trim();
                                var brand   = cardData.GetValueOrDefault("brand", "").Trim();
                                var notes   = cardData.GetValueOrDefault("notes", "").Trim();
                                var key     = cardData.GetValueOrDefault("key", "").Trim();
                                if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(number))
                                {
                                    SaveCard(new PasswordItem
                                    {
                                        Key = string.IsNullOrEmpty(key) ? $"card_{Guid.NewGuid():N}" : key,
                                        Kind = "card",
                                        Label = label,
                                        Url = site,
                                        CardHolder = holder,
                                        CardNumber = number,
                                        CardExpiry = expiry,
                                        CardBrand = brand,
                                        Notes = notes
                                    });
                                    await Dispatcher.InvokeAsync(async () =>
                                    {
                                        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                                        {
                                            var wv = _tabs[_activeTabIndex].WebView;
                                            var vault = LoadPasswordsDecrypted();
                                            var vaultJson = JsonSerializer.Serialize(vault);
                                            await wv.ExecuteScriptAsync($"window.setPasswords && window.setPasswords({vaultJson})");
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                        break;

                    case "UPDATE_CARD":
                        try
                        {
                            var cardData = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
                            if (cardData != null)
                            {
                                var key     = cardData.GetValueOrDefault("key", "").Trim();
                                var label   = cardData.GetValueOrDefault("label", "").Trim();
                                var site    = cardData.GetValueOrDefault("site", "").Trim();
                                var holder  = cardData.GetValueOrDefault("holder", "").Trim();
                                var number  = cardData.GetValueOrDefault("number", "").Trim();
                                var expiry  = cardData.GetValueOrDefault("expiry", "").Trim();
                                var brand   = cardData.GetValueOrDefault("brand", "").Trim();
                                var notes   = cardData.GetValueOrDefault("notes", "").Trim();
                                if (!string.IsNullOrEmpty(key))
                                {
                                    UpdateCard(new PasswordItem
                                    {
                                        Key = key,
                                        Kind = "card",
                                        Label = label,
                                        Url = site,
                                        CardHolder = holder,
                                        CardNumber = number,
                                        CardExpiry = expiry,
                                        CardBrand = brand,
                                        Notes = notes
                                    });
                                    await Dispatcher.InvokeAsync(async () =>
                                    {
                                        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                                        {
                                            var wv = _tabs[_activeTabIndex].WebView;
                                            var vault = LoadPasswordsDecrypted();
                                            var vaultJson = JsonSerializer.Serialize(vault);
                                            await wv.ExecuteScriptAsync($"window.setPasswords && window.setPasswords({vaultJson})");
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                        break;
                        
                    case "DELETE":
                        DeletePassword(data);
                        break;
                        
                    case "CLEAR_ALL":
                        ClearPasswords();
                        break;
                }
            }
            // Handle newtab ready message
            else if (message == "__newtab__:ready")
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                    {
                        var webView = _tabs[_activeTabIndex].WebView;
                        var bookmarks = LoadBookmarks();
                        var bookmarksJson = JsonSerializer.Serialize(bookmarks);
                        await webView.ExecuteScriptAsync($"window.setBookmarks && window.setBookmarks({bookmarksJson})");
                    }
                });
            }
            // Handle browser/default commands from settings
            else if (message.StartsWith("__browser__:"))
            {
                var jsonData = message.Substring(12);
                var browserMsg = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonData);
                if (browserMsg != null && browserMsg.TryGetValue("type", out var msgType))
                {
                    switch (msgType)
                    {
                        case "setDefault":
                            await Dispatcher.InvokeAsync(() => SetAsDefaultBrowser());
                            break;
                    }
                }
            }
            // Handle settings changes
            else if (message.StartsWith("__settings__:SET:"))
            {
                var settingData = message.Substring(17);
                var parts = settingData.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    var key = parts[0];
                    var value = parts[1];
                    await Dispatcher.InvokeAsync(() => ApplySettingChange(key, value));
                }
            }
            else if (message.StartsWith("{") && message.Contains("\"type\":\"settings:set\""))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message);
                    if (payload != null &&
                        payload.TryGetValue("key", out var keyEl) &&
                        payload.TryGetValue("value", out var valueEl))
                    {
                        var key = keyEl.GetString() ?? "";
                        var value = valueEl.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            await Dispatcher.InvokeAsync(() => ApplySettingChange(key, value));
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }
    
    private async void InternalPage_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Handle plain string messages (e.g. from guide.html)
            var rawMsg = e.TryGetWebMessageAsString();
            if (rawMsg == "__guide__:DISMISS")
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _settings.HasSeenGuide = true;
                    SaveSettings();
                });
                return;
            }

            // Parse the message — use rawMsg first (postMessage sends strings; WebMessageAsJson double-encodes them)
            Dictionary<string, JsonElement>? message = null;
            if (!string.IsNullOrEmpty(rawMsg) && rawMsg.TrimStart().StartsWith("{"))
            {
                try { message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rawMsg); } catch { }
            }
            // Fallback to WebMessageAsJson (for postMessage(object) calls)
            if (message == null)
            {
                try { message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.WebMessageAsJson); } catch { }
            }
            if (message == null || !message.TryGetValue("type", out var typeElement)) return;
            
            var type = typeElement.GetString();
            
            switch (type)
            {
                case "settings:set":
                    if (message.TryGetValue("key", out var keyElement) &&
                        message.TryGetValue("value", out var valueElement))
                    {
                        var key = keyElement.GetString() ?? "";
                        var value = valueElement.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            await Dispatcher.InvokeAsync(() => ApplySettingChange(key, value));
                        }
                    }
                    break;

                case "history:open":
                    if (message.TryGetValue("url", out var urlElement))
                    {
                        var url = urlElement.GetString();
                        if (!string.IsNullOrEmpty(url))
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                                    _tabs[_activeTabIndex].WebView?.CoreWebView2?.Navigate(url);
                            });
                        }
                    }
                    break;
                    
                case "history:clear":
                    ClearHistory();
                    if (sender is CoreWebView2 clearWv)
                        await clearWv.ExecuteScriptAsync("window.loadHistory && window.loadHistory([])");
                    break;
                    
                case "history:getAll":
                    if (sender is CoreWebView2 wv)
                    {
                        var history = LoadHistory();
                        var json = JsonSerializer.Serialize(history);
                        await wv.ExecuteScriptAsync($"window.loadHistory && window.loadHistory({json})");
                    }
                    break;

                case "newtab:openApps":
                    await CreateTab("ycb://apps");
                    break;

                case "newtab:openProfiles":
                    await ShowProfileChooserAsync(switchOnSelect: true);
                    break;

                case "profiles:getState":
                    if (sender is CoreWebView2 profilesStateWv)
                    {
                        await SendProfileStateAsync(profilesStateWv);
                    }
                    break;

                case "profiles:open":
                    {
                        var name = message.TryGetValue("name", out var openNameEl) ? openNameEl.GetString() : null;
                        var initial = message.TryGetValue("initial", out var openInitialEl) ? openInitialEl.GetString() : null;
                        var color = message.TryGetValue("color", out var openColorEl) ? openColorEl.GetString() : null;
                        var icon = message.TryGetValue("icon", out var openIconEl) ? openIconEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            await SwitchToProfileAsync(name, initial, color, icon);
                        }
                        else
                        {
                            await ShowProfileChooserAsync(switchOnSelect: true);
                        }
                    }
                    break;

                case "profiles:create":
                    {
                        var name = message.TryGetValue("name", out var nameEl) ? nameEl.GetString() : null;
                        var initial = message.TryGetValue("initial", out var initialEl) ? initialEl.GetString() : null;
                        var color = message.TryGetValue("color", out var colorEl) ? colorEl.GetString() : null;
                        var icon = message.TryGetValue("icon", out var iconEl) ? iconEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            await ShowProfileChooserAsync(switchOnSelect: false);
                        }
                        else
                        {
                            await SwitchToProfileAsync(name, initial, color, icon);
                        }
                    }
                    break;

                case "profiles:pickIcon":
                    if (sender is CoreWebView2 iconWv)
                    {
                        var picked = PickProfileIconPath();
                        await iconWv.ExecuteScriptAsync($"window.setPickedProfileIcon && window.setPickedProfileIcon({JsonSerializer.Serialize(picked ?? "")})");
                    }
                    break;

                case "profiles:setStartup":
                    {
                        var enabled = message.TryGetValue("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True;
                        _settings.StartupMode = enabled ? "newtab" : "newtab";
                        SaveSettings();
                    }
                    break;

                case "apps:open":
                    {
                        var appUrl = message.TryGetValue("url", out var appUrlEl) ? appUrlEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(appUrl))
                        {
                            OpenUrl(appUrl);
                        }
                    }
                    break;

                case "bitwarden:open":
                    if (message.TryGetValue("url", out var bitwardenUrlElement))
                    {
                        var bitwardenUrl = bitwardenUrlElement.GetString();
                        if (!string.IsNullOrWhiteSpace(bitwardenUrl))
                        {
                            var target = message.TryGetValue("target", out var targetElement) ? targetElement.GetString() : "ycb";
                            if (string.Equals(target, "default", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo(bitwardenUrl) { UseShellExecute = true });
                                }
                                catch (Exception ex)
                                {
                                    App.WriteTrace($"Bitwarden external launch failed: {ex.Message}");
                                }
                            }
                            else
                            {
                                OpenUrl(bitwardenUrl);
                            }
                        }
                    }
                    break;

                case "bitwarden:getState":
                    if (sender is CoreWebView2 bwStateWv)
                    {
                        await SendBitwardenStateAsync(bwStateWv);
                    }
                    break;

                case "bitwarden:list":
                    if (sender is CoreWebView2 bwListWv)
                    {
                        var vault = await LoadBitwardenVaultAsync();
                        var vaultJson = JsonSerializer.Serialize(vault);
                        await bwListWv.ExecuteScriptAsync($"window.setBitwardenItems && window.setBitwardenItems({vaultJson})");
                    }
                    break;

                case "bitwarden:getItem":
                    if (sender is CoreWebView2 bwItemWv)
                    {
                        var id = message.TryGetValue("id", out var itemIdElement) ? itemIdElement.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id))
                            await SendBitwardenItemAsync(bwItemWv, id);
                    }
                    break;

                case "bitwarden:login":
                    if (sender is CoreWebView2 bwLoginWv)
                    {
                        var email = message.TryGetValue("email", out var emailEl) ? emailEl.GetString() ?? "" : "";
                        var password = message.TryGetValue("password", out var passEl) ? passEl.GetString() ?? "" : "";
                        var result = await LoginBitwardenAsync(email, password);
                        if (result.ExitCode == 0)
                        {
                            await SendBitwardenStateAsync(bwLoginWv);
                        }
                        else
                        {
                            await SendBitwardenErrorAsync(bwLoginWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden login failed." : result.StdErr.Trim());
                        }
                    }
                    break;

                case "bitwarden:unlock":
                    if (sender is CoreWebView2 bwUnlockWv)
                    {
                        var password = message.TryGetValue("password", out var passEl) ? passEl.GetString() ?? "" : "";
                        var result = await UnlockBitwardenAsync(password);
                        if (result.ExitCode == 0)
                        {
                            await SendBitwardenStateAsync(bwUnlockWv);
                        }
                        else
                        {
                            await SendBitwardenErrorAsync(bwUnlockWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden unlock failed." : result.StdErr.Trim());
                        }
                    }
                    break;

                case "bitwarden:sync":
                    if (sender is CoreWebView2 bwSyncWv)
                    {
                        var result = await SyncBitwardenAsync();
                        if (result.ExitCode == 0)
                        {
                            await SendBitwardenStateAsync(bwSyncWv);
                        }
                        else
                        {
                            await SendBitwardenErrorAsync(bwSyncWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden sync failed." : result.StdErr.Trim());
                        }
                    }
                    break;

                case "bitwarden:lock":
                    if (sender is CoreWebView2 bwLockWv)
                    {
                        var result = await LockBitwardenAsync();
                        if (result.ExitCode == 0)
                        {
                            await SendBitwardenStateAsync(bwLockWv);
                        }
                        else
                        {
                            await SendBitwardenErrorAsync(bwLockWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden lock failed." : result.StdErr.Trim());
                        }
                    }
                    break;

                case "bitwarden:create":
                    if (sender is CoreWebView2 bwCreateWv)
                    {
                        var kind = message.TryGetValue("kind", out var kindEl) ? kindEl.GetString() ?? "password" : "password";
                        var item = message.TryGetValue("item", out var itemEl) && itemEl.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Deserialize<Dictionary<string, string>>(itemEl.GetRawText())
                            : null;
                        if (item != null)
                        {
                            var result = await CreateBitwardenItemAsync(kind, item);
                            if (result.ExitCode == 0)
                                await SendBitwardenStateAsync(bwCreateWv);
                            else
                                await SendBitwardenErrorAsync(bwCreateWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden create failed." : result.StdErr.Trim());
                        }
                    }
                    break;

                case "bitwarden:update":
                    if (sender is CoreWebView2 bwUpdateWv)
                    {
                        var id = message.TryGetValue("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var kind = message.TryGetValue("kind", out var kindEl) ? kindEl.GetString() ?? "password" : "password";
                        var item = message.TryGetValue("item", out var itemEl) && itemEl.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Deserialize<Dictionary<string, string>>(itemEl.GetRawText())
                            : null;
                        if (item != null)
                        {
                            var result = await UpdateBitwardenItemAsync(id, kind, item);
                            if (result.ExitCode == 0)
                                await SendBitwardenStateAsync(bwUpdateWv);
                            else
                                await SendBitwardenErrorAsync(bwUpdateWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden update failed." : result.StdErr.Trim());
                        }
                    }
                    break;

                case "bitwarden:delete":
                    if (sender is CoreWebView2 bwDeleteWv)
                    {
                        var id = message.TryGetValue("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var result = await DeleteBitwardenItemAsync(id);
                        if (result.ExitCode == 0)
                            await SendBitwardenStateAsync(bwDeleteWv);
                        else
                            await SendBitwardenErrorAsync(bwDeleteWv, string.IsNullOrWhiteSpace(result.StdErr) ? "Bitwarden delete failed." : result.StdErr.Trim());
                    }
                    break;

                case "bitwarden:createAccount":
                    if (sender is CoreWebView2 bwCreateAccountWv)
                    {
                        try
                        {
                            OpenUrl("https://bitwarden.com/signup/");
                        }
                        catch { }
                        await SendBitwardenStateAsync(bwCreateAccountWv);
                    }
                    break;
                    
                case "downloads:getHistory":
                    if (sender is CoreWebView2 dwv)
                    {
                        var downloads = LoadDownloads();
                        var json = JsonSerializer.Serialize(downloads);
                        await dwv.ExecuteScriptAsync($"window.setDownloadHistory && window.setDownloadHistory({json})");
                    }
                    break;
                    
                case "downloads:clearHistory":
                    ClearDownloads();
                    break;
                    
                case "support:getUserId":
                    if (sender is CoreWebView2 ugvw)
                    {
                        var uid = JsonSerializer.Serialize(ErrorReporter.UserId ?? "unknown");
                        await ugvw.ExecuteScriptAsync($"window.setUserId && window.setUserId({uid})");
                    }
                    break;

                case "support:create":
                    if (sender is CoreWebView2 scvw)
                    {
                        var subject = message.TryGetValue("subject", out var sj) ? sj.GetString() ?? "" : "";
                        var msg     = message.TryGetValue("message", out var mg) ? mg.GetString() ?? "" : "";
                        var userId  = ErrorReporter.UserId ?? "unknown";
                        try
                        {
                            using var http = new System.Net.Http.HttpClient();
                            http.Timeout = TimeSpan.FromSeconds(10);
                            var payload = JsonSerializer.Serialize(new { userId, subject, message = msg });
                            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                            var resp = await http.PostAsync("https://ycb.tomcreations.org/Support/Ticket/", content);
                            var statusCode = (int)resp.StatusCode;
                            if (resp.IsSuccessStatusCode)
                            {
                                var json = await resp.Content.ReadAsStringAsync();
                                await scvw.ExecuteScriptAsync($"window.onTicketCreated && window.onTicketCreated({json}, {statusCode})");
                            }
                            else
                            {
                                await scvw.ExecuteScriptAsync($"window.onTicketError && window.onTicketError({statusCode})");
                            }
                        }
                        catch (Exception ex)
                        {
                            await scvw.ExecuteScriptAsync($"window.onTicketError && window.onTicketError(0, {JsonSerializer.Serialize(ex.Message)})");
                        }
                    }
                    break;

                case "support:reply":
                    if (sender is CoreWebView2 srvw)
                    {
                        var ticketId = message.TryGetValue("ticketId", out var tid) ? tid.GetString() ?? "" : "";
                        var replyMsg = message.TryGetValue("message",  out var rm)  ? rm.GetString()  ?? "" : "";
                        var userId   = ErrorReporter.UserId ?? "unknown";
                        try
                        {
                            using var http = new System.Net.Http.HttpClient();
                            http.Timeout = TimeSpan.FromSeconds(10);
                            var payload = JsonSerializer.Serialize(new { userId, message = replyMsg });
                            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                            var resp = await http.PostAsync($"https://ycb.tomcreations.org/Support/Ticket/{ticketId}/Reply/", content);
                            var statusCode = (int)resp.StatusCode;
                            await srvw.ExecuteScriptAsync($"window.onReplyResult && window.onReplyResult({(resp.IsSuccessStatusCode ? "true" : "false")}, {statusCode})");
                        }
                        catch
                        {
                            await srvw.ExecuteScriptAsync("window.onReplyResult && window.onReplyResult(false, 0)");
                        }
                    }
                    break;

                case "support:poll":
                    if (sender is CoreWebView2 spvw)
                    {
                        var ticketId = message.TryGetValue("ticketId", out var ptid) ? ptid.GetString() ?? "" : "";
                        try
                        {
                            using var http = new System.Net.Http.HttpClient();
                            http.Timeout = TimeSpan.FromSeconds(10);
                            var resp = await http.GetAsync($"https://ycb.tomcreations.org/Support/Ticket/{ticketId}/");
                            var statusCode = (int)resp.StatusCode;
                            if (resp.IsSuccessStatusCode)
                            {
                                var json = await resp.Content.ReadAsStringAsync();
                                await spvw.ExecuteScriptAsync($"window.onPollResult && window.onPollResult({json}, {statusCode})");
                            }
                            else
                            {
                                await spvw.ExecuteScriptAsync($"window.onPollResult && window.onPollResult(null, {statusCode})");
                            }
                        }
                        catch
                        {
                            await spvw.ExecuteScriptAsync("window.onPollResult && window.onPollResult(null, 0)");
                        }
                    }
                    break;

                case "settings:getUserId":
                    if (sender is CoreWebView2 swv)
                    {
                        var uid = JsonSerializer.Serialize(ErrorReporter.UserId);
                        await swv.ExecuteScriptAsync($@"
                            (function() {{
                                var el = document.getElementById('about-user-id');
                                if (!el) return;
                                var id = {uid};
                                el.textContent = id;
                                el.onclick = function() {{
                                    navigator.clipboard.writeText(id).then(function() {{
                                        el.textContent = 'Copied!';
                                        el.style.color = '#81c995';
                                        setTimeout(function() {{ el.textContent = id; el.style.color = ''; }}, 1500);
                                    }});
                                }};
                            }})();
                        ");
                    }
                    break;

                case "setDefault":
                    await Dispatcher.InvokeAsync(() => SetAsDefaultBrowser());
                    break;

                case "downloads:openFile":
                    if (message.TryGetValue("path", out var pathElement))
                    {
                        var path = pathElement.GetString();
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                    }
                    break;
                    
                case "downloads:showInFolder":
                    if (message.TryGetValue("path", out var folderPathElement))
                    {
                        var path = folderPathElement.GetString();
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            Process.Start("explorer.exe", $"/select,\"{path}\"");
                        }
                    }
                    break;

                case "extensions:getAll":
                    if (sender is CoreWebView2 exwv)
                    {
                        await RefreshBrowserExtensionsUiAsync(exwv);
                    }
                    break;

                case "extensions:installPath":
                    if (sender is CoreWebView2 installWv && message.TryGetValue("path", out var installPathEl))
                    {
                        var installPath = installPathEl.GetString() ?? "";
                        await InstallBrowserExtensionAsync(installWv, installPath);
                        await RefreshBrowserExtensionsUiAsync(installWv);
                    }
                    break;

                case "extensions:toggle":
                    if (sender is CoreWebView2 toggleWv && message.TryGetValue("id", out var toggleIdEl))
                    {
                        var extId = toggleIdEl.GetString() ?? "";
                        var enabled = message.TryGetValue("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True;
                        await SetBrowserExtensionEnabledAsync(toggleWv, extId, enabled);
                        await RefreshBrowserExtensionsUiAsync(toggleWv);
                    }
                    break;

                case "extensions:remove":
                    if (sender is CoreWebView2 removeWv && message.TryGetValue("id", out var removeIdEl))
                    {
                        var extId = removeIdEl.GetString() ?? "";
                        await RemoveBrowserExtensionAsync(removeWv, extId);
                        await RefreshBrowserExtensionsUiAsync(removeWv);
                    }
                    break;

                case "extensions:openStore":
                    _ = CreateTab("https://chromewebstore.google.com/");
                    break;

                case "extensions:openManager":
                    _ = CreateTab("ycb://extensions");
                    break;
            }
        }
        catch { }
    }
    
    private static string GetProfileInitial(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Y";
        }

        var c = trimmed[0];
        return char.ToUpperInvariant(c).ToString();
    }

    private static string? PickProfileIconPath()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a profile icon",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private object GetProfileInfo()
    {
        var name = string.IsNullOrWhiteSpace(_settings.ProfileName) ? Environment.UserName : _settings.ProfileName.Trim();
        var saved = GetSavedProfiles().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var initial = !string.IsNullOrWhiteSpace(_settings.ProfileInitial)
            ? _settings.ProfileInitial.Trim()
            : saved?.Initial ?? GetProfileInitial(name);
        var icon = !string.IsNullOrWhiteSpace(_settings.ProfileIcon)
            ? NormalizeProfileIcon(_settings.ProfileIcon)
            : NormalizeProfileIcon(saved?.Icon);
        var color = !string.IsNullOrWhiteSpace(_settings.ProfileColor)
            ? _settings.ProfileColor.Trim()
            : saved?.Color ?? "#5b9bf9";
        return new
        {
            name,
            initial,
            icon,
            color,
            startupMode = _settings.StartupMode ?? "newtab",
            showOnStartup = false,
            profiles = GetSavedProfiles().Select(p => new
            {
                name = p.Name,
                initial = string.IsNullOrWhiteSpace(p.Initial) ? GetProfileInitial(p.Name) : p.Initial,
                icon = string.IsNullOrWhiteSpace(p.Icon) ? "" : p.Icon,
                color = string.IsNullOrWhiteSpace(p.Color) ? "#5b9bf9" : p.Color
            }).ToList()
        };
    }

    private void SetProfileInfo(string? name, string? initial = null, string? color = null, string? icon = null)
    {
        var resolvedName = string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim();
        _settings.ProfileName = resolvedName;
        _settings.ProfileInitial = string.IsNullOrWhiteSpace(initial) ? GetProfileInitial(resolvedName) : initial.Trim();
        _settings.ProfileIcon = NormalizeProfileIcon(icon);
        if (!string.IsNullOrWhiteSpace(color))
        {
            _settings.ProfileColor = color.Trim();
        }
        SaveSettings();
    }

    private List<ProfileItem> GetSavedProfiles()
    {
        _launcherState.Profiles ??= new List<ProfileItem>();
        var deduped = new List<ProfileItem>();
        foreach (var profile in _launcherState.Profiles.Where(p => p != null))
        {
            var name = string.IsNullOrWhiteSpace(profile.Name) ? Environment.UserName : profile.Name.Trim();
            var existing = deduped.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                deduped.Add(new ProfileItem
                {
                    Name = name,
                    Initial = string.IsNullOrWhiteSpace(profile.Initial) ? GetProfileInitial(name) : profile.Initial.Trim(),
                    Color = string.IsNullOrWhiteSpace(profile.Color) ? "#5b9bf9" : profile.Color.Trim(),
                    Icon = NormalizeProfileIcon(profile.Icon)
                });
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(profile.Initial))
                    existing.Initial = profile.Initial.Trim();
                if (!string.IsNullOrWhiteSpace(profile.Color))
                    existing.Color = profile.Color.Trim();
                var icon = NormalizeProfileIcon(profile.Icon);
                if (!string.IsNullOrWhiteSpace(icon))
                    existing.Icon = icon;
            }
        }
        if (deduped.Count != _launcherState.Profiles.Count || _launcherState.Profiles.Any(p => !string.Equals(NormalizeProfileIcon(p.Icon), p.Icon ?? "", StringComparison.Ordinal)))
        {
            _launcherState.Profiles = deduped;
            SaveLauncherState();
        }
        return _launcherState.Profiles;
    }

    private void SaveCurrentProfileToSavedList()
    {
        UpsertSavedProfile(_settings.ProfileName, _settings.ProfileInitial, _settings.ProfileColor, _settings.ProfileIcon);
        _launcherState.ActiveProfileName = string.IsNullOrWhiteSpace(_settings.ProfileName) ? Environment.UserName : _settings.ProfileName.Trim();
        SaveLauncherState();
    }

    private void SaveProfileDefinition(ProfileItem profile)
    {
        if (profile == null) return;

        var resolvedName = string.IsNullOrWhiteSpace(profile.Name) ? Environment.UserName : profile.Name.Trim();
        UpsertSavedProfile(resolvedName, profile.Initial, profile.Color, NormalizeProfileIcon(profile.Icon));
        if (string.Equals(_launcherState.ActiveProfileName, resolvedName, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ProfileName = resolvedName;
            _settings.ProfileInitial = string.IsNullOrWhiteSpace(profile.Initial) ? GetProfileInitial(resolvedName) : profile.Initial.Trim();
            _settings.ProfileIcon = NormalizeProfileIcon(profile.Icon);
            _settings.ProfileColor = string.IsNullOrWhiteSpace(profile.Color) ? "#5b9bf9" : profile.Color.Trim();
            SaveSettings();
        }
    }

    private void UpsertSavedProfile(string? name, string? initial, string? color, string? icon)
    {
        var profiles = GetSavedProfiles();
        var current = new ProfileItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim(),
            Initial = string.IsNullOrWhiteSpace(initial) ? GetProfileInitial(name) : initial.Trim(),
            Color = string.IsNullOrWhiteSpace(color) ? "#5b9bf9" : color.Trim(),
            Icon = NormalizeProfileIcon(icon)
        };
        if (profiles.Any(p => string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var existing = profiles.First(p => string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase));
            existing.Initial = current.Initial;
            existing.Color = current.Color;
            existing.Icon = current.Icon;
        }
        else
        {
            profiles.Add(current);
        }
        SaveLauncherState();
    }

    private string RemoveSavedProfile(string? name)
    {
        var resolvedName = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        if (string.IsNullOrWhiteSpace(resolvedName)) return _launcherState.ActiveProfileName ?? Environment.UserName;
        var profiles = GetSavedProfiles();
        profiles.RemoveAll(p => string.Equals(p.Name, resolvedName, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_launcherState.ActiveProfileName, resolvedName, StringComparison.OrdinalIgnoreCase) && profiles.Count > 0)
        {
            _launcherState.ActiveProfileName = profiles[0].Name;
        }
        if (profiles.Count == 0)
        {
            var fallback = new ProfileItem
            {
                Name = Environment.UserName,
                Initial = GetProfileInitial(Environment.UserName),
                Color = "#5b9bf9",
                Icon = ""
            };
            profiles.Add(fallback);
            _launcherState.ActiveProfileName = fallback.Name;
        }
        else
        {
            var changed = false;
            foreach (var profile in profiles)
            {
                var normalized = NormalizeProfileIcon(profile.Icon);
                if (!string.Equals(normalized, profile.Icon ?? "", StringComparison.Ordinal))
                {
                    profile.Icon = normalized;
                    changed = true;
                }
            }
            if (changed)
            {
                SaveLauncherState();
            }
        }
        SaveLauncherState();
        return _launcherState.ActiveProfileName ?? Environment.UserName;
    }

    private static string NormalizeProfileIcon(string? icon)
    {
        var value = (icon ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (string.Equals(value, "♂", StringComparison.Ordinal) || string.Equals(value, "♂️", StringComparison.Ordinal))
            return "";
        return value;
    }

    private async Task SendProfileStateToAllAsync()
    {
        foreach (var tab in _tabs)
        {
            if (tab.WebView?.CoreWebView2 == null) continue;
            await SendProfileStateAsync(tab.WebView.CoreWebView2);
        }
    }

    private async Task SendProfileStateAsync(CoreWebView2 webView)
    {
        try
        {
            var json = JsonSerializer.Serialize(GetProfileInfo());
            await webView.ExecuteScriptAsync($@"
                (function() {{
                    if (window.setProfileState) window.setProfileState({json});
                    if (window.setProfilesState) window.setProfilesState({json});
                }})();
            ");
        }
        catch { }
    }

    private async Task SwitchToProfileAsync(string? name, string? initial = null, string? color = null, string? icon = null)
    {
        var resolvedName = string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim();
        var normalizedIcon = NormalizeProfileIcon(icon);
        UpsertSavedProfile(resolvedName, initial, color, normalizedIcon);
        if (!string.IsNullOrWhiteSpace(normalizedIcon))
        {
            _settings.ProfileIcon = normalizedIcon;
        }
        if (!string.IsNullOrWhiteSpace(initial))
        {
            _settings.ProfileInitial = initial.Trim();
        }
        if (!string.IsNullOrWhiteSpace(color))
        {
            _settings.ProfileColor = color.Trim();
        }
        SaveSettings();
        _launcherState.ActiveProfileName = resolvedName;
        SaveLauncherState();

        var next = new MainWindow(false, null, resolvedName, true, false);
        next.Show();
        Close();
        await Task.CompletedTask;
    }

    private void ApplyProfileInPlace(ProfileItem profile)
    {
        var resolvedName = string.IsNullOrWhiteSpace(profile.Name) ? Environment.UserName : profile.Name.Trim();
        UpsertSavedProfile(resolvedName, profile.Initial, profile.Color, NormalizeProfileIcon(profile.Icon));
        _launcherState.ActiveProfileName = resolvedName;
        SaveLauncherState();
        ConfigureProfileStorage(resolvedName);
        LoadSettings();
        ApplyTheme();
        ApplyAllSettings();
        ApplyWindowPositionFromSettings();
        Title = $"YCB - {(_settings.ProfileName ?? resolvedName).Trim()}";
    }

    private async Task<bool> ShowProfileChooserAsync(bool switchOnSelect)
    {
        if (_isIncognito) return false;

        SaveSettings();
        SaveCurrentProfileToSavedList();

        var chooser = new ProfileChooserWindow(
            GetSavedProfiles(),
            _launcherState.ActiveProfileName ?? _settings.ProfileName,
            _isDarkMode,
            SaveProfileDefinition,
            RemoveSavedProfile)
        {
            Owner = this
        };

        var result = chooser.ShowDialog();
        if (result != true) return false;

        var chosen = chooser.SelectedProfile;
        if (chosen == null) return false;

        if (switchOnSelect)
        {
            await SwitchToProfileAsync(chosen.Name, chosen.Initial, chosen.Color, chosen.Icon);
        }
        else
        {
            ApplyProfileInPlace(chosen);
        }
        return true;
    }

    private void ApplySavedProfileDefaults()
    {
        var profile = GetSavedProfiles().FirstOrDefault(p =>
            string.Equals(p.Name, _settings.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile == null) return;
        if (string.IsNullOrWhiteSpace(_settings.ProfileInitial))
            _settings.ProfileInitial = string.IsNullOrWhiteSpace(profile.Initial) ? GetProfileInitial(profile.Name) : profile.Initial;
        if (string.IsNullOrWhiteSpace(_settings.ProfileIcon))
            _settings.ProfileIcon = NormalizeProfileIcon(profile.Icon);
        if (string.IsNullOrWhiteSpace(_settings.ProfileColor))
            _settings.ProfileColor = string.IsNullOrWhiteSpace(profile.Color) ? "#5b9bf9" : profile.Color;
    }

    private static string NormalizeCookieBrowser(string? browser)
    {
        var value = (browser ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "chrome" or "google-chrome" or "chromium" => "chrome",
            "edge" or "msedge" or "microsoft-edge" => "edge",
            "brave" or "brave-browser" => "brave",
            "firefox" or "ff" => "firefox",
            _ => "chrome"
        };
    }

    private static string GetChromiumUserDataRoot(string browser)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return browser switch
        {
            "edge" => IoPath.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            "brave" => IoPath.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            _ => IoPath.Combine(localAppData, "Google", "Chrome", "User Data")
        };
    }

    private static string GetFirefoxRoot()
    {
        return IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
    }

    private async Task SendCookieImportProfilesAsync(CoreWebView2 webView, string browser)
    {
        var normalizedBrowser = NormalizeCookieBrowser(browser);
        try
        {
            var profiles = await Task.Run(() => LoadCookieImportSourceProfiles(normalizedBrowser));
            var payload = JsonSerializer.Serialize(new
            {
                browser = normalizedBrowser,
                profiles = profiles.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    path = p.Path,
                    isDefault = p.IsDefault
                }).ToList()
            });
            await webView.ExecuteScriptAsync($"window.setCookieImportProfiles && window.setCookieImportProfiles({payload});");
        }
        catch (Exception ex)
        {
            var payload = JsonSerializer.Serialize(new
            {
                browser = normalizedBrowser,
                error = $"Could not read {normalizedBrowser} profiles: {ex.Message}",
                profiles = Array.Empty<object>()
            });
            await webView.ExecuteScriptAsync($"window.setCookieImportProfiles && window.setCookieImportProfiles({payload});");
        }
    }

    private async Task SendCookieImportStatusAsync(CoreWebView2 webView, string message, bool isError = false)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { text = message, isError });
            await webView.ExecuteScriptAsync($"window.setCookieImportStatus && window.setCookieImportStatus({payload});");
        }
        catch { }
    }

    private async Task ImportCookiesIntoCurrentProfileAsync(CoreWebView2 webView, string browser, string profileId)
    {
        var normalizedBrowser = NormalizeCookieBrowser(browser);
        var profiles = LoadCookieImportSourceProfiles(normalizedBrowser);
        var profile = profiles.FirstOrDefault(p =>
            string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Path, profileId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, profileId, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
        {
            await SendCookieImportStatusAsync(webView, $"Could not find the selected {normalizedBrowser} profile.", true);
            return;
        }

        await SendCookieImportStatusAsync(webView, $"Reading cookies from {profile.Name}...");
        List<ImportedCookie> cookies;
        try
        {
            cookies = await Task.Run(() => LoadCookiesFromSourceProfile(profile));
        }
        catch (Exception ex)
        {
            await SendCookieImportStatusAsync(webView, $"Could not read cookies: {ex.Message}", true);
            return;
        }

        if (cookies.Count == 0)
        {
            await SendCookieImportStatusAsync(webView, $"No cookies found in {profile.Name}.", true);
            return;
        }

        var cookieManager = webView.CookieManager;
        var imported = 0;
        var skipped = 0;
        foreach (var cookie in cookies)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Domain))
                {
                    skipped++;
                    continue;
                }
                if (cookie.HasExpiry && cookie.ExpiresUtc.HasValue && cookie.ExpiresUtc.Value <= DateTime.UtcNow.AddMinutes(-1))
                {
                    skipped++;
                    continue;
                }

                var domain = cookie.Domain.Trim();
                var path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path.Trim();
                var value = cookie.Value ?? "";
                var wroteCookie = false;
                foreach (var candidateDomain in GetCookieDomainsForImport(domain))
                {
                    try
                    {
                        cookieManager.DeleteCookiesWithDomainAndPath(cookie.Name.Trim(), candidateDomain, path);
                    }
                    catch { }

                    try
                    {
                        var webCookie = cookieManager.CreateCookie(cookie.Name.Trim(), value, candidateDomain, path);
                        if (cookie.HasExpiry && cookie.ExpiresUtc.HasValue && cookie.ExpiresUtc.Value > DateTime.MinValue)
                        {
                            webCookie.Expires = cookie.ExpiresUtc.Value;
                        }
                        webCookie.IsHttpOnly = cookie.IsHttpOnly;
                        webCookie.IsSecure = cookie.IsSecure;
                        webCookie.SameSite = cookie.SameSite == CoreWebView2CookieSameSiteKind.None && !cookie.IsSecure
                            ? CoreWebView2CookieSameSiteKind.Lax
                            : cookie.SameSite;
                        cookieManager.AddOrUpdateCookie(webCookie);
                        wroteCookie = true;
                    }
                    catch
                    {
                        continue;
                    }
                }
                if (wroteCookie) imported++;
                else skipped++;
            }
            catch
            {
                skipped++;
            }
        }

        if (imported > 0)
        {
            await RefreshOpenTabsAfterCookieImportAsync();
        }

        var summary = skipped > 0
            ? $"Imported {imported} cookies from {profile.Name}. {skipped} were skipped. Open tabs were refreshed."
            : $"Imported {imported} cookies from {profile.Name}. Open tabs were refreshed.";
        if (imported == 0)
        {
            summary = $"YCB could read cookies from {profile.Name}, but WebView2 rejected all of them. Close the source browser, try again, or use Firefox/Edge if Chrome has app-bound encrypted cookies.";
            await SendCookieImportStatusAsync(webView, summary, true);
            return;
        }
        await SendCookieImportStatusAsync(webView, summary);
    }

    private async Task RefreshOpenTabsAfterCookieImportAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var tab in _tabs)
                {
                    var source = tab.WebView.Source?.ToString() ?? "";
                    if (source.StartsWith("ycb://", StringComparison.OrdinalIgnoreCase)) continue;
                    if (tab.WebView.CoreWebView2 == null) continue;
                    if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        continue;

                    tab.WebView.CoreWebView2.Navigate(source);
                }
            });

            // Give the browser a moment to commit the imported cookies, then nudge any
            // visible pages once more so sign-in state is reflected immediately.
            await Task.Delay(300);
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var tab in _tabs)
                {
                    var source = tab.WebView.Source?.ToString() ?? "";
                    if (source.StartsWith("ycb://", StringComparison.OrdinalIgnoreCase)) continue;
                    if (tab.WebView.CoreWebView2 == null) continue;
                    if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        continue;

                    tab.WebView.CoreWebView2.Reload();
                }
            });
        }
        catch { }
    }

    private sealed class ImportedCookie
    {
        public string Domain { get; set; } = "";
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Path { get; set; } = "/";
        public DateTime? ExpiresUtc { get; set; }
        public bool HasExpiry { get; set; }
        public bool IsHttpOnly { get; set; }
        public bool IsSecure { get; set; }
        public CoreWebView2CookieSameSiteKind SameSite { get; set; } = CoreWebView2CookieSameSiteKind.None;
    }

    private static IEnumerable<string> GetCookieDomainsForImport(string domain)
    {
        var cleaned = (domain ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            yield break;

        yield return cleaned;

        var stripped = cleaned.TrimStart('.');
        if (!string.Equals(stripped, cleaned, StringComparison.Ordinal))
            yield return stripped;
    }

    private List<CookieImportSourceProfile> LoadCookieImportSourceProfiles(string browser)
    {
        return browser switch
        {
            "firefox" => LoadFirefoxSourceProfiles(),
            _ => LoadChromiumSourceProfiles(browser)
        };
    }

    private List<CookieImportSourceProfile> LoadChromiumSourceProfiles(string browser)
    {
        var root = GetChromiumUserDataRoot(browser);
        var result = new List<CookieImportSourceProfile>();
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!ChromiumProfileHasCookies(dir)) continue;
            var folderName = IoPath.GetFileName(dir);
            var friendly = TryGetChromiumProfileName(dir) ?? folderName;
            result.Add(new CookieImportSourceProfile
            {
                Browser = browser,
                RootPath = root,
                Path = dir,
                Id = folderName,
                Name = friendly,
                IsDefault = string.Equals(folderName, "Default", StringComparison.OrdinalIgnoreCase)
            });
        }

        if (result.Count == 0 && ChromiumProfileHasCookies(root))
        {
            var friendly = TryGetChromiumProfileName(root) ?? "Default";
            result.Add(new CookieImportSourceProfile
            {
                Browser = browser,
                RootPath = root,
                Path = root,
                Id = "Default",
                Name = friendly,
                IsDefault = true
            });
        }

        return result
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ChromiumProfileHasCookies(string profilePath)
    {
        return File.Exists(IoPath.Combine(profilePath, "Network", "Cookies"))
            || File.Exists(IoPath.Combine(profilePath, "Cookies"));
    }

    private static string? TryGetChromiumProfileName(string profilePath)
    {
        var preferences = IoPath.Combine(profilePath, "Preferences");
        if (!File.Exists(preferences)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(preferences, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("profile", out var profileEl) &&
                profileEl.TryGetProperty("name", out var nameEl))
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch { }
        return null;
    }

    private List<CookieImportSourceProfile> LoadFirefoxSourceProfiles()
    {
        var root = GetFirefoxRoot();
        var result = new List<CookieImportSourceProfile>();
        var profilesIni = IoPath.Combine(root, "profiles.ini");

        if (File.Exists(profilesIni))
        {
            foreach (var profile in ParseFirefoxProfilesIni(profilesIni, root))
            {
                if (File.Exists(IoPath.Combine(profile.Path, "cookies.sqlite")))
                {
                    result.Add(profile);
                }
            }
        }

        if (result.Count == 0)
        {
            var profilesRoot = IoPath.Combine(root, "Profiles");
            if (Directory.Exists(profilesRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(profilesRoot))
                {
                    var db = IoPath.Combine(dir, "cookies.sqlite");
                    if (!File.Exists(db)) continue;
                    var name = IoPath.GetFileName(dir);
                    result.Add(new CookieImportSourceProfile
                    {
                        Browser = "firefox",
                        RootPath = root,
                        Path = dir,
                        Id = name,
                        Name = name,
                        IsDefault = false
                    });
                }
            }
        }

        return result
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<CookieImportSourceProfile> ParseFirefoxProfilesIni(string profilesIni, string firefoxRoot)
    {
        string? section = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var yieldProfiles = new List<CookieImportSourceProfile>();

        void Flush()
        {
            if (section == null) return;
            if (!values.TryGetValue("Path", out var pathValue) || string.IsNullOrWhiteSpace(pathValue)) return;
            values.TryGetValue("Name", out var nameValue);
            values.TryGetValue("IsRelative", out var relativeValue);
            values.TryGetValue("Default", out var defaultValue);

            var isRelative = !string.Equals(relativeValue?.Trim(), "0", StringComparison.OrdinalIgnoreCase);
            var fullPath = isRelative
                ? IoPath.GetFullPath(IoPath.Combine(firefoxRoot, pathValue.Trim()))
                : IoPath.GetFullPath(pathValue.Trim());
            var displayName = string.IsNullOrWhiteSpace(nameValue) ? IoPath.GetFileName(fullPath) : nameValue.Trim();

            yieldProfiles.Add(new CookieImportSourceProfile
            {
                Browser = "firefox",
                RootPath = firefoxRoot,
                Path = fullPath,
                Id = fullPath,
                Name = displayName,
                IsDefault = string.Equals(defaultValue?.Trim(), "1", StringComparison.OrdinalIgnoreCase)
            });
        }
        foreach (var rawLine in File.ReadAllLines(profilesIni, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)) continue;
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                Flush();
                section = line.Trim('[', ']');
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();
            values[key] = value;
        }
        Flush();
        return yieldProfiles;
    }

    private List<ImportedCookie> LoadCookiesFromSourceProfile(CookieImportSourceProfile profile)
    {
        return profile.Browser switch
        {
            "firefox" => LoadFirefoxCookies(profile),
            _ => LoadChromiumCookies(profile)
        };
    }

    private List<ImportedCookie> LoadChromiumCookies(CookieImportSourceProfile profile)
    {
        var result = new List<ImportedCookie>();
        var localStatePath = IoPath.Combine(profile.RootPath, "Local State");
        var cookiesPath = IoPath.Combine(profile.Path, "Network", "Cookies");
        if (!File.Exists(cookiesPath))
        {
            cookiesPath = IoPath.Combine(profile.Path, "Cookies");
        }
        if (!File.Exists(localStatePath) || !File.Exists(cookiesPath)) return result;

        string? localStateTemp = null;
        string? cookiesTemp = null;
        byte[]? masterKey = null;
        try
        {
            localStateTemp = CopyFileToTemp(localStatePath);
            cookiesTemp = CopyFileToTemp(cookiesPath);
            masterKey = ReadChromiumMasterKey(localStateTemp);

            using (var conn = new SqliteConnection($"Data Source={cookiesTemp};Mode=ReadOnly;Cache=Shared"))
            {
                conn.Open();
                var cookieColumns = GetSqliteTableColumns(conn, "cookies");
                var unsupportedEncryptedCookies = 0;
                var sameSiteColumn = cookieColumns.Contains("samesite")
                    ? "samesite"
                    : cookieColumns.Contains("same_site")
                        ? "same_site"
                        : null;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sameSiteColumn == null
                    ? @"
SELECT
    host_key,
    name,
    path,
    value,
    encrypted_value,
    expires_utc,
    has_expires,
    is_httponly,
    is_secure
FROM cookies"
                    : $@"
SELECT
    host_key,
    name,
    path,
    value,
    encrypted_value,
    expires_utc,
    has_expires,
    is_httponly,
    is_secure,
    {sameSiteColumn}
FROM cookies";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var domain = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var path = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var value = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    var encrypted = reader.IsDBNull(4) ? Array.Empty<byte>() : reader.GetFieldValue<byte[]>(4);
                    var expiresUtcRaw = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5));
                    var hasExpires = !reader.IsDBNull(6) && Convert.ToInt32(reader.GetValue(6)) != 0;
                    var isHttpOnly = !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7)) != 0;
                    var isSecure = !reader.IsDBNull(8) && Convert.ToInt32(reader.GetValue(8)) != 0;
                    var sameSite = reader.FieldCount > 9 && !reader.IsDBNull(9)
                        ? MapSameSiteValue(Convert.ToInt32(reader.GetValue(9)))
                        : CoreWebView2CookieSameSiteKind.None;

                    var hasEncryptedValue = encrypted.Length > 0;
                    if (IsChromiumAppBoundEncryptedCookie(encrypted))
                    {
                        unsupportedEncryptedCookies++;
                        continue;
                    }

                    var finalValue = !string.IsNullOrEmpty(value)
                        ? value
                        : DecryptChromiumCookieValue(encrypted, masterKey);

                    if (hasEncryptedValue && string.IsNullOrEmpty(finalValue))
                        continue;

                    if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name)) continue;
                    result.Add(new ImportedCookie
                    {
                        Domain = domain,
                        Name = name,
                        Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
                        Value = finalValue,
                        HasExpiry = hasExpires || expiresUtcRaw > 0,
                        ExpiresUtc = expiresUtcRaw > 0 ? ChromiumUtcToDateTime(expiresUtcRaw) : null,
                        IsHttpOnly = isHttpOnly,
                        IsSecure = isSecure,
                        SameSite = sameSite
                    });
                }

                if (result.Count == 0 && unsupportedEncryptedCookies > 0)
                {
                    throw new InvalidOperationException(
                        $"This Chromium profile uses app-bound encrypted cookies (v20). YCB cannot safely decrypt those cookies directly, so importing them would create broken sign-ins.");
                }
            }
        }
        finally
        {
            SafeDeleteTempFile(localStateTemp);
            SafeDeleteTempFile(cookiesTemp);
        }

        return result;
    }

    private List<ImportedCookie> LoadFirefoxCookies(CookieImportSourceProfile profile)
    {
        var result = new List<ImportedCookie>();
        var dbPath = IoPath.Combine(profile.Path, "cookies.sqlite");
        if (!File.Exists(dbPath)) return result;

        string? dbTemp = null;
        try
        {
            dbTemp = CopyFileToTemp(dbPath);
            using (var conn = new SqliteConnection($"Data Source={dbTemp};Mode=ReadOnly;Cache=Shared"))
            {
                conn.Open();
                var cookieColumns = GetSqliteTableColumns(conn, "moz_cookies");
                var sameSiteColumn = cookieColumns.Contains("sameSite")
                    ? "sameSite"
                    : cookieColumns.Contains("samesite")
                        ? "samesite"
                        : null;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sameSiteColumn == null
                    ? "SELECT name, value, host, path, expiry, isSecure, isHttpOnly FROM moz_cookies"
                    : $"SELECT name, value, host, path, expiry, isSecure, isHttpOnly, {sameSiteColumn} FROM moz_cookies";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var host = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var path = reader.IsDBNull(3) ? "/" : reader.GetString(3);
                    var expiry = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                    var isSecure = !reader.IsDBNull(5) && reader.GetBoolean(5);
                    var isHttpOnly = !reader.IsDBNull(6) && reader.GetBoolean(6);
                    var sameSite = reader.FieldCount > 7 && !reader.IsDBNull(7)
                        ? MapSameSiteValue(Convert.ToInt32(reader.GetValue(7)))
                        : CoreWebView2CookieSameSiteKind.None;

                    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(name)) continue;
                    result.Add(new ImportedCookie
                    {
                        Domain = host,
                        Name = name,
                        Value = value ?? "",
                        Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
                        HasExpiry = expiry > 0,
                        ExpiresUtc = expiry > 0 ? FirefoxUtcToDateTime(expiry) : null,
                        IsSecure = isSecure,
                        IsHttpOnly = isHttpOnly,
                        SameSite = sameSite
                    });
                }
            }
        }
        finally
        {
            SafeDeleteTempFile(dbTemp);
        }

        return result;
    }

    private static byte[] ReadChromiumMasterKey(string localStatePath)
    {
        var json = File.ReadAllText(localStatePath, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
            !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyEl))
        {
            throw new InvalidOperationException("Chromium Local State is missing the encryption key.");
        }

        var encryptedKey = encryptedKeyEl.GetString();
        if (string.IsNullOrWhiteSpace(encryptedKey))
            throw new InvalidOperationException("Chromium encryption key is empty.");

        var keyData = Convert.FromBase64String(encryptedKey);
        if (keyData.Length <= 5)
            throw new InvalidOperationException("Chromium encryption key is invalid.");

        var dpapiKey = keyData.Skip(5).ToArray();
        return ProtectedData.Unprotect(dpapiKey, null, DataProtectionScope.CurrentUser);
    }

    private static string DecryptChromiumCookieValue(byte[] encryptedValue, byte[] masterKey)
    {
        if (encryptedValue == null || encryptedValue.Length == 0) return "";
        try
        {
            if (encryptedValue.Length > 3)
            {
                var prefix = Encoding.ASCII.GetString(encryptedValue, 0, 3);
                if (prefix == "v10" || prefix == "v11")
                {
                    if (encryptedValue.Length < 3 + 12 + 16) return "";
                    var nonce = encryptedValue.Skip(3).Take(12).ToArray();
                    var tag = encryptedValue.Skip(encryptedValue.Length - 16).Take(16).ToArray();
                    var cipherText = encryptedValue.Skip(15).Take(encryptedValue.Length - 15 - 16).ToArray();
                    var plain = new byte[cipherText.Length];
                    using var aes = new AesGcm(masterKey, 16);
                    aes.Decrypt(nonce, cipherText, tag, plain, null);
                    return Encoding.UTF8.GetString(plain).TrimEnd('\0');
                }
            }
        }
        catch { }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encryptedValue, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    private static bool IsChromiumAppBoundEncryptedCookie(byte[] encryptedValue)
    {
        if (encryptedValue == null || encryptedValue.Length < 3) return false;
        return Encoding.ASCII.GetString(encryptedValue, 0, 3) == "v20";
    }

    private static DateTime ChromiumUtcToDateTime(long microseconds)
    {
        if (microseconds <= 0) return DateTime.MinValue;
        try
        {
            return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(microseconds * 10);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime FirefoxUtcToDateTime(long seconds)
    {
        if (seconds <= 0) return DateTime.MinValue;
        try
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static CoreWebView2CookieSameSiteKind MapSameSiteValue(int raw)
    {
        return raw switch
        {
            1 => CoreWebView2CookieSameSiteKind.Lax,
            2 => CoreWebView2CookieSameSiteKind.Strict,
            _ => CoreWebView2CookieSameSiteKind.None
        };
    }

    private static HashSet<string> GetSqliteTableColumns(SqliteConnection conn, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
                columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string CopyFileToTemp(string sourcePath)
    {
        var tempPath = IoPath.Combine(IoPath.GetTempPath(), $"ycb-cookie-{Guid.NewGuid():N}-{IoPath.GetFileName(sourcePath)}");
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dest = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(dest);
        return tempPath;
    }

    private static void SafeDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    // ── Win32 native drag ─────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int WM_SYSCOMMAND   = 0x0112;
    private const int SC_MINIMIZE      = 0xF020;
    private const int HTCAPTION = 2;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const int GWL_STYLE      = -16;
    private const int WS_CAPTION     = 0x00C00000;
    private const int WS_THICKFRAME  = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int SW_MINIMIZE    = 6;
    private const int SW_MAXIMIZE    = 3;
    private const int SW_RESTORE     = 9;
    private const int SW_HIDE        = 0;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const byte VK_RETURN = 0x0D;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // OnSourceInitialized: SingleBorderWindow already has WS_CAPTION/THICKFRAME
    // DWM animates minimize/maximize/restore natively via ShowWindow P/Invoke
    protected override void OnSourceInitialized(EventArgs e)
    {
        // Add our hook BEFORE base — hooks are called in addition order (FIFO).
        // If we add after base, WPF's ChromeWorker hook runs first and sets handled=true,
        // meaning our hook never runs for messages like WM_NCCALCSIZE.
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WndProc);

        base.OnSourceInitialized(e);

        // Intercept F11 at the thread message level — fires even when WebView2 has focus.
        // WPF's KeyDown never fires for keys consumed by WebView2 (Win32 child HWND).
        ComponentDispatcher.ThreadPreprocessMessage += (ref MSG msg, ref bool handled) =>
        {
            const int WM_KEYDOWN = 0x0100;
            const int VK_F11    = 0x7A;
            if (!handled && msg.message == WM_KEYDOWN && (int)msg.wParam == VK_F11)
            {
                ToggleFullscreen();
                handled = true;
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    private const int WM_GETMINMAXINFO         = 0x0024;
    private const int SC_MAXIMIZE              = 0xF030;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND        = 1;
    private const int DWMWCP_DEFAULT           = 0;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private const uint SWP_FRAMECHANGED  = 0x0020;
    private const uint SWP_NOSIZE        = 0x0001;
    private const uint SWP_NOMOVE        = 0x0002;
    private const uint SWP_NOACTIVATE    = 0x0010;
    private static readonly IntPtr HWND_TOPMOST    = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST  = new IntPtr(-2);

    private void SetCornerPreference(int pref)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        else if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MAXIMIZE)
        {
            // Aero snap / Win+Up / system maximize — set DONOTROUND before Windows extends the window
            SetCornerPreference(DWMWCP_DONOTROUND);
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi     = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var work = mi.rcWork;
            var mon  = mi.rcMonitor;
            mmi.ptMaxPosition.x = Math.Abs(work.Left - mon.Left);
            mmi.ptMaxPosition.y = Math.Abs(work.Top  - mon.Top);
            mmi.ptMaxSize.x     = Math.Abs(work.Right  - work.Left);
            mmi.ptMaxSize.y     = Math.Abs(work.Bottom - work.Top);
            mmi.ptMinTrackSize.x = 400;
            mmi.ptMinTrackSize.y = 300;
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void BeginNativeDrag()
    {
        ReleaseCapture();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    // Event handlers
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
        }
        else
        {
            // Native drag handles maximized-restore-and-drag automatically
            BeginNativeDrag();
        }
    }
    
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        // ShowWindow goes straight to Win32 — DWM plays native swoop to taskbar
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, SW_MINIMIZE);
    }
    
    private void ManualMaximize()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        var src = PresentationSource.FromVisual(this);
        double scaleX = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double scaleY = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        double toL = mi.rcWork.Left                      * scaleX;
        double toT = mi.rcWork.Top                       * scaleY;
        double toW = (mi.rcWork.Right  - mi.rcWork.Left) * scaleX;
        double toH = (mi.rcWork.Bottom - mi.rcWork.Top)  * scaleY;
        _manuallyMaximized = true;
        ShowRestoreIcon();
        AnimateBounds(Left, Top, Width, Height, toL, toT, toW, toH);
    }

    private void AnimateBounds(double fL, double fT, double fW, double fH,
                                double tL, double tT, double tW, double tH)
    {
        var dur  = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Anim(DependencyProperty dp, double from, double to) =>
            BeginAnimation(dp, new DoubleAnimation(from, to, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        Anim(Window.LeftProperty,            fL, tL);
        Anim(Window.TopProperty,             fT, tT);
        Anim(FrameworkElement.WidthProperty,  fW, tW);
        Anim(FrameworkElement.HeightProperty, fH, tH);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        timer.Tick += (s, _) =>
        {
            timer.Stop();
            BeginAnimation(Window.LeftProperty, null);
            BeginAnimation(Window.TopProperty, null);
            BeginAnimation(FrameworkElement.WidthProperty, null);
            BeginAnimation(FrameworkElement.HeightProperty, null);
            Left = tL; Top = tT; Width = tW; Height = tH;
        };
        timer.Start();
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (!_manuallyMaximized)
        {
            _savedLeft = Left; _savedTop = Top;
            _savedWidth = Width; _savedHeight = Height;
            _savedWindowState = WindowState;
            ManualMaximize();
        }
        else
        {
            _manuallyMaximized = false;
            ShowMaximizeIcon();
            if (_savedWidth > 0 && _savedHeight > 0)
                AnimateBounds(Left, Top, Width, Height, _savedLeft, _savedTop, _savedWidth, _savedHeight);
        }
    }
    
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }
    
    private async void AddTab_Click(object sender, RoutedEventArgs e)
    {
        await CreateTab(_settings.HomePage ?? "ycb://newtab");
    }
    
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].WebView.CoreWebView2?.GoBack();
        }
    }
    
    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].WebView.CoreWebView2?.GoForward();
        }
    }
    
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var webView = _tabs[_activeTabIndex].WebView;
            if (webView.CoreWebView2 != null)
            {
                _loadingTabs.Add(webView);
                UpdateRefreshButton();
                webView.CoreWebView2.Reload();
            }
        }
    }

    private void UpdateRefreshButton()
    {
        var shouldSpin = _activeTabIndex >= 0 &&
                         _activeTabIndex < _tabs.Count &&
                         _loadingTabs.Contains(_tabs[_activeTabIndex].WebView);

        if (shouldSpin == _refreshSpinnerActive) return;
        _refreshSpinnerActive = shouldSpin;

        if (RefreshIconCanvas?.RenderTransform is RotateTransform rotate)
        {
            if (shouldSpin)
            {
                var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
                RefreshBtn.ToolTip = "Loading page";
            }
            else
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                rotate.Angle = 0;
                RefreshBtn.ToolTip = "Reload page";
            }
        }
    }

    private static string GetFriendlyNavigationError(string errName, int errCode, string url)
    {
        if (errName.Contains("BlockedByClient", StringComparison.OrdinalIgnoreCase))
        {
            return "If you suspect this is wrong, uBlock Origin (our ad blocker) may be flagging this. Use the Whitelist this website button, or temporarily disable your ad blocker with the stickman button.";
        }
        if (errName.Contains("NameNotResolved", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("HostNameNotResolved", StringComparison.OrdinalIgnoreCase))
        {
            return $"Check the address for {GetHostOnly(url)} and try again.";
        }
        if (errName.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return "The site took too long to respond.";
        if (errName.Contains("Connection", StringComparison.OrdinalIgnoreCase))
            return "Check your connection and try again.";
        return $"YCB could not load this page. Error {errCode}.";
    }

    private static bool ShouldShowNavigationError(string errName, int errCode, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (url.Contains("google.com/sorry", StringComparison.OrdinalIgnoreCase))
            return false;

        if (errName.Contains("BlockedByClient", StringComparison.OrdinalIgnoreCase))
            return true;

        if (errName.Contains("NameNotResolved", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("HostNameNotResolved", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("Ssl", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("Certificate", StringComparison.OrdinalIgnoreCase) ||
            errName.Contains("Secure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Avoid converting ordinary HTTP failures and transient challenge pages into our
        // custom error UI when the page itself may still be usable.
        return false;
    }

    private string BuildNavigationErrorPage(string title, string detail, string failedUrl)
    {
        var adblockHint = detail.Contains("uBlock Origin", StringComparison.OrdinalIgnoreCase);
        var safeUrl = System.Net.WebUtility.HtmlEncode(failedUrl);
        var safeUrlJs = JsonSerializer.Serialize(failedUrl);
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeDetail = System.Net.WebUtility.HtmlEncode(detail);
        var safeAdblockHint = System.Net.WebUtility.HtmlEncode(
            "uBlock Origin (our ad blocker) may be flagging this. Use the Whitelist this website button, or temporarily disable it with the grey/green stickman button.");
        var themeBg = _isDarkMode ? "#202124" : "#f8f9fa";
        var cardBg = _isDarkMode ? "#2d2e30" : "#ffffff";
        var text = _isDarkMode ? "#e8eaed" : "#202124";
        var muted = _isDarkMode ? "#9aa0a6" : "#5f6368";
        var accent = _isDarkMode ? "#8ab4f8" : "#1a73e8";
        return $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <title>{safeTitle}</title>
  <style>
    html,body{{height:100%;margin:0;background:{themeBg};color:{text};font-family:Segoe UI,system-ui,sans-serif;}}
    body{{display:flex;align-items:center;justify-content:center;padding:32px;box-sizing:border-box;}}
    .card{{max-width:680px;width:100%;background:{cardBg};border:1px solid rgba(127,127,127,.15);border-radius:20px;padding:30px 32px;box-shadow:0 20px 60px rgba(0,0,0,.18)}}
    .chip{{display:inline-flex;align-items:center;gap:8px;padding:6px 10px;border-radius:999px;background:rgba(127,127,127,.08);color:{muted};font-size:12px;margin-bottom:14px;}}
    .dot{{width:10px;height:10px;border-radius:50%;background:{accent};box-shadow:0 0 0 4px rgba(138,180,248,.12);}}
    h1{{margin:0 0 10px;font-size:30px;font-weight:600;letter-spacing:-.01em;}}
    p{{margin:0 0 14px;line-height:1.5;color:{muted};font-size:14px;}}
    .url{{font-family:Consolas,monospace;font-size:12px;word-break:break-all;padding:10px 12px;background:rgba(127,127,127,.08);border-radius:10px;color:{text};margin-bottom:18px;}}
    .hint{{display:flex;gap:12px;align-items:flex-start;padding:12px 14px;border-radius:14px;background:rgba(138,180,248,.08);border:1px solid rgba(138,180,248,.18);color:{text};margin-bottom:18px;}}
    .hint-badge{{flex:0 0 auto;width:34px;height:34px;border-radius:50%;background:linear-gradient(135deg,#8e8e8e 0%,#5fd65f 100%);display:flex;align-items:center;justify-content:center;color:#fff;font-weight:700;font-size:14px;}}
    .hint-title{{font-size:13px;font-weight:700;margin:0 0 4px;}}
    .hint-text{{margin:0;font-size:13px;line-height:1.5;color:{muted};}}
    .actions{{display:flex;gap:10px;flex-wrap:wrap;margin-top:8px;}}
    button,a{{appearance:none;border:none;border-radius:999px;padding:10px 16px;font-size:13px;font-weight:600;cursor:pointer;text-decoration:none;}}
    .primary{{background:{accent};color:white;}}
    .ghost{{background:transparent;color:{text};border:1px solid rgba(127,127,127,.3);}}
    .whitelist{{background:rgba(129,201,149,.16);color:{text};border:1px solid rgba(129,201,149,.40);}}
  </style>
</head>
<body>
    <div class=""card"">
    <div class=""chip""><span class=""dot""></span>Page unavailable</div>
    <h1>{safeTitle}</h1>
    <p>{safeDetail}</p>
    {(adblockHint ? "<p style=\"margin:-2px 0 14px;color:" + muted + ";font-weight:600;\">Updated adblock help: use the Whitelist this website button below.</p>" : "")}
    <div class=""url"">{safeUrl}</div>
    {(adblockHint ? $@"<div class=""hint"">
      <div class=""hint-badge"">◐</div>
      <div>
        <div class=""hint-title"">Ad blocker tip</div>
        <p class=""hint-text"">{safeAdblockHint}</p>
      </div>
    </div>" : "")}
    <div class=""actions"">
      <button class=""primary"" onclick=""window.location.reload()"">Try again</button>
      <button class=""ghost"" onclick=""history.back()"">Go back</button>
      {(adblockHint ? $@"<button class=""whitelist"" onclick=""window.chrome && window.chrome.webview && window.chrome.webview.postMessage(JSON.stringify({{type:'adblock:whitelist',url:{safeUrlJs}}}))"">Whitelist this website</button>" : "")}
    </div>
  </div>
</body>
</html>";
    }
    
    // ── P/Invoke: enumerate audio input (microphone) devices via winmm ──
    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveInGetDevCaps(int id, ref WAVEINCAPS2 c, int sz);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WAVEINCAPS2
    {
        public ushort wMid, wPid; public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats; public ushort wChannels, wReserved1;
    }
    private static List<string> GetMicrophoneDevices()
    {
        var list = new List<string>();
        try { int n = waveInGetNumDevs(); for (int i = 0; i < n; i++) { var c = new WAVEINCAPS2(); if (waveInGetDevCaps(i, ref c, Marshal.SizeOf(c)) == 0) list.Add(c.szPname); } } catch { }
        if (list.Count == 0) list.Add("Default microphone");
        return list;
    }
    private static List<string> GetCameraDevices() => new() { "Default camera" };

    private static string GetPermissionName(CoreWebView2PermissionKind k) => k switch
    {
        CoreWebView2PermissionKind.Camera       => "camera",
        CoreWebView2PermissionKind.Microphone   => "microphone",
        CoreWebView2PermissionKind.Geolocation  => "location",
        CoreWebView2PermissionKind.Notifications => "notifications",
        CoreWebView2PermissionKind.ClipboardRead => "clipboard",
        _ => k.ToString().ToLower()
    };

    private Dictionary<string, Dictionary<string, string>> LoadSitePermissions()
    {
        try { if (File.Exists(_permissionsPath)) return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_permissionsPath)) ?? new(); } catch { }
        return new();
    }
    private void SaveSitePermission(string origin, string perm, string state)
    {
        try { var all = LoadSitePermissions(); if (!all.ContainsKey(origin)) all[origin] = new(); all[origin][perm] = state; File.WriteAllText(_permissionsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
    private void RemoveSitePermission(string origin, string perm)
    {
        try { var all = LoadSitePermissions(); if (all.TryGetValue(origin, out var d)) d.Remove(perm); File.WriteAllText(_permissionsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    private void ShowPermissionDialog(WebView2 webView, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.Handled = true;
        var deferral = e.GetDeferral();

        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var kind    = e.PermissionKind;
                var uri     = new Uri(e.Uri);
                var origin  = uri.Host;
                var permKey = GetPermissionName(kind);

                bool hasPicker = kind == CoreWebView2PermissionKind.Camera || kind == CoreWebView2PermissionKind.Microphone;
                var devices = hasPicker
                    ? (kind == CoreWebView2PermissionKind.Microphone ? GetMicrophoneDevices() : GetCameraDevices())
                    : new List<string>();

                string subtitle = kind switch
                {
                    CoreWebView2PermissionKind.Microphone   => $"Use available microphones ({devices.Count})",
                    CoreWebView2PermissionKind.Camera       => $"Use available cameras ({devices.Count})",
                    CoreWebView2PermissionKind.Geolocation  => "Know your location",
                    CoreWebView2PermissionKind.Notifications => "Show notifications",
                    CoreWebView2PermissionKind.ClipboardRead => "Read your clipboard",
                    _ => $"Access {permKey}"
                };

                string iconData = kind switch
                {
                    CoreWebView2PermissionKind.Camera       => "M15 8v8H3V8h2l1-2h6l1 2h2zm-6 6a3 3 0 100-6 3 3 0 000 6z",
                    CoreWebView2PermissionKind.Microphone   => "M12 14a3 3 0 003-3V5a3 3 0 00-6 0v6a3 3 0 003 3zm5-3a5 5 0 01-10 0H5a7 7 0 0014 0h-2zm-5 5v-3",
                    CoreWebView2PermissionKind.Geolocation  => "M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5a2.5 2.5 0 010-5 2.5 2.5 0 010 5z",
                    CoreWebView2PermissionKind.Notifications => "M12 22c1.1 0 2-.9 2-2h-4c0 1.1.9 2 2 2zm6-6v-5c0-3.07-1.64-5.64-4.5-6.32V4c0-.83-.67-1.5-1.5-1.5S10.5 3.17 10.5 4v.68C7.63 5.36 6 7.92 6 11v5l-2 2v1h16v-1l-2-2z",
                    _ => "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z"
                };

                var accent  = (Color)ColorConverter.ConvertFromString("#8ab4f8")!;
                var bgDark  = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#292a2d" : "#ffffff")!;
                var bgDeep  = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#202124" : "#f8f9fa")!;
                var border1 = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c4043" : "#dfe1e5")!;
                var textPri = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!;
                var textSub = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!;

                var popup = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true,
                    Background = Brushes.Transparent, ShowInTaskbar = false,
                    Topmost = true, Owner = this,
                    Width = 320, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                var pt = SecurityIconCanvas.PointToScreen(new Point(0, SecurityIconCanvas.ActualHeight));
                popup.Left = pt.X - 20; popup.Top = pt.Y + 6;

                var rootBorder = new Border
                {
                    Background = new SolidColorBrush(bgDark), CornerRadius = new CornerRadius(12),
                    BorderBrush = new SolidColorBrush(border1), BorderThickness = new Thickness(1),
                    Margin = new Thickness(8),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
                };

                var outerStack = new StackPanel();

                // Title row
                var titleGrid = new Grid { Margin = new Thickness(16, 14, 12, 10) };
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titleGrid.Children.Add(new TextBlock { Text = $"{origin} wants to", Foreground = new SolidColorBrush(textPri), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var xBtn = new Button { Content = "✕", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(textSub), FontSize = 13, Width = 26, Height = 26, Cursor = Cursors.Hand, Padding = new Thickness(0) };
                Grid.SetColumn(xBtn, 1); titleGrid.Children.Add(xBtn);
                outerStack.Children.Add(titleGrid);

                // Subtitle row
                var subRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 14) };
                subRow.Children.Add(new WpfPath { Data = Geometry.Parse(iconData), Fill = new SolidColorBrush(textSub), Width = 16, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                subRow.Children.Add(new TextBlock { Text = subtitle, Foreground = new SolidColorBrush(textPri), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                outerStack.Children.Add(subRow);

                // Device picker
                if (hasPicker && devices.Count > 0)
                {
                    var pickerBorder = new Border
                    {
                        Background = new SolidColorBrush(bgDeep), CornerRadius = new CornerRadius(8),
                        BorderBrush = new SolidColorBrush(border1), BorderThickness = new Thickness(1),
                        Margin = new Thickness(14, 0, 14, 14), Padding = new Thickness(12, 10, 12, 10)
                    };
                    var pickerStack = new StackPanel();

                    // Icon + toggle row
                    var iconToggleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
                    iconToggleRow.Children.Add(new WpfPath { Data = Geometry.Parse(iconData), Fill = new SolidColorBrush(accent), Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    // Toggle switch (visual, always ON)
                    var toggleGrid = new Grid { Width = 36, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                    toggleGrid.Children.Add(new Border { Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(10) });
                    toggleGrid.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) });
                    iconToggleRow.Children.Add(toggleGrid);
                    pickerStack.Children.Add(iconToggleRow);

                    var combo = new ComboBox { FontSize = 13, Height = 32, Foreground = new SolidColorBrush(textPri), Background = new SolidColorBrush(bgDark), BorderBrush = new SolidColorBrush(border1) };
                    foreach (var d in devices) combo.Items.Add(d);
                    combo.SelectedIndex = 0;
                    pickerStack.Children.Add(combo);

                    pickerBorder.Child = pickerStack;
                    outerStack.Children.Add(pickerBorder);
                }

                // Separator
                outerStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(border1) });

                // Helper: create a pill button
                Border MakePill(string label) {
                    var b = new Border
                    {
                        CornerRadius = new CornerRadius(20), Margin = new Thickness(14, 6, 14, 6),
                        Padding = new Thickness(0, 11, 0, 11), Cursor = Cursors.Hand,
                        Background = new SolidColorBrush(Color.FromArgb(30, accent.R, accent.G, accent.B)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(80, accent.R, accent.G, accent.B)),
                        BorderThickness = new Thickness(1)
                    };
                    b.Child = new TextBlock { Text = label, Foreground = new SolidColorBrush(accent), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
                    b.MouseEnter  += (s, _) => b.Background = new SolidColorBrush(Color.FromArgb(60, accent.R, accent.G, accent.B));
                    b.MouseLeave  += (s, _) => b.Background = new SolidColorBrush(Color.FromArgb(30, accent.R, accent.G, accent.B));
                    return b;
                }

                var allowAlways = MakePill("Allow while visiting the site");
                var allowOnce   = MakePill("Allow this time");
                var neverAllow  = MakePill("Never allow");
                // Give "Never allow" a red tint
                neverAllow.Background = new SolidColorBrush(Color.FromArgb(25, 0xf2, 0x8b, 0x82));
                neverAllow.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0xf2, 0x8b, 0x82));
                ((TextBlock)neverAllow.Child).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
                neverAllow.MouseEnter += (s, _) => neverAllow.Background = new SolidColorBrush(Color.FromArgb(55, 0xf2, 0x8b, 0x82));
                neverAllow.MouseLeave += (s, _) => neverAllow.Background = new SolidColorBrush(Color.FromArgb(25, 0xf2, 0x8b, 0x82));
                neverAllow.Margin = new Thickness(14, 6, 14, 14);

                outerStack.Children.Add(allowAlways);
                outerStack.Children.Add(allowOnce);
                outerStack.Children.Add(neverAllow);

                rootBorder.Child = outerStack;
                popup.Content = rootBorder;

                void CloseWith(CoreWebView2PermissionState state, bool save)
                {
                    e.State = state;
                    if (save) SaveSitePermission(origin, permKey, state == CoreWebView2PermissionState.Allow ? "allow" : "block");
                    deferral.Complete();
                    popup.Close();
                }

                allowAlways.MouseLeftButtonDown += (s, _) => CloseWith(CoreWebView2PermissionState.Allow, true);
                allowOnce.MouseLeftButtonDown   += (s, _) => CloseWith(CoreWebView2PermissionState.Allow, false);
                neverAllow.MouseLeftButtonDown  += (s, _) => CloseWith(CoreWebView2PermissionState.Deny,  true);
                xBtn.Click += (s, _) => { e.State = CoreWebView2PermissionState.Default; deferral.Complete(); popup.Close(); };

                popup.Deactivated += (s, _) => { if (popup.IsVisible) { e.State = CoreWebView2PermissionState.Default; deferral.Complete(); popup.Close(); } };
                popup.Show();
                ForcePopupOnTop(popup);
                TrackPopupPosition(popup, () => { var pt = SecurityIconCanvas.PointToScreen(new Point(0, SecurityIconCanvas.ActualHeight)); return (pt.X - 20, pt.Y + 6); });
            }
            catch
            {
                e.State = CoreWebView2PermissionState.Default;
                deferral.Complete();
            }
        });
    }

    private void SecurityIcon_Click(object sender, MouseButtonEventArgs e) => OpenSiteInfoForActiveTab();

    private void OpenSiteInfoForActiveTab()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var webView = _tabs[_activeTabIndex].WebView;
        var url = webView.Source?.ToString() ?? "";
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http")) return;
        try { ShowSiteInfoPanel(webView, new Uri(url)); } catch { }
    }

    private void ShowSiteInfoPanel(WebView2 webView, Uri uri)
    {
        var origin  = uri.Host;
        var isHttps = uri.Scheme == "https";

        var accent  = (Color)ColorConverter.ConvertFromString("#8ab4f8")!;
        var bgDark  = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#292a2d" : "#ffffff")!;
        var bgDeep  = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#202124" : "#f8f9fa")!;
        var border1 = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c4043" : "#dfe1e5")!;
        var textPri = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!;
        var textSub = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!;

        var popup = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false,
            Topmost = true, Owner = this,
            Width = 300, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var pt = SecurityIconCanvas.PointToScreen(new Point(0, SecurityIconCanvas.ActualHeight));
        popup.Left = pt.X - 20; popup.Top = pt.Y + 6;

        var rootBorder = new Border
        {
            Background = new SolidColorBrush(bgDark), CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(border1), BorderThickness = new Thickness(1),
            Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
        };
        var outerStack = new StackPanel();

        // Title row: lock + origin + X
        var titleGrid = new Grid { Margin = new Thickness(14, 14, 12, 4) };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lockIcon = new WpfPath
        {
            Data = Geometry.Parse(isHttps ? "M7 11V7a5 5 0 0110 0v4M5 11h14a2 2 0 012 2v7a2 2 0 01-2 2H5a2 2 0 01-2-2v-7a2 2 0 012-2z" : "M17 11V7a5 5 0 00-9.9-1M5 11h14a2 2 0 012 2v7a2 2 0 01-2 2H5a2 2 0 01-2-2v-7a2 2 0 012-2z"),
            Stroke = new SolidColorBrush(isHttps ? (Color)ColorConverter.ConvertFromString("#81c995")! : (Color)ColorConverter.ConvertFromString("#f28b82")!),
            StrokeThickness = 1.5, Fill = Brushes.Transparent,
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lockIcon, 0); titleGrid.Children.Add(lockIcon);
        var originText = new TextBlock { Text = origin, Foreground = new SolidColorBrush(textPri), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(originText, 1); titleGrid.Children.Add(originText);
        var xBtn = new Button { Content = "✕", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(textSub), FontSize = 13, Width = 26, Height = 26, Cursor = Cursors.Hand, Padding = new Thickness(0) };
        xBtn.Click += (s, _) => popup.Close();
        Grid.SetColumn(xBtn, 2); titleGrid.Children.Add(xBtn);
        outerStack.Children.Add(titleGrid);

        // Connection status
        outerStack.Children.Add(new TextBlock
        {
            Text = isHttps ? "Connection is secure" : "Connection is not secure",
            Foreground = new SolidColorBrush(isHttps ? (Color)ColorConverter.ConvertFromString("#81c995")! : (Color)ColorConverter.ConvertFromString("#f28b82")!),
            FontSize = 12, Margin = new Thickness(14, 2, 14, 12)
        });

        outerStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(border1) });

        // Permissions section
        outerStack.Children.Add(new TextBlock { Text = "Permissions", Foreground = new SolidColorBrush(textSub), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 10, 14, 6) });

        var savedPerms = LoadSitePermissions();
        savedPerms.TryGetValue(origin, out var domainPerms);
        domainPerms ??= new();

        (string key, string label, string iconPath)[] permTypes =
        {
            ("camera",        "Camera",        "M15 8v8H3V8h2l1-2h6l1 2h2zm-6 6a3 3 0 100-6 3 3 0 000 6z"),
            ("microphone",    "Microphone",    "M12 14a3 3 0 003-3V5a3 3 0 00-6 0v6a3 3 0 003 3zm5-3a5 5 0 01-10 0H5a7 7 0 0014 0h-2zm-5 5v-3"),
            ("location",      "Location",      "M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5a2.5 2.5 0 010-5 2.5 2.5 0 010 5z"),
            ("notifications", "Notifications", "M12 22c1.1 0 2-.9 2-2h-4c0 1.1.9 2 2 2zm6-6v-5c0-3.07-1.64-5.64-4.5-6.32V4c0-.83-.67-1.5-1.5-1.5S10.5 3.17 10.5 4v.68C7.63 5.36 6 7.92 6 11v5l-2 2v1h16v-1l-2-2z"),
            ("clipboard",     "Clipboard",     "M16 4h2a2 2 0 012 2v14a2 2 0 01-2 2H6a2 2 0 01-2-2V6a2 2 0 012-2h2M9 2h6a1 1 0 010 2H9a1 1 0 010-2z")
        };

        foreach (var (key, label, iconPath) in permTypes)
        {
            var rowGrid = new Grid { Margin = new Thickness(14, 4, 14, 4) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new WpfPath { Data = Geometry.Parse(iconPath), Fill = new SolidColorBrush(textSub), Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0); rowGrid.Children.Add(icon);
            var lbl = new TextBlock { Text = label, Foreground = new SolidColorBrush(textPri), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 1); rowGrid.Children.Add(lbl);

            var currentState = domainPerms.TryGetValue(key, out var s) ? s : "ask";
            var combo = new ComboBox { FontSize = 12, Height = 28, MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
            combo.Items.Add("Allow"); combo.Items.Add("Block"); combo.Items.Add("Ask (default)");
            combo.SelectedIndex = currentState == "allow" ? 0 : currentState == "block" ? 1 : 2;
            var capturedKey = key;
            combo.SelectionChanged += (s, _) =>
            {
                switch (combo.SelectedIndex)
                {
                    case 0: SaveSitePermission(origin, capturedKey, "allow"); break;
                    case 1: SaveSitePermission(origin, capturedKey, "block"); break;
                    case 2: RemoveSitePermission(origin, capturedKey); break;
                }
            };
            Grid.SetColumn(combo, 2); rowGrid.Children.Add(combo);
            outerStack.Children.Add(rowGrid);
        }

        outerStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(border1), Margin = new Thickness(0, 8, 0, 0) });

        // Clear cookies button
        var clearBtn = new Border
        {
            CornerRadius = new CornerRadius(8), Margin = new Thickness(14, 8, 14, 14),
            Padding = new Thickness(0, 10, 0, 10), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(20, 0xf2, 0x8b, 0x82)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0xf2, 0x8b, 0x82)),
            BorderThickness = new Thickness(1)
        };
        clearBtn.Child = new TextBlock { Text = "Clear cookies and site data", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
        clearBtn.MouseEnter += (s, _) => clearBtn.Background = new SolidColorBrush(Color.FromArgb(50, 0xf2, 0x8b, 0x82));
        clearBtn.MouseLeave += (s, _) => clearBtn.Background = new SolidColorBrush(Color.FromArgb(20, 0xf2, 0x8b, 0x82));
        clearBtn.MouseLeftButtonDown += async (s, _) =>
        {
            try
            {
                // Delete cookies for this domain
                var cookieManager = webView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync($"{uri.Scheme}://{origin}");
                foreach (var ck in cookies) cookieManager.DeleteCookie(ck);
                // Clear cache (global, best we can do in WebView2)
                await webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.DiskCache);
                popup.Close();
                webView.Reload();
            }
            catch { popup.Close(); }
        };
        outerStack.Children.Add(clearBtn);

        rootBorder.Child = outerStack;
        popup.Content = rootBorder;
        popup.Deactivated += (s, _) => { if (popup.IsVisible) popup.Close(); };
        popup.Show();
        ForcePopupOnTop(popup);
        TrackPopupPosition(popup, () => { var pt = SecurityIconCanvas.PointToScreen(new Point(0, SecurityIconCanvas.ActualHeight)); return (pt.X - 20, pt.Y + 6); });
    }
    
    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // If a suggestion is selected, navigate to it
            if (SuggestPopup.IsOpen && SuggestionsList.SelectedItem is OmniSuggestion sel)
            {
                NavigateSuggestion(sel);
                e.Handled = true;
                return;
            }
            Navigate(UrlBox.Text);
            SuggestPopup.IsOpen = false;
            if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                _tabs[_activeTabIndex].WebView.Focus();
        }
    }

    private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!SuggestPopup.IsOpen) return;
        if (!OmniboxBorder.IsMouseOver)
            SuggestPopup.IsOpen = false;
    }

    private void UrlBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Mark user as actively editing for keys that modify text
        if (e.Key == Key.Back || e.Key == Key.Delete)
            _userEditingUrl = true;

        if (!SuggestPopup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            var count = SuggestionsList.Items.Count;
            if (count == 0) return;
            SuggestionsList.SelectedIndex = Math.Min((SuggestionsList.SelectedIndex + 1), count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            SuggestionsList.SelectedIndex = Math.Max(SuggestionsList.SelectedIndex - 1, -1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestPopup.IsOpen = false;
            e.Handled = true;
        }
    }
    
    private void UrlBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        _userEditingUrl = true;
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateUrlPlaceholder();
        var text = UrlBox.Text;
        // Only style as URL when user is actively typing
        if (UrlBox.IsFocused)
        {
            var looksLikeUrl = !string.IsNullOrWhiteSpace(text) &&
                               !text.Contains(' ') &&
                               (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                (text.Contains('.') && !text.StartsWith("ycb://")));
            if (looksLikeUrl)
            {
                UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#8ab4f8" : "#1558d6")!);
                UrlBox.TextDecorations = TextDecorations.Underline;
            }
            else
            {
                UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
                UrlBox.TextDecorations = null;
            }
        }
        if (!string.IsNullOrWhiteSpace(text) && _userEditingUrl)
            _ = UpdateSuggestionsAsync(text);
        else
            SuggestPopup.IsOpen = false;
    }
    
    private void UrlBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _userEditingUrl = false;
        OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c4043" : "#ffffff")!);
        UrlPlaceholder.Visibility = Visibility.Collapsed;
        // Reset URL styling so editing starts clean
        UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
        UrlBox.TextDecorations = null;
        
        // Show actual URL when focused (for editing), but never expose file:// internal paths
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var actualUrl = _tabs[_activeTabIndex].WebView?.Source?.ToString() ?? _tabs[_activeTabIndex].Url;
            var displayUrl = GetDisplayUrl(actualUrl);
            if (!string.IsNullOrEmpty(displayUrl))
            {
                UrlBox.Text = displayUrl;
            }
        }
        UrlBox.SelectAll();
    }
    
    private void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _userEditingUrl = false;
        // Don't process lost focus when focus moved to BookmarkBtn — it causes star icon flicker
        if (BookmarkBtn.IsKeyboardFocused || BookmarkBtn.IsFocused) return;
        // Don't close suggestions if focus moved into the suggestions list
        if (SuggestionsList.IsKeyboardFocusWithin) return;
        SuggestPopup.IsOpen = false;

        OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#292b2f" : "#f1f3f4")!);
        // Clear URL typing style
        UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
        UrlBox.TextDecorations = null;
        
        // Show display URL when losing focus
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var actualUrl = _tabs[_activeTabIndex].WebView?.Source?.ToString() ?? _tabs[_activeTabIndex].Url;
            UrlBox.Text = GetDisplayUrl(actualUrl);
        }
        UpdateUrlPlaceholder();
    }

    private async Task UpdateSuggestionsAsync(string query)
    {
        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var cts = _suggestCts;

        if (string.IsNullOrWhiteSpace(query))
        {
            SuggestPopup.IsOpen = false;
            return;
        }

        try
        {
            await Task.Delay(150, cts.Token);
            if (cts.IsCancellationRequested) return;

            var suggestions = new List<OmniSuggestion>();

            // History matches (top 3, most recent first)
            var history = LoadHistory();
            var historyMatches = history
                .Where(h => h.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             h.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(h => h.Timestamp)
                .Take(3)
                .Select(h => new OmniSuggestion
                {
                    Primary = GetSuggestionPrimary(h),
                    Secondary = GetSuggestionSecondary(h),
                    NavigateUrl = h.Url,
                    Kind = IsSearchHistoryItem(h) ? OmniSuggestionKind.History : OmniSuggestionKind.Site,
                    FaviconUrl = GetFaviconServiceUrl(h.Url),
                    IsRemovable = true
                });
            suggestions.AddRange(historyMatches);

            // Google search suggestions (up to 5)
            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(3);
                var encoded = Uri.EscapeDataString(query);
                var json = await http.GetStringAsync(
                    $"https://suggestqueries.google.com/complete/search?client=firefox&q={encoded}", cts.Token);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var s in doc.RootElement[1].EnumerateArray().Take(5))
                {
                    var text = s.GetString() ?? "";
                    if (string.IsNullOrEmpty(text)) continue;
                    suggestions.Add(new OmniSuggestion
                    {
                        Primary = text,
                        NavigateUrl = GetSearchUrl(text),
                        Kind = OmniSuggestionKind.Search
                    });
                }
            }
            catch { /* ignore network failures */ }

            if (cts.IsCancellationRequested) return;

            SuggestionsList.ItemsSource = suggestions;
            SuggestionsList.SelectedIndex = -1;
            SuggestPopup.IsOpen = suggestions.Count > 0;

            if (suggestions.Count > 0)
            {
                // Start or reset a short timer to close suggestions when focus moves away (handles clicks into WebView HWND)
                if (_suggestCloseTimer == null)
                {
                    _suggestCloseTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    _suggestCloseTimer.Tick += (ts, te) =>
                    {
                        try
                        {
                            if (!UrlBox.IsKeyboardFocused && !SuggestionsList.IsKeyboardFocusWithin && !OmniboxBorder.IsMouseOver && !BookmarkBtn.IsKeyboardFocused && !BookmarkBtn.IsMouseOver)
                            {
                                SuggestPopup.IsOpen = false;
                                _suggestCloseTimer?.Stop();
                            }
                        }
                        catch { }
                    };
                }
                _suggestCloseTimer.Stop();
                _suggestCloseTimer.Start();
            }
            else
            {
                _suggestCloseTimer?.Stop();
            }
        }
        catch (TaskCanceledException) { }
    }

    private void NavigateSuggestion(OmniSuggestion s)
    {
        SuggestPopup.IsOpen = false;
        Navigate(s.NavigateUrl);
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].WebView.Focus();
    }

    private void RemoveSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OmniSuggestion suggestion }) return;
        if (string.IsNullOrWhiteSpace(suggestion.NavigateUrl)) return;
        try
        {
            var history = LoadHistory();
            history.RemoveAll(h => string.Equals(h.Url, suggestion.NavigateUrl, StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
            _ = UpdateSuggestionsAsync(UrlBox.Text);
        }
        catch { }
    }

    private static string GetSuggestionPrimary(HistoryItem item)
    {
        var title = (item.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(title) && !LooksLikeUrl(title))
            return title;

        if (IsGoogleSearchUrl(item.Url, out var query))
            return query;

        return GetFriendlyUrlLabel(item.Url);
    }

    private static string GetSuggestionSecondary(HistoryItem item)
    {
        if (IsGoogleSearchUrl(item.Url, out _))
            return "Google Search";

        var host = GetHostLabel(item.Url);
        var title = (item.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(title) && !LooksLikeUrl(title) && !string.Equals(title, host, StringComparison.OrdinalIgnoreCase))
            return $"{host} - {GetShortUrl(item.Url)}";

        return GetShortUrl(item.Url);
    }

    private static bool IsSearchHistoryItem(HistoryItem item)
    {
        if (IsGoogleSearchUrl(item.Url, out _)) return true;
        return string.Equals((item.Title ?? "").Trim(), (item.Url ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFaviconServiceUrl(string? url)
    {
        var host = GetHostLabel(url);
        return string.IsNullOrWhiteSpace(host)
            ? ""
            : $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=32";
    }

    private static bool IsGoogleSearchUrl(string? url, out string query)
    {
        query = "";
        try
        {
            var uri = new Uri(url ?? "");
            if (!uri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase)) return false;
            var q = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && string.Equals(parts[0], "q", StringComparison.OrdinalIgnoreCase))
                .Select(parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")))
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(q)) return false;
            query = q;
            return true;
        }
        catch { return false; }
    }

    private static bool LooksLikeUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Uri.TryCreate(text, UriKind.Absolute, out _) ||
               text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetHostLabel(string? url)
    {
        try
        {
            var host = new Uri(url ?? "").Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch { return ""; }
    }

    private static string GetFriendlyUrlLabel(string? url)
    {
        var host = GetHostLabel(url);
        return string.IsNullOrWhiteSpace(host) ? (url ?? "") : host;
    }

    private static string GetShortUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            var path = uri.PathAndQuery.TrimEnd('/');
            var shortPath = path.Length > 34 ? path[..34] + "..." : path;
            return string.IsNullOrWhiteSpace(shortPath) || shortPath == "/" ? host : $"{host}{shortPath}";
        }
        catch
        {
            return url.Length > 46 ? url[..46] + "..." : url;
        }
    }

    private void SuggestionsList_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe &&
            fe.DataContext is OmniSuggestion s)
        {
            NavigateSuggestion(s);
            e.Handled = true;
        }
    }

    private void SuggestionsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionsList.SelectedItem is OmniSuggestion s)
        {
            NavigateSuggestion(s);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestPopup.IsOpen = false;
            UrlBox.Focus();
            e.Handled = true;
        }
    }

    private void BookmarkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;

        var tab = _tabs[_activeTabIndex];
        var url = tab.WebView?.Source?.ToString() ?? tab.Url ?? "";

        // Never bookmark system / internal pages
        if (string.IsNullOrEmpty(url) || url.StartsWith("ycb://") || url.StartsWith("file:///"))
        {
            BookmarkBtn.ToolTip = "Can't bookmark this page";
            return;
        }

        BookmarkBtn.ToolTip = "Bookmark this tab";
        var bookmarks = LoadBookmarks();
        var existing = bookmarks.FindIndex(b => b.Url == url);

        if (existing >= 0)
        {
            // Already bookmarked — remove it
            RemoveBookmark(existing);
            UpdateBookmarkStar(false);
            UpdateBookmarksBar();
        }
        else
        {
            // Add new bookmark
            var title = tab.Title ?? url;
            AddBookmark(url, title);
            UpdateBookmarkStar(true);
            UpdateBookmarksBar();
        }

        // Return focus to the WebView so the page doesn't flicker
        tab.WebView?.Focus();
    }

    /// <summary>Updates the star icon to filled (bookmarked) or hollow (not bookmarked).</summary>
    private void UpdateBookmarkStar(bool isBookmarked)
    {
        if (BookmarkStarPath == null) return;
        if (isBookmarked)
        {
            BookmarkStarPath.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#fabd05")!);
            BookmarkStarPath.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#fabd05")!);
        }
        else
        {
            BookmarkStarPath.Fill = Brushes.Transparent;
            BookmarkStarPath.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!);
        }
    }

    /// <summary>Called whenever the active tab URL changes — syncs the star icon state.</summary>
    private void RefreshBookmarkStar()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) { UpdateBookmarkStar(false); return; }
        var url = _tabs[_activeTabIndex].WebView?.Source?.ToString() ?? _tabs[_activeTabIndex].Url ?? "";
        bool isInternal = string.IsNullOrEmpty(url) || url.StartsWith("ycb://") || url.StartsWith("file:///");

        if (isInternal)
        {
            UpdateBookmarkStar(false);
            BookmarkBtn.IsEnabled = false;
            BookmarkBtn.Opacity = 0.4;
            return;
        }
        BookmarkBtn.IsEnabled = true;
        BookmarkBtn.Opacity = 1.0;
        var bookmarks = LoadBookmarks();
        UpdateBookmarkStar(bookmarks.Any(b => b.Url == url));
    }
    
    private string GetDisplayUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        
        // For all internal ycb:// pages, return empty so the placeholder shows the page name
        if (url.StartsWith("ycb://")) return "";
        if (IsInternalVirtualUrl(url)) return "";
        
        // Also hide file:// paths for internal renderer pages — return empty string
        if (url.StartsWith("file:///") &&
            (url.Contains("/renderer/") || url.Contains("\\renderer\\")))
        {
            return "";
        }
        
        return url;
    }

    private string GetLoadingTabTitle(string? url, bool useHostFallback = true)
    {
        if (string.IsNullOrWhiteSpace(url)) return "New Tab";

        var systemName = GetSystemPageName(url);
        if (!string.IsNullOrWhiteSpace(systemName)) return systemName;
        if (url.StartsWith("ycb://newtab", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("newtab.html", StringComparison.OrdinalIgnoreCase))
            return "New Tab";

        try
        {
            var uri = new Uri(url);
            if (IsGoogleSearchUrl(url, out var searchQuery))
                return string.IsNullOrWhiteSpace(searchQuery) ? "Google Search" : $"{searchQuery} - Google Search";
            if (useHostFallback && !string.IsNullOrWhiteSpace(uri.Host))
                return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host[4..]
                    : uri.Host;
        }
        catch { }

        return "Loading...";
    }

    private static bool IsInternalVirtualUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               url.StartsWith(InternalOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static string VirtualInternalUrlToYcbUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var file = IoPath.GetFileName(uri.AbsolutePath).ToLowerInvariant();
            var page = file switch
            {
                "settings.html" => "settings",
                "history.html" => "history",
                "downloads.html" => "downloads",
                "passwords.html" => "passwords",
                "profiles.html" => "profiles",
                "apps.html" => "apps",
                "guide.html" => "guide",
                "support.html" => "support",
                "newtab.html" => "newtab",
                _ => "newtab"
            };
            return string.IsNullOrWhiteSpace(uri.Fragment)
                ? $"ycb://{page}"
                : $"ycb://{page}{uri.Fragment}";
        }
        catch
        {
            return "ycb://newtab";
        }
    }
    
    private string GetSystemPageName(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("ycb://"))
        {
            return url switch
            {
                "ycb://newtab"    => "",
                "ycb://apps"      => "Apps",
                "ycb://profiles"  => "Profiles",
                "ycb://settings"  => "Settings",
                "ycb://history"   => "History",
                "ycb://downloads" => "Downloads",
            "ycb://passwords" => "Bitwarden",
                "ycb://bookmarks" => "Bookmarks",
                "ycb://guide"     => "Help & Guide",
                _ => ""
            };
        }
        if (IsInternalVirtualUrl(url))
        {
            return GetSystemPageName(VirtualInternalUrlToYcbUrl(url));
        }
        if (url.StartsWith("file:///"))
        {
            if (url.Contains("newtab.html"))    return "";
            if (url.Contains("apps.html"))      return "Apps";
            if (url.Contains("profiles.html"))   return "Profiles";
            if (url.Contains("settings.html"))  return "Settings";
            if (url.Contains("history.html"))   return "History";
            if (url.Contains("downloads.html")) return "Downloads";
        if (url.Contains("passwords.html")) return "Bitwarden";
            if (url.Contains("bookmarks.html")) return "Bookmarks";
            if (url.Contains("guide.html"))     return "Help & Guide";
        }
        return "";
    }
    
    private void UpdateUrlPlaceholder()
    {
        // On a system page, show the page name as the placeholder
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var currentUrl = _tabs[_activeTabIndex].WebView?.Source?.ToString()
                             ?? _tabs[_activeTabIndex].Url ?? "";
            var pageName = GetSystemPageName(currentUrl);
            if (!string.IsNullOrEmpty(pageName))
            {
                UrlPlaceholder.Text = pageName;
                UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlBox.Text) && !UrlBox.IsFocused ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
        }

        // Normal page — show search engine prompt
        var searchText = _searchEngine switch
        {
            "bing"       => "Search with Bing",
            "duckduckgo" => "Search with DuckDuckGo",
            "ecosia"     => "Search with Ecosia",
            "brave"      => "Search with Brave",
            "yahoo"      => "Search with Yahoo",
            _            => "Search with Google"
        };
        UrlPlaceholder.Text = searchText;
        UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlBox.Text) && !UrlBox.IsFocused ? Visibility.Visible : Visibility.Collapsed;
    }
    
    private void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        ImagePickerPopup.IsOpen = !ImagePickerPopup.IsOpen;
    }

    private async void TakeScreenshot_Click(object sender, RoutedEventArgs e)
    {
        ImagePickerPopup.IsOpen = false;
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var webView = _tabs[_activeTabIndex].WebView;
        if (webView?.CoreWebView2 == null) return;

        var path = IoPath.Combine(IoPath.GetTempPath(), $"ycb_screenshot_{DateTime.Now:yyyyMMddHHmmss}.png");
        using (var stream = new FileStream(path, FileMode.Create))
        {
            await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }

        _attachedImagePath = path;
        ImageAttachName.Text = "📸 Screenshot";
        ImageAttachIndicator.Visibility = Visibility.Visible;
    }

    private void OpenGallery_Click(object sender, RoutedEventArgs e)
    {
        ImagePickerPopup.IsOpen = false;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _attachedImagePath = dlg.FileName;
            ImageAttachName.Text = "📎 " + IoPath.GetFileName(dlg.FileName);
            ImageAttachIndicator.Visibility = Visibility.Visible;
        }
    }

    private void RemoveAttachedImage_Click(object sender, RoutedEventArgs e)
    {
        _attachedImagePath = null;
        ImageAttachIndicator.Visibility = Visibility.Collapsed;
    }

    private async void Copilot_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAiEnabledForProfile)
        {
            ApplyAiVisibility();
            return;
        }

        // Block Copilot in incognito mode unless setting enabled
        if (_isIncognito && !(_settings.IncognitoAIEnabled ?? false))
        {
            MessageBox.Show("AI/Copilot is disabled in Incognito mode.\nYou can enable it in Settings > Privacy > Allow AI in Incognito.", 
                "Incognito Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        
        _copilotVisible = !_copilotVisible;
        CopilotSidebar.Visibility = _copilotVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _copilotVisible ? new GridLength(Math.Max(240, _aiSidebarWidth)) : new GridLength(0);
        AiSidebarSplitter.Visibility = _copilotVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_copilotVisible)
        {
            await ShowAiProviderAsync();
        }
        else
        {
            SaveSettings();
        }
    }
    
    private void CloseCopilot_Click(object sender, RoutedEventArgs e)
    {
        if (SidebarColumn.ActualWidth > 0)
        {
            _aiSidebarWidth = Math.Max(240, Math.Min(SidebarColumn.ActualWidth, 720));
        }
        _copilotVisible = false;
        CopilotSidebar.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
        AiSidebarSplitter.Visibility = Visibility.Collapsed;
        SaveSettings();
    }

    private static readonly IReadOnlyDictionary<string, (string Label, string Url)> AiProviders =
        new Dictionary<string, (string Label, string Url)>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatgpt"] = ("ChatGPT", "https://chatgpt.com/"),
            ["gemini"] = ("Gemini", "https://gemini.google.com/"),
            ["claude"] = ("Claude", "https://claude.ai/"),
            ["microsoft-copilot"] = ("Microsoft Copilot", "https://copilot.microsoft.com/"),
            ["grok"] = ("Grok", "https://grok.com/"),
            ["meta-ai"] = ("Meta AI", "https://www.meta.ai/"),
            ["github-copilot"] = ("GitHub Copilot", "https://github.com/copilot"),
            ["perplexity"] = ("Perplexity AI", "https://www.perplexity.ai/"),
            ["deepseek"] = ("DeepSeek", "https://chat.deepseek.com/"),
        };

    private static string NormalizeAiProvider(string? provider)
    {
        var value = (provider ?? "").Trim();
        return value.ToLowerInvariant() switch
        {
            "copilot" => "github-copilot",
            "github" => "github-copilot",
            "codex" => "github-copilot",
            "microsoft" => "microsoft-copilot",
            "openai" => "chatgpt",
            "gpt" => "chatgpt",
            "perplexity ai" => "perplexity",
            "meta" => "meta-ai",
            "chat gpt" => "chatgpt",
            _ when AiProviders.ContainsKey(value) => value,
            _ => "chatgpt"
        };
    }

    private string GetAiProviderKey() => NormalizeAiProvider(_settings.AiProvider);

    private string GetAiProviderLabel(string? provider = null)
    {
        var key = NormalizeAiProvider(provider ?? _settings.AiProvider);
        return AiProviders.TryGetValue(key, out var meta) ? meta.Label : "ChatGPT";
    }

    private string GetAiProviderUrl(string? provider = null)
    {
        var key = NormalizeAiProvider(provider ?? _settings.AiProvider);
        return AiProviders.TryGetValue(key, out var meta) ? meta.Url : AiProviders["chatgpt"].Url;
    }

    private void SyncAiProviderCombo()
    {
        if (AiProviderCombo == null) return;
        var key = GetAiProviderKey();
        foreach (var item in AiProviderCombo.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag?.ToString() ?? "") == key)
            {
                AiProviderCombo.SelectedItem = item;
                break;
            }
        }
    }

    private async Task EnsureAiWebViewAsync()
    {
        if (AiWebView.CoreWebView2 != null)
        {
            return;
        }

        var dataFolder = _isIncognito ? _incognitoUserDataFolder : _profileFolder;
        if (_isIncognito)
        {
            _incognitoWebViewEnvironment ??= await CreateWebViewEnvironment(dataFolder);
            await AiWebView.EnsureCoreWebView2Async(_incognitoWebViewEnvironment);
        }
        else
        {
            _webViewEnvironment ??= await CreateWebViewEnvironment(dataFolder);
            await AiWebView.EnsureCoreWebView2Async(_webViewEnvironment);
        }

        AiWebView.DefaultBackgroundColor = _isDarkMode
            ? System.Drawing.Color.FromArgb(255, 32, 33, 36)
            : System.Drawing.Color.FromArgb(255, 255, 255, 255);
        _aiWebViewReady = true;
    }

    private async Task ShowAiProviderAsync()
    {
        await EnsureAiWebViewAsync();
        SyncAiProviderCombo();
        UpdateAiSidebarTheme();
        AiSidebarSplitter.Visibility = Visibility.Visible;
        if (SidebarColumn.Width.Value <= 0)
        {
            SidebarColumn.Width = new GridLength(Math.Max(240, _aiSidebarWidth));
        }
        NavigateAiProvider();
    }

    private void NavigateAiProvider(bool openInNewTab = false)
    {
        var url = GetAiProviderUrl();
        var label = GetAiProviderLabel();
        if (CopilotTitleText != null) CopilotTitleText.Text = label;
        if (CopilotSubtitleText != null) CopilotSubtitleText.Text = "Browsing " + label + " in the sidebar";
        if (openInNewTab)
        {
            _ = CreateTab(url);
            return;
        }

        if (AiWebView.CoreWebView2 != null)
        {
            AiWebView.CoreWebView2.Navigate(url);
        }
        else
        {
            AiWebView.Source = new Uri(url);
        }
    }

    private void UpdateAiSidebarTheme()
    {
        if (AiToolbarBorder == null) return;
        AiToolbarBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#1f2024" : "#f8f9fa")!);
        AiToolbarBorder.BorderBrush = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
        if (AiToolbarLabel != null)
        {
            AiToolbarLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!);
        }
        if (AiProviderCombo != null)
        {
            AiProviderCombo.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2a2b36" : "#ffffff")!);
            AiProviderCombo.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
            AiProviderCombo.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3e4a" : "#dfe1e5")!);
        }
        foreach (var btn in new[] { AiOpenTabBtn, AiRefreshBtn, AiHomeBtn })
        {
            if (btn == null) continue;
            btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2a2b36" : "#ffffff")!);
            btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
            btn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3e4a" : "#dfe1e5")!);
        }
    }

    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiProviderCombo.SelectedItem is ComboBoxItem item)
        {
            var selected = NormalizeAiProvider(item.Tag?.ToString() ?? "");
            if (!string.IsNullOrEmpty(selected))
            {
                _settings.AiProvider = selected;
                SaveSettings();
                if (_copilotVisible)
                {
                    NavigateAiProvider();
                }
            }
        }
    }

    private void AiSidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth <= 0)
        {
            return;
        }

        _aiSidebarWidth = Math.Max(240, Math.Min(SidebarColumn.ActualWidth, 720));
        _settings.AiSidebarWidth = _aiSidebarWidth;
        SaveSettings();

        if (SidebarColumn.ActualWidth < 220)
        {
            CloseCopilot_Click(sender, new RoutedEventArgs());
        }
    }

    private void AiOpenTabBtn_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _ = CreateTab(GetAiProviderUrl());
    }

    private async void AiRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        await EnsureAiWebViewAsync();
        SyncAiProviderCombo();
        NavigateAiProvider();
    }

    private async void AiHomeBtn_Click(object sender, RoutedEventArgs e)
    {
        await EnsureAiWebViewAsync();
        SyncAiProviderCombo();
        NavigateAiProvider();
    }
    
    private void CopilotInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendCopilot_Click(sender, e);
        }
    }
    
    private async void SendCopilot_Click(object sender, RoutedEventArgs e)
    {
        var message = CopilotInput.Text.Trim();
        if (string.IsNullOrEmpty(message) && _attachedImagePath == null) return;

        var displayMessage = message;
        if (_attachedImagePath != null)
            displayMessage = (string.IsNullOrEmpty(message) ? "" : message + "\n") + "📎 " + System.IO.Path.GetFileName(_attachedImagePath);

        AddCopilotMessage(string.IsNullOrEmpty(displayMessage) ? "📎 Image attached" : displayMessage, true);
        _chatHistory.Add(new ChatMessage { Role = "user", Content = displayMessage });
        CopilotInput.Text = "";
        CopilotInput.IsEnabled = false;

        var imagePath = _attachedImagePath;
        _attachedImagePath = null;
        ImageAttachIndicator.Visibility = Visibility.Collapsed;

        // Get current URL
        var currentUrl = "";
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            currentUrl = _tabs[_activeTabIndex].WebView?.Source?.ToString() ?? "";
        }

        var provider = GetAiProvider();
        var model = GetAssistantModel(provider);

        // Create response message placeholder
        var responseBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#24263a" : "#f1f3f6")!),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 5, 40, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 280,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Opacity = 0.7
        };

        _currentResponseBlock = new TextBlock
        {
            Text = "Thinking...",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        responseBorder.Child = _currentResponseBlock;
        MessagesPanel.Children.Add(responseBorder);
        CopilotMessages.ScrollToEnd();

        try
        {
            // Build prompt with context-aware URL handling.
            // The CLI prompt argument is used as a system prompt for Codex; stdin carries the user message.
            var systemPrompt = "You are YCB Browser's in-app assistant. This text is the system prompt. The user's actual message will be supplied separately on stdin, and you must treat that stdin content as the user message. Do not talk about the prompt, instructions, or any hidden markers. Do not copy the style of earlier assistant replies. Answer the current user message directly. For normal chat, writing, explaining, summarizing, coding, or other general requests, answer directly instead of asking what to open or look up. Only mention browsing if the user explicitly asks you to open, visit, or look up a website. If that happens, include [OPEN_URL:https://example.com] in your reply so YCB can open it. If an image is attached, inspect it first and answer what is visible, including reading text in the image when possible. Do not begin with a canned greeting or a generic help line unless the user specifically asks for one. ";
            if (!string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank")
            {
                string pageContext;
                if (currentUrl.StartsWith("ycb://") || currentUrl.Contains("ycb://"))
                {
                    // Map internal pages to friendly names
                    if (currentUrl.Contains("settings")) pageContext = "the YCB Settings page";
                    else if (currentUrl.Contains("history")) pageContext = "the YCB History page";
                    else if (currentUrl.Contains("downloads")) pageContext = "the YCB Downloads page";
        else if (currentUrl.Contains("passwords")) pageContext = "the YCB Bitwarden page";
                    else if (currentUrl.Contains("newtab")) pageContext = "the YCB New Tab page";
                    else if (currentUrl.Contains("guide")) pageContext = "the YCB Guide page";
                    else if (currentUrl.Contains("support")) pageContext = "the YCB Support page";
                    else pageContext = "a YCB Browser internal page";
                    systemPrompt += $"The user is on {pageContext}. ";
                }
                else if (currentUrl.StartsWith("file:///"))
                {
                    // Silently suppress raw file paths — shouldn't reach here but just in case
                }
                else
                {
                    // Regular web page — show domain only to avoid exposing full private URLs
                    try
                    {
                        var host = new Uri(currentUrl).Host;
                        systemPrompt += $"The user is on {host}. ";
                    }
                    catch
                    {
                        // ignore bad URLs
                    }
                }
            }
            systemPrompt += "IMPORTANT: If the user asks you to open, navigate to, or visit a website, append a hidden marker in the form [OPEN_URL:https://example.com]. Do not mention the marker to the user.\n\n";

            var userPrompt = imagePath != null
                ? (string.IsNullOrWhiteSpace(message) ? "Read the attached image and tell me what it says." : message)
                : message;

            if (imagePath != null && !IsCodexProvider(provider))
            {
                await SendImageToVision(imagePath, string.IsNullOrEmpty(message) ? "Describe everything you see in this image." : message);
                return;
            }

            if (IsCodexProvider(provider))
            {
                await RunAssistantProcessAsync(provider, systemPrompt, userPrompt, model, imagePath);
            }
            else
            {
                var legacyPrompt = systemPrompt + "User: " + userPrompt;
                await RunAssistantProcessAsync(provider, legacyPrompt, userPrompt, model, null);
            }
        }
        catch (Exception ex)
        {
            _currentResponseBlock!.Text = $"Failed to start {provider}: {ex.Message}";
            _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
            CopilotInput.IsEnabled = true;
        }
    }

    private async Task SendImageToVision(string imagePath, string userMessage)
    {
        // Show placeholder
        var responseBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#24263a" : "#f1f3f6")!),
            CornerRadius = new CornerRadius(14, 14, 14, 3),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 5, 40, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 280
        };
        _currentResponseBlock = new TextBlock
        {
            Text = "Analysing image...",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        responseBorder.Child = _currentResponseBlock;
        MessagesPanel.Children.Add(responseBorder);
        CopilotMessages.ScrollToEnd();

        try
        {
            // Get GitHub token via gh CLI (same login used by Copilot)
            var tokenProc = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = "gh", Arguments = "auth token",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            }};
            tokenProc.Start();
            var token = (await tokenProc.StandardOutput.ReadToEndAsync()).Trim();
            tokenProc.WaitForExit(3000);

            if (string.IsNullOrEmpty(token))
            {
                _currentResponseBlock.Text = "Not logged in to GitHub. Run 'gh auth login' and try again.";
                _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
                CopilotInput.IsEnabled = true;
                return;
            }

            // Base64-encode the image
            var bytes = await File.ReadAllBytesAsync(imagePath);
            var b64 = Convert.ToBase64String(bytes);
            var mime = IoPath.GetExtension(imagePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"  => "image/gif",
                ".webp" => "image/webp",
                _       => "image/png"
            };

            // Build JSON request
            var body = JsonSerializer.Serialize(new
            {
                model = "gpt-4o",
                messages = new object[]
                {
                    new { role = "system", content = "You are a helpful browser assistant built into YCB." },
                    new { role = "user", content = new object[]
                        {
                            new { type = "image_url", image_url = new { url = $"data:{mime};base64,{b64}" } },
                            new { type = "text", text = userMessage }
                        }
                    }
                }
            });

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await http.PostAsync(
                "https://models.inference.ai.azure.com/chat/completions",
                new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            var json = await resp.Content.ReadAsStringAsync();

            string reply;
            try
            {
                using var doc = JsonDocument.Parse(json);
                reply = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "No response.";
            }
            catch { reply = $"Error {(int)resp.StatusCode}: {json}"; }

            _currentResponseBlock.Text = reply;
            _chatHistory.Add(new ChatMessage { Role = "assistant", Content = reply });
        }
        catch (Exception ex)
        {
            _currentResponseBlock.Text = $"Vision error: {ex.Message}";
            _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
        }
        finally { CopilotInput.IsEnabled = true; }
    }

    private string GetAiProvider()
    {
        var provider = _settings.AiProvider ?? "";
        if (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider, "github", StringComparison.OrdinalIgnoreCase))
        {
            return "github";
        }

        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return "codex";
        }

        return "github";
    }

    private static bool IsCodexProvider(string provider)
    {
        return string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultModelForProvider(string provider)
    {
        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return "gpt-5.3-codex";
        }

        return "gpt-5-mini";
    }

    private static bool IsModelSupportedByProvider(string provider, string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var list = IsCodexProvider(provider)
            ? new[] { "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.3-codex", "gpt-5.2" }
            : new[] { "gpt-5-mini", "gpt-5.4-mini", "gpt-5.1", "gpt-5.2", "gpt-5.4", "claude-haiku-4.5", "claude-sonnet-4", "claude-sonnet-4.5", "claude-sonnet-4.6", "claude-opus-4.5", "claude-opus-4.6", "claude-opus-4.6-fast", "gemini-3-pro-preview" };
        return list.Contains(model, StringComparer.OrdinalIgnoreCase);
    }

    private string GetAssistantModel(string provider)
    {
        var model = _settings.YcbModel ?? "";
        return IsModelSupportedByProvider(provider, model) ? model : GetDefaultModelForProvider(provider);
    }

    private string? FindAssistantExe(string provider)
    {
        return string.Equals(provider, "github", StringComparison.OrdinalIgnoreCase)
            ? FindCopilotExe() ?? "copilot"
            : null;
    }

    private async Task RunAssistantProcessAsync(string provider, string prompt, string userPrompt, string model, string? imagePath)
    {
        try
        {
            if (IsCodexProvider(provider))
            {
                await RunCodexProcessAsync(prompt, userPrompt, model, imagePath);
            }
            else
            {
                var executable = FindAssistantExe(provider);
                if (string.IsNullOrEmpty(executable))
                {
                    throw new InvalidOperationException("Copilot CLI not found. Please install GitHub Copilot CLI.");
                }
                await RunAssistantProcessCoreAsync(executable, prompt, model, provider);
            }
        }
        catch (Exception ex)
        {
            App.WriteTrace($"{provider} launch failed: {ex.Message}");
            throw;
        }
    }

    private async Task RunCodexProcessAsync(string systemPrompt, string userPrompt, string model, string? imagePath)
    {
        var outputFile = IoPath.Combine(IoPath.GetTempPath(), $"ycb-codex-{Guid.NewGuid():N}.txt");
        try
        {
            var nodeExe = FindNodeExe();
            var npxCli = FindNpxCli();
            if (string.IsNullOrWhiteSpace(nodeExe) || string.IsNullOrWhiteSpace(npxCli))
            {
                throw new InvalidOperationException("Node.js/npm was not found. Install Node.js or add it to PATH.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            startInfo.ArgumentList.Add(npxCli);
            startInfo.ArgumentList.Add("--yes");
            startInfo.ArgumentList.Add("@openai/codex");
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--output-last-message");
            startInfo.ArgumentList.Add(outputFile);
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(imagePath);
            }

            _copilotProcess = new Process { StartInfo = startInfo };
            _copilotProcess.EnableRaisingEvents = true;
            _copilotProcess.Start();
            await _copilotProcess.StandardInput.WriteAsync(systemPrompt);
            await _copilotProcess.StandardInput.WriteLineAsync();
            await _copilotProcess.StandardInput.WriteAsync(userPrompt);
            await _copilotProcess.StandardInput.WriteLineAsync();
            _copilotProcess.StandardInput.Close();
            var stdoutTask = _copilotProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = _copilotProcess.StandardError.ReadToEndAsync();
            await _copilotProcess.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var response = "";
            if (File.Exists(outputFile))
            {
                response = await File.ReadAllTextAsync(outputFile);
            }
            if (string.IsNullOrWhiteSpace(response))
            {
                response = stdout;
            }
            if (string.IsNullOrWhiteSpace(response))
            {
                response = stderr;
            }

            var cleanedResponse = CleanAssistantResponse(response);

            Dispatcher.Invoke(() =>
            {
                CopilotInput.IsEnabled = true;
                var finalText = string.IsNullOrWhiteSpace(cleanedResponse.Text) ? "No response." : cleanedResponse.Text.Trim();
                if (_currentResponseBlock?.Parent is Border rb)
                {
                    rb.CornerRadius = new CornerRadius(14, 14, 14, 3);
                    rb.BorderThickness = new Thickness(0);
                    rb.Opacity = 1.0;
                    rb.Child = BuildAssistantMessageUI(finalText);
                }
                _currentResponseBlock = null;
                if (!string.IsNullOrWhiteSpace(cleanedResponse.Text))
                {
                    _chatHistory.Add(new ChatMessage { Role = "assistant", Content = finalText });
                    foreach (var urlToOpen in cleanedResponse.OpenUrls)
                    {
                        _ = CreateTab(urlToOpen);
                    }
                }
                _copilotProcess = null;
                CopilotMessages.ScrollToEnd();
            });
        }
        catch (Exception ex)
        {
            _currentResponseBlock!.Text = $"Failed to start codex: {ex.Message}";
            _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
            CopilotInput.IsEnabled = true;
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(outputFile))
                {
                    File.Delete(outputFile);
                }
            }
            catch { }
        }
    }

    private static string? FindNodeExe()
    {
        var nodeDir = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
        var candidate = IoPath.Combine(nodeDir, "node.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        candidate = IoPath.Combine(nodeDir, "node");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return Environment.GetEnvironmentVariable("PATH")?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => IoPath.Combine(dir, "node.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindNpxCli()
    {
        var nodeDir = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
        var candidate = IoPath.Combine(nodeDir, "node_modules", "npm", "bin", "npx-cli.js");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return Environment.GetEnvironmentVariable("PATH")?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => IoPath.Combine(dir, "node_modules", "npm", "bin", "npx-cli.js"))
            .FirstOrDefault(File.Exists);
    }

    private static (string Text, List<string> OpenUrls) CleanAssistantResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return ("", new List<string>());
        }

        var openUrls = new List<string>();
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            response,
            @"\[OPEN_URL:\s*(https?://[^\]]+)\]",
            match =>
            {
                var url = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    openUrls.Add(url);
                }
                return "";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return (cleaned.Trim(), openUrls);
    }

    private async Task RunAssistantProcessCoreAsync(string executable, string prompt, string model, string provider)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add("--no-ask-user");
            startInfo.ArgumentList.Add("--allow-all-paths");

            _copilotProcess = new Process { StartInfo = startInfo };
            _copilotProcess.EnableRaisingEvents = true;
            _copilotProcess.Start();
            using var ms = new System.IO.MemoryStream();
            await _copilotProcess.StandardOutput.BaseStream.CopyToAsync(ms);
            await _copilotProcess.WaitForExitAsync();
            var response = System.Text.Encoding.UTF8.GetString(ms.ToArray()).Trim();

            var cleanedResponse = CleanAssistantResponse(response);

            Dispatcher.Invoke(() =>
            {
                CopilotInput.IsEnabled = true;
                var finalText = string.IsNullOrWhiteSpace(cleanedResponse.Text) ? "No response." : cleanedResponse.Text;
                if (_currentResponseBlock?.Parent is Border rb)
                {
                    rb.CornerRadius = new CornerRadius(14, 14, 14, 3);
                    rb.BorderThickness = new Thickness(0);
                    rb.Opacity = 1.0;
                    rb.Child = BuildAssistantMessageUI(finalText);
                }
                _currentResponseBlock = null;
                if (!string.IsNullOrWhiteSpace(cleanedResponse.Text))
                {
                    _chatHistory.Add(new ChatMessage { Role = "assistant", Content = finalText });
                    foreach (var urlToOpen in cleanedResponse.OpenUrls)
                    {
                        _ = CreateTab(urlToOpen);
                    }
                }
                _copilotProcess = null;
                CopilotMessages.ScrollToEnd();
            });
        }
        catch (Exception ex)
        {
            _currentResponseBlock!.Text = $"Failed to start {provider}: {ex.Message}";
            _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
            CopilotInput.IsEnabled = true;
            throw;
        }
    }

    // The copilot CLI outputs UTF-8 bytes, but .NET reads them as the system default
    // encoding (CP1252 on Windows), producing mojibake like â€" instead of —.
    // Fix: re-encode the mis-decoded string back to CP1252 bytes, then decode as UTF-8.
    private static string FixCopilotEncoding(string raw)
    {
        try
        {
            var cp1252 = System.Text.Encoding.GetEncoding(1252);
            var bytes = cp1252.GetBytes(raw);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return raw; }
    }

    private void AdBlock_Click(object sender, RoutedEventArgs e)
    {
        _settings.AdBlockerEnabled = !_settings.AdBlockerEnabled;
        SaveSettings();
        _ = ApplyUBlockOriginStateAsync();
        UpdateAdBlockButton();
    }

    private void MenuAdBlockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.AdBlockerEnabled == false) return;
        _settings.AdBlockerEnabled = false;
        SaveSettings();
        _ = ApplyUBlockOriginStateAsync();
        UpdateAdBlockButton();
    }

    private void UpdateAdBlockButton()
    {
        AdBlockBtn.Visibility = Visibility.Visible;
        var on = _settings.AdBlockerEnabled;
        var onBg = _isDarkMode
            ? Color.FromArgb(34, 129, 201, 149)
            : Color.FromArgb(22, 129, 201, 149);
        var offBg = _isDarkMode
            ? Color.FromArgb(18, 95, 99, 104)
            : Color.FromArgb(12, 95, 99, 104);
        AdBlockBtn.Background = new SolidColorBrush(on ? onBg : offBg);
        if (AdBlockOnIcon != null && AdBlockOffIcon != null)
        {
            AdBlockOnIcon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            AdBlockOffIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }
        AdBlockBtn.ToolTip = on
            ? "Ad blocker: enabled (click to disable)"
            : "Ad blocker: disabled (click to enable)";
        if (MenuAdBlockToggle != null)
        {
            MenuAdBlockToggle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        if (MenuAdBlockSeparator != null)
        {
            MenuAdBlockSeparator.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        if (MenuAdBlockToggleText != null)
        {
            MenuAdBlockToggleText.Text = on ? "Disable ad blocker" : "Enable ad blocker";
        }
    }

    private static readonly string[] _adBlockDomains =
    [
        // Google Ads
        "*://googlesyndication.com/*", "*://*.googlesyndication.com/*",
        "*://doubleclick.net/*",       "*://*.doubleclick.net/*",
        "*://googleadservices.com/*",  "*://*.googleadservices.com/*",
        "*://pagead2.googlesyndication.com/*",
        "*://adservice.google.com/*",  "*://*.adservice.google.*/*",
        "*://googletagmanager.com/*",  "*://*.googletagmanager.com/*",
        "*://googletagservices.com/*", "*://*.googletagservices.com/*",
        // Ad networks
        "*://adnxs.com/*",             "*://*.adnxs.com/*",
        "*://amazon-adsystem.com/*",   "*://*.amazon-adsystem.com/*",
        "*://media.net/*",             "*://*.media.net/*",
        "*://pubmatic.com/*",          "*://*.pubmatic.com/*",
        "*://openx.net/*",             "*://*.openx.net/*",
        "*://rubiconproject.com/*",    "*://*.rubiconproject.com/*",
        "*://casalemedia.com/*",       "*://*.casalemedia.com/*",
        "*://adsrvr.org/*",            "*://*.adsrvr.org/*",
        "*://moatads.com/*",           "*://*.moatads.com/*",
        "*://yieldmo.com/*",           "*://*.yieldmo.com/*",
        "*://criteo.com/*",            "*://*.criteo.com/*",
        "*://taboola.com/*",           "*://*.taboola.com/*",
        "*://outbrain.com/*",          "*://*.outbrain.com/*",
        "*://revcontent.com/*",        "*://*.revcontent.com/*",
        "*://lijit.com/*",             "*://*.lijit.com/*",
        "*://advertising.com/*",       "*://*.advertising.com/*",
        "*://adtech.com/*",            "*://*.adtech.com/*",
        "*://bidswitch.net/*",         "*://*.bidswitch.net/*",
        "*://contextweb.com/*",        "*://*.contextweb.com/*",
        "*://sharethrough.com/*",      "*://*.sharethrough.com/*",
        "*://triplelift.com/*",        "*://*.triplelift.com/*",
        "*://33across.com/*",          "*://*.33across.com/*",
        "*://sovrn.com/*",             "*://*.sovrn.com/*",
        "*://smartadserver.com/*",     "*://*.smartadserver.com/*",
        "*://teads.tv/*",              "*://*.teads.tv/*",
        "*://spotxchange.com/*",       "*://*.spotxchange.com/*",
        "*://spotx.tv/*",              "*://*.spotx.tv/*",
        "*://undertone.com/*",         "*://*.undertone.com/*",
        "*://adroll.com/*",            "*://*.adroll.com/*",
        "*://perfectmarket.com/*",     "*://*.perfectmarket.com/*",
        "*://mediavine.com/*",         "*://*.mediavine.com/*",
        // Trackers & analytics
        "*://google-analytics.com/*",  "*://*.google-analytics.com/*",
        "*://analytics.google.com/*",  "*://click.googleanalytics.com/*",
        "*://ssl.google-analytics.com/*",
        "*://connect.facebook.net/*",  "*://www.facebook.com/tr/*",
        "*://hotjar.com/*",            "*://*.hotjar.com/*",
        "*://hotjar.io/*",             "*://*.hotjar.io/*",
        "*://mouseflow.com/*",         "*://*.mouseflow.com/*",
        "*://mixpanel.com/*",          "*://*.mixpanel.com/*",
        "*://segment.com/*",           "*://*.segment.io/*",
        "*://amplitude.com/*",         "*://*.amplitude.com/*",
        "*://clarity.ms/*",            "*://*.clarity.ms/*",
        "*://scorecardresearch.com/*", "*://*.scorecardresearch.com/*",
        "*://comscore.com/*",          "*://*.comscore.com/*",
        "*://krxd.net/*",              "*://*.krxd.net/*",
        "*://chartbeat.com/*",         "*://*.chartbeat.com/*",
        "*://quantserve.com/*",        "*://*.quantserve.com/*",
        "*://everesttech.net/*",       "*://*.everesttech.net/*",
        "*://statcounter.com/*",       "*://*.statcounter.com/*",
        "*://mc.yandex.ru/*",          "*://metrika.yandex.ru/*",
        "*://newrelic.com/*",          "*://*.newrelic.com/*",
        "*://nr-data.net/*",           "*://*.nr-data.net/*",
        "*://heap.io/*",               "*://*.heapanalytics.com/*",
        "*://intercom.io/*",           "*://*.intercom.com/*",
        "*://crazyegg.com/*",          "*://*.crazyegg.com/*",
        "*://luckyorange.com/*",       "*://*.luckyorange.com/*",
        "*://inspectlet.com/*",        "*://*.inspectlet.com/*",
        "*://clicky.com/*",            "*://*.clicky.com/*",
        "*://woopra.com/*",            "*://*.woopra.com/*",
        // Error/session trackers
        "*://sentry.io/*",             "*://*.sentry.io/*",
        "*://browser.sentry-cdn.com/*","*://js.sentry-cdn.com/*",
        "*://bugsnag.com/*",           "*://*.bugsnag.com/*",
        "*://bugsnag-builds.s3.amazonaws.com/*",
        "*://d2wy8f7a9ursnm.cloudfront.net/*",
        "*://logrocket.com/*",         "*://*.logrocket.com/*",
        "*://fullstory.com/*",         "*://*.fullstory.com/*",
        "*://datadoghq.com/*",         "*://*.datadoghq.com/*",
        "*://datadog-browser-agent.com/*",
        "*://js-agent.newrelic.com/*", "*://bam.nr-data.net/*",
        "*://cdn.rollbar.com/*",       "*://*.rollbar.com/*",
        "*://raygun.com/*",            "*://*.raygun.com/*",
        "*://az416426.vo.msecnd.net/*",
        // Ads
        "*://adtago.s3.amazonaws.com/*", "*://analyticsengine.s3.amazonaws.com/*",
        "*://analytics.s3.amazonaws.com/*", "*://advice-ads.s3.amazonaws.com/*",
        "*://adcolony.com/*", "*://*.adcolony.com/*",
        "*://ads30.adcolony.com/*", "*://adc3-launch.adcolony.com/*",
        "*://events3alt.adcolony.com/*", "*://wd.adcolony.com/*",
        "*://pagead2.googlesyndication.com/*", "*://afs.googlesyndication.com/*",
        "*://adservice.google.com/*", "*://pagead2.googleadservices.com/*",
        "*://stats.g.doubleclick.net/*", "*://ad.doubleclick.net/*",
        "*://static.doubleclick.net/*", "*://m.doubleclick.net/*",
        "*://mediavisor.doubleclick.net/*",
        "*://static.media.net/*", "*://adservetx.media.net/*",
        // Analytics
        "*://freshmarketer.com/*", "*://*.freshmarketer.com/*",
        "*://claritybt.freshmarketer.com/*", "*://fwtracks.freshmarketer.com/*",
        "*://*.luckyorange.net/*", "*://stats.wp.com/*",
        "*://api.luckyorange.com/*", "*://realtime.luckyorange.com/*",
        "*://cdn.luckyorange.com/*", "*://w1.luckyorange.com/*",
        "*://upload.luckyorange.net/*", "*://cs.luckyorange.net/*",
        "*://settings.luckyorange.net/*",
        "*://adm.hotjar.com/*", "*://identify.hotjar.com/*",
        "*://insights.hotjar.com/*", "*://script.hotjar.com/*",
        "*://surveys.hotjar.com/*", "*://careers.hotjar.com/*",
        "*://events.hotjar.io/*",
        "*://cdn.mouseflow.com/*", "*://o2.mouseflow.com/*",
        "*://gtm.mouseflow.com/*", "*://api.mouseflow.com/*",
        "*://tools.mouseflow.com/*", "*://cdn-test.mouseflow.com/*",
        "*://analytics.google.com/*", "*://click.googleanalytics.com/*",
        "*://ssl.google-analytics.com/*",
        "*://app.getsentry.com/*", "*://browser.sentry-cdn.com/*",
        "*://notify.bugsnag.com/*", "*://sessions.bugsnag.com/*",
        "*://api.bugsnag.com/*", "*://app.bugsnag.com/*",
        // FreshWorks full suite
        "*://freshworks.com/*", "*://*.freshworks.com/*",
        "*://freshdesk.com/*",  "*://*.freshdesk.com/*",
        "*://freshchat.com/*",  "*://*.freshchat.com/*",
        "*://wchat.freshchat.com/*", "*://api.freshchat.com/*",
        // Social trackers
        "*://static.ads-twitter.com/*", "*://ads-api.twitter.com/*",
        "*://*.ads-twitter.com/*",
        "*://ads.linkedin.com/*", "*://analytics.pointdrive.linkedin.com/*",
        "*://ads.pinterest.com/*", "*://log.pinterest.com/*", "*://trk.pinterest.com/*",
        "*://events.reddit.com/*", "*://events.redditmedia.com/*",
        "*://ads.youtube.com/*",
        "*://ads-api.tiktok.com/*", "*://analytics.tiktok.com/*",
        "*://ads-sg.tiktok.com/*", "*://analytics-sg.tiktok.com/*",
        "*://business-api.tiktok.com/*", "*://ads.tiktok.com/*",
        "*://log.byteoversea.com/*",
        // Yahoo / Yandex / Unity
        "*://ads.yahoo.com/*", "*://analytics.yahoo.com/*", "*://geo.yahoo.com/*", "*://udcm.yahoo.com/*",
        "*://analytics.query.yahoo.com/*", "*://partnerads.ysm.yahoo.com/*",
        "*://log.fc.yahoo.com/*", "*://gemini.yahoo.com/*", "*://adtech.yahooinc.com/*",
        "*://extmaps-api.yandex.net/*", "*://appmetrica.yandex.ru/*",
        "*://adfstat.yandex.ru/*", "*://offerwall.yandex.net/*", "*://adfox.yandex.ru/*",
        "*://auction.unityads.unity3d.com/*", "*://webview.unityads.unity3d.com/*",
        "*://config.unityads.unity3d.com/*", "*://adserver.unityads.unity3d.com/*",
        // OEM trackers
        "*://iot-eu-logser.realme.com/*", "*://iot-logser.realme.com/*",
        "*://bdapi-ads.realmemobile.com/*", "*://bdapi-in-ads.realmemobile.com/*",
        "*://api.ad.xiaomi.com/*", "*://data.mistat.xiaomi.com/*",
        "*://data.mistat.india.xiaomi.com/*", "*://data.mistat.rus.xiaomi.com/*",
        "*://sdkconfig.ad.xiaomi.com/*", "*://sdkconfig.ad.intl.xiaomi.com/*",
        "*://tracking.rus.miui.com/*",
        "*://adsfs.oppomobile.com/*", "*://adx.ads.oppomobile.com/*",
        "*://ck.ads.oppomobile.com/*", "*://data.ads.oppomobile.com/*",
        "*://metrics.data.hicloud.com/*", "*://metrics2.data.hicloud.com/*",
        "*://grs.hicloud.com/*", "*://logservice.hicloud.com/*",
        "*://logservice1.hicloud.com/*", "*://logbak.hicloud.com/*",
        "*://click.oneplus.cn/*", "*://open.oneplus.net/*",
        "*://samsungads.com/*", "*://smetrics.samsung.com/*",
        "*://nmetrics.samsung.com/*", "*://samsung-com.112.2o7.net/*",
        "*://analytics-api.samsunghealthcn.com/*",
        "*://iadsdk.apple.com/*", "*://metrics.icloud.com/*",
        "*://metrics.mzstatic.com/*", "*://api-adservices.apple.com/*",
        "*://books-analytics-events.apple.com/*",
        "*://weather-analytics-events.apple.com/*",
        "*://notes-analytics-events.apple.com/*",
        // Additional high-value ad/tracking domains
        "*://bat.bing.com/*",          "*://c.bing.com/*",
        "*://adclick.g.doubleclick.net/*",
        "*://www.googleadservices.com/*",
        "*://tpc.googlesyndication.com/*",
        "*://surveymonkey.com/*", "*://*.surveymonkey.com/*",
        "*://zopim.com/*", "*://*.zopim.com/*",
        "*://snap.licdn.com/*", "*://platform.linkedin.com/analytics/*",
        "*://ct.pinterest.com/*",
        "*://alb.reddit.com/*",
        "*://pixel.reddit.com/*",
        "*://www.redditstatic.com/ads/*",
        "*://t.co/i/*",
        "*://jwpltx.com/*", "*://*.jwpltx.com/*",
        "*://jwpsrv.com/*", "*://*.jwpsrv.com/*",
        "*://freewheel.tv/*", "*://*.freewheel.tv/*",
        "*://cdn.flashtalking.com/*",
        "*://go.sonobi.com/*",
        "*://c2.taboola.com/*", "*://trc.taboola.com/*",
        "*://syndication.twitter.com/i/*",
        "*://t.myvisualiq.net/*",
        "*://secure.insightexpressai.com/*",
        "*://cdn.optimizely.com/*", "*://*.optimizely.com/*",
        "*://d.turn.com/*", "*://rpm.turn.com/*",
        "*://*.demdex.net/*",
        "*://*.bluekai.com/*",
        "*://*.exelator.com/*",
        "*://addthis.com/*", "*://*.addthis.com/*",
        "*://sharethis.com/*", "*://*.sharethis.com/*",
        "*://stickyadstv.com/*", "*://*.stickyadstv.com/*",
        "*://*.serving-sys.com/*",
        "*://ads.rubiconproject.com/*",
        "*://eb2.3lift.com/*",
        "*://sync.mathtag.com/*", "*://*.mathtag.com/*",
        "*://ads.yap.yahoo.com/*",
        "*://udc.yahoo.com/*",
        "*://munchkin.marketo.net/*", "*://munchkin.marketo.com/*",
        "*://js.hs-analytics.net/*", "*://js.hsforms.net/*", "*://js.hscta.net/*",
        "*://js.hubspot.com/*",  "*://*.hubspot.com/analytics/*",
        "*://pardot.com/*", "*://*.pardot.com/*",
        "*://marketo.com/*", "*://*.marketo.com/*",
        "*://eloqua.com/*", "*://*.eloqua.com/*",
        "*://bat.r.msn.com/*",
        "*://*.adsafeprotected.com/*",
        "*://*.doubleverify.com/*",
        "*://*.integral-assets.com/*",
        "*://*.adsafe.net/*",
        "*://ib.adnxs.com/*",
        "*://secure.adnxs.com/*",
        "*://aax.amazon-adsystem.com/*",
        "*://fls-na.amazon-adsystem.com/*",
        "*://c.amazon-adsystem.com/*",
        "*://*.demdex.net/*",
        // Missing domains from test results (87 accessible domains now blocked)
        "*://aan.amazon.com/*",
        "*://static.criteo.net/*",
        "*://mgid.com/*", "*://cdn.mgid.com/*", "*://servicer.mgid.com/*",
        "*://bingads.microsoft.com/*",
        "*://ads.microsoft.com/*",
        "*://liftoff.io/*",
        "*://cdn.indexexchange.com/*",
        "*://smartyads.com/*",
        "*://ad.gt/*",
        "*://eb2.3lift.com/*", "*://tlx.3lift.com/*", "*://apex.go.sonobi.com/*",
        "*://cdn.kargo.com/*",
        "*://sync.kargo.com/*",
        "*://pangleglobal.com/*",
        "*://s.youtube.com/*", "*://redirector.googlevideo.com/*", "*://youtubei.googleapis.com/*",
        "*://graph.facebook.com/*", "*://tr.facebook.com/*",
        "*://sc-analytics.appspot.com/*",
        "*://d.reddit.com/*",
        "*://ads-api.x.com/*", "*://ads.x.com/*",
        "*://pixel.quora.com/*",
        "*://px.srvcs.tumblr.com/*",
        "*://ads.vk.com/*", "*://vk.com/rtrg/*",
        "*://ad.mail.ru/*", "*://top-fwz1.mail.ru/*",
        "*://xp.apple.com/*",
        "*://ads.huawei.com/*",
        "*://data.mistat.india.xiaomi.com/*", "*://data.mistat.rus.xiaomi.com/*", "*://tracking.miui.com/*",
        "*://ngfts.lge.com/*",
        "*://smartclip.net/*",
        "*://vortex.data.microsoft.com/*",
        "*://device-metrics-us.amazon.com/*", "*://device-metrics-us-2.amazon.com/*", "*://mads-eu.amazon.com/*",
        "*://ads.roku.com/*",
        "*://app-measurement.com/*", "*://firebase-settings.crashlytics.com/*",
        "*://sdk.privacy-center.org/*",
        "*://app.usercentrics.eu/*",
        "*://shareasale-analytics.com/*",
        "*://impact.com/*", "*://d.impactradius-event.com/*", "*://api.impact.com/*",
        "*://www.awin1.com/*", "*://zenaps.com/*",
        "*://partnerstack.com/*", "*://api.partnerstack.com/*",
        "*://api.refersion.com/*",
        "*://cdn.dynamicyield.com/*",
        "*://track.hubspot.com/*",
        "*://trackcmp.net/*",
        "*://js.driftt.com/*",
        "*://imasdk.googleapis.com/*", "*://dai.google.com/*",
        "*://ssl.p.jwpcdn.com/*",
        "*://mssl.fwmrm.net/*",
        "*://tremorhub.com/*", "*://ads.tremorhub.com/*",
        "*://fpjs.io/*", "*://api.fpjs.io/*",
        "*://onetag-sys.com/*",
        "*://id5-sync.com/*",
        "*://thetradedesk.com/*",
        "*://prod.uidapi.com/*",
        "*://bnc.lt/*",
        "*://wzrkt.com/*",
        "*://clevertap-prod.com/*",
        "*://crypto-loot.org/*",
        "*://popads.net/*", "*://popcash.net/*", "*://onclickads.net/*", "*://popmyads.com/*", "*://trafficjunky.net/*", "*://juicyads.com/*",
        "*://greatis.com/*",
        "*://init.supersonicads.com/*",
        "*://api.fyber.com/*",
        "*://ironSource.mobi/*",
        "*://outcome-ssp.supersonicads.com/*",
        "*://cdn.cookielaw.org/*",
        "*://analytics.adobe.io/*",

        // ── Affiliate Networks ──────────────────────────────────────────
        "*://impactradius.com/*",      "*://*.impactradius.com/*",
        "*://impact.com/*",            "*://*.impact.com/*",
        "*://shareasale.com/*",        "*://*.shareasale.com/*",
        "*://cj.com/*",                "*://*.cj.com/*",
        "*://commission-junction.com/*","*://*.commission-junction.com/*",
        "*://dpbolvw.net/*",           "*://*.dpbolvw.net/*",
        "*://jdoqocy.com/*",           "*://*.jdoqocy.com/*",
        "*://kqzyfj.com/*",            "*://*.kqzyfj.com/*",
        "*://qksrv.net/*",             "*://*.qksrv.net/*",
        "*://tkqlhce.com/*",           "*://*.tkqlhce.com/*",
        "*://anrdoezrs.net/*",         "*://*.anrdoezrs.net/*",
        "*://awin.com/*",              "*://*.awin.com/*",
        "*://awin1.com/*",             "*://*.awin1.com/*",
        "*://zanox.com/*",             "*://*.zanox.com/*",
        "*://zanox-affiliate.de/*",    "*://*.zanox-affiliate.de/*",
        "*://tradedoubler.com/*",      "*://*.tradedoubler.com/*",
        "*://viglink.com/*",           "*://*.viglink.com/*",
        "*://skimlinks.com/*",         "*://*.skimlinks.com/*",
        "*://skimresources.com/*",     "*://*.skimresources.com/*",
        "*://go.skimresources.com/*",
        "*://pepperjam.com/*",         "*://*.pepperjam.com/*",
        "*://pjtra.com/*",             "*://pjatr.com/*",
        "*://avantlink.com/*",         "*://*.avantlink.com/*",
        "*://maxbounty.com/*",         "*://*.maxbounty.com/*",
        "*://partnerize.com/*",        "*://*.partnerize.com/*",
        "*://conversant.com/*",        "*://*.conversant.com/*",
        "*://conversantmedia.com/*",   "*://*.conversantmedia.com/*",
        "*://flexoffers.com/*",        "*://*.flexoffers.com/*",
        "*://webgains.com/*",          "*://*.webgains.com/*",
        "*://commissionfactory.com/*", "*://*.commissionfactory.com/*",
        "*://tune.com/*",              "*://*.tune.com/*",
        "*://hasoffers.com/*",         "*://*.hasoffers.com/*",
        "*://everflow.io/*",           "*://*.everflow.io/*",
        "*://affise.com/*",            "*://*.affise.com/*",
        "*://linkconnector.com/*",     "*://*.linkconnector.com/*",
        "*://cake.com/*",              "*://*.cake.com/*",
        "*://rakuten.com/*",           "*://*.rakuten.com/*",
        "*://linksynergy.com/*",       "*://*.linksynergy.com/*",
        "*://phpadsnew.com/*",         "*://*.phpadsnew.com/*",
        "*://affiliatefuture.com/*",   "*://*.affiliatefuture.com/*",
        "*://performancehorizon.com/*","*://*.performancehorizon.com/*",
        "*://offervault.com/*",        "*://*.offervault.com/*",
        "*://clickbooth.com/*",        "*://*.clickbooth.com/*",
        "*://clickbank.com/*",         "*://*.clickbank.com/*",
        "*://clkmon.com/*",            "*://*.clkmon.com/*",
        "*://clkrev.com/*",            "*://*.clkrev.com/*",
        "*://go2cloud.org/*",          "*://*.go2cloud.org/*",
        "*://trk.vindicosuite.com/*",
        "*://affiliatewindow.com/*",   "*://*.affiliatewindow.com/*",
        "*://2mdn.net/*",              "*://*.2mdn.net/*",
        "*://clicky.com/*",            "*://*.clicky.com/*",
        "*://refer.viglink.com/*",     "*://api.viglink.com/*",

        // ── Video Ads ───────────────────────────────────────────────────
        "*://jwplayer.com/*",          "*://*.jwplayer.com/*",
        "*://brightcove.com/*",        "*://*.brightcove.com/*",
        "*://springserve.com/*",       "*://*.springserve.com/*",
        "*://videoamp.com/*",          "*://*.videoamp.com/*",
        "*://unrulymedia.com/*",       "*://*.unrulymedia.com/*",
        "*://tremormedia.com/*",       "*://*.tremormedia.com/*",
        "*://tremorvideo.com/*",       "*://*.tremorvideo.com/*",
        "*://innovid.com/*",           "*://*.innovid.com/*",
        "*://vindico.com/*",           "*://*.vindico.com/*",
        "*://yume.com/*",              "*://*.yume.com/*",
        "*://extreme-reach.com/*",     "*://*.extreme-reach.com/*",
        "*://vidazoo.com/*",           "*://*.vidazoo.com/*",
        "*://connatix.com/*",          "*://*.connatix.com/*",
        "*://loopme.com/*",            "*://*.loopme.com/*",
        "*://gumgum.com/*",            "*://*.gumgum.com/*",
        "*://primis.tech/*",           "*://*.primis.tech/*",
        "*://adtelligent.com/*",       "*://*.adtelligent.com/*",
        "*://magnite.com/*",           "*://*.magnite.com/*",
        "*://springads.com/*",         "*://*.springads.com/*",
        "*://scanscout.com/*",         "*://*.scanscout.com/*",
        "*://adap.tv/*",               "*://*.adap.tv/*",
        "*://liverail.com/*",          "*://*.liverail.com/*",
        "*://videohub.tv/*",           "*://*.videohub.tv/*",
        "*://streamrail.com/*",        "*://*.streamrail.com/*",
        "*://aniview.com/*",           "*://*.aniview.com/*",

        // ── Consent Management Platforms ────────────────────────────────
        "*://onetrust.com/*",          "*://*.onetrust.com/*",
        "*://cookielaw.org/*",         "*://*.cookielaw.org/*",
        "*://cdn.cookielaw.org/*",
        "*://cookiebot.com/*",         "*://*.cookiebot.com/*",
        "*://consent.cookiebot.com/*",
        "*://trustarc.com/*",          "*://*.trustarc.com/*",
        "*://consent.trustarc.com/*",
        "*://truste.com/*",            "*://*.truste.com/*",
        "*://consensu.org/*",          "*://*.consensu.org/*",
        "*://quantcast.mgr.consensu.org/*",
        "*://consentmanager.net/*",    "*://*.consentmanager.net/*",
        "*://didomi.io/*",             "*://*.didomi.io/*",
        "*://sdk.privacy-center.org/*",
        "*://usercentrics.com/*",      "*://*.usercentrics.com/*",
        "*://iubenda.com/*",           "*://*.iubenda.com/*",
        "*://cookiefirst.com/*",       "*://*.cookiefirst.com/*",
        "*://osano.com/*",             "*://*.osano.com/*",
        "*://sourcepoint.com/*",       "*://*.sourcepoint.com/*",
        "*://evidon.com/*",            "*://*.evidon.com/*",
        "*://crownpeak.com/*",         "*://*.crownpeak.com/*",
        "*://cookie-script.com/*",     "*://*.cookie-script.com/*",
        "*://cookiehub.com/*",         "*://*.cookiehub.com/*",
        "*://termly.io/*",             "*://*.termly.io/*",
        "*://cookieyes.com/*",         "*://*.cookieyes.com/*",
        "*://complianz.io/*",          "*://*.complianz.io/*",
        "*://secureprivacy.ai/*",      "*://*.secureprivacy.ai/*",

        // ── Tracking & Fingerprinting ────────────────────────────────────
        "*://fingerprint.com/*",       "*://*.fingerprint.com/*",
        "*://fingerprintjs.com/*",     "*://*.fingerprintjs.com/*",
        "*://fpjscdn.net/*",           "*://*.fpjscdn.net/*",
        "*://cdn.fingerprint.com/*",
        "*://maxmind.com/*",           "*://*.maxmind.com/*",
        "*://threatmetrix.com/*",      "*://*.threatmetrix.com/*",
        "*://iovation.com/*",          "*://*.iovation.com/*",
        "*://sessioncam.com/*",        "*://*.sessioncam.com/*",
        "*://clicktale.com/*",         "*://*.clicktale.com/*",
        "*://contentsquare.com/*",     "*://*.contentsquare.com/*",
        "*://dynatrace.com/*",         "*://*.dynatrace.com/*",
        "*://sift.com/*",              "*://*.sift.com/*",
        "*://siftscience.com/*",       "*://*.siftscience.com/*",
        "*://perimeterx.com/*",        "*://*.perimeterx.com/*",
        "*://px-cdn.net/*",            "*://*.px-cdn.net/*",
        "*://px-cloud.net/*",          "*://*.px-cloud.net/*",
        "*://imperva.com/*",           "*://*.imperva.com/*",
        "*://distilnetworks.com/*",    "*://*.distilnetworks.com/*",
        "*://human.security/*",        "*://*.human.security/*",
        "*://tiqcdn.com/*",            "*://*.tiqcdn.com/*",
        "*://tags.tiqcdn.com/*",
        "*://tns-counter.ru/*",        "*://*.tns-counter.ru/*",
        "*://ipqualityscore.com/*",    "*://*.ipqualityscore.com/*",
        "*://deviceatlas.com/*",       "*://*.deviceatlas.com/*",
        "*://51degrees.com/*",         "*://*.51degrees.com/*",
        "*://forensiq.com/*",          "*://*.forensiq.com/*",
        "*://fraudlogix.com/*",        "*://*.fraudlogix.com/*",
        "*://tmx.com/*",               "*://*.tmx.com/*",
        "*://visitoridentification.net/*","*://*.visitoridentification.net/*",
        "*://augur.io/*",              "*://*.augur.io/*",
        "*://botd.fpjscdn.net/*",
        "*://liveramp.com/*",          "*://*.liveramp.com/*",
        "*://rlcdn.com/*",             "*://*.rlcdn.com/*",
        "*://agkn.com/*",              "*://*.agkn.com/*",
        "*://rapleaf.com/*",           "*://*.rapleaf.com/*",
        "*://neustar.biz/*",           "*://*.neustar.biz/*",
        "*://tapad.com/*",             "*://*.tapad.com/*",
        "*://drawbridge.com/*",        "*://*.drawbridge.com/*",
        "*://cross-pixel.com/*",       "*://*.cross-pixel.com/*",
        "*://totient.co/*",            "*://*.totient.co/*",

        // ── Cryptominers & Malware ──────────────────────────────────────
        "*://coinhive.com/*",          "*://*.coinhive.com/*",
        "*://coin-hive.com/*",         "*://*.coin-hive.com/*",
        "*://cryptoloot.pro/*",        "*://*.cryptoloot.pro/*",
        "*://minero.cc/*",             "*://*.minero.cc/*",
        "*://jsecoin.com/*",           "*://*.jsecoin.com/*",
        "*://monerominer.rocks/*",     "*://*.monerominer.rocks/*",
        "*://webmr.eu/*",              "*://*.webmr.eu/*",
        "*://coinimp.com/*",           "*://*.coinimp.com/*",
        "*://papoto.com/*",            "*://*.papoto.com/*",
        "*://cryptonight.pro/*",       "*://*.cryptonight.pro/*",
        "*://afminer.com/*",           "*://*.afminer.com/*",
        "*://coinerra.com/*",          "*://*.coinerra.com/*",
        "*://minerpool.net/*",         "*://*.minerpool.net/*",
        "*://nbminer.com/*",           "*://*.nbminer.com/*",
        "*://crypto-loot.com/*",       "*://*.crypto-loot.com/*",
        "*://deepminer.com/*",         "*://*.deepminer.com/*",
        "*://monero-miner.com/*",      "*://*.monero-miner.com/*",
        "*://xmrpool.net/*",           "*://*.xmrpool.net/*",
        "*://reasedoper.pw/*",         "*://*.reasedoper.pw/*",

        // ── Email Tracking ──────────────────────────────────────────────
        "*://opens.mailchimp.com/*",   "*://list-manage.com/*",
        "*://tracking.sendgrid.net/*", "*://*.sendgrid.net/*",
        "*://mailgun.org/*",           "*://*.mailgun.org/*",
        "*://sparkpostmail.com/*",     "*://*.sparkpostmail.com/*",
        "*://sailthru.com/*",          "*://*.sailthru.com/*",
        "*://litmus.com/*",            "*://*.litmus.com/*",
        "*://vero.co/*",               "*://*.vero.co/*",
        "*://customer.io/*",           "*://*.customer.io/*",
        "*://klaviyo.com/*",           "*://*.klaviyo.com/*",
        "*://drip.com/*",              "*://*.drip.com/*",
        "*://convertkit.com/*",        "*://*.convertkit.com/*",
        "*://activecampaign.com/*",    "*://*.activecampaign.com/*",
        "*://constantcontact.com/*",   "*://*.constantcontact.com/*",
        "*://mailjet.com/*",           "*://*.mailjet.com/*",
        "*://emailtracking.io/*",      "*://*.emailtracking.io/*",
        "*://trk.email/*",             "*://*.trk.email/*",
        "*://stripo.email/*",          "*://*.stripo.email/*",

        // ── A/B Testing ─────────────────────────────────────────────────
        "*://abtasty.com/*",           "*://*.abtasty.com/*",
        "*://vwo.com/*",               "*://*.vwo.com/*",
        "*://convert.com/*",           "*://*.convert.com/*",
        "*://kameleoon.com/*",         "*://*.kameleoon.com/*",
        "*://unbounce.com/*",          "*://*.unbounce.com/*",
        "*://qubit.com/*",             "*://*.qubit.com/*",
        "*://conductrics.com/*",       "*://*.conductrics.com/*",
        "*://monetate.net/*",          "*://*.monetate.net/*",
        "*://richrelevance.com/*",     "*://*.richrelevance.com/*",

        // ── More Social Trackers ────────────────────────────────────────
        "*://platform.twitter.com/widgets/*",
        "*://snap.com/*",              "*://*.sc-static.net/*",
        "*://tr.snapchat.com/*",
        "*://static.xx.fbcdn.net/rsrc.php/v3/ads/*",
        "*://graph.facebook.com/*/activities/*",
        "*://twitter.com/i/adsct/*",
        "*://t.co/i/adsct/*",
        "*://linkedin.com/li/track/*",
        "*://sherpany.com/*",          "*://*.sherpany.com/*",
        "*://social-analytics.io/*",   "*://*.social-analytics.io/*",
        "*://socialsignin.com/*",      "*://*.socialsignin.com/*",

        // ── More Ad Networks ────────────────────────────────────────────
        "*://indexww.com/*",           "*://*.indexww.com/*",
        "*://lkqd.net/*",              "*://*.lkqd.net/*",
        "*://districtm.io/*",          "*://*.districtm.io/*",
        "*://districtm.ca/*",          "*://*.districtm.ca/*",
        "*://epom.com/*",              "*://*.epom.com/*",
        "*://admob.com/*",             "*://*.admob.com/*",
        "*://inmobi.com/*",            "*://*.inmobi.com/*",
        "*://mopub.com/*",             "*://*.mopub.com/*",
        "*://applovin.com/*",          "*://*.applovin.com/*",
        "*://ironsource.com/*",        "*://*.ironsource.com/*",
        "*://vungle.com/*",            "*://*.vungle.com/*",
        "*://chartboost.com/*",        "*://*.chartboost.com/*",
        "*://startapp.com/*",          "*://*.startapp.com/*",
        "*://ogury.com/*",             "*://*.ogury.com/*",
        "*://propellerads.com/*",      "*://*.propellerads.com/*",
        "*://trafficjunky.net/*",      "*://*.trafficjunky.net/*",
        "*://exoclick.com/*",          "*://*.exoclick.com/*",
        "*://juicyads.com/*",          "*://*.juicyads.com/*",
        "*://hilltopads.net/*",        "*://*.hilltopads.net/*",
        "*://popads.net/*",            "*://*.popads.net/*",
        "*://popcash.net/*",           "*://*.popcash.net/*",
        "*://admaven.com/*",           "*://*.admaven.com/*",
        "*://yieldlove.com/*",         "*://*.yieldlove.com/*",
        "*://adnium.com/*",            "*://*.adnium.com/*",
        "*://trafficstars.com/*",      "*://*.trafficstars.com/*",
        "*://seedtag.com/*",           "*://*.seedtag.com/*",
        "*://zemanta.com/*",           "*://*.zemanta.com/*",
        "*://adform.net/*",            "*://*.adform.net/*",
        "*://adform.com/*",            "*://*.adform.com/*",
        "*://nextperf.com/*",          "*://*.nextperf.com/*",
        "*://smartclip.net/*",         "*://*.smartclip.net/*",
        "*://appier.com/*",            "*://*.appier.com/*",
        "*://adjust.com/*",            "*://*.adjust.com/*",
        "*://appsflyer.com/*",         "*://*.appsflyer.com/*",
        "*://kochava.com/*",           "*://*.kochava.com/*",
        "*://branch.io/*",             "*://*.branch.io/*",
        "*://singular.net/*",          "*://*.singular.net/*",
        "*://tenjin.io/*",             "*://*.tenjin.io/*",
        "*://attributionapp.com/*",    "*://*.attributionapp.com/*",
        "*://moat.com/*",              "*://*.moat.com/*",
        "*://adalyser.com/*",          "*://*.adalyser.com/*",
        "*://integral-assets.com/*",   "*://*.integral-assets.com/*",
        "*://gwiq.com/*",              "*://*.gwiq.com/*",
        "*://adsymptotic.com/*",       "*://*.adsymptotic.com/*",
        "*://nrich.ai/*",              "*://*.nrich.ai/*",

        // ── More OEM Vendors ────────────────────────────────────────────
        // Vivo
        "*://analytics.vivo.com.cn/*", "*://sa.vivo.com.cn/*",
        "*://tracking.vivo.com/*",     "*://log.vivo.com.cn/*",
        "*://push.vivo.com.cn/*",      "*://adv.vivo.com.cn/*",
        "*://cm.vivo.com.cn/*",        "*://analytics-sg.vivo.com/*",
        // LG
        "*://lganalytics.com/*",       "*://*.lganalytics.com/*",
        "*://lge.com/analytics*",      "*://tracking.lge.com/*",
        "*://ads.lge.com/*",           "*://stats.lge.com/*",
        "*://lgtvsdp.com/*",           "*://*.lgtvsdp.com/*",
        "*://lgsmartad.com/*",         "*://*.lgsmartad.com/*",
        "*://smartshare.lgappstv.com/*","*://ibis.lgappstv.com/*",
        // Motorola
        "*://analytics.motorola.com/*","*://tracking.motorola.com/*",
        "*://moto-analytics.com/*",    "*://*.moto-analytics.com/*",
        "*://motorola-analytics.com/*","*://*.motog.motorola.com/ads/*",
        // Sony
        "*://sony-analytics.com/*",    "*://*.sony-analytics.com/*",
        "*://analyticsservices.sony.com/*",
        "*://ad.sonyentertainmentnetwork.com/*",
        "*://ps-metrics.sonyentertainmentnetwork.com/*",
        "*://tele.sonyentertainmentnetwork.com/*",
        // Lenovo
        "*://analytics.lenovo.com/*",  "*://track.lenovo.com/*",
        "*://collector.lenovo.com/*",  "*://adv.lenovo.com/*",
        "*://ads.lenovo.com/*",
        // ASUS
        "*://analytics.asus.com/*",    "*://tracking.asus.com/*",
        "*://metrics.asus.com/*",      "*://ads.asus.com/*",
        "*://splashads.asus.com/*",    "*://asus-splashads.com/*",
        // Nokia / HMD Global
        "*://analytics.hmdglobal.com/*","*://track.hmdglobal.com/*",
        "*://nokia-analytics.com/*",   "*://*.nokia-analytics.com/*",
        // HTC
        "*://analytics.htc.com/*",     "*://tracking.htc.com/*",
        "*://ads.htc.com/*",           "*://htcmetrics.com/*",
        // Wiko
        "*://analytics.wikozone.com/*","*://tracking.wiko.com/*",
        // TCL
        "*://analytics.tcl.com/*",     "*://track.tcl.com/*",
        "*://ad.tcl.com/*",

        // ── More Social Trackers (subdomains & variants) ─────────────────
        // Facebook/Instagram
        "*://connect.facebook.net/*",
        "*://web.facebook.com/tr*",
        "*://graph.instagram.com/*",
        "*://i.instagram.com/*",
        "*://pixel.facebook.com/*",
        // Twitter/X
        "*://analytics.twitter.com/*",
        "*://t.co/i/*",
        "*://platform.twitter.com/*",
        "*://cdn.syndication.twimg.com/*",
        "*://syndication.twitter.com/*",
        // LinkedIn
        "*://snap.licdn.com/*",
        "*://px.ads.linkedin.com/*",
        "*://dc.ads.linkedin.com/*",
        "*://platform.linkedin.com/*",
        // Snapchat
        "*://tr.snapchat.com/*",
        "*://sc-static.net/*",         "*://*.sc-static.net/*",
        "*://snapads.com/*",           "*://*.snapads.com/*",
        "*://businesshelp.snapchat.com/ads*",
        // Pinterest more
        "*://analytics.pinterest.com/*",
        "*://widgets.pinterest.com/analytics*",
        // YouTube
        "*://s.youtube.com/api/stats/ads*",
        "*://www.youtube.com/pagead*",
        // Twitch
        "*://spade.twitch.tv/*",       "*://ads.twitch.tv/*",
        "*://static.ads.twitch.tv/*",
        // Discord (tracking)
        "*://discordapp.com/api/science*",
        "*://discord.com/api/science*",

        // ── More Cryptominers & Malware ──────────────────────────────────
        "*://minergate.com/*",         "*://*.minergate.com/*",
        "*://nicehash.com/*",          "*://*.nicehash.com/*",
        "*://2giga.link/*",            "*://*.2giga.link/*",
        "*://hashfor.cash/*",          "*://*.hashfor.cash/*",
        "*://coin-have.com/*",         "*://*.coin-have.com/*",
        "*://cryptobara.com/*",        "*://*.cryptobara.com/*",
        "*://xmrpool.eu/*",            "*://*.xmrpool.eu/*",
        "*://supportxmr.com/*",        "*://*.supportxmr.com/*",
        "*://monerocean.stream/*",     "*://*.monerocean.stream/*",
        "*://hashvault.pro/*",         "*://*.hashvault.pro/*",
        "*://xmrig.com/*",             "*://*.xmrig.com/*",
        "*://3aliansso.com/*",         "*://*.3aliansso.com/*",
        "*://coinblind.com/*",         "*://*.coinblind.com/*",
        "*://gridcash.net/*",          "*://*.gridcash.net/*",
        "*://miner.rocks/*",           "*://*.miner.rocks/*",
        "*://lmodr.biz/*",             "*://*.lmodr.biz/*",
        "*://listat.biz/*",            "*://*.listat.biz/*",
        "*://scriptzol.xyz/*",         "*://*.scriptzol.xyz/*",
        "*://cfts.pw/*",               "*://*.cfts.pw/*",

        // ── More Video Ads ───────────────────────────────────────────────
        "*://ooyala.com/*",            "*://*.ooyala.com/*",
        "*://brightroll.com/*",        "*://*.brightroll.com/*",
        "*://beachfront.com/*",        "*://*.beachfront.com/*",
        "*://verve.com/*",             "*://*.verve.com/*",
        "*://rhythmone.com/*",         "*://*.rhythmone.com/*",
        "*://360yield.com/*",          "*://*.360yield.com/*",
        "*://undertone.com/*",         "*://*.undertone.com/*",
        "*://yieldmo.com/*",           "*://*.yieldmo.com/*",
        "*://xumo.tv/*",               "*://*.xumo.tv/*",
        "*://appads.com/*",            "*://*.appads.com/*",
        "*://videologygroup.com/*",    "*://*.videologygroup.com/*",
        "*://playwire.com/*",          "*://*.playwire.com/*",
        "*://synacor.com/*",           "*://*.synacor.com/*",
        "*://freewheel.tv/*",          "*://*.freewheel.tv/*",
        "*://stickyadstv.com/*",       "*://*.stickyadstv.com/*",

        // ── More Tracking & Fingerprinting ───────────────────────────────
        "*://forter.com/*",            "*://*.forter.com/*",
        "*://riskiq.com/*",            "*://*.riskiq.com/*",
        "*://inauth.com/*",            "*://*.inauth.com/*",
        "*://accertify.com/*",         "*://*.accertify.com/*",
        "*://kount.com/*",             "*://*.kount.com/*",
        "*://signifyd.com/*",          "*://*.signifyd.com/*",
        "*://bounceexchange.com/*",    "*://*.bounceexchange.com/*",
        "*://wunderkind.co/*",         "*://*.wunderkind.co/*",
        "*://semasio.net/*",           "*://*.semasio.net/*",
        "*://eyeota.com/*",            "*://*.eyeota.com/*",
        "*://weborama.com/*",          "*://*.weborama.com/*",
        "*://pippio.com/*",            "*://*.pippio.com/*",
        "*://nexac.com/*",             "*://*.nexac.com/*",
        "*://netmng.com/*",            "*://*.netmng.com/*",
        "*://audienceinsights.net/*",  "*://*.audienceinsights.net/*",
        "*://creativecdn.com/*",       "*://*.creativecdn.com/*",
        "*://4dex.io/*",               "*://*.4dex.io/*",
        "*://bfmio.com/*",             "*://*.bfmio.com/*",
        "*://zergnet.com/*",           "*://*.zergnet.com/*",
        "*://tremorhub.com/*",         "*://*.tremorhub.com/*",
        "*://openx.com/*",             "*://*.openx.com/*",
        "*://permutive.com/*",         "*://*.permutive.com/*",

        // ── More Consent Management ──────────────────────────────────────
        "*://privacymanager.io/*",     "*://*.privacymanager.io/*",
        "*://traffective.com/*",       "*://*.traffective.com/*",
        "*://cookieinformation.com/*", "*://*.cookieinformation.com/*",
        "*://borlabs-cookie.de/*",     "*://*.borlabs-cookie.de/*",
        "*://consentframework.com/*",  "*://*.consentframework.com/*",
        "*://uniconsent.com/*",        "*://*.uniconsent.com/*",
        "*://cdn.privacy-mgmt.com/*",

        // ── More Affiliate Networks ──────────────────────────────────────
        "*://go2speed.org/*",          "*://*.go2speed.org/*",
        "*://financeads.net/*",        "*://*.financeads.net/*",
        "*://affilinet.com/*",         "*://*.affilinet.com/*",
        "*://belboon.com/*",           "*://*.belboon.com/*",
        "*://adcell.de/*",             "*://*.adcell.de/*",
        "*://tradetracker.com/*",      "*://*.tradetracker.com/*",
        "*://admitad.com/*",           "*://*.admitad.com/*",
        "*://cityads.com/*",           "*://*.cityads.com/*",
        "*://leadbit.com/*",           "*://*.leadbit.com/*",
        "*://marketgid.com/*",         "*://*.marketgid.com/*",
        "*://cpalead.com/*",           "*://*.cpalead.com/*",
        "*://cpaway.com/*",            "*://*.cpaway.com/*",
        "*://offerwall.io/*",          "*://*.offerwall.io/*",
        "*://avangate.com/*",          "*://*.avangate.com/*",
        "*://2checkout.com/*",         "*://*.2checkout.com/*",

        // ── More Email Tracking ──────────────────────────────────────────
        "*://postmarkapp.com/*",       "*://*.postmarkapp.com/*",
        "*://mandrillapp.com/*",       "*://*.mandrillapp.com/*",
        "*://campaignmonitor.com/*",   "*://*.campaignmonitor.com/*",
        "*://createsend.com/*",        "*://*.createsend.com/*",
        "*://aweber.com/*",            "*://*.aweber.com/*",
        "*://infusionsoft.com/*",      "*://*.infusionsoft.com/*",
        "*://keap.com/*",              "*://*.keap.com/*",
        "*://mailer-analytics.net/*",  "*://*.mailer-analytics.net/*",
        "*://emailtracker.website/*",  "*://*.emailtracker.website/*",
        "*://whoreadme.com/*",         "*://*.whoreadme.com/*",
        "*://bananatag.com/*",         "*://*.bananatag.com/*",
        "*://getnotify.com/*",         "*://*.getnotify.com/*",
        "*://yesware.com/*",           "*://*.yesware.com/*",

        // ── More A/B Testing ─────────────────────────────────────────────
        "*://launchdarkly.com/*",      "*://*.launchdarkly.com/*",
        "*://split.io/*",              "*://*.split.io/*",
        "*://statsig.com/*",           "*://*.statsig.com/*",
        "*://growthbook.io/*",         "*://*.growthbook.io/*",
        "*://flagship.io/*",           "*://*.flagship.io/*",
        "*://apptimize.com/*",         "*://*.apptimize.com/*",
        
        // ── AGGRESSIVE CATCH-ALL PATTERNS FOR REMAINING ACCESSIBLE DOMAINS ──
        // These patterns specifically target domains from test results that were accessible
        "*://aan.amazon.com/*",
        "*://static.criteo.net/*",
        "*://cdn.mgid.com/*",          "*://servicer.mgid.com/*",
        "*://bingads.microsoft.com/*",
        "*://liftoff.io/*",
        "*://cdn.indexexchange.com/*",
        "*://smartyads.com/*",         "*://ad.gt/*",
        "*://tlx.3lift.com/*",         "*://apex.go.sonobi.com/*",
        "*://sync.kargo.com/*",
        "*://pangleglobal.com/*",
        "*://redirector.googlevideo.com/*",
        "*://youtubei.googleapis.com/*",
        "*://analytics.adobe.io/*",
        "*://fpjs.io/*",               "*://api.fpjs.io/*",
        "*://onetag-sys.com/*",
        "*://id5-sync.com/*",
        "*://prod.uidapi.com/*",
        "*://bnc.lt/*",
        "*://graph.facebook.com/*",    "*://tr.facebook.com/*",
        "*://sc-analytics.appspot.com/*",
        "*://d.reddit.com/*",
        "*://pixel.quora.com/*",
        "*://px.srvcs.tumblr.com/*",
        "*://ads.vk.com/*",            "*://vk.com/rtrg*",
        "*://ad.mail.ru/*",            "*://top-fwz1.mail.ru/*",
        "*://xp.apple.com/*",
        "*://ads.huawei.com/*",
        "*://data.mistat.india.xiaomi.com/*",
        "*://data.mistat.rus.xiaomi.com/*",
        "*://tracking.miui.com/*",
        "*://ngfts.lge.com/*",
        "*://smartclip.net/*",
        "*://vortex.data.microsoft.com/*",
        "*://device-metrics-us.amazon.com/*",
        "*://device-metrics-us-2.amazon.com/*",
        "*://mads-eu.amazon.com/*",
        "*://ads.roku.com/*",
        "*://app-measurement.com/*",
        "*://firebase-settings.crashlytics.com/*",
        "*://sdk.privacy-center.org/*",
        "*://app.usercentrics.eu/*",
        "*://shareasale-analytics.com/*",
        "*://d.impactradius-event.com/*",
        "*://api.impact.com/*",
        "*://www.awin1.com/*",
        "*://zenaps.com/*",
        "*://partnerstack.com/*",      "*://api.partnerstack.com/*",
        "*://api.refersion.com/*",
        "*://cdn.dynamicyield.com/*",
        "*://track.hubspot.com/*",
        "*://trackcmp.net/*",
        "*://js.driftt.com/*",
        "*://imasdk.googleapis.com/*",
        "*://dai.google.com/*",
        "*://ssl.p.jwpcdn.com/*",
        "*://mssl.fwmrm.net/*",
        "*://ads.tremorhub.com/*",
        "*://init.supersonicads.com/*",
        "*://api.fyber.com/*",
        "*://ironSource.mobi/*",       "*://ironsource.mobi/*",
        "*://outcome-ssp.supersonicads.com/*",
        "*://crypto-loot.org/*",
        "*://popads.net/*",            "*://popcash.net/*",
        "*://onclickads.net/*",
        "*://popmyads.com/*",
        "*://trafficjunky.net/*",
        "*://juicyads.com/*",
        "*://greatis.com/*",

        // ── d3ward adblock test — ALL tested domains ────────────────────
        "*://adtago.s3.amazonaws.com/*",
        "*://analyticsengine.s3.amazonaws.com/*",
        "*://analytics.s3.amazonaws.com/*",
        "*://advice-ads.s3.amazonaws.com/*",
        "*://widget.privy.com/*",
        "*://c.amazon-adsystem.com/*",
        "*://s.amazon-adsystem.com/*",
        "*://an.facebook.com/*",
        "*://pixel.facebook.com/*",
        "*://staticxx.facebook.com/*",
        "*://www.facebook.com/tr*",
        "*://www.facebook.com/tr/*",
        "*://pixel.quantcount.com/*",
        "*://pixel.quantserve.com/*",
        "*://segment.quantserve.com/*",
        "*://rules.quantcount.com/*",
        "*://pixel.adsafeprotected.com/*",
        "*://static.adsafeprotected.com/*",
        "*://fw.adsafeprotected.com/*",
        "*://data.adsafeprotected.com/*",
        "*://dt.adsafeprotected.com/*",
        "*://cdn.doubleverify.com/*",
        "*://rtb.doubleverify.com/*",
        "*://pixel.doubleverify.com/*",
        "*://tps.doubleverify.com/*",
        "*://cdn3.doubleverify.com/*",
        "*://cdn.krxd.net/*",
        "*://beacon.krxd.net/*",
        "*://consumer.krxd.net/*",
        "*://usermatch.krxd.net/*",
        "*://apiservices.krxd.net/*",
        "*://pixel.everesttech.net/*",
        "*://dsum-sec.casalemedia.com/*",
        "*://ssum-sec.casalemedia.com/*",
        "*://ssum.casalemedia.com/*",
        "*://pixel.rubiconproject.com/*",
        "*://fastlane.rubiconproject.com/*",
        "*://optimized-by.rubiconproject.com/*",
        "*://prebid-server.rubiconproject.com/*",
        "*://token.rubiconproject.com/*",
        "*://geo.moatads.com/*",
        "*://px.moatads.com/*",
        "*://js.moatads.com/*",
        "*://mb.moatads.com/*",
        "*://pixel.moatads.com/*",
        "*://s.pubmine.com/*",
        "*://ad.turn.com/*",
        "*://d.turn.com/*",
        "*://r.turn.com/*",
        "*://rpm.turn.com/*",
        "*://cm.g.doubleclick.net/*",
        "*://simage2.pubmatic.com/*",
        "*://image2.pubmatic.com/*",
        "*://image4.pubmatic.com/*",
        "*://image6.pubmatic.com/*",
        "*://hbopenbid.pubmatic.com/*",
        "*://ads.pubmatic.com/*",
        "*://t.pubmatic.com/*",
        "*://ow.pubmatic.com/*",
        "*://us-u.openx.net/*",
        "*://uk-ads.openx.net/*",
        "*://rtb.openx.net/*",
        "*://u.openx.net/*",
        "*://usermatch.openx.com/*",
        "*://delivery.adnuntius.com/*",
        "*://data.adnuntius.com/*",
        "*://adnuntius.com/*",          "*://*.adnuntius.com/*",
        "*://ads.bridgewell.com/*",     "*://*.bridgewell.com/*",
        "*://tg1.clevertap-prod.com/*",
        "*://wzrkt.com/*",              "*://*.wzrkt.com/*",
        "*://cdn.concert.io/*",         "*://concert.io/*",
        "*://bam-cell.nr-data.net/*",
        "*://securepubads.g.doubleclick.net/*",
        "*://eus.rubiconproject.com/*",
        "*://idsync.rlcdn.com/*",
        "*://p.adsymptotic.com/*",
        "*://static.cloudflareinsights.com/*",
        "*://cdn.speedcurve.com/*",
        "*://cdn.segment.com/*",
        "*://api.segment.io/*",
        "*://cdn.heapanalytics.com/*",
        "*://heapanalytics.com/*",
        "*://cdn-3.convertexperiments.com/*",
        "*://cdn.mxpnl.com/*",
        "*://api-js.mixpanel.com/*",
        "*://decide.mixpanel.com/*",
        "*://bat.bing.com/*",
        "*://c.bing.com/*",
        "*://bat.r.msn.com/*",
        "*://a.clarity.ms/*",
        "*://c.clarity.ms/*",
        "*://d.clarity.ms/*",
        "*://js.monitor.azure.com/*",
        "*://cdn.cookielaw.org/*",
        "*://geolocation.onetrust.com/*",
        "*://privacyportal.onetrust.com/*",
        "*://optanon.blob.core.windows.net/*",
        "*://consent.cookiebot.com/*",
        "*://consentcdn.cookiebot.com/*",
        "*://cdn.privacy-mgmt.com/*",
        "*://wrapper.sp-prod.net/*",
        "*://sourcepoint.mgr.consensu.org/*",
        "*://quantcast.mgr.consensu.org/*",
        "*://cmpv2.mgr.consensu.org/*",
        "*://vendorlist.consensu.org/*",

        // ── More d3ward domains (additional categories) ──────────────────
        "*://s.pinimg.com/*",
        "*://ct.pinterest.com/*",
        "*://widgets.pinterest.com/*",
        "*://log.pinterest.com/*",
        "*://trk.pinterest.com/*",
        "*://assets.pinterest.com/*",
        "*://api.amplitude.com/*",
        "*://cdn.amplitude.com/*",
        "*://api2.amplitude.com/*",
        "*://static.hotjar.com/*",
        "*://vars.hotjar.com/*",
        "*://vc.hotjar.io/*",
        "*://in.hotjar.com/*",
        "*://ws.hotjar.com/*",
        "*://t.clarity.ms/*",

        // ── Broader wildcard patterns ──────────────────────────────────
        "*://*.adnxs.com/*",
        "*://*.adsrvr.org/*",
        "*://*.bidswitch.net/*",
        "*://*.casalemedia.com/*",
        "*://*.demdex.net/*",
        "*://*.doubleclick.net/*",
        "*://*.everesttech.net/*",
        "*://*.krxd.net/*",
        "*://*.moatads.com/*",
        "*://*.openx.net/*",
        "*://*.pubmatic.com/*",
        "*://*.quantserve.com/*",
        "*://*.rubiconproject.com/*",
        "*://*.scorecardresearch.com/*",
        "*://*.serving-sys.com/*",
        "*://*.turn.com/*",
        "*://*.2mdn.net/*",
        "*://*.adsafeprotected.com/*",
        "*://*.doubleverify.com/*",
        "*://*.mathtag.com/*",
        "*://*.nr-data.net/*",
        "*://*.sentry-cdn.com/*",
        "*://*.sentry.io/*",
        "*://*.amplitude.com/*",
        "*://*.clarity.ms/*",
        "*://*.segment.io/*",
        "*://*.segment.com/*",
        "*://*.mixpanel.com/*",
        "*://*.heapanalytics.com/*",
        // Broad tracker/ad keyword fallbacks
        "*://*/*ad*/*", "*://*/*ads*/*", "*://*/*banner*/*", "*://*/*sponsor*/*",
        "*://*/*promo*/*", "*://*/*tracker*/*", "*://*/*analytics*/*", "*://*/*pixel*/*",
        "*://*/*preroll*/*", "*://*/*midroll*/*", "*://*/*popunder*/*",
        "*://*/*doubleclick*/*", "*://*/*taboola*/*", "*://*/*outbrain*/*",
        "*://*/*adserver*/*", "*://*/*adsystem*/*", "*://*/*adservice*/*",
    ];

    private string GetAdBlockerEarlyScript()
    {
        if (!_settings.AdBlockerEnabled) return "void 0;";
        var jsPath = IoPath.Combine(RendererPath, "adblocker_early.js");
        try
        {
            if (File.Exists(jsPath)) return File.ReadAllText(jsPath);
        }
        catch { }
        return "(function(){ /* adblocker script unavailable */ })();\n";
    }


    private Task<CoreWebView2Environment> CreateWebViewEnvironment(string dataFolder)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            AdditionalBrowserArguments = string.Join(" ", new[]
            {
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-breakpad",
                "--disable-hang-monitor",
                "--disable-renderer-accessibility",
                "--renderer-process-limit=4",
                "--process-per-site",
                "--disable-site-isolation-trials",
                "--disable-features=msEdgeShoppingAssistant,msEdgeCollections,msEdgeFollow,msEdgeSidebarV2,msWebOOUI,IsolateOrigins,site-per-process,CalculateNativeWinOcclusion,OptimizationHints,MediaRouter,AutofillServerCommunication,InterestFeedContentSuggestions"
            })
        };
        return CoreWebView2Environment.CreateAsync(null, dataFolder, options);
    }

    private async Task EnsureUBlockOriginInstalledAsync(WebView2 webView, string dataFolder)
    {
        if (_installedBrowserExtensionProfiles.Contains(dataFolder)) return;
        if (!_settings.AdBlockerEnabled)
        {
            try
            {
                var existing = await GetUBlockOriginExtensionAsync(webView);
                if (existing != null)
                    await existing.EnableAsync(false);
            }
            catch { }
            _installedBrowserExtensionProfiles.Add(dataFolder);
            return;
        }

        var extensionPath = UBlockExtensionPath;
        if (!Directory.Exists(extensionPath)) return;

        try
        {
            var extension = await GetUBlockOriginExtensionAsync(webView);
            if (extension == null)
                extension = await webView.CoreWebView2.Profile.AddBrowserExtensionAsync(extensionPath);
            await extension.EnableAsync(_settings.AdBlockerEnabled);
            App.WriteTrace($"uBlock Origin {( _settings.AdBlockerEnabled ? "enabled" : "disabled")} for {(dataFolder.Contains("Incognito", StringComparison.OrdinalIgnoreCase) ? "incognito" : "default")} profile");
            _installedBrowserExtensionProfiles.Add(dataFolder);
        }
        catch (Exception ex)
        {
            App.WriteTrace($"uBlock Origin install failed: {ex.Message}");
            ErrorReporter.Track("UBlockInstallFail", new()
            {
                ["msg"] = ex.Message,
                ["profile"] = dataFolder.Contains("Incognito", StringComparison.OrdinalIgnoreCase) ? "incognito" : "default"
            });
        }
    }

    private static async Task<CoreWebView2BrowserExtension?> GetUBlockOriginExtensionAsync(WebView2 webView)
    {
        try
        {
            var extensions = await webView.CoreWebView2.Profile.GetBrowserExtensionsAsync();
            return extensions.FirstOrDefault(ext =>
                ext.Name.Contains("uBlock Origin", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshBrowserExtensionsUiAsync(CoreWebView2? core)
    {
        if (core == null) return;

        try
        {
            var extensions = await core.Profile.GetBrowserExtensionsAsync();
            var uiState = LoadExtensionUiState();
            var payload = extensions
                .Where(ext => !uiState.TryGetValue(ext.Id, out var state) || !state.Removed)
                .Select(ext => new
                {
                    id = ext.Id,
                    name = ext.Name,
                    enabled = uiState.TryGetValue(ext.Id, out var state) ? !state.Disabled : ext.IsEnabled,
                    hasPopup = !string.IsNullOrWhiteSpace(GetExtensionPopupRelativePath(ext.Id, ext.Name))
                })
                .ToList();

            var json = JsonSerializer.Serialize(payload);
            await core.ExecuteScriptAsync($"window.setExtensions && window.setExtensions({json})");
        }
        catch { }
    }

    private async Task InstallBrowserExtensionAsync(CoreWebView2? core, string extensionFolderPath)
    {
        if (core == null) return;
        if (string.IsNullOrWhiteSpace(extensionFolderPath) || !Directory.Exists(extensionFolderPath)) return;

        try
        {
            var extension = await core.Profile.AddBrowserExtensionAsync(extensionFolderPath);
            await extension.EnableAsync(true);
            UpdateExtensionUiState(extension.Id, disabled: false, removed: false, installPath: extensionFolderPath);
            App.WriteTrace($"Extension installed: {extension.Name} ({extension.Id})");
        }
        catch (Exception ex)
        {
            App.WriteTrace($"Extension install failed: {ex.Message}");
        }
    }

    private async Task SetBrowserExtensionEnabledAsync(CoreWebView2? core, string extensionId, bool enabled)
    {
        if (core == null) return;
        if (string.IsNullOrWhiteSpace(extensionId)) return;

        try
        {
            var extensions = await core.Profile.GetBrowserExtensionsAsync();
            var extension = extensions.FirstOrDefault(ext =>
                string.Equals(ext.Id, extensionId, StringComparison.OrdinalIgnoreCase));
            if (extension == null) return;
            await extension.EnableAsync(enabled);
            UpdateExtensionUiState(extensionId, disabled: !enabled, removed: false);
        }
        catch (Exception ex)
        {
            App.WriteTrace($"Extension toggle failed: {ex.Message}");
        }
    }

    private async Task RemoveBrowserExtensionAsync(CoreWebView2? core, string extensionId)
    {
        if (core == null) return;
        if (string.IsNullOrWhiteSpace(extensionId)) return;

        try
        {
            var extensions = await core.Profile.GetBrowserExtensionsAsync();
            var extension = extensions.FirstOrDefault(ext =>
                string.Equals(ext.Id, extensionId, StringComparison.OrdinalIgnoreCase));
            if (extension == null) return;
            await extension.RemoveAsync();
            UpdateExtensionUiState(extensionId, disabled: true, removed: true);
        }
        catch (Exception ex)
        {
            App.WriteTrace($"Extension remove failed: {ex.Message}");
        }
    }

    private Dictionary<string, ExtensionUiState> LoadExtensionUiState()
    {
        try
        {
            if (File.Exists(_extensionsStatePath))
            {
                var json = File.ReadAllText(_extensionsStatePath);
                return JsonSerializer.Deserialize<Dictionary<string, ExtensionUiState>>(json)
                    ?? new Dictionary<string, ExtensionUiState>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }

        return new Dictionary<string, ExtensionUiState>(StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateExtensionUiState(string extensionId, bool disabled, bool removed, string? installPath = null)
    {
        if (string.IsNullOrWhiteSpace(extensionId)) return;

        try
        {
            var state = LoadExtensionUiState();
            if (!disabled && !removed && string.IsNullOrWhiteSpace(installPath))
            {
                state.Remove(extensionId);
            }
            else
            {
                state.TryGetValue(extensionId, out var existing);
                state[extensionId] = new ExtensionUiState
                {
                    Disabled = disabled,
                    Removed = removed,
                    InstallPath = string.IsNullOrWhiteSpace(installPath) ? existing?.InstallPath ?? "" : installPath
                };
            }

            File.WriteAllText(_extensionsStatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private CoreWebView2? GetActiveCore()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return null;
        return _tabs[_activeTabIndex].WebView.CoreWebView2;
    }

    private async void ExtensionsBtn_Click(object sender, RoutedEventArgs e)
    {
        await ShowExtensionsPopupAsync();
    }

    private async Task ShowExtensionsPopupAsync()
    {
        var core = GetActiveCore();
        if (core == null) return;

        IReadOnlyList<CoreWebView2BrowserExtension> extensions;
        try { extensions = await core.Profile.GetBrowserExtensionsAsync(); }
        catch { return; }

        var uiState = LoadExtensionUiState();
        var visible = extensions
            .Where(ext => !uiState.TryGetValue(ext.Id, out var state) || !state.Removed)
            .Select(ext => new ExtensionPopupItem(
                ext.Id,
                ext.Name,
                uiState.TryGetValue(ext.Id, out var state) ? !state.Disabled : ext.IsEnabled,
                GetExtensionPopupRelativePath(ext.Id, ext.Name)))
            .Where(ext => !string.IsNullOrWhiteSpace(ext.PopupPath))
            .OrderByDescending(ext => ext.Enabled)
            .ThenBy(ext => ext.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = ExtensionsBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade
        };

        var root = new Border
        {
            Width = 320,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2b2c31" : "#ffffff")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3f46" : "#dadce0")!),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = _isDarkMode ? 0.45 : 0.18 }
        };
        var stack = new StackPanel();
        root.Child = stack;

        var header = new Grid { Margin = new Thickness(16, 14, 12, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Extensions",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#f1f3f4" : "#111111")!)
        });
        var close = MakeExtensionIconButton("×");
        close.Click += (_, _) => popup.IsOpen = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        stack.Children.Add(header);

        if (visible.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No extensions with popup interfaces are available.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 8, 20, 18),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#c9ccd3" : "#3c4043")!),
                FontSize = 13,
                LineHeight = 18
            });
        }
        else
        {
            AddExtensionSection(stack, "Full access", "These extensions can see and change information on this site.", visible.Where(x => x.Enabled).ToList(), popup);
            AddExtensionSection(stack, "No access needed", "These extensions don't need to see and change information on this site.", visible.Where(x => !x.Enabled).ToList(), popup);
        }

        var manage = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 12, 16, 14),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "⚙", FontSize = 15, Margin = new Thickness(0,0,12,0), Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#c7c9d1" : "#3c4043")!) },
                    new TextBlock { Text = "Manage extensions", FontSize = 13, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#f1f3f4" : "#111111")!) }
                }
            }
        };
        manage.Click += async (_, _) => { popup.IsOpen = false; await CreateTab("ycb://extensions"); };
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3f46" : "#e8eaed")!) });
        stack.Children.Add(manage);

        popup.Child = root;
        popup.HorizontalOffset = -290;
        popup.VerticalOffset = 6;
        popup.IsOpen = true;
    }

    private void AddExtensionSection(StackPanel stack, string title, string desc, List<ExtensionPopupItem> items, System.Windows.Controls.Primitives.Popup popup)
    {
        if (items.Count == 0) return;
        var fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#f1f3f4" : "#111111")!);
        var sub = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#c9ccd3" : "#111111")!);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(16, 8, 16, 4) });
        stack.Children.Add(new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = sub, Margin = new Thickness(16, 0, 20, 10), LineHeight = 18 });
        foreach (var item in items)
        {
            var row = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 8, 12, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = string.IsNullOrWhiteSpace(item.PopupPath) ? Cursors.Arrow : Cursors.Hand
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = GetExtensionGlyph(item.Name), FontSize = 15, Width = 24, VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = TruncateExtensionName(item.Name), FontSize = 13, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            var pin = MakeExtensionIconButton(item.Enabled ? "♢" : "📌");
            pin.ToolTip = item.Enabled ? "Pinned" : "Pin";
            Grid.SetColumn(pin, 2);
            grid.Children.Add(pin);
            var more = MakeExtensionIconButton("⋮");
            more.ToolTip = "Extension options";
            more.Click += async (s, e) =>
            {
                e.Handled = true;
                var core = GetActiveCore();
                if (core != null)
                {
                    await SetBrowserExtensionEnabledAsync(core, item.Id, !item.Enabled);
                }
                popup.IsOpen = false;
            };
            Grid.SetColumn(more, 3);
            grid.Children.Add(more);
            row.Content = grid;
            row.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(item.PopupPath)) return;
                popup.IsOpen = false;
                OpenExtensionPopupWindow(item);
            };
            stack.Children.Add(row);
        }
    }

    private Button MakeExtensionIconButton(string text) => new()
    {
        Content = text,
        Width = 30,
        Height = 28,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#d7d9df" : "#3c4043")!),
        FontSize = 18,
        Cursor = Cursors.Hand
    };

    private static string TruncateExtensionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Extension";
        return name.Length > 28 ? name[..25] + "..." : name;
    }

    private static string GetExtensionGlyph(string name)
    {
        if (name.Contains("vpn", StringComparison.OrdinalIgnoreCase)) return "🛡";
        if (name.Contains("chat", StringComparison.OrdinalIgnoreCase) || name.Contains("ai", StringComparison.OrdinalIgnoreCase)) return "♧";
        if (name.Contains("virus", StringComparison.OrdinalIgnoreCase) || name.Contains("download", StringComparison.OrdinalIgnoreCase)) return "✸";
        if (name.Contains("block", StringComparison.OrdinalIgnoreCase)) return "🧩";
        return "🧩";
    }

    private void OpenExtensionPopupWindow(ExtensionPopupItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PopupPath)) return;
        try
        {
            _extensionPopupWindow?.Close();
        }
        catch { }

        var popupUrl = $"chrome-extension://{item.Id}/{item.PopupPath.TrimStart('/')}";
        var isUBlockPopup = item.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase) ||
                            item.PopupPath.Contains("popup-fenix", StringComparison.OrdinalIgnoreCase);
        var initialWidth = isUBlockPopup ? 306.0 : 340.0;
        var initialHeight = isUBlockPopup ? 392.0 : 400.0;
        var root = new Border
        {
            Width = initialWidth,
            Height = initialHeight,
            MaxWidth = 380,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 120),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#202124" : "#ffffff")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3f46" : "#dadce0")!),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = _isDarkMode ? 0.45 : 0.18 }
        };
        var webView = new WebView2
        {
            Width = root.Width,
            Height = root.Height,
            DefaultBackgroundColor = _isDarkMode
                ? System.Drawing.Color.FromArgb(255, 32, 33, 36)
                : System.Drawing.Color.FromArgb(255, 255, 255, 255)
        };
        root.Child = webView;

        var popupWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = this,
            Width = initialWidth,
            Height = initialHeight,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = root
        };
        var start = GetExtensionPopupPosition(initialWidth);
        popupWindow.Left = start.left;
        popupWindow.Top = start.top;
        popupWindow.Deactivated += (_, _) =>
        {
            try { if (popupWindow.IsVisible) popupWindow.Close(); } catch { }
        };
        popupWindow.Closed += (_, _) =>
        {
            try { webView.CoreWebView2?.Stop(); } catch { }
            try { webView.Dispose(); } catch { }
            if (ReferenceEquals(_extensionPopupWindow, popupWindow))
                _extensionPopupWindow = null;
        };
        _extensionPopupWindow = popupWindow;
        popupWindow.Show();
        ForcePopupOnTop(popupWindow);
        TrackPopupPosition(popupWindow, () => GetExtensionPopupPosition(root.Width));

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var dataFolder = _isIncognito ? _incognitoUserDataFolder : _profileFolder;
                var env = _isIncognito
                    ? (_incognitoWebViewEnvironment ??= await CreateWebViewEnvironment(dataFolder))
                    : (_webViewEnvironment ??= await CreateWebViewEnvironment(dataFolder));
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    if (isUBlockPopup)
                    {
                        await ForceUBlockPopupSizeAsync(root, webView, popupWindow);
                        return;
                    }
                    foreach (var delay in new[] { 40, 140, 350, 800 })
                    {
                        await ResizeExtensionPopupToContentAsync(root, webView, popupWindow, delay);
                    }
                };
                webView.CoreWebView2.Navigate(popupUrl);
            }
            catch (Exception ex)
            {
                App.WriteTrace($"Extension popup failed: {ex.Message}");
                popupWindow.Close();
            }
        });
    }

    private (double left, double top) GetExtensionPopupPosition(double popupWidth)
    {
        var pt = ExtensionsBtn.PointToScreen(new Point(ExtensionsBtn.ActualWidth, ExtensionsBtn.ActualHeight));
        return (pt.X - popupWidth, pt.Y + 6);
    }

    private async Task ForceUBlockPopupSizeAsync(Border root, WebView2 webView, Window popup)
    {
        try
        {
            if (webView.CoreWebView2 == null) return;
            await Task.Delay(120);
            await webView.CoreWebView2.ExecuteScriptAsync(@"
                (function() {
                    const style = document.createElement('style');
                    style.textContent = `
                        html, body { margin: 0 !important; width: 306px !important; min-width: 306px !important; max-width: 306px !important; overflow: hidden !important; }
                        #panes { width: 306px !important; min-width: 306px !important; max-width: 306px !important; overflow: hidden !important; }
                        #main { width: 306px !important; min-width: 306px !important; max-width: 306px !important; }
                        #firewall, #firewall-vspacer { display: none !important; }
                    `;
                    document.documentElement.appendChild(style);
                })();
            ");

            const double width = 306;
            const double height = 392;
            root.Width = width;
            root.Height = height;
            webView.Width = width;
            webView.Height = height;
            popup.Width = width;
            popup.Height = height;
            var pos = GetExtensionPopupPosition(width);
            popup.Left = pos.left;
            popup.Top = pos.top;
        }
        catch (Exception ex)
        {
            App.WriteTrace($"uBlock popup resize failed: {ex.Message}");
        }
    }

    private async Task ResizeExtensionPopupToContentAsync(Border root, WebView2 webView, Window popup, int delayMs = 120)
    {
        try
        {
            if (webView.CoreWebView2 == null) return;
            await Task.Delay(delayMs);
            var raw = await webView.CoreWebView2.ExecuteScriptAsync(@"
                (function() {
                    const doc = document.documentElement;
                    const body = document.body;
                    const styles = body ? getComputedStyle(body) : null;
                    const targets = [
                        document.querySelector('#panes'),
                        document.querySelector('#main'),
                        body ? body.firstElementChild : null
                    ].filter(Boolean);
                    let width = 0;
                    let height = 0;
                    for (const el of targets) {
                        const rect = el.getBoundingClientRect();
                        width = Math.max(width, Math.ceil(rect.width), el.scrollWidth || 0, el.offsetWidth || 0);
                        height = Math.max(height, Math.ceil(rect.height), el.scrollHeight || 0, el.offsetHeight || 0);
                    }
                    return JSON.stringify({
                        width,
                        height,
                        bodyWidth: body ? body.offsetWidth : 0,
                        bodyHeight: body ? body.offsetHeight : 0,
                        minWidth: styles ? parseFloat(styles.minWidth) || 0 : 0,
                        minHeight: styles ? parseFloat(styles.minHeight) || 0 : 0
                    });
                })();
            ");

            var json = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            var info = doc.RootElement;
            var measuredWidth = Math.Max(
                info.TryGetProperty("width", out var w) ? w.GetDouble() : 0,
                info.TryGetProperty("bodyWidth", out var bw) ? bw.GetDouble() : 0);
            var measuredHeight = Math.Max(
                info.TryGetProperty("height", out var h) ? h.GetDouble() : 0,
                info.TryGetProperty("bodyHeight", out var bh) ? bh.GetDouble() : 0);
            if (info.TryGetProperty("minWidth", out var minW)) measuredWidth = Math.Max(measuredWidth, minW.GetDouble());
            if (info.TryGetProperty("minHeight", out var minH)) measuredHeight = Math.Max(measuredHeight, minH.GetDouble());

            var maxWidth = 380.0;
            var maxHeight = Math.Max(320.0, SystemParameters.WorkArea.Height - 120);
            var width = Math.Clamp(measuredWidth > 0 ? measuredWidth : 340, 300, maxWidth);
            var height = Math.Clamp(measuredHeight > 0 ? measuredHeight : 400, 220, maxHeight);

            root.Width = width;
            root.Height = height;
            webView.Width = width;
            webView.Height = height;
            popup.Width = width;
            popup.Height = height;
            var pos = GetExtensionPopupPosition(width);
            popup.Left = pos.left;
            popup.Top = pos.top;
        }
        catch (Exception ex)
        {
            App.WriteTrace($"Extension popup resize failed: {ex.Message}");
        }
    }

    private string GetExtensionPopupRelativePath(string extensionId, string extensionName)
    {
        var manifest = FindExtensionManifestPath(extensionId, extensionName);
        if (string.IsNullOrWhiteSpace(manifest)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            foreach (var section in new[] { "action", "browser_action", "page_action" })
            {
                if (root.TryGetProperty(section, out var action) &&
                    action.ValueKind == JsonValueKind.Object &&
                    action.TryGetProperty("default_popup", out var popup) &&
                    popup.ValueKind == JsonValueKind.String)
                {
                    return popup.GetString() ?? "";
                }
            }
        }
        catch { }
        return "";
    }

    private string FindExtensionManifestPath(string extensionId, string extensionName)
    {
        var state = LoadExtensionUiState();
        if (state.TryGetValue(extensionId, out var saved) && !string.IsNullOrWhiteSpace(saved.InstallPath))
        {
            var manifest = IoPath.Combine(saved.InstallPath, "manifest.json");
            if (File.Exists(manifest)) return manifest;
        }
        if (extensionName.Contains("uBlock Origin", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = IoPath.Combine(UBlockExtensionPath, "manifest.json");
            if (File.Exists(manifest)) return manifest;
        }
        try
        {
            foreach (var manifest in Directory.EnumerateFiles(BrowserExtensionsPath, "manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("name", out var nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String &&
                        string.Equals(nameEl.GetString(), extensionName, StringComparison.OrdinalIgnoreCase))
                    {
                        return manifest;
                    }
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    private async Task ApplyUBlockOriginStateAsync()
    {
        foreach (var tab in _tabs)
        {
            if (tab.WebView.CoreWebView2 == null) continue;
            var extension = await GetUBlockOriginExtensionAsync(tab.WebView);
            if (extension == null) continue;
            try { await extension.EnableAsync(_settings.AdBlockerEnabled); }
            catch { }
        }
    }

    private void SetupAdBlockerNetwork(WebView2 webView)
    {
        if (webView.CoreWebView2 == null) return;

        var contexts = new[]
        {
            CoreWebView2WebResourceContext.Script,
            CoreWebView2WebResourceContext.Stylesheet,
            CoreWebView2WebResourceContext.Image,
            CoreWebView2WebResourceContext.Media,
            CoreWebView2WebResourceContext.Font,
            CoreWebView2WebResourceContext.XmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch
        };

        foreach (var pattern in _adBlockDomains)
        {
            foreach (var context in contexts)
            {
                webView.CoreWebView2.AddWebResourceRequestedFilter(pattern, context);
            }
        }

        webView.CoreWebView2.WebResourceRequested += (s, e) =>
        {
            if (!_settings.AdBlockerEnabled) return;
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return;

            try
            {
                var reqUri = e.Request.Uri;
                if (Uri.TryCreate(reqUri, UriKind.Absolute, out var blockedUri))
                {
                    if (IsAdBlockAllowlisted(blockedUri))
                    {
                        return;
                    }

                    if (string.Equals(blockedUri.Host, "www.google.com", StringComparison.OrdinalIgnoreCase) &&
                        blockedUri.AbsolutePath.StartsWith("/sorry", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (_adBlockLoggedHosts.Add(blockedUri.Host))
                    {
                        App.WriteTrace($"[ADBLOCK] Blocking {blockedUri.Host}");
                    }
                }
            }
            catch { }

            e.Response = _webViewEnvironment!.CreateWebResourceResponse(
                null,
                403,
                "Blocked",
                "Access-Control-Allow-Origin: *");
        };
    }

    private bool IsAdBlockAllowlisted(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        try
        {
            var savedPerms = LoadSitePermissions();
            if (savedPerms.TryGetValue(uri.Host, out var perms) &&
                perms.TryGetValue("adblock", out var state) &&
                string.Equals(state, "allow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        if (host == "accounts.google.com" || host.EndsWith(".accounts.google.com", StringComparison.Ordinal))
        {
            return true;
        }

        if (host == "get.microsoft.com" || host.EndsWith(".get.microsoft.com", StringComparison.Ordinal))
        {
            return true;
        }

        if (host == "bitwarden.com" || host.EndsWith(".bitwarden.com", StringComparison.Ordinal) ||
            host == "vault.bitwarden.com" || host.EndsWith(".vault.bitwarden.com", StringComparison.Ordinal) ||
            host == "id.bitwarden.com" || host.EndsWith(".id.bitwarden.com", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private string? FindCopilotExe()
    {
        // Check common locations
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesDir = IoPath.Combine(programFiles, "Microsoft", "WinGet", "Packages");
        
        if (Directory.Exists(packagesDir))
        {
            foreach (var dir in Directory.GetDirectories(packagesDir))
            {
                if (IoPath.GetFileName(dir).StartsWith("GitHub.Copilot_", StringComparison.OrdinalIgnoreCase))
                {
                    var exe = IoPath.Combine(dir, "copilot.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        
        // Check if copilot is in PATH
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "copilot",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            proc?.WaitForExit(3000);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim();
            if (!string.IsNullOrEmpty(output) && File.Exists(output.Split('\n')[0]))
            {
                return output.Split('\n')[0].Trim();
            }
        }
        catch { }
        
        // Fallback to just "copilot" and hope it's in PATH
        return "copilot";
    }
    
    private void AddCopilotMessage(string message, bool isUser)
    {
        var bubble = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                isUser ? (_isDarkMode ? "#1e3a5f" : "#dbeafe") : (_isDarkMode ? "#24263a" : "#f1f3f6"))!),
            CornerRadius = new CornerRadius(isUser ? 14 : 14, isUser ? 14 : 14, isUser ? 3 : 14, isUser ? 14 : 3),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(isUser ? 36 : 0, 5, isUser ? 0 : 36, 5),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 290
        };

        if (isUser)
        {
            bubble.Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    _isDarkMode ? "#cce3ff" : "#1a3a6b")!),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
        }
        else
        {
            bubble.Child = BuildAssistantMessageUI(message);
        }
        
        MessagesPanel.Children.Add(bubble);
        CopilotMessages.ScrollToEnd();
    }

    // Renders assistant messages with code block support (``` ... ```)
    private FrameworkElement BuildAssistantMessageUI(string text)
    {
        bool dark = _isDarkMode;
        string textColor  = dark ? "#e8eaed" : "#202124";
        string codeBg     = dark ? "#0d1117"  : "#f6f8fa";
        string codeBorder = dark ? "#30363d"  : "#d0d7de";
        string codeText   = dark ? "#e6edf3"  : "#24292f";

        // Split on triple-backtick code fences
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"```(?:\w+)?");
        
        // Outer container with plain text and code blocks
        var outer = new StackPanel { Orientation = Orientation.Vertical };

        if (parts.Length <= 1)
        {
            // No code blocks — render inline markdown styling in a TextBlock
            outer.Children.Add(MakeFormattedTextBlock(text, textColor, dark));
        }
        else
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                if (i % 2 == 0)
                {
                    var segment = parts[i].Trim('\r', '\n');
                    if (!string.IsNullOrWhiteSpace(segment))
                        outer.Children.Add(MakeFormattedTextBlock(segment, textColor, dark));
                }
                else
                {
                    // Code block
                    var code = parts[i].Trim('\r', '\n');
                    var codeBorderEl = new Border
                    {
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(codeBg)!),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 8, 12, 10),
                        Margin = new Thickness(0, 6, 0, 6),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(codeBorder)!),
                        BorderThickness = new Thickness(1)
                    };
                    var codePanel = new StackPanel();
                    var codeText2 = new TextBox
                    {
                        Text = code,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(codeText)!),
                        Background = System.Windows.Media.Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Courier New"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0),
                        IsReadOnly = true,
                        Padding = new Thickness(0),
                        CaretBrush = System.Windows.Media.Brushes.Transparent
                    };
                    codePanel.Children.Add(codeText2);
                    codeBorderEl.Child = codePanel;
                    outer.Children.Add(codeBorderEl);
                }
            }
        }

        return outer;
    }

    private static FrameworkElement MakeSelectableText(string text, string color)
    {
        return new TextBlock
        {
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 2)
        };
    }

    private static FrameworkElement MakeFormattedTextBlock(string text, string color, bool dark)
    {
        var block = new TextBlock
        {
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 2)
        };

        foreach (var inline in ParseInlineMarkdown(text, color, dark))
        {
            block.Inlines.Add(inline);
        }

        return block;
    }

    private static IEnumerable<System.Windows.Documents.Inline> ParseInlineMarkdown(string text, string color, bool dark)
    {
        var result = new List<System.Windows.Documents.Inline>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(new System.Windows.Documents.Run(""));
            return result;
        }

        var plainColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        var codeBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d2f34")!);
        var codeFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#e6edf3" : "#24292f")!);

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    var code = text.Substring(i + 1, end - i - 1);
                    result.Add(new System.Windows.Documents.Run(code)
                    {
                        FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Courier New"),
                        Foreground = codeFg,
                        Background = codeBg
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    var boldText = text.Substring(i + 2, end - i - 2);
                    result.Add(new System.Windows.Documents.Run(boldText)
                    {
                        FontWeight = FontWeights.Bold,
                        Foreground = plainColor
                    });
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    var italicText = text.Substring(i + 1, end - i - 1);
                    result.Add(new System.Windows.Documents.Run(italicText)
                    {
                        FontStyle = FontStyles.Italic,
                        Foreground = plainColor
                    });
                    i = end + 1;
                    continue;
                }
            }

            var nextSpecial = FindNextMarkdownSpecial(text, i);
            var plain = text.Substring(i, nextSpecial - i);
            result.Add(new System.Windows.Documents.Run(plain)
            {
                Foreground = plainColor
            });
            i = nextSpecial;
        }

        if (result.Count == 0)
        {
            result.Add(new System.Windows.Documents.Run(text) { Foreground = plainColor });
        }

        return result;
    }

    private static int FindNextMarkdownSpecial(string text, int startIndex)
    {
        var next = text.Length;
        foreach (var token in new[] { "`", "**", "*" })
        {
            var idx = text.IndexOf(token, startIndex, StringComparison.Ordinal);
            if (idx >= 0 && idx < next)
            {
                next = idx;
            }
        }
        return next;
    }
    
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = true;
    }
    
    private async void MenuNewTab_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab(_settings.HomePage ?? "ycb://newtab");
    }
    
    private void MenuNewWindow_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenNewWindow();
    }
    
    private void MenuNewIncognitoWindow_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenIncognitoWindow();
    }
    
    private void MenuBookmarks_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        RefreshBookmarksPopup();
        BookmarksPopup.IsOpen = true;
    }

    private void RefreshBookmarksPopup()
    {
        // Pre-fill name with current page title
        var title = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            ? _tabs[_activeTabIndex].Title ?? ""
            : "";
        BookmarkNameBox.Text = title;

        // Populate bookmarks list
        BookmarksList.Children.Clear();
        var bookmarks = LoadBookmarks();
        if (!bookmarks.Any())
        {
            BookmarksList.Children.Add(new TextBlock
            {
                Text = "No bookmarks saved yet.",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 2)
            });
            return;
        }

        for (int i = 0; i < bookmarks.Count; i++)
        {
            var bm = bookmarks[i];
            var idx = i;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = bm.Label ?? bm.Title ?? bm.Url,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!),
                    FontSize = 13
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 5, 4, 5),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = bm.Url
            };
            nameBtn.Click += (s, ev) =>
            {
                BookmarksPopup.IsOpen = false;
                if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                    _tabs[_activeTabIndex].WebView.CoreWebView2.Navigate(bm.Url);
            };
            Grid.SetColumn(nameBtn, 0);

            var delBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "✕",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
                    FontSize = 11
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 5, 6, 5),
                Cursor = Cursors.Hand,
                ToolTip = "Remove bookmark"
            };
            delBtn.Click += (s, ev) =>
            {
                RemoveBookmark(idx);
                RefreshBookmarkStar();
                RefreshBookmarksPopup();
            };
            Grid.SetColumn(delBtn, 1);

            row.Children.Add(nameBtn);
            row.Children.Add(delBtn);
            BookmarksList.Children.Add(row);
        }
    }

    private void BookmarkSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var url = _tabs[_activeTabIndex].Url ?? "";
        if (string.IsNullOrEmpty(url) || url.StartsWith("ycb://")) return;

        var name = BookmarkNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
            name = _tabs[_activeTabIndex].Title ?? url;

        var bookmarks = LoadBookmarks();
        var existing = bookmarks.FindIndex(b => b.Url == url);
        if (existing >= 0)
            RemoveBookmark(existing);
        else
            AddBookmark(url, name);

        RefreshBookmarkStar();
        RefreshBookmarksPopup();
    }

    private async void MenuHistory_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://history");
    }
    
    private async void MenuDownloads_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://downloads");
    }

    private async void MenuProfiles_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await ShowProfileChooserAsync(switchOnSelect: true);
    }
    
    private async void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://settings");
    }

    private async void MenuExtensions_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://extensions");
    }
    
    private async void MenuPasswords_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://passwords");
    }
    
    private void MenuSupport_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _ = CreateTab("https://ycb.tomcreations.org/auth/login?next=/support");
    }

    // Maps a live file:// URL back to its ycb:// equivalent so internal pages survive reload
    private string GetReloadUrl(BrowserTab tab)
    {
        // Try live source first
        var live = tab.WebView.Source?.ToString();
        if (!string.IsNullOrEmpty(live) && IsInternalVirtualUrl(live))
        {
            return VirtualInternalUrlToYcbUrl(live);
        }
        if (!string.IsNullOrEmpty(live) && live.StartsWith("file:///"))
        {
            if (live.Contains("settings.html"))  return "ycb://settings";
            if (live.Contains("history.html"))   return "ycb://history";
            if (live.Contains("downloads.html")) return "ycb://downloads";
            if (live.Contains("passwords.html")) return "ycb://passwords";
            if (live.Contains("profiles.html"))  return "ycb://profiles";
            if (live.Contains("apps.html"))       return "ycb://apps";
            if (live.Contains("guide.html"))     return "ycb://guide";
            if (live.Contains("support.html"))   return "ycb://support";
            if (live.Contains("newtab.html"))    return "ycb://newtab";
        }
        if (!string.IsNullOrEmpty(live) &&
            (live.StartsWith("http://") || live.StartsWith("https://")))
            return live;
        // Fall back to stored URL
        var stored = tab.Url ?? "";
        if (stored.StartsWith("ycb://") || stored.StartsWith("http://") || stored.StartsWith("https://"))
            return stored;
        return "ycb://newtab";
    }

    private async Task TrySilentSupportLogin(WebView2 webView)
    {
        try
        {
            var userId = ErrorReporter.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                System.Diagnostics.Debug.WriteLine("[SilentLogin] No user ID available, skipping.");
                return;
            }

            var safeId = userId.Replace("\\", "\\\\").Replace("'", "\\'");
            System.Diagnostics.Debug.WriteLine($"[SilentLogin] POSTing user ID to /auth/ycbuseridlogin");

            // Run fetch from within the page's own origin so the browser
            // handles Set-Cookie automatically — no manual cookie injection needed
            var js = $@"
(function() {{
    console.log('[YCB SilentLogin] Starting POST for user: {safeId}');
    fetch('/auth/ycbuseridlogin', {{
        method: 'POST',
        headers: {{ 'Content-Type': 'application/x-www-form-urlencoded' }},
        body: 'ycb_user_id={safeId}',
        credentials: 'include',
        redirect: 'follow'
    }}).then(function(r) {{
        console.log('[YCB SilentLogin] Response status: ' + r.status + ' url: ' + r.url);
        window.location.href = '/support';
    }}).catch(function(err) {{
        console.error('[YCB SilentLogin] Fetch error: ' + err);
        window.location.href = '/support';
    }});
}})();";

            await webView.ExecuteScriptAsync(js);
            System.Diagnostics.Debug.WriteLine("[SilentLogin] JS injected OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SilentLogin] Exception: {ex.Message}");
        }
    }

    private async void MenuGuide_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://guide");
    }
    
    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _zoomFactor = Math.Min(_zoomFactor + 0.1, 3.0);
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].WebView.ZoomFactor = _zoomFactor;
        }
    }
    
    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _zoomFactor = Math.Max(_zoomFactor - 0.1, 0.5);
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].WebView.ZoomFactor = _zoomFactor;
        }
    }
    
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].WebView.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }
    }
    
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SaveSettings();
        MenuPopup.IsOpen = false;
    }
    
    private void ApplyTheme()
    {
        var themeText = ThemeToggle.Content as TextBlock;
        string iconColor;
        
        if (_isDarkMode)
        {
            iconColor = "#9aa0a6";
            // Dark chrome colors - keep tabs neutral gray
            MainGrid.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            TabStrip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            Toolbar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35363a")!);
            OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#292b2f")!);
            OmniboxBorder.BorderBrush = null;
            OmniboxBorder.BorderThickness = new Thickness(0);
            UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!);
            UrlBox.CaretBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!);
            UrlPlaceholder.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5f6368")!);
            BookmarksBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d2e31")!);
            DownloadShelf.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d2e31")!);
            IncognitoPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2a2b2f")!);
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            NewTabBtn.Style = (Style)FindResource("NewTabBtnStyle");
            if (themeText != null) themeText.Text = "Light Mode";
        }
        else
        {
            iconColor = "#5f6368";
            // Light theme colors — neutral cool gray, not lavender
            MainGrid.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dee1e6")!);
            TabStrip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dee1e6")!);
            Toolbar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff")!);
            OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f1f3f4")!);
            OmniboxBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dfe1e5")!);
            OmniboxBorder.BorderThickness = new Thickness(1);
            UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            UrlBox.CaretBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            UrlPlaceholder.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#80868b")!);
            BookmarksBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff")!);
            DownloadShelf.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f1f3f4")!);
            IncognitoPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!);
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dee1e6")!);
            NewTabBtn.Style = (Style)FindResource("LightNewTabBtnStyle");
            if (themeText != null) themeText.Text = "Dark Mode";
        }
        
        // Recolor all toolbar icons
        var iconBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!);
        BackIcon.Stroke = iconBrush; ForwardIcon.Stroke = iconBrush;
        RefreshArc.Stroke = iconBrush; RefreshArrow.Stroke = iconBrush;
        NewTabIcon.Stroke = iconBrush;
        MinIcon.Fill = iconBrush; MaxIcon.Stroke = iconBrush; CloseIcon.Stroke = iconBrush;
        MenuDot1.Fill = iconBrush; MenuDot2.Fill = iconBrush; MenuDot3.Fill = iconBrush;
        if (!BookmarkStarPath.Fill.Equals(Brushes.Transparent))
        {
            // Bookmarked — keep yellow
        }
        else
        {
            BookmarkStarPath.Stroke = iconBrush;
        }
        
        // Update all tab styles and title colors based on theme
        string inactiveStyle = _isDarkMode ? "TabStyle" : "LightTabStyle";
        string activeStyle   = _isDarkMode ? "ActiveTabStyle" : "LightActiveTabStyle";
        for (int i = 0; i < _tabs.Count; i++)
        {
            bool isActive = i == _activeTabIndex;
            _tabs[i].TabButton.Style = (Style)FindResource(isActive ? activeStyle : inactiveStyle);
            
            if (_tabs[i].TabButton.Content is Grid grid)
            {
                var title = grid.Children.OfType<TextBlock>().FirstOrDefault();
                if (title != null)
                {
                    title.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        isActive ? (_isDarkMode ? "#e8eaed" : "#202124") : (_isDarkMode ? "#9aa0a6" : "#202124"))!);
                }
            }
            
            // Update WebView background color
            _tabs[i].WebView.DefaultBackgroundColor = _isDarkMode 
                ? System.Drawing.Color.FromArgb(255, 32, 33, 36)  // #202124
                : System.Drawing.Color.FromArgb(255, 255, 255, 255);  // white
        }
        
        // Update suggestion popup for current theme
        OmniSuggestion.IsDark = _isDarkMode;
        OmniSuggestion.ThemePrimary   = _isDarkMode ? "#e8eaed" : "#202124";
        OmniSuggestion.ThemeSecondary = _isDarkMode ? "#9aa0a6" : "#5f6368";
        SuggestBorder.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2d2e31" : "#ffffff")!);
        SuggestBorder.BorderBrush = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
        
        // Update copilot sidebar theme
        CopilotSidebar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#1a1b26" : "#f8f9fa")!);
        CopilotSidebar.BorderBrush = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
        CopilotHeaderBorder.Background = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(5, 255, 255, 255))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff")!);
        CopilotHeaderBorder.BorderBrush = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
        CopilotTitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
        CopilotSubtitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!);
        CopilotInputAreaBorder.Background = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(5, 255, 255, 255))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff")!);
        CopilotInputAreaBorder.BorderBrush = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
        CopilotInputFieldBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2a2b36" : "#f1f3f4")!);
        CopilotInputFieldBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3e4a" : "#dfe1e5")!);
        CopilotInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
        if (AiToolbarBorder != null)
        {
            AiToolbarBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#1f2024" : "#f8f9fa")!);
            AiToolbarBorder.BorderBrush = _isDarkMode
                ? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
        }
        if (AiToolbarLabel != null)
        {
            AiToolbarLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!);
        }
        if (AiProviderCombo != null)
        {
            AiProviderCombo.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2a2b36" : "#ffffff")!);
            AiProviderCombo.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
            AiProviderCombo.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3e4a" : "#dfe1e5")!);
        }
        foreach (var btn in new[] { AiOpenTabBtn, AiRefreshBtn, AiHomeBtn })
        {
            if (btn == null) continue;
            btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2a2b36" : "#ffffff")!);
            btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
            btn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3d3e4a" : "#dfe1e5")!);
        }
        if (AiWebView != null)
        {
            AiWebView.DefaultBackgroundColor = _isDarkMode
                ? System.Drawing.Color.FromArgb(255, 32, 33, 36)
                : System.Drawing.Color.FromArgb(255, 255, 255, 255);
        }
        // Update menu popup theme
        MenuPopupBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2d2e31" : "#ffffff")!);
        MenuPopupBorder.BorderBrush = _isDarkMode
            ? new SolidColorBrush(Color.FromArgb(18, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
        var menuFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!);
        foreach (var child in LogicalTreeHelper.GetChildren(MenuPopupBorder.Child as StackPanel ?? new StackPanel()))
        {
            if (child is Button mb) mb.Foreground = menuFg;
        }
        
        // Rebuild ItemContainerStyle so hover/selected backgrounds match theme
        var hoverColor = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c4043" : "#f1f3f4")!;
        var fgColor    = (Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!;
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(fgColor)));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        itemStyle.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        var bdFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        bdFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Control.BackgroundProperty) });
        bdFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Control.PaddingProperty) });
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        bdFactory.AppendChild(cpFactory);
        var ct = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bdFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverColor), "Bd"));
        ct.Triggers.Add(hoverTrigger);
        var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverColor), "Bd"));
        ct.Triggers.Add(selTrigger);
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, ct));
        SuggestionsList.ItemContainerStyle = itemStyle;
    }
    
    // Download shelf
    private void ShowDownloadShelf(DownloadItem item)
    {
        DownloadShelf.Visibility = Visibility.Visible;
        DownloadShelfRow.Height = new GridLength(72);

        var itemBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c3d41" : "#ffffff")!),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 200,
            MaxWidth = 280,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#00000000" : "#dfe1e5")!),
            BorderThickness = new Thickness(_isDarkMode ? 0 : 1),
            Tag = item.FilePath
        };

        var outer = new StackPanel { Orientation = Orientation.Vertical };

        // Top row: icon + name + status
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconPath = new WpfPath
        {
            Data = Geometry.Parse("M4 2h8l4 4v12a2 2 0 01-2 2H4a2 2 0 01-2-2V4a2 2 0 012-2z"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            StrokeThickness = 1.5,
            Width = 20, Height = 24,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(iconPath, 0);
        topGrid.Children.Add(iconPath);

        var infoStack = new StackPanel();
        var nameBlock = new TextBlock
        {
            Text = item.Filename,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var statusBlock = new TextBlock
        {
            Text = item.Status,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        infoStack.Children.Add(nameBlock);
        infoStack.Children.Add(statusBlock);
        Grid.SetColumn(infoStack, 1);
        topGrid.Children.Add(infoStack);
        outer.Children.Add(topGrid);

        // Action buttons row (hidden until complete)
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 0),
            Visibility = Visibility.Collapsed,
            Tag = "actionRow"
        };

        var openBtn = MakeShelfButton("Open");
        openBtn.Click += (s, e) =>
        {
            if (File.Exists(item.FilePath))
                Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        };

        var folderBtn = MakeShelfButton("Open folder");
        folderBtn.Click += (s, e) =>
        {
            if (File.Exists(item.FilePath))
                Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
        };

        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(folderBtn);
        outer.Children.Add(btnRow);

        itemBorder.Child = outer;
        DownloadItems.Children.Add(itemBorder);
    }

    private Button MakeShelfButton(string label) => new Button
    {
        Content = label,
        FontSize = 11,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#2a3a4a" : "#eef3fd")!),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(0, 0, 6, 0),
        Cursor = System.Windows.Input.Cursors.Hand,
        Template = CreateFlatButtonTemplate()
    };

    private static ControlTemplate CreateFlatButtonTemplate()
    {
        var tpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;
        return tpl;
    }

    private void UpdateDownloadItem(DownloadItem item)
    {
        foreach (Border border in DownloadItems.Children.OfType<Border>())
        {
            if (border.Tag?.ToString() != item.FilePath) continue;
            if (border.Child is not StackPanel outer) continue;

            // Update status text
            var topGrid = outer.Children.OfType<Grid>().FirstOrDefault();
            var infoStack = topGrid?.Children.OfType<StackPanel>().FirstOrDefault();
            var statusBlock = infoStack?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
            if (statusBlock != null)
                statusBlock.Text = item.Status;

            // Show action buttons when complete
            if (item.State == "completed")
            {
                var btnRow = outer.Children.OfType<StackPanel>()
                                  .FirstOrDefault(p => p.Tag?.ToString() == "actionRow");
                if (btnRow != null)
                    btnRow.Visibility = Visibility.Visible;
            }
        }
    }
    
    private async void SeeAllDownloads_Click(object sender, RoutedEventArgs e)
    {
        await CreateTab("ycb://downloads");
    }
    
    private void CloseDownloadShelf_Click(object sender, RoutedEventArgs e)
    {
        DownloadShelf.Visibility = Visibility.Collapsed;
        DownloadShelfRow.Height = new GridLength(0);
        DownloadItems.Children.Clear();
    }
    
    // ─── BITWARDEN CONTENT SCRIPT ───────────────────────────────────────
    private const string BitwardenSiteScript = @"
(function() {
    if (window.__ycbBitwardenInjected) return;
    window.__ycbBitwardenInjected = true;
    var matches = [];
    var savedOnce = false;

    function post(payload) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(payload));
            }
        } catch (err) {}
    }

    function setField(el, value) {
        if (!el || value == null) return;
        try {
            var proto = el.constructor && el.constructor.prototype || window.HTMLInputElement.prototype;
            var desc = Object.getOwnPropertyDescriptor(proto, 'value') || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
            if (desc && desc.set) desc.set.call(el, value);
            else el.value = value;
        } catch (err) {
            el.value = value;
        }
        el.dispatchEvent(new Event('input', { bubbles: true, cancelable: true }));
        el.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
    }

    function visible(el) {
        return !!(el && (el.offsetWidth || el.offsetHeight || el.getClientRects().length));
    }

    function firstVisible(selector, root) {
        var scope = root || document;
        var els = Array.from(scope.querySelectorAll(selector));
        return els.find(visible) || els[0] || null;
    }

    function detectKind(form) {
        var root = form || document;
        if (root.querySelector('input[autocomplete*=""cc-number"" i], input[name*=""card"" i], input[id*=""card"" i]')) return 'card';
        if (root.querySelector('input[type=""password""]')) return 'password';
        if (root.querySelector('input[type=""email""], input[name*=""phone"" i], input[name*=""email"" i], input[name*=""first"" i], input[name*=""last"" i]')) return 'identity';
        return '';
    }

    function collectData(form, kind) {
        var root = form || document;
        var payload = { name: '', notes: '' };
        if (kind === 'password') {
            var pwField = firstVisible('input[type=""password""]', root);
            var userField = firstVisible('input[type=""email""],input[type=""text""],input[autocomplete~=""username""],input[name*=""user"" i],input[name*=""login"" i],input[name*=""email"" i],input[id*=""user"" i],input[id*=""email"" i]', root);
            payload.name = (document.title || location.hostname || 'Login').trim();
            payload.username = userField ? (userField.value || '') : '';
            payload.password = pwField ? (pwField.value || '') : '';
            payload.url = location.href;
        } else if (kind === 'card') {
            var holder = firstVisible('input[name*=""holder"" i],input[id*=""holder"" i],input[autocomplete*=""cc-name"" i]', root);
            var number = firstVisible('input[autocomplete*=""cc-number"" i],input[name*=""card"" i],input[id*=""card"" i]', root);
            var brand = firstVisible('input[name*=""brand"" i],input[id*=""brand"" i]', root);
            var code = firstVisible('input[autocomplete*=""cc-csc"" i],input[name*=""code"" i],input[id*=""code"" i]', root);
            var month = firstVisible('input[autocomplete*=""cc-exp-month"" i],input[name*=""month"" i],input[id*=""month"" i]', root);
            var year = firstVisible('input[autocomplete*=""cc-exp-year"" i],input[name*=""year"" i],input[id*=""year"" i]', root);
            payload.name = (document.title || location.hostname || 'Card').trim();
            payload.cardHolder = holder ? (holder.value || '') : '';
            payload.cardNumber = number ? (number.value || '').replace(/\s+/g, '') : '';
            payload.cardBrand = brand ? (brand.value || '') : '';
            payload.cardCode = code ? (code.value || '') : '';
            payload.cardMonth = month ? (month.value || '') : '';
            payload.cardYear = year ? (year.value || '') : '';
        } else if (kind === 'identity') {
            var first = firstVisible('input[autocomplete*=""given-name"" i],input[name*=""first"" i],input[id*=""first"" i]', root);
            var last = firstVisible('input[autocomplete*=""family-name"" i],input[name*=""last"" i],input[id*=""last"" i]', root);
            var email = firstVisible('input[type=""email""],input[autocomplete*=""email"" i],input[name*=""email"" i],input[id*=""email"" i]', root);
            var phone = firstVisible('input[type=""tel""],input[autocomplete*=""tel"" i],input[name*=""phone"" i],input[id*=""phone"" i]', root);
            var company = firstVisible('input[name*=""company"" i],input[id*=""company"" i]', root);
            payload.name = (document.title || location.hostname || 'Identity').trim();
            payload.firstName = first ? (first.value || '') : '';
            payload.lastName = last ? (last.value || '') : '';
            payload.email = email ? (email.value || '') : '';
            payload.phone = phone ? (phone.value || '') : '';
            payload.company = company ? (company.value || '') : '';
        }
        return payload;
    }

    function fillPassword(item) {
        var pwField = firstVisible('input[type=""password""]');
        if (!pwField) return false;
        var form = pwField.closest('form') || document;
        var userField = firstVisible('input[type=""email""],input[type=""text""],input[autocomplete~=""username""],input[name*=""user"" i],input[name*=""login"" i],input[name*=""email"" i],input[id*=""user"" i],input[id*=""email"" i]', form);
        if (userField && !userField.value) setField(userField, item.username || '');
        if (!pwField.value) setField(pwField, item.password || '');
        return true;
    }

    function fillCard(item) {
        var form = firstVisible('input[autocomplete*=""cc-number"" i],input[name*=""card"" i],input[id*=""card"" i]')?.closest('form') || document;
        var holder = firstVisible('input[name*=""holder"" i],input[id*=""holder"" i],input[autocomplete*=""cc-name"" i]', form);
        var number = firstVisible('input[autocomplete*=""cc-number"" i],input[name*=""card"" i],input[id*=""card"" i]', form);
        var brand = firstVisible('input[name*=""brand"" i],input[id*=""brand"" i]', form);
        var code = firstVisible('input[autocomplete*=""cc-csc"" i],input[name*=""code"" i],input[id*=""code"" i]', form);
        var month = firstVisible('input[autocomplete*=""cc-exp-month"" i],input[name*=""month"" i],input[id*=""month"" i]', form);
        var year = firstVisible('input[autocomplete*=""cc-exp-year"" i],input[name*=""year"" i],input[id*=""year"" i]', form);
        if (holder && !holder.value) setField(holder, item.cardHolder || '');
        if (number && !number.value) setField(number, item.cardNumber || '');
        if (brand && !brand.value) setField(brand, item.cardBrand || '');
        if (code && !code.value) setField(code, item.cardCode || '');
        var expiry = (item.cardExpiry || '').split('/').map(function(s){ return s.trim(); }).filter(Boolean);
        if (month && !month.value) setField(month, item.cardMonth || expiry[0] || '');
        if (year && !year.value) setField(year, item.cardYear || expiry[1] || '');
        return true;
    }

    function fillIdentity(item) {
        var form = firstVisible('input[type=""email""],input[type=""tel""],input[name*=""phone"" i]', document)?.closest('form') || document;
        var first = firstVisible('input[autocomplete*=""given-name"" i],input[name*=""first"" i],input[id*=""first"" i]', form);
        var last = firstVisible('input[autocomplete*=""family-name"" i],input[name*=""last"" i],input[id*=""last"" i]', form);
        var email = firstVisible('input[type=""email""],input[autocomplete*=""email"" i],input[name*=""email"" i],input[id*=""email"" i]', form);
        var phone = firstVisible('input[type=""tel""],input[autocomplete*=""tel"" i],input[name*=""phone"" i],input[id*=""phone"" i]', form);
        var company = firstVisible('input[name*=""company"" i],input[id*=""company"" i]', form);
        if (first && !first.value) setField(first, item.firstName || '');
        if (last && !last.value) setField(last, item.lastName || '');
        if (email && !email.value) setField(email, item.email || '');
        if (phone && !phone.value) setField(phone, item.phone || '');
        if (company && !company.value) setField(company, item.company || '');
        return true;
    }

    function autofill() {
        var list = Array.isArray(matches) ? matches : [];
        if (!list.length) return;
        var passwordItem = list.find(function(i){ return i.kind === 'password'; });
        var cardItem = list.find(function(i){ return i.kind === 'card'; });
        var identityItem = list.find(function(i){ return i.kind === 'identity'; });
        if (passwordItem) fillPassword(passwordItem);
        if (cardItem) fillCard(cardItem);
        if (identityItem) fillIdentity(identityItem);
    }

    window.bitwardenSetMatches = function(next) {
        matches = Array.isArray(next) ? next : [];
        autofill();
    };

    document.addEventListener('submit', function(e) {
        var form = e.target && e.target.closest ? e.target.closest('form') : null;
        var kind = detectKind(form || document);
        if (!kind) return;
        var data = collectData(form || document, kind);
        if (!savedOnce) {
            savedOnce = true;
            post({ type: 'bitwarden:saveCandidate', kind: kind, url: location.href, item: data });
        }
    }, true);

    function markReady() {
        post({ type: 'bitwarden:siteReady', url: location.href, host: location.hostname, title: document.title || '' });
        autofill();
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', markReady, { once: true });
    else markReady();
    setTimeout(autofill, 1000);
})();
";

    private System.Threading.Tasks.Task HandleWebConsole(WebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var json = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (!json.RootElement.TryGetProperty("args", out var argsEl)) return System.Threading.Tasks.Task.CompletedTask;
            if (argsEl.ValueKind != JsonValueKind.Array || argsEl.GetArrayLength() == 0) return System.Threading.Tasks.Task.CompletedTask;
            if (!argsEl[0].TryGetProperty("value", out var valueEl)) return System.Threading.Tasks.Task.CompletedTask;
            var message = valueEl.GetString();
            if (message == null) return System.Threading.Tasks.Task.CompletedTask;
            if (message.StartsWith("__passwords__:"))
                return System.Threading.Tasks.Task.CompletedTask;
        }
        catch { }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void ShowSavePasswordPopup(string url, string username, string password)
    {
        var domain = GetDomain(url);
        ErrorReporter.Track("PwPrompt", new() { ["host"] = domain });
        var subtitle = string.IsNullOrEmpty(username) ? domain : $"{username} · {domain}";
        ShowPasswordPopup(
            "Save password?",
            subtitle,
            "Save",
            () =>
            {
                SavePassword(url, username, password);
            });
    }

    private void ShowAutofillPopup(WebView2 webView, string entriesJson, string domain, string username)
    {
        ErrorReporter.Track("AutofillShown", new() { ["host"] = domain });
        var subtitle = string.IsNullOrEmpty(username) ? domain : $"{username} · {domain}";
        ShowPasswordPopup(
            $"Sign in to {domain}?",
            subtitle,
            "Autofill",
            async () =>
            {
                ErrorReporter.Track("AutofillUsed", new() { ["host"] = domain });
                var script = BuildAutofillScript(entriesJson);
                try { await webView.ExecuteScriptAsync(script); } catch { }
            });
    }

    private void ShowPasswordPopup(string title, string subtitle, string confirmLabel, Action onConfirm)
    {
        var popup = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = this,
            Width = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        // Position below the BookmarkBtn (right side of URL bar)
        var pt = BookmarkBtn.PointToScreen(new Point(BookmarkBtn.ActualWidth / 2, BookmarkBtn.ActualHeight));
        popup.Left = pt.X - 280;
        popup.Top  = pt.Y + 6;

        var border = new Border
        {
            Background       = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#292a2d" : "#ffffff")!),
            CornerRadius     = new CornerRadius(8),
            BorderBrush      = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c4043" : "#dfe1e5")!),
            BorderThickness  = new Thickness(1),
            Padding          = new Thickness(16, 14, 16, 14)
        };
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Icon + text
        var topStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var iconCanvas = new Canvas { Width = 18, Height = 18, Margin = new Thickness(0, 1, 10, 0) };
        var lockRect = new System.Windows.Shapes.Rectangle
        {
            Width = 12, Height = 8, RadiusX = 2, RadiusY = 2,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            StrokeThickness = 1.5, Fill = Brushes.Transparent
        };
        System.Windows.Controls.Canvas.SetLeft(lockRect, 3);
        System.Windows.Controls.Canvas.SetTop(lockRect, 9);
        iconCanvas.Children.Add(lockRect);
        var lockArch = new WpfPath
        {
            Data = Geometry.Parse("M5 9 C5 6 7 4.5 9 4.5 C11 4.5 13 6 13 9"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            StrokeThickness = 1.5, Fill = Brushes.Transparent,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 18, Height = 18, Stretch = Stretch.None
        };
        iconCanvas.Children.Add(lockArch);
        topStack.Children.Add(iconCanvas);

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!),
            FontSize = 13, FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(subtitle))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!),
                FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220
            });
        }
        topStack.Children.Add(textStack);
        Grid.SetRow(topStack, 0);
        mainGrid.Children.Add(topStack);

        // Buttons
        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var dismissBtn = new Button
        {
            Content = "Not now", Background = Brushes.Transparent,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            BorderThickness = new Thickness(0), Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand, FontSize = 13
        };

        var confirmBtn = new Button
        {
            Content = confirmLabel,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!),
            BorderThickness = new Thickness(0), Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0)
        };
        confirmBtn.Resources.Add(typeof(Border), new Style(typeof(Border))
        {
            Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(4)) }
        });

        dismissBtn.Click += (s, a) => popup.Close();
        confirmBtn.Click += (s, a) => { popup.Close(); onConfirm(); };

        btnStack.Children.Add(dismissBtn);
        btnStack.Children.Add(confirmBtn);
        Grid.SetRow(btnStack, 1);
        mainGrid.Children.Add(btnStack);

        border.Child = mainGrid;
        popup.Content = border;
        popup.Deactivated += (s, a) => { try { if (popup.IsVisible) popup.Close(); } catch { } };
        popup.Show();
        ForcePopupOnTop(popup);
        TrackPopupPosition(popup, () => { var pt = BookmarkBtn.PointToScreen(new Point(BookmarkBtn.ActualWidth / 2, BookmarkBtn.ActualHeight)); return (pt.X - 280, pt.Y + 6); });
    }

    private void ShowRestorePrompt()
    {
        var count = _settings.LastTabs?.Count ?? 0;
        if (count == 0) return;

        var popup = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = this,
            Width = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        // Position below the toolbar, right-aligned inside the browser window
        var winPt = this.PointToScreen(new Point(this.ActualWidth, 36 + 46));
        popup.Left = winPt.X - 310;  // 300px popup + 10px margin from right edge
        popup.Top  = winPt.Y + 4;

        var border = new Border
        {
            Background      = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#292a2d" : "#ffffff")!),
            CornerRadius    = new CornerRadius(8),
            BorderBrush     = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#3c4043" : "#dfe1e5")!),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(16, 14, 16, 14)
        };
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black
        };

        var stack = new StackPanel();

        // Icon + title row
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var iconPath = new WpfPath
        {
            Data = Geometry.Parse("M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0h6"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            StrokeThickness = 1.6, Fill = Brushes.Transparent,
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 1, 8, 0),
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var titleBlock = new TextBlock
        {
            Text = "Restore your tabs?",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#e8eaed" : "#202124")!),
            FontSize = 13, FontWeight = FontWeights.Medium
        };
        titleRow.Children.Add(iconPath);
        titleRow.Children.Add(titleBlock);
        stack.Children.Add(titleRow);

        var subBlock = new TextBlock
        {
            Text = $"You had {count} tab{(count == 1 ? "" : "s")} open last time",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isDarkMode ? "#9aa0a6" : "#5f6368")!),
            FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(subBlock);

        // List each tab by host + path
        var tabList = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var displayTabs = _settings.LastTabs!.Take(6).ToList();
        foreach (var url in displayTabs)
        {
            string display;
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.TrimEnd('/');
                display = path.Length > 1 ? uri.Host + path : uri.Host;
            }
            catch { display = url.Length > 40 ? url[..40] + "…" : url; }
            if (display.Length > 45) display = display[..45] + "…";
            tabList.Children.Add(new TextBlock
            {
                Text = "• " + display,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#bdc1c6")!),
                FontSize = 11, Margin = new Thickness(4, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        if (count > 6)
            tabList.Children.Add(new TextBlock
            {
                Text = $"  + {count - 6} more…",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5f6368")!),
                FontSize = 11, Margin = new Thickness(4, 1, 0, 0)
            });
        stack.Children.Add(tabList);

        // Buttons
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var freshBtn = new Button
        {
            Content = "Start fresh", FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        freshBtn.Click += (s, e) =>
        {
            // Track the startup tab so SaveSettings() can exclude it from LastTabs —
            // prevents the restore prompt appearing next session just for the homepage.
            _freshStartTab = _tabs.Count > 0 ? _tabs[0] : null;
            _freshStartTabInitialUrl = _freshStartTab?.Url;
            _settings.LastTabs = null;
            popup.Close();
        };

        var restoreBtn = new Button
        {
            Content = "Restore", FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a2a3a")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        restoreBtn.Click += async (s, e) =>
        {
            popup.Close();
            var tabsToRestore = _settings.LastTabs!.ToList();
            // Open restored tabs first, then close the placeholder
            foreach (var url in tabsToRestore)
                await CreateTab(url);
            // Now safe to close the placeholder (index 0) since we have other tabs open
            if (_tabs.Count > tabsToRestore.Count)
                CloseTab(0);
        };

        btnRow.Children.Add(freshBtn);
        btnRow.Children.Add(restoreBtn);
        stack.Children.Add(btnRow);

        border.Child = stack;
        popup.Content = border;
        popup.Show();
        ForcePopupOnTop(popup);
        TrackPopupPosition(popup, () => { var p = this.PointToScreen(new Point(this.ActualWidth, 36 + 46)); return (p.X - 310, p.Y + 4); });
    }

    /// <summary>Keeps <paramref name="popup"/> anchored to the same screen position relative to the
    /// main window whenever the window is moved or resized, and ensures it stays above the
    /// WebView2 content even when the main window is activated.</summary>
    private void TrackPopupPosition(Window popup, Func<(double left, double top)> computePosition)
    {
        void Reposition()
        {
            if (!popup.IsVisible) return;
            var (left, top) = computePosition();
            popup.Left = left;
            popup.Top  = top;
            ForcePopupOnTop(popup);
        }
        void OnMoveOrActivate(object? s, EventArgs e) => Reposition();
        void OnSizeChanged(object? s, SizeChangedEventArgs e) => Reposition();
        LocationChanged += OnMoveOrActivate;
        Activated       += OnMoveOrActivate;
        SizeChanged     += OnSizeChanged;
        popup.Closed    += (s, e) =>
        {
            LocationChanged -= OnMoveOrActivate;
            Activated       -= OnMoveOrActivate;
            SizeChanged     -= OnSizeChanged;
        };
    }

    /// <summary>Forces <paramref name="popup"/> to the top of the z-order without stealing focus,
    /// counteracting any airspace z-order disturbance caused by WebView2's HWND.</summary>
    private void ForcePopupOnTop(Window popup)
    {
        var handle = new WindowInteropHelper(popup).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    private static string GetDomain(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return url; }
    }

    private static string GetHostOnly(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }
    private void AddToHistory(string? url, string? title)
    {
        if (_isIncognito) return;
        if (string.IsNullOrEmpty(url) || url.StartsWith("ycb://")) return;
        // Ignore entries fired immediately after a clear (WebView2 can fire
        // DocumentTitleChanged on all open tabs right after ClearBrowsingData)
        if ((DateTime.UtcNow - _historyClearedAt).TotalSeconds < 3) return;
        
        try
        {
            var history = LoadHistory();
            history.Insert(0, new HistoryItem { Url = url, Title = title ?? url, Timestamp = DateTime.Now });
            if (history.Count > 1000) history.RemoveAt(history.Count - 1);
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch { }
    }
    
    private List<HistoryItem> LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                var all = JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new List<HistoryItem>();
                // Filter out anything older than the last clear — bulletproof even if delete fails
                if (_historyClearedAt > DateTime.MinValue)
                    all = all.Where(h => h.Timestamp > _historyClearedAt).ToList();
                return all;
            }
        }
        catch { }
        return new List<HistoryItem>();
    }
    
    private void SaveDownload(DownloadItem item)
    {
        try
        {
            var downloads = LoadDownloads();
            downloads.Insert(0, item);
            var json = JsonSerializer.Serialize(downloads, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_downloadsPath, json);
        }
        catch { }
    }
    
    private List<DownloadItem> LoadDownloads()
    {
        try
        {
            if (File.Exists(_downloadsPath))
            {
                var json = File.ReadAllText(_downloadsPath);
                return JsonSerializer.Deserialize<List<DownloadItem>>(json) ?? new List<DownloadItem>();
            }
        }
        catch { }
        return new List<DownloadItem>();
    }
    
    private void ClearHistory()
    {
        _historyClearedAt = DateTime.UtcNow;
        _settings.HistoryClearedAt = _historyClearedAt;
        SaveSettings();
        // Delete and recreate as empty — leaves no old data on disk
        try
        {
            File.Delete(_historyPath);
            File.WriteAllText(_historyPath, "[]");
        }
        catch { }
    }
    
    private void ClearDownloads()
    {
        try
        {
            if (File.Exists(_downloadsPath))
            {
                File.Delete(_downloadsPath);
            }
        }
        catch { }
    }
    
    // Bookmark management
    private List<BookmarkItem> LoadBookmarks()
    {
        try
        {
            if (File.Exists(_bookmarksPath))
            {
                var json = File.ReadAllText(_bookmarksPath);
                return JsonSerializer.Deserialize<List<BookmarkItem>>(json) ?? new List<BookmarkItem>();
            }
        }
        catch { }
        return new List<BookmarkItem>();
    }
    
    private void AddBookmark(string url, string label)
    {
        if (string.IsNullOrEmpty(url)) return;
        
        try
        {
            var bookmarks = LoadBookmarks();
            bookmarks.Add(new BookmarkItem { Url = url, Label = label });
            
            var json = JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_bookmarksPath, json);
        }
        catch { }
    }
    
    private void RemoveBookmark(int index)
    {
        try
        {
            var bookmarks = LoadBookmarks();
            if (index >= 0 && index < bookmarks.Count)
            {
                bookmarks.RemoveAt(index);
                var json = JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_bookmarksPath, json);
            }
        }
        catch { }
    }
    
    // Password management
    private static string EncryptPassword(string plaintext)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(plaintext);
            var enc  = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return "DPAPI:" + Convert.ToBase64String(enc);
        }
        catch { return plaintext; }
    }

    private static string DecryptPassword(string stored)
    {
        try
        {
            if (stored.StartsWith("DPAPI:"))
            {
                var data = Convert.FromBase64String(stored.Substring(6));
                var dec  = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            return stored; // legacy plaintext
        }
        catch { return stored; }
    }

    private List<PasswordItem> LoadPasswords()
    {
        try
        {
            if (File.Exists(_passwordsPath))
            {
                var json  = File.ReadAllText(_passwordsPath);
                var items = JsonSerializer.Deserialize<List<PasswordItem>>(json) ?? new List<PasswordItem>();
                bool migrated = false;
                foreach (var item in items)
                {
                    item.Kind = string.IsNullOrWhiteSpace(item.Kind)
                        ? (!string.IsNullOrWhiteSpace(item.CardNumber) ? "card" : "password")
                        : item.Kind;

                    if (item.Kind == "card")
                    {
                        if (!string.IsNullOrEmpty(item.CardNumber) && !item.CardNumber.StartsWith("DPAPI:"))
                        {
                            item.CardNumber = EncryptPassword(item.CardNumber);
                            migrated = true;
                        }
                    }
                    else
                    {
                        if (!item.Password.StartsWith("DPAPI:"))
                        {
                            item.Password = EncryptPassword(item.Password);
                            migrated = true;
                        }
                    }
                }
                if (migrated)
                    File.WriteAllText(_passwordsPath, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
                return items;
            }
        }
        catch { }
        return new List<PasswordItem>();
    }

    private List<PasswordItem> LoadPasswordsDecrypted()
    {
        return LoadPasswords().Select(p => new PasswordItem
        {
            Key      = p.Key,
            Url      = p.Url,
            Username = p.Username,
            Password = DecryptPassword(p.Password),
            Kind     = string.IsNullOrWhiteSpace(p.Kind)
                ? (!string.IsNullOrWhiteSpace(p.CardNumber) ? "card" : "password")
                : p.Kind,
            Label    = p.Label,
            CardHolder = p.CardHolder,
            CardNumber = DecryptPassword(p.CardNumber),
            CardExpiry = p.CardExpiry,
            CardBrand = p.CardBrand,
            Notes = p.Notes
        }).ToList();
    }
    
    private void SavePassword(string url, string username, string password)
    {
        if (string.IsNullOrEmpty(url)) return;
        var domain = GetDomain(url);
        try
        {
            var passwords = LoadPasswords();
            var existing = passwords.FirstOrDefault(p => GetDomain(p.Url) == domain && p.Username == username);
            var encrypted = EncryptPassword(password);
            if (existing != null)
            {
                existing.Kind = "password";
                existing.Password = encrypted;
                existing.Url = url;
                existing.Label = "";
                existing.CardHolder = "";
                existing.CardNumber = "";
                existing.CardExpiry = "";
                existing.CardBrand = "";
                existing.Notes = "";
                ErrorReporter.Track("PwSaved", new() { ["host"] = domain, ["upd"] = true });
            }
            else
            {
                passwords.Add(new PasswordItem
                {
                    Key = $"{domain}_{username}",
                    Url = url,
                    Username = username,
                    Password = encrypted,
                    Kind = "password"
                });
                ErrorReporter.Track("PwSaved", new() { ["host"] = domain, ["upd"] = false });
            }
            var json = JsonSerializer.Serialize(passwords, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_passwordsPath, json);
        }
        catch { }
    }

    private void SaveCard(PasswordItem card)
    {
        if (card == null) return;
        try
        {
            var vault = LoadPasswords();
            var existing = vault.FirstOrDefault(p => (p.Key ?? "") == (card.Key ?? ""));
            card.Kind = "card";
            card.CardNumber = EncryptPassword(card.CardNumber ?? "");
            if (existing != null)
            {
                existing.Kind = "card";
                existing.Url = card.Url ?? "";
                existing.Label = card.Label ?? "";
                existing.CardHolder = card.CardHolder ?? "";
                if (!string.IsNullOrWhiteSpace(card.CardNumber))
                    existing.CardNumber = card.CardNumber;
                existing.CardExpiry = card.CardExpiry ?? "";
                existing.CardBrand = card.CardBrand ?? "";
                existing.Notes = card.Notes ?? "";
            }
            else
            {
                vault.Add(card);
            }
            File.WriteAllText(_passwordsPath, JsonSerializer.Serialize(vault, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void UpdateCard(PasswordItem card)
    {
        if (card == null) return;
        try
        {
            var vault = LoadPasswords();
            var existing = vault.FirstOrDefault(p => (p.Key ?? "") == (card.Key ?? ""));
            if (existing == null) return;
            existing.Kind = "card";
            existing.Url = card.Url ?? existing.Url;
            existing.Label = card.Label ?? existing.Label;
            existing.CardHolder = card.CardHolder ?? existing.CardHolder;
            existing.CardExpiry = card.CardExpiry ?? existing.CardExpiry;
            existing.CardBrand = card.CardBrand ?? existing.CardBrand;
            existing.Notes = card.Notes ?? existing.Notes;
            if (!string.IsNullOrWhiteSpace(card.CardNumber))
                existing.CardNumber = EncryptPassword(card.CardNumber);
            File.WriteAllText(_passwordsPath, JsonSerializer.Serialize(vault, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
    
    private void DeletePassword(string key)
    {
        try
        {
            var passwords = LoadPasswords();
            passwords.RemoveAll(p => (p.Key ?? p.Url) == key);
            
            var json = JsonSerializer.Serialize(passwords, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_passwordsPath, json);
        }
        catch { }
    }
    
    private void ClearPasswords()
    {
        try
        {
            if (File.Exists(_passwordsPath))
            {
                File.Delete(_passwordsPath);
            }
        }
        catch { }
    }

    private sealed record BitwardenCommandResult(int ExitCode, string StdOut, string StdErr);

    private static string? FindBitwardenCliExe()
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "bw",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            proc?.WaitForExit(2500);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim();
            var first = output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first) && File.Exists(first))
                return first;
        }
        catch { }
        return null;
    }

    private (string FileName, List<string> PrefixArgs) GetBitwardenRunner()
    {
        var bwExe = FindBitwardenCliExe();
        if (!string.IsNullOrWhiteSpace(bwExe))
        {
            return (bwExe, new List<string>());
        }

        var nodeExe = FindNodeExe();
        var npxCli = FindNpxCli();
        if (!string.IsNullOrWhiteSpace(nodeExe) && !string.IsNullOrWhiteSpace(npxCli))
        {
            return (nodeExe!, new List<string> { npxCli!, "--yes", "@bitwarden/cli" });
        }

        return ("npx", new List<string> { "--yes", "@bitwarden/cli" });
    }

    private async Task<BitwardenCommandResult> RunBitwardenAsync(IEnumerable<string> arguments, string? session = null, string? stdin = null)
    {
        var runner = GetBitwardenRunner();
        var psi = new ProcessStartInfo
        {
            FileName = runner.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            CreateNoWindow = true
        };

        foreach (var arg in runner.PrefixArgs)
            psi.ArgumentList.Add(arg);
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["BITWARDENCLI_APPDATA_DIR"] = _bitwardenCliAppDataDir;
        if (!string.IsNullOrWhiteSpace(session))
            psi.Environment["BW_SESSION"] = session;

        var process = new Process { StartInfo = psi };
        process.Start();
        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new BitwardenCommandResult(process.ExitCode, stdout, stderr);
    }

    private async Task<string> GetBitwardenStatusJsonAsync()
    {
        try
        {
            var result = await RunBitwardenAsync(new[] { "status" });
            return result.StdOut.Trim();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    private async Task<List<PasswordItem>> LoadBitwardenVaultAsync()
    {
        var items = new List<PasswordItem>();
        try
        {
            if (string.IsNullOrWhiteSpace(_bitwardenSession))
                return items;

            var result = await RunBitwardenAsync(new[] { "list", "items" }, _bitwardenSession);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
                return items;

            using var doc = JsonDocument.Parse(result.StdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return items;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var mapped = MapBitwardenItem(el);
                if (mapped != null)
                    items.Add(mapped);
            }
        }
        catch { }
        return items;
    }

    private async Task<PasswordItem?> LoadBitwardenItemAsync(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(_bitwardenSession))
                return null;

            var result = await RunBitwardenAsync(new[] { "get", "item", id }, _bitwardenSession);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
                return null;

            using var doc = JsonDocument.Parse(result.StdOut);
            return MapBitwardenItem(doc.RootElement);
        }
        catch { return null; }
    }

    private async Task PushBitwardenMatchesAsync(WebView2 webView, string siteUrl)
    {
        try
        {
            if (webView?.CoreWebView2 == null || string.IsNullOrWhiteSpace(siteUrl))
                return;

            var matches = await GetBitwardenMatchesAsync(siteUrl);
            var matchesJson = JsonSerializer.Serialize(matches);
            await webView.ExecuteScriptAsync($"window.bitwardenSetMatches && window.bitwardenSetMatches({matchesJson})");
        }
        catch { }
    }

    private async Task<List<PasswordItem>> GetBitwardenMatchesAsync(string siteUrl)
    {
        var items = await LoadBitwardenVaultAsync();
        if (items.Count == 0)
            return items;

        var host = GetDomain(siteUrl);
        if (string.IsNullOrWhiteSpace(host))
            return items;

        bool UrlMatches(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            var candidateHost = GetDomain(candidate);
            if (string.IsNullOrWhiteSpace(candidateHost)) return false;
            return string.Equals(candidateHost, host, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + candidateHost, StringComparison.OrdinalIgnoreCase) ||
                   candidateHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
        }

        var filtered = items.Where(i =>
            (i.Kind == "password" || i.Kind == "card" || i.Kind == "identity") &&
            (UrlMatches(i.Url) ||
             (i.Label?.Contains(host, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (i.Notes?.Contains(host, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();

        return filtered.Count > 0 ? filtered : items;
    }

    private async Task<bool> BitwardenAlreadyExistsAsync(string kind, string siteUrl, Dictionary<string, string> data)
    {
        try
        {
            var items = await LoadBitwardenVaultAsync();
            var host = GetDomain(siteUrl);
            string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();
            string Last4(string? value)
            {
                var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
                return digits.Length <= 4 ? digits : digits[^4..];
            }

            foreach (var item in items.Where(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            {
                if (kind == "password")
                {
                    var siteMatch = !string.IsNullOrWhiteSpace(host) &&
                                    (Normalize(item.Label).Contains(Normalize(host)) || Normalize(item.Url).Contains(Normalize(host)));
                    var userMatch = Normalize(item.Username) == Normalize(data.GetValueOrDefault("username"));
                    if (siteMatch && userMatch)
                        return true;
                }
                else if (kind == "card")
                {
                    var holderMatch = Normalize(item.CardHolder) == Normalize(data.GetValueOrDefault("cardHolder"));
                    var last4Match = Last4(item.CardNumber) == Last4(data.GetValueOrDefault("cardNumber"));
                    if (holderMatch && last4Match)
                        return true;
                }
                else if (kind == "identity")
                {
                    var emailMatch = Normalize(item.Email) == Normalize(data.GetValueOrDefault("email"));
                    var phoneMatch = Normalize(item.Phone) == Normalize(data.GetValueOrDefault("phone"));
                    if (emailMatch || phoneMatch)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private void ShowBitwardenSavePrompt(string kind, string siteUrl, Dictionary<string, string> data)
    {
        var host = GetDomain(siteUrl);
        var subtitle = string.IsNullOrWhiteSpace(host) ? "Save in Bitwarden?" : $"Save this {kind} for {host}?";
        ShowPasswordPopup(
            "Save in Bitwarden?",
            subtitle,
            "Save",
            () =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await CreateBitwardenItemAsync(kind, data);
                        if (result.ExitCode == 0)
                            await SyncBitwardenAsync();
                        else
                            App.WriteTrace($"Bitwarden save failed: {result.StdErr}");
                    }
                    catch (Exception ex)
                    {
                        App.WriteTrace($"Bitwarden save failed: {ex.Message}");
                    }
                });
            });
    }

    private static string BuildAutofillScript(string entriesJson)
    {
        return $@"
(function(entries) {{
    if (!Array.isArray(entries) || !entries.length) return;
    var entry = entries[0];

    function setField(el, value) {{
        if (!el || value == null) return;
        try {{
            var proto = el.constructor && el.constructor.prototype || window.HTMLInputElement.prototype;
            var desc = Object.getOwnPropertyDescriptor(proto, 'value') || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
            if (desc && desc.set) desc.set.call(el, value);
            else el.value = value;
        }} catch (err) {{
            el.value = value;
        }}
        el.dispatchEvent(new Event('input', {{ bubbles: true, cancelable: true }}));
        el.dispatchEvent(new Event('change', {{ bubbles: true, cancelable: true }}));
    }}

    function visible(el) {{
        return !!(el && (el.offsetWidth || el.offsetHeight || el.getClientRects().length));
    }}

    function firstVisible(selector, root) {{
        var scope = root || document;
        var els = Array.from(scope.querySelectorAll(selector));
        return els.find(visible) || els[0] || null;
    }}

    var form = document.querySelector('form') || document;
    if (entry.kind === 'password') {{
        var userField = firstVisible('input[type=""email""],input[type=""text""],input[autocomplete~=""username""],input[name*=""user"" i],input[name*=""login"" i],input[name*=""email"" i],input[id*=""user"" i],input[id*=""email"" i]', form);
        var pwField = firstVisible('input[type=""password""]', form);
        if (userField) setField(userField, entry.username || '');
        if (pwField) setField(pwField, entry.password || '');
    }} else if (entry.kind === 'card') {{
        var holder = firstVisible('input[name*=""holder"" i],input[id*=""holder"" i],input[autocomplete*=""cc-name"" i]', form);
        var number = firstVisible('input[autocomplete*=""cc-number"" i],input[name*=""card"" i],input[id*=""card"" i]', form);
        var brand = firstVisible('input[name*=""brand"" i],input[id*=""brand"" i]', form);
        var code = firstVisible('input[autocomplete*=""cc-csc"" i],input[name*=""code"" i],input[id*=""code"" i]', form);
        var month = firstVisible('input[autocomplete*=""cc-exp-month"" i],input[name*=""month"" i],input[id*=""month"" i]', form);
        var year = firstVisible('input[autocomplete*=""cc-exp-year"" i],input[name*=""year"" i],input[id*=""year"" i]', form);
        var expiry = (entry.cardExpiry || '').split('/').map(function(s) {{ return s.trim(); }}).filter(Boolean);
        if (holder) setField(holder, entry.cardHolder || '');
        if (number) setField(number, entry.cardNumber || '');
        if (brand) setField(brand, entry.cardBrand || '');
        if (code) setField(code, entry.cardCode || '');
        if (month) setField(month, entry.cardMonth || expiry[0] || '');
        if (year) setField(year, entry.cardYear || expiry[1] || '');
    }} else if (entry.kind === 'identity') {{
        var first = firstVisible('input[autocomplete*=""given-name"" i],input[name*=""first"" i],input[id*=""first"" i]', form);
        var last = firstVisible('input[autocomplete*=""family-name"" i],input[name*=""last"" i],input[id*=""last"" i]', form);
        var email = firstVisible('input[type=""email""],input[autocomplete*=""email"" i],input[name*=""email"" i],input[id*=""email"" i]', form);
        var phone = firstVisible('input[type=""tel""],input[autocomplete*=""tel"" i],input[name*=""phone"" i],input[id*=""phone"" i]', form);
        var company = firstVisible('input[name*=""company"" i],input[id*=""company"" i]', form);
        if (first) setField(first, entry.firstName || '');
        if (last) setField(last, entry.lastName || '');
        if (email) setField(email, entry.email || '');
        if (phone) setField(phone, entry.phone || '');
        if (company) setField(company, entry.company || '');
    }}
}})({entriesJson});
";
    }

    private async Task SendBitwardenStateAsync(CoreWebView2 webView)
    {
        var statusJson = await GetBitwardenStatusJsonAsync();
        await webView.ExecuteScriptAsync($"window.setBitwardenState && window.setBitwardenState({statusJson})");
        var vault = await LoadBitwardenVaultAsync();
        var vaultJson = JsonSerializer.Serialize(vault);
        await webView.ExecuteScriptAsync($"window.setBitwardenItems && window.setBitwardenItems({vaultJson})");
    }

    private static Task SendBitwardenErrorAsync(CoreWebView2 webView, string message)
    {
        var safe = JsonSerializer.Serialize(message);
        return webView.ExecuteScriptAsync($"window.onBitwardenError && window.onBitwardenError({safe})");
    }

    private async Task SendBitwardenItemAsync(CoreWebView2 webView, string id)
    {
        var item = await LoadBitwardenItemAsync(id);
        var itemJson = JsonSerializer.Serialize(item);
        await webView.ExecuteScriptAsync($"window.setBitwardenItem && window.setBitwardenItem({itemJson})");
    }

    private static PasswordItem? MapBitwardenItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        string GetString(JsonElement obj, params string[] path)
        {
            var cur = obj;
            foreach (var seg in path)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                    return "";
            }
            return cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? "" : cur.ToString();
        }

        var kind = "password";
        if (item.TryGetProperty("type", out var typeEl))
        {
            kind = typeEl.ValueKind switch
            {
                JsonValueKind.Number when typeEl.GetInt32() == 3 => "card",
                JsonValueKind.Number when typeEl.GetInt32() == 4 => "identity",
                _ => "password"
            };
        }

        var result = new PasswordItem
        {
            Key = GetString(item, "id"),
            Kind = kind,
            Label = GetString(item, "name"),
            Notes = GetString(item, "notes"),
            Url = GetString(item, "login", "uris", "0", "uri"),
            Username = GetString(item, "login", "username"),
            Password = GetString(item, "login", "password"),
            CardHolder = GetString(item, "card", "cardholderName"),
            CardNumber = GetString(item, "card", "number"),
            CardBrand = GetString(item, "card", "brand"),
            CardCode = GetString(item, "card", "code"),
            CardExpiry = $"{GetString(item, "card", "expMonth")}/{GetString(item, "card", "expYear")}".Trim('/'),
            Email = GetString(item, "identity", "email"),
            Phone = GetString(item, "identity", "phone"),
            FirstName = GetString(item, "identity", "firstName"),
            LastName = GetString(item, "identity", "lastName"),
            Company = GetString(item, "identity", "company")
        };

        if (string.IsNullOrWhiteSpace(result.Label))
        {
            result.Label = result.Kind switch
            {
                "card" => $"{result.CardBrand} {result.CardHolder}".Trim(),
                "identity" => $"{result.FirstName} {result.LastName}".Trim(),
                _ => result.Username
            };
        }

        return result;
    }

    private static string EncodeBwPayload(JsonObject node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static JsonObject BuildLoginPayload(string name, string username, string password, string url, string notes)
    {
        var payload = new JsonObject
        {
            ["type"] = 1,
            ["name"] = name,
            ["notes"] = notes,
            ["login"] = new JsonObject
            {
                ["username"] = username,
                ["password"] = password,
                ["uris"] = new JsonArray(new JsonObject { ["uri"] = url })
            }
        };
        return payload;
    }

    private static JsonObject BuildCardPayload(string name, string holder, string number, string brand, string expiryMonth, string expiryYear, string code, string notes)
    {
        var payload = new JsonObject
        {
            ["type"] = 3,
            ["name"] = name,
            ["notes"] = notes,
            ["card"] = new JsonObject
            {
                ["cardholderName"] = holder,
                ["brand"] = brand,
                ["number"] = number,
                ["expMonth"] = expiryMonth,
                ["expYear"] = expiryYear,
                ["code"] = code
            }
        };
        return payload;
    }

    private static JsonObject BuildIdentityPayload(string name, string firstName, string lastName, string email, string phone, string company, string notes)
    {
        var payload = new JsonObject
        {
            ["type"] = 4,
            ["name"] = name,
            ["notes"] = notes,
            ["identity"] = new JsonObject
            {
                ["firstName"] = firstName,
                ["lastName"] = lastName,
                ["email"] = email,
                ["phone"] = phone,
                ["company"] = company
            }
        };
        return payload;
    }

    private async Task<BitwardenCommandResult> CreateBitwardenItemAsync(string kind, Dictionary<string, string> data)
    {
        JsonObject payload = kind switch
        {
            "card" => BuildCardPayload(
                data.GetValueOrDefault("name", ""),
                data.GetValueOrDefault("cardHolder", ""),
                data.GetValueOrDefault("cardNumber", ""),
                data.GetValueOrDefault("cardBrand", ""),
                data.GetValueOrDefault("cardMonth", ""),
                data.GetValueOrDefault("cardYear", ""),
                data.GetValueOrDefault("cardCode", ""),
                data.GetValueOrDefault("notes", "")),
            "identity" => BuildIdentityPayload(
                data.GetValueOrDefault("name", ""),
                data.GetValueOrDefault("firstName", ""),
                data.GetValueOrDefault("lastName", ""),
                data.GetValueOrDefault("email", ""),
                data.GetValueOrDefault("phone", ""),
                data.GetValueOrDefault("company", ""),
                data.GetValueOrDefault("notes", "")),
            _ => BuildLoginPayload(
                data.GetValueOrDefault("name", ""),
                data.GetValueOrDefault("username", ""),
                data.GetValueOrDefault("password", ""),
                data.GetValueOrDefault("url", ""),
                data.GetValueOrDefault("notes", ""))
        };

        var encoded = EncodeBwPayload(payload);
        return await RunBitwardenAsync(new[] { "create", "item", encoded }, _bitwardenSession);
    }

    private async Task<BitwardenCommandResult> UpdateBitwardenItemAsync(string id, string kind, Dictionary<string, string> data)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new BitwardenCommandResult(1, "", "Missing item id");

        var getResult = await RunBitwardenAsync(new[] { "get", "item", id }, _bitwardenSession);
        if (getResult.ExitCode != 0 || string.IsNullOrWhiteSpace(getResult.StdOut))
            return new BitwardenCommandResult(getResult.ExitCode, getResult.StdOut, getResult.StdErr);

        var node = JsonNode.Parse(getResult.StdOut) as JsonObject;
        if (node == null)
            return new BitwardenCommandResult(1, "", "Unable to parse Bitwarden item");

        node["name"] = data.GetValueOrDefault("name", node["name"]?.GetValue<string>() ?? "");
        node["notes"] = data.GetValueOrDefault("notes", node["notes"]?.GetValue<string>() ?? "");

        if (kind == "card")
        {
            node["type"] = 3;
            node["card"] ??= new JsonObject();
            var card = node["card"] as JsonObject;
            if (card != null)
            {
                card["cardholderName"] = data.GetValueOrDefault("cardHolder", card["cardholderName"]?.GetValue<string>() ?? "");
                card["brand"] = data.GetValueOrDefault("cardBrand", card["brand"]?.GetValue<string>() ?? "");
                card["number"] = data.GetValueOrDefault("cardNumber", card["number"]?.GetValue<string>() ?? "");
                card["expMonth"] = data.GetValueOrDefault("cardMonth", card["expMonth"]?.GetValue<string>() ?? "");
                card["expYear"] = data.GetValueOrDefault("cardYear", card["expYear"]?.GetValue<string>() ?? "");
                card["code"] = data.GetValueOrDefault("cardCode", card["code"]?.GetValue<string>() ?? "");
            }
        }
        else if (kind == "identity")
        {
            node["type"] = 4;
            node["identity"] ??= new JsonObject();
            var identity = node["identity"] as JsonObject;
            if (identity != null)
            {
                identity["firstName"] = data.GetValueOrDefault("firstName", identity["firstName"]?.GetValue<string>() ?? "");
                identity["lastName"] = data.GetValueOrDefault("lastName", identity["lastName"]?.GetValue<string>() ?? "");
                identity["email"] = data.GetValueOrDefault("email", identity["email"]?.GetValue<string>() ?? "");
                identity["phone"] = data.GetValueOrDefault("phone", identity["phone"]?.GetValue<string>() ?? "");
                identity["company"] = data.GetValueOrDefault("company", identity["company"]?.GetValue<string>() ?? "");
            }
        }
        else
        {
            node["type"] = 1;
            node["login"] ??= new JsonObject();
            var login = node["login"] as JsonObject;
            if (login != null)
            {
                login["username"] = data.GetValueOrDefault("username", login["username"]?.GetValue<string>() ?? "");
                login["password"] = data.GetValueOrDefault("password", login["password"]?.GetValue<string>() ?? "");
                var url = data.GetValueOrDefault("url", "");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    login["uris"] = new JsonArray(new JsonObject { ["uri"] = url });
                }
            }
        }

        var encoded = EncodeBwPayload(node);
        return await RunBitwardenAsync(new[] { "edit", "item", id, encoded }, _bitwardenSession);
    }

    private async Task<BitwardenCommandResult> DeleteBitwardenItemAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new BitwardenCommandResult(1, "", "Missing item id");
        return await RunBitwardenAsync(new[] { "delete", "item", id }, _bitwardenSession);
    }

    private async Task<BitwardenCommandResult> LoginBitwardenAsync(string email, string password)
    {
        var result = await RunBitwardenAsync(new[] { "login", email, password, "--raw" });
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            _bitwardenSession = result.StdOut.Trim();
        return result;
    }

    private async Task<BitwardenCommandResult> UnlockBitwardenAsync(string password)
    {
        var result = await RunBitwardenAsync(new[] { "unlock", password, "--raw" });
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            _bitwardenSession = result.StdOut.Trim();
        return result;
    }

    private async Task<BitwardenCommandResult> SyncBitwardenAsync()
    {
        return await RunBitwardenAsync(new[] { "sync" }, _bitwardenSession);
    }

    private async Task<BitwardenCommandResult> LockBitwardenAsync()
    {
        var result = await RunBitwardenAsync(new[] { "lock" }, _bitwardenSession);
        if (result.ExitCode == 0)
            _bitwardenSession = null;
        return result;
    }
    
    // Default browser registration
    private void SetAsDefaultBrowser()
    {
        // Ensure registration is up to date (updates exe path, writes Capabilities + RegisteredApplications)
        // This is done via App so the logic is in one place
        App.OpenDefaultAppsForYCB();
    }
    
    private bool CheckIsDefaultBrowser()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            if (key != null)
            {
                var progId = key.GetValue("ProgId")?.ToString();
                return progId == "YCBUrl";
            }
        }
        catch { }
        return false;
    }

    private bool IsAiEnabledForProfile => _installAiEnabled && _settings.AiEnabled;

    private void ApplyAiVisibility()
    {
        var visible = IsAiEnabledForProfile;
        CopilotBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _copilotVisible = false;
            CopilotSidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            AiSidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }
    
    private void ApplySettingChange(string key, string value)
    {
        switch (key)
        {
            case "browser_theme":
                _isDarkMode = value != "light";
                _settings.DarkMode = _isDarkMode;
                ApplyTheme();
                SaveSettings();
                break;
                
            case "font_size":
                _settings.FontSize = value;
                SaveSettings();
                // Apply font size to all tabs
                var fontSize = value switch
                {
                    "small" => 0.85,
                    "large" => 1.15,
                    "larger" => 1.3,
                    _ => 1.0
                };
                foreach (var tab in _tabs)
                {
                    tab.WebView.ZoomFactor = fontSize * _zoomFactor;
                }
                break;
                
            case "incognito_ai_enabled":
                _settings.IncognitoAIEnabled = value == "true";
                SaveSettings();
                break;
                
            case "bookmarks_bar":
                _settings.BookmarksBarVisible = value == "on";
                SaveSettings();
                UpdateBookmarksBar();
                break;
                
            case "search_engine":
                _settings.SearchEngine = value;
                _searchEngine = value;
                UpdateUrlPlaceholder();
                SaveSettings();
                break;
                
            case "startup_mode":
                _settings.StartupMode = value;
                SaveSettings();
                break;

            case "ai_provider":
                _settings.AiProvider = NormalizeAiProvider(value);
                if (_copilotVisible)
                {
                    _ = Dispatcher.InvokeAsync(() => NavigateAiProvider());
                }
                SaveSettings();
                break;

            case "ai_enabled":
                _settings.AiEnabled = value != "off";
                ApplyAiVisibility();
                SaveSettings();
                break;

            case "ai_sidebar_width":
                if (double.TryParse(value, out var width))
                {
                    _aiSidebarWidth = Math.Max(240, Math.Min(width, 720));
                    _settings.AiSidebarWidth = _aiSidebarWidth;
                    if (_copilotVisible)
                    {
                        SidebarColumn.Width = new GridLength(_aiSidebarWidth);
                    }
                    SaveSettings();
                }
                break;

            case "ycb_model":
                // Legacy model setting is ignored now that YCB uses provider webpages.
                SaveSettings();
                break;

            case "telemetry_enabled":
                _settings.TelemetryEnabled = value == "true";
                ErrorReporter.IsEnabled = _settings.TelemetryEnabled;
                SaveSettings();
                break;

            case "ad_blocker_enabled":
                _settings.AdBlockerEnabled = value == "on";
                SaveSettings();
                _ = ApplyUBlockOriginStateAsync();
                UpdateAdBlockButton();
                break;

            case "home_page":
                _settings.HomePage = value;
                SaveSettings();
                break;

        }
    }
    
    private void UpdateBookmarksBar()
    {
        if (_settings.BookmarksBarVisible)
        {
            BookmarksBar.Visibility = Visibility.Visible;
            BookmarksBarRow.Height = new GridLength(32);
            LoadBookmarksBar();
        }
        else
        {
            BookmarksBar.Visibility = Visibility.Collapsed;
            BookmarksBarRow.Height = new GridLength(0);
        }
    }
    
    private void LoadBookmarksBar()
    {
        BookmarksBarItems.Children.Clear();
        var bookmarks = LoadBookmarks();
        
        foreach (var bookmark in bookmarks.Take(20)) // Show up to 20 bookmarks
        {
            var btn = new Button
            {
                Content = new TextBlock 
                { 
                    Text = bookmark.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 120
                },
                Tag = bookmark.Url,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = bookmark.Url
            };
            btn.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is string url)
                {
                    if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                    {
                        _tabs[_activeTabIndex].WebView.CoreWebView2.Navigate(url);
                    }
                }
            };
            BookmarksBarItems.Children.Add(btn);
        }
    }
    
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        base.OnClosing(e);
    }

    private static bool IsSearchPage(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLower();
            var path = uri.AbsolutePath.ToLower();
            var q    = uri.Query;
            return (host.Contains("google.")    && path.StartsWith("/search") && q.Contains("q=")) ||
                   (host.Contains("bing.com")   && path.StartsWith("/search")) ||
                   (host.Contains("duckduckgo.com") && q.Contains("q=")) ||
                   (host.Contains("search.yahoo.com")) ||
                   (host.Contains("ecosia.org") && path.StartsWith("/search"));
        }
        catch { return false; }
    }

}

// Data classes
public class BrowserTab
{
    public WebView2 WebView { get; set; } = null!;
    public Button TabButton { get; set; } = null!;
    public string Url { get; set; } = "";
    public string Title { get; set; } = "New Tab";
    public bool IsSuspended { get; set; }
    public Image? TabFavicon { get; set; }
    public Button? TabCloseBtn { get; set; }
    public TextBlock? TabTitle { get; set; }
}

public class Settings
{
    public bool DarkMode { get; set; } = true;
    public string? HomePage { get; set; } = "ycb://newtab";
    public List<string>? LastTabs { get; set; }
    public bool? IncognitoAIEnabled { get; set; } = false;
    public bool BookmarksBarVisible { get; set; } = false;
    public string SearchEngine { get; set; } = "google";
    public string FontSize { get; set; } = "medium";
    public string StartupMode { get; set; } = "newtab";
    public string YcbModel { get; set; } = "";
    public bool AiEnabled { get; set; } = true;
    public string AiProvider { get; set; } = "chatgpt";
    public double? AiSidebarWidth { get; set; } = 340;
    public string ProfileName { get; set; } = Environment.UserName;
    public string ProfileInitial { get; set; } = "";
    public string ProfileIcon { get; set; } = "";
    public string ProfileColor { get; set; } = "#5b9bf9";
    public List<ProfileItem> Profiles { get; set; } = new();
    public bool HasSeenGuide { get; set; } = false;
    public bool TelemetryEnabled { get; set; } = true;
    // Window position/state persistence
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string? WindowState { get; set; }
    public bool AdBlockerEnabled { get; set; } = true;
    public DateTime? HistoryClearedAt { get; set; }
}

public class LauncherState
{
    public string? ActiveProfileName { get; set; }
    public List<ProfileItem> Profiles { get; set; } = new();
}

public class ProfileItem
{
    public string Name { get; set; } = "";
    public string Initial { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "#5b9bf9";
}

public class ExtensionUiState
{
    public bool Disabled { get; set; }
    public bool Removed { get; set; }
    public string InstallPath { get; set; } = "";
}

public sealed record ExtensionPopupItem(string Id, string Name, bool Enabled, string PopupPath);

public class HistoryItem
{
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("visitedAt")]
    public DateTime Timestamp { get; set; }
}

public class DownloadItem
{
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("savePath")]
    public string SavePath { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "downloading";
    
    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}

public class BookmarkItem
{
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public class PasswordItem
{
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; set; } = "password";

    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string Label { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string Username { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("cardHolder")]
    public string CardHolder { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("cardNumber")]
    public string CardNumber { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("cardExpiry")]
    public string CardExpiry { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("cardBrand")]
    public string CardBrand { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("cardCode")]
    public string CardCode { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("firstName")]
    public string FirstName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("lastName")]
    public string LastName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("company")]
    public string Company { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("notes")]
    public string Notes { get; set; } = "";
}

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public enum OmniSuggestionKind
{
    Search,
    History,
    Site
}

public class OmniSuggestion
{
    public string Primary { get; set; } = "";
    public string Secondary { get; set; } = "";
    public string NavigateUrl { get; set; } = "";
    public OmniSuggestionKind Kind { get; set; } = OmniSuggestionKind.Search;
    public string FaviconUrl { get; set; } = "";
    public bool IsRemovable { get; set; }

    // Search magnifier icon
    private const string SearchPath = "M10.5 10.5 L14 14 M9 15 C12.3137 15 15 12.3137 15 9 C15 5.68629 12.3137 3 9 3 C5.68629 3 3 5.68629 3 9 C3 12.3137 5.68629 15 9 15 Z";
    // Clock/history icon
    private const string HistoryPath = "M8 2 C4.686 2 2 4.686 2 8 C2 11.314 4.686 14 8 14 C11.314 14 14 11.314 14 8 C14 4.686 11.314 2 8 2 Z M8 5 L8 8.5 L11 10";

    public string IconPath => Kind == OmniSuggestionKind.History ? HistoryPath : SearchPath;
    public string IconColor => Kind == OmniSuggestionKind.History
        ? (IsDark ? "#8ab4f8" : "#1a73e8")
        : (IsDark ? "#9aa0a6" : "#5f6368");
    public System.Windows.Visibility FaviconVisibility =>
        Kind == OmniSuggestionKind.Site && !string.IsNullOrWhiteSpace(FaviconUrl)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility VectorIconVisibility =>
        FaviconVisibility == System.Windows.Visibility.Visible
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    public System.Windows.Visibility RemoveVisibility =>
        IsRemovable ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Media.Brush IconBackBrush =>
        Kind == OmniSuggestionKind.Site
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? "#303134" : "#e8f0fe")!)
            : Brushes.Transparent;
    public FontWeight PrimaryWeight =>
        Kind == OmniSuggestionKind.Search ? FontWeights.SemiBold : FontWeights.Normal;

    // Static theme flag — updated by ApplyTheme() before populating suggestions
    public static bool IsDark { get; set; } = true;
    public static string ThemePrimary { get; set; } = "#e8eaed";
    public static string ThemeSecondary { get; set; } = "#9aa0a6";

    public string PrimaryColor => ThemePrimary;
    public string SecondaryColor => ThemeSecondary;

    public System.Windows.Visibility SecondaryVisibility =>
        string.IsNullOrEmpty(Secondary) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}
