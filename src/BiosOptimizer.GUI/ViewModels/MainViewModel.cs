
using System;







using System.Collections.Generic;







using System.Threading.Tasks;







using System.Windows.Input;







using BiosOptimizer.GUI.Services;







using BiosOptimizer.GUI.ViewModels.Base;







namespace BiosOptimizer.GUI.ViewModels







{







    public class MainViewModel : ViewModelBase







    {







        private ViewModelBase _currentView;







        private readonly IIpcClient _ipc;







        private readonly Dictionary<string, Func<ViewModelBase>> _viewFactory;







        private readonly Dictionary<string, ViewModelBase> _viewCache;







        private readonly System.Windows.Threading.DispatcherTimer _healthTimer;







        public event Action<string> NavigationChanged;







        public ViewModelBase CurrentView







        {







            get => _currentView;







            private set { _currentView = value; OnPropertyChanged(); }







        }







        public ICommand NavigateCommand { get; }



        public ICommand SwitchToV3Command { get; }



        public ICommand SwitchToV4Command { get; }



        public ICommand SwitchToEmulatorCommand { get; }



        public ICommand RetryServiceCommand { get; }







        // ── Generation Mode (V3 vs V4 vs Emulator) ──────────────────────



        private bool _isV4Mode = false;



        private string _lastV3Route = "Dashboard";



        private string _lastV4Route = "V4MaxPerformance";







        public bool IsV3Mode



        {



            get => !_isV4Mode;



            set



            {



                if (_isV4Mode == value)



                {



                    _isV4Mode = !value;



                    NotifyModeProperties();



                }



            }



        }







        public bool IsV4Mode



        {



            get => _isV4Mode;



            set



            {



                if (_isV4Mode != value)



                {



                    _isV4Mode = value;



                    NotifyModeProperties();



                }



            }



        }







        public bool IsEmulatorMode => false;







        private void NotifyModeProperties()



        {



            OnPropertyChanged(nameof(IsV3Mode));



            OnPropertyChanged(nameof(IsV4Mode));



            OnPropertyChanged(nameof(IsEmulatorMode));



            OnPropertyChanged(nameof(HeaderBrandTitle));



            OnPropertyChanged(nameof(HeaderWorkspaceLabel));
            OnPropertyChanged(nameof(HeaderSubtitleText));
            OnPropertyChanged(nameof(FooterStatusText));



            OnPropertyChanged(nameof(WindowTitle));



        }







        public string HeaderBrandTitle => "ERROR OPTIMIZER V4";
        public string HeaderWorkspaceLabel => "Unified Optimization Suite";
        public string HeaderSubtitleText => "ERROR OPTIMIZER V4 — UNIFIED SYSTEM ENGINE";
        public string FooterStatusText => "ERROR OPTIMIZER V4 — PRODUCTION DESKTOP SUITE";
        public string WindowTitle => "ERROR OPTIMIZER — VERSION V4";







        // ── Navigation Unlock State (Permanently Unlocked) ──────────────────



        public bool IsNavigationUnlocked => true;



        public bool IsStartupIntroActive => false;



        public bool IsStartupIntroLocked => false;



        public int StartupIntroCountdown => 0;



        public string StartupIntroCountdownText => string.Empty;







        public Action<Exception> _showGlobalErrorAction;







        public void ShowGlobalError(Exception ex)







        {







            _showGlobalErrorAction?.Invoke(ex);







        }







        







        // ------ Global Toast Overlay ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------







        private bool _showToast;







        private string _toastTitle = "";







        private string _toastMessage = "";







        







        public bool ShowToast { get => _showToast; set { _showToast = value; OnPropertyChanged(); } }







        public string ToastTitle { get => _toastTitle; set { _toastTitle = value; OnPropertyChanged(); } }







        public string ToastMessage { get => _toastMessage; set { _toastMessage = value; OnPropertyChanged(); } }







        







        // ------ Premium Result Overlay ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------







        private bool _showResultOverlay;







        private string _resultType = "Success"; // "Success", "Partial", "Failed"







        private string _resultTitle = "";







        private string _resultSubtitle = "";







        private int _resultOptimizedCount;







        private int _resultAlreadyOptimizedCount;







        private int _resultNotAvailableCount;







        private int _resultNotApplicableCount;







        private int _resultBlockedCount;







        private int _resultFailedCount;







        private string _resultPrimaryReason = "";







        







