using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfPath = System.Windows.Shapes.Path;
using IoPath = System.IO.Path;

namespace YCB;

public partial class MainWindow : Window
{
    private readonly List<BrowserTab> _tabs = new();
    private int _activeTabIndex = -1;
    private bool _isDarkMode = true;
    private bool _copilotVisible = false;
    private bool _isFullscreen = false;
    private readonly string _userDataFolder;
    private readonly string _incognitoUserDataFolder;
    private readonly string _settingsPath;
    private readonly string _historyPath;
    private readonly string _downloadsPath;
    private readonly string _bookmarksPath;
    private readonly string _passwordsPath;
    private readonly string _permissionsPath;
    private Settings _settings = new();
    private string _searchEngine = "google";
    private double _zoomFactor = 1.0;
    private readonly bool _isIncognito;
    private readonly List<ChatMessage> _chatHistory = new();
    private Process? _copilotProcess;
    private TextBlock? _currentResponseBlock;
    private string? _startupUrl;
    private readonly Dictionary<WebView2, string> _autofillShownForTab = new();
    private readonly Dictionary<WebView2, DateTime> _navStartTimes = new();
    private CoreWebView2Environment? _webViewEnvironment;
    private CoreWebView2Environment? _incognitoWebViewEnvironment;
    private string? _attachedImagePath;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;
    private WindowState _savedWindowState;
    
    public MainWindow() : this(false, null) { }

    public MainWindow(bool incognito) : this(incognito, null) { }

    public MainWindow(string? startupUrl) : this(false, startupUrl) { }

    public MainWindow(bool incognito, string? startupUrl = null)
    {
        InitializeComponent();
        _isIncognito = incognito;
        _startupUrl  = startupUrl;
        
        _userDataFolder = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YCB-Browser");
        _incognitoUserDataFolder = IoPath.Combine(IoPath.GetTempPath(), "YCB-Incognito-" + Guid.NewGuid().ToString("N"));
        _settingsPath = IoPath.Combine(_userDataFolder, "settings.json");
        _historyPath = IoPath.Combine(_userDataFolder, "history.json");
        _downloadsPath = IoPath.Combine(_userDataFolder, "downloads.json");
        _bookmarksPath = IoPath.Combine(_userDataFolder, "bookmarks.json");
        _passwordsPath = IoPath.Combine(_userDataFolder, "passwords.json");
        _permissionsPath = IoPath.Combine(_userDataFolder, "permissions.json");
        
        Directory.CreateDirectory(_userDataFolder);
        if (_isIncognito)
        {
            Directory.CreateDirectory(_incognitoUserDataFolder);
        }
        
        LoadSettings();
        
        Loaded += MainWindow_Loaded;
        LocationChanged += (_, _) => { if (!_isFullscreen && Top < 0) Top = 0; };
        StateChanged += MainWindow_StateChanged;
        KeyDown += MainWindow_KeyDown;
        SizeChanged += (_, _) => UpdateTabWidths();
        if (_isIncognito)
        {
            Closed += IncognitoWindow_Closed;
        }
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

        // Setup incognito mode
        if (_isIncognito)
        {
            Title = "YCB (Incognito)";
            IncognitoBarRow.Height = new GridLength(32);
            IncognitoBar.Visibility = Visibility.Visible;
            
            // Update incognito detail text based on AI setting
            bool aiEnabledInIncognito = _settings.IncognitoAIEnabled ?? false;
            IncognitoDetail.Text = aiEnabledInIncognito 
                ? "History, cookies and site data won't be saved."
                : "History, cookies and site data won't be saved. AI is disabled.";
        }
        
        ApplyTheme();
        ApplyAllSettings();
        
        // Restore tabs from last session or create new tab (incognito always starts fresh)
        if (!_isIncognito && _settings.StartupMode == "continue" && _settings.LastTabs?.Count > 0)
        {
            foreach (var url in _settings.LastTabs)
            {
                await CreateTab(url);
            }
        }
        else
        {
            await CreateTab(_settings.HomePage ?? "ycb://newtab");
        }

        // If launched with a URL argument, open it in a new tab
        if (!string.IsNullOrEmpty(_startupUrl))
        {
            await CreateTab(_startupUrl);
        }

        // Show guide on first launch
        if (!_isIncognito && !_settings.HasSeenGuide)
        {
            await CreateTab("ycb://guide");
            _settings.HasSeenGuide = true;
            SaveSettings();
        }
    }

