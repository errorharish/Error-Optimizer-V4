using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using ErrorOptimizerPrototypeHost.Services;
using DrawingColor = System.Drawing.Color;

namespace ErrorOptimizerPrototypeHost;

public partial class MainWindow : Window
{
    private const string VirtualHostName = "appassets.local";
    private const string InitialUrl = "https://appassets.local/index.html";
    private const string EmbeddedArchiveName = "ERROR_OPTIMIZER_PROTOTYPE.frontend.zip";
    private const double TargetWindowWidth = 1280;
    private const double TargetWindowHeight = 820;

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;
    private const int VK_LBUTTON = 0x01;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_CLOAK = 13;

    // COLORREF is 0x00BBGGRR: #1A2544 -> 0x0044251A, #050811 -> 0x00110805
    private const int LovableBorderColorRef = 0x0044251A;
    private const int LovableBackgroundColorRef = 0x00110805;

    private bool _initialized;
    private bool _focusedOnce;
    private bool _useRegionFallback;
    private bool _windowCloaked;
    private bool _windowRevealed;
    private uint _wmTaskbarCreated;
    private System.Windows.Threading.DispatcherTimer? _startupWatchdog;
    private readonly OperationsBridgeHandler _bridge;

    public MainWindow()
    {
        InitializeComponent();
        InitializeWindowIcon();

        // Support headless/minimized launch flags (--startup-background / --minimized) and record active exe path
        try
        {
            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
            {
                var s = BiosOptimizer.Core.Implementations.Config.UnifiedSettingsStore.LoadSafe();
                if (!string.Equals(s.HostExecutablePath, procPath, StringComparison.OrdinalIgnoreCase))
                {
                    s.HostExecutablePath = procPath;
                    BiosOptimizer.Core.Implementations.Config.UnifiedSettingsStore.Save(s);
                }
            }

            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.Equals("--startup-background", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                {
                    WindowState = WindowState.Minimized;
                    break;
                }
            }
        }
        catch { }

        _bridge = new OperationsBridgeHandler(this, PostWebMessageJson);
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private void PostWebMessageJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return;

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                AppWebView.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch
            {
                // Ignore WebView teardown races during window close.
            }
        });
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        Rect workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            double clampedW = Math.Min(TargetWindowWidth, Math.Max(960, workArea.Width - 24));
            double clampedH = Math.Min(TargetWindowHeight, Math.Max(600, workArea.Height - 24));
            if (Math.Abs(clampedW - Width) > 0.5 || Math.Abs(clampedH - Height) > 0.5)
            {
                Width = clampedW;
                Height = clampedH;
                Left = workArea.Left + (workArea.Width - clampedW) / 2.0;
                Top = workArea.Top + (workArea.Height - clampedH) / 2.0;
            }
        }

        ConfigureDwmWindowAppearance();

        try
        {
            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                HwndSource? source = HwndSource.FromHwnd(helper.Handle);
                source?.AddHook(WndProc);
            }
        }
        catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
        }
        catch { }

        // Cloak the window upon initial HWND creation so Windows DWM prevents any blank/black frame
        // from showing on screen while WebView2 environment and frontend shell are initializing.
        CloakWindow();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
        {
            HostBackgroundSupervisor.Instance.OnTaskbarCreated();
        }
        return IntPtr.Zero;
    }

    private void CloakWindow()
    {
        if (_windowCloaked || _windowRevealed)
            return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int cloak = 1;
        int res = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        if (res == 0)
        {
            _windowCloaked = true;
        }
    }

    public void RevealWindow()
    {
        if (_windowRevealed)
            return;
        _windowRevealed = true;

        if (_startupWatchdog != null)
        {
            _startupWatchdog.Stop();
            _startupWatchdog = null;
        }

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && _windowCloaked)
                {
                    int uncloak = 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref uncloak, sizeof(int));
                    _windowCloaked = false;
                }

                if (WindowState != WindowState.Minimized)
                {
                    Activate();
                    Focus();
                    AppWebView.Focus();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[REVEAL WINDOW ERROR] {ex}");
            }
        });
    }

    private void ConfigureDwmWindowAppearance()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int useDark = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
        }

        int captionColor = LovableBackgroundColorRef;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

        int borderColor = LovableBorderColorRef;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

        int cornerPreference = DWMWCP_ROUND;
        int cornerResult = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        // Windows 11 DWM natively rounds and anti-aliases both the window and child WebView2 HWND.
        // Fall back to a DPI-scaled region only on OS versions without DWMWA_WINDOW_CORNER_PREFERENCE.
        _useRegionFallback = cornerResult != 0;
        if (_useRegionFallback)
            UpdateFallbackWindowRegion();
    }

    private void UpdateFallbackWindowRegion()
    {
        if (!_useRegionFallback || WindowState == WindowState.Maximized)
            return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int widthPx = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int heightPx = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        int ellipsePx = Math.Max(2, (int)Math.Round(24 * dpi.DpiScaleX));

        IntPtr hRgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, ellipsePx, ellipsePx);
        if (hRgn != IntPtr.Zero && SetWindowRgn(hwnd, hRgn, true) == 0)
        {
            DeleteObject(hRgn);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_useRegionFallback)
            UpdateFallbackWindowRegion();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_useRegionFallback)
            UpdateFallbackWindowRegion();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && _useRegionFallback)
            UpdateFallbackWindowRegion();

        if (WindowState == WindowState.Minimized)
        {
            try
            {
                var settings = BiosOptimizer.Core.Implementations.Config.UnifiedSettingsStore.LoadSafe();
                if (settings.RunInSystemTray)
                {
                    Hide();
                }
            }
            catch { }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeFrontendAsync();
    }

    private async Task InitializeFrontendAsync()
    {
        RecoveryOverlay.Visibility = Visibility.Collapsed;
        AppWebView.Visibility = Visibility.Visible;

        try
        {
            string frontendDir = ResolveFrontendDirectory();

            if (!_initialized)
            {
                string userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ErrorOptimizerPrototype",
                    "WebView2Data");
                Directory.CreateDirectory(userDataDir);

                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataDir);

                AppWebView.DefaultBackgroundColor = DrawingColor.FromArgb(255, 5, 8, 17);
                AppWebView.ZoomFactor = 1.0;
                await AppWebView.EnsureCoreWebView2Async(environment);

                var core = AppWebView.CoreWebView2;
                core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsBuiltInErrorPageEnabled = false;

                core.SetVirtualHostNameToFolderMapping(
                    VirtualHostName,
                    frontendDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                core.WebMessageReceived += Core_WebMessageReceived;
                core.NavigationStarting += Core_NavigationStarting;
                core.NavigationCompleted += Core_NavigationCompleted;
                core.ProcessFailed += (s, args) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ShowRecovery("WebView2 Render Process Terminated",
                            $"Process failed with reason: {args.ProcessFailedKind}. Click Retry to re-initialize.");
                    });
                };

                await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function() {
    window.addEventListener('error', function(e) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({
                    action: 'frontend:error',
                    message: (e.error && e.error.message) || e.message || 'Script error',
                    stack: (e.error && e.error.stack) || (e.filename + ':' + e.lineno)
                }));
            }
        } catch(_) {}
    });
    window.addEventListener('unhandledrejection', function(e) {
        try {
            if (window.chrome && window.chrome.webview) {
                var msg = (e.reason && e.reason.message) || String(e.reason) || 'Unhandled promise rejection';
                window.chrome.webview.postMessage(JSON.stringify({
                    action: 'frontend:error',
                    message: msg,
                    stack: (e.reason && e.reason.stack) || ''
                }));
            }
        } catch(_) {}
    });
})();
");

                _initialized = true;
            }

            // Start safety watchdog: reveal window after timeout even if frontend ready message is delayed
            _startupWatchdog?.Stop();
            _startupWatchdog = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _startupWatchdog.Tick += (s, args) =>
            {
                _startupWatchdog?.Stop();
                RevealWindow();
            };
            _startupWatchdog.Start();

            AppWebView.CoreWebView2.Navigate(InitialUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowRecovery(
                "Microsoft Edge WebView2 Runtime not found",
                "Please install the Microsoft Edge WebView2 Evergreen Runtime on Windows to run ERROR OPTIMIZER V4.");
        }
        catch (Exception ex)
        {
            ShowRecovery(
                "Unable to load prototype assets",
                $"Initialization failed cleanly without connecting to any system service.\n\nDetails: {ex.Message}");
        }
    }

    private static string ResolveFrontendDirectory()
    {
        string adjacentDir = Path.Combine(AppContext.BaseDirectory, "frontend");
        if (File.Exists(Path.Combine(adjacentDir, "index.html")) &&
            Directory.Exists(Path.Combine(adjacentDir, "assets")))
        {
            return adjacentDir;
        }

        using Stream? archiveStream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedArchiveName);

        if (archiveStream == null)
        {
            throw new DirectoryNotFoundException(
                $"Bundled frontend directory not found at '{adjacentDir}'.");
        }

        string hashHex;
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(archiveStream);
            hashHex = Convert.ToHexString(hash)[..16];
        }

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ErrorOptimizerPrototype",
            "frontend-cache",
            hashHex);

        string cachedIndex = Path.Combine(cacheDir, "index.html");
        string cachedAssets = Path.Combine(cacheDir, "assets");
        if (File.Exists(cachedIndex) && Directory.Exists(cachedAssets))
            return cacheDir;

        string stagingDir = cacheDir + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(stagingDir);

        try
        {
            archiveStream.Position = 0;
            using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(stagingDir, overwriteFiles: true);
            }

            if (Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, recursive: true); } catch { }
            }

            if (!Directory.Exists(cacheDir))
            {
                Directory.Move(stagingDir, cacheDir);
            }
            else
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }

            return cacheDir;
        }
        catch
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
            throw;
        }
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            raw = e.WebMessageAsJson;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("action", out var actionProp))
                return;

            string? action = actionProp.GetString();
            switch (action)
            {
                case "app:ready":
                    RevealWindow();
                    break;
                case "window:minimize":
                    try
                    {
                        var settings = BiosOptimizer.Core.Implementations.Config.UnifiedSettingsStore.LoadSafe();
                        if (settings.RunInSystemTray)
                        {
                            WindowState = WindowState.Minimized;
                            Hide();
                        }
                        else
                        {
                            WindowState = WindowState.Minimized;
                        }
                    }
                    catch
                    {
                        WindowState = WindowState.Minimized;
                    }
                    break;
                case "window:close":
                    Close();
                    break;
                case "window:drag":
                    BeginWindowDrag();
                    break;
                case "frontend:error":
                    {
                        string msg = doc.RootElement.TryGetProperty("message", out var mProp) ? mProp.GetString() ?? "Unknown error" : "Unknown error";
                        string stk = doc.RootElement.TryGetProperty("stack", out var sProp) ? sProp.GetString() ?? "" : "";
                        System.Diagnostics.Debug.WriteLine($"[FRONTEND SCRIPT ERROR] {msg} | {stk}");
                        Dispatcher.BeginInvoke(() =>
                        {
                            ShowRecovery("Interface Script Exception", $"A frontend JavaScript exception occurred:\n\n{msg}\n\n{stk}");
                        });
                    }
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(action) && _bridge.CanHandleWindowlessAction(action))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _bridge.HandleMessageAsync(raw).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[HOST BRIDGE ERROR] {ex}");
                            }
                        });
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from WebView2.
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            return;

        bool isLocalAppHost =
            uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals(VirtualHostName, StringComparison.OrdinalIgnoreCase);

        bool isInternalScheme =
            uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase);

        if (isLocalAppHost || isInternalScheme)
            return;

        e.Cancel = true;

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Ignore browser launch failures for optional external links.
            }
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            if (!_focusedOnce)
            {
                _focusedOnce = true;
                AppWebView.Focus();
            }

            // Fallback safety: if app:ready message was delayed, reveal window shortly after navigation
            var navFallback = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            navFallback.Tick += (s, args) =>
            {
                navFallback.Stop();
                RevealWindow();
            };
            navFallback.Start();
            return;
        }

        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            return;

        ShowRecovery(
            "Interface failed to finish loading",
            $"WebView2 reported navigation error '{e.WebErrorStatus}' while opening local prototype assets.");
    }

    private void ShowRecovery(string title, string message)
    {
        RevealWindow();
        RecoveryTitle.Text = title;
        RecoveryMessage.Text = message;
        AppWebView.Visibility = Visibility.Collapsed;
        RecoveryOverlay.Visibility = Visibility.Visible;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        _windowRevealed = false;
        CloakWindow();
        await InitializeFrontendAsync();
    }

    private void CloseRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BeginWindowDrag()
    {
        if (WindowState == WindowState.Maximized)
            return;

        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    public void RestoreFromBackground()
    {
        try
        {
            RevealWindow();
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExplicitExit && HostBackgroundSupervisor.Instance.ShouldKeepRunningInBackground())
        {
            e.Cancel = true;
            Hide();
            try { _bridge.PauseBackgroundUiStreams(); } catch { }
            HostBackgroundSupervisor.Instance.EnsureTrayVisible();
            base.OnClosing(e);
            return;
        }

        if (!App.IsExplicitExit)
        {
            try { HostBackgroundSupervisor.Instance.RunExitMaintenanceIfConfigured(); } catch { }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _bridge.Dispose();
            if (AppWebView.CoreWebView2 != null)
            {
                AppWebView.CoreWebView2.WebMessageReceived -= Core_WebMessageReceived;
                AppWebView.CoreWebView2.NavigationStarting -= Core_NavigationStarting;
                AppWebView.CoreWebView2.NavigationCompleted -= Core_NavigationCompleted;
            }
            AppWebView.Dispose();
        }
        catch
        {
            // Ignore disposal issues during window teardown.
        }
        base.OnClosed(e);
        App.LogStartup($"MainWindow.OnClosed called! IsExplicitExit: {App.IsExplicitExit}");
        if (!App.IsExplicitExit && !HostBackgroundSupervisor.Instance.ShouldKeepRunningInBackground())
        {
            App.LogStartup("MainWindow.OnClosed: Shutting down application.");
            try { Application.Current?.Shutdown(); } catch { }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    private void InitializeWindowIcon()
    {
        try
        {
            var packUri = new Uri("pack://application:,,,/assets/app_logo.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(packUri);
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return;
            }
        }
        catch { }

        try
        {
            string baseDir = AppContext.BaseDirectory;
            string[] diskCandidates = new[]
            {
                Path.Combine(baseDir, "assets", "Error Optimizer V3.ico"),
                Path.Combine(baseDir, "assets", "app_logo.ico"),
                Path.Combine(baseDir, "frontend", "app_logo.ico"),
                Path.Combine(baseDir, "Error Optimizer V3.ico")
            };
            foreach (var cand in diskCandidates)
            {
                if (File.Exists(cand))
                {
                    Icon = BitmapFrame.Create(new Uri(cand, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    return;
                }
            }
        }
        catch { }

        try
        {
            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(procPath);
                if (sysIcon != null)
                {
                    Icon = Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    return;
                }
            }
        }
        catch { }
    }
}
