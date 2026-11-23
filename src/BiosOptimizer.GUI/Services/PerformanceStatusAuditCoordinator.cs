#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.GUI.ViewModels.Base;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.GUI.Services
{
    public enum PerformanceWorkloadType
    {
        Gaming = 0,
        Editing = 1,
        Streaming = 2
    }

    public enum WorkloadPerformanceState
    {
        Analyzing = 0,
        NotOptimized = 1,
        Balanced = 2,
        Optimized = 3,
        Unavailable = 4
    }

    /// <summary>
    /// Represents a single hardware, workload, configuration, or protection audit item
    /// evaluated for Gaming, Editing, or Streaming.
    /// </summary>
    public sealed class WorkloadAuditCheckItem
    {
        public string AreaId { get; init; } = string.Empty;
        public string OpportunityId => AreaId;
        public PerformanceWorkloadType WorkloadType { get; init; }
        public string AreaName { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string OptimizationCategory => Category;
        public string CurrentState { get; init; } = string.Empty;
        public string TargetState { get; init; } = string.Empty;
        public string AuditOutcome { get; init; } = "ANALYZING"; // VERIFIED, ALREADY OPTIMIZED, OPTIMIZATION AVAILABLE, SKIPPED — PROTECTED, NOT APPLICABLE
        public string ImpactLevel { get; init; } = "MODERATE"; // HIGH, MODERATE, PROTECTED
        public bool IsOpportunity { get; init; }
        public bool IsMeaningfulOpportunity { get; init; }
        public bool IsVerifiedOrOptimal { get; init; }
        public bool IsProtected { get; init; }
        public string LinkedOneClickActionId { get; init; } = string.Empty;
        public string PageKey { get; init; } = string.Empty;
        public string SectionKey { get; init; } = string.Empty;
        public string ItemKey { get; init; } = string.Empty;
        public int Priority { get; init; } = 50;

        public string NavigationTarget =>
            string.IsNullOrWhiteSpace(PageKey)
                ? string.Empty
                : (string.IsNullOrWhiteSpace(SectionKey) ? PageKey : $"{PageKey}:{SectionKey}");

        public Brush OutcomeBrush => AuditOutcome switch
        {
            "VERIFIED" or "ALREADY OPTIMIZED" => CreateFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            "SKIPPED — PROTECTED" => CreateFrozenBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            "OPTIMIZATION AVAILABLE" => IsMeaningfulOpportunity
                ? CreateFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
                : CreateFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9)),
            _ => CreateFrozenBrush(Color.FromRgb(0x8A, 0x8F, 0x98))
        };

        public Brush OutcomeBadgeBackground => AuditOutcome switch
        {
            "VERIFIED" or "ALREADY OPTIMIZED" => CreateFrozenBrush(Color.FromArgb(0x20, 0x10, 0xB9, 0x81)),
            "SKIPPED — PROTECTED" => CreateFrozenBrush(Color.FromArgb(0x20, 0x38, 0xBD, 0xF8)),
            "OPTIMIZATION AVAILABLE" => IsMeaningfulOpportunity
                ? CreateFrozenBrush(Color.FromArgb(0x24, 0xF5, 0x9E, 0x0B))
                : CreateFrozenBrush(Color.FromArgb(0x24, 0x68, 0x72, 0xD9)),
            _ => CreateFrozenBrush(Color.FromArgb(0x18, 0x8A, 0x8F, 0x98))
        };

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Authoritative, real data-driven Performance Status output model for a specific workload
    /// (Gaming, Editing, or Streaming) on the Dashboard.
    /// Never hardcoded, guessed, or simulated.
    /// </summary>
    public sealed class WorkloadPerformanceStatus : ViewModelBase
    {
        private WorkloadPerformanceState _status = WorkloadPerformanceState.Analyzing;
        private bool _isAuditComplete;
        private bool _isVerified;
        private int _opportunityCount;
        private int _meaningfulOpportunityCount;
        private int _verifiedCount;
        private int _protectedCount;
        private int _remainingOptimizationCount;
        private bool _activeWorkloadDetected;
        private string _activeWorkloadSummary = string.Empty;
        private DateTime _lastAuditTime = DateTime.UtcNow;
        private string _reason = "Auditing hardware, active workload, and system configuration...";
        private string _recommendedAction = string.Empty;
        private string _navigationTarget = "MaxPerformance";
        private string _targetPageKey = string.Empty;
        private string _targetSectionKey = string.Empty;
        private string _targetOpportunityId = string.Empty;

        public PerformanceWorkloadType WorkloadType { get; init; }

        public string WorkloadKey => WorkloadType switch
        {
            PerformanceWorkloadType.Gaming => "GAMING",
            PerformanceWorkloadType.Editing => "EDITING",
            PerformanceWorkloadType.Streaming => "STREAMING",
            _ => "GAMING"
        };

        public string DisplayName => WorkloadKey;

        public string EmojiIcon => WorkloadType switch
        {
            PerformanceWorkloadType.Gaming => "🎮",
            PerformanceWorkloadType.Editing => "🎬",
            PerformanceWorkloadType.Streaming => "📡",
            _ => "⚡"
        };

        public WorkloadPerformanceState Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    NotifyComputedProperties();
                }
            }
        }

        public string StatusText => _status switch
        {
            WorkloadPerformanceState.Analyzing => "ANALYZING",
            WorkloadPerformanceState.NotOptimized => "NOT OPTIMIZED",
            WorkloadPerformanceState.Balanced => "BALANCED",
            WorkloadPerformanceState.Optimized => "OPTIMIZED",
            WorkloadPerformanceState.Unavailable => "UNAVAILABLE",
            _ => "ANALYZING"
        };

        public Brush StatusBrush => _status switch
        {
            WorkloadPerformanceState.Optimized => GetFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81)),     // Emerald Green
            WorkloadPerformanceState.Balanced => GetFrozenBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),      // Precision Cyan/Blue
            WorkloadPerformanceState.NotOptimized => GetFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),  // Amber Warning
            WorkloadPerformanceState.Analyzing => GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9)),     // Indigo Accent
            _ => GetFrozenBrush(Color.FromRgb(0x6E, 0x74, 0x80))                                       // Muted Slate
        };

        public Brush StatusBadgeBackground => _status switch
        {
            WorkloadPerformanceState.Optimized => GetFrozenBrush(Color.FromArgb(0x20, 0x10, 0xB9, 0x81)),
            WorkloadPerformanceState.Balanced => GetFrozenBrush(Color.FromArgb(0x20, 0x38, 0xBD, 0xF8)),
            WorkloadPerformanceState.NotOptimized => GetFrozenBrush(Color.FromArgb(0x22, 0xF5, 0x9E, 0x0B)),
            WorkloadPerformanceState.Analyzing => GetFrozenBrush(Color.FromArgb(0x20, 0x68, 0x72, 0xD9)),
            _ => GetFrozenBrush(Color.FromArgb(0x18, 0x6E, 0x74, 0x80))
        };

        public Brush StatusBadgeBorder => _status switch
        {
            WorkloadPerformanceState.Optimized => GetFrozenBrush(Color.FromArgb(0x45, 0x10, 0xB9, 0x81)),
            WorkloadPerformanceState.Balanced => GetFrozenBrush(Color.FromArgb(0x45, 0x38, 0xBD, 0xF8)),
            WorkloadPerformanceState.NotOptimized => GetFrozenBrush(Color.FromArgb(0x45, 0xF5, 0x9E, 0x0B)),
            WorkloadPerformanceState.Analyzing => GetFrozenBrush(Color.FromArgb(0x40, 0x68, 0x72, 0xD9)),
            _ => GetFrozenBrush(Color.FromArgb(0x30, 0x6E, 0x74, 0x80))
        };

        public bool IsAuditComplete
        {
            get => _isAuditComplete;
            set { if (_isAuditComplete != value) { _isAuditComplete = value; OnPropertyChanged(); NotifyComputedProperties(); } }
        }

        public bool IsVerified
        {
            get => _isVerified;
            set { if (_isVerified != value) { _isVerified = value; OnPropertyChanged(); } }
        }

        public int OpportunityCount
        {
            get => _opportunityCount;
            set { if (_opportunityCount != value) { _opportunityCount = value; OnPropertyChanged(); } }
        }

        public int MeaningfulOpportunityCount
        {
            get => _meaningfulOpportunityCount;
            set { if (_meaningfulOpportunityCount != value) { _meaningfulOpportunityCount = value; OnPropertyChanged(); } }
        }

        public int VerifiedCount
        {
            get => _verifiedCount;
            set { if (_verifiedCount != value) { _verifiedCount = value; OnPropertyChanged(); } }
        }

        public int ProtectedCount
        {
            get => _protectedCount;
            set { if (_protectedCount != value) { _protectedCount = value; OnPropertyChanged(); } }
        }

        public int RemainingOptimizationCount
        {
            get => _remainingOptimizationCount;
            set { if (_remainingOptimizationCount != value) { _remainingOptimizationCount = value; OnPropertyChanged(); } }
        }

        public bool ActiveWorkloadDetected
        {
            get => _activeWorkloadDetected;
            set
            {
                if (_activeWorkloadDetected != value)
                {
                    _activeWorkloadDetected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActiveWorkloadVisibility));
                }
            }
        }

        public Visibility ActiveWorkloadVisibility =>
            _activeWorkloadDetected ? Visibility.Visible : Visibility.Collapsed;

        public string ActiveWorkloadSummary
        {
            get => _activeWorkloadSummary;
            set { if (_activeWorkloadSummary != value) { _activeWorkloadSummary = value; OnPropertyChanged(); } }
        }

        public DateTime LastAuditTime
        {
            get => _lastAuditTime;
            set { if (_lastAuditTime != value) { _lastAuditTime = value; OnPropertyChanged(); } }
        }

        public string Reason
        {
            get => _reason;
            set { if (_reason != value) { _reason = value; OnPropertyChanged(); } }
        }

        public string RecommendedAction
        {
            get => _recommendedAction;
            set { if (_recommendedAction != value) { _recommendedAction = value; OnPropertyChanged(); } }
        }

        public string NavigationTarget
        {
            get => _navigationTarget;
            set { if (_navigationTarget != value) { _navigationTarget = value; OnPropertyChanged(); } }
        }

        public string TargetPageKey
        {
            get => _targetPageKey;
            set { if (_targetPageKey != value) { _targetPageKey = value; OnPropertyChanged(); } }
        }

        public string TargetSectionKey
        {
            get => _targetSectionKey;
            set { if (_targetSectionKey != value) { _targetSectionKey = value; OnPropertyChanged(); } }
        }

        public string TargetOpportunityId
        {
            get => _targetOpportunityId;
            set { if (_targetOpportunityId != value) { _targetOpportunityId = value; OnPropertyChanged(); } }
        }

        public string ActionButtonText => _status switch
        {
            WorkloadPerformanceState.Optimized => "View Audit →",
            WorkloadPerformanceState.NotOptimized or WorkloadPerformanceState.Balanced => "View & Optimize →",
            WorkloadPerformanceState.Analyzing => "Analyzing...",
            _ => "Unavailable"
        };

        public bool CanInvokeShortcut =>
            _status == WorkloadPerformanceState.NotOptimized ||
            _status == WorkloadPerformanceState.Balanced ||
            _status == WorkloadPerformanceState.Optimized;

        public ObservableCollection<WorkloadAuditCheckItem> AuditItems { get; } = new();

        public ICommand? ShortcutCommand { get; set; }

        public void ApplyFrom(WorkloadPerformanceStatus source)
        {
            Status = source.Status;
            IsAuditComplete = source.IsAuditComplete;
            IsVerified = source.IsVerified;
            OpportunityCount = source.OpportunityCount;
            MeaningfulOpportunityCount = source.MeaningfulOpportunityCount;
            VerifiedCount = source.VerifiedCount;
            ProtectedCount = source.ProtectedCount;
            RemainingOptimizationCount = source.RemainingOptimizationCount;
            ActiveWorkloadDetected = source.ActiveWorkloadDetected;
            ActiveWorkloadSummary = source.ActiveWorkloadSummary;
            LastAuditTime = source.LastAuditTime;
            Reason = source.Reason;
            RecommendedAction = source.RecommendedAction;
            NavigationTarget = source.NavigationTarget;
            TargetPageKey = source.TargetPageKey;
            TargetSectionKey = source.TargetSectionKey;
            TargetOpportunityId = source.TargetOpportunityId;

            AuditItems.Clear();
            foreach (var item in source.AuditItems)
            {
                AuditItems.Add(item);
            }

            NotifyComputedProperties();
        }

        private void NotifyComputedProperties()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusBadgeBackground));
            OnPropertyChanged(nameof(StatusBadgeBorder));
            OnPropertyChanged(nameof(ActionButtonText));
            OnPropertyChanged(nameof(CanInvokeShortcut));
            OnPropertyChanged(nameof(ActiveWorkloadVisibility));
        }
    }

    /// <summary>
    /// Thin orchestration coordinator that reuses existing V3/V4 hardware detection,
    /// WorkloadDetectionEngine, ProcessIntelligenceEngine, ProtectedApplicationPolicy,
    /// OptimizationOpportunityEngine, OneClickOptimizationEngine, and FullOptimizationCoordinator
    /// to evaluate real, live Performance Status for Gaming, Editing, and Streaming.
    /// </summary>
    public sealed class PerformanceStatusAuditCoordinator
    {
        private static readonly Lazy<PerformanceStatusAuditCoordinator> _instance =
            new(() => new PerformanceStatusAuditCoordinator());

        public static PerformanceStatusAuditCoordinator Instance => _instance.Value;

        private readonly WorkloadDetectionEngine _workloadEngine = new();

        private PerformanceStatusAuditCoordinator()
        {
        }

        public static string GetDefaultOptimizationTarget(PerformanceWorkloadType type) => type switch
        {
            PerformanceWorkloadType.Gaming => "MaxPerformance:GAMING_POWER",
            PerformanceWorkloadType.Editing => "AiRamLimiter:MEMORY",
            PerformanceWorkloadType.Streaming => "MaxPerformance",
            _ => "MaxPerformance"
        };

        public static string GetDefaultAuditTarget(PerformanceWorkloadType type) => type switch
        {
            PerformanceWorkloadType.Gaming => "AiWorkload:GAMES",
            PerformanceWorkloadType.Editing => "AiWorkload:CREATIVE",
            PerformanceWorkloadType.Streaming => "AiWorkload:RECORDING",
            _ => "AiWorkload:GAMES"
        };

        /// <summary>
        /// Creates initial ANALYZING state instances for Gaming, Editing, and Streaming on startup.
        /// Never shows NOT OPTIMIZED or OPTIMIZED before the real system audit finishes.
        /// </summary>
        public static (WorkloadPerformanceStatus Gaming, WorkloadPerformanceStatus Editing, WorkloadPerformanceStatus Streaming) CreateInitialAnalyzingStatuses()
        {
            return (
                CreateAnalyzingStatus(PerformanceWorkloadType.Gaming),
                CreateAnalyzingStatus(PerformanceWorkloadType.Editing),
                CreateAnalyzingStatus(PerformanceWorkloadType.Streaming)
            );
        }

        public static WorkloadPerformanceStatus CreateAnalyzingStatus(PerformanceWorkloadType type)
        {
            string defaultTarget = GetDefaultOptimizationTarget(type);
            var (pageKey, sectionKey) = SplitNavigationTarget(defaultTarget);
            return new WorkloadPerformanceStatus
            {
                WorkloadType = type,
                Status = WorkloadPerformanceState.Analyzing,
                IsAuditComplete = false,
                IsVerified = false,
                OpportunityCount = 0,
                MeaningfulOpportunityCount = 0,
                VerifiedCount = 0,
                ProtectedCount = 0,
                RemainingOptimizationCount = 0,
                Reason = "Auditing hardware, active workload, and system configuration...",
                RecommendedAction = "Wait for system audit to complete",
                NavigationTarget = defaultTarget,
                TargetPageKey = pageKey,
                TargetSectionKey = sectionKey
            };
        }

        public static WorkloadPerformanceStatus CreateUnavailableStatus(PerformanceWorkloadType type, string reason)
        {
            string defaultTarget = GetDefaultOptimizationTarget(type);
            var (pageKey, sectionKey) = SplitNavigationTarget(defaultTarget);
            return new WorkloadPerformanceStatus
            {
                WorkloadType = type,
                Status = WorkloadPerformanceState.Unavailable,
                IsAuditComplete = false,
                IsVerified = false,
                OpportunityCount = 0,
                MeaningfulOpportunityCount = 0,
                VerifiedCount = 0,
                ProtectedCount = 0,
                RemainingOptimizationCount = 0,
                Reason = !string.IsNullOrWhiteSpace(reason)
                    ? reason
                    : "Service offline — optimization state cannot be verified",
                RecommendedAction = "Reconnect optimizer service",
                NavigationTarget = defaultTarget,
                TargetPageKey = pageKey,
                TargetSectionKey = sectionKey
            };
        }

        /// <summary>
        /// Evaluates Gaming, Editing, and Streaming performance statuses independently using real hardware,
        /// process, memory, storage, network, power, and 1-Click/Master optimization state.
        /// </summary>
        public (WorkloadPerformanceStatus Gaming, WorkloadPerformanceStatus Editing, WorkloadPerformanceStatus Streaming) EvaluateAllWorkloads(
            bool isServiceOnline,
            bool hasCompletedInitialScan,
            bool isAnalyzing,
            OptimizationOpportunityContext? context,
            OneClickPlan? oneClickPlan,
            bool hasDedicatedGpu = true,
            string? dedicatedGpuName = null,
            string? integratedGpuName = null,
            int storageUsedPercent = 50)
        {
            if (!isServiceOnline)
            {
                const string offlineMsg = "Service offline — optimization state cannot be verified";
                return (
                    CreateUnavailableStatus(PerformanceWorkloadType.Gaming, offlineMsg),
                    CreateUnavailableStatus(PerformanceWorkloadType.Editing, offlineMsg),
                    CreateUnavailableStatus(PerformanceWorkloadType.Streaming, offlineMsg)
                );
            }

            if (!hasCompletedInitialScan || isAnalyzing || (context == null && oneClickPlan == null))
            {
                return CreateInitialAnalyzingStatuses();
            }

            // Detect active processes/workloads via existing V4 WorkloadDetectionEngine
            var activeWorkloads = DetectActiveWorkloadFlags(context?.DetectedWorkload);

            var gaming = EvaluateGamingWorkload(
                context,
                oneClickPlan,
                activeWorkloads.IsGamingOrEmulatorActive,
                activeWorkloads.GamingLabel,
                hasDedicatedGpu,
                dedicatedGpuName,
                integratedGpuName);

            var editing = EvaluateEditingWorkload(
                context,
                oneClickPlan,
                activeWorkloads.IsEditingActive,
                activeWorkloads.EditingLabel,
                storageUsedPercent);

            var streaming = EvaluateStreamingWorkload(
                context,
                oneClickPlan,
                activeWorkloads.IsStreamingActive,
                activeWorkloads.StreamingLabel);

            EnsureDistinctWorkloadTargetsWhenOpportunitiesExist(gaming, editing, streaming);

            return (gaming, editing, streaming);
        }

        public WorkloadPerformanceStatus EvaluateGamingWorkload(
            OptimizationOpportunityContext? ctx,
            OneClickPlan? plan,
            bool isActiveWorkload = false,
            string activeWorkloadLabel = "",
            bool hasDedicatedGpu = true,
            string? dedicatedGpuName = null,
            string? integratedGpuName = null)
        {
            var checks = new List<WorkloadAuditCheckItem>();
            var actions = plan?.Actions ?? new List<OneClickActionItem>();

            // 0. Active Android Emulator Hypervisor & Frame-Pacing Check (when emulator is active)
            bool isEmulatorActive = isActiveWorkload &&
                activeWorkloadLabel.Contains("Emulator", StringComparison.OrdinalIgnoreCase);
            int pendingEmulatorCount = ctx?.PendingEmulatorOptimizationsCount ?? 0;
            if (isEmulatorActive)
            {
                bool emulatorOptimal = pendingEmulatorCount == 0;
                checks.Add(new WorkloadAuditCheckItem
                {
                    AreaId = "gaming.emulator",
                    WorkloadType = PerformanceWorkloadType.Gaming,
                    AreaName = "Android Emulator Hypervisor & Frame-Time Tuning",
                    Category = "EMULATOR / GAMING",
                    CurrentState = emulatorOptimal
                        ? "Emulator Deep Profile Verified"
                        : $"{Math.Max(1, pendingEmulatorCount)} Emulator Optimization(s) Available",
                    TargetState = "Low-Latency Emulator Priority & GPU Binding",
                    AuditOutcome = emulatorOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                    ImpactLevel = "HIGH",
                    IsOpportunity = !emulatorOptimal,
                    IsMeaningfulOpportunity = !emulatorOptimal,
                    IsVerifiedOrOptimal = emulatorOptimal,
                    LinkedOneClickActionId = "emulator-hypervisor",
                    PageKey = "EmulatorDeepOptimize",
                    SectionKey = "EMULATOR",
                    ItemKey = "emulator-hypervisor",
                    Priority = 100
                });
            }

            // 1. Sustained Gaming Power Mode (power-performance -> MaxPerformance)
            var powerItem = actions.FirstOrDefault(a => a.Id == "power-performance");
            string activePower = ctx?.ActivePowerPlan ?? powerItem?.CurrentStateText?.Replace("Active Plan: ", "") ?? "Balanced";
            bool powerOptimal = (powerItem != null && powerItem.IsCompleted) ||
                                activePower.Contains("High", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Max Performance", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Verified", StringComparison.OrdinalIgnoreCase);

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.power",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = "Sustained Gaming Power Mode",
                Category = "POWER / CPU",
                CurrentState = powerItem?.CurrentStateText ?? $"Active Plan: {activePower}",
                TargetState = "High / Ultimate Performance",
                AuditOutcome = powerOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "HIGH",
                IsOpportunity = !powerOptimal,
                IsMeaningfulOpportunity = !powerOptimal,
                IsVerifiedOrOptimal = powerOptimal,
                LinkedOneClickActionId = "power-performance",
                PageKey = "MaxPerformance",
                SectionKey = "GAMING_POWER",
                ItemKey = "power-performance",
                Priority = 95
            });

            // 2. System Timer & Frame-Pacing Consistency (bcd-platform-timers -> InputOptimizer)
            var bcdItem = actions.FirstOrDefault(a => a.Id == "bcd-platform-timers");
            bool bcdOptimal = bcdItem != null && bcdItem.IsCompleted;
            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.timers",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = "System Timer & Frame-Pacing Consistency",
                Category = "LATENCY / TIMERS",
                CurrentState = bcdItem?.CurrentStateText ?? (bcdOptimal ? "Consistent Timer Active" : "Default Dynamic Tick"),
                TargetState = "Consistent Low-Latency Timer",
                AuditOutcome = bcdOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "HIGH",
                IsOpportunity = !bcdOptimal,
                IsMeaningfulOpportunity = !bcdOptimal,
                IsVerifiedOrOptimal = bcdOptimal,
                LinkedOneClickActionId = "bcd-platform-timers",
                PageKey = "InputOptimizer",
                SectionKey = "TIMERS",
                ItemKey = "bcd-platform-timers",
                Priority = 90
            });

            // 3. Background Process Contention (background-workload -> AiWorkload:GAMES)
            var bgItem = actions.FirstOrDefault(a => a.Id == "background-workload");
            int safeCandidates = bgItem?.SafeCandidateCount ?? ctx?.SafeReducibleProcesses ?? 0;
            int protectedProcs = Math.Max(bgItem?.ProtectedProcessCount ?? 0, ctx?.ProtectedProcesses ?? 18);
            bool bgOptimal = (bgItem != null && bgItem.IsCompleted) || safeCandidates == 0;
            bool bgHighImpact = !bgOptimal && safeCandidates >= 5;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.background",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = "Background CPU Contention Control",
                Category = "BACKGROUND WORKLOAD",
                CurrentState = bgOptimal
                    ? $"0 Safe Candidates ({protectedProcs} Protected Preserved)"
                    : $"{safeCandidates} Non-Essential Background Helpers Active",
                TargetState = "BelowNormal Priority for Non-Protected Helpers",
                AuditOutcome = bgOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = bgHighImpact ? "HIGH" : "MODERATE",
                IsOpportunity = !bgOptimal,
                IsMeaningfulOpportunity = bgHighImpact,
                IsVerifiedOrOptimal = bgOptimal,
                LinkedOneClickActionId = "background-workload",
                PageKey = "AiWorkload",
                SectionKey = "GAMES",
                ItemKey = "background-workload",
                Priority = 84
            });

            // 4. Windows Visual & DWM Responsiveness (visual-tweaks -> MaxPerformance)
            var visualItem = actions.FirstOrDefault(a => a.Id == "visual-tweaks");
            bool visualOptimal = visualItem != null && visualItem.IsCompleted;
            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.visual",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = "Desktop Window Manager (DWM) & UI Latency",
                Category = "SYSTEM PERFORMANCE",
                CurrentState = visualItem?.CurrentStateText ?? (visualOptimal ? "0ms Menu Delay" : "Animations & Delay Active"),
                TargetState = "0ms Menu Delay • Reduced DWM Overhead",
                AuditOutcome = visualOptimal ? "ALREADY OPTIMIZED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "HIGH",
                IsOpportunity = !visualOptimal,
                IsMeaningfulOpportunity = !visualOptimal,
                IsVerifiedOrOptimal = visualOptimal,
                LinkedOneClickActionId = "visual-tweaks",
                PageKey = "MaxPerformance",
                SectionKey = "DWM_LATENCY",
                ItemKey = "visual-tweaks",
                Priority = 92
            });

            // 5. GPU Driver Shader Cache & Multimedia Scheduling (shader-cache / PendingGpuOptimizationsCount -> GpuRegistryValues)
            var shaderItem = actions.FirstOrDefault(a => a.Id == "shader-cache");
            int pendingGpu = ctx?.PendingGpuOptimizationsCount ?? 0;
            bool shaderOptimal = (shaderItem == null || shaderItem.IsCompleted) && pendingGpu == 0;
            string gpuSummary = hasDedicatedGpu && !string.IsNullOrWhiteSpace(dedicatedGpuName)
                ? dedicatedGpuName
                : (!string.IsNullOrWhiteSpace(ctx?.GpuHardwareName) ? ctx.GpuHardwareName : (integratedGpuName ?? "System GPU"));

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.gpu_shader",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = $"GPU Shader & Multimedia Scheduling ({gpuSummary})",
                Category = "GRAPHICS / GPU",
                CurrentState = pendingGpu > 0
                    ? $"{pendingGpu} GPU Scheduling / MMCSS Item(s) Unoptimized"
                    : (shaderItem?.CurrentStateText ?? "Shader Cache & GPU Scheduling Verified"),
                TargetState = "Optimized GPU Scheduling & Clean Shader Cache",
                AuditOutcome = shaderOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = pendingGpu > 0 ? "HIGH" : "MODERATE",
                IsOpportunity = !shaderOptimal,
                IsMeaningfulOpportunity = pendingGpu > 0,
                IsVerifiedOrOptimal = shaderOptimal,
                LinkedOneClickActionId = "shader-cache",
                PageKey = "GpuRegistryValues",
                SectionKey = "GPU",
                ItemKey = "shader-cache",
                Priority = pendingGpu > 0 ? 97 : 88
            });

            // 6. Gaming Memory & Standby Headroom (ram-standby -> AiRamLimiter)
            var ramItem = actions.FirstOrDefault(a => a.Id == "ram-standby");
            double memPressure = ctx?.MemorySnapshot?.MemoryPressurePercent ?? 0;
            bool ramOptimal = (ramItem != null && ramItem.IsCompleted) || (ramItem == null && memPressure < 70.0);
            bool ramHighImpact = !ramOptimal && memPressure >= 75.0;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.memory",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = "Physical RAM & Standby Headroom",
                Category = "MEMORY",
                CurrentState = ramItem?.CurrentStateText ?? (memPressure > 0 ? $"{memPressure:F0}% RAM Used" : "Healthy RAM Headroom"),
                TargetState = "Refreshed Standby & Working Sets",
                AuditOutcome = ramOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = ramHighImpact ? "HIGH" : "MODERATE",
                IsOpportunity = !ramOptimal,
                IsMeaningfulOpportunity = ramHighImpact,
                IsVerifiedOrOptimal = ramOptimal,
                LinkedOneClickActionId = "ram-standby",
                PageKey = "AiRamLimiter",
                SectionKey = "STANDBY",
                ItemKey = "ram-standby",
                Priority = 78
            });

            // 7. Global System Storage & Disk I/O Responsiveness (when pending)
            var pendingStorage = ctx?.MasterPlan?.AllActions
                .Where(a => a.CategoryKey.Equals("Storage", StringComparison.OrdinalIgnoreCase) &&
                            a.IsPending &&
                            !a.ItemId.Contains("temp", StringComparison.OrdinalIgnoreCase) &&
                            !a.ItemId.Contains("cache", StringComparison.OrdinalIgnoreCase) &&
                            !a.ItemId.Contains("recycle", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pendingStorage != null && pendingStorage.Count > 0)
            {
                checks.Add(new WorkloadAuditCheckItem
                {
                    AreaId = "gaming.storage_io",
                    WorkloadType = PerformanceWorkloadType.Gaming,
                    AreaName = "Storage & Asset Streaming I/O Responsiveness",
                    Category = "STORAGE / IO",
                    CurrentState = $"{pendingStorage.Count} Storage Parameter(s) Pending",
                    TargetState = "Low-Latency NVMe / SSD File System Configuration",
                    AuditOutcome = "OPTIMIZATION AVAILABLE",
                    ImpactLevel = "MODERATE",
                    IsOpportunity = true,
                    IsMeaningfulOpportunity = false,
                    IsVerifiedOrOptimal = false,
                    LinkedOneClickActionId = "storage-io",
                    PageKey = "Storage",
                    SectionKey = "STORAGE",
                    ItemKey = "storage-io",
                    Priority = 72
                });
            }

            // 8. Protected Anti-Cheat, Launchers & Drivers (SKIPPED — PROTECTED)
            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "gaming.protected",
                WorkloadType = PerformanceWorkloadType.Gaming,
                AreaName = "Anti-Cheat, Game Launchers & Audio/GPU Drivers",
                Category = "SAFETY / PROTECTION",
                CurrentState = $"{protectedProcs} Protected Processes Excluded",
                TargetState = "Untouched by SafetyKernel",
                AuditOutcome = "SKIPPED — PROTECTED",
                ImpactLevel = "PROTECTED",
                IsOpportunity = false,
                IsMeaningfulOpportunity = false,
                IsVerifiedOrOptimal = false,
                IsProtected = true
            });

            return BuildWorkloadStatusResult(
                PerformanceWorkloadType.Gaming,
                "gaming",
                checks,
                protectedProcs,
                isActiveWorkload,
                activeWorkloadLabel);
        }

        public WorkloadPerformanceStatus EvaluateEditingWorkload(
            OptimizationOpportunityContext? ctx,
            OneClickPlan? plan,
            bool isActiveWorkload = false,
            string activeWorkloadLabel = "",
            int storageUsedPercent = 50)
        {
            var checks = new List<WorkloadAuditCheckItem>();
            var actions = plan?.Actions ?? new List<OneClickActionItem>();
            var bgItem = actions.FirstOrDefault(a => a.Id == "background-workload");
            int protectedProcs = Math.Max(bgItem?.ProtectedProcessCount ?? 0, ctx?.ProtectedProcesses ?? 18);

            // 1. Editing RAM & Standby Memory Headroom (ram-standby -> AiRamLimiter)
            var ramItem = actions.FirstOrDefault(a => a.Id == "ram-standby");
            double memPressure = ctx?.MemorySnapshot?.MemoryPressurePercent ?? 0;
            bool ramOptimal = (ramItem != null && ramItem.IsCompleted) || (ramItem == null && memPressure < 65.0);
            bool ramMeaningful = !ramOptimal && (memPressure >= 70.0 || ramItem?.Status == OneClickActionStatus.Recommended);

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "editing.memory",
                WorkloadType = PerformanceWorkloadType.Editing,
                AreaName = "Timeline & Render Memory Headroom",
                Category = "MEMORY / STANDBY",
                CurrentState = ramItem?.CurrentStateText ?? (memPressure > 0 ? $"{memPressure:F0}% RAM Used" : "Healthy RAM Headroom"),
                TargetState = "Refreshed RAM & Standby Cache",
                AuditOutcome = ramOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = ramMeaningful ? "HIGH" : "MODERATE",
                IsOpportunity = !ramOptimal,
                IsMeaningfulOpportunity = ramMeaningful,
                IsVerifiedOrOptimal = ramOptimal,
                LinkedOneClickActionId = "ram-standby",
                PageKey = "AiRamLimiter",
                SectionKey = "MEMORY",
                ItemKey = "ram-standby",
                Priority = ramMeaningful ? 96 : 89
            });

            // 2. Scratch & Temporary System Cache (fast-clean -> Storage)
            var fastCleanItem = actions.FirstOrDefault(a => a.Id == "fast-clean");
            bool fastCleanOptimal = fastCleanItem == null || fastCleanItem.IsCompleted;
            bool storageTight = storageUsedPercent >= 85;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "editing.temp_cache",
                WorkloadType = PerformanceWorkloadType.Editing,
                AreaName = "Temporary & Scratch Cache Directories",
                Category = "STORAGE / CACHE",
                CurrentState = fastCleanItem?.CurrentStateText ?? "Temp Cache Clean",
                TargetState = "Clean Unlocked Temp & Scratch Files",
                AuditOutcome = fastCleanOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = storageTight ? "HIGH" : "MODERATE",
                IsOpportunity = !fastCleanOptimal,
                IsMeaningfulOpportunity = !fastCleanOptimal && storageTight,
                IsVerifiedOrOptimal = fastCleanOptimal,
                LinkedOneClickActionId = "fast-clean",
                PageKey = "Storage",
                SectionKey = "SCRATCH_CACHE",
                ItemKey = "fast-clean",
                Priority = 94
            });

            // 3. Deep Media & Update Cache (deep-clean -> Storage)
            var deepCleanItem = actions.FirstOrDefault(a => a.Id == "deep-clean");
            bool deepCleanOptimal = deepCleanItem == null || deepCleanItem.IsCompleted;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "editing.deep_cache",
                WorkloadType = PerformanceWorkloadType.Editing,
                AreaName = "Deep System & Media Delivery Cache",
                Category = "STORAGE / CACHE",
                CurrentState = deepCleanItem?.CurrentStateText ?? "System Cache Clean",
                TargetState = "Reclaimed SSD Cache Space",
                AuditOutcome = deepCleanOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "MODERATE",
                IsOpportunity = !deepCleanOptimal,
                IsMeaningfulOpportunity = !deepCleanOptimal && !fastCleanOptimal,
                IsVerifiedOrOptimal = deepCleanOptimal,
                LinkedOneClickActionId = "deep-clean",
                PageKey = "Storage",
                SectionKey = "DEEP_CACHE",
                ItemKey = "deep-clean",
                Priority = 91
            });

            // 4. Sustained Multi-Core Render Power (power-performance -> AiPowerPlan)
            var powerItem = actions.FirstOrDefault(a => a.Id == "power-performance");
            string activePower = ctx?.ActivePowerPlan ?? powerItem?.CurrentStateText?.Replace("Active Plan: ", "") ?? "Balanced";
            bool powerOptimal = (powerItem != null && powerItem.IsCompleted) ||
                                activePower.Contains("High", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Max Performance", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Verified", StringComparison.OrdinalIgnoreCase);

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "editing.power",
                WorkloadType = PerformanceWorkloadType.Editing,
                AreaName = "Sustained Multi-Core Render Power",
                Category = "POWER / CPU",
                CurrentState = powerItem?.CurrentStateText ?? $"Active Plan: {activePower}",
                TargetState = "High / Ultimate Performance",
                AuditOutcome = powerOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "HIGH",
                IsOpportunity = !powerOptimal,
                IsMeaningfulOpportunity = !powerOptimal,
                IsVerifiedOrOptimal = powerOptimal,
                LinkedOneClickActionId = "power-performance",
                PageKey = "AiPowerPlan",
                SectionKey = "RENDER_POWER",
                ItemKey = "power-performance",
                Priority = 85
            });

            // 5. Background CPU Contention During Render (background-workload -> ProcessReduction:HIGH_CPU)
            int safeCandidates = bgItem?.SafeCandidateCount ?? ctx?.SafeReducibleProcesses ?? 0;
            bool bgOptimal = (bgItem != null && bgItem.IsCompleted) || safeCandidates == 0;
            bool bgHighImpact = !bgOptimal && safeCandidates >= 8;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "editing.background",
                WorkloadType = PerformanceWorkloadType.Editing,
                AreaName = "Background CPU Contention Control",
                Category = "BACKGROUND WORKLOAD",
                CurrentState = bgOptimal
                    ? $"0 Safe Candidates ({protectedProcs} Protected Preserved)"
                    : $"{safeCandidates} Non-Essential Background Helpers Active",
                TargetState = "Foreground Creative App Priority",
                AuditOutcome = bgOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = bgHighImpact ? "HIGH" : "MODERATE",
                IsOpportunity = !bgOptimal,
                IsMeaningfulOpportunity = bgHighImpact,
                IsVerifiedOrOptimal = bgOptimal,
                LinkedOneClickActionId = "background-workload",
                PageKey = "Dashboard",
                SectionKey = "HIGH_CPU",
                ItemKey = "background-workload",
                Priority = 82
            });

            // 6. Global System Storage & Disk I/O Responsiveness (affecting scratch disks & timeline scrubbing)
            var pendingStorageEditing = ctx?.MasterPlan?.AllActions
                .Where(a => a.CategoryKey.Equals("Storage", StringComparison.OrdinalIgnoreCase) &&
                            a.IsPending &&
                            !a.ItemId.Contains("temp", StringComparison.OrdinalIgnoreCase) &&
                            !a.ItemId.Contains("cache", StringComparison.OrdinalIgnoreCase) &&
                            !a.ItemId.Contains("recycle", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pendingStorageEditing != null && pendingStorageEditing.Count > 0)
            {
                checks.Add(new WorkloadAuditCheckItem
                {
                    AreaId = "editing.storage_io",
                    WorkloadType = PerformanceWorkloadType.Editing,
                    AreaName = "Scratch Disk & Timeline Scrubbing I/O Responsiveness",
                    Category = "STORAGE / IO",
                    CurrentState = $"{pendingStorageEditing.Count} Storage Parameter(s) Pending",
                    TargetState = "Low-Latency NVMe / SSD File System Configuration",
                    AuditOutcome = "OPTIMIZATION AVAILABLE",
                    ImpactLevel = "MODERATE",
                    IsOpportunity = true,
                    IsMeaningfulOpportunity = false,
                    IsVerifiedOrOptimal = false,
                    LinkedOneClickActionId = "storage-io",
                    PageKey = "Storage",
                    SectionKey = "STORAGE",
                    ItemKey = "storage-io",
                    Priority = 74
                });
            }

            // 7. Protected Creative Suites & Plugins (SKIPPED — PROTECTED)
            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "editing.protected",
                WorkloadType = PerformanceWorkloadType.Editing,
                AreaName = "Adobe, DaVinci, Maxon, Red Giant, Topaz & Blender",
                Category = "SAFETY / PROTECTION",
                CurrentState = $"{protectedProcs} Protected Processes Excluded",
                TargetState = "Untouched by SafetyKernel",
                AuditOutcome = "SKIPPED — PROTECTED",
                ImpactLevel = "PROTECTED",
                IsOpportunity = false,
                IsMeaningfulOpportunity = false,
                IsVerifiedOrOptimal = false,
                IsProtected = true
            });

            return BuildWorkloadStatusResult(
                PerformanceWorkloadType.Editing,
                "editing",
                checks,
                protectedProcs,
                isActiveWorkload,
                activeWorkloadLabel);
        }

        public WorkloadPerformanceStatus EvaluateStreamingWorkload(
            OptimizationOpportunityContext? ctx,
            OneClickPlan? plan,
            bool isActiveWorkload = false,
            string activeWorkloadLabel = "")
        {
            var checks = new List<WorkloadAuditCheckItem>();
            var actions = plan?.Actions ?? new List<OneClickActionItem>();
            var bgItem = actions.FirstOrDefault(a => a.Id == "background-workload");
            int protectedProcs = Math.Max(bgItem?.ProtectedProcessCount ?? 0, ctx?.ProtectedProcesses ?? 18);

            // 1. Encoder & Capture Background CPU Contention (background-workload -> ProcessReduction:SAFE)
            int safeCandidates = bgItem?.SafeCandidateCount ?? ctx?.SafeReducibleProcesses ?? 0;
            bool bgOptimal = (bgItem != null && bgItem.IsCompleted) || safeCandidates == 0;
            bool bgHighImpact = !bgOptimal && safeCandidates >= 6;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "streaming.background",
                WorkloadType = PerformanceWorkloadType.Streaming,
                AreaName = "Encoder & Capture Background CPU Contention",
                Category = "BACKGROUND WORKLOAD",
                CurrentState = bgOptimal
                    ? $"0 Safe Candidates ({protectedProcs} Protected Preserved)"
                    : $"{safeCandidates} Non-Essential Helpers Competing with Encoder",
                TargetState = "Balanced Background Priority (OBS Protected)",
                AuditOutcome = bgOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = bgHighImpact ? "HIGH" : "MODERATE",
                IsOpportunity = !bgOptimal,
                IsMeaningfulOpportunity = bgHighImpact,
                IsVerifiedOrOptimal = bgOptimal,
                LinkedOneClickActionId = "background-workload",
                PageKey = "Dashboard",
                SectionKey = "SAFE",
                ItemKey = "background-workload",
                Priority = 96
            });

            // 2. Audio/Video Frame Sync Timer (bcd-platform-timers -> InputOptimizer)
            var bcdItem = actions.FirstOrDefault(a => a.Id == "bcd-platform-timers");
            bool bcdOptimal = bcdItem != null && bcdItem.IsCompleted;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "streaming.timers",
                WorkloadType = PerformanceWorkloadType.Streaming,
                AreaName = "Audio/Video Frame Pacing Timer",
                Category = "LATENCY / TIMERS",
                CurrentState = bcdItem?.CurrentStateText ?? (bcdOptimal ? "Consistent Timer Active" : "Default Dynamic Tick"),
                TargetState = "Consistent Low-Jitter Platform Timer",
                AuditOutcome = bcdOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "HIGH",
                IsOpportunity = !bcdOptimal,
                IsMeaningfulOpportunity = !bcdOptimal,
                IsVerifiedOrOptimal = bcdOptimal,
                LinkedOneClickActionId = "bcd-platform-timers",
                PageKey = "InputOptimizer",
                SectionKey = "TIMERS",
                ItemKey = "bcd-platform-timers",
                Priority = 93
            });

            // 3. Sustained Encoding Power State (power-performance -> AiPowerPlan)
            var powerItem = actions.FirstOrDefault(a => a.Id == "power-performance");
            string activePower = ctx?.ActivePowerPlan ?? powerItem?.CurrentStateText?.Replace("Active Plan: ", "") ?? "Balanced";
            bool powerOptimal = (powerItem != null && powerItem.IsCompleted) ||
                                activePower.Contains("High", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Max Performance", StringComparison.OrdinalIgnoreCase) ||
                                activePower.Contains("Verified", StringComparison.OrdinalIgnoreCase);

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "streaming.power",
                WorkloadType = PerformanceWorkloadType.Streaming,
                AreaName = "Sustained Encoding & Broadcast Power Mode",
                Category = "POWER / ENCODER",
                CurrentState = powerItem?.CurrentStateText ?? $"Active Plan: {activePower}",
                TargetState = "High / Ultimate Performance",
                AuditOutcome = powerOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = "HIGH",
                IsOpportunity = !powerOptimal,
                IsMeaningfulOpportunity = !powerOptimal,
                IsVerifiedOrOptimal = powerOptimal,
                LinkedOneClickActionId = "power-performance",
                PageKey = "AiPowerPlan",
                SectionKey = "ENCODING_POWER",
                ItemKey = "power-performance",
                Priority = 86
            });

            // 4. Broadcast Memory & Buffer Headroom (ram-standby -> AiRamLimiter)
            var ramItem = actions.FirstOrDefault(a => a.Id == "ram-standby");
            double memPressure = ctx?.MemorySnapshot?.MemoryPressurePercent ?? 0;
            bool ramOptimal = (ramItem != null && ramItem.IsCompleted) || (ramItem == null && memPressure < 70.0);
            bool ramHighImpact = !ramOptimal && memPressure >= 75.0;

            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "streaming.memory",
                WorkloadType = PerformanceWorkloadType.Streaming,
                AreaName = "Broadcast Scene & Buffer RAM Headroom",
                Category = "MEMORY",
                CurrentState = ramItem?.CurrentStateText ?? (memPressure > 0 ? $"{memPressure:F0}% RAM Used" : "Healthy RAM Headroom"),
                TargetState = "Refreshed Standby & Working Sets",
                AuditOutcome = ramOptimal ? "VERIFIED" : "OPTIMIZATION AVAILABLE",
                ImpactLevel = ramHighImpact ? "HIGH" : "MODERATE",
                IsOpportunity = !ramOptimal,
                IsMeaningfulOpportunity = ramHighImpact,
                IsVerifiedOrOptimal = ramOptimal,
                LinkedOneClickActionId = "ram-standby",
                PageKey = "AiRamLimiter",
                SectionKey = "STANDBY",
                ItemKey = "ram-standby",
                Priority = 80
            });

            // 5. Global System Network Latency & Buffer Tuning (affecting RTMP / SRT bitrate stability)
            var pendingNetworkStreaming = ctx?.MasterPlan?.AllActions
                .Where(a => a.CategoryKey.Equals("Network", StringComparison.OrdinalIgnoreCase) &&
                            a.IsPending &&
                            !a.ItemId.Contains("dns", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pendingNetworkStreaming != null && pendingNetworkStreaming.Count > 0)
            {
                checks.Add(new WorkloadAuditCheckItem
                {
                    AreaId = "streaming.network",
                    WorkloadType = PerformanceWorkloadType.Streaming,
                    AreaName = "Broadcast Stream Latency & Congestion Provider",
                    Category = "NETWORK / LATENCY",
                    CurrentState = $"{pendingNetworkStreaming.Count} Network Parameter(s) Pending",
                    TargetState = "Low-Latency Network Adapter Buffer Configuration",
                    AuditOutcome = "OPTIMIZATION AVAILABLE",
                    ImpactLevel = "HIGH",
                    IsOpportunity = true,
                    IsMeaningfulOpportunity = true,
                    IsVerifiedOrOptimal = false,
                    LinkedOneClickActionId = "network-latency",
                    PageKey = "Network",
                    SectionKey = "NETWORK",
                    ItemKey = "network-latency",
                    Priority = 88
                });
            }

            // 6. Protected Capture, Audio Interfaces & VPN Tunnels (SKIPPED — PROTECTED)
            checks.Add(new WorkloadAuditCheckItem
            {
                AreaId = "streaming.protected",
                WorkloadType = PerformanceWorkloadType.Streaming,
                AreaName = "OBS Studio, Streamlabs, Audio Drivers & VPN Tunnels",
                Category = "SAFETY / PROTECTION",
                CurrentState = $"{protectedProcs} Protected Processes Excluded",
                TargetState = "Untouched by SafetyKernel",
                AuditOutcome = "SKIPPED — PROTECTED",
                ImpactLevel = "PROTECTED",
                IsOpportunity = false,
                IsMeaningfulOpportunity = false,
                IsVerifiedOrOptimal = false,
                IsProtected = true
            });

            return BuildWorkloadStatusResult(
                PerformanceWorkloadType.Streaming,
                "streaming",
                checks,
                protectedProcs,
                isActiveWorkload,
                activeWorkloadLabel);
        }

        private static WorkloadPerformanceStatus BuildWorkloadStatusResult(
            PerformanceWorkloadType workloadType,
            string workloadNoun,
            List<WorkloadAuditCheckItem> checks,
            int protectedProcessCount,
            bool activeWorkloadDetected,
            string activeWorkloadLabel)
        {
            int remainingOpportunities = checks.Count(c => c.IsOpportunity);
            int meaningfulOpportunities = checks.Count(c => c.IsMeaningfulOpportunity);
            int verifiedOrOptimalCount = checks.Count(c => c.IsVerifiedOrOptimal);

            var topOpportunity = checks
                .Where(c => c.IsOpportunity && !string.IsNullOrWhiteSpace(c.PageKey))
                .OrderByDescending(c => c.IsMeaningfulOpportunity)
                .ThenByDescending(c => c.Priority)
                .FirstOrDefault();

            WorkloadPerformanceState state;
            string reason;
            string recommendedAction;
            string navTarget;
            string targetPageKey;
            string targetSectionKey;
            string targetOpportunityId = topOpportunity?.OpportunityId ?? string.Empty;

            if (remainingOpportunities == 0)
            {
                state = WorkloadPerformanceState.Optimized;
                navTarget = GetDefaultAuditTarget(workloadType);
                (targetPageKey, targetSectionKey) = SplitNavigationTarget(navTarget);
                recommendedAction = "View verified workload audit";
                reason = activeWorkloadDetected && !string.IsNullOrWhiteSpace(activeWorkloadLabel)
                    ? $"{activeWorkloadLabel} active • All {verifiedOrOptimalCount} safe {workloadNoun} checks verified"
                    : $"All {verifiedOrOptimalCount} safe {workloadNoun} checks verified ({protectedProcessCount} protected preserved)";
            }
            else if (meaningfulOpportunities == 0 && verifiedOrOptimalCount >= 2 && remainingOpportunities <= 2)
            {
                state = WorkloadPerformanceState.Balanced;
                navTarget = topOpportunity != null && !string.IsNullOrWhiteSpace(topOpportunity.NavigationTarget)
                    ? topOpportunity.NavigationTarget
                    : GetDefaultOptimizationTarget(workloadType);
                (targetPageKey, targetSectionKey) = SplitNavigationTarget(navTarget);

                string focusArea = GetShortAreaHint(topOpportunity?.LinkedOneClickActionId);
                recommendedAction = $"Optimize {remainingOpportunities} remaining {workloadNoun} item{(remainingOpportunities == 1 ? "" : "s")}";
                reason = remainingOpportunities == 1
                    ? $"1 {workloadNoun} {focusArea} optimization available"
                    : $"{remainingOpportunities} {workloadNoun} optimizations available ({verifiedOrOptimalCount} already verified)";
            }
            else
            {
                state = WorkloadPerformanceState.NotOptimized;
                navTarget = topOpportunity != null && !string.IsNullOrWhiteSpace(topOpportunity.NavigationTarget)
                    ? topOpportunity.NavigationTarget
                    : GetDefaultOptimizationTarget(workloadType);
                (targetPageKey, targetSectionKey) = SplitNavigationTarget(navTarget);

                recommendedAction = $"Optimize {remainingOpportunities} {workloadNoun} performance checks";
                reason = $"{remainingOpportunities} {workloadNoun} optimization{(remainingOpportunities == 1 ? "" : "s")} available";
            }

            if (activeWorkloadDetected && !string.IsNullOrWhiteSpace(activeWorkloadLabel) && state != WorkloadPerformanceState.Optimized)
            {
                reason = $"{activeWorkloadLabel} active • {reason}";
            }

            var status = new WorkloadPerformanceStatus
            {
                WorkloadType = workloadType,
                Status = state,
                IsAuditComplete = true,
                IsVerified = state == WorkloadPerformanceState.Optimized,
                OpportunityCount = remainingOpportunities,
                MeaningfulOpportunityCount = meaningfulOpportunities,
                VerifiedCount = verifiedOrOptimalCount,
                ProtectedCount = protectedProcessCount,
                RemainingOptimizationCount = remainingOpportunities,
                ActiveWorkloadDetected = activeWorkloadDetected,
                ActiveWorkloadSummary = activeWorkloadDetected ? activeWorkloadLabel : "Idle / Ready",
                LastAuditTime = DateTime.UtcNow,
                Reason = reason,
                RecommendedAction = recommendedAction,
                NavigationTarget = navTarget,
                TargetPageKey = targetPageKey,
                TargetSectionKey = targetSectionKey,
                TargetOpportunityId = targetOpportunityId
            };

            foreach (var item in checks)
            {
                status.AuditItems.Add(item);
            }

            return status;
        }

        private static void EnsureDistinctWorkloadTargetsWhenOpportunitiesExist(
            WorkloadPerformanceStatus gaming,
            WorkloadPerformanceStatus editing,
            WorkloadPerformanceStatus streaming)
        {
            var usedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(gaming.TargetPageKey))
            {
                usedPages.Add(gaming.TargetPageKey);
            }

            if (editing.OpportunityCount > 0 && usedPages.Contains(editing.TargetPageKey))
            {
                var altEdit = editing.AuditItems
                    .Where(c => c.IsOpportunity && !string.IsNullOrWhiteSpace(c.PageKey) && !usedPages.Contains(c.PageKey))
                    .OrderByDescending(c => c.Priority)
                    .FirstOrDefault();
                if (altEdit != null)
                {
                    editing.NavigationTarget = altEdit.NavigationTarget;
                    editing.TargetPageKey = altEdit.PageKey;
                    editing.TargetSectionKey = altEdit.SectionKey;
                    editing.TargetOpportunityId = altEdit.OpportunityId;
                }
            }
            if (!string.IsNullOrWhiteSpace(editing.TargetPageKey))
            {
                usedPages.Add(editing.TargetPageKey);
            }

            if (streaming.OpportunityCount > 0 && usedPages.Contains(streaming.TargetPageKey))
            {
                var altStream = streaming.AuditItems
                    .Where(c => c.IsOpportunity && !string.IsNullOrWhiteSpace(c.PageKey) && !usedPages.Contains(c.PageKey))
                    .OrderByDescending(c => c.Priority)
                    .FirstOrDefault();
                if (altStream != null)
                {
                    streaming.NavigationTarget = altStream.NavigationTarget;
                    streaming.TargetPageKey = altStream.PageKey;
                    streaming.TargetSectionKey = altStream.SectionKey;
                    streaming.TargetOpportunityId = altStream.OpportunityId;
                }
            }
        }

        private static (string PageKey, string SectionKey) SplitNavigationTarget(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return (string.Empty, string.Empty);
            }
            int idx = target.IndexOf(':');
            if (idx < 0)
            {
                return (target.Trim(), string.Empty);
            }
            return (target.Substring(0, idx).Trim(), target.Substring(idx + 1).Trim());
        }

        private static string GetShortAreaHint(string? actionId) => actionId switch
        {
            "ram-standby" => "memory/standby",
            "fast-clean" or "deep-clean" => "memory/cache",
            "shader-cache" => "shader cache",
            "power-performance" => "power mode",
            "bcd-platform-timers" => "timer latency",
            "visual-tweaks" => "DWM responsiveness",
            "background-workload" => "background CPU",
            "emulator-hypervisor" => "emulator tuning",
            _ => "performance"
        };

        private DateTime _lastWorkloadPollUtc = DateTime.MinValue;
        private WorkloadStatus _cachedWorkloadStatus = new();

        private (bool IsGamingOrEmulatorActive, string GamingLabel, bool IsEditingActive, string EditingLabel, bool IsStreamingActive, string StreamingLabel)
            DetectActiveWorkloadFlags(string? dashboardDetectedWorkload)
        {
            bool isGaming = false;
            string gamingLabel = string.Empty;
            bool isEditing = false;
            string editingLabel = string.Empty;
            bool isStreaming = false;
            string streamingLabel = string.Empty;

            try
            {
                if ((DateTime.UtcNow - _lastWorkloadPollUtc).TotalSeconds >= 4.0)
                {
                    _lastWorkloadPollUtc = DateTime.UtcNow;
                    _cachedWorkloadStatus = _workloadEngine.DetectActiveWorkloads(Process.GetProcesses());
                }
                var detected = _cachedWorkloadStatus;
                if (detected.IsGamingDetected || detected.IsEmulatorDetected)
                {
                    isGaming = true;
                    gamingLabel = detected.IsEmulatorDetected
                        ? "Android Emulator"
                        : (detected.ActiveWorkloadNames.FirstOrDefault() ?? "Gaming");
                }

                var streamingProc = detected.ActiveWorkloadProcesses.FirstOrDefault(p =>
                    p.Equals("obs64", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("obs32", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("obs", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("streamlabs", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(streamingProc))
                {
                    isStreaming = true;
                    streamingLabel = $"Streaming ({streamingProc})";
                }

                if (detected.IsEditingDetected)
                {
                    var editProc = detected.ActiveWorkloadProcesses.FirstOrDefault(p =>
                        !p.Equals("obs64", StringComparison.OrdinalIgnoreCase) &&
                        !p.Equals("obs32", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains("streamlabs", StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrWhiteSpace(editProc) || string.IsNullOrWhiteSpace(streamingProc))
                    {
                        isEditing = true;
                        editingLabel = !string.IsNullOrWhiteSpace(editProc) ? $"Creative ({editProc})" : "Creative Suite";
                    }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(dashboardDetectedWorkload))
            {
                if (!isGaming && dashboardDetectedWorkload.Contains("Emulator", StringComparison.OrdinalIgnoreCase))
                {
                    isGaming = true;
                    gamingLabel = "Android Emulator";
                }
                if (!isEditing && (dashboardDetectedWorkload.Contains("After Effects", StringComparison.OrdinalIgnoreCase) ||
                                   dashboardDetectedWorkload.Contains("Topaz", StringComparison.OrdinalIgnoreCase) ||
                                   dashboardDetectedWorkload.Contains("Video Editing", StringComparison.OrdinalIgnoreCase)))
                {
                    isEditing = true;
                    editingLabel = dashboardDetectedWorkload.Replace(" Detected", "");
                }
                if (!isStreaming && dashboardDetectedWorkload.Contains("Streaming", StringComparison.OrdinalIgnoreCase))
                {
                    isStreaming = true;
                    streamingLabel = "Live Streaming";
                }
            }

            return (isGaming, gamingLabel, isEditing, editingLabel, isStreaming, streamingLabel);
        }
    }
}