    // Called by App.xaml.cs pipe server when another instance sends a URL
    public async void OpenUrl(string url)
    {
        BringToFront();
        await CreateTab(url);
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
        // Update maximize/restore button icon- using rectangles for proper rendering
        if (WindowState == WindowState.Maximized)
        {
            // Show restore icon (two overlapping rectangles)
            var canvas = new Canvas { Width = 10, Height = 10 };
            var rect1 = new Rectangle { Width = 7, Height = 7, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!), StrokeThickness = 1.2, Fill = Brushes.Transparent };
            var rect2 = new Rectangle { Width = 7, Height = 7, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!), StrokeThickness = 1.2, Fill = Brushes.Transparent };
            Canvas.SetLeft(rect1, 0);
            Canvas.SetTop(rect1, 3);
            Canvas.SetLeft(rect2, 3);
            Canvas.SetTop(rect2, 0);
            canvas.Children.Add(rect1);
            canvas.Children.Add(rect2);
            MaxRestoreBtn.Content = canvas;
        }
        else
        {
            // Show maximize icon (single rectangle)
            MaxRestoreBtn.Content = new Rectangle 
            { 
                Width = 8.5, Height = 8.5, 
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!), 
                StrokeThickness = 1.5, 
                Fill = Brushes.Transparent 
            };
        }
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

        if (_isFullscreen)
        {
            _savedWindowState = WindowState;

            TabStrip.Visibility = Visibility.Collapsed;
            Toolbar.Visibility  = Visibility.Collapsed;
            MainGrid.RowDefinitions[0].Height = new GridLength(0);
            MainGrid.RowDefinitions[1].Height = new GridLength(0);

            // WM_GETMINMAXINFO will constrain Maximized to full monitor (no taskbar gap, no overflow)
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            TabStrip.Visibility = Visibility.Visible;
            Toolbar.Visibility  = Visibility.Visible;
            MainGrid.RowDefinitions[0].Height = new GridLength(36);
            MainGrid.RowDefinitions[1].Height = new GridLength(46);

            WindowState = WindowState.Normal;
            if (_savedWindowState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
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
        
        _isDarkMode = _settings.DarkMode;
        _searchEngine = _settings.SearchEngine ?? "google";
        ErrorReporter.IsEnabled = _settings.TelemetryEnabled;
    }
    
    private void SaveSettings()
    {
        try
        {
            _settings.DarkMode = _isDarkMode;
            _settings.LastTabs = _tabs.Where(t => t.WebView?.Source != null)
                                       .Select(t => t.WebView!.Source!.ToString())
                                       .ToList();
            
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }
    
    private async System.Threading.Tasks.Task CreateTab(string url = "ycb://newtab")
    {
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
        TabsPanel.Children.Add(tabButton);
        WebViewContainer.Children.Add(webView);
        
        // Recompute tab widths now that count changed
        Dispatcher.InvokeAsync(UpdateTabWidths, System.Windows.Threading.DispatcherPriority.Loaded);
        
        var tab = new BrowserTab
        {
            WebView = webView,
            TabButton = tabButton,
            Url = url,
            Title = "New Tab"
        };
        _tabs.Add(tab);
        ErrorReporter.Track("TabOpen", new() { ["tabs"] = _tabs.Count });
        
        // Initialize WebView2 with appropriate data folder — reuse shared environment
        var dataFolder = _isIncognito ? _incognitoUserDataFolder : _userDataFolder;
        if (_isIncognito)
        {
            _incognitoWebViewEnvironment ??= await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await webView.EnsureCoreWebView2Async(_incognitoWebViewEnvironment);
        }
        else
        {
            _webViewEnvironment ??= await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await webView.EnsureCoreWebView2Async(_webViewEnvironment);
        }
        
        // Set default background color based on theme
        webView.DefaultBackgroundColor = _isDarkMode 
            ? System.Drawing.Color.FromArgb(255, 32, 33, 36)  // #202124
            : System.Drawing.Color.FromArgb(255, 255, 255, 255);  // white
        
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
        // Use the full window width minus fixed chrome (window controls ~138px, + button ~30px, padding)
        double available = Math.Max(0, ActualWidth - 168);
        double tabWidth = Math.Min(220, Math.Max(60, available / _tabs.Count));
        foreach (var tab in _tabs)
            tab.TabButton.Width = tabWidth;
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
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);
        
        // Close button
        var closeBtn = new Button
        {
            Width = 16,
            Height = 16,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = index,
            Opacity = 0
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
        
        // Show close button on hover
        button.MouseEnter += (s, e) => closeBtn.Opacity = 1;
        button.MouseLeave += (s, e) => { if (!IsActiveTab(index)) closeBtn.Opacity = 0; };
        
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
        
        webView.NavigationStarting += (s, e) =>
        {
            _navStartTimes[webView] = DateTime.UtcNow;
            _autofillShownForTab.Remove(webView);
            var idx = GetTabIndexForWebView(webView);
            if (idx == _activeTabIndex)
            {
                UrlBox.Text = GetDisplayUrl(e.Uri);
                UpdateUrlPlaceholder();
                UpdateSecurityIcon(e.Uri);
            }
        };
        
        webView.NavigationCompleted += (s, e) =>
        {
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
            {
                UpdateNavButtons();
                
                // Apply zoom
                webView.ZoomFactor = _zoomFactor;

                // Sync bookmark star for the active tab
                if (idx == _activeTabIndex)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        UrlBox.Text = GetDisplayUrl(webView.Source?.ToString());
                        UpdateUrlPlaceholder();
                        RefreshBookmarkStar();
                    });
                }

                // Inject password detection script on real websites
                if (e.IsSuccess)
                {
                    var src2 = webView.Source?.ToString() ?? "";
                    if (!src2.StartsWith("file:///") && !string.IsNullOrEmpty(src2))
                    {
                        _ = webView.ExecuteScriptAsync(PasswordContentScript);
                        // Track nav success — host only, no full URL
                        var navHost = GetDomain(src2);
                        var navMs   = _navStartTimes.TryGetValue(webView, out var t0)
                                        ? (int)(DateTime.UtcNow - t0).TotalMilliseconds : -1;
                        _navStartTimes.Remove(webView);
                        ErrorReporter.Track("NavOk", new() { ["host"] = navHost, ["ms"] = navMs });
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
                }
            }
        };
        
        // Enable DevTools console for password detection (all pages)
        webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled")
            .DevToolsProtocolEventReceived += async (s2, args) => await HandleWebConsole(webView, args);
        webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        
        webView.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            var idx = GetTabIndexForWebView(webView);
            if (idx >= 0 && idx < _tabs.Count)
            {
                var title = webView.CoreWebView2.DocumentTitle;
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
                        image.Source = bitmap;
                    }
                    catch { }
                }
            }
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
            var color = uri.Scheme == "https" ? "#81c995" : "#9aa0a6";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
            SecurityShield.Stroke = brush;
            SecurityCheck.Stroke = brush;
        }
        catch
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!);
            SecurityShield.Stroke = brush;
            SecurityCheck.Stroke = brush;
        }
    }
    
    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        
        // Update old tab styling
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].WebView.Visibility = Visibility.Collapsed;
            _tabs[_activeTabIndex].TabButton.Style = (Style)FindResource("TabStyle");
            
            // Update title color
            if (_tabs[_activeTabIndex].TabButton.Content is Grid oldGrid)
            {
                var oldTitle = oldGrid.Children.OfType<TextBlock>().FirstOrDefault();
                if (oldTitle != null)
                    oldTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!);
                
                var oldClose = oldGrid.Children.OfType<Button>().FirstOrDefault();
                if (oldClose != null)
                    oldClose.Opacity = 0;
            }
        }
        
        // Update new tab styling
        _activeTabIndex = index;
        _tabs[index].WebView.Visibility = Visibility.Visible;
        _tabs[index].TabButton.Style = (Style)FindResource("ActiveTabStyle");
        
        // Update title color
        if (_tabs[index].TabButton.Content is Grid grid)
        {
            var title = grid.Children.OfType<TextBlock>().FirstOrDefault();
            if (title != null)
                title.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!);
            
            var close = grid.Children.OfType<Button>().FirstOrDefault();
            if (close != null)
                close.Opacity = 1;
        }
        
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
        RefreshBookmarkStar();
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
        
        SwitchToTab(_activeTabIndex);
        UpdateTabWidths();
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
    
    private void Navigate(string input)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        
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
            // Use Google search
            url = $"https://www.google.com/search?q={Uri.EscapeDataString(url)}";
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
        
        // Get the path to the renderer folder
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDir = IoPath.GetDirectoryName(exePath) ?? "";
        var rendererPath = IoPath.Combine(exeDir, "renderer");
        
        string htmlFile;
        switch (pageName)
        {
            case "history":
                htmlFile = IoPath.Combine(rendererPath, "history.html");
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
            webView.CoreWebView2?.Navigate($"file:///{htmlFile.Replace("\\", "/")}");
            
            // Setup message handler for this internal page
            SetupInternalPageMessageHandler(webView, pageName);
        }
        else
        {
            // Fallback to generated HTML if file not found
            webView.CoreWebView2?.NavigateToString($"<h1>Page not found: {pageName}</h1><p>Expected at: {htmlFile}</p>");
        }
    }
    
    private void SetupInternalPageMessageHandler(WebView2 webView, string pageName)
    {
        // Remove any existing handlers first
        webView.CoreWebView2.WebMessageReceived -= InternalPage_WebMessageReceived;
        webView.CoreWebView2.WebMessageReceived += InternalPage_WebMessageReceived;
        
        // Setup console message handler for newtab/passwords pages
        webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived -= ConsoleMessage_Received;
        webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += ConsoleMessage_Received;
        webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        
        // Inject data after page loads
        webView.NavigationCompleted += async (s, e) =>
        {
            if (!e.IsSuccess) return;
            
            try
            {
                // Inject theme for all internal pages
                var themeScript = _isDarkMode ? "" : @"
                    document.body.style.background = '#ffffff';
                    document.body.style.color = '#202124';
                    document.querySelectorAll('.card, .greeting-bar, .bookmarks-section, .bm-chip').forEach(el => {
                        el.style.background = el.style.background.replace('#202124', '#f8f9fa').replace('#2d2e30', '#ffffff').replace('#3c4043', '#f1f3f4');
                        el.style.color = el.style.color.replace('#e8eaed', '#202124').replace('#bdc1c6', '#5f6368');
                    });
                ";
                if (!_isDarkMode)
                {
                    await webView.ExecuteScriptAsync(themeScript);
                }
                
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
                        // Inject Copilot info and default browser status
                        var copilotInfo = GetCopilotInfo();
                        var infoJson = JsonSerializer.Serialize(copilotInfo);
                        await webView.ExecuteScriptAsync($"window.setCopilotInfo && window.setCopilotInfo({infoJson})");
                        
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
                                // Update user info
                                var userEl = document.getElementById('copilot-user');
                                if (userEl && {infoJson}.authenticated) {{
                                    userEl.textContent = '@' + {infoJson}.username + ' (signed in)';
                                    userEl.className = 'row-desc success';
                                }}
                            }})();
                        ");
                        
                        // Inject current settings so the page shows persisted values
                        var settingsData = new {
                            bookmarks_bar = _settings.BookmarksBarVisible ? "on" : "off",
                            search_engine = _settings.SearchEngine ?? "google",
                            startup_mode = _settings.StartupMode ?? "newtab",
                            ycb_model = _settings.YcbModel ?? "gpt-4.1",
                            incognito_ai_enabled = (_settings.IncognitoAIEnabled ?? false).ToString().ToLower(),
                            browser_theme = _settings.DarkMode ? "dark" : "light",
                            telemetry_enabled = _settings.TelemetryEnabled.ToString().ToLower(),
                            user_id = ErrorReporter.UserId
                        };
                        var settingsDataJson = JsonSerializer.Serialize(settingsData);
                        await webView.ExecuteScriptAsync($"window.loadSettings && window.loadSettings({settingsDataJson})");
                        
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
                        // Inject bookmarks into newtab page
                        var bookmarks = LoadBookmarks();
                        var bookmarksJson = JsonSerializer.Serialize(bookmarks);
                        await webView.ExecuteScriptAsync($"window.setBookmarks && window.setBookmarks({bookmarksJson})");
                        break;
                        
                    case "passwords":
                        // Inject passwords
                        var passwords = LoadPasswordsDecrypted();
                        var passwordsJson = JsonSerializer.Serialize(passwords);
                        await webView.ExecuteScriptAsync($"window.setPasswords && window.setPasswords({passwordsJson})");
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
            if (message.StartsWith("__bookmarks__:"))
            {
                var parts = message.Substring(14).Split(new[] { ':' }, 2);
                var action = parts[0];
                var data = parts.Length > 1 ? parts[1] : "";
                
                switch (action)
                {
                    case "ADD":
                        var bookmarkData = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
                        if (bookmarkData != null)
                        {
                            AddBookmark(bookmarkData.GetValueOrDefault("url", ""), bookmarkData.GetValueOrDefault("label", ""));
                            // Update the newtab page
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
                        break;
                        
                    case "REMOVE":
                        if (int.TryParse(data, out var index))
                        {
                            RemoveBookmark(index);
                            // Update the newtab page
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
                        break;
                }
            }
            // Handle password messages
            else if (message.StartsWith("__passwords__:"))
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

            var message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.WebMessageAsJson);
            if (message == null || !message.TryGetValue("type", out var typeElement)) return;
            
            var type = typeElement.GetString();
            
            switch (type)
            {
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
                    break;
                    
                case "history:getAll":
                    if (sender is CoreWebView2 wv)
                    {
                        var history = LoadHistory();
                        var json = JsonSerializer.Serialize(history);
                        await wv.ExecuteScriptAsync($"window.loadHistory && window.loadHistory({json})");
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
            }
        }
        catch { }
    }
    
    private object GetCopilotInfo()
    {
        try
        {
            var configPath = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (config != null && config.TryGetValue("logged_in_users", out var users))
                {
                    var userList = users.Deserialize<List<Dictionary<string, string>>>();
                    if (userList?.Count > 0 && userList[0].TryGetValue("login", out var login))
                    {
                        return new { authenticated = true, username = login, cliPath = "copilot" };
                    }
                }
            }
        }
        catch { }
        return new { authenticated = false, username = "", cliPath = "copilot" };
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
    private const int GWL_STYLE      = -16;
    private const int WS_CAPTION     = 0x00C00000;
    private const int WS_THICKFRAME  = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int SW_MINIMIZE    = 6;
    private const int SW_MAXIMIZE    = 3;
    private const int SW_RESTORE     = 9;

    // OnSourceInitialized: SingleBorderWindow already has WS_CAPTION/THICKFRAME
    // DWM animates minimize/maximize/restore natively via ShowWindow P/Invoke
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WndProc);
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
    private const int WM_GETMINMAXINFO   = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO && _isFullscreen)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);
            mmi.ptMaxPosition.x = info.rcMonitor.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.y = info.rcMonitor.Top  - info.rcMonitor.Top;
            mmi.ptMaxSize.x     = info.rcMonitor.Right  - info.rcMonitor.Left;
            mmi.ptMaxSize.y     = info.rcMonitor.Bottom - info.rcMonitor.Top;
            mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
            mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
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
    
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        // ShowWindow goes straight to Win32 — DWM plays native maximize/restore animation
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, WindowState == WindowState.Maximized ? SW_RESTORE : SW_MAXIMIZE);
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
            _tabs[_activeTabIndex].WebView.CoreWebView2?.Reload();
        }
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
                var bgDark  = (Color)ColorConverter.ConvertFromString("#292a2d")!;
                var bgDeep  = (Color)ColorConverter.ConvertFromString("#202124")!;
                var border1 = (Color)ColorConverter.ConvertFromString("#3c4043")!;
                var textPri = (Color)ColorConverter.ConvertFromString("#e8eaed")!;
                var textSub = (Color)ColorConverter.ConvertFromString("#9aa0a6")!;

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
        var bgDark  = (Color)ColorConverter.ConvertFromString("#292a2d")!;
        var bgDeep  = (Color)ColorConverter.ConvertFromString("#202124")!;
        var border1 = (Color)ColorConverter.ConvertFromString("#3c4043")!;
        var textPri = (Color)ColorConverter.ConvertFromString("#e8eaed")!;
        var textSub = (Color)ColorConverter.ConvertFromString("#9aa0a6")!;

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
    }
    
    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(UrlBox.Text);
            // Remove focus from URL box
            if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            {
                _tabs[_activeTabIndex].WebView.Focus();
            }
        }
    }
    
    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateUrlPlaceholder();
    }
    
    private void UrlBox_GotFocus(object sender, RoutedEventArgs e)
    {
        OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3c4043")!);
        UrlPlaceholder.Visibility = Visibility.Collapsed;
        
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
        // Don't process lost focus when focus moved to BookmarkBtn — it causes star icon flicker
        if (BookmarkBtn.IsKeyboardFocused || BookmarkBtn.IsFocused) return;

        OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#292b2f")!);
        
        // Show display URL when losing focus
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var actualUrl = _tabs[_activeTabIndex].WebView?.Source?.ToString() ?? _tabs[_activeTabIndex].Url;
            UrlBox.Text = GetDisplayUrl(actualUrl);
        }
        UpdateUrlPlaceholder();
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
            BookmarkStarPath.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!);
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
        
        // Also hide file:// paths for internal renderer pages — return empty string
        if (url.StartsWith("file:///") &&
            (url.Contains("/renderer/") || url.Contains("\\renderer\\")))
        {
            return "";
        }
        
        return url;
    }
    
    private string GetSystemPageName(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("ycb://"))
        {
            return url switch
            {
                "ycb://newtab"    => "",
                "ycb://settings"  => "Settings",
                "ycb://history"   => "History",
                "ycb://downloads" => "Downloads",
                "ycb://passwords" => "Password Manager",
                "ycb://bookmarks" => "Bookmarks",
                "ycb://guide"     => "Help & Guide",
                _ => ""
            };
        }
        if (url.StartsWith("file:///"))
        {
            if (url.Contains("newtab.html"))    return "";
            if (url.Contains("settings.html"))  return "Settings";
            if (url.Contains("history.html"))   return "History";
            if (url.Contains("downloads.html")) return "Downloads";
            if (url.Contains("passwords.html")) return "Password Manager";
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
                UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlBox.Text) ? Visibility.Visible : Visibility.Collapsed;
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
        UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlBox.Text) ? Visibility.Visible : Visibility.Collapsed;
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

    private void Copilot_Click(object sender, RoutedEventArgs e)
    {
        // Block Copilot in incognito mode unless setting enabled
        if (_isIncognito && !(_settings.IncognitoAIEnabled ?? false))
        {
            MessageBox.Show("AI/Copilot is disabled in Incognito mode.\nYou can enable it in Settings > Privacy > Allow AI in Incognito.", 
                "Incognito Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        
        _copilotVisible = !_copilotVisible;
        CopilotSidebar.Visibility = _copilotVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _copilotVisible ? new GridLength(340) : new GridLength(0);
    }
    
    private void CloseCopilot_Click(object sender, RoutedEventArgs e)
    {
        _copilotVisible = false;
        CopilotSidebar.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
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
        
        // Find copilot.exe
        var copilotExe = FindCopilotExe();
        if (string.IsNullOrEmpty(copilotExe))
        {
            AddCopilotMessage("Copilot CLI not found. Please install GitHub Copilot CLI.", false);
            CopilotInput.IsEnabled = true;
            return;
        }
        
        // Build prompt
        var prompt = "You are a helpful browser assistant built into YCB. ";
        if (!string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank")
        {
            prompt += $"The user is currently viewing: {currentUrl}\n";
        }
        prompt += "IMPORTANT: If the user asks you to open, navigate to, or visit a website, include [OPEN_URL: https://example.com] in your response (with the full URL).\n\n";
        
        // Add recent history
        var recent = _chatHistory.TakeLast(6).ToList();
        if (recent.Count > 0)
        {
            prompt += "Previous conversation:\n";
            foreach (var h in recent)
            {
                var role = h.Role == "user" ? "User" : "Assistant";
                prompt += $"{role}: {h.Content}\n";
            }
            prompt += "\n";
        }
        
        if (imagePath != null)
        {
            var userText = string.IsNullOrEmpty(message) ? "Describe everything you see in this image." : message;
            prompt += $"CRITICAL INSTRUCTION: You have vision capabilities. You MUST use your read tool to open and analyze the image file below. Do NOT say you cannot view images or render images - you can and must do this. Read the file, look at it, and answer the question.\n\nImage file path: {imagePath}\n\nUser question: {userText}";
        }
        else
        {
            prompt += $"User: {message}";
        }
        
        // Create response message placeholder
        var responseBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#24263a")!),
            CornerRadius = new CornerRadius(14, 14, 14, 3),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 5, 40, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 280
        };
        
        _currentResponseBlock = new TextBlock
        {
            Text = "Thinking...",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        responseBorder.Child = _currentResponseBlock;
        MessagesPanel.Children.Add(responseBorder);
        CopilotMessages.ScrollToEnd();
        
        // Start copilot process
        try
        {
            var extraArgs = imagePath != null ? "--allow-all-paths " : "";
            var startInfo = new ProcessStartInfo
            {
                FileName = copilotExe,
                Arguments = $"{extraArgs}-p \"{prompt.Replace("\"", "\\\"")}\" --model {_settings.YcbModel ?? "gpt-4.1"} -s --no-ask-user --stream on",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            _copilotProcess = new Process { StartInfo = startInfo };
            var fullResponse = new System.Text.StringBuilder();
            
            _copilotProcess.OutputDataReceived += (s, args) =>
            {
                if (args.Data != null)
                {
                    fullResponse.Append(args.Data);
                    Dispatcher.Invoke(() =>
                    {
                        if (_currentResponseBlock != null)
                        {
                            _currentResponseBlock.Text = fullResponse.ToString();
                            CopilotMessages.ScrollToEnd();
                        }
                    });
                }
            };
            
            _copilotProcess.ErrorDataReceived += (s, args) =>
            {
                if (args.Data != null && args.Data.ToLower().Contains("error"))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_currentResponseBlock != null)
                        {
                            _currentResponseBlock.Text = $"Error: {args.Data}";
                            _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
                        }
                    });
                }
            };
            
            _copilotProcess.Exited += (s, args) =>
            {
                Dispatcher.Invoke(() =>
                {
                    CopilotInput.IsEnabled = true;
                    var response = fullResponse.ToString();
                    if (!string.IsNullOrWhiteSpace(response))
                    {
                        _chatHistory.Add(new ChatMessage { Role = "assistant", Content = response });
                        
                        // Check for [OPEN_URL: ...] commands
                        var urlMatches = System.Text.RegularExpressions.Regex.Matches(response, @"\[OPEN_URL:\s*(https?://[^\]]+)\]");
                        foreach (System.Text.RegularExpressions.Match match in urlMatches)
                        {
                            var urlToOpen = match.Groups[1].Value.Trim();
                            _ = CreateTab(urlToOpen);
                        }
                    }
                    _copilotProcess = null;
                });
            };
            
            _copilotProcess.EnableRaisingEvents = true;
            _copilotProcess.Start();
            _copilotProcess.BeginOutputReadLine();
            _copilotProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _currentResponseBlock!.Text = $"Failed to start Copilot: {ex.Message}";
            _currentResponseBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f28b82")!);
            CopilotInput.IsEnabled = true;
        }
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
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isUser ? "#1e3a5f" : "#24263a")!),
            CornerRadius = new CornerRadius(isUser ? 14 : 14, isUser ? 14 : 14, isUser ? 3 : 14, isUser ? 14 : 3),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(isUser ? 40 : 0, 5, isUser ? 0 : 40, 5),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 280
        };
        
        border.Child = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isUser ? "#cce3ff" : "#e8eaed")!),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        
        MessagesPanel.Children.Add(border);
        CopilotMessages.ScrollToEnd();
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
    
    private async void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://settings");
    }
    
    private async void MenuPasswords_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        await CreateTab("ycb://passwords");
    }
    
    private void MenuSupport_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _ = CreateTab("ycb://support");
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
        if (_isDarkMode)
        {
            // Dark theme colors
            TabStrip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            Toolbar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35363a")!);
            OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#292b2f")!);
            UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!);
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            if (themeText != null) themeText.Text = "Light Mode";
        }
        else
        {
            // Light theme colors
            TabStrip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f1f3f4")!);
            Toolbar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff")!);
            OmniboxBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!);
            UrlBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f1f3f4")!);
            if (themeText != null) themeText.Text = "Dark Mode";
        }
        
        // Update all tab title colors based on theme
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].TabButton.Content is Grid grid)
            {
                var title = grid.Children.OfType<TextBlock>().FirstOrDefault();
                if (title != null)
                {
                    title.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        i == _activeTabIndex ? (_isDarkMode ? "#e8eaed" : "#202124") : (_isDarkMode ? "#9aa0a6" : "#5f6368"))!);
                }
            }
            
            // Update WebView background color
            _tabs[i].WebView.DefaultBackgroundColor = _isDarkMode 
                ? System.Drawing.Color.FromArgb(255, 32, 33, 36)  // #202124
                : System.Drawing.Color.FromArgb(255, 255, 255, 255);  // white
        }
    }
    
    // Download shelf
    private void ShowDownloadShelf(DownloadItem item)
    {
        DownloadShelf.Visibility = Visibility.Visible;
        DownloadShelfRow.Height = new GridLength(68);
        
        var itemBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3c3d41")!),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 190,
            MaxWidth = 260,
            Tag = item.FilePath
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        // File icon
        var iconPath = new WpfPath
        {
            Data = Geometry.Parse("M4 2h8l4 4v12a2 2 0 01-2 2H4a2 2 0 01-2-2V4a2 2 0 012-2z"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8ab4f8")!),
            StrokeThickness = 1.5,
            Width = 20,
            Height = 24,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(iconPath, 0);
        grid.Children.Add(iconPath);
        
        // Info
        var infoStack = new StackPanel();
        var nameBlock = new TextBlock
        {
            Text = item.Filename,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var statusBlock = new TextBlock
        {
            Text = item.Status,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        };
        infoStack.Children.Add(nameBlock);
        infoStack.Children.Add(statusBlock);
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);
        
        itemBorder.Child = grid;
        itemBorder.MouseLeftButtonUp += (s, e) =>
        {
            if (File.Exists(item.FilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
            }
        };
        
        DownloadItems.Children.Add(itemBorder);
    }
    
    private void UpdateDownloadItem(DownloadItem item)
    {
        // Update status in the shelf
        foreach (Border border in DownloadItems.Children.OfType<Border>())
        {
            if (border.Tag?.ToString() == item.FilePath && border.Child is Grid grid)
            {
                var infoStack = grid.Children.OfType<StackPanel>().FirstOrDefault();
                if (infoStack != null)
                {
                    var statusBlock = infoStack.Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
                    if (statusBlock != null)
                    {
                        statusBlock.Text = item.Status;
                    }
                }
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
    
    // ─── PASSWORD CONTENT SCRIPT ────────────────────────────────────────
    private const string PasswordContentScript = @"
(function() {
    if (window.__ycbPwInjected) return;
    window.__ycbPwInjected = true;
    var __ycbAutoFillSent = false;

    // On form submit, capture credentials
    document.addEventListener('submit', function(e) {
        var form = e.target;
        var pwField = form.querySelector('input[type=""password""]');
        if (!pwField || !pwField.value) return;
        var q = 'input[type=""email""],input[type=""text""],input[autocomplete~=""username""],input[name*=""user"" i],input[name*=""login"" i],input[name*=""email"" i],input[id*=""user"" i],input[id*=""email"" i]';
        var userField = form.querySelector(q);
        var username = userField ? userField.value : '';
        console.log('__passwords__:SAVE_PROMPT:' + encodeURIComponent(window.location.href) + '|' + encodeURIComponent(username) + '|' + encodeURIComponent(pwField.value));
    }, true);

    // Check for password fields — only send once per page
    function checkPwFields() {
        if (!__ycbAutoFillSent && document.querySelector('input[type=""password""]')) {
            __ycbAutoFillSent = true;
            console.log('__passwords__:AUTOFILL_CHECK:' + encodeURIComponent(window.location.hostname));
        }
    }
    checkPwFields();
    setTimeout(checkPwFields, 1000);
    setTimeout(checkPwFields, 2500);
})();
";

    private static string BuildAutofillScript(string entriesJson)
    {
        return $@"
(function(entries) {{
    if (!entries || !entries.length) return;
    var pwFields = Array.from(document.querySelectorAll('input[type=""password""]'));
    if (!pwFields.length) return;
    var entry = entries[0];

    // Use native setter so React/Vue/Angular controlled inputs update correctly
    function fillField(el, value) {{
        try {{
            var proto = el.constructor && el.constructor.prototype || window.HTMLInputElement.prototype;
            var desc = Object.getOwnPropertyDescriptor(proto, 'value') ||
                       Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
            if (desc && desc.set) desc.set.call(el, value);
            else el.value = value;
        }} catch(err) {{ el.value = value; }}
        el.dispatchEvent(new Event('input',  {{bubbles:true, cancelable:true}}));
        el.dispatchEvent(new Event('change', {{bubbles:true, cancelable:true}}));
        el.dispatchEvent(new KeyboardEvent('keydown', {{bubbles:true}}));
        el.dispatchEvent(new KeyboardEvent('keyup',   {{bubbles:true}}));
    }}

    pwFields.forEach(function(pwField) {{
        var form = pwField.closest('form') || document;
        var q = 'input[type=""email""],input[type=""text""],input[autocomplete~=""username""],input[name*=""user"" i],input[name*=""login"" i],input[name*=""email"" i],input[id*=""user"" i],input[id*=""email"" i]';
        var uf = form.querySelector(q);
        if (uf) fillField(uf, entry.username || '');
        fillField(pwField, entry.password || '');
    }});
}})({entriesJson});
";
    }

    private async System.Threading.Tasks.Task HandleWebConsole(WebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var json = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (!json.RootElement.TryGetProperty("args", out var argsEl)) return;
            if (argsEl.ValueKind != JsonValueKind.Array || argsEl.GetArrayLength() == 0) return;
            if (!argsEl[0].TryGetProperty("value", out var valueEl)) return;
            var message = valueEl.GetString();
            if (message == null) return;

            if (message.StartsWith("__passwords__:SAVE_PROMPT:"))
            {
                var data = message.Substring("__passwords__:SAVE_PROMPT:".Length);
                var parts = data.Split('|');
                if (parts.Length >= 3 && !_isIncognito)
                {
                    var url  = Uri.UnescapeDataString(parts[0]);
                    var user = Uri.UnescapeDataString(parts[1]);
                    var pass = Uri.UnescapeDataString(parts[2]);
                    var domain = GetDomain(url);
                    var existing = LoadPasswords().FirstOrDefault(p => GetDomain(p.Url) == domain && p.Username == user);
                    if (existing == null)
                        await Dispatcher.InvokeAsync(() => ShowSavePasswordPopup(url, user, pass));
                }
            }
            else if (message.StartsWith("__passwords__:AUTOFILL_CHECK:"))
            {
                var domain = Uri.UnescapeDataString(message.Substring("__passwords__:AUTOFILL_CHECK:".Length));
                var passwords = LoadPasswordsDecrypted();
                var matches = passwords.Where(p => GetDomain(p.Url) == domain).ToList();
                if (matches.Any() && !_isIncognito)
                {
                    // Only show prompt once per navigation per tab
                    bool alreadyShown = _autofillShownForTab.TryGetValue(webView, out var shownDomain) && shownDomain == domain;
                    if (!alreadyShown)
                    {
                        _autofillShownForTab[webView] = domain;
                        var entriesJson = JsonSerializer.Serialize(matches);
                        var firstUser = matches[0].Username ?? "";
                        await Dispatcher.InvokeAsync(() => ShowAutofillPopup(webView, entriesJson, domain, firstUser));
                    }
                }
            }
        }
        catch { }
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
            Background       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#292a2d")!),
            CornerRadius     = new CornerRadius(8),
            BorderBrush      = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3c4043")!),
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
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8eaed")!),
            FontSize = 13, FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(subtitle))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9aa0a6")!),
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
    }

    private static string GetDomain(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return url; }
    }
    private void AddToHistory(string? url, string? title)
    {
        // Don't save history in incognito mode
        if (_isIncognito) return;
        if (string.IsNullOrEmpty(url) || url.StartsWith("ycb://")) return;
        
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
                return JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new List<HistoryItem>();
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
        try
        {
            if (File.Exists(_historyPath))
            {
                File.Delete(_historyPath);
            }
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
                    if (!item.Password.StartsWith("DPAPI:"))
                    {
                        item.Password = EncryptPassword(item.Password);
                        migrated = true;
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
            Password = DecryptPassword(p.Password)
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
                existing.Password = encrypted;
                existing.Url = url;
                ErrorReporter.Track("PwSaved", new() { ["host"] = domain, ["upd"] = true });
            }
            else
            {
                passwords.Add(new PasswordItem
                {
                    Key = $"{domain}_{username}",
                    Url = url,
                    Username = username,
                    Password = encrypted
                });
                ErrorReporter.Track("PwSaved", new() { ["host"] = domain, ["upd"] = false });
            }
            var json = JsonSerializer.Serialize(passwords, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_passwordsPath, json);
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
    
    // Default browser registration
    private void SetAsDefaultBrowser()
    {
        try
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (exePath.EndsWith(".dll"))
            {
                exePath = exePath.Replace(".dll", ".exe");
            }
            
            var cmdStr = $"\"{exePath}\" \"%1\"";
            
            // Register URL handler
            var registryKeys = new (string key, string name, string value)[]
            {
                (@"HKEY_CURRENT_USER\Software\Classes\YCBUrl", "", "YCB URL"),
                (@"HKEY_CURRENT_USER\Software\Classes\YCBUrl", "URL Protocol", ""),
                (@"HKEY_CURRENT_USER\Software\Classes\YCBUrl\shell\open\command", "", cmdStr),
                (@"HKEY_CURRENT_USER\Software\Classes\YCBHtml", "", "YCB HTML Document"),
                (@"HKEY_CURRENT_USER\Software\Classes\YCBHtml\shell\open\command", "", cmdStr),
                (@"HKEY_CURRENT_USER\Software\YCB\Capabilities", "ApplicationName", "YCB Browser"),
                (@"HKEY_CURRENT_USER\Software\YCB\Capabilities", "ApplicationDescription", "YCB Browser with WebView2"),
                (@"HKEY_CURRENT_USER\Software\YCB\Capabilities\URLAssociations", "http", "YCBUrl"),
                (@"HKEY_CURRENT_USER\Software\YCB\Capabilities\URLAssociations", "https", "YCBUrl"),
                (@"HKEY_CURRENT_USER\Software\YCB\Capabilities\FileAssociations", ".htm", "YCBHtml"),
                (@"HKEY_CURRENT_USER\Software\YCB\Capabilities\FileAssociations", ".html", "YCBHtml"),
                (@"HKEY_CURRENT_USER\Software\RegisteredApplications", "YCB", @"Software\YCB\Capabilities"),
            };
            
            foreach (var (key, name, value) in registryKeys)
            {
                try
                {
                    Microsoft.Win32.Registry.SetValue(key, name, value);
                }
                catch { }
            }
            
            // Open Windows Default Apps settings
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch { }
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
                
            case "ycb_model":
                _settings.YcbModel = value;
                SaveSettings();
                break;

            case "telemetry_enabled":
                _settings.TelemetryEnabled = value == "true";
                ErrorReporter.IsEnabled = _settings.TelemetryEnabled;
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
}

// Data classes
public class BrowserTab
{
    public WebView2 WebView { get; set; } = null!;
    public Button TabButton { get; set; } = null!;
    public string Url { get; set; } = "";
    public string Title { get; set; } = "New Tab";
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
    public string YcbModel { get; set; } = "gpt-4.1";
    public bool HasSeenGuide { get; set; } = false;
    public bool TelemetryEnabled { get; set; } = true;
}

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
    
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string Username { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}
