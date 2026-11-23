#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Automation;
using BiosOptimizer.Core.Implementations.Config;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Power;
using BiosOptimizer.IPC.Contracts;
using System.Runtime.InteropServices;
using System.Text.Json;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace ErrorOptimizerPrototypeHost.Services;

/// <summary>
/// Standalone background automation, AI boot-policy supervisor, single-instance activator,
/// and System Tray lifecycle host. Automatically yields background ticks to <c>BiosOptimizer.Service</c>
/// whenever its named pipe is active so scheduled/policy actions never double-execute.
/// </summary>
public sealed class HostBackgroundSupervisor : IDisposable
{
    private static readonly Lazy<HostBackgroundSupervisor> _instance = new(() => new HostBackgroundSupervisor());
    public static HostBackgroundSupervisor Instance => _instance.Value;

    private const string MutexName = @"Local\ErrorOptimizerPrototype_SingleInstance_v1";
    private const string WakeEventName = @"Local\ErrorOptimizerPrototype_WakeWindow_v1";
    private const string ExitEventName = @"Local\ErrorOptimizerPrototype_Exit_v1";

    private readonly object _sync = new();
    private Mutex? _instanceMutex;
    private EventWaitHandle? _wakeEvent;
    private EventWaitHandle? _exitEvent;
    private Thread? _wakeListenerThread;
    private Thread? _exitListenerThread;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private Task? _interactiveAgentTask;
    private readonly ServiceIpcClient _ipc = new();
    private WinForms.NotifyIcon? _trayIcon;
    private Action? _restoreWindowCallback;
    private Action? _exitAppCallback;
    private bool _isStartupLaunch;
    private bool _isBackgroundStartup;
    private bool _disposed;

    public enum OperationalServiceStatus
    {
        SERVICE_NOT_INSTALLED,
        SERVICE_STOPPED,
        SERVICE_START_PENDING,
        SERVICE_RUNNING,
        SERVICE_ERROR,
        IPC_DISCONNECTED,
        IPC_CONNECTED,
        HEALTH_CHECK_FAILED,
        ONLINE
    }

    private OperationalServiceStatus _operationalStatus = OperationalServiceStatus.SERVICE_NOT_INSTALLED;
    private int _consecutiveHealthFailures = 0;
    private string _lastStatusDiagnostic = "Initializing operational state...";

    public OperationalServiceStatus OperationalStatus => _operationalStatus;
    public string LastStatusDiagnostic => _lastStatusDiagnostic;

    private HostBackgroundSupervisor() { }

