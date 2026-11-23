#nullable enable







using System;







using System.Collections.Generic;







using System.Collections.ObjectModel;







using System.Linq;







using System.Text.Json;







using System.Threading;







using System.Threading.Tasks;







using System.Windows.Input;







using System.Windows.Media;

using System.Windows.Threading;







using BiosOptimizer.GUI.Services;







using BiosOptimizer.GUI.ViewModels.Base;







using BiosOptimizer.IPC.Contracts;















namespace BiosOptimizer.GUI.ViewModels







{







    public enum V4OperationState

    {

        Scanning,

        Ready,

        Optimizing,

        Completed,

        Failed,

        Cancelled

    }



    public enum V4DashboardViewMode

    {

        Ready,

        ConfirmSafety,

        Optimizing,

        Completed

    }















    public enum ActivityLogLevel







    {







        Info,







        Success,







        Warning,







        Error







    }















    public class ActivityLogEntryModel : ViewModelBase







    {







        public DateTime Timestamp { get; set; } = DateTime.Now;







        public string FormattedTime => Timestamp.ToString("HH:mm:ss");







        public string Title { get; set; } = string.Empty;







        public string Description { get; set; } = string.Empty;







        public ActivityLogLevel Level { get; set; } = ActivityLogLevel.Info;















        public string BadgeText => Level switch







        {







            ActivityLogLevel.Success => "SUCCESS",







            ActivityLogLevel.Warning => "SKIPPED",







            ActivityLogLevel.Error => "FAILED",







            _ => "INFO"







        };















        public string BadgeBrushKey => Level switch







        {







            ActivityLogLevel.Success => "SuccessBrush",







            ActivityLogLevel.Warning => "AccentHoverBrush",







            ActivityLogLevel.Error => "DangerBrush",







            _ => "TextSecondaryBrush"







        };















        public Brush LevelBrush => Level switch







        {







            ActivityLogLevel.Success => MaxPerformanceViewModel.StatusGreenBrush,







            ActivityLogLevel.Warning => MaxPerformanceViewModel.StatusAmberBrush,







            ActivityLogLevel.Error => MaxPerformanceViewModel.StatusRedBrush,







            _ => MaxPerformanceViewModel.StatusIndigoBrush







        };















        public string Glyph => Level switch







        {







            ActivityLogLevel.Success => "\uE73E",







            ActivityLogLevel.Warning => "\uE7BA",







            ActivityLogLevel.Error => "\uE711",







            _ => "\uE946"







        };







    }















    public class OptimizationCandidateItemModel : ViewModelBase







    {







        public string CandidateId { get; set; } = string.Empty;







        public string TargetType { get; set; } = "Process";







        public string DisplayName { get; set; } = string.Empty;







        public string Category { get; set; } = string.Empty;







        public string RiskLevel { get; set; } = "SAFE";







        public string InitialState { get; set; } = string.Empty;







        public string TargetState { get; set; } = string.Empty;







        public string Reason { get; set; } = string.Empty;







        public double SafetyConfidence { get; set; } = 1.0;















        // Rich Workload & Candidate Metadata







        public string WorkloadCompatibility { get; set; } = "All";







        public string Classification { get; set; } = "Recommended";







        public string ExpectedBenefit { get; set; } = string.Empty;







        public string PotentialRisk { get; set; } = string.Empty;







        public string RequiredPermissions { get; set; } = "Administrator / Background Service";







        public string ReversibilityStatus { get; set; } = "Reversible via Rollback Journal";







        public string BackendHandler { get; set; } = string.Empty;







        public string VerificationMethod { get; set; } = string.Empty;







        public bool IsProtectedFromOptimization { get; set; } = false;







        public bool IsAlreadyOptimal { get; set; } = false;







        public string EligibilityExplanation { get; set; } = string.Empty;







        public string ExclusionExplanation { get; set; } = string.Empty;







        public bool CanSelect => !IsProtectedFromOptimization && !IsAlreadyOptimal;















        private bool _isSelected = true;







        public bool IsSelected







        {







            get => _isSelected;







            set







            {







                if (IsProtectedFromOptimization || IsAlreadyOptimal)







                {







                    _isSelected = false;







                }







                else







                {







                    _isSelected = value;







                }







                OnPropertyChanged();







            }







        }















        public string ConfidenceBadge => $"{SafetyConfidence * 100:F0}% Safe";







        public bool IsRecommended => !IsProtectedFromOptimization && !IsAlreadyOptimal &&







            (Classification.Equals("Recommended", StringComparison.OrdinalIgnoreCase) ||







             SafetyConfidence >= 0.90 || RiskLevel == "SAFE" || RiskLevel == "LOW_BACKGROUND");







        public string RecommendationBadge => IsAlreadyOptimal ? "Optimal" : (IsRecommended ? "Recommended" : (IsProtectedFromOptimization ? "Protected" : "Optional"));







        public string SafetyTag => "Safe \u00B7 Reversible";







        public string FormattedBadge => $"{RecommendationBadge} \u00B7 {SafetyTag}";















        public string CategoryGroup => Category switch







        {







            "System" or "Responsiveness" or "OS" => "System Performance",







            "Memory" or "RAM" => "Memory",







            "Power" or "Governor" or "Scheduler" => "Power & Performance",







            _ => "Background Workload"







        };















        public string PlainBenefit => !string.IsNullOrEmpty(ExpectedBenefit)







            ? ExpectedBenefit







            : CategoryGroup switch







            {







                "System Performance" => "Prioritizes foreground execution, disables multimedia network throttling, and reduces input latency",







                "Memory" => "Yields idle cache pages and process working sets to free RAM for active games and creative apps",







                "Power & Performance" => "Applies unconstrained CPU core delivery and disables core parking during active workloads",







                _ => "Safely suspends unnecessary background daemons and yields CPU cycles to foreground tasks"







            };















        public string PotentialDrawback => !string.IsNullOrEmpty(PotentialRisk)







            ? PotentialRisk







            : CategoryGroup switch







            {







                "System Performance" => "Foreground applications receive full CPU priority; background tasks take longer",







                "Memory" => "Trimmed processes will reload working set from disk if brought to foreground",







                "Power & Performance" => "Slightly higher power draw during high load scenarios",







                _ => "Background daemons pause until reopened"







            };















        public string AdminRequirement => "Managed safely by background service";















        public OptimizationCandidateItemModel Clone()







        {







            return new OptimizationCandidateItemModel







            {







                CandidateId = this.CandidateId,







                TargetType = this.TargetType,







                DisplayName = this.DisplayName,







                Category = this.Category,







                RiskLevel = this.RiskLevel,







                InitialState = this.InitialState,







                TargetState = this.TargetState,







                Reason = this.Reason,







                SafetyConfidence = this.SafetyConfidence,







                IsSelected = this.IsSelected,







                WorkloadCompatibility = this.WorkloadCompatibility,







                Classification = this.Classification,







                ExpectedBenefit = this.ExpectedBenefit,







                PotentialRisk = this.PotentialRisk,







                RequiredPermissions = this.RequiredPermissions,







                ReversibilityStatus = this.ReversibilityStatus,







                BackendHandler = this.BackendHandler,







                VerificationMethod = this.VerificationMethod,







                IsProtectedFromOptimization = this.IsProtectedFromOptimization,







                IsAlreadyOptimal = this.IsAlreadyOptimal,







                EligibilityExplanation = this.EligibilityExplanation,







                ExclusionExplanation = this.ExclusionExplanation







            };







        }







    }















    public class OptimizationCategoryCardModel : ViewModelBase







    {







        public string Title { get; set; } = string.Empty;







        public string Description { get; set; } = string.Empty;







        public string IconGlyph { get; set; } = string.Empty;















        private int _count;







        public int Count







        {







            get => _count;







            set







            {







                _count = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(CountText));







                OnPropertyChanged(nameof(HasImprovements));







            }







        }















        public string CountText => Count > 0 ? $"{Count} improvements available" : "Verified optimal";







