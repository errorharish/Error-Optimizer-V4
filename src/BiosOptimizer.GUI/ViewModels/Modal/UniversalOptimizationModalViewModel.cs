using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiosOptimizer.GUI.ViewModels.Base;
using BiosOptimizer.GUI.Services;

namespace BiosOptimizer.GUI.ViewModels.Modal
{
    public class UniversalOptimizationModalViewModel : INotifyPropertyChanged
    {
        private static readonly Lazy<UniversalOptimizationModalViewModel> _instance =
            new(() => new UniversalOptimizationModalViewModel());
        public static UniversalOptimizationModalViewModel Instance => _instance.Value;

        private readonly object _stateLock = new();
        private UniversalOptimizationModalState _currentState = UniversalOptimizationModalState.Closed;

        // Header Branding & Profile
        private string _brandingTitle = "ERROR OPTIMIZER";
        private string _versionBadge = "V4.2";
        private string _profileName = "NORMAL";
        private string _headerSubtitle = "Kernel Latency & System Architecture";
        private string _statusBadgeText = "NORMAL PROFILE";
        private Brush _statusBadgeBrush = new SolidColorBrush(Color.FromRgb(0x74, 0xE1, 0xFF));
        private Brush _statusBadgeBorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        private Brush _statusBadgeBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x33));

        private string _headerIconGlyph = "⚡";
        private Brush _headerIconForegroundBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        private Brush _headerIconBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x33));
        private Brush _headerIconBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));

        // State 1: Loading
        private string _engineBadgeText = "RING-0 COOPERATIVE ENGINE";
        private string _mainHeadingText = "OPTIMIZING NORMAL";
        private string _mainSubtitleText = "Kernel & System Optimization Pipeline";
        private int _phaseStep = 1;
        private string _phaseNumberText = "PHASE 1/5:";
        private string _phaseName = "ANALYZING SYSTEM";
        private double _progressPercent = 0.0;
        private string _telemetryLogText = "Initializing NT kernel hook handles...";
        private string _vssStatusText = "#419 Armed";
        private string _targetTimerText = "0.500 ms (Locked)";

        // State 2: Cancelling
        private double _rollbackProgressPercent = 0.0;
        private string _rollbackLogText = "Releasing Ring-0 Mutex lock...";

        // State 3: Cancelled
        private string _systemStateTileValue = "Unmodified";
        private string _restorePointTileValue = "#418 Armed";
        private string _ring0MutexTileValue = "Released";
        private string _stagedHivesTileValue = "0 Committed";

        // State 4: Success
        private string _successHeading = "OPTIMIZATION COMPLETED";
        private string _successSubtitle = "NORMAL · Successfully applied and verified.";
        private int _appliedCount = 48;
        private string _appliedArmedText = "100% Armed";
        private string _verifiedCount = "48/48";
        private string _zeroRegressText = "Zero Regress";
        private int _alreadyOptimalCount = 12;
        private string _alreadyOptimalText = "Bypassed";
        private int _protectedCount = 3;
        private string _protectedText = "HVCI Safe";
        private string _executionTimeText = "1.84s";
        private string _systemBackupText = "Restore Point #419";
        private string _rollbackStatusText = "Ready / Armored";
        private bool _isAdvancedDetailsExpanded = false;
        private string _selectedSuccessTab = "Kernel"; // "Kernel", "Network", "Interrupts", "Protected"

        // State 5: Failed
        private string _failedHeading = "OPTIMIZATION FAILED";
        private string _failedSubtitle = "The operation could not be completed.";
        private string _errorCodeText = "NTSTATUS 0xC0000022";
        private string _failedComponentText = "Kernel DPC Watchdog Hook Engine";
        private string _failureDescriptionText = "Virtualization-Based Security (VBS) Memory Integrity (HVCI) locked ring-0 descriptor tables. The operation was aborted to maintain kernel stability.";
        private string _failureRollbackNote = "All staged optimizations safely reverted. System state 100% stable.";
        private bool _isDiagnosticTraceExpanded = false;
        private string _diagnosticTraceLog = string.Empty;

        // Safety Confirmations
        private string _riskTitle = "HIGH RISK OPTIMIZATION WARNING";
        private string _riskOptimizationName = "Kernel DPC Optimization";
        private string _riskLevel = "HIGH RISK";
        private string _riskWarningReason = "Modifies sensitive kernel dispatch parameters.";
        private string _riskAffectedArea = "Kernel Latency & System Architecture";
        private string _riskConfirmationQuestion = "Do you want to proceed with execution?";
        private TaskCompletionSource<bool>? _riskTcs;

        private string _restorePointInputName = "Error Optimizer - Before Optimization";
        private TaskCompletionSource<string?>? _restorePointTcs;

        private string _systemProtectionDrive = "C:";
        private string _systemProtectionMessage = "System Protection is currently disabled for the Windows system drive.";
        private TaskCompletionSource<bool>? _protectionTcs;

        // Developer Preview Mode
        private bool _isPreviewModeActive = false;
        private double _previewSliderValue = 83.0;

        // Collections
        public ObservableCollection<OptimizationItemDetailDto> AdvancedDetailItems { get; } = new();
        public ObservableCollection<OptimizationItemDetailDto> FilteredDetailItems { get; } = new();
        public ObservableCollection<RollbackStepDto> RollbackSteps { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public UniversalOptimizationModalViewModel()
        {
            CancelOptimizationCommand = new RelayCommand(_ => OnUserCancelRequested());
            RetryCommand = new RelayCommand(_ => OnRetryRequested());
            ReturnToDashboardCommand = new RelayCommand(_ => Close());
            CloseCommand = new RelayCommand(_ => Close());
            ToggleAdvancedDetailsCommand = new RelayCommand(_ => IsAdvancedDetailsExpanded = !IsAdvancedDetailsExpanded);
            ToggleDiagnosticTraceCommand = new RelayCommand(_ => IsDiagnosticTraceExpanded = !IsDiagnosticTraceExpanded);
            CopyLogCommand = new RelayCommand(_ => CopyDiagnosticLog());
            SelectSuccessTabCommand = new RelayCommand(param => SelectedSuccessTab = param?.ToString() ?? "Kernel");

            // Confirmation Commands
            ConfirmRiskCommand = new RelayCommand(_ =>
            {
                var tcs = _riskTcs;
                _riskTcs = null;
                tcs?.TrySetResult(true);
            });
            DenyRiskCommand = new RelayCommand(_ =>
            {
                var tcs = _riskTcs;
                _riskTcs = null;
                Close();
                tcs?.TrySetResult(false);
            });

            ConfirmRestorePointCommand = new RelayCommand(_ =>
            {
                var tcs = _restorePointTcs;
                _restorePointTcs = null;
                string chosen = string.IsNullOrWhiteSpace(RestorePointInputName)
                    ? "Error Optimizer - Before Optimization"
                    : RestorePointInputName.Trim();
                tcs?.TrySetResult(chosen);
            });
            CancelRestorePointCommand = new RelayCommand(_ =>
            {
                var tcs = _restorePointTcs;
                _restorePointTcs = null;
                Close();
                tcs?.TrySetResult(null);
            });

            AuthorizeSystemProtectionCommand = new RelayCommand(_ =>
            {
                var tcs = _protectionTcs;
                _protectionTcs = null;
                tcs?.TrySetResult(true);
            });
            DenySystemProtectionCommand = new RelayCommand(_ =>
            {
                var tcs = _protectionTcs;
                _protectionTcs = null;
                Close();
                tcs?.TrySetResult(false);
            });

            // Preview commands
            PreviewSetStateCommand = new RelayCommand(param =>
            {
                if (param is string stateStr && Enum.TryParse<UniversalOptimizationModalState>(stateStr, true, out var st))
                {
                    SetPreviewState(st, ProfileName);
                }
            });

            PreviewFlowCommand = new RelayCommand(async param =>
            {
                if (param is string flow)
                {
                    await RunTestFlowAsync(flow);
                }
            });

            // Initialize default rollback checklist steps
            InitializeRollbackSteps();
        }

        #region Public Bindable Properties

        public UniversalOptimizationModalState CurrentState
        {
            get => _currentState;
            set
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsOpen));
                    OnPropertyChanged(nameof(IsLoading));
                    OnPropertyChanged(nameof(IsCancelling));
                    OnPropertyChanged(nameof(IsCancelled));
                    OnPropertyChanged(nameof(IsSuccess));
                    OnPropertyChanged(nameof(IsFailed));
                    OnPropertyChanged(nameof(IsRiskConfirmation));
                    OnPropertyChanged(nameof(IsRestorePointPrompt));
                    OnPropertyChanged(nameof(IsSystemProtectionPrompt));
                    UpdateHeaderByState();
                }
            }
        }

        public bool IsOpen => _currentState != UniversalOptimizationModalState.Closed;
        public bool IsLoading => _currentState == UniversalOptimizationModalState.Loading;
        public bool IsCancelling => _currentState == UniversalOptimizationModalState.Cancelling;
        public bool IsCancelled => _currentState == UniversalOptimizationModalState.Cancelled;
        public bool IsSuccess => _currentState == UniversalOptimizationModalState.Success;
        public bool IsFailed => _currentState == UniversalOptimizationModalState.Failed;
        public bool IsRiskConfirmation => _currentState == UniversalOptimizationModalState.RiskConfirmation;
        public bool IsRestorePointPrompt => _currentState == UniversalOptimizationModalState.RestorePointPrompt;
        public bool IsSystemProtectionPrompt => _currentState == UniversalOptimizationModalState.SystemProtectionPrompt;

        // Header Properties
        public string BrandingTitle { get => _brandingTitle; set { _brandingTitle = value; OnPropertyChanged(); } }
        public string VersionBadge { get => _versionBadge; set { _versionBadge = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsVersionBadgeVisible)); } }
        public bool IsVersionBadgeVisible => !string.IsNullOrEmpty(_versionBadge);
        public string ProfileName
        {
            get => _profileName;
            set
            {
                _profileName = value;
                OnPropertyChanged();
                MainHeadingText = $"OPTIMIZING {_profileName.ToUpperInvariant()}";
                SuccessSubtitle = $"{_profileName.ToUpperInvariant()} — Successfully applied and verified.";
                if (_currentState == UniversalOptimizationModalState.Loading)
                {
                    StatusBadgeText = $"● {_profileName.ToUpperInvariant()}";
                }
            }
        }
        public string HeaderSubtitle { get => _headerSubtitle; set { _headerSubtitle = value; OnPropertyChanged(); } }
        public string StatusBadgeText { get => _statusBadgeText; set { _statusBadgeText = value; OnPropertyChanged(); } }
        public Brush StatusBadgeBrush { get => _statusBadgeBrush; set { _statusBadgeBrush = value; OnPropertyChanged(); } }
        public Brush StatusBadgeBorderBrush { get => _statusBadgeBorderBrush; set { _statusBadgeBorderBrush = value; OnPropertyChanged(); } }
        public Brush StatusBadgeBackgroundBrush { get => _statusBadgeBackgroundBrush; set { _statusBadgeBackgroundBrush = value; OnPropertyChanged(); } }

        public string HeaderIconGlyph { get => _headerIconGlyph; set { _headerIconGlyph = value; OnPropertyChanged(); } }
        public Brush HeaderIconForegroundBrush { get => _headerIconForegroundBrush; set { _headerIconForegroundBrush = value; OnPropertyChanged(); } }
        public Brush HeaderIconBackgroundBrush { get => _headerIconBackgroundBrush; set { _headerIconBackgroundBrush = value; OnPropertyChanged(); } }
        public Brush HeaderIconBorderBrush { get => _headerIconBorderBrush; set { _headerIconBorderBrush = value; OnPropertyChanged(); } }

        // State 1: Loading
        public string EngineBadgeText { get => _engineBadgeText; set { _engineBadgeText = value; OnPropertyChanged(); } }
        public string MainHeadingText { get => _mainHeadingText; set { _mainHeadingText = value; OnPropertyChanged(); } }
        public string MainSubtitleText { get => _mainSubtitleText; set { _mainSubtitleText = value; OnPropertyChanged(); } }

        public int PhaseStep
        {
            get => _phaseStep;
            set
            {
                _phaseStep = Math.Clamp(value, 1, 5);
                PhaseNumberText = $"PHASE {_phaseStep}/5:";
                OnPropertyChanged();
                OnPropertyChanged(nameof(CenteredPhaseBadgeText));
            }
        }
        public string PhaseNumberText { get => _phaseNumberText; private set { _phaseNumberText = value; OnPropertyChanged(); } }
        public string PhaseName
        {
            get => _phaseName;
            set
            {
                _phaseName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CenteredPhaseBadgeText));
            }
        }
        public string CenteredPhaseBadgeText => $"PHASE {_phaseStep}: {_phaseName.ToUpperInvariant()}";

        public double ProgressPercent
        {
            get => _progressPercent;
            set
            {
                _progressPercent = Math.Clamp(value, 0.0, 100.0);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercentInt));
                OnPropertyChanged(nameof(ProgressPercentFormatted));
            }
        }
        public int ProgressPercentInt => (int)Math.Round(_progressPercent);
        public string ProgressPercentFormatted => $"{ProgressPercentInt}%";
        public string TelemetryLogText { get => _telemetryLogText; set { _telemetryLogText = value; OnPropertyChanged(); } }
        public string VssStatusText { get => _vssStatusText; set { _vssStatusText = value; OnPropertyChanged(); } }
        public string TargetTimerText { get => _targetTimerText; set { _targetTimerText = value; OnPropertyChanged(); } }

        // State 2: Cancelling
        public double RollbackProgressPercent
        {
            get => _rollbackProgressPercent;
            set
            {
                _rollbackProgressPercent = Math.Clamp(value, 0.0, 100.0);
                OnPropertyChanged();
                OnPropertyChanged(nameof(RollbackProgressPercentFormatted));
                UpdateRollbackSteps(_rollbackProgressPercent, _rollbackLogText);
            }
        }
        public string RollbackProgressPercentFormatted => $"{(int)Math.Round(_rollbackProgressPercent)}%";
        public string RollbackLogText
        {
            get => _rollbackLogText;
            set
            {
                _rollbackLogText = value;
                OnPropertyChanged();
                UpdateRollbackSteps(_rollbackProgressPercent, _rollbackLogText);
            }
        }

        // State 3: Cancelled
        public string SystemStateTileValue { get => _systemStateTileValue; set { _systemStateTileValue = value; OnPropertyChanged(); } }
        public string RestorePointTileValue { get => _restorePointTileValue; set { _restorePointTileValue = value; OnPropertyChanged(); } }
        public string Ring0MutexTileValue { get => _ring0MutexTileValue; set { _ring0MutexTileValue = value; OnPropertyChanged(); } }
        public string StagedHivesTileValue { get => _stagedHivesTileValue; set { _stagedHivesTileValue = value; OnPropertyChanged(); } }

        // State 4: Success
        public string SuccessHeading { get => _successHeading; set { _successHeading = value; OnPropertyChanged(); } }
        public string SuccessSubtitle { get => _successSubtitle; set { _successSubtitle = value; OnPropertyChanged(); } }
        public int AppliedCount { get => _appliedCount; set { _appliedCount = value; OnPropertyChanged(); } }
        public string AppliedArmedText { get => _appliedArmedText; set { _appliedArmedText = value; OnPropertyChanged(); } }
        public string VerifiedCount { get => _verifiedCount; set { _verifiedCount = value; OnPropertyChanged(); } }
        public string ZeroRegressText { get => _zeroRegressText; set { _zeroRegressText = value; OnPropertyChanged(); } }
        public int AlreadyOptimalCount { get => _alreadyOptimalCount; set { _alreadyOptimalCount = value; OnPropertyChanged(); } }
        public string AlreadyOptimalText { get => _alreadyOptimalText; set { _alreadyOptimalText = value; OnPropertyChanged(); } }
        public int ProtectedCount { get => _protectedCount; set { _protectedCount = value; OnPropertyChanged(); } }
        public string ProtectedText { get => _protectedText; set { _protectedText = value; OnPropertyChanged(); } }
        public string ExecutionTimeText { get => _executionTimeText; set { _executionTimeText = value; OnPropertyChanged(); } }
        public string SystemBackupText { get => _systemBackupText; set { _systemBackupText = value; OnPropertyChanged(); } }
        public string RollbackStatusText { get => _rollbackStatusText; set { _rollbackStatusText = value; OnPropertyChanged(); } }

        public bool IsAdvancedDetailsExpanded
        {
            get => _isAdvancedDetailsExpanded;
            set
            {
                _isAdvancedDetailsExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AdvancedDetailsToggleText));
                OnPropertyChanged(nameof(AdvancedDetailsChevronGlyph));
            }
        }
        public string AdvancedDetailsToggleText => _isAdvancedDetailsExpanded ? "[-] HIDE DETAILS" : "[+] VIEW DETAILS";
        public string AdvancedDetailsChevronGlyph => _isAdvancedDetailsExpanded ? "˄" : "˅";

        public string SelectedSuccessTab
        {
            get => _selectedSuccessTab;
            set
            {
                if (_selectedSuccessTab != value)
                {
                    _selectedSuccessTab = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsKernelTabSelected));
                    OnPropertyChanged(nameof(IsNetworkTabSelected));
                    OnPropertyChanged(nameof(IsInterruptsTabSelected));
                    OnPropertyChanged(nameof(IsProtectedTabSelected));
                    UpdateFilteredDetailItems();
                }
            }
        }
        public bool IsKernelTabSelected => _selectedSuccessTab.Equals("Kernel", StringComparison.OrdinalIgnoreCase);
        public bool IsNetworkTabSelected => _selectedSuccessTab.Equals("Network", StringComparison.OrdinalIgnoreCase);
        public bool IsInterruptsTabSelected => _selectedSuccessTab.Equals("Interrupts", StringComparison.OrdinalIgnoreCase);
        public bool IsProtectedTabSelected => _selectedSuccessTab.Equals("Protected", StringComparison.OrdinalIgnoreCase);

        // State 5: Failed
        public string FailedHeading { get => _failedHeading; set { _failedHeading = value; OnPropertyChanged(); } }
        public string FailedSubtitle { get => _failedSubtitle; set { _failedSubtitle = value; OnPropertyChanged(); } }
        public string ErrorCodeText { get => _errorCodeText; set { _errorCodeText = value; OnPropertyChanged(); } }
        public string FailedComponentText { get => _failedComponentText; set { _failedComponentText = value; OnPropertyChanged(); } }
        public string FailureDescriptionText { get => _failureDescriptionText; set { _failureDescriptionText = value; OnPropertyChanged(); } }
        public string FailureRollbackNote { get => _failureRollbackNote; set { _failureRollbackNote = value; OnPropertyChanged(); } }
        public bool IsDiagnosticTraceExpanded
        {
            get => _isDiagnosticTraceExpanded;
            set
            {
                _isDiagnosticTraceExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiagnosticTraceToggleText));
                OnPropertyChanged(nameof(DiagnosticTraceChevronGlyph));
            }
        }
        public string DiagnosticTraceToggleText => _isDiagnosticTraceExpanded ? "[-] HIDE DETAILS" : "[+] VIEW DETAILS";
        public string DiagnosticTraceChevronGlyph => _isDiagnosticTraceExpanded ? "˄" : "˅";
        public string DiagnosticTraceLog { get => _diagnosticTraceLog; set { _diagnosticTraceLog = value; OnPropertyChanged(); } }

        // Safety Confirmations
        public string RiskTitle { get => _riskTitle; set { _riskTitle = value; OnPropertyChanged(); } }
        public string RiskOptimizationName { get => _riskOptimizationName; set { _riskOptimizationName = value; OnPropertyChanged(); } }
        public string RiskLevel { get => _riskLevel; set { _riskLevel = value; OnPropertyChanged(); } }
        public string RiskWarningReason { get => _riskWarningReason; set { _riskWarningReason = value; OnPropertyChanged(); } }
        public string RiskAffectedArea { get => _riskAffectedArea; set { _riskAffectedArea = value; OnPropertyChanged(); } }
        public string RiskConfirmationQuestion { get => _riskConfirmationQuestion; set { _riskConfirmationQuestion = value; OnPropertyChanged(); } }

        public string RestorePointInputName { get => _restorePointInputName; set { _restorePointInputName = value; OnPropertyChanged(); } }

        public string SystemProtectionDrive { get => _systemProtectionDrive; set { _systemProtectionDrive = value; OnPropertyChanged(); } }
        public string SystemProtectionMessage { get => _systemProtectionMessage; set { _systemProtectionMessage = value; OnPropertyChanged(); } }

        // Preview & Demonstration Mode
        public bool IsPreviewModeActive
        {
            get => _isPreviewModeActive;
            set
            {
                _isPreviewModeActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPrototypeControllerVisible));
            }
        }
        public bool IsPrototypeControllerVisible => false;

        public double PreviewSliderValue
        {
            get => _previewSliderValue;
            set
            {
                _previewSliderValue = value;
                OnPropertyChanged();
                ProgressPercent = _previewSliderValue;
            }
        }

        #endregion

        #region Commands

        public ICommand CancelOptimizationCommand { get; }
        public ICommand RetryCommand { get; }
        public ICommand ReturnToDashboardCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand ToggleAdvancedDetailsCommand { get; }
        public ICommand ToggleDiagnosticTraceCommand { get; }
        public ICommand CopyLogCommand { get; }
        public ICommand SelectSuccessTabCommand { get; }

        public ICommand ConfirmRiskCommand { get; }
        public ICommand DenyRiskCommand { get; }
        public ICommand ConfirmRestorePointCommand { get; }
        public ICommand CancelRestorePointCommand { get; }
        public ICommand AuthorizeSystemProtectionCommand { get; }
        public ICommand DenySystemProtectionCommand { get; }

        public ICommand PreviewSetStateCommand { get; }
        public ICommand PreviewFlowCommand { get; }

        #endregion

        #region State Machine & Transition Coordinator

        public bool RequestTransition(UniversalOptimizationModalState targetState)
        {
            lock (_stateLock)
            {
                // Never transition to Success if operation was cancelled or failed
                if (targetState == UniversalOptimizationModalState.Success)
                {
                    if (_currentState == UniversalOptimizationModalState.Cancelling ||
                        _currentState == UniversalOptimizationModalState.Cancelled ||
                        _currentState == UniversalOptimizationModalState.Failed ||
                        _currentState == UniversalOptimizationModalState.Closed)
                    {
                        return false;
                    }
                }

                // If currently cancelled or failed, only explicit retry can transition to Loading
                if ((_currentState == UniversalOptimizationModalState.Cancelled || _currentState == UniversalOptimizationModalState.Failed) &&
                    targetState != UniversalOptimizationModalState.Loading && targetState != UniversalOptimizationModalState.Closed)
                {
                    return false;
                }

                RunOnUi(() => CurrentState = targetState);
                return true;
            }
        }

        public static void RunOnUi(Action action)
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        }

        private void UpdateHeaderByState()
        {
            switch (_currentState)
            {
                case UniversalOptimizationModalState.Loading:
                    VersionBadge = "V4.2";
                    HeaderSubtitle = "Kernel Latency & System Architecture";
                    StatusBadgeText = $"● {_profileName.ToUpperInvariant()}";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x26));
                    HeaderIconGlyph = "⚡";
                    HeaderIconForegroundBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                    HeaderIconBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x26));
                    HeaderIconBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));
                    break;

                case UniversalOptimizationModalState.Cancelling:
                    VersionBadge = string.Empty;
                    HeaderSubtitle = "Transaction Rollback Engine";
                    StatusBadgeText = "ABORTING";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    HeaderIconGlyph = "⚠";
                    HeaderIconForegroundBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    HeaderIconBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x26));
                    HeaderIconBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));
                    break;

                case UniversalOptimizationModalState.Cancelled:
                    VersionBadge = string.Empty;
                    HeaderSubtitle = "Operation Aborted";
                    StatusBadgeText = "● CANCELLED";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    HeaderIconGlyph = "!";
                    HeaderIconForegroundBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    HeaderIconBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x26));
                    HeaderIconBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));
                    break;

                case UniversalOptimizationModalState.Success:
                    VersionBadge = string.Empty;
                    HeaderSubtitle = "Optimization Result";
                    StatusBadgeText = "✓ VERIFIED ACTIVE";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x10, 0xB9, 0x81));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x20, 0x10, 0xB9, 0x81));
                    HeaderIconGlyph = "⚡";
                    HeaderIconForegroundBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    HeaderIconBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x26));
                    HeaderIconBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));
                    break;

                case UniversalOptimizationModalState.Failed:
                    VersionBadge = string.Empty;
                    HeaderSubtitle = "Execution Interrupted";
                    StatusBadgeText = "● ERROR 0xC0000022";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xEF, 0x44, 0x44));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xEF, 0x44, 0x44));
                    HeaderIconGlyph = "🛡";
                    HeaderIconForegroundBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    HeaderIconBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x26));
                    HeaderIconBorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x44));
                    break;

                case UniversalOptimizationModalState.RiskConfirmation:
                    HeaderSubtitle = "Safety Guardrails";
                    StatusBadgeText = "CONFIRMATION REQUIRED";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    break;

                case UniversalOptimizationModalState.RestorePointPrompt:
                    HeaderSubtitle = "Windows System Protection";
                    StatusBadgeText = "SYSTEM SNAPSHOT";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x33));
                    break;

                case UniversalOptimizationModalState.SystemProtectionPrompt:
                    HeaderSubtitle = "Windows Drive Protection";
                    StatusBadgeText = "PROTECTION REQUIRED";
                    StatusBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    StatusBadgeBorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B));
                    StatusBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    break;

                default:
                    break;
            }
        }

        public void Close()
        {
            CurrentState = UniversalOptimizationModalState.Closed;
            OptimizationProgressService.Instance.Close();
        }

        private void OnUserCancelRequested()
        {
            OptimizationProgressService.Instance.CancelOperation();
        }

        private void OnRetryRequested()
        {
            _ = OptimizationProgressService.Instance.RetryAsync();
        }

        private void CopyDiagnosticLog()
        {
            try
            {
                string text = !string.IsNullOrWhiteSpace(_diagnosticTraceLog)
                    ? _diagnosticTraceLog
                    : $"{FailedComponentText}\nError: {ErrorCodeText}\n{FailureDescriptionText}";
                Clipboard.SetText(text);
            }
            catch { }
        }

        #endregion

        #region Rollback Steps & Subsystem Details Management

        public void InitializeRollbackSteps()
        {
            RollbackSteps.Clear();
            RollbackSteps.Add(new RollbackStepDto { StepNumber = 1, Title = "Releasing Ring-0 Synchronization Mutex", Status = "Pending", IconGlyph = "○" });
            RollbackSteps.Add(new RollbackStepDto { StepNumber = 2, Title = "Reverting NtSetTimerResolution hook (15.625ms restored)", Status = "Pending", IconGlyph = "○" });
            RollbackSteps.Add(new RollbackStepDto { StepNumber = 3, Title = "Flushing temporary staged registry hives", Status = "Pending", IconGlyph = "○" });
            RollbackSteps.Add(new RollbackStepDto { StepNumber = 4, Title = "Verifying VSS snapshot #418 integrity", Status = "Pending", IconGlyph = "○" });
        }

        public void UpdateRollbackSteps(double progressPercent, string currentAction = "")
        {
            if (RollbackSteps.Count == 0)
            {
                InitializeRollbackSteps();
            }

            int activeIdx = progressPercent switch
            {
                < 25 => 0,
                < 50 => 1,
                < 75 => 2,
                _ => 3
            };

            for (int i = 0; i < RollbackSteps.Count; i++)
            {
                var step = RollbackSteps[i];
                if (i < activeIdx)
                {
                    step.Status = "Completed";
                    step.IconGlyph = "✔";
                    step.IconBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Emerald
                    step.TextBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)); // White
                }
                else if (i == activeIdx)
                {
                    step.Status = "Active";
                    step.IconGlyph = "⟳";
                    step.IconBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber
                    step.TextBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber
                }
                else
                {
                    step.Status = "Pending";
                    step.IconGlyph = "○";
                    step.IconBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)); // Slate
                    step.TextBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)); // Slate
                }
            }
        }

        public void UpdateFilteredDetailItems()
        {
            FilteredDetailItems.Clear();
            string target = _selectedSuccessTab.ToUpperInvariant();
            foreach (var item in AdvancedDetailItems)
            {
                string cat = item.Category.ToUpperInvariant();
                bool matches = target switch
                {
                    "KERNEL" => cat.Contains("KERNEL") || cat.Contains("CPU") || cat.Contains("TIMER") || cat.Contains("SYS"),
                    "NETWORK" => cat.Contains("NET") || cat.Contains("TCP") || cat.Contains("BUFFER"),
                    "INTERRUPTS" => cat.Contains("INTERRUPT") || cat.Contains("MSI") || cat.Contains("GPU") || cat.Contains("DEVICE"),
                    "PROTECTED" => cat.Contains("PROTECT") || cat.Contains("HVCI") || cat.Contains("SECURITY") || cat.Contains("SKIP"),
                    _ => true
                };
                if (matches)
                {
                    FilteredDetailItems.Add(item);
                }
            }
        }

        #endregion

        #region Preview & Demonstration Support

        public void SetPreviewState(UniversalOptimizationModalState state, string profile = "NORMAL")
        {
            IsPreviewModeActive = true;
            ProfileName = profile;

            if (state == UniversalOptimizationModalState.Loading)
            {
                PhaseStep = 4;
                PhaseName = "VERIFYING CHANGES";
                ProgressPercent = 83.0;
                TelemetryLogText = "Validating thread quantum allocation on CPU cores 0-15...";
                VssStatusText = "#419 Armed";
                TargetTimerText = "0.500ms";
            }
            else if (state == UniversalOptimizationModalState.Cancelling)
            {
                RollbackProgressPercent = 75.0;
                RollbackLogText = "Flushing temporary registry keys from Session Manager...";
                UpdateRollbackSteps(75.0, "Flushing temporary registry keys from Session Manager...");
            }
            else if (state == UniversalOptimizationModalState.Cancelled)
            {
                SystemStateTileValue = "Unmodified";
                RestorePointTileValue = "#418 Armed";
                Ring0MutexTileValue = "Released";
                StagedHivesTileValue = "0 Committed";
            }
            else if (state == UniversalOptimizationModalState.Success)
            {
                SuccessHeading = "OPTIMIZATION COMPLETED";
                SuccessSubtitle = $"{profile.ToUpperInvariant()} — Successfully applied and verified.";
                AppliedCount = 48;
                AppliedArmedText = "100% Armed";
                VerifiedCount = "48/48";
                ZeroRegressText = "Zero Regress";
                AlreadyOptimalCount = 12;
                AlreadyOptimalText = "Bypassed";
                ProtectedCount = 3;
                ProtectedText = "HVCI Safe";
                ExecutionTimeText = "1.84s";
                SystemBackupText = "Restore Point #41...";
                RollbackStatusText = "Ready / Armored";

                if (AdvancedDetailItems.Count == 0)
                {
                    // Kernel Precision
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "NtSetTimerResolution", Category = "KERNEL", Status = "VERIFIED", DetailNote = "15.625ms -> 0.500ms (Global Hook Locked)", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "Win32PrioritySeparation", Category = "KERNEL", Status = "VERIFIED", DetailNote = "0x02 -> 0x26 (Short variable quantum)", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "SystemResponsiveness", Category = "KERNEL", Status = "VERIFIED", DetailNote = "20 -> 10 (10% Background / 90% Multimedia Quota)", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });

                    // Network Stack
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "TcpAckFrequency & TCPNoDelay", Category = "NETWORK", Status = "OPTIMAL", DetailNote = "Nagle algorithm suppressed, Immediate ACK enabled", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "NetworkThrottlingIndex", Category = "NETWORK", Status = "VERIFIED", DetailNote = "0xFFFFFFFF (Gaming network throttle disabled)", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });

                    // Interrupt Steering
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "GPU & NVMe MSI-X Vector Steering", Category = "INTERRUPTS", Status = "STEERED", DetailNote = "Target: Dedicated CPU Core 2 (Mask 0x04)", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "IRQL Thread Affinity", Category = "INTERRUPTS", Status = "VERIFIED", DetailNote = "Direct dispatch to Core 2-3", StatusBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });

                    // Protected Hardware
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "HVCI Memory Integrity & TPM PCR Banks", Category = "PROTECTED", Status = "PROTECTED", DetailNote = "Security policy detected; ring-0 modification safely bypassed", StatusBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) });
                    AdvancedDetailItems.Add(new OptimizationItemDetailDto { Name = "SecureBoot Policy Tables", Category = "PROTECTED", Status = "PROTECTED", DetailNote = "Hardware measurement verified unmodified", StatusBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) });
                }
                UpdateFilteredDetailItems();
            }
            else if (state == UniversalOptimizationModalState.Failed)
            {
                FailedHeading = "OPTIMIZATION FAILED";
                FailedSubtitle = "The operation could not be completed.";
                ErrorCodeText = "STATUS_ACCESS_DENIED";
                FailedComponentText = "Kernel DPC Watchdog Hook Engine";
                FailureDescriptionText = "Virtualization-Based Security (VBS) Memory Integrity (HVCI) rejected modification of the kernel DPC execution timer pointer. All staged optimizations have been automatically and safely reverted.";
                FailureRollbackNote = "Safe Rollback Executed: System state is 100% stable and intact.";
                DiagnosticTraceLog = "[19:24:12.802] [NT_ENTRY] Allocating Ring-0 hook table at 0xFFFFF800...\n[19:24:12.845] [VSS_CHECK] Volume Shadow Copy restore point #419 armed.\n[19:24:12.890] [HVCI_FAULT] KERNEL_PAGE_FAULT at KeSetTimerResolution+0x42\n[19:24:12.895] [EXCEPTION] STATUS_ACCESS_DENIED (0xC0000022) - Ring-0 pointer write locked by Hyper-V VBS.\n[19:24:12.920] [ROLLBACK] Invoking atomic revert pipeline...\n[19:24:12.960] [ROLLBACK_SUCCESS] Hives flushed. System registry clean.";
            }

            CurrentState = state;
        }

        public async Task RunTestFlowAsync(string flowName)
        {
            if (flowName.Equals("LoadingSuccess", StringComparison.OrdinalIgnoreCase))
            {
                SetPreviewState(UniversalOptimizationModalState.Loading);
                for (int i = 0; i <= 100; i += 20)
                {
                    ProgressPercent = i;
                    PhaseStep = Math.Clamp((i / 20) + 1, 1, 5);
                    await Task.Delay(200);
                }
                SetPreviewState(UniversalOptimizationModalState.Success);
            }
            else if (flowName.Equals("LoadingCancelled", StringComparison.OrdinalIgnoreCase))
            {
                SetPreviewState(UniversalOptimizationModalState.Loading);
                ProgressPercent = 50;
                PhaseStep = 3;
                await Task.Delay(500);
                SetPreviewState(UniversalOptimizationModalState.Cancelling);
                for (int i = 25; i <= 100; i += 25)
                {
                    RollbackProgressPercent = i;
                    await Task.Delay(300);
                }
                SetPreviewState(UniversalOptimizationModalState.Cancelled);
            }
            else if (flowName.Equals("LoadingFailed", StringComparison.OrdinalIgnoreCase))
            {
                SetPreviewState(UniversalOptimizationModalState.Loading);
                ProgressPercent = 65;
                PhaseStep = 4;
                await Task.Delay(500);
                SetPreviewState(UniversalOptimizationModalState.Failed);
            }
        }

        #endregion

        #region INotifyPropertyChanged

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