    public bool TryAcquirePrimaryInstance(bool signalIfSecondary = true)
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew)
            {
                _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
                _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                try { _wakeEvent.Reset(); } catch { }
                try { _exitEvent.Reset(); } catch { }
                return true;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;

            if (signalIfSecondary)
            {
                try
                {
                    using var existingWake = EventWaitHandle.OpenExisting(WakeEventName);
                    existingWake.Set();
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    public void Start(
        bool isStartupLaunch,
        bool isBackgroundStartup,
        Action restoreWindowCallback,
        Action exitAppCallback)
    {
        lock (_sync)
        {
            if (_disposed || _workerTask != null)
                return;

            _isStartupLaunch = isStartupLaunch;
            _isBackgroundStartup = isBackgroundStartup;
            _restoreWindowCallback = restoreWindowCallback;
            _exitAppCallback = exitAppCallback;

            InitializeWakeListener();
            InitializeExitListener();
            EnsureTrayIconLocked();

            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _workerTask = Task.Run(() => RunSupervisorLoopAsync(ct), ct);
            _interactiveAgentTask = Task.Run(() => RunInteractiveSessionAgentAsync(ct), ct);
        }
    }

    public void EnsureTrayVisible()
    {
        lock (_sync)
        {
            if (_disposed) return;
            EnsureTrayIconLocked();
        }
    }

    public static bool IsServicePipeActive()
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\", "BiosOptimizer_IPC_v2*").Any();
        }
        catch
        {
            return false;
        }
    }

    public static bool IsServiceActiveOrTransitioning()
    {
        if (IsServicePipeActive())
            return true;

        var scm = ServiceIpcClient.InspectScmState();
        return scm is ServiceScmState.Running or ServiceScmState.StartPending;
    }

    public bool ShouldKeepRunningInBackground()
    {
        try
        {
            var settings = UnifiedSettingsStore.LoadSafe();
            if (settings.RunInSystemTray)
                return true;

            if (IsServiceActiveOrTransitioning())
                return false;

            if (settings.AutoOptimizationEnabled || settings.AiOptimizationEnabled)
                return true;

            var cfg = AutoOptimizeEngine.Instance.LoadConfig();
            var eval = AutoOptimizePolicyEvaluator.Evaluate(settings, cfg);

            if (eval.IsScheduleEligible &&
                (cfg.CloseBehavior.Equals("background", StringComparison.OrdinalIgnoreCase) ||
                 (settings.AiEnabled && settings.Policies.AutoOptimize) ||
                 cfg.RunOnWindowsStartup))
            {
                return true;
            }

            if (settings.AiEnabled && settings.Policies.Workload)
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    public void RunExitMaintenanceIfConfigured()
    {
        if (IsServiceActiveOrTransitioning())
            return;

        try
        {
            var cfg = AutoOptimizeEngine.Instance.LoadConfig();
            var settings = UnifiedSettingsStore.LoadSafe();
            var eval = AutoOptimizePolicyEvaluator.Evaluate(settings, cfg);
            if (eval.ShouldRunOnAppExit)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                AutoOptimizeEngine.Instance
                    .ExecuteAsync("App exit", eval.SelectedTasks, cfg.RecycleConfirm, cts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception ex)
        {
            AuditLogManager.Write("AutoOptimize", "AppExit", "Warning", ex.Message);
        }
    }

    private void InitializeWakeListener()
    {
        if (_wakeEvent == null || _wakeListenerThread != null)
            return;

        _wakeListenerThread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_wakeEvent?.WaitOne() != true || _disposed)
                        break;

                    WpfApp.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        try { _restoreWindowCallback?.Invoke(); } catch { }
                    });
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "EO-WakeWindowListener"
        };
        _wakeListenerThread.Start();
    }

    private void InitializeExitListener()
    {
        if (_exitEvent == null || _exitListenerThread != null)
            return;

        _exitListenerThread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_exitEvent?.WaitOne() != true || _disposed)
                        break;

                    App.LogStartup("HostBackgroundSupervisor: _exitEvent was signaled! Triggering exit callback.");
                    WpfApp.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        try { _exitAppCallback?.Invoke(); } catch { }
                    });
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "EO-ExitAppListener"
        };
        _exitListenerThread.Start();
    }

    private void EnsureTrayIconLocked()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            return;
        }

        void CreateOnSta()
        {
            try
            {
                if (_trayIcon != null || _disposed) return;

                Drawing.Icon? icon = null;
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string[] candidates = new[]
                    {
                        Path.Combine(baseDir, "assets", "Error Optimizer V3.ico"),
                        Path.Combine(baseDir, "Error Optimizer V3.ico"),
                        Path.Combine(Directory.GetCurrentDirectory(), "assets", "Error Optimizer V3.ico"),
                        Path.Combine(baseDir, "..", "..", "assets", "Error Optimizer V3.ico")
                    };
                    foreach (var path in candidates)
                    {
                        if (File.Exists(path))
                        {
                            icon = new Drawing.Icon(path);
                            break;
                        }
                    }
                }
                catch { }

                if (icon == null)
                {
                    try
                    {
                        string? exe = Environment.ProcessPath;
                        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                            icon = Drawing.Icon.ExtractAssociatedIcon(exe);
                    }
                    catch { }
                }
                icon ??= Drawing.SystemIcons.Application;

                var menu = new WinForms.ContextMenuStrip();

                // 1. Open ERROR OPTIMIZER V4 (Default, bold)
                var openItem = new WinForms.ToolStripMenuItem("Open ERROR OPTIMIZER V4")
                {
                    Font = new Drawing.Font(Drawing.SystemFonts.DefaultFont, Drawing.FontStyle.Bold)
                };
                openItem.Click += (_, _) =>
                    WpfApp.Current?.Dispatcher.BeginInvoke(() => _restoreWindowCallback?.Invoke());
                menu.Items.Add(openItem);

                menu.Items.Add(new WinForms.ToolStripSeparator());

                // 2. Connection Status (dynamic status item, click to re-check)
                var connectionStatusItem = new WinForms.ToolStripMenuItem(GetConnectionStatusMenuText());
                connectionStatusItem.Click += (_, _) =>
                {
                    _ = Task.Run(async () =>
                    {
                        await EvaluateServiceHealthAsync(CancellationToken.None).ConfigureAwait(false);
                        RefreshTrayStatus();
                    });
                };
                menu.Items.Add(connectionStatusItem);

                // 3. Settings
                var settingsItem = new WinForms.ToolStripMenuItem("Settings");
                settingsItem.Click += (_, _) =>
                {
                    WpfApp.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        _restoreWindowCallback?.Invoke();
                        OperationsBridgeHandler.ActiveInstance?.NavigateToPage("settings");
                    });
                };
                menu.Items.Add(settingsItem);

                menu.Items.Add(new WinForms.ToolStripSeparator());

                // 4. Exit
                var exitItem = new WinForms.ToolStripMenuItem("Exit");
                exitItem.Click += (_, _) =>
                    WpfApp.Current?.Dispatcher.BeginInvoke(() => _exitAppCallback?.Invoke());
                menu.Items.Add(exitItem);

                menu.Opening += (_, _) =>
                {
                    connectionStatusItem.Text = GetConnectionStatusMenuText();
                };

                _trayIcon = new WinForms.NotifyIcon
                {
                    Icon = icon,
                    Text = "ERROR OPTIMIZER V4",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                _trayIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == WinForms.MouseButtons.Left)
                    {
                        WpfApp.Current?.Dispatcher.BeginInvoke(() => _restoreWindowCallback?.Invoke());
                    }
                };
                _trayIcon.DoubleClick += (_, _) =>
                    WpfApp.Current?.Dispatcher.BeginInvoke(() => _restoreWindowCallback?.Invoke());

                UpdateTrayMenu(menu, connectionStatusItem);
            }
            catch { }
        }

        var dispatcher = WpfApp.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            CreateOnSta();
        else
            dispatcher.BeginInvoke((Action)CreateOnSta);
    }

    public void OnTaskbarCreated()
    {
        var d = WpfApp.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(() =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (_trayIcon != null)
                {
                    try
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Visible = true;
                    }
                    catch
                    {
                        try { _trayIcon.Dispose(); } catch { }
                        _trayIcon = null;
                        EnsureTrayIconLocked();
                    }
                }
                else
                {
                    EnsureTrayIconLocked();
                }
            }
        });
    }

    public string GetConnectionStatusMenuText()
    {
        return _operationalStatus switch
        {
            OperationalServiceStatus.ONLINE => "Connection Status: Service Online",
            OperationalServiceStatus.SERVICE_NOT_INSTALLED => "Connection Status: Desktop Engine (In-Process)",
            OperationalServiceStatus.SERVICE_STOPPED => "Connection Status: Service Stopped (Fallback)",
            OperationalServiceStatus.SERVICE_START_PENDING => "Connection Status: Service Starting...",
            OperationalServiceStatus.SERVICE_RUNNING => "Connection Status: Connecting...",
            OperationalServiceStatus.IPC_DISCONNECTED => "Connection Status: IPC Disconnected (Fallback)",
            OperationalServiceStatus.HEALTH_CHECK_FAILED => "Connection Status: Health Check Failed (Fallback)",
            OperationalServiceStatus.SERVICE_ERROR => "Connection Status: Service Error (Fallback)",
            _ => "Connection Status: Desktop Engine Active"
        };
    }

    public void RefreshTrayStatus()
    {
        try
        {
            var d = WpfApp.Current?.Dispatcher;
            if (d == null) return;
            d.BeginInvoke(() =>
            {
                if (_trayIcon?.ContextMenuStrip is WinForms.ContextMenuStrip menu)
                {
                    var connItem = menu.Items.OfType<WinForms.ToolStripMenuItem>()
                        .FirstOrDefault(i => i.Text?.StartsWith("Connection Status") == true);
                    UpdateTrayMenu(menu, connItem);
                }
            });
        }
        catch { }
    }

    private void UpdateTrayMenu(
        WinForms.ContextMenuStrip menu,
        WinForms.ToolStripMenuItem? connectionStatusItem)
    {
        try
        {
            if (connectionStatusItem != null)
            {
                connectionStatusItem.Text = GetConnectionStatusMenuText();
            }

            if (_trayIcon != null)
            {
                _trayIcon.Text = "ERROR OPTIMIZER V4";
            }
        }
        catch { }
    }

    public async Task<bool> EvaluateServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var scm = ServiceIpcClient.InspectScmState(out string resolvedName);
            if (scm == ServiceScmState.NotInstalled)
            {
                _operationalStatus = OperationalServiceStatus.SERVICE_NOT_INSTALLED;
                _lastStatusDiagnostic = "Windows service is not installed in SCM. Running in Standalone (In-Process) mode.";
                _consecutiveHealthFailures = 0;
                RefreshTrayStatus();
                return false;
            }

            if (scm == ServiceScmState.Stopped)
            {
                _operationalStatus = OperationalServiceStatus.SERVICE_STOPPED;
                _lastStatusDiagnostic = $"Windows service '{resolvedName}' is stopped in SCM. Running standalone fallback.";
                _consecutiveHealthFailures = 0;
                RefreshTrayStatus();
                return false;
            }

            if (scm == ServiceScmState.StartPending)
            {
                _operationalStatus = OperationalServiceStatus.SERVICE_START_PENDING;
                _lastStatusDiagnostic = $"Windows service '{resolvedName}' is currently starting in SCM.";
                RefreshTrayStatus();
                return false;
            }

            if (scm == ServiceScmState.Running)
            {
                bool healthy = false;
                try
                {
                    var healthSnap = await _ipc.CheckHealthAsync(ct).ConfigureAwait(false);
                    healthy = healthSnap.Healthy;
                }
                catch (Exception ex)
                {
                    _lastStatusDiagnostic = $"IPC health check exception: {ex.Message}";
                }

                if (healthy)
                {
                    _operationalStatus = OperationalServiceStatus.ONLINE;
                    _lastStatusDiagnostic = $"Service '{resolvedName}' is verified ONLINE via authenticated v2 pipe.";
                    _consecutiveHealthFailures = 0;
                    RefreshTrayStatus();
                    return true;
                }
                else
                {
                    _consecutiveHealthFailures++;
                    // Debounce: minimum 3 consecutive failures before transitioning away from ONLINE to avoid transient flapping
                    if (_consecutiveHealthFailures < 3 && _operationalStatus == OperationalServiceStatus.ONLINE)
                    {
                        return true;
                    }

                    if (!IsServicePipeActive())
                    {
                        _operationalStatus = OperationalServiceStatus.IPC_DISCONNECTED;
                        _lastStatusDiagnostic = $"Service '{resolvedName}' is running in SCM, but Named Pipe is not present.";
                    }
                    else
                    {
                        _operationalStatus = OperationalServiceStatus.HEALTH_CHECK_FAILED;
                        _lastStatusDiagnostic = $"Service '{resolvedName}' pipe is present, but authenticated health check failed.";
                    }
                    RefreshTrayStatus();
                    return false;
                }
            }

            _operationalStatus = OperationalServiceStatus.SERVICE_ERROR;
            _lastStatusDiagnostic = $"Service '{resolvedName}' reported unexpected SCM state: {scm}";
            RefreshTrayStatus();
            return false;
        }
        catch (Exception ex)
        {
            _operationalStatus = OperationalServiceStatus.SERVICE_ERROR;
            _lastStatusDiagnostic = $"Exception evaluating service health: {ex.Message}";
            RefreshTrayStatus();
            return false;
        }
    }

    private async Task ExecuteStartupServiceLifecycleAsync(CancellationToken ct)
    {
        try
        {
            AuditLogManager.Write("HostBackground", "StartupLifecycle", "Info",
                "Initiating 9-step service detection and startup lifecycle...");

            var scm = ServiceIpcClient.InspectScmState(out string resolvedName);
            if (scm == ServiceScmState.NotInstalled)
            {
                _operationalStatus = OperationalServiceStatus.SERVICE_NOT_INSTALLED;
                _lastStatusDiagnostic = "Windows Service is not installed. Active in Standalone (In-Process) mode.";
                AuditLogManager.Write("HostBackground", "StartupLifecycle", "Info",
                    "Service not registered in SCM. Standalone supervisor activated.");
                RefreshTrayStatus();
                return;
            }

            if (scm == ServiceScmState.Stopped || scm == ServiceScmState.Paused)
            {
                _operationalStatus = OperationalServiceStatus.SERVICE_STOPPED;
                _lastStatusDiagnostic = $"Service '{resolvedName}' is installed but stopped. Attempting automatic start...";
                RefreshTrayStatus();

                bool started = ServiceIpcClient.TryStartService(out string startDiag);
                AuditLogManager.Write("HostBackground", "StartupLifecycle", started ? "OK" : "Warning",
                    $"Service start attempt: {startDiag}");

                if (started)
                {
                    _operationalStatus = OperationalServiceStatus.SERVICE_START_PENDING;
                    RefreshTrayStatus();
                    bool pipeReady = await _ipc.WaitForServiceInitializationAsync(8000, ct).ConfigureAwait(false);
                    AuditLogManager.Write("HostBackground", "StartupLifecycle", pipeReady ? "OK" : "Warning",
                        pipeReady ? "Service pipe initialized." : "Service pipe wait timed out.");
                }
            }
            else if (scm == ServiceScmState.StartPending)
            {
                _operationalStatus = OperationalServiceStatus.SERVICE_START_PENDING;
                RefreshTrayStatus();
                await _ipc.WaitForServiceInitializationAsync(8000, ct).ConfigureAwait(false);
            }

            // Perform initial health check
            await EvaluateServiceHealthAsync(ct).ConfigureAwait(false);
            AuditLogManager.Write("HostBackground", "StartupLifecycle", "OK",
                $"Startup lifecycle complete. Status: {_operationalStatus} ({_lastStatusDiagnostic})");
        }
        catch (Exception ex)
        {
            _operationalStatus = OperationalServiceStatus.SERVICE_ERROR;
            _lastStatusDiagnostic = $"Startup lifecycle exception: {ex.Message}";
            AuditLogManager.Write("HostBackground", "StartupLifecycle", "Error", ex.Message);
            RefreshTrayStatus();
        }
    }

    private async Task RunSupervisorLoopAsync(CancellationToken ct)
    {
        bool policiesApplied = false;
        bool startupAutoRunArmed = false;
        bool wasStandaloneActive = false;
        DateTime lastSettingsWriteUtc = DateTime.MinValue;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Execute startup service detection and startup lifecycle
        await ExecuteStartupServiceLifecycleAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool isOnline = await EvaluateServiceHealthAsync(ct).ConfigureAwait(false);

                if (isOnline)
                {
                    // Yield strictly to BiosOptimizer.Service when it is verified ONLINE
                    if (wasStandaloneActive)
                    {
                        wasStandaloneActive = false;
                        StopHostFallbackWorkersOnServiceTakeover();
                    }
                    policiesApplied = false;
                    startupAutoRunArmed = false;
                }
                else
                {
                    wasStandaloneActive = true;
                    var settings = UnifiedSettingsStore.LoadSafe();
                    var cfg = AutoOptimizeEngine.Instance.LoadConfig();
                    DateTime curSettingsWriteUtc = File.Exists(UnifiedSettingsStore.SettingsFilePath)
                        ? File.GetLastWriteTimeUtc(UnifiedSettingsStore.SettingsFilePath)
                        : DateTime.MinValue;

                    if (!policiesApplied || curSettingsWriteUtc > lastSettingsWriteUtc)
                    {
                        policiesApplied = true;
                        lastSettingsWriteUtc = curSettingsWriteUtc;
                        await ApplyStandalonePoliciesAsync(settings, ct).ConfigureAwait(false);
                    }

                    var initialEval = AutoOptimizePolicyEvaluator.Evaluate(settings, cfg);
                    if (!startupAutoRunArmed)
                    {
                        startupAutoRunArmed = true;
                        if (_isStartupLaunch && initialEval.IsStartupEligible)
                        {
                            if (!initialEval.DelayStartupByInterval)
                            {
                                AutoOptimizeEngine.Instance.SetNextRunUtc(DateTime.UtcNow.AddMinutes(2));
                                AuditLogManager.Write("HostBackground", "AutoOptimize.Startup", "Scheduled",
                                    "Armed Windows startup Auto Optimize pass in 2 minutes (after-delay).");
                            }
                            else
                            {
                                AutoOptimizeEngine.Instance.SetNextRunUtc(DateTime.UtcNow.AddMinutes(cfg.IntervalMin));
                            }
                        }
                        else if (!_isStartupLaunch && initialEval.ShouldRunOnAppLaunch)
                        {
                            await AutoOptimizeEngine.Instance
                                .ExecuteAsync("App launch", initialEval.SelectedTasks, cfg.RecycleConfirm, ct)
                                .ConfigureAwait(false);
                        }
                    }

                    var snap = AutoOptimizeEngine.Instance.GetStateSnapshot();
                    var tickEval = AutoOptimizePolicyEvaluator.Evaluate(settings, snap.Config, snap.NextRunUtc);
                    if (tickEval.ShouldRunScheduledTick)
                    {
                        string trig = (_isStartupLaunch && snap.LastRunUtc == null &&
                                       !tickEval.DelayStartupByInterval)
                            ? "Windows startup"
                            : "Schedule";
                        await AutoOptimizeEngine.Instance
                            .ExecuteAsync(trig, tickEval.SelectedTasks, false, ct)
                            .ConfigureAwait(false);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AuditLogManager.Write("HostBackground", "Tick", "Warning", ex.Message, detailedOnly: true);
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private static void StopHostFallbackWorkersOnServiceTakeover()
    {
        try
        {
            PowerPlanEngine.Instance.StopBackgroundEnforcement();
            WorkloadOptimizationEngine.Instance.Stop();
            AuditLogManager.Write("HostBackground", "ServiceTakeover", "OK",
                "Yielded background automation to active BiosOptimizer.Service.");
        }
        catch { }
    }

    private static async Task ApplyStandalonePoliciesAsync(UnifiedAppSettings settings, CancellationToken ct)
    {
        WorkloadOptimizationEngine.Instance.LoadConfiguration();
        WorkloadOptimizationEngine.Instance.BuildInstalledInventory();

        await AiPolicyReconciler
            .ReconcileAsync(settings, managePowerPlan: true, ct: ct)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
            try { _wakeEvent?.Dispose(); } catch { }
            try { _exitEvent?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            try { _ipc.Dispose(); } catch { }

            var icon = _trayIcon;
            _trayIcon = null;
            if (icon != null)
            {
                try
                {
                    var d = WpfApp.Current?.Dispatcher;
                    if (d == null || d.CheckAccess())
                    {
                        icon.Visible = false;
                        icon.Dispose();
                    }
                    else
                    {
                        d.Invoke(() => { icon.Visible = false; icon.Dispose(); });
                    }
                }
                catch { }
            }

            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch { }
            try { _instanceMutex?.Dispose(); } catch { }
        }
    }

    private async Task RunInteractiveSessionAgentAsync(CancellationToken ct)
    {
        uint lastReportedPid = 0;
        bool lastReportedFs = false;
        DateTime lastHeartbeatUtc = DateTime.MinValue;

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                int mySession = Process.GetCurrentProcess().SessionId;
                if (mySession > 0)
                {
                    IntPtr hwnd = NativeMethods.GetForegroundWindow();
                    uint pid = 0;
                    string procName = string.Empty;
                    string exePath = string.Empty;
                    string title = string.Empty;
                    bool isFs = false;

                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                        if (pid > 0)
                        {
                            try
                            {
                                using var p = Process.GetProcessById((int)pid);
                                procName = p.ProcessName;
                                try { exePath = p.MainModule?.FileName ?? string.Empty; } catch { }
                            }
                            catch { }
                        }

                        if (NativeMethods.GetWindowRect(hwnd, out RECT r))
                        {
                            int sw = NativeMethods.GetSystemMetrics(0);
                            int sh = NativeMethods.GetSystemMetrics(1);
                            isFs = (r.Right - r.Left) >= sw && (r.Bottom - r.Top) >= sh;
                        }
                    }

                    // Update in-process bridge for standalone execution
                    InteractiveSessionBridge.UpdateSessionForeground(mySession, pid, procName, exePath, title, isFs);

                    // Send IPC update to Service when PID/fullscreen changed or every 5 seconds (heartbeat)
                    bool stateChanged = pid != lastReportedPid || isFs != lastReportedFs;
                    bool heartbeatDue = (DateTime.UtcNow - lastHeartbeatUtc).TotalSeconds >= 5;

                    if ((stateChanged || heartbeatDue) && IsServicePipeActive())
                    {
                        lastReportedPid = pid;
                        lastReportedFs = isFs;
                        lastHeartbeatUtc = DateTime.UtcNow;

                        string payload = JsonSerializer.Serialize(new
                        {
                            sessionId = mySession,
                            foregroundPid = pid,
                            processName = procName,
                            executablePath = exePath,
                            windowTitle = title,
                            isFullscreen = isFs
                        });

                        try
                        {
                            await _ipc.SendAsync(IpcMessageType.ReportInteractiveSessionState, payload, timeoutMsOverride: 2000, ct: ct).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }

            try
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
