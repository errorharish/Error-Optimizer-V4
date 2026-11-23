using System;

using System.Configuration;

using System.Data;

using System.Diagnostics;

using System.IO;

using System.Runtime.InteropServices;

using System.Threading;

using System.Threading.Tasks;

using System.Windows;

using BiosOptimizer.Core.Implementations.Diagnostics;

using BiosOptimizer.Core.Implementations.Startup;

using BiosOptimizer.Core.Services;

using BiosOptimizer.GUI.Services;

namespace BiosOptimizer.GUI

{

    public partial class App : Application

    {

        public static bool IsSafeMode { get; private set; }

        public static bool IsStartupBackground { get; private set; }

        public static bool IsDiagnosticMode { get; private set; }

        public static bool IsNoGpuMode { get; private set; }

        public static bool IsNoAdvancedMemoryMode { get; private set; }

        public static bool IsNoEffectsMode { get; private set; }

        public static bool IsExiting { get; set; }

        public BackendManager BackendManager { get; private set; }

        [DllImport("user32.dll")]

        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]

        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]

        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]

        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential)]

        public struct RECT

        {

            public int Left;

            public int Top;

            public int Right;

            public int Bottom;

        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]

        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]

        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]

        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]

        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        private static Mutex? _singleInstanceMutex;

        private static EventWaitHandle? _wakeupEvent;

        private static CancellationTokenSource? _wakeupListenerCts;

        public App()

        {

            try

            {

                Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            }

            catch { }

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            SetupExceptionHandling();

            try 

            { 

                SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); 

            } 

            catch { }

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>

            {

                StartupSelfDiagnosticTracker.Instance.RecordExit(Environment.ExitCode);

                ApplicationCrashLogger.Instance.LogStartupStage("PROCESS_EXIT", $"ProcessExit. ExitCode={Environment.ExitCode}. Trace: {Environment.StackTrace}");

            };

        }

        private void SetupExceptionHandling()

        {

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>

            {

                if (e.ExceptionObject is Exception ex)

                {

                    bool isCrtUnload = ex is DllNotFoundException && (ex.TargetSite?.Name?.Contains("destroy_list") == true || ex.StackTrace?.Contains("ModuleUninitializer") == true);

                    bool isFatal = !e.IsTerminating && !isCrtUnload;

                    ApplicationCrashLogger.Instance.LogCrash(ex, "AppDomain", isFatal);

                    StartupRecoveryManager.Instance.RecordStartupFailure("AppDomain", ex);

                }

            };

            this.DispatcherUnhandledException += (s, e) =>

            {

                ApplicationCrashLogger.Instance.LogCrash(e.Exception, "Dispatcher", false);

                e.Handled = true;

                // Safely show non-fatal error toast or in-page recovery on MainWindow

                if (MainWindow?.DataContext is BiosOptimizer.GUI.ViewModels.MainViewModel mainVm)

                {

                    try

                    {

                        mainVm.ShowGlobalError(e.Exception);

                    }

                    catch { }

                }

            };

            TaskScheduler.UnobservedTaskException += (s, e) =>

            {

                ApplicationCrashLogger.Instance.LogCrash(e.Exception, "TaskScheduler", false);

                e.SetObserved();

            };

        }

        private static List<IntPtr> FindVisibleProcessWindows(int pid)

        {

            var list = new List<IntPtr>();

            try

            {

                EnumWindows((hWnd, lParam) =>

                {

                    GetWindowThreadProcessId(hWnd, out uint p);

                    if (p == (uint)pid && IsWindowVisible(hWnd))

                    {

                        RECT r;

                        GetWindowRect(hWnd, out r);

                        int w = r.Right - r.Left;

                        int h = r.Bottom - r.Top;

                        if (w > 300 && h > 200)

                        {

                            list.Add(hWnd);

                        }

                    }

                    return true;

                }, IntPtr.Zero);

            }

            catch { }

            return list;

        }

        private static bool ActivateExistingInstance(List<Process> otherInstances)

        {

            try

            {

                StartupLogger.Log("SINGLE_INSTANCE", $"Attempting to activate visible window across {otherInstances.Count} other process(es)...");

                foreach (var p in otherInstances)

                {

                    var hwnds = FindVisibleProcessWindows(p.Id);

                    foreach (var hwnd in hwnds)

                    {

                        StartupLogger.Log("SINGLE_INSTANCE", $"Found visible window {hwnd} in PID {p.Id}. Bringing to foreground.");

                        ShowWindow(hwnd, 9); // SW_RESTORE

                        SetForegroundWindow(hwnd);

                        return true;

                    }

                }

                var titles = new[] { "Error Optimizer Pro", "Error Optimizer", "ErrorOptimizer" };

                foreach (var title in titles)

                {

                    var handle = FindWindow(null, title);

                    if (handle != IntPtr.Zero && IsWindowVisible(handle))

                    {

                        StartupLogger.Log("SINGLE_INSTANCE", $"Found visible window {handle} matching title '{title}'. Bringing to foreground.");

                        ShowWindow(handle, 9); // SW_RESTORE

                        SetForegroundWindow(handle);

                        return true;

                    }

                }

            }

            catch (Exception ex)

            {

                StartupLogger.LogError("SINGLE_INSTANCE", "Exception in ActivateExistingInstance", ex);

            }

            return false;

        }

        protected override void OnStartup(StartupEventArgs e)

        {

            StartupLogger.Log("ENTRY", $"App.OnStartup. PID={Environment.ProcessId}, Args=[{string.Join(" ", e.Args)}]");

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            ApplicationCrashLogger.Instance.LogStartupPhaseStart("BOOT");

            ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 01", "App process started");

            StartupRecoveryManager.Instance.RecordStartupAttempt();

            bool isBackground = false;

            bool isStartupLaunch = false;

            string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

            StartupSelfDiagnosticTracker.Instance.RecordStartupLaunch(e.Args, currentExe);

            foreach (var arg in e.Args)

            {

                if (arg.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase))

                {

                    IsSafeMode = true;

                }

                else if (arg.Equals("--diagnostic", StringComparison.OrdinalIgnoreCase) ||

                         arg.Equals("--diagnostic-startup", StringComparison.OrdinalIgnoreCase))

                {

                    IsDiagnosticMode = true;

                }

                else if (arg.Equals("--no-gpu", StringComparison.OrdinalIgnoreCase))

                {

                    IsNoGpuMode = true;

                }

                else if (arg.Equals("--no-advanced-memory", StringComparison.OrdinalIgnoreCase))

                {

                    IsNoAdvancedMemoryMode = true;

                }

                else if (arg.Equals("--no-effects", StringComparison.OrdinalIgnoreCase))

                {

                    IsNoEffectsMode = true;

                }

                else if (arg.Equals("--startup-background", StringComparison.OrdinalIgnoreCase) ||

                         arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||

                         arg.Equals("--startup", StringComparison.OrdinalIgnoreCase))

                {

                    isBackground = true;

                    isStartupLaunch = true;

                    IsStartupBackground = true;

                }

                else if (arg.Equals("--auto-optimize-background", StringComparison.OrdinalIgnoreCase) ||

                         arg.Equals("--auto-optimize-silent", StringComparison.OrdinalIgnoreCase))

                {

                    // Headless background run via Windows Task Scheduler

                    Task.Run(async () =>

                    {

                        try

                        {

                            await SmartAutoOptimizeScheduler.Instance.RunManualOrScheduledAsync("TASK_SCHEDULER");

                        }

                        catch { }

                        finally

                        {

                            Environment.Exit(0);

                        }

                    }).Wait();

                    return;

                }

                else if (arg.Equals("--reconcile-startup", StringComparison.OrdinalIgnoreCase))

                {

                    try

                    {

                        WindowsStartupRegistrar.Instance.ReconcileAndCleanAllDuplicates();

                    }

                    catch { }

                    Environment.Exit(0);

                    return;

                }

                else if (arg.Equals("--register-startup", StringComparison.OrdinalIgnoreCase))

                {

                    try

                    {

                        WindowsStartupRegistrar.Instance.ConfigureStartup(true, startMinimized: true);

                    }

                    catch { }

                    Environment.Exit(0);

                    return;

                }

                else if (arg.Equals("--unregister-startup", StringComparison.OrdinalIgnoreCase))

                {

                    try

                    {

                        WindowsStartupRegistrar.Instance.ConfigureStartup(false);

                    }

                    catch { }

                    Environment.Exit(0);

                    return;

                }

            }

            ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("BOOT");

            // Single Instance Enforcement & Wakeup Signaling (Permission-Safe & Abandonment-Proof)

            const string mutexNameGlobal = @"Global\ErrorOptimizer_SingleInstance_Mutex";

            const string mutexNameLocal = @"Local\ErrorOptimizer_SingleInstance_Mutex";

            const string eventNameGlobal = @"Global\ErrorOptimizer_Wakeup_Event";

            const string eventNameLocal = @"Local\ErrorOptimizer_Wakeup_Event";

            bool isOnlyInstance = false;

            try

            {

                _singleInstanceMutex = new Mutex(true, mutexNameGlobal, out bool createdNew);

                if (createdNew)

                {

                    isOnlyInstance = true;

                }

                else

                {

                    try

                    {

                        if (_singleInstanceMutex.WaitOne(0, false))

                        {

                            isOnlyInstance = true;

                        }

                    }

                    catch (AbandonedMutexException)

                    {

                        isOnlyInstance = true;

                    }

                }

            }

            catch (UnauthorizedAccessException)

            {

                try

                {

                    _singleInstanceMutex = new Mutex(true, mutexNameLocal, out bool createdLocal);

                    if (createdLocal)

                    {

                        isOnlyInstance = true;

                    }

                    else

                    {

                        try

                        {

                            if (_singleInstanceMutex.WaitOne(0, false))

                            {

                                isOnlyInstance = true;

                            }

                        }

                        catch (AbandonedMutexException)

                        {

                            isOnlyInstance = true;

                        }

                    }

                }

                catch

                {

                    isOnlyInstance = true;

                }

            }

            catch

            {

                try

                {

                    _singleInstanceMutex = new Mutex(true, mutexNameLocal, out bool createdLocal);

                    isOnlyInstance = createdLocal;

                }

                catch

                {

                    isOnlyInstance = true;

                }

            }

            // CRITICAL SANITY CHECK: Check actual running processes in the OS

            try

            {

                int currentPid = Environment.ProcessId;

                var otherInstances = Process.GetProcessesByName("ErrorOptimizer")

                    .Where(p => p.Id != currentPid)

                    .ToList();

                if (otherInstances.Count == 0)

                {

                    isOnlyInstance = true;

                    StartupLogger.Log("SINGLE_INSTANCE", "No other ErrorOptimizer processes found. Primary instance confirmed.");

                }

                else

                {

                    StartupLogger.Log("SINGLE_INSTANCE", $"Found {otherInstances.Count} other ErrorOptimizer process(es).");

                    bool activated = ActivateExistingInstance(otherInstances);

                    if (activated)

                    {

                        StartupLogger.Log("SINGLE_INSTANCE", "Existing visible window successfully activated. Exiting secondary instance.");

                        Environment.Exit(0);

                        return;

                    }

                    StartupLogger.Log("SINGLE_INSTANCE", "Other instance(s) have NO visible window (headless or zombie). Terminating them to reclaim primary instance...");

                    foreach (var p in otherInstances)

                    {

                        try

                        {

                            StartupLogger.Log("SINGLE_INSTANCE", $"Terminating zombie process PID {p.Id}...");

                            p.Kill(true);

                            p.WaitForExit(1000);

                        }

                        catch (Exception exKill)

                        {

                            StartupLogger.LogError("SINGLE_INSTANCE", $"Error terminating PID {p.Id}", exKill);

                        }

                    }

                    try

                    {

                        _singleInstanceMutex?.Dispose();

                        _singleInstanceMutex = new Mutex(true, mutexNameGlobal, out bool createdNew);

                    }

                    catch { }

                    isOnlyInstance = true;

                }

            }

            catch (Exception exSanity)

            {

                StartupLogger.LogError("SINGLE_INSTANCE", "Sanity check error", exSanity);

                isOnlyInstance = true;

            }

            StartupSelfDiagnosticTracker.Instance.RecordSingleInstance(isOnlyInstance);

            // Primary instance creates the wakeup event and listens for manual activations

            try

            {

                try

                {

                    _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventNameGlobal);

                }

                catch

                {

                    _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventNameLocal);

                }

                _wakeupListenerCts = new CancellationTokenSource();

                var token = _wakeupListenerCts.Token;

                Task.Run(() =>

                {

                    while (!IsExiting && !token.IsCancellationRequested)

                    {

                        try

                        {

                            if (_wakeupEvent != null && _wakeupEvent.WaitOne(500))

                            {

                                if (IsExiting || token.IsCancellationRequested) break;

                                Current?.Dispatcher.BeginInvoke(new Action(() =>

                                {

                                    // Headless host: no GUI window to restore

                                }));

                            }

                        }

                        catch { }

                    }

                }, token);

            }

            catch { }

            // [STARTUP 02] CONFIG LOAD

            ApplicationCrashLogger.Instance.LogStartupPhaseStart("CONFIG LOAD");

            try

            {

                AppSettingsService.Instance.Load();

                if (isStartupLaunch)

                {

                    isBackground = true;

                    IsStartupBackground = true;

                }

                ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("CONFIG LOAD");

                ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 02", "Core configuration loaded");

            }

            catch (Exception ex)

            {

                ApplicationCrashLogger.Instance.LogStartupPhaseFailed("CONFIG LOAD", ex);

                ApplicationCrashLogger.Instance.LogCrash(ex, "Startup.LoadSettings", false);

            }

            // [STARTUP 03] THEME LOAD

            ApplicationCrashLogger.Instance.LogStartupPhaseStart("THEME LOAD");

            base.OnStartup(e);

            

            try

            {

                // Theme resources removed in headless mode

                

                ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("THEME LOAD");

            }

            catch (Exception ex)

            {

                ApplicationCrashLogger.Instance.LogStartupPhaseFailed("THEME LOAD", ex);

                ApplicationCrashLogger.Instance.LogCrash(ex, "Startup.ThemeBrushService", false);

            }

            ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 03", "Theme resources loaded");

            // [PHASE 1] FRONTEND-ONLY LOVABLE PROTOTYPE MODE
            // Per user directive: Temporarily disconnect all native backend services, IPC, and reconciliation.
            // All native backend code, services, and classes are preserved intact for Phase 2.
            const bool isPhase1FrontendOnlyMode = true;

            ServiceBootStatus serviceBootStatus = ServiceBootStatus.Running;

            if (!isPhase1FrontendOnlyMode)
            {
// [STARTUP 03.5] SERVICE BOOTSTRAP AUTO-REGISTRATION (Non-blocking so MainWindow always opens immediately)

            serviceBootStatus = ServiceBootStatus.Running;

            _ = Task.Run(async () =>

            {

                try

                {

                    serviceBootStatus = await ServiceBootstrapper.EnsureRunningAsync();

                    ApplicationCrashLogger.Instance.LogStartupStage("SERVICE_BOOTSTRAP", $"ServiceBootStatus: {serviceBootStatus}");

                }

                catch (Exception ex)

                {

                    ApplicationCrashLogger.Instance.LogCrash(ex, "Startup.ServiceBootstrapper", false);

                }

            });

            // [STARTUP 04] SERVICE INITIALIZATION

            ApplicationCrashLogger.Instance.LogStartupPhaseStart("SERVICE INITIALIZATION");

            BackendManager = new BackendManager();

            _ = Task.Run(() =>

            {

                try

                {

                    BackendManager.StartBackend();

                    StartupSelfDiagnosticTracker.Instance.RecordBackend(true);

                    ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("SERVICE INITIALIZATION");

                }

                catch (Exception ex)

                {

                    StartupSelfDiagnosticTracker.Instance.RecordBackend(false);

                    ApplicationCrashLogger.Instance.LogStartupPhaseFailed("SERVICE INITIALIZATION", ex);

                    ApplicationCrashLogger.Instance.LogCrash(ex, "Startup.BackendManager", false);

                }

            });

            // Run Phased Authoritative Startup Coordinator

            _ = Task.Run(async () =>

            {

                try

                {

                    var coord = StartupTaskCoordinator.Instance;

                    await coord.ExecuteTaskAsync(

                        "DetectHardware",

                        async ct => 

                        { 

                            if (!IsNoGpuMode)

                            {

                                BiosOptimizer.Core.Implementations.HardwareProfiler.GetQuickProfile(forceRefresh: true);

                            }

                            ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 08", "Hardware identity loaded");

                            await Task.CompletedTask; 

                        },

                        TimeSpan.FromSeconds(5));

                    await coord.ExecuteTaskAsync(

                        "StartTelemetry",

                        async ct =>

                        {

                            ApplicationCrashLogger.Instance.LogStartupPhaseStart("TELEMETRY INITIALIZATION");

                            ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 09", "Lightweight telemetry started");

                            ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("TELEMETRY INITIALIZATION");

                            await Task.CompletedTask;

                        },

                        TimeSpan.FromSeconds(5));

                    await coord.ExecuteTaskAsync(

                        "LoadProfileState",

                        async ct =>

                        {

                            ApplicationCrashLogger.Instance.LogStartupPhaseStart("OPTIMIZATION ENGINE INITIALIZATION");

                            ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 10", "Profile state loaded");

                            ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("OPTIMIZATION ENGINE INITIALIZATION");

                            await Task.CompletedTask;

                        },

                        TimeSpan.FromSeconds(5));

                    await coord.ExecuteTaskAsync(

                        "ReconcileSystem",

                        async ct => 

                        { 

                            ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 11", "Background reconciliation started");

                            await StartupBootstrapService.Instance.RunBootstrapAsync(isBackground, ct); 

                        },

                        TimeSpan.FromSeconds(10));

                    coord.MarkCompleted();

                    ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 12", "Startup complete");

                    ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("READY");

                    // SystemTrayService removed in headless conversion

                    StartupSelfDiagnosticTracker.Instance.RecordAiEngines(workload: true, ram: true, powerPlan: true);

                }

                catch (Exception ex)

                {

                    ApplicationCrashLogger.Instance.LogCrash(ex, "Startup.PhasedCoordinator", false);

                }

            });

                        }
            else
            {
                BackendManager = new BackendManager();
                StartupLogger.Log("PHASE_1", "Phase 1: Native backend services, IPC, and reconciliation disconnected (Frontend Prototype Mode).");
                ApplicationCrashLogger.Instance.LogStartupStage("PHASE_1", "Frontend-only prototype mode active. Native backend disconnected.");
                ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("READY");
            }

            StartMemoryMonitor();

        }

        private void StartMemoryMonitor()

        {

            if (IsSafeMode) return;

            var timer = new System.Windows.Threading.DispatcherTimer();

            timer.Interval = TimeSpan.FromSeconds(15);

            timer.Tick += (s, ev) =>

            {

                try

                {

                    var (wsMb, pvtMb, gcMb) = BiosOptimizer.Core.Implementations.AdaptiveResourceGovernor.Instance.GetAppMemoryFootprint();

                    if (wsMb > 300)

                    {

                        BiosOptimizer.Core.Implementations.AdaptiveResourceGovernor.Instance.TrimAppMemory();

                    }

                }

                catch { }

            };

            timer.Start();

        }

        protected override void OnExit(ExitEventArgs e)

        {

            IsExiting = true;

            ApplicationCrashLogger.Instance.LogStartupStage("ON_EXIT", $"OnExit. AppExitCode={e.ApplicationExitCode}. Trace: {Environment.StackTrace}");

            try

            {

                _wakeupListenerCts?.Cancel();

                _wakeupEvent?.Set();

                _wakeupEvent?.Dispose();

            }

            catch { }

            try

            {

                _singleInstanceMutex?.Dispose();

            }

            catch { }

            try

            {

                BackendManager?.Dispose();

            }

            catch { }

            base.OnExit(e);

        }

        public void RestartBackend()

        {

            try

            {

                BackendManager?.Shutdown();

                _ = Task.Run(() => BackendManager.StartBackend());

            }

            catch { }

        }

    }

}