        public bool ShowResultOverlay { get => _showResultOverlay; set { _showResultOverlay = value; OnPropertyChanged(); } }







        public string ResultType { get => _resultType; set { _resultType = value; OnPropertyChanged(); } }







        public string ResultTitle { get => _resultTitle; set { _resultTitle = value; OnPropertyChanged(); } }







        public string ResultSubtitle { get => _resultSubtitle; set { _resultSubtitle = value; OnPropertyChanged(); } }







        public int ResultOptimizedCount { get => _resultOptimizedCount; set { _resultOptimizedCount = value; OnPropertyChanged(); } }







        public int ResultAlreadyOptimizedCount { get => _resultAlreadyOptimizedCount; set { _resultAlreadyOptimizedCount = value; OnPropertyChanged(); } }







        public int ResultNotAvailableCount { get => _resultNotAvailableCount; set { _resultNotAvailableCount = value; OnPropertyChanged(); } }







        public int ResultNotApplicableCount { get => _resultNotApplicableCount; set { _resultNotApplicableCount = value; OnPropertyChanged(); } }







        public int ResultBlockedCount { get => _resultBlockedCount; set { _resultBlockedCount = value; OnPropertyChanged(); } }







        public int ResultFailedCount { get => _resultFailedCount; set { _resultFailedCount = value; OnPropertyChanged(); } }







        public string ResultPrimaryReason { get => _resultPrimaryReason; set { _resultPrimaryReason = value; OnPropertyChanged(); } }







        







        public void ShowGlobalToast(string title, string message)







        {







            ToastTitle = title;







            ToastMessage = message;







            ShowToast = true;







            _ = HideToastAsync();







        }







        







        public void ShowOptimizationResult(string type, string title, string subtitle, int optimized, int alreadyOptimized, int notAvailable, int notApplicable, int blocked, int failed, string primaryReason)







        {







            ResultType = type;







            ResultTitle = title;







            ResultSubtitle = subtitle;







            ResultOptimizedCount = optimized;







            ResultAlreadyOptimizedCount = alreadyOptimized;







            ResultNotAvailableCount = notAvailable;







            ResultNotApplicableCount = notApplicable;







            ResultBlockedCount = blocked;







            ResultFailedCount = failed;







            ResultPrimaryReason = primaryReason;







            ShowResultOverlay = true;







            _ = HideResultOverlayAsync();







        }







        







        private async Task HideToastAsync()







        {







            await Task.Delay(5000);







            ShowToast = false;







        }







        private async Task HideResultOverlayAsync()







        {







            await Task.Delay(5000);







            ShowResultOverlay = false;







        }







        // ------ Build IDs ------------------------------------------------------------------------------------------------------------------------------------------







        private string _guiBuildId = "Checking...";







        public string GuiBuildId { get => _guiBuildId; set { _guiBuildId = value; OnPropertyChanged(); } }



        public string ProcessPath => Environment.ProcessPath ?? AppContext.BaseDirectory;











        private string _serviceBuildId = "Connecting...";







        public string ServiceBuildId







        {







            get => _serviceBuildId;







            set







            {







                _serviceBuildId = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(IsVersionMismatch));







                OnPropertyChanged(nameof(StatusText));







                OnPropertyChanged(nameof(StatusBrush));







            }







        }







        // - Service connection state -



        // Four states: Connecting | Connected | Degraded | Offline



        public IpcConnectionState ConnectionState => _ipc.ConnectionState;







        private void SetConnectionState(IpcConnectionState state)



        {



            _ipc.SetConnectionState(state);



            OnPropertyChanged(nameof(ConnectionState));



            OnPropertyChanged(nameof(StatusText));



            OnPropertyChanged(nameof(StatusBrush));



        }







        public bool IsVersionMismatch



            => ConnectionState == IpcConnectionState.Connected



            && ServiceBuildId != "Connecting..."



            && ServiceBuildId != "Offline"



            && ServiceBuildId != "Protocol Mismatch"



            && !IpcClient.AreBuildIdsCompatible(GuiBuildId, ServiceBuildId);







        public string StatusText => IsVersionMismatch ? "Version mismatch" : _ipc.StatusText;







        public System.Windows.Media.Brush StatusBrush => IsVersionMismatch



            ? _ipc.StatusBrush



            : _ipc.StatusBrush;







        public MainViewModel(IIpcClient ipc)







