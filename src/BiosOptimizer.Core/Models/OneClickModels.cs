#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BiosOptimizer.Core.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OneClickActionCategory
    {
        Cache,
        Memory,
        Cleanup,
        Visual,
        HardwareTimers,
        SystemPerformance,
        BackgroundWorkload,
        PowerPerformance
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OneClickActionStatus
    {
        Available,
        Recommended,
        AlreadyCompleted,
        RequiresAdmin,
        RequiresRestart,
        NotApplicable,
        Scanning,
        Applying,
        Verifying,
        Verified,
        Failed,
        Skipped
    }

    public class OneClickActionItem : INotifyPropertyChanged
    {
        private OneClickActionStatus _status = OneClickActionStatus.Available;
        private string _currentStateText = "Unknown";
        private string _targetStateText = "Optimal";
        private string _metricText = "";
        private string _resultText = "Ready to optimize";
        private bool _isSelected = true;
        private bool _isAdvancedDetailsExpanded = false;

        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name => Title;

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("whatItDoes")]
        public string WhatItDoes { get; set; } = "";

        [JsonPropertyName("whyItMatters")]
        public string WhyItMatters { get; set; } = "";

        [JsonPropertyName("safetySummary")]
        public string SafetySummary { get; set; } = "Safe • Reversible";

        [JsonPropertyName("category")]
        public OneClickActionCategory Category { get; set; }

        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = "SYSTEM PERFORMANCE";

        [JsonPropertyName("categoryName")]
        public string CategoryName => Category switch
        {
            OneClickActionCategory.Cache => "SYSTEM PERFORMANCE",
            OneClickActionCategory.Memory => "MEMORY",
            OneClickActionCategory.Cleanup => "SYSTEM PERFORMANCE",
            OneClickActionCategory.Visual => "SYSTEM PERFORMANCE",
            OneClickActionCategory.HardwareTimers => "SYSTEM PERFORMANCE",
            OneClickActionCategory.SystemPerformance => "SYSTEM PERFORMANCE",
            OneClickActionCategory.BackgroundWorkload => "BACKGROUND WORKLOAD",
            OneClickActionCategory.PowerPerformance => "POWER / PERFORMANCE",
            _ => "SYSTEM PERFORMANCE"
        };

        [JsonPropertyName("status")]
        public OneClickActionStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(CanOptimizeDirectly));
                    OnPropertyChanged(nameof(ActionButtonText));
                    OnPropertyChanged(nameof(StatusBadgeText));
                    OnPropertyChanged(nameof(StatusBrushKey));
                }
            }
        }

        [JsonPropertyName("currentStateText")]
        public string CurrentStateText
        {
            get => _currentStateText;
            set { if (_currentStateText != value) { _currentStateText = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("targetStateText")]
        public string TargetStateText
        {
            get => _targetStateText;
            set { if (_targetStateText != value) { _targetStateText = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("metricText")]
        public string MetricText
        {
            get => _metricText;
            set { if (_metricText != value) { _metricText = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("resultText")]
        public string ResultText
        {
            get => _resultText;
            set { if (_resultText != value) { _resultText = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("isSelected")]
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("canonicalStatus")]
        public string CanonicalStatus => Status switch
        {
            OneClickActionStatus.AlreadyCompleted => "ALREADY_OPTIMIZED",
            OneClickActionStatus.Verified => "VERIFIED",
            OneClickActionStatus.Failed => "FAILED",
            OneClickActionStatus.Skipped => "PROTECTED",
            OneClickActionStatus.NotApplicable => "NOT_APPLICABLE",
            _ => "UNVERIFIED"
        };

        [JsonPropertyName("alreadyOptimized")]
        public bool AlreadyOptimized => Status == OneClickActionStatus.AlreadyCompleted;

        [JsonPropertyName("executed")]
        public bool Executed => Status == OneClickActionStatus.Verified || Status == OneClickActionStatus.Failed;

        [JsonPropertyName("ok")]
        public bool Ok => Status == OneClickActionStatus.Verified || Status == OneClickActionStatus.AlreadyCompleted;

        [JsonPropertyName("verified")]
        public bool Verified => Status == OneClickActionStatus.Verified;

        [JsonPropertyName("reason")]
        public string Reason => !string.IsNullOrWhiteSpace(ResultText) ? ResultText : (Status == OneClickActionStatus.AlreadyCompleted ? "Already in desired optimal state" : "Completed");

        [JsonPropertyName("verification")]
        public string Verification => Status == OneClickActionStatus.Verified ? ResultText : (Status == OneClickActionStatus.AlreadyCompleted ? "Verified • Already optimal" : "");

        [JsonPropertyName("detail")]
        public string Detail => Status == OneClickActionStatus.AlreadyCompleted ? "ALREADY_OPTIMIZED" : (Status == OneClickActionStatus.Verified ? "VERIFIED" : ResultText);

        [JsonPropertyName("retryable")]
        public bool Retryable => Status == OneClickActionStatus.Failed;

        public bool IsAdvancedDetailsExpanded
        {
            get => _isAdvancedDetailsExpanded;
            set
            {
                if (_isAdvancedDetailsExpanded != value)
                {
                    _isAdvancedDetailsExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AdvancedDetailsToggleText));
                }
            }
        }

        public string AdvancedDetailsToggleText => _isAdvancedDetailsExpanded
            ? "▴ Hide Advanced Details"
            : "▾ Advanced Details";

        public bool RequiresAdmin { get; set; } = false;
        public bool RequiresRestart { get; set; } = false;
        public string RiskLevel { get; set; } = "SAFE";
        public string UnderlyingEngine { get; set; } = "OneClickOptimizationEngine";
        public string ExecutionDetail { get; set; } = "";

        // Expandable Advanced Details fields (Section 23)
        public string TechnicalImplementation { get; set; } = "";
        public string BackendCommand { get; set; } = "";
        public string RestartRequirementText => RequiresRestart
            ? "System restart recommended for full kernel/boot effect"
            : "No restart required (Immediate effect)";
        public string VerificationMethod { get; set; } = "";
        public string RollbackMethod { get; set; } = "";

        public int ProtectedProcessCount { get; set; }
        public int SafeCandidateCount { get; set; }
        public List<string> ApplicableWorkloads { get; set; } = new() { "GAMING", "EDITING", "STREAMING" };
        public string WorkloadTagsText => ApplicableWorkloads != null && ApplicableWorkloads.Count > 0
            ? string.Join(" • ", ApplicableWorkloads)
            : "ALL WORKLOADS";

        public bool IsApplicableToWorkload(string workloadKey)
        {
            if (string.IsNullOrWhiteSpace(workloadKey) || workloadKey.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                return true;
            return ApplicableWorkloads != null &&
                   ApplicableWorkloads.Any(w => w.Equals(workloadKey, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsCompleted => Status == OneClickActionStatus.AlreadyCompleted || Status == OneClickActionStatus.Verified;
        public bool IsAlreadyOptimized => IsCompleted;
        public bool IsProtected => Status == OneClickActionStatus.Skipped || string.Equals(RiskLevel, "PROTECTED", StringComparison.OrdinalIgnoreCase);
        public bool IsSelectable => !IsAlreadyOptimized && !IsProtected && Status != OneClickActionStatus.NotApplicable;
        public bool IsAutoSchedulable => !RequiresRestart && !RequiresAdmin && !IsProtected && !IsAlreadyOptimized && string.Equals(RiskLevel, "SAFE", StringComparison.OrdinalIgnoreCase);

        public bool CanOptimizeDirectly => !IsCompleted && Status != OneClickActionStatus.NotApplicable && Status != OneClickActionStatus.Applying;

        public string RecommendationLevelText => Status switch
        {
            OneClickActionStatus.Recommended => "RECOMMENDED",
            OneClickActionStatus.AlreadyCompleted or OneClickActionStatus.Verified => "ALREADY OPTIMAL",
            OneClickActionStatus.Skipped => "PROTECTED",
            OneClickActionStatus.NotApplicable => "NOT APPLICABLE",
            _ => "OPTIONAL"
        };

        public string ActionButtonText => Status switch
        {
            OneClickActionStatus.Applying => "APPLYING...",
            OneClickActionStatus.Verifying => "VERIFYING...",
            OneClickActionStatus.AlreadyCompleted or OneClickActionStatus.Verified => "VERIFIED",
            OneClickActionStatus.Failed => "RETRY",
            OneClickActionStatus.NotApplicable => "NOT AVAILABLE",
            _ => "OPTIMIZE"
        };

        public string StatusBadgeText => Status switch
        {
            OneClickActionStatus.AlreadyCompleted or OneClickActionStatus.Verified => "✓ OPTIMIZED",
            OneClickActionStatus.Recommended => "RECOMMENDED",
            OneClickActionStatus.Available => "OPTIONAL",
            OneClickActionStatus.Applying => "APPLYING...",
            OneClickActionStatus.Verifying => "VERIFYING...",
            OneClickActionStatus.Failed => "FAILED",
            OneClickActionStatus.Skipped => "🔒 PROTECTED",
            OneClickActionStatus.NotApplicable => "NOT APPLICABLE",
            _ => "READY"
        };

        public string StatusBrushKey => Status switch
        {
            OneClickActionStatus.AlreadyCompleted or OneClickActionStatus.Verified => "SuccessBrush",
            OneClickActionStatus.Recommended => "AccentBrush",
            OneClickActionStatus.Applying or OneClickActionStatus.Verifying => "AccentHoverBrush",
            OneClickActionStatus.Failed => "DangerBrush",
            OneClickActionStatus.Skipped => "TextMutedBrush",
            _ => "SecondaryTextBrush"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AuditedExistingFeature
    {
        public string FeatureName { get; set; } = "";
        public string HandledByModule { get; set; } = "";
        public string AuditStatus { get; set; } = "ALREADY EXISTS (NOT DUPLICATED)";
        public string Description { get; set; } = "";
    }

    public class OneClickPlan
    {
        [JsonPropertyName("actions")]
        public List<OneClickActionItem> Actions { get; set; } = new();

        [JsonPropertyName("existingAuditedFeatures")]
        public List<AuditedExistingFeature> ExistingAuditedFeatures { get; set; } = new();

        [JsonPropertyName("excludedRepairFeatures")]
        public List<string> ExcludedRepairFeatures { get; set; } = new();

        [JsonPropertyName("totalFound")]
        public int TotalFound => Actions.Count;

        [JsonPropertyName("applicableCount")]
        public int ApplicableCount { get; set; }

        [JsonPropertyName("recommendedCount")]
        public int RecommendedCount { get; set; }

        [JsonPropertyName("alreadyDoneCount")]
        public int AlreadyDoneCount { get; set; }

        [JsonPropertyName("notAvailableCount")]
        public int NotAvailableCount { get; set; }

        [JsonPropertyName("manualCount")]
        public int ManualCount { get; set; }

        [JsonPropertyName("protectedItemsCount")]
        public int ProtectedItemsCount { get; set; }

        [JsonPropertyName("totalProcessCount")]
        public int TotalProcessCount { get; set; }

        [JsonPropertyName("activePowerPlanName")]
        public string ActivePowerPlanName { get; set; } = "Balanced";

        [JsonPropertyName("estimatedBytesRecoverable")]
        public long EstimatedBytesRecoverable { get; set; }

        [JsonPropertyName("generatedAt")]
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }

    public class OneClickExecutionResult
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "optimize:result";

        [JsonPropertyName("moduleId")]
        public string ModuleId { get; set; } = "1-Click";

        [JsonPropertyName("moduleTitle")]
        public string ModuleTitle { get; set; } = "1-Click Optimization";

        [JsonPropertyName("success")]
        public bool Success => OverallSuccess;

        [JsonPropertyName("overallSuccess")]
        public bool OverallSuccess { get; set; }

        [JsonPropertyName("totalProcessed")]
        public int TotalProcessed { get; set; }

        [JsonPropertyName("appliedCount")]
        public int AppliedCount { get; set; }

        [JsonPropertyName("verifiedCount")]
        public int VerifiedCount { get; set; }

        [JsonPropertyName("alreadyOptimizedCount")]
        public int AlreadyOptimizedCount { get; set; }

        [JsonPropertyName("protectedCount")]
        public int ProtectedCount { get; set; }

        [JsonPropertyName("skippedCount")]
        public int SkippedCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }

        [JsonPropertyName("remainingCount")]
        public int RemainingCount { get; set; }

        [JsonPropertyName("manualCount")]
        public int ManualCount { get; set; }

        [JsonPropertyName("bytesRecovered")]
        public long BytesRecovered { get; set; }

        [JsonPropertyName("summaryMessage")]
        public string SummaryMessage { get; set; } = "";

        [JsonPropertyName("executionLogs")]
        public List<string> ExecutionLogs { get; set; } = new();

        [JsonPropertyName("completedActions")]
        public List<OneClickActionItem> CompletedActions { get; set; } = new();

        [JsonPropertyName("operations")]
        public List<OneClickActionItem> Operations => CompletedActions;
    }
}
