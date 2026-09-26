#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels.Base;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.GUI.ViewModels
{
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

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string ConfidenceBadge => $"{SafetyConfidence * 100:F0}% Safe";
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
            "Verified" or "Applied" => "SuccessBrush",
            "Skipped" => "AccentBrush",
            "Failed" => "DangerBrush",
            _ => "TextMutedBrush"
        };
    }

    /// <summary>
    /// ViewModel for ERROR OPTIMIZER V4 — MAXIMUM PERFORMANCE Dashboard.
    /// </summary>
    public class MaxPerformanceViewModel : ViewModelBase
    {
        private readonly IIpcClient _ipc;

        public MaxPerformanceViewModel(IIpcClient ipc)
        {
            _ipc = ipc;

            ScanCommand = new RelayCommand(async _ => await RefreshIntelligenceAsync(), _ => !IsBusy);
            PreviewCommand = new RelayCommand(async _ => await PreviewPlanAsync(), _ => !IsBusy);
            ApplyOptimizationCommand = new RelayCommand(async _ => await ExecuteFullOptimizationAsync(), _ => !IsBusy);
            RollbackCommand = new RelayCommand(async _ => await ExecuteRollbackAsync(), _ => !IsBusy && !string.IsNullOrEmpty(LastBatchId));

            // Initial load
            _ = RefreshIntelligenceAsync();
        }

        // ── State & Progress ──────────────────────────────────────────
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private string _statusMessage = "Ready — System scanned";
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        // ── Metrics & Process Counts ──────────────────────────────────
        private int _currentProcessCount;
        public int CurrentProcessCount
        {
            get => _currentProcessCount;
            set { _currentProcessCount = value; OnPropertyChanged(); }
        }

        private int _initialProcessCount;
        public int InitialProcessCount
        {
            get => _initialProcessCount;
            set { _initialProcessCount = value; OnPropertyChanged(); }
        }

        private int _protectedProcessCount;
        public int ProtectedProcessCount
        {
            get => _protectedProcessCount;
            set { _protectedProcessCount = value; OnPropertyChanged(); }
        }

        private int _requiredProcessCount;
        public int RequiredProcessCount
        {
            get => _requiredProcessCount;
            set { _requiredProcessCount = value; OnPropertyChanged(); }
        }

        private int _workloadProcessCount;
        public int WorkloadProcessCount
        {
            get => _workloadProcessCount;
            set { _workloadProcessCount = value; OnPropertyChanged(); }
        }

        private int _reducibleProcessCount;
        public int ReducibleProcessCount
        {
            get => _reducibleProcessCount;
            set { _reducibleProcessCount = value; OnPropertyChanged(); }
        }

        private int _safeMinimumLow = 70;
        public int SafeMinimumLow
        {
            get => _safeMinimumLow;
            set { _safeMinimumLow = value; OnPropertyChanged(); OnPropertyChanged(nameof(SafeMinimumRangeText)); }
        }

        private int _safeMinimumHigh = 85;
        public int SafeMinimumHigh
        {
            get => _safeMinimumHigh;
            set { _safeMinimumHigh = value; OnPropertyChanged(); OnPropertyChanged(nameof(SafeMinimumRangeText)); }
        }

        public string SafeMinimumRangeText => $"{SafeMinimumLow}–{SafeMinimumHigh}";

        // ── RAM & Performance Deltas ──────────────────────────────────
        private double _ramUsedGb;
        public double RamUsedGb
        {
            get => _ramUsedGb;
            set { _ramUsedGb = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedRamUsed)); }
        }

        public string FormattedRamUsed => $"{RamUsedGb:F1} GB";

        private double _ramRecoveredMb;
        public double RamRecoveredMb
        {
            get => _ramRecoveredMb;
            set { _ramRecoveredMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedRamRecovered)); }
        }

        public string FormattedRamRecovered => RamRecoveredMb >= 1024.0
            ? $"{RamRecoveredMb / 1024.0:F1} GB recovered"
            : $"{RamRecoveredMb:F0} MB recovered";

        private int _processesReducedCount;
        public int ProcessesReducedCount
        {
            get => _processesReducedCount;
            set { _processesReducedCount = value; OnPropertyChanged(); }
        }

        private int _actionsAppliedCount;
        public int ActionsAppliedCount
        {
            get => _actionsAppliedCount;
            set { _actionsAppliedCount = value; OnPropertyChanged(); }
        }

        private int _actionsSkippedCount;
        public int ActionsSkippedCount
        {
            get => _actionsSkippedCount;
            set { _actionsSkippedCount = value; OnPropertyChanged(); }
        }

        private string _stopReason = string.Empty;
        public string StopReason
        {
            get => _stopReason;
            set { _stopReason = value; OnPropertyChanged(); }
        }

        private string _lastBatchId = string.Empty;
        public string LastBatchId
        {
            get => _lastBatchId;
            set { _lastBatchId = value; OnPropertyChanged(); }
        }

        // ── Workload Shields ──────────────────────────────────────────
        private bool _isGamingActive;
        public bool IsGamingActive
        {
            get => _isGamingActive;
            set { _isGamingActive = value; OnPropertyChanged(); }
        }

        private bool _isEditingActive;
        public bool IsEditingActive
        {
            get => _isEditingActive;
            set { _isEditingActive = value; OnPropertyChanged(); }
        }

        private string _workloadDescription = "No active heavy gaming or rendering workloads detected.";
        public string WorkloadDescription
        {
            get => _workloadDescription;
            set { _workloadDescription = value; OnPropertyChanged(); }
        }

        private bool _hasCompletedOptimization;
        public bool HasCompletedOptimization
        {
            get => _hasCompletedOptimization;
            set { _hasCompletedOptimization = value; OnPropertyChanged(); }
        }

        // ── Collections ───────────────────────────────────────────────
        public ObservableCollection<OptimizationCandidateItemModel> Candidates { get; } = new();
        public ObservableCollection<OptimizationActionItemModel> ActionHistory { get; } = new();
        public ObservableCollection<PassProgressionDto> PassHistory { get; } = new();

        // ── System Performance Tuning Metrics ─────────────────────────
        private SystemTuningMetricsDto _systemTuning = new();
        public SystemTuningMetricsDto SystemTuning
        {
            get => _systemTuning;
            set
            {
                _systemTuning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StandbyPurgedText));
                OnPropertyChanged(nameof(MmcssStatusText));
                OnPropertyChanged(nameof(Win32PriorityText));
                OnPropertyChanged(nameof(NetworkThrottlingText));
            }
        }

        public string StandbyPurgedText => _systemTuning.StandbyListPurgedMb > 0 
            ? $"{_systemTuning.StandbyListPurgedMb:F0} MB Purged" 
            : "Standby Cache Clean";
        public string MmcssStatusText => _systemTuning.MmcssPriorityBoostEnabled ? "Active (Gaming/Audio Priority)" : "Standard";
        public string Win32PriorityText => _systemTuning.Win32PrioritySeparationConfigured ? "0x26 (Short Variable High Quantum)" : "Default";
        public string NetworkThrottlingText => _systemTuning.NetworkThrottlingIndexDisabled ? "Disabled (0xFFFFFFFF Zero Packet Throttling)" : "Standard";

        // ── Commands ──────────────────────────────────────────────────
        public ICommand ScanCommand { get; }
        public ICommand PreviewCommand { get; }
        public ICommand ApplyOptimizationCommand { get; }
        public ICommand RollbackCommand { get; }

        // ── Operations ────────────────────────────────────────────────
        public async Task RefreshIntelligenceAsync()
        {
            IsBusy = true;
            StatusMessage = "Analyzing system processes and workload state...";

            try
            {
                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4ScanProcessIntelligence);
                if (resp != null && resp.Success && !string.IsNullOrEmpty(resp.Data))
                {
                    var snapshot = JsonSerializer.Deserialize<ProcessSnapshotDto>(resp.Data);
                    if (snapshot != null)
                    {
                        CurrentProcessCount = snapshot.TotalProcessCount;
                        if (InitialProcessCount == 0) InitialProcessCount = CurrentProcessCount;

                        ProtectedProcessCount = snapshot.ProtectedProcessCount;
                        RequiredProcessCount = snapshot.RequiredProcessCount;
                        WorkloadProcessCount = snapshot.WorkloadProcessCount;
                        ReducibleProcessCount = snapshot.ReducibleProcessCount;

                        // Query plan for safe minimum bounds
                        var planResp = await _ipc.SendRequestAsync(IpcMessageType.V4GetMaximumPerformancePlan);
                        if (planResp != null && planResp.Success && !string.IsNullOrEmpty(planResp.Data))
                        {
                            var plan = JsonSerializer.Deserialize<OptimizationPlanDto>(planResp.Data);
                            if (plan != null)
                            {
                                SafeMinimumLow = plan.TargetSafeMinimumRangeLow;
                                SafeMinimumHigh = plan.TargetSafeMinimumRangeHigh;

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
                                        IsSelected = c.IsSelected
                                    });
                                }
                            }
                        }

                        // Live Metrics
                        var metricsResp = await _ipc.SendRequestAsync(IpcMessageType.V4GetLiveMetrics);
                        if (metricsResp != null && metricsResp.Success && !string.IsNullOrEmpty(metricsResp.Data))
                        {
                            var metrics = JsonSerializer.Deserialize<OptimizationMetricsDto>(metricsResp.Data);
                            if (metrics != null)
                            {
                                RamUsedGb = Math.Round(metrics.RamUsedMb / 1024.0, 1);
                                IsGamingActive = metrics.IsGamingWorkloadActive;
                                IsEditingActive = metrics.IsEditingWorkloadActive;
                                WorkloadDescription = string.IsNullOrEmpty(metrics.ActiveWorkloadSummary)
                                    ? "Standard Mode — Normal background tasks"
                                    : $"Active Workload Shield: {metrics.ActiveWorkloadSummary}";
                            }
                        }

                        StatusMessage = $"Scan complete. {ReducibleProcessCount} reducible background items identified.";
                    }
                }
                else
                {
                    StatusMessage = $"Scan failed: {resp?.ErrorMessage ?? "Service unavailable"}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Scan error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task PreviewPlanAsync()
        {
            await RefreshIntelligenceAsync();
            StatusMessage = $"Plan Preview: {Candidates.Count} candidate optimizations ready for execution.";
        }

        public async Task ExecuteFullOptimizationAsync()
        {
            IsBusy = true;
            StatusMessage = "Executing Maximum Performance Optimization Loop (Scan → Plan → Backup → Apply → Verify)...";

            try
            {
                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4ExecuteFullOptimization, "4");
                if (resp != null && resp.Success && !string.IsNullOrEmpty(resp.Data))
                {
                    var result = JsonSerializer.Deserialize<OptimizationResultDto>(resp.Data);
                    if (result != null)
                    {
                        InitialProcessCount = result.InitialProcessCount;
                        CurrentProcessCount = result.FinalProcessCount;
                        ProcessesReducedCount = result.ActualProcessReduction;
                        RamRecoveredMb = result.RamRecoveredMb;
                        ActionsAppliedCount = result.ActionsAppliedCount;
                        ActionsSkippedCount = result.ActionsSkippedCount;
                        StopReason = result.StopReason;
                        LastBatchId = result.TransactionBatchId;
                        HasCompletedOptimization = true;

                        if (result.SystemTuning != null)
                        {
                            SystemTuning = result.SystemTuning;
                        }

                        PassHistory.Clear();
                        if (result.PassHistory != null)
                        {
                            foreach (var pass in result.PassHistory)
                            {
                                PassHistory.Add(pass);
                            }
                        }

                        ActionHistory.Clear();
                        foreach (var a in result.Actions)
                        {
                            ActionHistory.Add(new OptimizationActionItemModel
                            {
                                ActionType = a.ActionType,
                                TargetIdentifier = a.TargetIdentifier,
                                Status = a.Status,
                                Reason = a.Reason,
                                VerificationMessage = a.VerificationMessage
                            });
                        }

                        StatusMessage = $"Optimization Finished! Safely reduced from {InitialProcessCount} to {CurrentProcessCount} processes.";
                    }
                }
                else
                {
                    StatusMessage = $"Optimization failed: {resp?.ErrorMessage ?? "Service unavailable"}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Optimization error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task ExecuteRollbackAsync()
        {
            if (string.IsNullOrEmpty(LastBatchId)) return;

            IsBusy = true;
            StatusMessage = $"Rolling back batch {LastBatchId}...";

            try
            {
                var resp = await _ipc.SendRequestAsync(IpcMessageType.V4RollbackMaximumPerformance, LastBatchId);
                if (resp != null && resp.Success)
                {
                    StatusMessage = "Rollback successful. All persistent changes restored to prior baseline.";
                    await RefreshIntelligenceAsync();
                }
                else
                {
                    StatusMessage = $"Rollback failed: {resp?.ErrorMessage ?? "Error during rollback"}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rollback error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