        {







            _ipc = ipc;







            _viewFactory = new Dictionary<string, Func<ViewModelBase>>(StringComparer.OrdinalIgnoreCase)







            {







                ["Dashboard"] = () =>



                {



                    var dashVm = new DashboardViewModel(ipc);



                    dashVm.RequestNavigate += route => UiDispatcher.Run(() => _ = NavigateAsync(route));



                    return dashVm;



                },







                ["AutoOptimize"]   = () => new AutoOptimizeViewModel(ipc),







                ["Normal"]         = () => new TierViewModel(ipc, "Normal",             "Normal",          "Safe optimizations for everyday use."),







                ["Pro"]            = () => new TierViewModel(ipc, "Pro",                "Pro",             "Advanced optimizations for power users."),







                ["Ultimate"]       = () => new TierViewModel(ipc, "Ultimate",           "Ultimate",        "Maximum system overhaul. High risk."),







                ["Debloat"]        = () => new TierViewModel(ipc, "Debloat", "Debloat", "Removes telemetry, diagnostic tracking, and background Windows bloatware."),







                ["BiosSafe"]       = () => new TierViewModel(ipc, "BiosSafe", "BIOS Safe", "Safe firmware-aware analysis and UEFI hardware optimization."),







                ["CoreOptimization"] = () => new MaxPerformanceViewModel(ipc),
                ["V4Dashboard"] = () => new MaxPerformanceViewModel(ipc),



                ["V4"] = () => new MaxPerformanceViewModel(ipc),







                ["PerformanceTuning"] = () =>



                {



                    var vm = new PerformanceTuningViewModel(ipc);



                    vm.RequestNavigate += route => UiDispatcher.Run(() => _ = NavigateAsync(route));



                    return vm;



                },



                ["Tuning"] = () =>



                {



                    var vm = new PerformanceTuningViewModel(ipc);



                    vm.RequestNavigate += route => UiDispatcher.Run(() => _ = NavigateAsync(route));



                    return vm;



                },







                // V3 Classic Suite Profile: Maximum Performance (TierViewModel -> TierView.xaml)

                ["MaxPerformance"] = () => new TierViewModel(ipc, "MaximumPerformance", "Maximum Performance", "Extreme optimizations for gaming and rendering."),



                ["MaximumPerformance"] = () => new TierViewModel(ipc, "MaximumPerformance", "Maximum Performance", "Extreme optimizations for gaming and rendering."),



                ["V3MaxPerformance"] = () => new TierViewModel(ipc, "MaximumPerformance", "Maximum Performance", "Extreme optimizations for gaming and rendering."),



                // V4 Professional Suite: Maximum Performance (MaxPerformanceViewModel -> MaxPerformanceView.xaml)

                ["V4MaxPerformance"] = () => new MaxPerformanceViewModel(ipc),
["AiOptimization"] = () => new AiOptimizationViewModel(ipc),







                ["SystemInfo"]     = () => new SystemInfoViewModel(ipc),



                ["Telemetry"]      = () => new SystemInfoViewModel(ipc),



                ["SystemTelemetry"] = () => new SystemInfoViewModel(ipc),







                ["StartupManager"] = () => new StartupManagerViewModel(ipc),













                ["Network"]        = () => new NetworkViewModel(ipc),







                ["Storage"]        = () => new StorageViewModel(ipc),







                ["Tools"]          = () => new ToolsViewModel(ipc),







                ["BackupRestore"]  = () => new BackupRestoreViewModel(ipc),







                ["Settings"]       = () => new SettingsViewModel(ipc),







                ["About"]          = () => new AboutViewModel(ipc),













                ["OneClick"]       = () =>



                {



                    var vm = new OneClickOptimizationViewModel(ipc);



                    vm.RequestNavigate += route => UiDispatcher.Run(() => _ = NavigateAsync(route));



                    return vm;



                },







                ["InputOptimizer"] = () => new InputOptimizerViewModel(ipc), 



                ["RegistryTweak"]  = () => new RegistryTweakViewModel(ipc),



                ["RegistryValues"] = () => new RegistryValuesViewModel(ipc),



                ["RegistryValuesRam"] = () => new RegistryValuesViewModel(ipc),



                ["RegistryValuesGpu"] = () => new RegistryValuesViewModel(ipc),



                ["GpuRegistryValues"] = () => new RegistryValuesViewModel(ipc),



                ["AiRamLimiter"]   = () => new AiOptimizationViewModel(ipc),



                ["AiWorkload"]     = () => new AiOptimizationViewModel(ipc),



                ["AiPowerPlan"]    = () => new AiOptimizationViewModel(ipc),



            };







            _viewCache = new Dictionary<string, ViewModelBase>(StringComparer.OrdinalIgnoreCase);







            NavigateCommand = new RelayCommand(async route => await NavigateAsync(route?.ToString() ?? (IsV4Mode ? "V4MaxPerformance" : "Dashboard")));







            SwitchToV3Command = new RelayCommand(async _ =>



            {



                IsV4Mode = false;



                await NavigateAsync(_lastV3Route ?? "Dashboard");



            });







            SwitchToV4Command = new RelayCommand(async _ =>
            {
                IsV4Mode = true;
                if (string.IsNullOrEmpty(_lastV4Route) ||
                    _lastV4Route.Equals("RegistryTweak", StringComparison.OrdinalIgnoreCase) ||
                    _lastV4Route.Equals("RegistryValues", StringComparison.OrdinalIgnoreCase))
                {
                    _lastV4Route = "V4MaxPerformance";
                }
                await NavigateAsync(_lastV4Route);
            });







            SwitchToEmulatorCommand = new RelayCommand(async _ => { IsV4Mode = true; await NavigateAsync("V4Dashboard"); });







            RetryServiceCommand = new RelayCommand(async _ => await TriggerManualServiceRetryAsync());







            // Global error handler for Dispatcher exceptions (preventing blank pages)



            _showGlobalErrorAction = (ex) => 



            {



                string targetRoute = IsV4Mode ? "V4MaxPerformance" : "Dashboard";



                string fullDetails = ex.InnerException != null 



                    ? $"{ex.Message}\n\nInner Exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n\n{ex.StackTrace}"



                    : $"{ex.Message}\n\n{ex.StackTrace}";







                var errorVm = new ErrorViewModel(



                    error: $"UI Rendering Error: {ex.GetType().Name} - {ex.Message}",



                    stack: fullDetails,



                    retryAction: () => NavigateCommand.Execute(targetRoute)



                );



                



                // We MUST set it safely via UiDispatcher



                UiDispatcher.Run(() => 



                {



                    CurrentView = errorVm;



                    NavigationChanged?.Invoke("Error");



                });



            };







            // Resolve GUI build ID from assembly attributes



            var attr = System.Reflection.Assembly.GetExecutingAssembly()



                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);







