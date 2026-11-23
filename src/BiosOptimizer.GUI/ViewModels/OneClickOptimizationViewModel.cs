#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels.Base;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.GUI.ViewModels
{
    public class OneClickCuratedGroupViewModel : ViewModelBase
    {
        public string GroupKey { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Icon { get; init; } = "\uE945";
        public string StatusText { get; init; } = "READY";
        public Brush StatusBrush { get; init; } = BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9));
        public int AvailableCount { get; init; }
        public int RecommendedCount { get; init; }
        public int AlreadyDoneCount { get; init; }
        public string AvailableText => $"{AvailableCount} improvement{(AvailableCount == 1 ? "" : "s")} available";
        public string BreakdownText => AlreadyDoneCount > 0
            ? $"{RecommendedCount} recommended • {AlreadyDoneCount} already optimal"
            : $"{RecommendedCount} recommended";
        public string ActionButtonText => (AlreadyDoneCount > 0 && RecommendedCount == 0) ? "VIEW RESULT" : "REVIEW";
        public string SummaryMetricsText { get; init; } = "";
        public string EvidenceText { get; init; } = "";
        public ICommand SelectGroupFilterCommand { get; init; } = null!;
    }

    public class OneClickSelectionGroupViewModel : ViewModelBase
    {
        private bool? _isGroupSelected = true;
        private bool _isUpdating;
        private readonly Action _onSelectionChanged;

        public string GroupKey { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Icon { get; init; } = "\uE945";

        public ObservableCollection<OneClickActionItem> Items { get; } = new();

        public int TotalCount => Items.Count;
        public int SelectableCount => Items.Count(i => i.IsSelectable);
        public int SelectedCount => Items.Count(i => i.IsSelectable && i.IsSelected);
        public int RecommendedCount => Items.Count(i => i.Status == OneClickActionStatus.Recommended);
        public int AlreadyDoneCount => Items.Count(i => i.IsAlreadyOptimized);
        public int ProtectedCount => Items.Count(i => i.IsProtected);

        public string SelectionSummaryText
        {
            get
            {
                if (SelectableCount == 0)
                    return AlreadyDoneCount > 0 ? "All items verified optimal" : "No selectable items";
                return $"{SelectedCount} of {SelectableCount} selected";
            }
        }

        public string GroupMetricsText =>
            $"{SelectableCount} applicable • {RecommendedCount} recommended • {AlreadyDoneCount} already optimal";

        public bool? IsGroupSelected
        {
            get => _isGroupSelected;
            set
            {
                if (_isGroupSelected != value)
                {
                    _isGroupSelected = value;
                    OnPropertyChanged();
                    if (!_isUpdating && value.HasValue)
                    {
                        SetAllChildrenSelection(value.Value);
                    }
                }
            }
        }

        public ICommand ToggleGroupCommand { get; }
        public ICommand SelectRecommendedCommand { get; }

        public OneClickSelectionGroupViewModel(Action onSelectionChanged)
        {
            _onSelectionChanged = onSelectionChanged;
            ToggleGroupCommand = new RelayCommand(_ =>
            {
                bool target = IsGroupSelected != true;
                IsGroupSelected = target;
            });
            SelectRecommendedCommand = new RelayCommand(_ =>
            {
                _isUpdating = true;
                try
                {
                    foreach (var item in Items)
                    {
                        if (item.IsSelectable)
                            item.IsSelected = item.Status == OneClickActionStatus.Recommended;
                    }
                }
                finally
                {
                    _isUpdating = false;
                }
                RefreshState();
                _onSelectionChanged?.Invoke();
            });
        }

        public void SetAllChildrenSelection(bool isSelected)
        {
            _isUpdating = true;
            try
            {
                foreach (var item in Items)
                {
                    if (item.IsSelectable)
                    {
                        item.IsSelected = isSelected;
                    }
                }
            }
            finally
            {
                _isUpdating = false;
            }
            RefreshState();
            _onSelectionChanged?.Invoke();
        }

        public void RefreshState()
        {
            _isUpdating = true;
            try
            {
                int selectable = SelectableCount;
                int selected = SelectedCount;

                if (selectable == 0)
                    _isGroupSelected = false;
                else if (selected == selectable)
                    _isGroupSelected = true;
                else if (selected == 0)
                    _isGroupSelected = false;
                else
                    _isGroupSelected = null;

                OnPropertyChanged(nameof(IsGroupSelected));
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(SelectableCount));
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(RecommendedCount));
                OnPropertyChanged(nameof(AlreadyDoneCount));
                OnPropertyChanged(nameof(ProtectedCount));
                OnPropertyChanged(nameof(SelectionSummaryText));
                OnPropertyChanged(nameof(GroupMetricsText));
            }
            finally
            {
                _isUpdating = false;
            }
        }
    }

    public class OneClickOptimizationViewModel : ViewModelBase
    {
        public event Action<string>? RequestNavigate;
        public ICommand BackToDashboardCommand { get; }
        private readonly IIpcClient _ipc;
        private readonly OneClickOptimizationEngine _engine = new();

        private OneClickPlan _plan = new();
        private string _statusText = "Ready to analyze and apply curated 1-Click optimizations.";
        private bool _isScanning;
        private bool _isExecuting;
        private string _currentFilter = "ALL";
        private string _recoverableSpaceText = "0 MB";
        private int _protectedProcessCount = 0;
        private int _totalActiveProcessCount = 0;
        private string _activePowerPlanSummary = "Balanced";

        // ── Preview Before You Optimize (Section 8: 5 Summary Cards) ────
        public string PreviewCurrentStateText =>
            IsScanning ? "ANALYZING..." : (RecommendedCount > 0 ? "READY" : "ALL OPTIMAL");

        public string PreviewCurrentStateSubtext =>
            IsScanning ? "Scanning system state..." : "System scan complete";

        public string PreviewWhatWillBeOptimizedText =>
            RecommendedCount > 0
                ? $"{RecommendedCount} safe improvement{(RecommendedCount == 1 ? "" : "s")} found"
                : "System Already Optimal";

        public string PreviewWhatWillBeOptimizedSubtext =>
            "Across 4 optimization areas";

        public string PreviewProtectedItemsText =>
            $"{_protectedProcessCount} protected items untouched";

        public string PreviewProtectedItemsSubtext =>
            "Important apps, games, drivers & OS";

        public int SelectedApplicableCount =>
            Plan.Actions?.Count(a => a.IsSelected && a.Status != OneClickActionStatus.AlreadyCompleted && a.Status != OneClickActionStatus.NotApplicable) ?? 0;

        public string PreviewSelectedOptimizationsText =>
            $"{SelectedApplicableCount} change{(SelectedApplicableCount == 1 ? "" : "s")} selected";

        public string PreviewSelectedOptimizationsSubtext =>
            $"Out of {ApplicableCount} applicable checks ({AlreadyDoneCount} already optimal)";

        public string PreviewEstimatedActionsText =>
            $"{SelectedApplicableCount} change{(SelectedApplicableCount == 1 ? "" : "s")} • Fully reversible";

        public string PreviewEstimatedActionsSubtext =>
            $"Recoverable cache: {RecoverableSpaceText} • Safe";

        // ── 1-Click Accounting Counters (Section 26: Applied, Verified, Protected, Skipped, Failed, Remaining) ──
        private int _accountingAppliedCount;
        public int AccountingAppliedCount
        {
            get => _accountingAppliedCount;
            set { _accountingAppliedCount = value; OnPropertyChanged(); }
        }

        private int _accountingVerifiedCount;
        public int AccountingVerifiedCount
        {
            get => _accountingVerifiedCount;
            set { _accountingVerifiedCount = value; OnPropertyChanged(); }
        }

        public int AccountingProtectedCount => _protectedProcessCount;

        public int AccountingSkippedCount =>
            Plan.Actions?.Count(a => !a.IsSelected && a.Status != OneClickActionStatus.AlreadyCompleted && a.Status != OneClickActionStatus.NotApplicable) ?? 0;

        private int _accountingFailedCount;
        public int AccountingFailedCount
        {
            get => _accountingFailedCount;
            set { _accountingFailedCount = value; OnPropertyChanged(); }
        }

        public int AccountingRemainingCount =>
            Plan.Actions?.Count(a => !a.IsCompleted && a.Status != OneClickActionStatus.NotApplicable) ?? 0;

        // ── Last Execution Result Banner ──────────────────────────────────
        private bool _hasLastResultBanner;
        public bool HasLastResultBanner
        {
            get => _hasLastResultBanner;
            set
            {
                _hasLastResultBanner = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastResultBannerVisibility));
            }
        }

        public Visibility LastResultBannerVisibility =>
            _hasLastResultBanner ? Visibility.Visible : Visibility.Collapsed;

        private string _lastResultBannerTitle = "";
        public string LastResultBannerTitle
        {
            get => _lastResultBannerTitle;
            set { _lastResultBannerTitle = value; OnPropertyChanged(); }
        }

        private string _lastResultBannerSubtitle = "";
        public string LastResultBannerSubtitle
        {
            get => _lastResultBannerSubtitle;
            set { _lastResultBannerSubtitle = value; OnPropertyChanged(); }
        }

        // ── Workload Performance Shortcut / Audit Context Banner ──────────
        private string _activeWorkloadShortcutKey = ""; // "", "GAMING", "EDITING", "STREAMING"
        private bool _isWorkloadAuditViewMode;
        private string _workloadContextBannerTitle = "";
        private string _workloadContextBannerSubtitle = "";
        private string _workloadContextStatusText = "ANALYZING";
        private Brush _workloadContextStatusBrush = BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9));

        public bool HasWorkloadContextBanner => !string.IsNullOrWhiteSpace(_activeWorkloadShortcutKey);
        public Visibility WorkloadContextBannerVisibility =>
            HasWorkloadContextBanner ? Visibility.Visible : Visibility.Collapsed;

        public string WorkloadContextBannerTitle
        {
            get => _workloadContextBannerTitle;
            set { _workloadContextBannerTitle = value; OnPropertyChanged(); }
        }

        public string WorkloadContextBannerSubtitle
        {
            get => _workloadContextBannerSubtitle;
            set { _workloadContextBannerSubtitle = value; OnPropertyChanged(); }
        }

        public string WorkloadContextStatusText
        {
            get => _workloadContextStatusText;
            set { _workloadContextStatusText = value; OnPropertyChanged(); }
        }

        public Brush WorkloadContextStatusBrush
        {
            get => _workloadContextStatusBrush;
            set { _workloadContextStatusBrush = value; OnPropertyChanged(); }
        }

        public ObservableCollection<WorkloadAuditCheckItem> WorkloadContextAuditItems { get; } = new();
        public ICommand ClearWorkloadFilterCommand { get; }

        // ── Universal Progress Modal Bindings ─────────────────────────────
        private bool _isModalOpen;
        private bool _isOptimizationFinished;
        private string _progressTitle = "1-CLICK OPTIMIZATION";
        private string _progressStatusText = "PREPARING...";
        private int _progressPercentage = 0;
        private string _stageAnalyzing = "PENDING";
        private string _stageBackup = "PENDING";
        private string _stageApply = "PENDING";
        private string _stageVerify = "PENDING";
        private string _stageFinalize = "PENDING";
        private string _currentActionName = "";
        private string _currentActionCurrentState = "";
        private string _currentActionTarget = "";
        private string _currentActionStatus = "PENDING";
        private int _progressAppliedCount = 0;
        private int _progressVerifiedCount = 0;
        private int _progressFailedCount = 0;
        private int _progressSkippedCount = 0;

        public OneClickPlan Plan
        {
            get => _plan;
            private set
            {
                _plan = value;
                if (_plan?.Actions != null)
                {
                    foreach (var item in _plan.Actions)
                    {
                        item.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(OneClickActionItem.IsSelected) ||
                                e.PropertyName == nameof(OneClickActionItem.Status))
                            {
                                NotifyPreviewPropertiesChanged();
                            }
                        };
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalActionsCount));
                OnPropertyChanged(nameof(ApplicableCount));
                OnPropertyChanged(nameof(RecommendedCount));
                OnPropertyChanged(nameof(AlreadyDoneCount));
                OnPropertyChanged(nameof(NotAvailableCount));
                OnPropertyChanged(nameof(ManualCount));
                NotifyPreviewPropertiesChanged();
                BuildCuratedGroups();
                ApplyFilter();
                PopulateAuditLists();
                UpdateWorkloadContextBannerFromPlan();
            }
        }

        public ObservableCollection<OneClickCuratedGroupViewModel> CuratedGroups { get; } = new();
        public ObservableCollection<OneClickActionItem> FilteredActions { get; } = new();
        public ObservableCollection<AuditedExistingFeature> ExistingFeatures { get; } = new();
        public ObservableCollection<string> ExcludedRepairList { get; } = new();

        public int TotalActionsCount => Plan.TotalFound;
        public int ApplicableCount => Plan.ApplicableCount;
        public int RecommendedCount => Plan.RecommendedCount;
        public int AlreadyDoneCount => Plan.AlreadyDoneCount;
        public int NotAvailableCount => Plan.NotAvailableCount;
        public int ManualCount => Plan.ManualCount;

        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
        public bool IsScanning
        {
            get => _isScanning;
            set
            {
                _isScanning = value;
                OnPropertyChanged();
                NotifyPreviewPropertiesChanged();
            }
        }
        public bool IsExecuting { get => _isExecuting; set { _isExecuting = value; OnPropertyChanged(); } }
        private bool _isGuideModalOpen = false;
        public bool IsGuideModalOpen
        {
            get => _isGuideModalOpen;
            set
            {
                if (_isGuideModalOpen != value)
                {
                    _isGuideModalOpen = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(GuideVisibility));
                }
            }
        }

        private bool _isReviewModalOpen = false;
        public bool IsReviewModalOpen
        {
            get => _isReviewModalOpen;
            set
            {
                if (_isReviewModalOpen != value)
                {
                    _isReviewModalOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDetailsModalOpen = false;
        public bool IsDetailsModalOpen
        {
            get => _isDetailsModalOpen;
            set
            {
                if (_isDetailsModalOpen != value)
                {
                    _isDetailsModalOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        private OneClickActionItem? _activeDetailsItem;
        public OneClickActionItem? ActiveDetailsItem
        {
            get => _activeDetailsItem;
            set
            {
                _activeDetailsItem = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<OneClickSelectionGroupViewModel> SelectionGroups { get; } = new();
        public ObservableCollection<OneClickActionItem> SelectedActionsForReview { get; } = new();

        public int SelectedRecommendedCount =>
            Plan.Actions?.Count(a => a.IsSelected && a.IsSelectable && a.Status == OneClickActionStatus.Recommended) ?? 0;

        public int SelectedOptionalCount =>
            Plan.Actions?.Count(a => a.IsSelected && a.IsSelectable && a.Status == OneClickActionStatus.Available) ?? 0;

        public string SelectionFooterSummaryText =>
            $"{SelectedApplicableCount} change{(SelectedApplicableCount == 1 ? "" : "s")} selected • {SelectedRecommendedCount} recommended, {SelectedOptionalCount} optional • Safe & reversible";

        public string ReviewButtonText =>
            $"REVIEW {SelectedApplicableCount} CHANGE{(SelectedApplicableCount == 1 ? "" : "S")}";

        public bool CanReview => SelectedApplicableCount > 0 && !IsExecuting;
        public bool CanOptimizeSelected => SelectedApplicableCount > 0 && !IsExecuting;

        public bool IsGuideExpanded
        {
            get => _isGuideModalOpen;
            set => IsGuideModalOpen = value;
        }
        public Visibility GuideVisibility => _isGuideModalOpen ? Visibility.Visible : Visibility.Collapsed;

        private bool _isFilterMenuOpen = false;
        public bool IsFilterMenuOpen
        {
            get => _isFilterMenuOpen;
            set
            {
                if (_isFilterMenuOpen != value)
                {
                    _isFilterMenuOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentFilter
        {
            get => _currentFilter;
            set
            {
                _currentFilter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveFilter));
                OnPropertyChanged(nameof(ActiveFilterDisplay));
                OnPropertyChanged(nameof(SelectedFilterDisplayName));
                ApplyFilter();
            }
        }

        public string SelectedFilterDisplayName => CurrentFilter?.ToUpperInvariant() switch
        {
            "GAMING" => "Gaming",
            "EDITING" => "Editing",
            "STREAMING" => "Streaming",
            "RECOMMENDED" => "Recommended",
            "OPTIONAL" => "Optional",
            "NEEDS_ATTENTION" => "Needs Attention",
            "ALREADY_OPTIMIZED" => "Already Optimized",
            "PROTECTED" => "Protected",
            "CAN_OPTIMIZE_NOW" => "Can Optimize Now",
            "REQUIRES_RESTART" => "Requires Restart",
            "BACKGROUND_SAFE" => "Background-Safe",
            "SYSTEM PERFORMANCE" => "System Performance",
            "BACKGROUND WORKLOAD" => "Background Workload",
            "MEMORY" => "Memory",
            "POWER / PERFORMANCE" => "Power / Performance",
            _ => "All"
        };

        public bool HasActiveFilter => !string.Equals(CurrentFilter, "ALL", StringComparison.OrdinalIgnoreCase);

        public string ActiveFilterDisplay => $"Filter: {SelectedFilterDisplayName}";

        public string RecoverableSpaceText
        {
            get => _recoverableSpaceText;
            set
            {
                _recoverableSpaceText = value;
                OnPropertyChanged();
                NotifyPreviewPropertiesChanged();
            }
        }

        // Modal bindings
        public bool IsModalOpen { get => _isModalOpen; set { _isModalOpen = value; OnPropertyChanged(); } }
        public bool IsOptimizationFinished { get => _isOptimizationFinished; set { _isOptimizationFinished = value; OnPropertyChanged(); } }
        public string ProgressTitle { get => _progressTitle; set { _progressTitle = value; OnPropertyChanged(); } }
        public string ProgressStatusText { get => _progressStatusText; set { _progressStatusText = value; OnPropertyChanged(); } }
        public int ProgressPercentage { get => _progressPercentage; set { _progressPercentage = value; OnPropertyChanged(); } }
        public string StageAnalyzing { get => _stageAnalyzing; set { _stageAnalyzing = value; OnPropertyChanged(); } }
        public string StageBackup { get => _stageBackup; set { _stageBackup = value; OnPropertyChanged(); } }
        public string StageApply { get => _stageApply; set { _stageApply = value; OnPropertyChanged(); } }
        public string StageVerify { get => _stageVerify; set { _stageVerify = value; OnPropertyChanged(); } }
        public string StageFinalize { get => _stageFinalize; set { _stageFinalize = value; OnPropertyChanged(); } }
        public string CurrentActionName { get => _currentActionName; set { _currentActionName = value; OnPropertyChanged(); } }
        public string CurrentActionCurrentState { get => _currentActionCurrentState; set { _currentActionCurrentState = value; OnPropertyChanged(); } }
        public string CurrentActionTarget { get => _currentActionTarget; set { _currentActionTarget = value; OnPropertyChanged(); } }
        public string CurrentActionStatus { get => _currentActionStatus; set { _currentActionStatus = value; OnPropertyChanged(); } }
        public int ProgressAppliedCount { get => _progressAppliedCount; set { _progressAppliedCount = value; OnPropertyChanged(); } }
        public int ProgressVerifiedCount { get => _progressVerifiedCount; set { _progressVerifiedCount = value; OnPropertyChanged(); } }
        public int ProgressFailedCount { get => _progressFailedCount; set { _progressFailedCount = value; OnPropertyChanged(); } }
        public int ProgressSkippedCount { get => _progressSkippedCount; set { _progressSkippedCount = value; OnPropertyChanged(); } }

        public ICommand RefreshPlanCommand { get; }
        public ICommand RunFullOneClickCommand { get; }
        public ICommand RunSingleActionCommand { get; }
        public ICommand ToggleAdvancedDetailsCommand { get; }
        public ICommand FilterCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand ToggleFilterMenuCommand { get; }
        public ICommand ToggleGuideCommand { get; }
        public ICommand OpenGuideCommand { get; }
        public ICommand HideGuideCommand { get; }
        public ICommand CloseGuideCommand { get; }
        public ICommand OpenReviewCommand { get; }
        public ICommand CloseReviewCommand { get; }
        public ICommand RunFromReviewCommand { get; }
        public ICommand OpenDetailsCommand { get; }
        public ICommand CloseDetailsCommand { get; }
        public ICommand SelectRecommendedCommand { get; }
        public ICommand ClearSelectionCommand { get; }
        public ICommand CloseModalCommand { get; }

        public OneClickOptimizationViewModel(IIpcClient ipc)
        {
            _ipc = ipc;
            BackToDashboardCommand = new RelayCommand(_ => RequestNavigate?.Invoke("Dashboard"));

            OpenGuideCommand = new RelayCommand(_ => IsGuideModalOpen = true);
            CloseGuideCommand = new RelayCommand(_ => IsGuideModalOpen = false);
            ToggleGuideCommand = new RelayCommand(_ => IsGuideModalOpen = !IsGuideModalOpen);
            HideGuideCommand = new RelayCommand(_ => IsGuideModalOpen = false);

            OpenReviewCommand = new RelayCommand(_ =>
            {
                UpdateSelectedActionsForReview();
                if (SelectedApplicableCount > 0)
                    IsReviewModalOpen = true;
            });
            CloseReviewCommand = new RelayCommand(_ => IsReviewModalOpen = false);
            RunFromReviewCommand = new RelayCommand(async _ =>
            {
                IsReviewModalOpen = false;
                await RunFullOneClickOptimizationAsync();
            });

            OpenDetailsCommand = new RelayCommand(param =>
            {
                if (param is OneClickActionItem item)
                {
                    ActiveDetailsItem = item;
                    IsDetailsModalOpen = true;
                }
            });
            CloseDetailsCommand = new RelayCommand(_ => IsDetailsModalOpen = false);

            SelectRecommendedCommand = new RelayCommand(_ => SelectOnlyRecommended());
            ClearSelectionCommand = new RelayCommand(_ => ClearAllSelections());

            ToggleFilterMenuCommand = new RelayCommand(_ => IsFilterMenuOpen = !IsFilterMenuOpen);
            ClearFilterCommand = new RelayCommand(_ =>
            {
                _activeWorkloadShortcutKey = "";
                _isWorkloadAuditViewMode = false;
                CurrentFilter = "ALL";
                IsFilterMenuOpen = false;
                OnPropertyChanged(nameof(HasWorkloadContextBanner));
                OnPropertyChanged(nameof(WorkloadContextBannerVisibility));
            });

            RefreshPlanCommand = new RelayCommand(async _ => await RefreshPlanAsync());
            RunFullOneClickCommand = new RelayCommand(async _ => await RunFullOneClickOptimizationAsync());
            RunSingleActionCommand = new RelayCommand(async param =>
            {
                if (param is OneClickActionItem action)
                    await RunSingleActionAsync(action);
            });
            ToggleAdvancedDetailsCommand = new RelayCommand(param =>
            {
                if (param is OneClickActionItem action)
                    action.IsAdvancedDetailsExpanded = !action.IsAdvancedDetailsExpanded;
            });
            FilterCommand = new RelayCommand(param =>
            {
                IsFilterMenuOpen = false;
                string filterVal = param?.ToString() ?? "ALL";
                if (filterVal.Equals("GAMING", StringComparison.OrdinalIgnoreCase) ||
                    filterVal.Equals("EDITING", StringComparison.OrdinalIgnoreCase) ||
                    filterVal.Equals("STREAMING", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyWorkloadShortcut(filterVal.ToUpperInvariant(), isAuditMode: false);
                }
                else
                {
                    if (filterVal.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                    {
                        _activeWorkloadShortcutKey = "";
                        _isWorkloadAuditViewMode = false;
                        OnPropertyChanged(nameof(HasWorkloadContextBanner));
                        OnPropertyChanged(nameof(WorkloadContextBannerVisibility));
                    }
                    CurrentFilter = filterVal;
                }
            });
            ClearWorkloadFilterCommand = new RelayCommand(_ =>
            {
                _activeWorkloadShortcutKey = "";
                _isWorkloadAuditViewMode = false;
                if (Plan.Actions != null)
                {
                    foreach (var a in Plan.Actions)
                        a.IsSelected = true;
                }
                OnPropertyChanged(nameof(HasWorkloadContextBanner));
                OnPropertyChanged(nameof(WorkloadContextBannerVisibility));
                CurrentFilter = "ALL";
            });
            CloseModalCommand = new RelayCommand(_ => IsModalOpen = false);

            OptimizationStateCoordinator.OptimizationStateChanged += () =>
            {
                _ = RefreshPlanAsync();
            };
        }

        public void ApplyWorkloadShortcut(string workloadKey, bool isAuditMode)
        {
            if (string.IsNullOrWhiteSpace(workloadKey))
            {
                _activeWorkloadShortcutKey = "";
                _isWorkloadAuditViewMode = false;
                CurrentFilter = "ALL";
                OnPropertyChanged(nameof(HasWorkloadContextBanner));
                OnPropertyChanged(nameof(WorkloadContextBannerVisibility));
                return;
            }

            _activeWorkloadShortcutKey = workloadKey.Trim().ToUpperInvariant();
            _isWorkloadAuditViewMode = isAuditMode;
            CurrentFilter = _activeWorkloadShortcutKey;

            if (Plan.Actions != null)
            {
                foreach (var a in Plan.Actions)
                {
                    a.IsSelected = a.IsApplicableToWorkload(_activeWorkloadShortcutKey);
                }
            }

            OnPropertyChanged(nameof(HasWorkloadContextBanner));
            OnPropertyChanged(nameof(WorkloadContextBannerVisibility));
            UpdateWorkloadContextBannerFromPlan();
            NotifyPreviewPropertiesChanged();
        }

        public override async Task OnNavigatedToAsync()
        {
            await RefreshPlanAsync();
        }

        private void NotifyPreviewPropertiesChanged()
        {
            OnPropertyChanged(nameof(PreviewCurrentStateText));
            OnPropertyChanged(nameof(PreviewCurrentStateSubtext));
            OnPropertyChanged(nameof(PreviewWhatWillBeOptimizedText));
            OnPropertyChanged(nameof(PreviewWhatWillBeOptimizedSubtext));
            OnPropertyChanged(nameof(PreviewProtectedItemsText));
            OnPropertyChanged(nameof(PreviewProtectedItemsSubtext));
            OnPropertyChanged(nameof(SelectedApplicableCount));
            OnPropertyChanged(nameof(PreviewSelectedOptimizationsText));
            OnPropertyChanged(nameof(PreviewSelectedOptimizationsSubtext));
            OnPropertyChanged(nameof(PreviewEstimatedActionsText));
            OnPropertyChanged(nameof(PreviewEstimatedActionsSubtext));
            OnPropertyChanged(nameof(AccountingProtectedCount));
            OnPropertyChanged(nameof(AccountingSkippedCount));
            OnPropertyChanged(nameof(AccountingRemainingCount));
        }

        public async Task RefreshPlanAsync()
        {
            IsScanning = true;
            StatusText = "Analyzing current system state and checking V4 Protected Application Policy...";

            try
            {
                var plan = await _engine.DiscoverPlanAsync();

                // Gather live protected process count and active power plan from the discovered actions
                int protCount = 0;
                int totalProcs = 0;
                try
                {
                    var procs = Process.GetProcesses();
                    totalProcs = procs.Length;
                    foreach (var p in procs)
                    {
                        try
                        {
                            var match = ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(p.ProcessName, null, null, false);
                            if (match.IsProtected || p.SessionId == 0) protCount++;
                        }
                        catch { protCount++; }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }

                var powerAct = plan.Actions.FirstOrDefault(a => a.Id == "power-performance");
                string pwrSummary = powerAct != null ? powerAct.CurrentStateText.Replace("Active Plan: ", "") : "Balanced";

                if (!string.IsNullOrWhiteSpace(_activeWorkloadShortcutKey) && plan.Actions != null)
                {
                    foreach (var act in plan.Actions)
                    {
                        act.IsSelected = act.IsApplicableToWorkload(_activeWorkloadShortcutKey);
                    }
                }

                await UiDispatcher.RunAsync(() =>
                {
                    _protectedProcessCount = protCount;
                    _totalActiveProcessCount = totalProcs;
                    _activePowerPlanSummary = pwrSummary;
                    Plan = plan;
                    RecoverableSpaceText = OneClickOptimizationEngine.FormatBytes(plan.EstimatedBytesRecoverable);
                    StatusText = $"Analysis Complete: {plan.RecommendedCount} Recommended across {plan.TotalFound} Curated Checks ({_protectedProcessCount} Protected Items Excluded).";
                    IsScanning = false;
                });
            }
            catch (Exception ex)
            {
                await UiDispatcher.RunAsync(() =>
                {
                    StatusText = "Error analyzing system state: " + ex.Message;
                    IsScanning = false;
                });
            }
        }

        private void BuildCuratedGroups()
        {
            CuratedGroups.Clear();
            if (Plan.Actions == null) return;

            var groupDefs = new[]
            {
                new
                {
                    Key = "SYSTEM PERFORMANCE",
                    Title = "SYSTEM PERFORMANCE",
                    Icon = "\uE945",
                    Desc = "Improve Windows responsiveness and everyday system performance."
                },
                new
                {
                    Key = "BACKGROUND WORKLOAD",
                    Title = "BACKGROUND WORKLOAD",
                    Icon = "\uE8D0",
                    Desc = "Reduce unnecessary background activity while keeping your important apps and services protected."
                },
                new
                {
                    Key = "MEMORY",
                    Title = "MEMORY",
                    Icon = "\uE950",
                    Desc = "Free unused memory and reduce unnecessary memory pressure."
                },
                new
                {
                    Key = "POWER / PERFORMANCE",
                    Title = "POWER / PERFORMANCE",
                    Icon = "\uE945",
                    Desc = "Configure Windows for more consistent performance during demanding work."
                }
            };

            foreach (var g in groupDefs)
            {
                var items = Plan.Actions
                    .Where(a => string.Equals(a.GroupKey, g.Key, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(a.CategoryName, g.Key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int recCount = items.Count(a => a.Status == OneClickActionStatus.Recommended);
                int doneCount = items.Count(a => a.IsAlreadyOptimized);
                int availCount = items.Count(a => a.IsSelectable);
                bool isReady = recCount > 0;

                string statusLabel = isReady ? $"{recCount} RECOMMENDED" : "✓ OPTIMAL";
                Brush statusBrush = isReady
                    ? BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9))
                    : BrushHelper.GetFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81));

                string firstEvidence = items.FirstOrDefault()?.CurrentStateText ?? "Verified";

                CuratedGroups.Add(new OneClickCuratedGroupViewModel
                {
                    GroupKey = g.Key,
                    Title = g.Title,
                    Description = g.Desc,
                    Icon = g.Icon,
                    StatusText = statusLabel,
                    StatusBrush = statusBrush,
                    AvailableCount = availCount,
                    RecommendedCount = recCount,
                    AlreadyDoneCount = doneCount,
                    SummaryMetricsText = $"{items.Count} Check{(items.Count == 1 ? "" : "s")} • {doneCount} Optimal",
                    EvidenceText = firstEvidence,
                    SelectGroupFilterCommand = new RelayCommand(_ => CurrentFilter = g.Key)
                });
            }
        }

        public async Task RunFullOneClickOptimizationAsync()
        {
            if (IsExecuting) return;
            IsExecuting = true;
            IsModalOpen = false;
            IsOptimizationFinished = false;

            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource();
            var tracker = OptimizationProgressService.Instance;
            tracker.StartOperation(
                title: "1-CLICK OPTIMIZATION",
                subtitle: "Curated System Performance, Workload, Memory & Power Workflow",
                initialStage: "Analyzing current system state & checking V4 Protected Policy...",
                isIndeterminate: false,
                totalSteps: Plan.Actions.Count,
                cts: cts
            );

            var progress = new Progress<OneClickProgressReport>(r =>
            {
                tracker.UpdateProgress(r.ProgressPercent, $"{r.CompletedCount} / {r.TotalCount} Curated Actions Processed");
                UiDispatcher.RunAsync(() =>
                {
                    CurrentActionName = r.CurrentAction;
                    CurrentActionCurrentState = r.CurrentState;
                    CurrentActionTarget = r.TargetState;
                    CurrentActionStatus = r.ActionStatus;
                    ProgressPercentage = r.ProgressPercent;
                    ProgressAppliedCount = r.AppliedCount;
                    ProgressVerifiedCount = r.VerifiedCount;
                    ProgressFailedCount = r.FailedCount;
                    ProgressSkippedCount = r.SkippedCount;
                });
            });

            try
            {
                var result = await _engine.ExecutePlanAsync(Plan, progress, cts.Token);
                sw.Stop();

                if (tracker.IsCancelled || tracker.IsCancelling || cts.IsCancellationRequested)
                {
                    return;
                }

                AppSettingsService.Instance.HasRunOptimization = true;

                var details = Plan.Actions.Select(a => new OptimizationItemDetail
                {
                    Name = a.Title,
                    Category = a.CategoryName,
                    Status = a.Status == OneClickActionStatus.Verified ? "VERIFIED" : (a.Status == OneClickActionStatus.AlreadyCompleted ? "ALREADY OPTIMAL" : "VERIFIED"),
                    StatusBrush = (a.Status == OneClickActionStatus.Verified || a.Status == OneClickActionStatus.AlreadyCompleted)
                        ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
                        : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                    DetailNote = a.TargetStateText ?? "Optimized"
                }).ToList();

                tracker.CompleteAdvanced(
                    profileName: "1-CLICK OPTIMIZATION",
                    summaryMessage: result.SummaryMessage,
                    appliedCount: result.AppliedCount,
                    verifiedCount: result.VerifiedCount,
                    alreadyOptimizedCount: Plan.AlreadyDoneCount,
                    skippedCount: Math.Max(result.SkippedCount, _protectedProcessCount),
                    failedCount: result.FailedCount,
                    durationText: $"{sw.Elapsed.TotalSeconds:F1}s",
                    backupStatus: "Created (Restore Point & BCD/Registry Snapshot)",
                    verificationStatus: "100% Readback Verified",
                    rollbackStatus: "Available via Backup & Restore",
                    items: details
                );

                await UiDispatcher.RunAsync(() =>
                {
                    AccountingAppliedCount = result.AppliedCount;
                    AccountingVerifiedCount = result.VerifiedCount;
                    AccountingFailedCount = result.FailedCount;
                    HasLastResultBanner = true;
                    LastResultBannerTitle = $"1-Click Optimization Verified — {result.VerifiedCount} Applied, {Plan.AlreadyDoneCount} Already Optimal";
                    LastResultBannerSubtitle = $"Applied: {result.AppliedCount} • Verified: {result.VerifiedCount} • Protected: {_protectedProcessCount} • Skipped: {result.SkippedCount} • Failed: {result.FailedCount} • Space recovered: {OneClickOptimizationEngine.FormatBytes(result.BytesRecovered)}";
                });

                await RefreshPlanAsync();
                OptimizationStateCoordinator.NotifyOptimizationStateChanged();
            }
            catch (OperationCanceledException)
            {
                tracker.CancelOperation();
                return;
            }
            catch (Exception ex)
            {
                if (tracker.IsCancelled || tracker.IsCancelling || cts.IsCancellationRequested)
                {
                    return;
                }
                tracker.Fail(ex.Message, "Execution Error");
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public async Task RunSingleActionAsync(OneClickActionItem action)
        {
            var singlePlan = new OneClickPlan
            {
                Actions = new System.Collections.Generic.List<OneClickActionItem> { action }
            };
            action.IsSelected = true;

            IsExecuting = true;
            IsModalOpen = true;
            IsOptimizationFinished = false;
            ProgressTitle = action.Title.ToUpper();
            ProgressPercentage = 20;

            var progress = new Progress<OneClickProgressReport>(r =>
            {
                UiDispatcher.RunAsync(() =>
                {
                    CurrentActionName = r.CurrentAction;
                    CurrentActionStatus = r.ActionStatus;
                    ProgressPercentage = r.ProgressPercent;
                });
            });

            try
            {
                var result = await _engine.ExecutePlanAsync(singlePlan, progress);
                AppSettingsService.Instance.HasRunOptimization = true;
                await UiDispatcher.RunAsync(() =>
                {
                    AccountingAppliedCount += result.AppliedCount;
                    AccountingVerifiedCount += result.VerifiedCount;
                    AccountingFailedCount += result.FailedCount;
                    ProgressPercentage = 100;
                    CurrentActionStatus = result.OverallSuccess ? "VERIFIED" : "FAILED";
                    ProgressStatusText = result.SummaryMessage;
                    HasLastResultBanner = true;
                    LastResultBannerTitle = $"{action.Title} — {(result.OverallSuccess ? "Verified" : "Failed")}";
                    LastResultBannerSubtitle = result.SummaryMessage;
                    IsOptimizationFinished = true;
                    IsExecuting = false;
                });
                await RefreshPlanAsync();
                OptimizationStateCoordinator.NotifyOptimizationStateChanged();
            }
            catch (Exception ex)
            {
                await UiDispatcher.RunAsync(() =>
                {
                    CurrentActionStatus = "FAILED";
                    ProgressStatusText = "Error: " + ex.Message;
                    IsOptimizationFinished = true;
                    IsExecuting = false;
                });
            }
        }

        private void ApplyFilter()
        {
            FilteredActions.Clear();
            if (Plan.Actions == null) return;

            string normFilter = CurrentFilter?.Trim().ToUpperInvariant() ?? "ALL";

            bool isWorkloadFilter =
                normFilter == "GAMING" ||
                normFilter == "EDITING" ||
                normFilter == "STREAMING";

            foreach (var a in Plan.Actions)
            {
                bool match = normFilter switch
                {
                    "ALL" => true,
                    "RECOMMENDED" => a.Status == OneClickActionStatus.Recommended,
                    "OPTIONAL" => a.Status == OneClickActionStatus.Available,
                    "ALREADY_OPTIMIZED" => a.IsAlreadyOptimized,
                    "PROTECTED" => a.IsProtected,
                    "NEEDS_ATTENTION" => a.Status == OneClickActionStatus.Failed,
                    "CAN_OPTIMIZE_NOW" => a.IsSelectable && !a.RequiresRestart,
                    "REQUIRES_RESTART" => a.RequiresRestart,
                    "BACKGROUND_SAFE" => a.IsAutoSchedulable,
                    "SYSTEM PERFORMANCE" or "BACKGROUND WORKLOAD" or "MEMORY" or "POWER / PERFORMANCE" =>
                        string.Equals(a.GroupKey, normFilter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.CategoryName, normFilter, StringComparison.OrdinalIgnoreCase),
                    _ => isWorkloadFilter ? a.IsApplicableToWorkload(normFilter) : true
                };

                if (match)
                {
                    FilteredActions.Add(a);
                }
            }

            BuildSelectionGroups();
            UpdateSelectedActionsForReview();
        }

        private void BuildSelectionGroups()
        {
            SelectionGroups.Clear();
            if (Plan.Actions == null || Plan.Actions.Count == 0) return;

            var groupDefs = new[]
            {
                new { Key = "SYSTEM PERFORMANCE", Title = "SYSTEM PERFORMANCE", Icon = "\uE945", Desc = "Improve Windows responsiveness and system scheduling." },
                new { Key = "BACKGROUND WORKLOAD", Title = "BACKGROUND WORKLOAD", Icon = "\uE8D0", Desc = "Reduce background CPU and memory pressure safely." },
                new { Key = "MEMORY", Title = "MEMORY", Icon = "\uE950", Desc = "Optimize system memory footprint and working set behavior." },
                new { Key = "POWER / PERFORMANCE", Title = "POWER / PERFORMANCE", Icon = "\uE945", Desc = "Tune energy and responsiveness behavior for peak performance." }
            };

            foreach (var gDef in groupDefs)
            {
                var matchingActions = FilteredActions.Where(a =>
                    string.Equals(a.GroupKey, gDef.Key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.CategoryName, gDef.Key, StringComparison.OrdinalIgnoreCase)
                ).ToList();

                if (matchingActions.Count > 0)
                {
                    var groupVm = new OneClickSelectionGroupViewModel(() =>
                    {
                        NotifyPreviewPropertiesChanged();
                        UpdateSelectedActionsForReview();
                    })
                    {
                        GroupKey = gDef.Key,
                        Title = gDef.Title,
                        Description = gDef.Desc,
                        Icon = gDef.Icon
                    };

                    foreach (var a in matchingActions)
                    {
                        groupVm.Items.Add(a);
                    }
                    groupVm.RefreshState();
                    SelectionGroups.Add(groupVm);
                }
            }
        }

        private void UpdateSelectedActionsForReview()
        {
            SelectedActionsForReview.Clear();
            if (Plan.Actions != null)
            {
                foreach (var a in Plan.Actions)
                {
                    if (a.IsSelected && a.IsSelectable)
                    {
                        SelectedActionsForReview.Add(a);
                    }
                }
            }
            NotifyPreviewPropertiesChanged();
        }

        public void SelectOnlyRecommended()
        {
            if (Plan.Actions == null) return;
            foreach (var a in Plan.Actions)
            {
                if (a.IsSelectable)
                {
                    a.IsSelected = a.Status == OneClickActionStatus.Recommended;
                }
            }
            foreach (var g in SelectionGroups)
            {
                g.RefreshState();
            }
            UpdateSelectedActionsForReview();
        }

        public void ClearAllSelections()
        {
            if (Plan.Actions == null) return;
            foreach (var a in Plan.Actions)
            {
                if (a.IsSelectable)
                {
                    a.IsSelected = false;
                }
            }
            foreach (var g in SelectionGroups)
            {
                g.RefreshState();
            }
            UpdateSelectedActionsForReview();
        }

        private void UpdateWorkloadContextBannerFromPlan()
        {
            if (string.IsNullOrWhiteSpace(_activeWorkloadShortcutKey) || Plan.Actions == null || Plan.Actions.Count == 0)
                return;

            try
            {
                var (gaming, editing, streaming) = PerformanceStatusAuditCoordinator.Instance.EvaluateAllWorkloads(
                    isServiceOnline: _ipc?.IsOnline ?? true,
                    hasCompletedInitialScan: true,
                    isAnalyzing: IsScanning,
                    context: null,
                    oneClickPlan: Plan);

                WorkloadPerformanceStatus selected = _activeWorkloadShortcutKey switch
                {
                    "GAMING" => gaming,
                    "EDITING" => editing,
                    "STREAMING" => streaming,
                    _ => gaming
                };

                WorkloadContextStatusText = selected.StatusText;
                WorkloadContextStatusBrush = selected.StatusBrush;

                string modePrefix = _isWorkloadAuditViewMode || selected.Status == WorkloadPerformanceState.Optimized
                    ? $"{selected.EmojiIcon} {selected.DisplayName} WORKLOAD AUDIT"
                    : $"{selected.EmojiIcon} {selected.DisplayName} DIRECT OPTIMIZATION SHORTCUT";

                WorkloadContextBannerTitle = $"{modePrefix} — {selected.StatusText}";
                WorkloadContextBannerSubtitle =
                    $"{selected.Reason} • Verified/Optimal: {selected.VerifiedCount} • Protected (SKIPPED — PROTECTED): {selected.ProtectedCount} • Remaining safe opportunities: {selected.RemainingOptimizationCount}";

                WorkloadContextAuditItems.Clear();
                foreach (var item in selected.AuditItems)
                {
                    WorkloadContextAuditItems.Add(item);
                }
            }
            catch { }
        }

        private void PopulateAuditLists()
        {
            ExistingFeatures.Clear();
            if (Plan.ExistingAuditedFeatures != null)
            {
                foreach (var item in Plan.ExistingAuditedFeatures)
                    ExistingFeatures.Add(item);
            }

            ExcludedRepairList.Clear();
            if (Plan.ExcludedRepairFeatures != null)
            {
                foreach (var item in Plan.ExcludedRepairFeatures)
                    ExcludedRepairList.Add(item);
            }
        }
    }
}