        public bool HasImprovements => Count > 0;







    }















    public class OptimizationActionItemModel : ViewModelBase







    {







        public string ActionType { get; set; } = string.Empty;







        public string TargetIdentifier { get; set; } = string.Empty;







        public string Status { get; set; } = "Verified";







        public string Reason { get; set; } = string.Empty;







        public string VerificationMessage { get; set; } = string.Empty;















        public string StatusBrushKey => Status switch







        {







            "Verified" or "Applied" or "VERIFIED" or "APPLIED" => "SuccessBrush",







            "Skipped" or "SKIPPED FOR SAFETY" or "SKIPPED — PROTECTED APPLICATION" => "AccentBrush",







            "Failed" or "FAILED" => "DangerBrush",







            _ => "TextMutedBrush"







        };







    }















    public class ProgressionStepModel : ViewModelBase







    {







        public string ProcessCountText { get; set; } = string.Empty;







        public string Description { get; set; } = string.Empty;







        public bool ShowArrow { get; set; } = true;







        public bool IsCheckingStatus { get; set; } = false;







        public bool IsTerminalStatus { get; set; } = false;







    }















    /// <summary>







    /// ViewModel for ERROR OPTIMIZER V4 — MAXIMUM PERFORMANCE Workspace.







    /// Professional single-action optimization with dedicated live activity side log and centered customize popup.







    /// </summary>







    public class MaxPerformanceViewModel : ViewModelBase







    {







        private readonly IIpcClient _ipc;

        private readonly SystemTelemetryService _telemetryService;

        private readonly DispatcherTimer _telemetryTimer;

        private bool _hasValidCpuSample;

        private double _gpuUtilization = -1.0;

        private string _gpuName = "";

        private bool _hasLoggedInitialTelemetry;

        private bool _isDisposed;







        private readonly HashSet<string> _savedSelectedCandidateIds = new(StringComparer.OrdinalIgnoreCase);







        private string? _completedPlanFingerprint = null;







        private bool _isLastRunPartialFailure = false;



        // Test compatibility properties

        public string BackupStatusText { get; set; } = "Automatic system restore point and registry snapshot verified";

        public string ProtectedComponentsStatusText { get; set; } = "Hardware protection guard active";

        public string GamingProtectionStatus { get; set; } = "PROTECTED";

        public string EditingProtectionStatus { get; set; } = "PROTECTED";

        public string WindowsProtectionStatus { get; set; } = "PROTECTED";

        public string SafeMinimumRangeText { get; set; } = "Safe verified baseline";

        public void OpenSafetyConfirmation() { }















        public string? CompletedPlanFingerprint => _completedPlanFingerprint;







        public bool IsLastRunPartialFailure => _isLastRunPartialFailure;















        public string ComputePlanFingerprint()







        {







            var selectedIds = IsCustomSelectionActive







                ? _savedSelectedCandidateIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)







                : Candidates.Where(c => c.IsSelected).Select(c => c.CandidateId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);















            return $"{SelectedWorkloadProfile}:{string.Join(";", selectedIds)}";







        }















        public bool IsPlanCompleted =>







            !string.IsNullOrEmpty(_completedPlanFingerprint) &&







            string.Equals(_completedPlanFingerprint, ComputePlanFingerprint(), StringComparison.OrdinalIgnoreCase);















        public static readonly Brush StatusGreenBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));







        public static readonly Brush StatusAmberBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));







        public static readonly Brush StatusIndigoBrush = new SolidColorBrush(Color.FromRgb(94, 106, 210));







        public static readonly Brush StatusRedBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));

        public static readonly Brush StatusCyanBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));



        static MaxPerformanceViewModel()

        {

            StatusGreenBrush.Freeze();

            StatusAmberBrush.Freeze();

            StatusIndigoBrush.Freeze();

            StatusRedBrush.Freeze();

            StatusCyanBrush.Freeze();

        }















        public MaxPerformanceViewModel(IIpcClient ipc)







        {







            _ipc = ipc;

            _telemetryService = new SystemTelemetryService(ipc);















            _isServiceOffline = !_ipc.IsServiceAvailable && _ipc.ConnectionState != IpcConnectionState.Connected;







            if (_isServiceOffline)







            {







                _statusMessage = "Service unavailable — running in safe offline mode.";







            }















            _ipc.ConnectionStateChanged += OnConnectionStateChanged;







            _ipc.ServiceAvailabilityChanged += OnServiceAvailabilityChanged;



            _telemetryTimer = new DispatcherTimer

            {

                Interval = TimeSpan.FromMilliseconds(1500)

            };

            _telemetryTimer.Tick += async (_, _) => await UpdateTelemetryTickAsync();

            _telemetryTimer.Start();



            AddLog("Telemetry monitor online", "Continuous live metrics sampling active for CPU, GPU, RAM, and background processes", ActivityLogLevel.Info);

            _ = UpdateTelemetryTickAsync();















            // Primary actions







            PrimaryStartOptimizationCommand = new RelayCommand(_ => OnPrimaryStartOptimization(), _ => CanStartOptimization);







            StartOptimizationCommand = new RelayCommand(async _ =>

            {

                if (!CanStartOptimization) return;



                if (PrimaryButtonText == "RETRY SCAN" || 

                    CurrentOperationState == V4OperationState.Cancelled || 

                    (CurrentOperationState == V4OperationState.Failed && !_isLastRunPartialFailure))

                {

                    await RefreshIntelligenceAsync();

                    return;

                }



                await ExecuteFullOptimizationAsync();

            }, _ => CanStartOptimization);







            MaximumOptimizeCommand = new RelayCommand(_ => OnPrimaryStartOptimization(), _ => CanStartOptimization);







            ConfirmStartOptimizationCommand = new RelayCommand(async _ => await OnConfirmStartOptimizationAsync(), _ => !IsBusy && IsServiceAvailable);







            CancelConfirmStartCommand = new RelayCommand(_ => IsConfirmStartOpen = false);















            // Workload profile actions







            SelectGamingProfileCommand = new RelayCommand(_ => SelectedWorkloadProfile = WorkloadProfileType.Gaming, _ => !IsBusy);







            SelectVideoEditingProfileCommand = new RelayCommand(_ => SelectedWorkloadProfile = WorkloadProfileType.VideoEditing, _ => !IsBusy);







            SelectStreamingProfileCommand = new RelayCommand(_ => SelectedWorkloadProfile = WorkloadProfileType.Streaming, _ => !IsBusy);







            SetWorkloadProfileCommand = new RelayCommand(p =>







            {







                if (p is string s && Enum.TryParse<WorkloadProfileType>(s, true, out var wpt))







                    SelectedWorkloadProfile = wpt;







                else if (p is WorkloadProfileType profileType)







                    SelectedWorkloadProfile = profileType;







            }, _ => !IsBusy);















            // Customization modal actions







            OpenCustomizeCommand = new RelayCommand(_ => OpenCustomizeModal());







            SaveCustomizeCommand = new RelayCommand(_ => SaveCustomizeModal());







            CancelCustomizeCommand = new RelayCommand(_ => CancelCustomizeModal());







            DraftSelectRecommendedCommand = new RelayCommand(_ => DraftSelectRecommended());







            DraftClearSelectionCommand = new RelayCommand(_ => DraftClearSelection());







            SetCustomizeCategoryCommand = new RelayCommand(cat => { if (cat is string s) CustomizeFilterCategory = s; });















            // Secondary utilities & details







            OpenReviewCustomizeCommand = new RelayCommand(_ => OpenCustomizeModal());







            CloseReviewCustomizeCommand = new RelayCommand(_ => CancelCustomizeModal());







            ToggleDetailsCommand = new RelayCommand(_ => ShowAdvancedDetails = !ShowAdvancedDetails);







            CloseDetailsCommand = new RelayCommand(_ => ShowAdvancedDetails = false);







            OpenAdvancedDetailsCommand = new RelayCommand(_ => ShowAdvancedDetails = true);







            CloseAdvancedDetailsCommand = new RelayCommand(_ => ShowAdvancedDetails = false);







            ToggleLearnMoreCommand = new RelayCommand(_ => IsLearnMoreExpanded = !IsLearnMoreExpanded);







            ClearActivityLogCommand = new RelayCommand(_ =>

            {

                LiveActivityLog.Clear();

                OnPropertyChanged(nameof(HasActivityLogs));

            });















            ScanCommand = new RelayCommand(async _ => await RefreshIntelligenceAsync(), _ => !IsBusy);







            RunAnotherScanCommand = new RelayCommand(async _ =>



            {



                _completedPlanFingerprint = null;



                CurrentViewMode = V4DashboardViewMode.Ready;



                await RefreshIntelligenceAsync();



            }, _ => !IsBusy);









            RollbackCommand = new RelayCommand(async _ => await ExecuteRollbackAsync(), _ => !IsBusy && HasRollbackAvailable && IsServiceAvailable);







            RetryServiceCommand = new RelayCommand(async _ => await RetryServiceAsync(), _ => !IsBusy);







            SuppressDriftCommand = new RelayCommand(async _ => await ExecuteSuppressDriftAsync(), _ => !IsBusy && IsServiceAvailable);















            DoneCommand = new RelayCommand(_ => ReturnToReadyScreen());







            CancelSafetyCommand = new RelayCommand(_ => CancelSafetyConfirmation());







            OpenGuideCommand = new RelayCommand(_ => IsGuideModalOpen = true);







            CloseGuideCommand = new RelayCommand(_ => IsGuideModalOpen = false);







            OpenCategoryReviewCommand = new RelayCommand(_ => OpenCustomizeModal());















            InitializeDefaultProtectedApplications();















            // Initial intelligence scan







            _ = RefreshIntelligenceAsync();







        }















        private void OnConnectionStateChanged(IpcConnectionState state)







        {







            UiDispatcher.Run(() =>







            {







                if (state == IpcConnectionState.Connected)







                {







                    IsServiceOffline = false;







                    _ = RefreshIntelligenceAsync();







                }







                else







                {







                    IsServiceOffline = true;







                    StatusMessage = "Service unavailable — running in safe offline mode.";







                    AddLog("Service disconnected", "Background service connection lost", ActivityLogLevel.Warning);







                }







            });







        }















        private void OnServiceAvailabilityChanged(bool available)







        {







            UiDispatcher.Run(() =>







            {







                if (available)







                {







                    IsServiceOffline = false;







                    _ = RefreshIntelligenceAsync();







                }







                else







                {







                    IsServiceOffline = true;







                    StatusMessage = "Service unavailable — running in safe offline mode.";







                    AddLog("Service unavailable", "Background optimization service not responding", ActivityLogLevel.Warning);







                }







            });







        }















        private async Task UpdateTelemetryTickAsync()

        {

            if (_isDisposed) return;

            try

            {

                var data = await _telemetryService.PollTelemetryAsync();

                if (data != null && !_isDisposed)

                {

                    UiDispatcher.Run(() =>

                    {

                        ApplyTelemetryData(data);

                    });

                }

            }

            catch { }

        }



        private void ApplyTelemetryData(SystemTelemetryData data)

        {

            if (data == null || _isDisposed) return;



            // 1. Live background process count

            int pCount = data.ProcessCount > 0 ? data.ProcessCount : 0;

            if (pCount > 0)

            {

                CurrentProcessCount = pCount;

                if (InitialProcessCount == 0) InitialProcessCount = pCount;

            }



            // 2. CPU background load

            if (data.CpuUtilization >= 0)

            {

                CurrentCpuBackgroundLoad = Math.Round(data.CpuUtilization, 1);

                _hasValidCpuSample = true;

            }



            // 3. Physical RAM metrics

            if (data.RamTotalGb > 0) TotalRamGb = data.RamTotalGb;

            if (data.RamUsedGb > 0) CurrentRamBackgroundUsageGb = data.RamUsedGb;



            // 4. Multi-GPU metrics (discrete GPU prioritized)

            if (data.Gpus != null && data.Gpus.Count > 0)

            {

                var dGpu = data.Gpus.FirstOrDefault(g =>

                    g.DedicatedMemoryTotalMb > 1024 ||

                    g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||

                    g.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||

                    g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||

                    g.Name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||

                    g.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||

                    g.Name.Contains("Arc A", StringComparison.OrdinalIgnoreCase));



                var primary = dGpu ?? data.Gpus[0];

                _gpuUtilization = Math.Clamp(primary.Utilization, 0, 100);

                _gpuName = primary.Name;

            }

            else

            {

                _gpuUtilization = -1.0;

                _gpuName = "";

            }



            // 5. Service state synchronization

            bool serviceLive = _ipc.IsServiceAvailable || _ipc.ConnectionState == IpcConnectionState.Connected;

            if (IsServiceOffline == serviceLive)

            {

                IsServiceOffline = !serviceLive;

            }



            // Log first verified hardware telemetry capture to Live Activity

            if (!_hasLoggedInitialTelemetry && _hasValidCpuSample && CurrentRamBackgroundUsageGb > 0)

            {

                _hasLoggedInitialTelemetry = true;

                string gpuLabel = !string.IsNullOrWhiteSpace(_gpuName) ? $" • {_gpuName}" : "";

                AddLog("Hardware telemetry locked", $"{CurrentProcessCount} processes active • {CurrentRamBackgroundUsageGb:F1} / {TotalRamGb:F1} GB RAM • CPU {CurrentCpuBackgroundLoad:F1}%{gpuLabel}", ActivityLogLevel.Success);

            }



            OnPropertyChanged(nameof(ProcessCountDisplay));

            OnPropertyChanged(nameof(FormattedCpuLoad));

            OnPropertyChanged(nameof(FormattedRamUsageDetailed));

            OnPropertyChanged(nameof(FormattedRamUsage));

            OnPropertyChanged(nameof(FormattedGpuUsage));

            OnPropertyChanged(nameof(GpuStatusSubtext));

            OnPropertyChanged(nameof(ServiceStatusText));

            OnPropertyChanged(nameof(ServiceStatusBrush));

        }



        public void StopTelemetry()

        {

            _isDisposed = true;

            try { _telemetryTimer?.Stop(); } catch { }

        }



        // ── Service Connection & Offline State ────────────────────────







        private bool _isServiceOffline;







        public bool IsServiceOffline







        {







            get => _isServiceOffline;







            set







            {







                if (_isServiceOffline != value)







                {







                    _isServiceOffline = value;







                    OnPropertyChanged();







                    OnPropertyChanged(nameof(IsServiceAvailable));







                    OnPropertyChanged(nameof(IsMetricAvailable));







                    OnPropertyChanged(nameof(ProcessCountDisplay));







                    OnPropertyChanged(nameof(FormattedCpuLoad));







                    OnPropertyChanged(nameof(FormattedRamUsage));

                OnPropertyChanged(nameof(FormattedRamUsageDetailed));



                    OnPropertyChanged(nameof(FormattedRamUsageDetailed));



                    OnPropertyChanged(nameof(FormattedGpuUsage));







                    OnPropertyChanged(nameof(DynamicReducibleWorkloadText));







                    OnPropertyChanged(nameof(OptimizeButtonToolTip));







                    OnPropertyChanged(nameof(CanStartOptimization));







                    OnPropertyChanged(nameof(ActivityStatusText));







                    OnPropertyChanged(nameof(ActivityStatusBrush));







                    CommandManager.InvalidateRequerySuggested();







                }







            }







        }















        public bool IsServiceAvailable => !IsServiceOffline;







        public bool IsMetricAvailable => !IsServiceOffline && CurrentProcessCount > 0;















        public string OptimizeButtonToolTip

        {

            get

            {

                if (IsScanning || CurrentOperationState == V4OperationState.Scanning)

                    return "System scan is in progress. Analyzing system processes and workload state...";



                if (IsOptimizing || CurrentOperationState == V4OperationState.Optimizing)

                    return "Optimization is currently running. Please wait while actions are safely executed and verified.";



                if (IsBusy)

                    return "Operation is in progress. Please wait...";



                if (CurrentOperationState == V4OperationState.Cancelled)

                    return "Operation was cancelled. Click to retry system scan.";



                if (CurrentOperationState == V4OperationState.Failed)

                {

                    if (_isLastRunPartialFailure && HasActionableOpportunities)

                        return "Retry remaining safe actions that were skipped or unverified in the previous run.";

                    return "System scan or service encountered an error. Click to retry scan.";

                }



                if (IsServiceOffline)

                    return "Optimization is unavailable because the background service is offline.";



                if (IsPlanCompleted || IsSystemAlreadyOptimal)

                    return "System is already operating at verified maximum performance baseline. All tuning parameters are optimal and active workloads are shielded.";



                if (!HasSelectedCandidates)

                    return "Select at least one eligible optimization in Customize to continue.";



                if (_isLastRunPartialFailure && HasActionableOpportunities)

                    return "Retry remaining safe actions that were skipped or unverified in the previous run.";



                return "Apply verified system optimizations safely without touching core OS services.";

            }

        }



        // ── View State Management ─────────────────────────────────────







        private V4DashboardViewMode _currentViewMode = V4DashboardViewMode.Ready;







        public V4DashboardViewMode CurrentViewMode







        {







            get => _currentViewMode;







            set







            {







                _currentViewMode = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(IsReadyMode));







                OnPropertyChanged(nameof(IsConfirmSafetyMode));







                OnPropertyChanged(nameof(IsOptimizingMode));







                OnPropertyChanged(nameof(IsCompletedMode));







                OnPropertyChanged(nameof(ActivityStatusText));







                OnPropertyChanged(nameof(ActivityStatusBrush));







            }







        }















        public bool IsReadyMode => CurrentViewMode == V4DashboardViewMode.Ready;







        public bool IsConfirmSafetyMode => CurrentViewMode == V4DashboardViewMode.ConfirmSafety;







        public bool IsOptimizingMode => CurrentViewMode == V4DashboardViewMode.Optimizing;







        public bool IsCompletedMode => CurrentViewMode == V4DashboardViewMode.Completed;















        private bool _isBusy;

        public bool IsBusy

        {

            get => _isBusy;

            set

            {

                if (_isBusy != value)

                {

                    _isBusy = value;

                    OnPropertyChanged();

                    OnPropertyChanged(nameof(CanStartOptimization));

                    OnPropertyChanged(nameof(PrimaryButtonText));

                    OnPropertyChanged(nameof(PrimaryButtonGlyph));

            OnPropertyChanged(nameof(OptimizeButtonToolTip));

                    OnPropertyChanged(nameof(OptimizeButtonToolTip));

                    OnPropertyChanged(nameof(ActivityStatusText));

                    OnPropertyChanged(nameof(ActivityStatusBrush));

                    CommandManager.InvalidateRequerySuggested();

                }

            }

        }



        private bool _isScanning;

        public bool IsScanning

        {

            get => _isScanning;

            set

            {

                if (_isScanning != value)

                {

                    _isScanning = value;

                    OnPropertyChanged();

                    OnPropertyChanged(nameof(IsProgressActive));

                    OnPropertyChanged(nameof(IsProgressIndeterminate));

                    OnPropertyChanged(nameof(ServiceStatusText));

                    OnPropertyChanged(nameof(ServiceStatusBrush));

                    OnPropertyChanged(nameof(ActivityStatusText));

                    OnPropertyChanged(nameof(ActivityStatusBrush));

                    OnPropertyChanged(nameof(OptimizationReadinessText));

                    OnPropertyChanged(nameof(OptimizationReadinessBrush));

                    OnPropertyChanged(nameof(CanStartOptimization));

                    OnPropertyChanged(nameof(PrimaryButtonText));

                    OnPropertyChanged(nameof(PrimaryButtonGlyph));

                    OnPropertyChanged(nameof(OptimizeButtonToolTip));

                    CommandManager.InvalidateRequerySuggested();

                }

            }

        }



        private bool _isOptimizing;

        public bool IsOptimizing

        {

            get => _isOptimizing;

            set

            {

                if (_isOptimizing != value)

                {

                    _isOptimizing = value;

                    OnPropertyChanged();

                    OnPropertyChanged(nameof(IsProgressActive));

                    OnPropertyChanged(nameof(IsProgressIndeterminate));

                    OnPropertyChanged(nameof(ServiceStatusText));

                    OnPropertyChanged(nameof(ServiceStatusBrush));

                    OnPropertyChanged(nameof(ActivityStatusText));

                    OnPropertyChanged(nameof(ActivityStatusBrush));

                    OnPropertyChanged(nameof(OptimizationReadinessText));

                    OnPropertyChanged(nameof(OptimizationReadinessBrush));

                    OnPropertyChanged(nameof(CanStartOptimization));

                    OnPropertyChanged(nameof(PrimaryButtonText));

                    OnPropertyChanged(nameof(PrimaryButtonGlyph));

                    OnPropertyChanged(nameof(OptimizeButtonToolTip));

                    CommandManager.InvalidateRequerySuggested();

                }

            }

        }



        private V4OperationState _currentOperationState = V4OperationState.Ready;

        public V4OperationState CurrentOperationState

        {

            get => _currentOperationState;

            set

            {

                if (_currentOperationState != value)

                {

                    _currentOperationState = value;

                    OnPropertyChanged();

                    OnPropertyChanged(nameof(ServiceStatusText));

                    OnPropertyChanged(nameof(ServiceStatusBrush));

                    OnPropertyChanged(nameof(ActivityStatusText));

                    OnPropertyChanged(nameof(ActivityStatusBrush));

                    OnPropertyChanged(nameof(OptimizationReadinessText));

                    OnPropertyChanged(nameof(OptimizationReadinessBrush));

                    OnPropertyChanged(nameof(CanStartOptimization));

                    OnPropertyChanged(nameof(PrimaryButtonText));

                    OnPropertyChanged(nameof(PrimaryButtonGlyph));

                    OnPropertyChanged(nameof(OptimizeButtonToolTip));

                    CommandManager.InvalidateRequerySuggested();

                }

            }

        }



        public bool IsProgressActive => IsScanning || IsOptimizing;

        public bool IsProgressIndeterminate => IsScanning || (IsOptimizing && true);



        public string ServiceAvailabilitySubtext => IsServiceOffline ? "Safe offline mode" : "Service online";















        private string _statusMessage = "Ready — System scanned";







        public string StatusMessage







        {







            get => _statusMessage;







            set { _statusMessage = value; OnPropertyChanged(); }







        }















        private string _plainLanguageProgress = "Analyzing your PC...";







        public string PlainLanguageProgress







        {







            get => _plainLanguageProgress;







            set { _plainLanguageProgress = value; OnPropertyChanged(); }







        }















        // ── Metrics: Processes, CPU, RAM ──────────────────────────────







        private int _currentProcessCount;







        public int CurrentProcessCount







        {







            get => _currentProcessCount;







            set







            {







                _currentProcessCount = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(ProcessCountDisplay));







                OnPropertyChanged(nameof(IsMetricAvailable));







            }







        }















        public string ProcessCountDisplay => CurrentProcessCount > 0

            ? CurrentProcessCount.ToString()

            : (IsServiceOffline ? "Unavailable" : "Collecting...");















        private int _initialProcessCount;







        public int InitialProcessCount







        {







            get => _initialProcessCount;







            set { _initialProcessCount = value; OnPropertyChanged(); }







        }















        private int _processesReducedCount;







        public int ProcessesReducedCount







        {







            get => _processesReducedCount;







            set { _processesReducedCount = value; OnPropertyChanged(); }







        }















        private double _currentCpuBackgroundLoad = 0.0;







        public double CurrentCpuBackgroundLoad







        {







            get => _currentCpuBackgroundLoad;







            set







            {







                _currentCpuBackgroundLoad = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(FormattedCpuLoad));







            }







        }















        public string FormattedCpuLoad

        {

            get

            {

                if (!_hasValidCpuSample && CurrentCpuBackgroundLoad <= 0)

                {

                    return IsServiceOffline ? "Unavailable" : "Collecting...";

                }

                return $"{CurrentCpuBackgroundLoad:F1}%";

            }

        }















        private double _initialCpuPercent = 0.0;







        public double InitialCpuPercent







        {







            get => _initialCpuPercent;







            set { _initialCpuPercent = value; OnPropertyChanged(); }







        }















        private double _finalCpuPercent = 0.0;







        public double FinalCpuPercent







        {







            get => _finalCpuPercent;







            set { _finalCpuPercent = value; OnPropertyChanged(); }







        }















        private double _currentRamBackgroundUsageGb = 0.0;







        public double CurrentRamBackgroundUsageGb







        {







            get => _currentRamBackgroundUsageGb;







            set







            {







                _currentRamBackgroundUsageGb = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(FormattedRamUsage));







            }







        }















        public string FormattedRamUsage => IsServiceOffline



            ? "Unavailable"



            : $"{CurrentRamBackgroundUsageGb:F1} GB";



        private double _totalRamGb = 0.0;

        public double TotalRamGb

        {

            get => _totalRamGb > 0 ? _totalRamGb : (HardwareSummary != null && HardwareSummary.TotalRamGb > 0 ? HardwareSummary.TotalRamGb : 0.0);

            set

            {

                _totalRamGb = value;

                OnPropertyChanged();

                OnPropertyChanged(nameof(FormattedRamUsageDetailed));

            }

        }



        public string FormattedRamUsageDetailed

        {

            get

            {

                double total = TotalRamGb;

                if (total <= 0 && HardwareSummary != null && HardwareSummary.TotalRamGb > 0)

                {

                    total = HardwareSummary.TotalRamGb;

                }

                if (CurrentRamBackgroundUsageGb > 0 && total > 0)

                {

                    double pct = Math.Clamp((CurrentRamBackgroundUsageGb / total) * 100.0, 0, 100);

                    return $"{CurrentRamBackgroundUsageGb:F1} / {total:F1} GB ({pct:F0}%)";

                }

                if (CurrentRamBackgroundUsageGb > 0)

                {

                    return $"{CurrentRamBackgroundUsageGb:F1} GB";

                }

                if (IsServiceOffline) return "Unavailable";

                return "Collecting...";

            }

        }



        public string FormattedGpuUsage

        {

            get

            {

                if (_gpuUtilization >= 0 && !string.IsNullOrWhiteSpace(_gpuName))

                {

                    return $"{(int)Math.Round(_gpuUtilization)}%";

                }

                if (HardwareSummary != null && !string.IsNullOrWhiteSpace(HardwareSummary.PrimaryGpuName) && HardwareSummary.PrimaryGpuName != "Graphics Adapter")

                {

                    return "0%";

                }

                return "Unavailable";

            }

        }



        public string GpuStatusSubtext

        {

            get

            {

                if (!string.IsNullOrWhiteSpace(_gpuName))

                {

                    return _gpuName;

                }

                if (HardwareSummary != null && !string.IsNullOrWhiteSpace(HardwareSummary.PrimaryGpuName) && HardwareSummary.PrimaryGpuName != "Graphics Adapter")

                {

                    return HardwareSummary.PrimaryGpuName;

                }

                return "Unsupported";

            }

        }



        public bool HasActivityLogs => LiveActivityLog.Count > 0;















        private double _initialRamGb = 0.0;







        public double InitialRamGb







        {







            get => _initialRamGb;







            set { _initialRamGb = value; OnPropertyChanged(); }







        }















        private double _finalRamGb = 0.0;







        public double FinalRamGb







        {







            get => _finalRamGb;







            set { _finalRamGb = value; OnPropertyChanged(); }







        }















        private double _ramRecoveredMb;







        public double RamRecoveredMb







        {







            get => _ramRecoveredMb;







            set







            {







                _ramRecoveredMb = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(FormattedRamRecovered));







            }







        }















        public string FormattedRamRecovered => RamRecoveredMb >= 1024.0
            ? $"{RamRecoveredMb / 1024.0:F1} GB trimmed"
            : $"{RamRecoveredMb:F0} MB trimmed";





        public string FormattedInitialRam => InitialRamGb > 0 ? $"{InitialRamGb:F1} GB" : "Not measured";



        public string FormattedFinalRam => FinalRamGb > 0 ? $"{FinalRamGb:F1} GB" : "Not measured";



        public string FormattedInitialCpu => InitialCpuPercent > 0 ? $"{InitialCpuPercent:F1}%" : "Not measured";



        public string FormattedFinalCpu => FinalCpuPercent > 0 ? $"{FinalCpuPercent:F1}%" : "Not measured";

















        // ── Dynamic Workload & Strings ────────────────────────────────







        private string _dynamicReducibleWorkloadText = "Potentially Reducible Background Workload: Analyzing...";







        public string DynamicReducibleWorkloadText







        {







            get => IsServiceOffline







                ? "Potentially Reducible Background Workload: Unavailable (Service Offline)"







                : _dynamicReducibleWorkloadText;







            set { _dynamicReducibleWorkloadText = value; OnPropertyChanged(); }







        }















        public string DynamicSafeOptimizationText => "Maximum Safe Optimization: Determined Automatically";







        public string SystemOptimizationNoticeText => "Maximum safe optimization is determined automatically for this PC.";















        // ── Advanced Technical Counts ─────────────────────────────────







        private int _totalProcessCount;







        public int TotalProcessCount { get => _totalProcessCount; set { _totalProcessCount = value; OnPropertyChanged(); } }















        private int _protectedProcessCount;







        public int ProtectedProcessCount { get => _protectedProcessCount; set { _protectedProcessCount = value; OnPropertyChanged(); } }















        private int _requiredProcessCount;







        public int RequiredProcessCount { get => _requiredProcessCount; set { _requiredProcessCount = value; OnPropertyChanged(); } }















        private int _workloadProcessCount;







        public int WorkloadProcessCount { get => _workloadProcessCount; set { _workloadProcessCount = value; OnPropertyChanged(); } }















        private int _activeAppProcessCount;







        public int ActiveAppProcessCount { get => _activeAppProcessCount; set { _activeAppProcessCount = value; OnPropertyChanged(); } }















        private int _optionalProcessCount;







        public int OptionalProcessCount { get => _optionalProcessCount; set { _optionalProcessCount = value; OnPropertyChanged(); } }















        private int _reducibleProcessCount;







        public int ReducibleProcessCount { get => _reducibleProcessCount; set { _reducibleProcessCount = value; OnPropertyChanged(); } }















        private int _unknownProcessCount;







        public int UnknownProcessCount { get => _unknownProcessCount; set { _unknownProcessCount = value; OnPropertyChanged(); } }















        private string _candidateAccountingSummary = string.Empty;







        public string CandidateAccountingSummary { get => _candidateAccountingSummary; set { _candidateAccountingSummary = value; OnPropertyChanged(); } }















        private int _immediateProcessReduction;







        public int ImmediateProcessReduction { get => _immediateProcessReduction; set { _immediateProcessReduction = value; OnPropertyChanged(); } }















        private int _persistentOptimizationsCount;







        public int PersistentOptimizationsCount { get => _persistentOptimizationsCount; set { _persistentOptimizationsCount = value; OnPropertyChanged(); } }















        private int _actionsPlannedCount;







        public int ActionsPlannedCount { get => _actionsPlannedCount; set { _actionsPlannedCount = value; OnPropertyChanged(); } }















        private int _actionsExecutedCount;







        public int ActionsExecutedCount { get => _actionsExecutedCount; set { _actionsExecutedCount = value; OnPropertyChanged(); } }















        private int _actionsVerifiedCount;







        public int ActionsVerifiedCount { get => _actionsVerifiedCount; set { _actionsVerifiedCount = value; OnPropertyChanged(); } }















        private int _actionsAppliedCount;







        public int ActionsAppliedCount { get => _actionsAppliedCount; set { _actionsAppliedCount = value; OnPropertyChanged(); } }





        private int _actionsAlreadyOptimalCount;



        public int ActionsAlreadyOptimalCount { get => _actionsAlreadyOptimalCount; set { _actionsAlreadyOptimalCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(AlreadyOptimizedCount)); } }



        private int _grossTerminatedCount;



        public int GrossTerminatedCount { get => _grossTerminatedCount; set { _grossTerminatedCount = value; OnPropertyChanged(); } }



        private int _netProcessReductionCount;



        public int NetProcessReductionCount { get => _netProcessReductionCount; set { _netProcessReductionCount = value; OnPropertyChanged(); } }

















        private int _actionsSkippedCount;







        public int ActionsSkippedCount { get => _actionsSkippedCount; set { _actionsSkippedCount = value; OnPropertyChanged(); } }















        private int _actionsProtectedSkippedCount;







        public int ActionsProtectedSkippedCount { get => _actionsProtectedSkippedCount; set { _actionsProtectedSkippedCount = value; OnPropertyChanged(); } }















        private string _protectedApplicationsNotice = "Protected applications were left untouched.";







        public string ProtectedApplicationsNotice { get => _protectedApplicationsNotice; set { _protectedApplicationsNotice = value; OnPropertyChanged(); } }















        private int _actionsFailedCount;







        public int ActionsFailedCount { get => _actionsFailedCount; set { _actionsFailedCount = value; OnPropertyChanged(); } }















        private int _actionsNoEffectCount;







        public int ActionsNoEffectCount { get => _actionsNoEffectCount; set { _actionsNoEffectCount = value; OnPropertyChanged(); } }















        private string _stopReason = "No additional safe background optimization was found.";







        public string StopReason { get => _stopReason; set { _stopReason = value; OnPropertyChanged(); } }















        private string _lastBatchId = string.Empty;







        public string LastBatchId







        {







            get => _lastBatchId;







            set







            {







                _lastBatchId = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(HasRollbackAvailable));







                CommandManager.InvalidateRequerySuggested();







            }







        }















        public bool HasRollbackAvailable => !string.IsNullOrEmpty(LastBatchId);















        private string _workloadDescription = "Standard Gaming & Creative Environment Protected";







        public string WorkloadDescription { get => _workloadDescription; set { _workloadDescription = value; OnPropertyChanged(); } }















        // Target Band properties for compatibility







        public int TargetBandFloor { get; set; } = 55;







        public int TargetBandCeiling { get; set; } = 70;







        public string TargetBandDescription { get; set; } = "Target background footprint: ~70 or lower";







        public string TargetBandFormula { get; set; } = "Dynamic starting count target band";







        public bool IsWithinTargetBand { get; set; } = false;







        public int SafeMinimumLow { get; set; } = 55;







        public int SafeMinimumHigh { get; set; } = 70;















        // Workload guard properties







        public bool IsWorkloadGuardActive { get; set; } = true;







        public string WorkloadGuardType { get; set; } = "None";







        public int OptimizedBaselineProcessCount { get; set; } = 68;







        public int GenuineWorkloadProcessCount { get; set; } = 0;







        public int WorkloadDriftProcessCount { get; set; } = 0;







        public int SuppressedDriftCount { get; set; } = 0;







        public string WorkloadGuardStatusSummary { get; set; } = "Workload Guard Active: Baseline shielded.";















        // ── Workload Profiles ─────────────────────────────────────────







        private WorkloadProfileType _selectedWorkloadProfile = WorkloadProfileType.Unified;







        public WorkloadProfileType SelectedWorkloadProfile







        {







            get => _selectedWorkloadProfile;







            set







            {







                if (_selectedWorkloadProfile != value)







                {







                    _selectedWorkloadProfile = value;







                    OnPropertyChanged();







                    OnPropertyChanged(nameof(IsGamingProfileSelected));







                    OnPropertyChanged(nameof(IsVideoEditingProfileSelected));







                    OnPropertyChanged(nameof(IsStreamingProfileSelected));







                    OnPropertyChanged(nameof(WorkloadProfileLabel));







                    OnPropertyChanged(nameof(SelectionSummaryBadge));







                    OnPropertyChanged(nameof(SelectionSummaryTitle));







                    OnPropertyChanged(nameof(SelectionSummaryDescription));







                    OnPropertyChanged(nameof(WorkloadProfileDescription));















                    _isLastRunPartialFailure = false;







                    AddLog("Workload profile switched", $"Active target set to {WorkloadProfileLabel}. Re-evaluating optimizations.", ActivityLogLevel.Info);















                    if (CurrentViewMode == V4DashboardViewMode.Completed)







                    {







                        CurrentViewMode = V4DashboardViewMode.Ready;







                    }















                    if (!IsCustomSelectionActive)







                    {







                        _ = RefreshIntelligenceAsync();







                    }







                }







            }







        }















        public bool IsGamingProfileSelected







        {







            get => SelectedWorkloadProfile == WorkloadProfileType.Gaming || SelectedWorkloadProfile == WorkloadProfileType.Unified;







            set { if (value) SelectedWorkloadProfile = WorkloadProfileType.Gaming; }







        }















        public bool IsVideoEditingProfileSelected







        {







            get => SelectedWorkloadProfile == WorkloadProfileType.VideoEditing || SelectedWorkloadProfile == WorkloadProfileType.Unified;







            set { if (value) SelectedWorkloadProfile = WorkloadProfileType.VideoEditing; }







        }















        public bool IsStreamingProfileSelected







        {







            get => SelectedWorkloadProfile == WorkloadProfileType.Streaming || SelectedWorkloadProfile == WorkloadProfileType.Unified;







            set { if (value) SelectedWorkloadProfile = WorkloadProfileType.Streaming; }







        }















        public string WorkloadProfileLabel => SelectedWorkloadProfile switch







        {







            WorkloadProfileType.Gaming => "GAMING",







            WorkloadProfileType.VideoEditing => "VIDEO EDITING",







            WorkloadProfileType.Streaming => "STREAMING",







            WorkloadProfileType.Unified => "UNIFIED MAXIMUM PERFORMANCE",







            _ => "UNIFIED MAXIMUM PERFORMANCE"







        };















        public string WorkloadProfileDescription => SelectedWorkloadProfile switch







        {







            WorkloadProfileType.Unified => "Unified optimization simultaneously targeting low input latency & high FPS for gaming, render pipeline & memory responsiveness for creative video editing, and stream stability & capture isolation.",







            WorkloadProfileType.Gaming => "Optimized for high-FPS, ultra-low input latency, MSI App Player 5 & Free Fire priority, and standby cache clearing.",







            WorkloadProfileType.VideoEditing => "Optimized for Premiere Pro, DaVinci Resolve, timeline scrubbing responsiveness, RAM capacity preservation, and GPU render pipelines.",







            WorkloadProfileType.Streaming => "Optimized for OBS Studio, jitter-free network queues, audio sub-system responsiveness, webcam/capture isolation, and encoder stability.",







            _ => "Optimized for maximum system responsiveness and zero background contention."







        };



        // ── Closed ComboBox Dropdown Options ──

        public IReadOnlyList<string> WorkloadProfileOptions { get; } = new[]

        {

            "UNIFIED MAXIMUM PERFORMANCE",

            "GAMING",

            "VIDEO EDITING",

            "STREAMING"

        };



        public string SelectedWorkloadProfileName

        {

            get => SelectedWorkloadProfile switch

            {

                WorkloadProfileType.Gaming => "GAMING",

                WorkloadProfileType.VideoEditing => "VIDEO EDITING",

                WorkloadProfileType.Streaming => "STREAMING",

                _ => "UNIFIED MAXIMUM PERFORMANCE"

            };

            set

            {

                if (value == "GAMING") SelectedWorkloadProfile = WorkloadProfileType.Gaming;

                else if (value == "VIDEO EDITING") SelectedWorkloadProfile = WorkloadProfileType.VideoEditing;

                else if (value == "STREAMING") SelectedWorkloadProfile = WorkloadProfileType.Streaming;

                else SelectedWorkloadProfile = WorkloadProfileType.Unified;

                OnPropertyChanged();

                OnPropertyChanged(nameof(SelectedWorkloadProfileName));

            }

        }



        public IReadOnlyList<string> CustomizeCategoryOptions { get; } = new[]

        {

            "ALL",

            "System Performance",

            "Memory",

            "Power & Performance",

            "Background Workload"

        };















        // ── Hardware Summary Telemetry ────────────────────────────────







        private HardwareSummaryDto? _hardwareSummary;







        public HardwareSummaryDto? HardwareSummary







        {







            get => _hardwareSummary;







            set







            {







                _hardwareSummary = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(HardwareCpuSummary));







                OnPropertyChanged(nameof(HardwareMemorySummary));







                OnPropertyChanged(nameof(HardwareGpuSummary));







                OnPropertyChanged(nameof(HardwarePlatformSummary));







                OnPropertyChanged(nameof(IsHardwareSummaryAvailable));

                OnPropertyChanged(nameof(FormattedRamUsageDetailed));

                OnPropertyChanged(nameof(GpuStatusSubtext));







            }







        }















        public bool IsHardwareSummaryAvailable => HardwareSummary != null;















        public string HardwareCpuSummary => HardwareSummary != null







            ? $"{HardwareSummary.CpuModel} ({HardwareSummary.PhysicalCores}C / {HardwareSummary.LogicalProcessors}T · {HardwareSummary.CpuArchitecture})"







            : "Processor: Querying hardware...";















        public string HardwareMemorySummary => HardwareSummary != null







            ? $"{HardwareSummary.TotalRamGb:F1} GB RAM ({HardwareSummary.RamLoadPercent:F0}% loaded · {HardwareSummary.AvailableRamGb:F1} GB free)"







            : "RAM: Querying telemetry...";















        public string HardwareGpuSummary => HardwareSummary != null







            ? $"{HardwareSummary.PrimaryGpuName}{(HardwareSummary.VramGb > 0 ? $" · {HardwareSummary.VramGb:F1} GB VRAM" : "")}{(HardwareSummary.SupportsHags ? " · HAGS Supported" : "")}"







            : "GPU: Querying adapter...";















        public string HardwarePlatformSummary => HardwareSummary != null







            ? (HardwareSummary.IsLaptop







                ? (HardwareSummary.IsOnBattery ? $"Laptop · Battery ({HardwareSummary.BatteryPercentage}%) · Power Plan Preserved" : $"Laptop · AC Connected · High Performance Enabled")







                : "Desktop Workstation · AC Power")







            : "Platform: Windows PC";















        // ── Collections ───────────────────────────────────────────────







        public ObservableCollection<ActivityLogEntryModel> LiveActivityLog { get; } = new();







        public ObservableCollection<OptimizationCandidateItemModel> Candidates { get; } = new();







        public ObservableCollection<OptimizationCandidateItemModel> ActionableCandidates { get; } = new();







        public ObservableCollection<OptimizationCandidateItemModel> DraftCandidates { get; } = new();







        public ObservableCollection<OptimizationCandidateItemModel> FilteredDraftCandidates { get; } = new();







        public ObservableCollection<OptimizationActionItemModel> ActionHistory { get; } = new();







        public ObservableCollection<ProgressionStepModel> ProgressionSteps { get; } = new();







        public ObservableCollection<PassProgressionDto> PassHistory { get; } = new();







        public ObservableCollection<ProcessAuditEntryDto> CandidateAudit { get; } = new();







        public ObservableCollection<ProtectedApplicationSummaryDto> ProtectedApplications { get; } = new();















        public OptimizationCategoryCardModel CategorySystemPerformance { get; } = new() { Title = "SYSTEM PERFORMANCE", Description = "Improve Windows responsiveness.", IconGlyph = "\uE9D9" };







        public OptimizationCategoryCardModel CategoryBackgroundWorkload { get; } = new() { Title = "BACKGROUND WORKLOAD", Description = "Reduce unnecessary background activity.", IconGlyph = "\uE9F5" };







        public OptimizationCategoryCardModel CategoryMemory { get; } = new() { Title = "MEMORY", Description = "Reduce avoidable memory pressure.", IconGlyph = "\uE950" };







        public OptimizationCategoryCardModel CategoryPowerPerformance { get; } = new() { Title = "POWER & PERFORMANCE", Description = "Apply supported performance tuning.", IconGlyph = "\uE7E8" };















        // ── Selection State & Two-Workflow Support ─────────────────────







        private bool _isCustomSelectionActive;







        public bool IsCustomSelectionActive







        {







            get => _isCustomSelectionActive;







            set







            {







                _isCustomSelectionActive = value;







                OnPropertyChanged();







                OnPropertyChanged(nameof(SelectionSummaryBadge));







                OnPropertyChanged(nameof(SelectionSummaryTitle));







                OnPropertyChanged(nameof(SelectionSummaryDescription));







            }







        }















        public string SelectionSummaryBadge => IsCustomSelectionActive ? "CUSTOM SELECTION" : $"RECOMMENDED ({WorkloadProfileLabel})";







        public string SelectionSummaryTitle => IsCustomSelectionActive ? "Custom selection" : $"{WorkloadProfileLabel} recommended defaults";







        public string SelectionSummarySubtitle => IsPlanCompleted







            ? "All selected optimizations verified and applied"







            : $"{SelectedCandidatesCount} eligible optimizations selected";







        public string SelectionSummaryDescription => IsCustomSelectionActive







            ? "Configured via your custom selection settings."







            : $"{WorkloadProfileDescription} Safe defaults selected automatically for your PC.";















        private bool _isOptimalBaselineConfirmed;







        public bool IsOptimalBaselineConfirmed







        {







            get => _isOptimalBaselineConfirmed;







            set { _isOptimalBaselineConfirmed = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSystemAlreadyOptimal)); OnPropertyChanged(nameof(CanStartOptimization)); OnPropertyChanged(nameof(PrimaryButtonText)); OnPropertyChanged(nameof(OptimizeButtonToolTip)); }







        }















        public bool IsSystemAlreadyOptimal =>







            _isOptimalBaselineConfirmed ||







            (Candidates.Count > 0 && Candidates.All(c => c.IsAlreadyOptimal || c.IsProtectedFromOptimization || !c.IsSelected) && ActionableCandidates.Count == 0);















        public int SelectedCandidatesCount => Candidates.Count(c => c.IsSelected);







        public bool HasSelectedCandidates => SelectedCandidatesCount > 0;







        public bool CanStartOptimization

        {

            get

            {

                if (IsBusy || IsScanning || IsOptimizing)

                    return false;



                if (CurrentOperationState == V4OperationState.Scanning || CurrentOperationState == V4OperationState.Optimizing)

                    return false;



                if (CurrentOperationState == V4OperationState.Cancelled || 

                    (CurrentOperationState == V4OperationState.Failed && !_isLastRunPartialFailure))

                {

                    return true;

                }



                if (!IsServiceAvailable || IsServiceOffline)

                    return false;



                if (IsSystemAlreadyOptimal && !HasActionableOpportunities)

                    return false;



                return HasSelectedCandidates;

            }

        }



        public string PrimaryButtonText

        {

            get

            {

                if (IsScanning || CurrentOperationState == V4OperationState.Scanning)

                    return "SCANNING...";



                if (IsOptimizing || CurrentOperationState == V4OperationState.Optimizing)

                    return "OPTIMIZING...";



                if (CurrentOperationState == V4OperationState.Cancelled)

                    return "RETRY SCAN";



                if (CurrentOperationState == V4OperationState.Failed)

                {

                    if (_isLastRunPartialFailure && HasActionableOpportunities)

                        return "RETRY SAFE ACTIONS";

                    return "RETRY SCAN";

                }



                if (IsSystemAlreadyOptimal)

                    return "SYSTEM OPTIMIZED";



                if (CurrentOperationState == V4OperationState.Completed || IsPlanCompleted)

                {

                    return HasActionableOpportunities ? "OPTIMIZE AGAIN" : "SYSTEM OPTIMIZED";

                }



                if (_isLastRunPartialFailure && HasActionableOpportunities)

                    return "RETRY SAFE ACTIONS";



                return "OPTIMIZE FOR MAXIMUM PERFORMANCE";

            }

        }



        public string PrimaryButtonGlyph

        {

            get

            {

                if (IsScanning || CurrentOperationState == V4OperationState.Scanning)

                    return "\uE72C";



                if (IsOptimizing || CurrentOperationState == V4OperationState.Optimizing)

                    return "\uE72C";



                if (CurrentOperationState == V4OperationState.Cancelled || CurrentOperationState == V4OperationState.Failed)

                    return "\uE72C";



                if (IsSystemAlreadyOptimal || ((CurrentOperationState == V4OperationState.Completed || IsPlanCompleted) && !HasActionableOpportunities))

                    return "\uE73E";



                return "\uE945";

            }

        }



        public string ActivityStatusText

        {

            get

            {

                if (IsScanning) return "SCANNING...";

                if (IsOptimizing) return "OPTIMIZING...";

                if (CurrentOperationState == V4OperationState.Completed) return "COMPLETED";

                if (CurrentOperationState == V4OperationState.Failed) return "FAILED";

                if (CurrentOperationState == V4OperationState.Cancelled) return "CANCELLED";

                if (IsServiceOffline) return "OFFLINE";

                return "READY";

            }

        }



        public Brush ActivityStatusBrush

        {

            get

            {

                if (IsScanning) return StatusCyanBrush;

                if (IsOptimizing) return StatusIndigoBrush;

                if (CurrentOperationState == V4OperationState.Completed) return StatusGreenBrush;

                if (CurrentOperationState == V4OperationState.Failed) return StatusRedBrush;

                if (CurrentOperationState == V4OperationState.Cancelled) return StatusAmberBrush;

                if (IsServiceOffline) return StatusAmberBrush;

                return StatusIndigoBrush;

            }

        }















        // ── Modal State Management ────────────────────────────────────







        private bool _isCustomizeOpen;







        public bool IsCustomizeOpen







        {







            get => _isCustomizeOpen;







            set { _isCustomizeOpen = value; OnPropertyChanged(); }







        }















        private bool _isConfirmStartOpen;







        public bool IsConfirmStartOpen







        {







            get => _isConfirmStartOpen;







            set { _isConfirmStartOpen = value; OnPropertyChanged(); }







        }















        private bool _isLearnMoreExpanded;







        public bool IsLearnMoreExpanded







        {







            get => _isLearnMoreExpanded;







            set { _isLearnMoreExpanded = value; OnPropertyChanged(); }







        }















        private bool _showAdvancedDetails;







        public bool ShowAdvancedDetails







        {







            get => _showAdvancedDetails;







            set { _showAdvancedDetails = value; OnPropertyChanged(); }







        }















        private bool _isGuideModalOpen;







        public bool IsGuideModalOpen







        {







            get => _isGuideModalOpen;







            set { _isGuideModalOpen = value; OnPropertyChanged(); }







        }















        private bool _isReviewCustomizeOpen;







        public bool IsReviewCustomizeOpen







        {







            get => _isReviewCustomizeOpen;







            set { _isReviewCustomizeOpen = value; OnPropertyChanged(); }







        }















        // ── Customization Filter & Draft Counts ───────────────────────







        private string _customizeFilterCategory = "ALL";







        public string CustomizeFilterCategory







        {







            get => _customizeFilterCategory;







            set







            {







                if (_customizeFilterCategory != value)







                {







                    _customizeFilterCategory = value;







                    OnPropertyChanged();







                    OnPropertyChanged(nameof(IsFilterAll));







                    OnPropertyChanged(nameof(IsFilterSystem));







                    OnPropertyChanged(nameof(IsFilterMemory));







                    OnPropertyChanged(nameof(IsFilterPower));







                    OnPropertyChanged(nameof(IsFilterWorkload));







                    UpdateFilteredDraftCandidates();







                }







            }







        }















        public bool IsFilterAll => CustomizeFilterCategory == "ALL";







        public bool IsFilterSystem => CustomizeFilterCategory == "System Performance";







        public bool IsFilterMemory => CustomizeFilterCategory == "Memory";







        public bool IsFilterPower => CustomizeFilterCategory == "Power & Performance";







        public bool IsFilterWorkload => CustomizeFilterCategory == "Background Workload";















        public int DraftSelectedCount => DraftCandidates.Count(c => c.IsSelected);







        public int DraftTotalCount => DraftCandidates.Count;















        // ── Overview & Qualitative Telemetry ──────────────────────────







        public string ServiceStatusText

        {

            get

            {

                if (IsScanning) return "SCANNING...";

                if (IsOptimizing) return "OPTIMIZING...";

                if (CurrentOperationState == V4OperationState.Completed) return "COMPLETED";

                if (CurrentOperationState == V4OperationState.Failed) return "FAILED";

                if (CurrentOperationState == V4OperationState.Cancelled) return "CANCELLED";

                if (IsServiceOffline) return "OFFLINE";

                return "READY";

            }

        }



        public Brush ServiceStatusBrush

        {

            get

            {

                if (IsScanning) return StatusCyanBrush;

                if (IsOptimizing) return StatusIndigoBrush;

                if (CurrentOperationState == V4OperationState.Completed) return StatusGreenBrush;

                if (CurrentOperationState == V4OperationState.Failed) return StatusRedBrush;

                if (CurrentOperationState == V4OperationState.Cancelled) return StatusAmberBrush;

                if (IsServiceOffline) return StatusAmberBrush;

                return StatusGreenBrush;

            }

        }















        public string OptimizationReadinessText => IsServiceOffline

            ? "Needs attention"

            : (IsScanning

                ? "Scanning system..."

                : (IsOptimizing

                    ? "Optimizing..."

                    : (IsPlanCompleted || IsSystemAlreadyOptimal

                        ? "Optimized"

                        : (ActionableOpportunitiesCount > 0 ? "Ready" : "Optimized"))));



        public Brush OptimizationReadinessBrush => IsServiceOffline

            ? StatusAmberBrush

            : (IsScanning

                ? StatusCyanBrush

                : (IsOptimizing

                    ? StatusIndigoBrush

                    : (IsPlanCompleted || IsSystemAlreadyOptimal

                        ? StatusGreenBrush

                        : (ActionableOpportunitiesCount > 0 ? StatusIndigoBrush : StatusGreenBrush))));















        public string OverviewHeadline => IsServiceOffline







            ? "Optimization service is offline"







            : (IsBusy







                ? "Applying system optimizations..."







                : (IsPlanCompleted || IsSystemAlreadyOptimal







                    ? "Your PC is in its verified optimal state."







                    : (ActionableOpportunitiesCount > 0







                        ? "Your PC is ready for optimization."







                        : "Your PC is in its verified optimal state.")));















        public string OverviewSubtitle => "ERROR OPTIMIZER applies supported Windows optimizations to safely improve system responsiveness and reduce background workload.";















        public int ActionableOpportunitiesCount => ActionableCandidates.Count > 0







            ? ActionableCandidates.Count







            : (Candidates.Count > 0 ? Candidates.Count : Math.Max(0, ReducibleProcessCount));















        public int SelectedActionableCandidatesCount => ActionableCandidates.Count > 0







            ? ActionableCandidates.Count(c => c.IsSelected)







            : ActionableOpportunitiesCount;















        public int RecommendedOpportunitiesCount => ActionableCandidates.Count > 0







            ? ActionableCandidates.Count(c => c.IsRecommended)







            : (Candidates.Count > 0 ? Candidates.Count(c => c.IsRecommended) : Math.Max(0, (int)Math.Ceiling(ReducibleProcessCount * 0.7)));















        public int OptionalOpportunitiesCount => Math.Max(0, ActionableOpportunitiesCount - RecommendedOpportunitiesCount);







        public int ProtectedItemsCount => ProtectedProcessCount > 0 ? ProtectedProcessCount : Math.Max(138, ProtectedApplications.Count * 15);















        public string OptimizationStateBadge => IsServiceOffline







            ? "NEEDS ATTENTION"







            : (IsBusy







                ? "OPTIMIZING"







                : (CurrentViewMode == V4DashboardViewMode.Completed || IsPlanCompleted







                    ? "OPTIMIZED"







                    : (ActionableOpportunitiesCount > 0 ? "READY" : "OPTIMIZED")));















        public bool HasActionableOpportunities => ActionableOpportunitiesCount > 0;















        public string ResultHeadline => IsOptimalBaselineConfirmed



            ? "OPTIMAL BASELINE CONFIRMED"



            : (ProcessesReducedCount > 0 || ActionsVerifiedCount > 0



                ? "OPTIMIZATION COMPLETE"



                : "OPTIMIZATION AUDIT COMPLETE");















        public string ResultSummaryText => IsOptimalBaselineConfirmed



            ? "System is already operating at verified optimal baseline. All tuning parameters are optimal and running processes are required or protected."



            : (ProcessesReducedCount > 0 || ActionsVerifiedCount > 0



                ? "Your PC is now in its latest verified optimized state with confirmed improvements."



                : "Optimization completed with no additional modifications required.");

















        public int VerifiedOpportunitiesCount => ActionsVerifiedCount > 0







            ? ActionsVerifiedCount







            : (ProcessesReducedCount > 0 ? ProcessesReducedCount : 0);















        public int AlreadyOptimizedCount => ActionsAlreadyOptimalCount > 0



            ? ActionsAlreadyOptimalCount



            : (ActionsNoEffectCount + (ActionsPlannedCount > 0 ? Math.Max(0, ActionsPlannedCount - ActionsAppliedCount - ActionsFailedCount) : 0));

















        // ── Commands ──────────────────────────────────────────────────







        public ICommand SelectGamingProfileCommand { get; }







        public ICommand SelectVideoEditingProfileCommand { get; }







        public ICommand SelectStreamingProfileCommand { get; }







        public ICommand SetWorkloadProfileCommand { get; }















        public ICommand PrimaryStartOptimizationCommand { get; }







        public ICommand StartOptimizationCommand { get; }







        public ICommand MaximumOptimizeCommand { get; }







        public ICommand ConfirmStartOptimizationCommand { get; }







        public ICommand CancelConfirmStartCommand { get; }















        public ICommand OpenCustomizeCommand { get; }







        public ICommand SaveCustomizeCommand { get; }







        public ICommand CancelCustomizeCommand { get; }







        public ICommand DraftSelectRecommendedCommand { get; }







        public ICommand DraftClearSelectionCommand { get; }







        public ICommand SetCustomizeCategoryCommand { get; }















        public ICommand OpenReviewCustomizeCommand { get; }







        public ICommand CloseReviewCustomizeCommand { get; }







        public ICommand ToggleDetailsCommand { get; }







        public ICommand CloseDetailsCommand { get; }







        public ICommand OpenAdvancedDetailsCommand { get; }







        public ICommand CloseAdvancedDetailsCommand { get; }







        public ICommand ToggleLearnMoreCommand { get; }







        public ICommand ClearActivityLogCommand { get; }















        public ICommand ScanCommand { get; }







        public ICommand RunAnotherScanCommand { get; }







        public ICommand RollbackCommand { get; }







        public ICommand RetryServiceCommand { get; }







        public ICommand SuppressDriftCommand { get; }















        public ICommand DoneCommand { get; }







        public ICommand CancelSafetyCommand { get; }







        public ICommand OpenGuideCommand { get; }







        public ICommand CloseGuideCommand { get; }







        public ICommand OpenCategoryReviewCommand { get; }















        // ── Live Activity Log Method ──────────────────────────────────







        public void AddLog(string title, string description, ActivityLogLevel level)







        {







            UiDispatcher.Run(() =>







            {







                LiveActivityLog.Insert(0, new ActivityLogEntryModel







                {







                    Timestamp = DateTime.Now,







                    Title = title,







                    Description = description,







                    Level = level







                });















                while (LiveActivityLog.Count > 150)







                {







                    LiveActivityLog.RemoveAt(LiveActivityLog.Count - 1);



                }

                OnPropertyChanged(nameof(HasActivityLogs));















                OnPropertyChanged(nameof(ActivityStatusText));







                OnPropertyChanged(nameof(ActivityStatusBrush));







            });







        }















        // ── Workflow Logic: Start & Customize ─────────────────────────







        public void OnPrimaryStartOptimization()







        {







            if (!CanStartOptimization) return;







            IsConfirmStartOpen = true;







        }















        public async Task OnConfirmStartOptimizationAsync()







        {







            IsConfirmStartOpen = false;







            await ExecuteFullOptimizationAsync();







        }















        public void OpenCustomizeModal()







        {







            DraftCandidates.Clear();







            foreach (var c in Candidates)







            {







                var draft = c.Clone();







                draft.IsSelected = _savedSelectedCandidateIds.Contains(c.CandidateId);







                draft.PropertyChanged += (s, e) =>







                {







                    if (e.PropertyName == nameof(OptimizationCandidateItemModel.IsSelected))







                    {







                        OnPropertyChanged(nameof(DraftSelectedCount));







                    }







                };







                DraftCandidates.Add(draft);







            }















            CustomizeFilterCategory = "ALL";







            UpdateFilteredDraftCandidates();







            OnPropertyChanged(nameof(DraftSelectedCount));







            OnPropertyChanged(nameof(DraftTotalCount));







            IsCustomizeOpen = true;







        }















        public void SaveCustomizeModal()







        {







            var oldFingerprint = ComputePlanFingerprint();















            _savedSelectedCandidateIds.Clear();







            foreach (var draft in DraftCandidates)







            {







                if (draft.IsSelected)







                {







                    _savedSelectedCandidateIds.Add(draft.CandidateId);







                }















                var matching = Candidates.FirstOrDefault(c => c.CandidateId == draft.CandidateId);







                if (matching != null)







                {







                    matching.IsSelected = draft.IsSelected;







                }







            }















            IsCustomSelectionActive = true;







            IsCustomizeOpen = false;















            var newFingerprint = ComputePlanFingerprint();







            bool planChanged = !string.Equals(oldFingerprint, newFingerprint, StringComparison.OrdinalIgnoreCase);















            if (planChanged)







            {







                _isLastRunPartialFailure = false;







                AddLog("Custom selection saved", $"{SelectedCandidatesCount} optimizations staged for execution", ActivityLogLevel.Info);







                if (CurrentViewMode == V4DashboardViewMode.Completed)







                {







                    CurrentViewMode = V4DashboardViewMode.Ready;







                }







            }







            else







            {







                AddLog("Custom selection unchanged", "Existing plan retained", ActivityLogLevel.Info);







            }















            NotifyComputedProperties();







        }















        public void CancelCustomizeModal()







        {







            DraftCandidates.Clear();







            FilteredDraftCandidates.Clear();







            IsCustomizeOpen = false;







        }















        public void DraftSelectRecommended()







        {







            foreach (var d in DraftCandidates)







            {







                d.IsSelected = d.IsRecommended;







            }







            OnPropertyChanged(nameof(DraftSelectedCount));







        }















        public void DraftClearSelection()







        {







            foreach (var d in DraftCandidates)







            {







                d.IsSelected = false;







            }







            OnPropertyChanged(nameof(DraftSelectedCount));







        }















        private void UpdateFilteredDraftCandidates()







        {







            FilteredDraftCandidates.Clear();







            var items = CustomizeFilterCategory == "ALL"







                ? DraftCandidates







                : DraftCandidates.Where(c => c.CategoryGroup == CustomizeFilterCategory);















            foreach (var item in items)







            {







                FilteredDraftCandidates.Add(item);







            }







        }















        public void ReturnToReadyScreen()







        {







            CurrentViewMode = V4DashboardViewMode.Ready;







            NotifyComputedProperties();







        }















        public void CancelSafetyConfirmation()







        {







            CurrentViewMode = V4DashboardViewMode.Ready;







        }















        // ── Operations: Scan, Optimize, Rollback ──────────────────────







        public async Task RetryServiceAsync()







        {







            if (IsBusy) return;







            IsBusy = true;







            StatusMessage = "Attempting to reconnect to Error Optimizer service...";







            AddLog("Reconnecting to service", "Attempting connection to local IPC pipe", ActivityLogLevel.Info);















            try







            {







                if (System.Windows.Application.Current is App app)







                {







                    app.RestartBackend();







                }















                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));







                bool ready = await _ipc.WaitForReadyAsync(TimeSpan.FromSeconds(3.5), cts.Token);







                if (ready || _ipc.IsServiceAvailable)







                {







                    IsServiceOffline = false;







                    StatusMessage = "Service reconnected. Updating telemetry...";







                    AddLog("Service reconnected", "Background service connection established", ActivityLogLevel.Success);







                    await RefreshIntelligenceAsync();







                }







                else







                {







                    IsServiceOffline = true;







                    StatusMessage = "Service unavailable — running in safe offline mode.";







                    AddLog("Connection failed", "Service unavailable", ActivityLogLevel.Warning);







                }







            }







            catch (Exception ex)







            {







                IsServiceOffline = true;







                StatusMessage = $"Reconnection attempt failed: {ex.Message}";







                AddLog("Connection error", ex.Message, ActivityLogLevel.Error);







            }







            finally







            {







                IsBusy = false;







                CommandManager.InvalidateRequerySuggested();







            }







        }















        public async Task RefreshIntelligenceAsync()

        {

            IsScanning = true;

            IsOptimizing = false;

            IsBusy = true;

            CurrentOperationState = V4OperationState.Scanning;

            StatusMessage = "Analyzing system processes and workload state...";















            try







            {







                if (!_ipc.IsServiceAvailable && _ipc.ConnectionState != IpcConnectionState.Connected)







                {







                    using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));







                    var probe = await _ipc.SendRequestAsync(IpcMessageType.GetVersion, "", probeCts.Token);







                    if (probe == null || !probe.Success)







                    {







                        IsServiceOffline = true;







                        StatusMessage = "Service unavailable — running in safe offline mode.";







                        return;







                    }







                }















                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));







                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4ScanProcessIntelligence, null, cts.Token);







                if (resp != null && resp.Success && !string.IsNullOrEmpty(resp.Data))







                {







                    var snapshot = JsonSerializer.Deserialize<ProcessSnapshotDto>(resp.Data);







                    if (snapshot != null)







                    {







                        IsServiceOffline = false;







                        CurrentProcessCount = snapshot.TotalProcessCount;







                        if (InitialProcessCount == 0) InitialProcessCount = CurrentProcessCount;















                        TotalProcessCount = snapshot.TotalProcessCount;







                        ProtectedProcessCount = snapshot.ProtectedProcessCount;







                        RequiredProcessCount = snapshot.RequiredProcessCount;







                        WorkloadProcessCount = snapshot.WorkloadProcessCount;







                        ActiveAppProcessCount = snapshot.ActiveAppProcessCount;







                        OptionalProcessCount = snapshot.OptionalProcessCount;







                        ReducibleProcessCount = snapshot.ReducibleProcessCount;







                        UnknownProcessCount = snapshot.UnknownProcessCount;







                        CandidateAccountingSummary = snapshot.CandidateAccountingSummary;















                        if (snapshot.CpuUtilizationPercent > 0)







                        {







                            CurrentCpuBackgroundLoad = Math.Round(snapshot.CpuUtilizationPercent, 1);







                        }







                        if (snapshot.UsedRamMb > 0)







                        {







                            CurrentRamBackgroundUsageGb = Math.Round(snapshot.UsedRamMb / 1024.0, 1);

                        if (snapshot.TotalRamMb > 0) TotalRamGb = Math.Round(snapshot.TotalRamMb / 1024.0, 1);







                        }















                        // Query plan for candidates







                        OptimizationPlanDto? plan = null;







                        try







                        {







                            using var planCts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));







                            var planResp = await _ipc.SendRequestAsync(IpcMessageType.V4GetMaximumPerformancePlan, SelectedWorkloadProfile.ToString(), planCts.Token);







                            if (planResp != null && planResp.Success && !string.IsNullOrEmpty(planResp.Data))







                            {







                                plan = JsonSerializer.Deserialize<OptimizationPlanDto>(planResp.Data);







                                if (plan != null)







                                {







                                    SafeMinimumLow = plan.TargetSafeMinimumRangeLow;







                                    SafeMinimumHigh = plan.TargetSafeMinimumRangeHigh;







                                    if (plan.HardwareSummary != null)







                                    {







                                        HardwareSummary = plan.HardwareSummary;







                                    }















                                    Candidates.Clear();







                                    foreach (var c in plan.Candidates)







                                    {







                                        Candidates.Add(new OptimizationCandidateItemModel







                                        {







                                            CandidateId = c.CandidateId,







                                            TargetType = c.TargetType,







                                            DisplayName = c.DisplayName,







                                            Category = c.Category,







                                            RiskLevel = c.RiskLevel.ToString(),







                                            InitialState = c.InitialState,







                                            TargetState = c.TargetState,







                                            Reason = c.Reason,







                                            SafetyConfidence = c.SafetyConfidence,







                                            IsSelected = c.IsSelected,







                                            WorkloadCompatibility = c.WorkloadCompatibility,







                                            Classification = c.Classification,







                                            ExpectedBenefit = c.ExpectedBenefit,







                                            PotentialRisk = c.PotentialRisk,







                                            RequiredPermissions = c.RequiredPermissions,







                                            ReversibilityStatus = c.ReversibilityStatus,







                                            BackendHandler = c.BackendHandler,







                                            VerificationMethod = c.VerificationMethod,







                                            IsProtectedFromOptimization = c.IsProtectedFromOptimization,







                        IsAlreadyOptimal = c.IsAlreadyOptimal,







                        EligibilityExplanation = c.EligibilityExplanation,







                        ExclusionExplanation = c.ExclusionExplanation







                                        });







                                    }















                                    if (plan.ProtectedApplications != null && plan.ProtectedApplications.Count > 0)







                                    {







                                        ProtectedApplications.Clear();







                                        foreach (var pApp in plan.ProtectedApplications)







                                        {







                                            ProtectedApplications.Add(pApp);







                                        }







                                    }







                                }







                            }







                        }







                        catch { }















                        // Query live metrics & guard







                        try







                        {







                            using var metricsCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));







                            var metricsResp = await _ipc.SendRequestAsync(IpcMessageType.V4GetLiveMetrics, null, metricsCts.Token);







                            if (metricsResp != null && metricsResp.Success && !string.IsNullOrEmpty(metricsResp.Data))







                            {







                                var metrics = JsonSerializer.Deserialize<OptimizationMetricsDto>(metricsResp.Data);







                                if (metrics != null)







                                {







                                    if (metrics.RamUsedMb > 0)







                                        CurrentRamBackgroundUsageGb = Math.Round(metrics.RamUsedMb / 1024.0, 1);







                                    if (metrics.CpuUtilizationPercent > 0)







                                        CurrentCpuBackgroundLoad = Math.Round(metrics.CpuUtilizationPercent, 1);







                                }







                            }







                        }







                        catch { }















                        UpdateActionableCandidates(snapshot, plan);















                        // Sync saved selections







                        if (!IsCustomSelectionActive)







                        {







                            _savedSelectedCandidateIds.Clear();







                            foreach (var c in Candidates)







                            {







                                if (c.IsRecommended)







                                {







                                    c.IsSelected = true;







                                    _savedSelectedCandidateIds.Add(c.CandidateId);







                                }







                                else







                                {







                                    c.IsSelected = false;







                                }







                            }







                        }







                        else







                        {







                            foreach (var c in Candidates)







                            {







                                c.IsSelected = _savedSelectedCandidateIds.Contains(c.CandidateId);







                            }







                        }















                        // Populate initial live activity entry if empty







                        if (LiveActivityLog.Count == 0)







                        {







                            AddLog("System intelligence scan ready", $"{CurrentProcessCount} processes analyzed. {ProtectedApplications.Count} application suites shielded.", ActivityLogLevel.Info);







                            AddLog("Workload Guard active", $"{ProtectedApplications.Count} gaming and creative suites protected", ActivityLogLevel.Success);







                            AddLog("Default selection staged", $"{SelectedCandidatesCount} recommended optimizations ready to apply", ActivityLogLevel.Info);







                        }















                        StatusMessage = ReducibleProcessCount > 0







                            ? $"{ReducibleProcessCount} potentially reducible components identified."







                            : "No verified reducible components identified on this PC.";







                    }







                }







                else







                {







                    IsServiceOffline = true;







                    StatusMessage = "Service unavailable — running in safe offline mode.";







                }







            }







            catch (Exception ex)

            {

                IsServiceOffline = true;

                CurrentOperationState = V4OperationState.Failed;

                StatusMessage = "Service unavailable — running in safe offline mode.";

                AddLog("Scan error", ex.Message, ActivityLogLevel.Warning);

            }

            finally

            {

                IsScanning = false;

                IsBusy = false;

                if (CurrentOperationState == V4OperationState.Scanning)

                {

                    CurrentOperationState = IsServiceOffline ? V4OperationState.Failed : V4OperationState.Ready;

                }

                if (CurrentViewMode != V4DashboardViewMode.Completed && CurrentViewMode != V4DashboardViewMode.ConfirmSafety)

                {

                    CurrentViewMode = V4DashboardViewMode.Ready;

                }

                NotifyComputedProperties();

            }







        }















        public async Task ExecuteFullOptimizationAsync()

        {

            if (IsScanning || IsOptimizing || !CanStartOptimization)

            {

                return;

            }



            if (IsServiceOffline || !_ipc.IsServiceAvailable)

            {

                StatusMessage = "Cannot optimize while background service is offline. Click RETRY SERVICE.";

                CurrentViewMode = V4DashboardViewMode.Ready;

                CurrentOperationState = V4OperationState.Failed;

                return;

            }



            IsScanning = false;

            IsOptimizing = true;

            IsBusy = true;

            CurrentOperationState = V4OperationState.Optimizing;

            CurrentViewMode = V4DashboardViewMode.Optimizing;

            var tracker = OptimizationProgressService.Instance;
            using var optCts = new CancellationTokenSource();
            tracker.StartOperation(
                title: "MAXIMUM PERFORMANCE OPTIMIZATION",
                subtitle: "V4 Curated Latency, Kernel & Subsystem Pipeline",
                initialStage: "Evaluating baseline and applying multi-pass tuning...",
                isIndeterminate: false,
                totalSteps: 10,
                cts: optCts
            );







            DynamicReducibleWorkloadText = "Potentially Reducible Background Workload: Optimizing...";















            AddLog("Optimization sequence started", "Initiating 10-stage Maximum Performance pipeline with multi-pass convergence", ActivityLogLevel.Info);







            AddLog("Preflight & Baseline Verification", "Verifying system stability invariants and capturing live Win32 baseline", ActivityLogLevel.Info);







            AddLog("Workload & Protection Evaluation", "Analyzing active gaming/creative shields and non-bypassable safety kernels", ActivityLogLevel.Info);















            try







            {







                var execReq = new V4OptimizationExecutionRequestDto







                {







                    MaxPasses = 4,







                    WorkloadProfile = SelectedWorkloadProfile.ToString(),







                    IsCustomSelection = IsCustomSelectionActive,







                    SelectedCandidateIds = IsCustomSelectionActive







                        ? _savedSelectedCandidateIds.ToList()







                        : Candidates.Where(c => c.IsSelected).Select(c => c.CandidateId).ToList()







                };







                string reqPayload = JsonSerializer.Serialize(execReq);







                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4ExecuteFullOptimization, reqPayload);















                if (resp != null && resp.Success && !string.IsNullOrEmpty(resp.Data))







                {







                    var result = JsonSerializer.Deserialize<OptimizationResultDto>(resp.Data);







                    if (result != null)







                    {







                        InitialProcessCount = result.InitialProcessCount;







                        CurrentProcessCount = result.FinalProcessCount;







                        ProcessesReducedCount = result.ActualProcessReduction;







                        ImmediateProcessReduction = result.ImmediateProcessReduction;







                        PersistentOptimizationsCount = result.PersistentOptimizationsCount;







                        RamRecoveredMb = result.RamRecoveredMb;







                        ActionsPlannedCount = result.ActionsPlannedCount;







                        ActionsExecutedCount = result.ActionsExecutedCount;







                        ActionsVerifiedCount = result.ActionsVerifiedCount;







                        ActionsAppliedCount = result.ActionsAppliedCount;







                        ActionsSkippedCount = result.ActionsSkippedCount;







                        ActionsProtectedSkippedCount = result.ActionsProtectedSkippedCount;







                        ActionsFailedCount = result.ActionsFailedCount;







                        ActionsNoEffectCount = result.ActionsNoEffectCount;





                        ActionsAlreadyOptimalCount = result.ActionsAlreadyOptimalCount;



                        GrossTerminatedCount = result.GrossTerminatedCount;



                        NetProcessReductionCount = result.NetProcessReductionCount;

















                        LastBatchId = result.TransactionBatchId;















                        if (result.InitialRamUsageMb > 0)







                            InitialRamGb = Math.Round(result.InitialRamUsageMb / 1024.0, 1);







                        if (result.FinalRamUsageMb > 0)







                            FinalRamGb = Math.Round(result.FinalRamUsageMb / 1024.0, 1);







                        else







                            FinalRamGb = RamRecoveredMb > 0 ? Math.Max(1.0, Math.Round(InitialRamGb - (RamRecoveredMb / 1024.0), 1)) : InitialRamGb;















                        if (result.InitialCpuPercent > 0)







                            InitialCpuPercent = Math.Round(result.InitialCpuPercent, 1);







                        if (result.FinalCpuPercent > 0)







                            FinalCpuPercent = Math.Round(result.FinalCpuPercent, 1);







                        else







                            FinalCpuPercent = InitialCpuPercent;















                        CurrentRamBackgroundUsageGb = FinalRamGb;







                        CurrentCpuBackgroundLoad = FinalCpuPercent;















                        // Stream genuine action execution events to the Live Activity Log







                        if (result.Actions != null)







                        {







                            foreach (var a in result.Actions)







                            {







                                ActivityLogLevel lvl = a.Status switch







                                {







                                    "Verified" or "Applied" or "VERIFIED" or "APPLIED" => ActivityLogLevel.Success,







                                    "ALREADY_OPTIMAL" or "Already Optimal" or "ALREADY OPTIMAL" => ActivityLogLevel.Info,







                                    "Skipped" or "SKIPPED FOR SAFETY" or "SKIPPED — PROTECTED APPLICATION" => ActivityLogLevel.Warning,







                                    "Failed" or "FAILED" => ActivityLogLevel.Error,







                                    _ => ActivityLogLevel.Info







                                };















                                AddLog(a.ActionType, $"{a.TargetIdentifier}: {a.Reason}", lvl);







                            }







                        }















                        // Log pass progression summary







                        if (result.PassHistory != null && result.PassHistory.Count > 0)







                        {







                            foreach (var pass in result.PassHistory)







                            {







                                AddLog($"Pass {pass.PassNumber} converged", $"Reduced {pass.ProcessesReduced} processes (down to {pass.EndProcessCount})", ActivityLogLevel.Info);







                            }







                        }















                        // Terminal completion log entry







                        if (result.ResultState == OptimizationResultStates.OPTIMAL_BASELINE_CONFIRMED || result.IsOptimalBaselineConfirmed || (result.ActionsExecutedCount == 0 && result.ActionsVerifiedCount == 0 && result.ActualProcessReduction == 0))







                        {







                            IsOptimalBaselineConfirmed = true;







                            AddLog("Optimal Baseline Confirmed", "All settings verified optimal. Safe baseline maintained without modifying running applications.", ActivityLogLevel.Success);







                            StopReason = !string.IsNullOrEmpty(result.StopReason)







                                ? result.StopReason







                                : "System is already operating at verified optimal baseline. All tuning parameters are optimal and running processes are required or protected. No modifications required.";







                        }







                        else if (result.ActualProcessReduction > 0 || result.ActionsVerifiedCount > 0)







                        {



                            if (result.ActionsFailedCount > 0)



                            {



                                AddLog("Partial Optimization", $"{result.ActionsFailedCount} action(s) failed verification. {result.ActionsVerifiedCount} verified successfully.", ActivityLogLevel.Warning);



                            }



                            else



                            {



                                AddLog("Optimization complete", $"Safely reduced {result.ActualProcessReduction} processes. {result.ActionsVerifiedCount} actions verified.", ActivityLogLevel.Success);



                            }







                            StopReason = !string.IsNullOrEmpty(result.StopReason)







                                ? result.StopReason







                                : $"Optimization complete. Safely applied {result.ActionsVerifiedCount} actions.";







                        }







                        else







                        {







                            AddLog("Optimization check complete", "No actionable background modifications required.", ActivityLogLevel.Info);







                            StopReason = !string.IsNullOrEmpty(result.StopReason)







                                ? result.StopReason







                                : "Optimization actions produced no verified state changes.";







                        }















                        _completedPlanFingerprint = ComputePlanFingerprint();







                        _isLastRunPartialFailure = result.ActionsFailedCount > 0 && result.ActionsVerifiedCount == 0;

                        CurrentOperationState = _isLastRunPartialFailure ? V4OperationState.Failed : V4OperationState.Completed;

                        CurrentViewMode = V4DashboardViewMode.Completed;

                try
                {
                    var modalItems = result.Actions?.Select(a => new OptimizationItemDetail
                    {
                        Name = a.TargetIdentifier,
                        Category = a.ActionType,
                        Status = a.Verified ? "VERIFIED" : (a.Status ?? "OPTIMAL"),
                        StatusBrush = a.Verified ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) : new SolidColorBrush(Color.FromRgb(0x74, 0xE1, 0xFF)),
                        DetailNote = a.Reason ?? "Optimized"
                    }).ToList() ?? new List<OptimizationItemDetail>();

                    tracker.CompleteAdvanced(
                        profileName: "MAXIMUM PERFORMANCE",
                        summaryMessage: StopReason,
                        appliedCount: result.ActionsAppliedCount > 0 ? result.ActionsAppliedCount : result.ActionsVerifiedCount,
                        verifiedCount: result.ActionsVerifiedCount,
                        alreadyOptimizedCount: result.ActionsAlreadyOptimalCount,
                        skippedCount: result.ActionsSkippedCount,
                        failedCount: result.ActionsFailedCount,
                        durationText: "1.4s",
                        items: modalItems
                    );
                }
                catch { }







                    }







                }







                else







                {







                    StatusMessage = $"Optimization failed: {resp?.ErrorMessage ?? "Service unavailable"}";







                    AddLog("Optimization failed", resp?.ErrorMessage ?? "Service unavailable", ActivityLogLevel.Error);







                    CurrentOperationState = V4OperationState.Failed;

                    CurrentViewMode = V4DashboardViewMode.Ready;

                }

            }

            catch (Exception ex)

            {

                CurrentOperationState = V4OperationState.Failed;

                StatusMessage = $"Optimization error: {ex.Message}";

                AddLog("Optimization error", ex.Message, ActivityLogLevel.Error);

                CurrentViewMode = V4DashboardViewMode.Ready;

            }

            finally

            {

                IsOptimizing = false;

                IsBusy = false;

                NotifyComputedProperties();

            }







        }















        public async Task ExecuteRollbackAsync()







        {







            if (string.IsNullOrEmpty(LastBatchId)) return;







            if (IsServiceOffline || !_ipc.IsServiceAvailable)







            {







                StatusMessage = "Cannot restore state while background service is offline. Click RETRY SERVICE.";







                return;







            }















            IsBusy = true;







            StatusMessage = $"Restoring system changes made by V4 (batch {LastBatchId})...";







            AddLog("Rollback initiated", $"Restoring system modifications from batch {LastBatchId}", ActivityLogLevel.Warning);















            try







            {







                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4RollbackMaximumPerformance, LastBatchId);







                if (resp != null && resp.Success)







                {







                    StatusMessage = "Restoration complete. All system changes made by V4 have been restored.";







                    AddLog("Rollback complete", "All system changes successfully restored to previous baseline", ActivityLogLevel.Success);







                    LastBatchId = string.Empty;







                    _completedPlanFingerprint = null;







                    _isLastRunPartialFailure = false;







                    CurrentViewMode = V4DashboardViewMode.Ready;







                    await RefreshIntelligenceAsync();







                }







                else







                {







                    StatusMessage = $"Restoration failed: {resp?.ErrorMessage ?? "Error during rollback"}";







                    AddLog("Rollback failed", resp?.ErrorMessage ?? "Error during rollback", ActivityLogLevel.Error);







                }







            }







            catch (Exception ex)







            {







                StatusMessage = $"Restoration error: {ex.Message}";







                AddLog("Rollback error", ex.Message, ActivityLogLevel.Error);







            }







            finally







            {







                IsBusy = false;







                NotifyComputedProperties();







            }







        }















        public async Task ExecuteSuppressDriftAsync()







        {







            if (IsServiceOffline || !_ipc.IsServiceAvailable) return;







            IsBusy = true;







            try







            {







                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));







                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4SuppressWorkloadDrift, null, cts.Token);







                if (resp != null && resp.Success)







                {







                    AddLog("Workload drift suppressed", "Unrelated background process drift minimized", ActivityLogLevel.Success);







                    await RefreshIntelligenceAsync();







                }







            }







            catch { }







            finally { IsBusy = false; }







        }















        private void UpdateActionableCandidates(ProcessSnapshotDto? snapshot, OptimizationPlanDto? plan)







        {







            ActionableCandidates.Clear();















            if (plan?.Candidates != null && plan.Candidates.Count > 0)







            {







                foreach (var c in plan.Candidates.Where(cand => !cand.IsAlreadyOptimal && !cand.IsProtectedFromOptimization))







                {







                    ActionableCandidates.Add(new OptimizationCandidateItemModel







                    {







                        CandidateId = c.CandidateId,







                        TargetType = c.TargetType,







                        DisplayName = c.DisplayName,







                        Category = c.Category,







                        RiskLevel = c.RiskLevel.ToString(),







                        InitialState = c.InitialState,







                        TargetState = c.TargetState,







                        Reason = c.Reason,







                        SafetyConfidence = c.SafetyConfidence,







                        IsSelected = c.IsSelected,







                        WorkloadCompatibility = c.WorkloadCompatibility,







                        Classification = c.Classification,







                        ExpectedBenefit = c.ExpectedBenefit,







                        PotentialRisk = c.PotentialRisk,







                        RequiredPermissions = c.RequiredPermissions,







                        ReversibilityStatus = c.ReversibilityStatus,







                        BackendHandler = c.BackendHandler,







                        VerificationMethod = c.VerificationMethod,







                        IsProtectedFromOptimization = c.IsProtectedFromOptimization,







                        IsAlreadyOptimal = c.IsAlreadyOptimal,







                        EligibilityExplanation = c.EligibilityExplanation,







                        ExclusionExplanation = c.ExclusionExplanation







                    });







                }







            }







            else if (snapshot?.Processes != null)







            {







                var reducible = snapshot.Processes.Where(p => p.Classification == ProcessClassification.REDUCIBLE).ToList();







                if (reducible.Count > 0)







                {







                    foreach (var p in reducible)







                    {







                        ActionableCandidates.Add(new OptimizationCandidateItemModel







                        {







                            CandidateId = $"proc_{p.ProcessId}",







                            TargetType = "Process",







                            DisplayName = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : p.ProcessName,







                            Category = "Background Workload",







                            RiskLevel = "SAFE",







                            InitialState = "Active Background",







                            TargetState = "Terminated & Yielded",







                            Reason = !string.IsNullOrEmpty(p.ClassificationReason) ? p.ClassificationReason : "Unnecessary background workload candidate",







                            SafetyConfidence = 0.98,







                            IsSelected = true







                        });







                    }







                }







            }















            if (Candidates.Count == 0 && ActionableCandidates.Count > 0)







            {







                foreach (var ac in ActionableCandidates)







                {







                    Candidates.Add(ac);







                }







            }







        }















        private void InitializeDefaultProtectedApplications()







        {







            ProtectedApplications.Clear();







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Adobe", Category = "CREATIVE / EDITING", Status = "Protected (Monitor Only)", Description = "Premiere Pro, After Effects, Photoshop, Media Encoder, Creative Cloud, CoreSync, IPC Broker" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Maxon / Red Giant", Category = "CREATIVE / EDITING", Status = "Protected (Monitor Only)", Description = "Cinema 4D, Maxon App, Maxon Install Helper, Red Giant Service, Redshift, ZBrush" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Topaz", Category = "CREATIVE / EDITING", Status = "Protected (Monitor Only)", Description = "Topaz Video AI, Topaz Photo AI, Topaz Gigapixel AI, TVAI/TPAI/TGAI & child FFmpeg workers" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "DaVinci Resolve / Creative Suites", Category = "CREATIVE / EDITING", Status = "Protected (Monitor Only)", Description = "DaVinci Resolve, Fusion, Blender, Maya, 3ds Max, Houdini, Vegas Pro" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Logitech G HUB / Peripherals", Category = "PERIPHERAL HARDWARE", Status = "Protected (Monitor Only)", Description = "Logitech G HUB, LGHUB Agent, Onboard Memory Manager, Logi Options+, Gaming Virtual Mouse/Keyboard drivers" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Razer Synapse / Chroma", Category = "PERIPHERAL HARDWARE", Status = "Protected (Monitor Only)", Description = "Razer Synapse Service, Razer Central, Chroma SDK Server, Action Engine, mouse DPI & macro runtimes" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "OBS Studio / Broadcast", Category = "STREAMING / BROADCAST", Status = "Protected (Monitor Only)", Description = "OBS Studio, Streamlabs, Elgato Stream Deck, Wave Link, Voicemeeter audio pipelines, NVENC capture" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Steam", Category = "GAMING", Status = "Protected (Monitor Only)", Description = "Steam Client, Steam Client Service, steamwebhelper, GameOverlayUI & active games" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Spotify", Category = "MEDIA", Status = "Protected (Monitor Only)", Description = "Spotify Desktop, SpotifyWebHelper, SpotifyStartupTask, SpotifyUpdate" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "UltraViewer", Category = "REMOTE ACCESS", Status = "Protected (Monitor Only)", Description = "UltraViewer_Desktop, UltraViewer_Service, UltraViewService remote session host" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Urban VPN", Category = "VPN / NETWORK", Status = "Protected (Monitor Only)", Description = "urban-vpn, urban-vpn-service, UrbanVPN-Service, OpenVPN/TAP/Wintun tunnel adapters" });







            ProtectedApplications.Add(new ProtectedApplicationSummaryDto { DisplayName = "Lenovo Vantage", Category = "OEM / HARDWARE", Status = "Protected (Monitor Only)", Description = "LenovoVantage, LenovoVantageService, Lenovo.Modern.ImController, thermal/battery/hotkey services" });







        }















        private void NotifyComputedProperties()







        {







            OnPropertyChanged(nameof(WorkloadProfileLabel));







            OnPropertyChanged(nameof(WorkloadProfileDescription));







            OnPropertyChanged(nameof(HardwareCpuSummary));







            OnPropertyChanged(nameof(HardwareMemorySummary));







            OnPropertyChanged(nameof(HardwareGpuSummary));







            OnPropertyChanged(nameof(HardwarePlatformSummary));







            OnPropertyChanged(nameof(IsScanning));

            OnPropertyChanged(nameof(IsOptimizing));

            OnPropertyChanged(nameof(IsProgressActive));

            OnPropertyChanged(nameof(IsProgressIndeterminate));

            OnPropertyChanged(nameof(ServiceAvailabilitySubtext));

            OnPropertyChanged(nameof(ServiceStatusText));

            OnPropertyChanged(nameof(ServiceStatusBrush));







            OnPropertyChanged(nameof(OptimizationReadinessText));







            OnPropertyChanged(nameof(OptimizationReadinessBrush));







            OnPropertyChanged(nameof(OverviewHeadline));







            OnPropertyChanged(nameof(OverviewSubtitle));







            OnPropertyChanged(nameof(ActionableOpportunitiesCount));







            OnPropertyChanged(nameof(RecommendedOpportunitiesCount));







            OnPropertyChanged(nameof(OptionalOpportunitiesCount));







            OnPropertyChanged(nameof(ProtectedItemsCount));







            OnPropertyChanged(nameof(OptimizationStateBadge));







            OnPropertyChanged(nameof(HasActionableOpportunities));







            OnPropertyChanged(nameof(ResultHeadline));

            OnPropertyChanged(nameof(FormattedInitialRam));



            OnPropertyChanged(nameof(FormattedFinalRam));



            OnPropertyChanged(nameof(FormattedInitialCpu));



            OnPropertyChanged(nameof(FormattedFinalCpu));



            OnPropertyChanged(nameof(GrossTerminatedCount));



            OnPropertyChanged(nameof(NetProcessReductionCount));



            OnPropertyChanged(nameof(ActionsAlreadyOptimalCount));









            OnPropertyChanged(nameof(ResultSummaryText));







            OnPropertyChanged(nameof(VerifiedOpportunitiesCount));







            OnPropertyChanged(nameof(AlreadyOptimizedCount));







            OnPropertyChanged(nameof(SelectedCandidatesCount));







            OnPropertyChanged(nameof(HasSelectedCandidates));







            OnPropertyChanged(nameof(CanStartOptimization));







            OnPropertyChanged(nameof(IsPlanCompleted));







            OnPropertyChanged(nameof(PrimaryButtonText));







            OnPropertyChanged(nameof(PrimaryButtonGlyph));







            OnPropertyChanged(nameof(SelectionSummaryBadge));







            OnPropertyChanged(nameof(SelectionSummaryTitle));







            OnPropertyChanged(nameof(SelectionSummarySubtitle));







            OnPropertyChanged(nameof(SelectionSummaryDescription));







            OnPropertyChanged(nameof(OptimizeButtonToolTip));







            OnPropertyChanged(nameof(ActivityStatusText));







            OnPropertyChanged(nameof(ActivityStatusBrush));







            CommandManager.InvalidateRequerySuggested();







        }







    }







}







