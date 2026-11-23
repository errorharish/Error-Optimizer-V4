#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels.Base;

namespace BiosOptimizer.GUI.ViewModels
{
    public class AutoOptimizeHistoryRowViewModel
    {
        public string TimestampText { get; set; } = "";
        public string ActionName { get; set; } = "";
        public string Status { get; set; } = "Completed";
        public string ResultText { get; set; } = "";
        public string TriggerSource { get; set; } = "Scheduled";
        public string InternalCommand { get; set; } = "";

        public Brush StatusBrush => Status.ToUpperInvariant() switch
        {
            "COMPLETED" => BrushHelper.GetFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81)), // Green
            "DEFERRED" or "SKIPPED" => BrushHelper.GetFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // Amber
            "FAILED" => BrushHelper.GetFrozenBrush(Color.FromRgb(0xEF, 0x44, 0x44)), // Red
            "RUNNING" => BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9)), // Indigo
            _ => BrushHelper.GetFrozenBrush(Color.FromRgb(0x8A, 0x8F, 0x98))
        };
    }

    public class AutoOptimizeViewModel : ViewModelBase
    {
        private readonly IIpcClient _ipc;
        private Timer? _uiDisplayRefreshTimer;
        private bool _suppressConfigCallback = false;

        // ── Schedule Options (Default = "None", no duplicate 60-minute labels) ──
        public ObservableCollection<string> ScheduleOptions { get; } = new()
        {
            "None",
            "5 minutes",
            "10 minutes",
            "30 minutes",
            "1 hour",
            "6 hours",
            "12 hours",
            "1 day"
        };

        private string _selectedScheduleOption = "None";
        public string SelectedScheduleOption
        {
            get => _selectedScheduleOption;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? "None" : value;
                if (_selectedScheduleOption != normalized)
                {
                    _selectedScheduleOption = normalized;
                    OnPropertyChanged();
                    NotifyConfigurationDependentProperties();
                    if (!_suppressConfigCallback)
                    {
                        _ = ApplyConfigurationChangeAsync();
                    }
                }
            }
        }

        private bool _cleanTempFilesSelected = false;
        public bool CleanTempFilesSelected
        {
            get => _cleanTempFilesSelected;
            set
            {
                if (_cleanTempFilesSelected != value)
                {
                    _cleanTempFilesSelected = value;
                    OnPropertyChanged();
                    NotifyConfigurationDependentProperties();
                    if (!_suppressConfigCallback)
                    {
                        _ = ApplyConfigurationChangeAsync();
                    }
                }
            }
        }

        private bool _freeStandbyMemorySelected = false;
        public bool FreeStandbyMemorySelected
        {
            get => _freeStandbyMemorySelected;
            set
            {
                if (_freeStandbyMemorySelected != value)
                {
                    _freeStandbyMemorySelected = value;
                    OnPropertyChanged();
                    NotifyConfigurationDependentProperties();
                    if (!_suppressConfigCallback)
                    {
                        _ = ApplyConfigurationChangeAsync();
                    }
                }
            }
        }

        public bool? IsAllAutoSelected
        {
            get
            {
                if (_cleanTempFilesSelected && _freeStandbyMemorySelected) return true;
                if (!_cleanTempFilesSelected && !_freeStandbyMemorySelected) return false;
                return null;
            }
            set
            {
                if (value.HasValue)
                {
                    _cleanTempFilesSelected = value.Value;
                    _freeStandbyMemorySelected = value.Value;
                    OnPropertyChanged(nameof(CleanTempFilesSelected));
                    OnPropertyChanged(nameof(FreeStandbyMemorySelected));
                    OnPropertyChanged(nameof(IsAllAutoSelected));
                    NotifyConfigurationDependentProperties();
                    if (!_suppressConfigCallback)
                    {
                        _ = ApplyConfigurationChangeAsync();
                    }
                }
            }
        }

        private bool _isAutoOptimizeEnabled = false;
        public bool IsAutoOptimizeEnabled
        {
            get => _isAutoOptimizeEnabled;
            private set
            {
                if (_isAutoOptimizeEnabled != value)
                {
                    _isAutoOptimizeEnabled = value;
                    OnPropertyChanged();
                    NotifyConfigurationDependentProperties();
                }
            }
        }

        private bool _isRunningNow = false;
        public bool IsRunningNow
        {
            get => _isRunningNow;
            private set
            {
                if (_isRunningNow != value)
                {
                    _isRunningNow = value;
                    OnPropertyChanged();
                    NotifyConfigurationDependentProperties();
                }
            }
        }

        private bool _isAdvancedDetailsExpanded = false;
        public bool IsAdvancedDetailsExpanded
        {
            get => _isAdvancedDetailsExpanded;
            set
            {
                if (_isAdvancedDetailsExpanded != value)
                {
                    _isAdvancedDetailsExpanded = value;
                    OnPropertyChanged();
                }
            }
        }

        // ── Service Availability (Single Authoritative Source) ────────────
        public bool IsServiceOnline => _ipc.ConnectionState == IpcConnectionState.Connected;
        public Visibility ServiceOnlineVisibility => IsServiceOnline ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ServiceOfflineVisibility => IsServiceOnline ? Visibility.Collapsed : Visibility.Visible;
        public string ServiceStatusText => _ipc.StatusText;
        public Brush ServiceStatusBrush => _ipc.StatusBrush;

        // ── Configuration Rules & Computed Flags ──────────────────────────
        public bool HasScheduleSelected =>
            SmartAutoOptimizeScheduler.ParseOption(_selectedScheduleOption) != AutoOptimizeScheduleOption.None;

        public bool HasAnyActionSelected =>
            _cleanTempFilesSelected || _freeStandbyMemorySelected;

        public bool CanEnableAutoOptimize =>
            !_isAutoOptimizeEnabled &&
            !_isRunningNow &&
            IsServiceOnline &&
            HasScheduleSelected &&
            HasAnyActionSelected;

        public bool CanDisableAutoOptimize =>
            _isAutoOptimizeEnabled && !_isRunningNow;

        public bool CanRunNow =>
            !_isRunningNow &&
            IsServiceOnline &&
            HasAnyActionSelected;

        public Visibility ShowEnableButtonVisibility =>
            _isAutoOptimizeEnabled ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ShowDisableButtonVisibility =>
            _isAutoOptimizeEnabled ? Visibility.Visible : Visibility.Collapsed;

        // ── Status Display (OFF / ON / RUNNING / UNAVAILABLE) ─────────────
        public string StatusValueText
        {
            get
            {
                if (!IsServiceOnline) return "Unavailable";
                if (_isRunningNow || SmartAutoOptimizeScheduler.Instance.IsExecuting) return "RUNNING";
                return _isAutoOptimizeEnabled ? "ON" : "OFF";
            }
        }

        public string StatusDotLabelText
        {
            get
            {
                if (!IsServiceOnline) return "Service offline — Scheduled maintenance will resume when the optimizer service is available.";
                if (_isRunningNow || SmartAutoOptimizeScheduler.Instance.IsExecuting) return "Running selected maintenance actions...";
                if (_isAutoOptimizeEnabled) return $"Auto Optimize on · Scheduled every {_selectedScheduleOption}";
                return "Auto Optimize off";
            }
        }

        public Brush StatusPrimaryBrush
        {
            get
            {
                if (!IsServiceOnline) return BrushHelper.GetFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber
                if (_isRunningNow || SmartAutoOptimizeScheduler.Instance.IsExecuting) return BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9)); // Indigo
                if (_isAutoOptimizeEnabled) return BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9)); // Subtle Indigo
                return BrushHelper.GetFrozenBrush(Color.FromRgb(0x6E, 0x74, 0x80)); // Grey
            }
        }

        public Visibility ActiveGreenDotVisibility =>
            IsServiceOnline && _isAutoOptimizeEnabled && !_isRunningNow ? Visibility.Visible : Visibility.Collapsed;

        public string SelectedActionsSummaryText
        {
            get
            {
                if (_cleanTempFilesSelected && _freeStandbyMemorySelected)
                    return "Clean Temp Files, Free Standby Memory";
                if (_cleanTempFilesSelected)
                    return "Clean Temp Files";
                if (_freeStandbyMemorySelected)
                    return "Free Standby Memory";
                return "None";
            }
        }

        public string ValidationHelperText
        {
            get
            {
                if (!IsServiceOnline)
                    return "Scheduled maintenance will resume when the optimizer service is available.";
                if (!HasAnyActionSelected)
                    return "Select at least one maintenance action.";
                if (!HasScheduleSelected && !_isAutoOptimizeEnabled)
                    return "Select a schedule interval to enable recurring maintenance, or click Run Now for a one-time run.";
                if (!_isAutoOptimizeEnabled)
                    return "Configuration ready. Click Enable Auto Optimize to start scheduled maintenance.";
                return $"Scheduled to run every {_selectedScheduleOption}.";
            }
        }

        public Brush ValidationHelperBrush
        {
            get
            {
                if (!IsServiceOnline) return BrushHelper.GetFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                if (!HasAnyActionSelected) return BrushHelper.GetFrozenBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
                if (_isAutoOptimizeEnabled) return BrushHelper.GetFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                return BrushHelper.GetFrozenBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
            }
        }

        // ── Next Run & Last Run Display ───────────────────────────────────
        private string _nextRunText = "—";
        public string NextRunText
        {
            get => _nextRunText;
            private set { _nextRunText = value; OnPropertyChanged(); }
        }

        private string _lastRunTimeText = "Never";
        public string LastRunTimeText
        {
            get => _lastRunTimeText;
            private set { _lastRunTimeText = value; OnPropertyChanged(); }
        }

        private string _lastRunStatusText = "—";
        public string LastRunStatusText
        {
            get => _lastRunStatusText;
            private set
            {
                _lastRunStatusText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastRunStatusBrush));
            }
        }

        public Brush LastRunStatusBrush => _lastRunStatusText.ToUpperInvariant() switch
        {
            "COMPLETED" => BrushHelper.GetFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81)), // Green
            "DEFERRED" or "SKIPPED" => BrushHelper.GetFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // Amber
            "FAILED" => BrushHelper.GetFrozenBrush(Color.FromRgb(0xEF, 0x44, 0x44)), // Red
            "RUNNING" => BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9)), // Indigo
            _ => BrushHelper.GetFrozenBrush(Color.FromRgb(0x6E, 0x74, 0x80)) // Grey
        };

        private string _lastRunActionsText = "—";
        public string LastRunActionsText
        {
            get => _lastRunActionsText;
            private set { _lastRunActionsText = value; OnPropertyChanged(); }
        }

        private string _lastRunResultText = "—";
        public string LastRunResultText
        {
            get => _lastRunResultText;
            private set { _lastRunResultText = value; OnPropertyChanged(); }
        }

        // ── Run History ───────────────────────────────────────────────────
        public ObservableCollection<AutoOptimizeHistoryRowViewModel> RunHistoryItems { get; } = new();
        public bool HasRunHistory => RunHistoryItems.Count > 0;
        public Visibility HasHistoryVisibility => HasRunHistory ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EmptyHistoryVisibility => HasRunHistory ? Visibility.Collapsed : Visibility.Visible;

        // ── Expandable Advanced Details ───────────────────────────────────
        private string _advSchedulerState = "OFF (Unscheduled)";
        public string AdvSchedulerState { get => _advSchedulerState; private set { _advSchedulerState = value; OnPropertyChanged(); } }

        private string _advTaskIdentifier = @"BiosOptimizer.Service + \ErrorOptimizer\SmartAutoOptimizeTask";
        public string AdvTaskIdentifier { get => _advTaskIdentifier; private set { _advTaskIdentifier = value; OnPropertyChanged(); } }

        private string _advInternalCommand = "None configured";
        public string AdvInternalCommand { get => _advInternalCommand; private set { _advInternalCommand = value; OnPropertyChanged(); } }

        private string _advExecutionTimestamp = "Never";
        public string AdvExecutionTimestamp { get => _advExecutionTimestamp; private set { _advExecutionTimestamp = value; OnPropertyChanged(); } }

        private string _advExecutionResult = "None";
        public string AdvExecutionResult { get => _advExecutionResult; private set { _advExecutionResult = value; OnPropertyChanged(); } }

        private string _advServiceStatus = "Service online";
        public string AdvServiceStatus { get => _advServiceStatus; private set { _advServiceStatus = value; OnPropertyChanged(); } }

        private string _advDeferReason = "None";
        public string AdvDeferReason { get => _advDeferReason; private set { _advDeferReason = value; OnPropertyChanged(); } }

        private string _advRetryReason = "None";
        public string AdvRetryReason { get => _advRetryReason; private set { _advRetryReason = value; OnPropertyChanged(); } }

        // ── Commands ──────────────────────────────────────────────────────
        public ICommand EnableAutoOptimizeCommand { get; }
        public ICommand DisableAutoOptimizeCommand { get; }
        public ICommand RunNowCommand { get; }
        public ICommand ToggleCleanTempCommand { get; }
        public ICommand ToggleFreeStandbyCommand { get; }
        public ICommand ToggleAdvancedDetailsCommand { get; }
        public ICommand SelectRecommendedCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        public AutoOptimizeViewModel(IIpcClient ipc)
        {
            _ipc = ipc ?? throw new ArgumentNullException(nameof(ipc));

            SmartAutoOptimizeScheduler.Instance.SetServiceOnlineEvaluator(
                () => _ipc.ConnectionState == IpcConnectionState.Connected);

            EnableAutoOptimizeCommand = new RelayCommand(
                async _ => await ExecuteEnableAutoOptimizeAsync(),
                _ => CanEnableAutoOptimize);

            DisableAutoOptimizeCommand = new RelayCommand(
                async _ => await ExecuteDisableAutoOptimizeAsync(),
                _ => CanDisableAutoOptimize);

            RunNowCommand = new RelayCommand(
                async _ => await ExecuteRunNowAsync(),
                _ => CanRunNow);

            ToggleCleanTempCommand = new RelayCommand(_ => CleanTempFilesSelected = !CleanTempFilesSelected);
            ToggleFreeStandbyCommand = new RelayCommand(_ => FreeStandbyMemorySelected = !FreeStandbyMemorySelected);
            ToggleAdvancedDetailsCommand = new RelayCommand(_ => IsAdvancedDetailsExpanded = !IsAdvancedDetailsExpanded);

            SelectRecommendedCommand = new RelayCommand(_ =>
            {
                CleanTempFilesSelected = true;
                FreeStandbyMemorySelected = true;
            });

            ClearSelectionCommand = new RelayCommand(_ =>
            {
                CleanTempFilesSelected = false;
                FreeStandbyMemorySelected = false;
            });

            SmartAutoOptimizeScheduler.Instance.StateChanged += state =>
            {
                UiDispatcher.Run(() => SyncFromSchedulerState(state));
            };

            _ipc.ConnectionStateChanged += _ =>
            {
                UiDispatcher.Run(() =>
                {
                    OnPropertyChanged(nameof(IsServiceOnline));
                    OnPropertyChanged(nameof(ServiceOnlineVisibility));
                    OnPropertyChanged(nameof(ServiceOfflineVisibility));
                    OnPropertyChanged(nameof(ServiceStatusText));
                    OnPropertyChanged(nameof(ServiceStatusBrush));
                    SyncFromSchedulerState(SmartAutoOptimizeScheduler.Instance.State);
                });
            };

            SyncFromSchedulerState(SmartAutoOptimizeScheduler.Instance.State);
        }

        public override Task OnNavigatedToAsync()
        {
            SyncFromSchedulerState(SmartAutoOptimizeScheduler.Instance.State);

            // Lightweight UI display refresh every 15s ONLY while viewing the page to keep relative "in X minutes" text fresh
            _uiDisplayRefreshTimer?.Dispose();
            _uiDisplayRefreshTimer = new Timer(_ =>
            {
                UiDispatcher.Run(() =>
                {
                    var st = SmartAutoOptimizeScheduler.Instance.State;
                    NextRunText = (!IsServiceOnline || !st.IsEnabled || !st.NextRun.HasValue)
                        ? "—"
                        : SmartAutoOptimizeScheduler.FormatNextRunRelative(st.NextRun, st.IsEnabled);
                });
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

            return Task.CompletedTask;
        }

        public override Task OnNavigatedFromAsync()
        {
            _uiDisplayRefreshTimer?.Dispose();
            _uiDisplayRefreshTimer = null;
            return Task.CompletedTask;
        }

        private async Task ApplyConfigurationChangeAsync()
        {
            var opt = SmartAutoOptimizeScheduler.ParseOption(_selectedScheduleOption);
            await SmartAutoOptimizeScheduler.Instance.UpdateConfigurationAsync(
                opt,
                _cleanTempFilesSelected,
                _freeStandbyMemorySelected);
        }

        private async Task ExecuteEnableAutoOptimizeAsync()
        {
            if (!CanEnableAutoOptimize) return;
            var opt = SmartAutoOptimizeScheduler.ParseOption(_selectedScheduleOption);
            await SmartAutoOptimizeScheduler.Instance.EnableAutoOptimizeAsync(
                opt,
                _cleanTempFilesSelected,
                _freeStandbyMemorySelected);
        }

        private async Task ExecuteDisableAutoOptimizeAsync()
        {
            if (!CanDisableAutoOptimize) return;
            await SmartAutoOptimizeScheduler.Instance.DisableAutoOptimizeAsync();
        }

        private async Task ExecuteRunNowAsync()
        {
            if (!CanRunNow) return;
            IsRunningNow = true;
            try
            {
                await SmartAutoOptimizeScheduler.Instance.RunSelectedNowAsync(
                    _cleanTempFilesSelected,
                    _freeStandbyMemorySelected);
            }
            finally
            {
                IsRunningNow = false;
                SyncFromSchedulerState(SmartAutoOptimizeScheduler.Instance.State);
            }
        }

        private void SyncFromSchedulerState(AutoOptimizeScheduleState state)
        {
            if (state == null) return;

            _suppressConfigCallback = true;
            try
            {
                var parsedOpt = SmartAutoOptimizeScheduler.ParseOption(state.OptionKey);
                _selectedScheduleOption = SmartAutoOptimizeScheduler.GetOptionDisplayName(parsedOpt);
                OnPropertyChanged(nameof(SelectedScheduleOption));

                _cleanTempFilesSelected = state.ActionCleanTempFiles;
                OnPropertyChanged(nameof(CleanTempFilesSelected));

                _freeStandbyMemorySelected = state.ActionFreeStandbyMemory;
                OnPropertyChanged(nameof(FreeStandbyMemorySelected));

                _isAutoOptimizeEnabled = state.IsEnabled;
                OnPropertyChanged(nameof(IsAutoOptimizeEnabled));
            }
            finally
            {
                _suppressConfigCallback = false;
            }

            NextRunText = (!IsServiceOnline || !state.IsEnabled || !state.NextRun.HasValue)
                ? "—"
                : SmartAutoOptimizeScheduler.FormatNextRunRelative(state.NextRun, state.IsEnabled);

            if (state.LastRun.HasValue)
            {
                LastRunTimeText = SmartAutoOptimizeScheduler.FormatLastRunTimestamp(state.LastRun);
                LastRunStatusText = string.IsNullOrWhiteSpace(state.LastRunStatus) || state.LastRunStatus == "Never"
                    ? "Completed"
                    : state.LastRunStatus;
                LastRunActionsText = string.IsNullOrWhiteSpace(state.LastRunActionsText) ? "—" : state.LastRunActionsText;
                LastRunResultText = string.IsNullOrWhiteSpace(state.LastResult) ? "—" : state.LastResult;
            }
            else
            {
                LastRunTimeText = "Never";
                LastRunStatusText = "—";
                LastRunActionsText = "—";
                LastRunResultText = "—";
            }

            RunHistoryItems.Clear();
            if (state.History != null)
            {
                foreach (var item in state.History)
                {
                    RunHistoryItems.Add(new AutoOptimizeHistoryRowViewModel
                    {
                        TimestampText = item.TimestampText,
                        ActionName = item.ActionName,
                        Status = item.Status,
                        ResultText = item.ResultText,
                        TriggerSource = item.TriggerSource,
                        InternalCommand = item.InternalCommand
                    });
                }
            }
            OnPropertyChanged(nameof(HasRunHistory));
            OnPropertyChanged(nameof(HasHistoryVisibility));
            OnPropertyChanged(nameof(EmptyHistoryVisibility));

            // Populate Expandable Advanced Details
            AdvSchedulerState = !IsServiceOnline
                ? "SERVICE_OFFLINE (Paused)"
                : (state.IsEnabled ? $"ARMED_ACTIVE (Interval: {_selectedScheduleOption})" : "DISABLED (OFF)");
            AdvTaskIdentifier = state.IsEnabled
                ? @"BiosOptimizer.Service + \ErrorOptimizer\SmartAutoOptimizeTask"
                : "BiosOptimizer.Service (Task Unregistered)";
            AdvInternalCommand = (_cleanTempFilesSelected, _freeStandbyMemorySelected) switch
            {
                (true, true) => "StorageCleanerEngine.CleanCategories(TEMP) + WindowsMemoryListProvider.ExecuteStandbyPurgeAsync()",
                (true, false) => "StorageCleanerEngine.CleanCategories(TEMP, log-files, crash-dumps)",
                (false, true) => "WindowsMemoryListProvider.ExecuteStandbyPurgeAsync(MemoryPurgeStandbyList)",
                _ => "None selected"
            };
            AdvExecutionTimestamp = state.LastRun.HasValue
                ? $"{state.LastRun.Value:yyyy-MM-dd HH:mm:ss} Local ({state.LastRun.Value.ToUniversalTime():HH:mm:ss} UTC)"
                : "Never executed";
            AdvExecutionResult = string.IsNullOrWhiteSpace(state.LastResult) || state.LastResult == "—"
                ? "No execution recorded"
                : state.LastResult;
            AdvServiceStatus = IsServiceOnline
                ? $"Online ({_ipc.ServiceBuildId})"
                : "Offline (IPC pipe disconnected)";
            AdvDeferReason = !string.IsNullOrWhiteSpace(state.LastTechnicalDeferDetail)
                ? state.LastTechnicalDeferDetail
                : (!string.IsNullOrWhiteSpace(state.LastDeferralReason) ? state.LastDeferralReason : "None");
            AdvRetryReason = !string.IsNullOrWhiteSpace(state.LastRetryReason)
                ? state.LastRetryReason
                : "None";

            NotifyConfigurationDependentProperties();
        }

        private void NotifyConfigurationDependentProperties()
        {
            OnPropertyChanged(nameof(IsAllAutoSelected));
            OnPropertyChanged(nameof(HasScheduleSelected));
            OnPropertyChanged(nameof(HasAnyActionSelected));
            OnPropertyChanged(nameof(CanEnableAutoOptimize));
            OnPropertyChanged(nameof(CanDisableAutoOptimize));
            OnPropertyChanged(nameof(CanRunNow));
            OnPropertyChanged(nameof(ShowEnableButtonVisibility));
            OnPropertyChanged(nameof(ShowDisableButtonVisibility));
            OnPropertyChanged(nameof(StatusValueText));
            OnPropertyChanged(nameof(StatusDotLabelText));
            OnPropertyChanged(nameof(StatusPrimaryBrush));
            OnPropertyChanged(nameof(ActiveGreenDotVisibility));
            OnPropertyChanged(nameof(SelectedActionsSummaryText));
            OnPropertyChanged(nameof(ValidationHelperText));
            OnPropertyChanged(nameof(ValidationHelperBrush));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