            GuiBuildId = attr != null && attr.Length > 0



                ? ((System.Reflection.AssemblyInformationalVersionAttribute)attr[0]).InformationalVersion



                : System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";







            // - AUTHORITATIVE STARTUP HANDSHAKE -



            // We do NOT mark "Service Connected" until WaitForReadyAsync



            // completes a real GetVersion round-trip. This prevents the



            // "Connected but timeout" bug.



            _ = StartupHandshakeAsync();







            // Subscribe to authoritative connection state changes



            _ipc.ConnectionStateChanged += state =>



            {



                UiDispatcher.Run(async () =>



                {



                    OnPropertyChanged(nameof(ConnectionState));



                    OnPropertyChanged(nameof(StatusText));



                    OnPropertyChanged(nameof(StatusBrush));



                    if (state == IpcConnectionState.Connected && CurrentView != null)



                    {



                        try { await CurrentView.OnNavigatedToAsync(); } catch { }



                    }



                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();



                });



            };







            // Periodic health check every 5 s to detect backend crashes/restarts



            _healthTimer = new System.Windows.Threading.DispatcherTimer



            {



                Interval = TimeSpan.FromSeconds(5)



            };







            _healthTimer.Tick += async (s, e) => await CheckVersionAsync();



            _healthTimer.Start();







            BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogStartupPhaseStart("NAVIGATION INITIALIZATION");



            BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 06", "Navigation initialized");



            BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("NAVIGATION INITIALIZATION");



        }







        private readonly System.Threading.SemaphoreSlim _navLock = new(1, 1);







        public async Task NavigateAsync(string key)



        {



            if (!await _navLock.WaitAsync(250))



            {



                System.Diagnostics.Debug.WriteLine($"[NAV] Discarding re-entrant navigation click: {key}");



                return;



            }







            try



            {



                int colonIndex = key.IndexOf(':');



                string baseRoute = colonIndex >= 0 ? key.Substring(0, colonIndex).Trim() : key.Trim();



                string sectionKey = colonIndex >= 0 ? key.Substring(colonIndex + 1).Trim() : string.Empty;







                // ── Version-Isolated Route Normalization ──

                if (baseRoute.Equals("V4Dashboard", StringComparison.OrdinalIgnoreCase) ||

                    baseRoute.Equals("V4", StringComparison.OrdinalIgnoreCase) ||

                    baseRoute.Equals("V4MaxPerformance", StringComparison.OrdinalIgnoreCase))

                {

                    baseRoute = "V4MaxPerformance";

                    IsV4Mode = true;

                    _lastV4Route = "V4MaxPerformance";

                }

                else if (baseRoute.Equals("V3MaxPerformance", StringComparison.OrdinalIgnoreCase))

                {

                    baseRoute = "MaxPerformance";

                    IsV3Mode = true;

                    _lastV3Route = "MaxPerformance";

                }

                else if (baseRoute.Equals("MaxPerformance", StringComparison.OrdinalIgnoreCase) ||

                         baseRoute.Equals("MaximumPerformance", StringComparison.OrdinalIgnoreCase))

                {

                    if (IsV4Mode)

                    {

                        baseRoute = "V4MaxPerformance";

                        _lastV4Route = "V4MaxPerformance";

                    }

                    else

                    {

                        baseRoute = "MaxPerformance";

                        IsV3Mode = true;

                        _lastV3Route = "MaxPerformance";

                    }

                }



                if (baseRoute.Equals("ProcessReduction", StringComparison.OrdinalIgnoreCase) ||

                    baseRoute.Equals("Processes", StringComparison.OrdinalIgnoreCase) ||

                    baseRoute.Equals("ProcessIntelligence", StringComparison.OrdinalIgnoreCase) ||

                    baseRoute.Equals("ServiceManager", StringComparison.OrdinalIgnoreCase))

                {

                    baseRoute = "Dashboard";

                }



                System.Diagnostics.Debug.WriteLine($"[NAV] Click Route: {key} (BaseRoute={baseRoute}, Section={sectionKey})");

                BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.RecordNavigation(key);



                if (baseRoute.Equals("V4MaxPerformance", StringComparison.OrdinalIgnoreCase) ||false)

                {



                    IsV4Mode = true;



                    _lastV4Route = baseRoute;



                }



                else if (baseRoute.Equals("RegistryTweak", StringComparison.OrdinalIgnoreCase) ||
                         baseRoute.Equals("RegistryValues", StringComparison.OrdinalIgnoreCase))
                {
                    // Registry optimization belongs strictly to V3 Classic Suite
                    IsV3Mode = true;
                    _lastV3Route = baseRoute;
                }
                else if (!baseRoute.Equals("About", StringComparison.OrdinalIgnoreCase) &&



                         !baseRoute.Equals("Settings", StringComparison.OrdinalIgnoreCase))



                {



                    IsV3Mode = true;



                    _lastV3Route = baseRoute;



                }







                if (baseRoute.Equals("Dashboard", StringComparison.OrdinalIgnoreCase))



                {



                    BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogStartupPhaseStart("DASHBOARD INITIALIZATION");



                    BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogStartupStage("STARTUP 07", "Dashboard shell initialized");



                    BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogStartupPhaseSuccess("DASHBOARD INITIALIZATION");



                }







                ViewModelBase? vm = null;







                if (baseRoute.Equals("RegistryValuesRam", StringComparison.OrdinalIgnoreCase))



                {



                    if (!_viewCache.TryGetValue("RegistryValues", out vm))



                    {



                        vm = _viewFactory["RegistryValues"]();



                        _viewCache["RegistryValues"] = vm;



                    }



                    if (vm is RegistryValuesViewModel rv)



                    {



                        rv.SelectRamTabCommand.Execute(null);



                    }



                }



                else if (baseRoute.Equals("RegistryValuesGpu", StringComparison.OrdinalIgnoreCase) ||



                         baseRoute.Equals("GpuRegistryValues", StringComparison.OrdinalIgnoreCase))



                {



                    if (!_viewCache.TryGetValue("RegistryValues", out vm))



                    {



                        vm = _viewFactory["RegistryValues"]();



                        _viewCache["RegistryValues"] = vm;



                    }



                    if (vm is RegistryValuesViewModel rv)



                    {



                        rv.SelectGpuTabCommand.Execute(null);



                    }



                }



                else if (baseRoute.Equals("AiRamLimiter", StringComparison.OrdinalIgnoreCase))



                {



                    if (!_viewCache.TryGetValue("AiOptimization", out vm))



                    {



                        vm = _viewFactory["AiOptimization"]();



                        _viewCache["AiOptimization"] = vm;



                    }



                    if (vm is AiOptimizationViewModel ai)



                    {



                        ai.SelectRamLimiterTabCommand.Execute(null);



                    }



                }



                else if (baseRoute.Equals("AiWorkload", StringComparison.OrdinalIgnoreCase))



                {



                    if (!_viewCache.TryGetValue("AiOptimization", out vm))



                    {



                        vm = _viewFactory["AiOptimization"]();



                        _viewCache["AiOptimization"] = vm;



                    }



                    if (vm is AiOptimizationViewModel ai)



                    {



                        ai.SelectWorkloadTabCommand.Execute(null);



                        if (sectionKey.Equals("GAMES", StringComparison.OrdinalIgnoreCase) ||



                            sectionKey.Equals("GAMING", StringComparison.OrdinalIgnoreCase))



                        {



                            ai.SetCategoryGamesCommand.Execute(null);



                        }



                        else if (sectionKey.Equals("CREATIVE", StringComparison.OrdinalIgnoreCase) ||



                                 sectionKey.Equals("EDITING", StringComparison.OrdinalIgnoreCase))



                        {



                            ai.SetCategoryCreativeCommand.Execute(null);



                        }



                        else if (sectionKey.Equals("RECORDING", StringComparison.OrdinalIgnoreCase) ||



                                 sectionKey.Equals("STREAMING", StringComparison.OrdinalIgnoreCase))



                        {



                            ai.SetCategoryRecordingCommand.Execute(null);



                        }



                        else if (sectionKey.Equals("EMULATORS", StringComparison.OrdinalIgnoreCase) ||



                                 sectionKey.Equals("EMULATOR", StringComparison.OrdinalIgnoreCase))



                        {



                            ai.SetCategoryEmulatorsCommand.Execute(null);



                        }



                        else if (sectionKey.Equals("VIDEO_AI", StringComparison.OrdinalIgnoreCase))



                        {



                            ai.SetCategoryVideoAiCommand.Execute(null);



                        }



                    }



                }



                else if (baseRoute.Equals("AiPowerPlan", StringComparison.OrdinalIgnoreCase))



                {



                    if (!_viewCache.TryGetValue("AiOptimization", out vm))



                    {



                        vm = _viewFactory["AiOptimization"]();



                        _viewCache["AiOptimization"] = vm;



                    }



                    if (vm is AiOptimizationViewModel ai)



                    {



                        ai.SelectPowerPlanTabCommand.Execute(null);



                    }



                }



                else if (baseRoute.Equals("OneClick", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sectionKey))



                {



                    if (!_viewCache.TryGetValue("OneClick", out vm))



                    {



                        vm = _viewFactory["OneClick"]();



                        _viewCache["OneClick"] = vm;



                    }



                    if (vm is OneClickOptimizationViewModel oneClickVm)



                    {



                        var suffix = sectionKey;



                        bool isAuditMode = false;



                        if (suffix.StartsWith("AUDIT:", StringComparison.OrdinalIgnoreCase))



                        {



                            isAuditMode = true;



                            suffix = suffix.Substring("AUDIT:".Length).Trim();



                        }



                        oneClickVm.ApplyWorkloadShortcut(suffix.ToUpperInvariant(), isAuditMode);



                    }



                }



                else if (



                         baseRoute.Equals("FreeFireOptimize", StringComparison.OrdinalIgnoreCase))



                {



                    if (!_viewCache.TryGetValue("EmulatorDeepOptimize", out vm))



                    {



                        vm = _viewFactory["EmulatorDeepOptimize"]();



                        _viewCache["EmulatorDeepOptimize"] = vm;



                    }



                    _viewCache[baseRoute] = vm;



                }



                else if (_viewFactory.TryGetValue(baseRoute, out var factory))



                {



                    if (!_viewCache.TryGetValue(baseRoute, out vm))



                    {



                        vm = factory();



                        _viewCache[baseRoute] = vm;



                    }



                    if (baseRoute.Equals("OneClick", StringComparison.OrdinalIgnoreCase) && vm is OneClickOptimizationViewModel oneClickDefaultVm)



                    {



                        oneClickDefaultVm.ApplyWorkloadShortcut("", false);



                    }





                    else if (vm is MaxPerformanceViewModel mpVm &&



                             (sectionKey.Equals("AUDIT", StringComparison.OrdinalIgnoreCase) ||



                              sectionKey.Equals("DETAILS", StringComparison.OrdinalIgnoreCase)))



                    {



                        mpVm.ShowAdvancedDetails = true;



                    }



                }







                if (vm != null)



                {



                    if (string.IsNullOrEmpty(sectionKey) && ReferenceEquals(CurrentView, vm))



                    {



                        // Target view is already active; avoid redundant transitions or teardown



                        NavigationChanged?.Invoke(baseRoute);



                        return;



                    }



                    var oldView = CurrentView;



                    if (oldView != null)



                    {



                        try



                        {



                            await oldView.OnNavigatedFromAsync();



                        }



                        catch { }



                    }







                    CurrentView = vm;



                    System.Diagnostics.Debug.WriteLine($"[NAVIGATION] Route = {key} (BaseRoute={baseRoute}) | ViewModel = {vm?.GetType().Name} | Build = {GuiBuildId}");



                    NavigationChanged?.Invoke(baseRoute);







                    try



                    {



                        await vm.OnNavigatedToAsync();



                    }



                    catch (Exception navToEx)



                    {



                        System.Diagnostics.Debug.WriteLine($"[NAVIGATION WARNING] OnNavigatedToAsync threw for {key}: {navToEx.Message}");



                        BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogCrash(navToEx, $"Navigation.{key}.OnNavigatedTo", isFatal: false);



                    }



                }



                else



                {



                    System.Diagnostics.Debug.WriteLine($"[NAV] Route '{key}' failed to resolve!");



                }



            }



            catch (Exception ex)



            {



                var innerMsg = ex.InnerException != null ? $" | Inner: [{ex.InnerException.GetType().Name}] {ex.InnerException.Message}" : "";



                var logDetail = $"[NAVIGATION ERROR] Route='{key}' | ExType='{ex.GetType().FullName}' | Msg='{ex.Message}'{innerMsg}";



                System.Diagnostics.Debug.WriteLine(logDetail);



                BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogCrash(ex, $"Navigation.{key}", isFatal: false);







                var oldView = CurrentView;



                if (oldView != null)



                {



                    try { await oldView.OnNavigatedFromAsync(); } catch { }



                }







                var fullDetail = $"{ex.GetType().Name}: {ex.Message}";



                if (ex.InnerException != null)



                {



                    fullDetail += $"\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";



                }



                fullDetail += $"\n\nStack Trace:\n{ex.StackTrace}";







                CurrentView = new ErrorViewModel(



                    error: $"Page Load Error: {key}",



                    stack: fullDetail,



                    retryAction: () => NavigateCommand.Execute(key)



                );



                NavigationChanged?.Invoke("Error");



            }



            finally



            {



                _navLock.Release();



            }



        }







        /// <summary>







        /// Blocks until the backend is provably ready (max 30 s), then







        /// sets the status to Connected/Offline and navigates to Dashboard.







        /// </summary>







        private async Task StartupHandshakeAsync()



        {



            SetConnectionState(IpcConnectionState.Connecting);







            // 15s is enough - if service doesn't respond, show DEGRADED (not Offline)



            // so pages are usable even without service.



            bool ready = await _ipc.WaitForReadyAsync(TimeSpan.FromSeconds(15));







            if (ready)



            {



                ServiceBuildId = _ipc.ServiceBuildId;



                SetConnectionState(IpcConnectionState.Connected);







                if (IsVersionMismatch)



                {



                    await HandleVersionMismatch();



                    return;



                }



            }



            else



            {



                // DEGRADED - pages still navigate, service features show offline state



                ServiceBuildId = "Degraded";



                SetConnectionState(IpcConnectionState.Degraded);



            }



        }







        /// <summary>



        /// Periodic health check - updates status without blocking navigation.



        /// Uses exponential backoff (5s up to 30s) when service is offline/degraded.



        /// </summary>



        private int _failedHealthChecks = 0;



        private int _retryBackoffSeconds = 5;







        public async Task TriggerManualServiceRetryAsync()



        {



            UiLogger.Log("MainViewModel: Manual service retry triggered by user.");



            _retryBackoffSeconds = 5;



            _failedHealthChecks = 0;



            _healthTimer.Interval = TimeSpan.FromSeconds(5);







            SetConnectionState(IpcConnectionState.Connecting);







            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



            {



                if (System.Windows.Application.Current is App app)



                    app.RestartBackend();



            });







            bool ready = await _ipc.WaitForReadyAsync(TimeSpan.FromSeconds(10));



            if (ready)



            {



                ServiceBuildId = _ipc.ServiceBuildId;



                SetConnectionState(IpcConnectionState.Connected);



                UiLogger.Log($"Manual service retry succeeded. ServiceBuildId: '{ServiceBuildId}'");



            }



            else



            {



                ServiceBuildId = "Offline";



                SetConnectionState(IpcConnectionState.Offline);



                UiLogger.Log("Manual service retry failed to connect to backend.");



            }



        }







        private void ApplyExponentialBackoff()



        {



            _retryBackoffSeconds = Math.Min(_retryBackoffSeconds == 5 ? 10 : (_retryBackoffSeconds >= 20 ? 30 : 20), 30);



            _healthTimer.Interval = TimeSpan.FromSeconds(_retryBackoffSeconds);



            UiLogger.Log($"[RETRY_BACKOFF] Service unavailable. Next health probe in {_retryBackoffSeconds}s.");



        }







        private async Task CheckVersionAsync()



        {



            var res = await _ipc.SendRequestAsync(BiosOptimizer.IPC.Contracts.IpcMessageType.GetVersion, "");



            if (res.Success && !string.IsNullOrEmpty(res.Data))



            {



                try



                {



                    var handshake = System.Text.Json.JsonSerializer



                        .Deserialize<BiosOptimizer.IPC.Contracts.IpcHandshakeResponse>(res.Data);



                    if (handshake?.ProtocolVersion == "v2")



                    {



                        _failedHealthChecks = 0;



                        _retryBackoffSeconds = 5;



                        _healthTimer.Interval = TimeSpan.FromSeconds(5);



                        ServiceBuildId = handshake.BuildId;



                        SetConnectionState(IpcConnectionState.Connected);



                        if (IsVersionMismatch)



                            await HandleVersionMismatch();



                    }



                    else



                    {



                        ServiceBuildId = "Protocol Mismatch";



                        SetConnectionState(IpcConnectionState.Offline);



                        ApplyExponentialBackoff();



                    }



                }



                catch (Exception ex)



                {



                    UiLogger.LogError("Health check handshake parsing error", ex);



                    _failedHealthChecks++;



                    if (_failedHealthChecks >= 3)



                    {



                        ServiceBuildId = "Offline";



                        SetConnectionState(IpcConnectionState.Offline);



                    }



                    else



                    {



                        SetConnectionState(IpcConnectionState.Degraded);



                    }



                    ApplyExponentialBackoff();



                }



            }



            else



            {



                _failedHealthChecks++;



                if (_failedHealthChecks >= 3)



                {



                    ServiceBuildId = "Offline";



                    SetConnectionState(IpcConnectionState.Offline);



                }



                else



                {



                    SetConnectionState(IpcConnectionState.Degraded);



                }



                ApplyExponentialBackoff();



            }



        }







        private async Task HandleVersionMismatch()



        {



            ServiceBuildId = "Application components are out of sync. Restarting backend...";



            UiLogger.Log("Version mismatch detected. Initiating backend restart...");







            SetConnectionState(IpcConnectionState.Connecting);







            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



            {



                if (System.Windows.Application.Current is App app)



                    app.RestartBackend();



            });







            // Re-poll after restart



            bool ready = await _ipc.WaitForReadyAsync(TimeSpan.FromSeconds(20));



            if (ready)



            {



                ServiceBuildId = _ipc.ServiceBuildId;



                SetConnectionState(IpcConnectionState.Connected);



                UiLogger.Log($"Backend restart complete. ServiceBuildId: '{ServiceBuildId}'");



            }



            else



            {



                ServiceBuildId = "Offline";



                SetConnectionState(IpcConnectionState.Offline);



                UiLogger.Log("Backend restart failed to establish connected state.");



            }



        }







    }







}







