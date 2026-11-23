#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.Services;
using BiosOptimizer.Core.Implementations.Debloat;
using BiosOptimizer.Core.V4.Architecture;
using BiosOptimizer.Core.V4.Recommendations;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations
{
    // =========================================================================
    // SECTION 3: UNIVERSAL OPERATION STATES (MANDATORY 4-TIER CONTRACT)
    // =========================================================================

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EligibilityState
    {
        ELIGIBLE,
        INELIGIBLE,
        UNSUPPORTED_HARDWARE,
        UNSUPPORTED_OS,
        DRIVER_REQUIREMENT_NOT_MET,
        PERMISSION_REQUIRED,
        DEPENDENCY_NOT_MET,
        CONFLICT_DETECTED,
        PROTECTED_BY_POLICY,
        ALREADY_OPTIMAL,
        ALREADY_OPTIMIZED,
        RECOMMENDATION_ONLY,
        DEFERRED,
        FAILED_VALIDATION,
        REQUIRES_ELEVATION,
        UNSUPPORTED,
        TEMPORARILY_UNAVAILABLE,
        UNKNOWN,
        READ_ONLY_AUDIT,
        NOT_APPLICABLE
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SelectionState
    {
        SELECTED,
        NOT_SELECTED,
        USER_EXCLUDED
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ExecutionState
    {
        NOT_STARTED,
        RUNNING,
        APPLIED,
        FAILED,
        VERIFIED,
        UNVERIFIED,
        ABORTED
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FinalResultState
    {
        APPLIED_AND_VERIFIED,
        ALREADY_OPTIMIZED,
        FAILED,
        PROTECTED,
        SKIPPED_BY_USER,
        DEFERRED,
        INELIGIBLE,
        UNSUPPORTED,
        REQUIRES_ELEVATION,
        UNVERIFIED,
        ABORTED,
        READ_ONLY_AUDIT
    }

    // =========================================================================
    // DATA TRANSFER OBJECTS FOR UNIVERSAL OPTIMIZATION
    // =========================================================================

    public class UniversalOptimizationItemDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("risk")]
        public string Risk { get; set; } = "Low"; // Safe, Low, Medium, High, Critical

        [JsonPropertyName("eligibilityState")]
        public EligibilityState EligibilityState { get; set; } = EligibilityState.UNKNOWN;

        [JsonPropertyName("selectionState")]
        public SelectionState SelectionState { get; set; } = SelectionState.NOT_SELECTED;

        [JsonPropertyName("executionState")]
        public ExecutionState ExecutionState { get; set; } = ExecutionState.NOT_STARTED;

        [JsonPropertyName("finalResult")]
        public FinalResultState FinalResult { get; set; } = FinalResultState.INELIGIBLE;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("currentValue")]
        public string CurrentValue { get; set; } = string.Empty;

        [JsonPropertyName("targetValue")]
        public string TargetValue { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("requiresAdmin")]
        public bool RequiresAdmin { get; set; }

        [JsonPropertyName("requiresRestart")]
        public bool RequiresRestart { get; set; }

        [JsonPropertyName("isReversible")]
        public bool IsReversible { get; set; } = true;

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "WINDOWS"; // WINDOWS, UEFI, HARDWARE, APP

        [JsonPropertyName("executionType")]
        public string ExecutionType { get; set; } = "WINDOWS_AUTOMATIC";

        [JsonPropertyName("isInherited")]
        public bool IsInherited { get; set; }

        [JsonPropertyName("title")]
        public string Title => Name;

        [JsonPropertyName("executed")]
        public bool Executed => ExecutionState == ExecutionState.VERIFIED || ExecutionState == ExecutionState.APPLIED || ExecutionState == ExecutionState.FAILED || ExecutionState == ExecutionState.UNVERIFIED;

        [JsonPropertyName("ok")]
        public bool Ok => ExecutionState == ExecutionState.VERIFIED || ExecutionState == ExecutionState.APPLIED || FinalResult == FinalResultState.APPLIED_AND_VERIFIED || FinalResult == FinalResultState.ALREADY_OPTIMIZED;

        [JsonPropertyName("verified")]
        public bool Verified => (ExecutionState == ExecutionState.VERIFIED || FinalResult == FinalResultState.APPLIED_AND_VERIFIED) && FinalResult != FinalResultState.ALREADY_OPTIMIZED;

        [JsonPropertyName("alreadyOptimized")]
        public bool AlreadyOptimized => FinalResult == FinalResultState.ALREADY_OPTIMIZED || EligibilityState == EligibilityState.ALREADY_OPTIMIZED;

        [JsonPropertyName("canonicalStatus")]
        public string CanonicalStatus =>
            FinalResult == FinalResultState.ALREADY_OPTIMIZED || EligibilityState == EligibilityState.ALREADY_OPTIMIZED
                ? "ALREADY_OPTIMIZED"
                : (ExecutionState == ExecutionState.VERIFIED && FinalResult == FinalResultState.APPLIED_AND_VERIFIED)
                    ? "VERIFIED"
                    : FinalResult == FinalResultState.REQUIRES_ELEVATION
                        ? "REQUIRES_ELEVATION"
                        : FinalResult == FinalResultState.UNSUPPORTED
                            ? "UNSUPPORTED"
                            : FinalResult == FinalResultState.READ_ONLY_AUDIT
                                ? "READ_ONLY_AUDIT"
                                : ExecutionState == ExecutionState.FAILED
                                    ? "FAILED"
                                    : ExecutionState == ExecutionState.UNVERIFIED
                                        ? "UNVERIFIED"
                                        : "SKIPPED";

        [JsonPropertyName("error")]
        public string? Error => ExecutionState == ExecutionState.FAILED ? (string.IsNullOrWhiteSpace(Reason) ? "Execution failed" : Reason) : null;

        [JsonPropertyName("verificationTimestamp")]
        public string VerificationTimestamp { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("isActionable")]
        public bool IsActionable => EligibilityState == EligibilityState.ELIGIBLE;

        [JsonPropertyName("subcategory")]
        public string Subcategory { get; set; } = string.Empty;

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();

        [JsonPropertyName("conflicts")]
        public List<string> Conflicts { get; set; } = new();

        [JsonPropertyName("supportedOs")]
        public string SupportedOs { get; set; } = "Windows 10 / 11";

        [JsonPropertyName("cpuRequirements")]
        public string CpuRequirements { get; set; } = "Universal";

        [JsonPropertyName("gpuRequirements")]
        public string GpuRequirements { get; set; } = "Universal";

        [JsonPropertyName("memoryRequirements")]
        public string MemoryRequirements { get; set; } = "Universal";

        [JsonPropertyName("storageRequirements")]
        public string StorageRequirements { get; set; } = "Universal";

        [JsonPropertyName("powerConditions")]
        public string PowerConditions { get; set; } = "Universal";

        [JsonPropertyName("workloadConditions")]
        public string WorkloadConditions { get; set; } = "Universal";

        [JsonPropertyName("scanFreshness")]
        public string ScanFreshness { get; set; } = "LIVE";

        [JsonPropertyName("verifyDetail")]
        public string? VerifyDetail => (ExecutionState == ExecutionState.VERIFIED && FinalResult == FinalResultState.APPLIED_AND_VERIFIED) ? Reason : null;

        [JsonPropertyName("retryable")]
        public bool Retryable =>
            ExecutionState == ExecutionState.FAILED &&
            !string.IsNullOrWhiteSpace(Reason) &&
            !Reason.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) &&
            !Reason.Contains("Administrator", StringComparison.OrdinalIgnoreCase) &&
            !Reason.Contains("privilege", StringComparison.OrdinalIgnoreCase) &&
            !Reason.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
            !Reason.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) &&
            !Reason.Contains("policy", StringComparison.OrdinalIgnoreCase);

        [JsonPropertyName("skippedReason")]
        public string? SkippedReason =>
            (FinalResult != FinalResultState.ALREADY_OPTIMIZED &&
             EligibilityState != EligibilityState.ALREADY_OPTIMIZED &&
             (ExecutionState == ExecutionState.NOT_STARTED ||
              FinalResult == FinalResultState.SKIPPED_BY_USER ||
              FinalResult == FinalResultState.INELIGIBLE ||
              FinalResult == FinalResultState.UNSUPPORTED ||
              FinalResult == FinalResultState.REQUIRES_ELEVATION ||
              FinalResult == FinalResultState.READ_ONLY_AUDIT))
                ? Reason
                : null;
    }

    public class UniversalOptimizationPlanDto
    {
        [JsonPropertyName("scanId")]
        public string ScanId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("catalogVersion")]
        public string CatalogVersion { get; set; } = "4.0.0";

        [JsonPropertyName("startedAt")]
        public string StartedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("completedAt")]
        public string CompletedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("systemFingerprint")]
        public string SystemFingerprint { get; set; } = string.Empty;

        [JsonPropertyName("moduleId")]
        public string ModuleId { get; set; } = string.Empty;

        [JsonPropertyName("moduleTitle")]
        public string ModuleTitle { get; set; } = string.Empty;

        [JsonPropertyName("totalCatalogCount")]
        public int TotalCatalogCount { get; set; }

        [JsonPropertyName("eligibleCount")]
        public int EligibleCount { get; set; }

        [JsonPropertyName("needsOptimizationCount")]
        public int NeedsOptimizationCount => EligibleCount;

        [JsonPropertyName("ineligibleCount")]
        public int IneligibleCount { get; set; }

        [JsonPropertyName("alreadyOptimizedCount")]
        public int AlreadyOptimizedCount { get; set; }

        [JsonPropertyName("deferredCount")]
        public int DeferredCount { get; set; }

        [JsonPropertyName("supportedEligibleCount")]
        public int SupportedEligibleCount => EligibleCount + AlreadyOptimizedCount;

        [JsonPropertyName("selectedCount")]
        public int SelectedCount { get; set; }

        [JsonPropertyName("userExcludedCount")]
        public int UserExcludedCount { get; set; }

        [JsonPropertyName("items")]
        public List<UniversalOptimizationItemDto> Items { get; set; } = new();

        [JsonPropertyName("deferredItems")]
        public List<UniversalOptimizationItemDto> DeferredItems { get; set; } = new();

        [JsonPropertyName("excludedItems")]
        public List<UniversalOptimizationItemDto> ExcludedItems { get; set; } = new();

        [JsonPropertyName("hardwareSummary")]
        public string HardwareSummary { get; set; } = string.Empty;

        [JsonPropertyName("canApply")]
        public bool CanApply => EligibleCount > 0 && SelectedCount > 0;
    }

    public class RollbackInfoDto
    {
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("restorePoint")]
        public string RestorePoint { get; set; } = string.Empty;

        [JsonPropertyName("transactionId")]
        public string? TransactionId { get; set; }
    }

    public class UniversalExecutionResultDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "optimize:result";

        [JsonPropertyName("moduleId")]
        public string ModuleId { get; set; } = string.Empty;

        [JsonPropertyName("moduleTitle")]
        public string ModuleTitle { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "SUCCESS"; // SUCCESS, PARTIAL, FAILED, NO_ELIGIBLE_OPERATIONS, ABORTED

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("totalCatalogCount")]
        public int TotalCatalogCount { get; set; }

        [JsonPropertyName("eligibleCount")]
        public int EligibleCount { get; set; }

        [JsonPropertyName("ineligibleCount")]
        public int IneligibleCount { get; set; }

        [JsonPropertyName("selectedCount")]
        public int SelectedCount { get; set; }

        [JsonPropertyName("excludedCount")]
        public int ExcludedCount { get; set; }

        [JsonPropertyName("attemptedCount")]
        public int AttemptedCount { get; set; }

        [JsonPropertyName("executionAttempts")]
        public int ExecutionAttempts { get; set; }

        [JsonPropertyName("appliedCount")]
        public int AppliedCount { get; set; }

        [JsonPropertyName("newlyAppliedCount")]
        public int NewlyAppliedCount => Math.Max(0, AppliedCount - AlreadyOptimizedCount);

        [JsonPropertyName("alreadyOptimizedCount")]
        public int AlreadyOptimizedCount { get; set; }

        [JsonPropertyName("alreadyOptimalCount")]
        public int AlreadyOptimalCount => AlreadyOptimizedCount;

        [JsonPropertyName("supportedEligibleCount")]
        public int SupportedEligibleCount => EligibleCount + AlreadyOptimizedCount;

        [JsonPropertyName("verifiedCount")]
        public int VerifiedCount { get; set; }

        [JsonPropertyName("protectedCount")]
        public int ProtectedCount { get; set; }

        [JsonPropertyName("unsupportedCount")]
        public int UnsupportedCount { get; set; }

        [JsonPropertyName("auditCount")]
        public int AuditCount { get; set; }

        [JsonPropertyName("failedCount")]
        public int FailedCount { get; set; }

        [JsonPropertyName("failedAttemptedCount")]
        public int FailedAttemptedCount => FailedCount;

        [JsonPropertyName("unverifiedCount")]
        public int UnverifiedCount { get; set; }

        [JsonPropertyName("abortedCount")]
        public int AbortedCount { get; set; }

        [JsonPropertyName("abortedBeforeExecutionCount")]
        public int AbortedBeforeExecutionCount { get; set; }

        [JsonPropertyName("notExecutedCount")]
        public int NotExecutedCount { get; set; }

        [JsonPropertyName("skippedCount")]
        public int SkippedCount { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("transactionId")]
        public string? TransactionId { get; set; }

        [JsonPropertyName("rollback")]
        public RollbackInfoDto Rollback => new RollbackInfoDto
        {
            Available = !string.IsNullOrEmpty(TransactionId),
            RestorePoint = TransactionId ?? "Internal State Backup",
            TransactionId = TransactionId
        };

        [JsonPropertyName("operations")]
        public List<UniversalOptimizationItemDto> Operations { get; set; } = new();

        [JsonPropertyName("excludedOperations")]
        public List<UniversalOptimizationItemDto> ExcludedOperations { get; set; } = new();

        [JsonPropertyName("submittedIds")]
        public List<string> SubmittedIds { get; set; } = new();

        [JsonPropertyName("resolvedIds")]
        public List<string> ResolvedIds { get; set; } = new();

        [JsonPropertyName("rejectedIds")]
        public List<string> RejectedIds { get; set; } = new();

        [JsonPropertyName("rejectionReasons")]
        public Dictionary<string, string> RejectionReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("attemptedIds")]
        public List<string> AttemptedIds { get; set; } = new();
    }

    // =========================================================================
    // UNIVERSAL ELIGIBILITY ENGINE IMPLEMENTATION
    // =========================================================================

    public interface IUniversalEligibilityEngine
    {
        UniversalOptimizationPlanDto EvaluateTierProfile(string tierId);
        UniversalOptimizationPlanDto EvaluateRegistryIntelligence();
        UniversalOptimizationPlanDto EvaluateInputOptimizer();
        UniversalOptimizationPlanDto EvaluateDebloater(CancellationToken ct = default);
        UniversalOptimizationPlanDto EvaluateOneClick();
        Task<UniversalExecutionResultDto> ExecutePlanAsync(
            string moduleId,
            HashSet<string>? selectedItemIds,
            bool dryRun = false,
            CancellationToken ct = default);
    }

    public class UniversalEligibilityEngine : IUniversalEligibilityEngine
    {
        private readonly IProfileRepository _profileRepo;
        private readonly IEnvironmentDetector _envDetector;
        private readonly IActionRegistry _actionRegistry;
        private readonly IBackupManager _backupManager;
        private readonly IPackageManager _packageManager;
        private readonly TierEngine _tierEngine;
        private readonly ConditionEvaluator _conditionEvaluator;

        private static readonly HashSet<string> NeverTouchServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "SecurityHealthService", "mpssvc", "wscsvc", "Sense", "WdNisSvc", "MpsSvc",
            "AudioSrv", "AudioEndpointBuilder", "RpcSs", "RpcEptMapper", "DcomLaunch", "PlugPlay",
            "Power", "EventLog", "EventSystem", "BrokerInfrastructure", "SystemEventsBroker",
            "TimeBrokerSvc", "Schedule", "CryptSvc", "Dhcp", "Dnscache", "netprofm", "nsi",
            "Winmgmt", "LSM", "FontCache", "StateRepository", "ProfSvc", "gpsvc",
            "LanmanWorkstation", "LanmanServer", "Themes", "WlanSvc", "WwanSvc", "NlaSvc",
            "Netman"
        };

        private static readonly Dictionary<string, string[]> TierCategoryMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Normal"] = new[] { "Power", "Input", "Memory", "RAM", "Storage", "Ntfs", "Privacy", "Telemetry", "Service", "Visual", "Startup" },
            ["Pro"] = new[] { "Cpu", "CPU", "Memory", "RAM", "Network", "Gaming", "Telemetry", "Power", "Service", "Storage" },
            ["Ultimate"] = new[] { "Cpu", "CPU", "Memory", "RAM", "Gpu", "GPU", "Gaming", "Network", "Storage", "Ntfs", "Power", "Service", "System", "Kernel" },
            ["Debloat"] = new[] { "Privacy", "Telemetry", "Bloatware", "Debloat", "Appx", "Feature" },
            ["BiosSafe"] = new[] { "System", "Bios", "BIOS", "Firmware", "Audit" },
            ["MaximumPerformance"] = new[] { "Cpu", "CPU", "Gpu", "GPU", "Power", "Memory", "RAM", "Gaming", "Storage", "Network", "Workload" },
            ["AaaGaming"] = new[] { "Gpu", "GPU", "Gaming", "Cpu", "CPU", "Input", "Power" },
            ["AfterEffectsTurbo"] = new[] { "Memory", "RAM", "Storage", "Cpu", "CPU", "Power", "Workload" },
            ["EmulatorMax"] = new[] { "System", "Power", "Cpu", "CPU", "Memory", "RAM", "Workload" },
            ["LowEndRescue"] = new[] { "System", "Power", "Memory", "RAM", "Storage" },
            ["RamTest"] = new[] { "Memory", "RAM" },
            ["TopazAI"] = new[] { "Gpu", "GPU", "Cpu", "CPU", "Power", "Workload" },
            ["TotalMax"] = new[] { "Cpu", "CPU", "Gpu", "GPU", "Power", "Memory", "RAM", "Storage", "Network", "Gaming", "Workload" }
        };

        public UniversalEligibilityEngine(
            IProfileRepository profileRepo,
            IEnvironmentDetector envDetector,
            IActionRegistry actionRegistry,
            IBackupManager backupManager,
            IPackageManager packageManager,
            IProcessSnapshot processSnapshot)
        {
            _profileRepo = profileRepo;
            _envDetector = envDetector;
            _actionRegistry = actionRegistry;
            _backupManager = backupManager;
            _packageManager = packageManager;
            _tierEngine = new TierEngine(profileRepo, envDetector, actionRegistry, processSnapshot, backupManager);
            _conditionEvaluator = new ConditionEvaluator();
        }

        // =========================================================================
        // 1. TIER PROFILE EVALUATION (Normal, Pro, Ultimate, Debloat, BiosSafe, etc.)
        // =========================================================================

        public UniversalOptimizationPlanDto EvaluateTierProfile(string tierId)
        {
            string normalized = ProfileRepository.NormalizeProfileId(tierId);
            var plan = new UniversalOptimizationPlanDto
            {
                ModuleId = normalized,
                ModuleTitle = $"{normalized} Optimization"
            };

            var env = _envDetector.Detect();
            var hw = HardwareDetectionService.Instance.GetSnapshot(false);
            plan.HardwareSummary = $"CPU: {env.CpuName} ({env.PhysicalCoreCount}C/{env.LogicalCoreCount}T) | RAM: {env.RamSizeGb} GB | GPU: {string.Join(", ", env.Gpus.Select(g => g.Name))}";

            var profile = _profileRepo.LoadResolvedProfile(normalized);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Evaluate Profile Entries from JSON (e.g., 61 entries for Normal, 121 for Pro)
            if (profile?.Entries != null)
            {
                foreach (var entry in profile.Entries)
                {
                    if (!seenIds.Add(entry.Id)) continue;

                    var item = EvaluateProfileEntry(entry, env, hw, normalized);
                    CategorizePlanItem(plan, item);
                }
            }

            // 2. Evaluate Matching Catalog Items only for non-primary presets (e.g. AaaGaming, AfterEffectsTurbo)
            bool isPrimaryTier = normalized is "Normal" or "Pro" or "Ultimate" or "MaximumPerformance" or "Debloat" or "BiosSafe";
            if (!isPrimaryTier && TierCategoryMap.TryGetValue(normalized, out var cats))
            {
                foreach (var meta in OptimizationCatalog.Instance.GetAll())
                {
                    if (seenIds.Contains(meta.OptimizationId)) continue;
                    if (!cats.Any(c => c.Equals(meta.Category, StringComparison.OrdinalIgnoreCase))) continue;
                    seenIds.Add(meta.OptimizationId);

                    var item = EvaluateCatalogItem(meta, hw, env);
                    CategorizePlanItem(plan, item);
                }
            }

            FinalizePlanCounts(plan);
            return plan;
        }

        private UniversalOptimizationItemDto EvaluateProfileEntry(
            OptimizationEntry entry,
            EnvironmentContext env,
            HardwareSnapshot hw,
            string currentProfileName)
        {
            string targetValue = entry.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(targetValue))
            {
                if (entry.Action.Equals("RemoveAppxPackage", StringComparison.OrdinalIgnoreCase) ||
                    entry.Action.Equals("UninstallAppx", StringComparison.OrdinalIgnoreCase))
                {
                    targetValue = "Removed";
                }
                else if (entry.Action.Equals("DisableService", StringComparison.OrdinalIgnoreCase) ||
                         entry.Action.Equals("DisableScheduledTask", StringComparison.OrdinalIgnoreCase))
                {
                    targetValue = "Disabled";
                }
            }

            var item = new UniversalOptimizationItemDto
            {
                Id = entry.Id,
                Name = entry.DisplayName,
                Category = entry.Category,
                Description = entry.Description ?? entry.DisplayName,
                Risk = string.IsNullOrWhiteSpace(entry.Risk) ? "Low" : entry.Risk,
                Target = entry.Target ?? string.Empty,
                Action = entry.Action ?? string.Empty,
                TargetValue = targetValue,
                ExecutionType = entry.ExecutionType ?? "WINDOWS_AUTOMATIC",
                IsInherited = !string.Equals(entry.SourceProfile, currentProfileName, StringComparison.OrdinalIgnoreCase),
                Scope = entry.Category.Equals("System", StringComparison.OrdinalIgnoreCase) || entry.Category.Equals("Bios", StringComparison.OrdinalIgnoreCase) ? "UEFI" : "WINDOWS"
            };

            // Reboot to UEFI Firmware (Manual Action / Recommendation Only)
            if (entry.Id.Equals("bios.action.reboot_to_uefi_firmware", StringComparison.OrdinalIgnoreCase) ||
                entry.Target.Equals("RebootToUefi", StringComparison.OrdinalIgnoreCase))
            {
                item.EligibilityState = EligibilityState.RECOMMENDATION_ONLY;
                item.ExecutionType = "MANUAL_ACTION";
                item.TargetValue = "Direct UEFI Reboot";
                item.CurrentValue = "Supported";
                item.Reason = "Manual reboot trigger to enter UEFI BIOS setup directly from Windows";
                return item;
            }

            // Read-only audits & readiness diagnostics
            if (entry.Id.StartsWith("bios.audit.", StringComparison.OrdinalIgnoreCase) ||
                entry.Id.StartsWith("biossafe.audit.", StringComparison.OrdinalIgnoreCase) ||
                entry.Id.EndsWith("rebar_readiness", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.StartsWith("Audit", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.EndsWith("Audit", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.StartsWith("Evaluate", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.EndsWith("Readiness", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.Contains("Readiness", StringComparison.OrdinalIgnoreCase) ||
                entry.Action.StartsWith("Evaluate", StringComparison.OrdinalIgnoreCase))
            {
                item.EligibilityState = EligibilityState.READ_ONLY_AUDIT;
                item.ExecutionType = "READ_ONLY_AUDIT";
                item.TargetValue = "Diagnostic Audit Only";

                if (entry.Id.Contains("secure_boot", StringComparison.OrdinalIgnoreCase) || 
                    entry.DisplayName.Contains("Secure Boot", StringComparison.OrdinalIgnoreCase) ||
                    entry.Id.Contains("pk_status", StringComparison.OrdinalIgnoreCase) ||
                    entry.Id.Contains("dbx", StringComparison.OrdinalIgnoreCase))
                {
                    var sb = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditSecureBoot();
                    item.CurrentValue = sb.DisplayValue;
                    item.Reason = $"{sb.DisplayValue} • Source: {sb.EvidenceSource} • {sb.Confidence}";
                }
                else if (entry.Id.Contains("virtualization", StringComparison.OrdinalIgnoreCase) || 
                         entry.DisplayName.Contains("Virtualization", StringComparison.OrdinalIgnoreCase))
                {
                    var vt = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditCpuVirtualization();
                    item.CurrentValue = vt.Status;
                    item.Reason = $"{vt.Status} • {vt.EvidenceSource}";
                }
                else if (entry.Id.Contains("iommu", StringComparison.OrdinalIgnoreCase) || 
                         entry.DisplayName.Contains("IOMMU", StringComparison.OrdinalIgnoreCase) || 
                         entry.DisplayName.Contains("DMA", StringComparison.OrdinalIgnoreCase) ||
                         entry.Target.Contains("VT-d", StringComparison.OrdinalIgnoreCase))
                {
                    var dma = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditIommuDmaProtection();
                    item.CurrentValue = dma.Status;
                    item.Reason = $"{dma.Status} • {dma.EvidenceSource}";
                }
                else if (entry.Id.Contains("xmp", StringComparison.OrdinalIgnoreCase) || 
                         entry.DisplayName.Contains("XMP", StringComparison.OrdinalIgnoreCase) || 
                         entry.DisplayName.Contains("Memory Overclock", StringComparison.OrdinalIgnoreCase) ||
                         entry.DisplayName.Contains("Memory Clocking", StringComparison.OrdinalIgnoreCase) ||
                         entry.Target.Contains("XMP_EXPO", StringComparison.OrdinalIgnoreCase))
                {
                    var mem = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditMemoryProfiles();
                    item.CurrentValue = mem.Status;
                    item.Reason = $"{mem.Status} • {mem.EvidenceSource}";
                }
                else if (entry.Id.Contains("tpm", StringComparison.OrdinalIgnoreCase) || 
                         entry.DisplayName.Contains("TPM", StringComparison.OrdinalIgnoreCase) ||
                         entry.Id.Contains("ftpm", StringComparison.OrdinalIgnoreCase))
                {
                    var tpm = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditTpmStatus();
                    item.CurrentValue = tpm.Status;
                    item.Reason = $"{tpm.Status} • {tpm.EvidenceSource}";
                }
                else if (entry.Id.Contains("modern_standby", StringComparison.OrdinalIgnoreCase) ||
                         entry.DisplayName.Contains("Modern Standby", StringComparison.OrdinalIgnoreCase) ||
                         entry.Target.Contains("AcpiSleepAudit", StringComparison.OrdinalIgnoreCase) ||
                         entry.Target.Contains("ACPI_S0IX_VS_S3", StringComparison.OrdinalIgnoreCase))
                {
                    var ms = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditModernStandby();
                    item.CurrentValue = ms.Status;
                    item.Reason = $"{ms.Status} • {ms.EvidenceSource}";
                }
                else
                {
                    var rebar = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditReBarAndAbove4G();
                    item.CurrentValue = rebar.PrimaryStatusSummary;
                    item.Reason = $"{rebar.PrimaryStatusSummary} • Above 4G: {rebar.Above4GDecodingStatus} • Source: {rebar.EvidenceSource} • {rebar.Confidence}";
                }
                return item;
            }

            // A. Never-touch and security policies
            if (IsBlockedByDefenderProtection(entry, out var defenderReason))
            {
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = defenderReason;
                return item;
            }

            // A1. Protected Applications & Shielded Consumer Media (Spotify, Media, Creative)
            if (entry.Target.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                entry.Id.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            {
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = "Protected by policy: Spotify is shielded from automatic debloater removal";
                return item;
            }

            if (entry.Action.Equals("RemoveAppxPackage", StringComparison.OrdinalIgnoreCase) ||
                entry.Action.Equals("UninstallAppx", StringComparison.OrdinalIgnoreCase))
            {
                if (BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.IsProtectedPackage(entry.Target) ||
                    BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.IsProtectedPackage(entry.DisplayName))
                {
                    item.EligibilityState = EligibilityState.INELIGIBLE;
                    item.Reason = $"Protected by policy: '{entry.DisplayName}' is shielded by protected application rules";
                    return item;
                }

                // Verify actual installation status of the package
                var pkg = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
                if (pkg == null)
                {
                    string canTgt = GlobalOptimizationRegistry.NormalizeTarget(entry.Action, entry.Target);
                    bool wasApplied = GlobalAppliedStateManager.Instance.IsApplied(canTgt) ||
                                      GlobalAppliedStateManager.Instance.IsApplied(entry.Id);
                    if (wasApplied)
                    {
                        item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                        item.CurrentValue = "Removed";
                        item.Reason = $"Already removed by ERROR OPTIMIZER";
                    }
                    else
                    {
                        item.EligibilityState = EligibilityState.NOT_APPLICABLE;
                        item.CurrentValue = "Not Installed";
                        item.Reason = $"Package '{entry.Target}' is not installed on this system";
                    }
                    return item;
                }
                else
                {
                    item.CurrentValue = $"Installed ({pkg.Version})";
                }
            }

            // A2. Hardware Vendor & Architecture Isolation (AMD vs Intel vs NVIDIA vs NVMe vs Battery)
            // 1. AMD 3D V-Cache / Dual-CCD
            bool isAmdVcacheEntry = entry.Id.Contains("vcache", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("v_cache", StringComparison.OrdinalIgnoreCase) ||
                                    entry.DisplayName.Contains("3D V-Cache", StringComparison.OrdinalIgnoreCase) ||
                                    entry.DisplayName.Contains("Dual-CCD", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Target.Contains("ThreadAffinityBias", StringComparison.OrdinalIgnoreCase);

            if (isAmdVcacheEntry)
            {
                if (!hw.Cpu.IsAmd)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires AMD Ryzen processor (Intel CPU detected)";
                    return item;
                }
                if (!hw.Cpu.IsDualCcdAmd3DVcache)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires AMD Ryzen Dual-CCD / 3D V-Cache processor (single-CCD or standard processor detected)";
                    return item;
                }
            }

            // 2. AMD CPU General (Ryzen / amdppm / CPPC)
            bool isAmdCpuEntry = entry.Id.Contains("amd_ryzen", StringComparison.OrdinalIgnoreCase) ||
                                 entry.Target.Contains(@"\amdppm\", StringComparison.OrdinalIgnoreCase) ||
                                 (entry.Id.StartsWith("pro.cpu.amd", StringComparison.OrdinalIgnoreCase) ||
                                  entry.Id.StartsWith("ultimate.cpu.amd", StringComparison.OrdinalIgnoreCase) ||
                                  entry.Id.StartsWith("maxperf.cpu.amd", StringComparison.OrdinalIgnoreCase));

            if (isAmdCpuEntry && !isAmdVcacheEntry && !hw.Cpu.IsAmd)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires AMD Ryzen processor (Intel CPU detected)";
                return item;
            }

            // 3. Intel CPU Thread Director & Hybrid Architecture (P/E-Cores)
            bool isIntelThreadDirectorEntry = entry.Id.Contains("thread_director", StringComparison.OrdinalIgnoreCase) ||
                                              entry.Id.Contains("intel_hybrid", StringComparison.OrdinalIgnoreCase) ||
                                              entry.DisplayName.Contains("Thread Director", StringComparison.OrdinalIgnoreCase) ||
                                              entry.DisplayName.Contains("P-Core", StringComparison.OrdinalIgnoreCase);

            if (isIntelThreadDirectorEntry)
            {
                if (!hw.Cpu.IsIntel)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires Intel Core processor (AMD CPU detected)";
                    return item;
                }
                if (!hw.Cpu.IsHybridCpu)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires Intel Hybrid Architecture with Performance and Efficient cores";
                    return item;
                }
            }

            // 4. Intel CPU General
            bool isIntelCpuEntry = (entry.Id.StartsWith("pro.cpu.intel", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.StartsWith("ultimate.cpu.intel", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.StartsWith("maxperf.cpu.intel", StringComparison.OrdinalIgnoreCase)) &&
                                   !isIntelThreadDirectorEntry;

            if (isIntelCpuEntry && !hw.Cpu.IsIntel)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires Intel Core processor (AMD CPU detected)";
                return item;
            }

            // 5. AMD GPU (Radeon / PowerPlay / Anti-Lag / KMD_AntiLag / PP_ThermalAutoThrottlingEnable)
            bool isAmdGpuEntry = entry.Id.Contains("amd_powerplay", StringComparison.OrdinalIgnoreCase) ||
                                 entry.Id.Contains("amd_anti_lag", StringComparison.OrdinalIgnoreCase) ||
                                 entry.Target.Contains("KMD_AntiLag", StringComparison.OrdinalIgnoreCase) ||
                                 entry.Target.Contains("PP_ThermalAutoThrottlingEnable", StringComparison.OrdinalIgnoreCase) ||
                                 entry.DisplayName.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase) ||
                                 entry.DisplayName.Contains("AMD Anti-Lag", StringComparison.OrdinalIgnoreCase) ||
                                 entry.DisplayName.Contains("AMD PowerPlay", StringComparison.OrdinalIgnoreCase) ||
                                 entry.Conditions.Any(c => c.Type.Equals("HasAmdGpu", StringComparison.OrdinalIgnoreCase));

            if (isAmdGpuEntry && !hw.HasAmd)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires AMD Radeon graphics hardware (not detected on this PC)";
                return item;
            }

            // 6. NVIDIA GPU (GeForce / PowerMizer / Low Latency / Container / nv.maxpower / nv.throttle)
            bool isNvidiaGpuEntry = entry.Id.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("powermizer", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("nv.maxpower", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("nv.throttle", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("nv.container", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("nv.agent", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Id.Contains("nv.update", StringComparison.OrdinalIgnoreCase) ||
                                    entry.DisplayName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Conditions.Any(c => c.Type.Equals("HasNvidiaGpu", StringComparison.OrdinalIgnoreCase));

            if (isNvidiaGpuEntry && !hw.HasNvidia)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires NVIDIA GeForce graphics hardware (not detected on this PC)";
                return item;
            }

            // 7. Intel GPU (Arc / UHD / Iris)
            bool isIntelGpuEntry = entry.Id.Contains("intel_arc", StringComparison.OrdinalIgnoreCase) ||
                                   entry.DisplayName.Contains("Intel Arc", StringComparison.OrdinalIgnoreCase) ||
                                   entry.Conditions.Any(c => c.Type.Equals("HasIntelGpu", StringComparison.OrdinalIgnoreCase));

            if (isIntelGpuEntry && !hw.HasIntel)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires Intel graphics hardware (not detected on this PC)";
                return item;
            }

            // 8. Storage NVMe Architecture Check
            bool isNvmeStorageEntry = entry.Id.Contains("nvme", StringComparison.OrdinalIgnoreCase) ||
                                      entry.Id.Contains("storport", StringComparison.OrdinalIgnoreCase) ||
                                      entry.DisplayName.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                      entry.Conditions.Any(c => c.Type.Equals("StorageIsNvme", StringComparison.OrdinalIgnoreCase));

            if (isNvmeStorageEntry && !hw.Storage.HasNvmeDrive)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires NVMe solid-state storage (NVMe drive not detected)";
                return item;
            }

            // 8b. Storport miniport driver protection: Storport device parameters under stornvme are protected kernel infrastructure
            if (isNvmeStorageEntry && (entry.Target.Contains(@"\Services\stornvme", StringComparison.OrdinalIgnoreCase) ||
                                       entry.Id.Contains("storport_command_timeout", StringComparison.OrdinalIgnoreCase) ||
                                       entry.Id.Contains("nvme_command_timeout", StringComparison.OrdinalIgnoreCase)))
            {
                item.EligibilityState = EligibilityState.PROTECTED_BY_POLICY;
                item.Reason = "NVMe Storport driver parameters under 'stornvme' are protected Windows kernel storage infrastructure. Modifying driver reset timeouts on active boot storage controllers is restricted by driver safety policy.";
                return item;
            }

            // 8c. Speculative execution & Spectre Variant 2 Branch Target Injection (BTI) mitigation protection
            bool isSpectreMitigationEntry = entry.Id.Contains("spectre", StringComparison.OrdinalIgnoreCase) ||
                                            entry.DisplayName.Contains("Spectre", StringComparison.OrdinalIgnoreCase) ||
                                            entry.Target.Contains("FeatureSettingsOverride", StringComparison.OrdinalIgnoreCase);

            if (isSpectreMitigationEntry)
            {
                item.EligibilityState = EligibilityState.PROTECTED_BY_POLICY;
                item.Reason = "Speculative execution mitigations (Spectre Variant 2) are enforced by CPU hardware and protected by Windows Virtualization-Based Security (VBS). Weakening hardware mitigations is prohibited by system safety policy.";
                return item;
            }

            // 9. Battery / Power Autonomy Protection
            bool isAggressivePowerTweak = entry.Id.Contains("energy_performance_preference_max", StringComparison.OrdinalIgnoreCase) ||
                                          entry.Id.Contains("power_target_prefer_maximum", StringComparison.OrdinalIgnoreCase) ||
                                          entry.Id.Contains("powermizer_prefer_maximum", StringComparison.OrdinalIgnoreCase) ||
                                          entry.Id.Contains("disable_power_throttling", StringComparison.OrdinalIgnoreCase) ||
                                          entry.Id.Contains("nv.throttle", StringComparison.OrdinalIgnoreCase) ||
                                          entry.Id.Contains("nv.maxpower", StringComparison.OrdinalIgnoreCase) ||
                                          entry.Conditions.Any(c => c.Type.Equals("IsOnAc", StringComparison.OrdinalIgnoreCase));

            if (isAggressivePowerTweak && (hw.IsOnBattery || env.MachineProfile.IsOnBattery))
            {
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = "Device is running on battery power (preserving battery autonomy and thermal envelope)";
                return item;
            }

            // 10. Touchscreen / Digitizer Hardware Check
            bool isTouchEntry = entry.Id.Contains("touch", StringComparison.OrdinalIgnoreCase) ||
                                entry.Id.Contains("tablet", StringComparison.OrdinalIgnoreCase) ||
                                entry.Id.Contains("pen", StringComparison.OrdinalIgnoreCase) ||
                                entry.Id.Contains("flicks", StringComparison.OrdinalIgnoreCase) ||
                                entry.DisplayName.Contains("Touch", StringComparison.OrdinalIgnoreCase) ||
                                entry.DisplayName.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ||
                                entry.DisplayName.Contains("Pen", StringComparison.OrdinalIgnoreCase) ||
                                entry.Target.Contains("Touch", StringComparison.OrdinalIgnoreCase) ||
                                entry.Target.Contains("TabletInputService", StringComparison.OrdinalIgnoreCase) ||
                                entry.Target.Contains("wisptis", StringComparison.OrdinalIgnoreCase) ||
                                entry.Conditions.Any(c => c.Type.Equals("HasTouchScreen", StringComparison.OrdinalIgnoreCase));

            if (isTouchEntry && !hw.HasTouchScreen)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires touchscreen or active pen digitizer hardware (touch hardware not detected on this PC)";
                return item;
            }

            // 11. Audio Component Check
            bool isAudioEntry = entry.Id.Contains("pro_audio", StringComparison.OrdinalIgnoreCase) ||
                                entry.Id.Contains("audio_latency", StringComparison.OrdinalIgnoreCase) ||
                                entry.DisplayName.Contains("Pro Audio", StringComparison.OrdinalIgnoreCase) ||
                                entry.Target.Contains(@"\Tasks\Pro Audio", StringComparison.OrdinalIgnoreCase) ||
                                entry.Target.Contains(@"\Tasks\Audio", StringComparison.OrdinalIgnoreCase) ||
                                entry.Conditions.Any(c => c.Type.Equals("HasAudioDevice", StringComparison.OrdinalIgnoreCase));

            if (isAudioEntry && !hw.HasAudioDevice)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires active audio playback or sound card hardware (audio hardware not detected on this PC)";
                return item;
            }

            // 12. Discrete GPU Check
            bool requiresDiscreteGpu = entry.Conditions.Any(c => c.Type.Equals("HasDiscreteGpu", StringComparison.OrdinalIgnoreCase)) ||
                                       entry.Id.Contains("discrete_gpu", StringComparison.OrdinalIgnoreCase) ||
                                       entry.DisplayName.Contains("Discrete GPU", StringComparison.OrdinalIgnoreCase) ||
                                       entry.Warning.Contains("discrete GPU", StringComparison.OrdinalIgnoreCase) ||
                                       entry.Description.Contains("discrete GPU", StringComparison.OrdinalIgnoreCase);

            if (requiresDiscreteGpu && !hw.HasDiscreteGpu)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires dedicated discrete graphics processor (system operates on integrated graphics only)";
                return item;
            }

            // B. OS Version Support
            if (!IsOsSupported(entry, env))
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = $"Unsupported on this OS ({(env.IsWindows10 ? "Windows 10" : "Windows 11")})";
                return item;
            }

            // C. Condition Evaluator (hardware/software preconditions)
            if (!_conditionEvaluator.Evaluate(entry.Conditions, env))
            {
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = BuildConditionReason(entry.Conditions, env);
                return item;
            }

            // D. Handler & Target Existence
            var handler = _actionRegistry.GetHandler(entry.Action);
            if (handler == null)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = $"Handler '{entry.Action}' is unavailable on this system";
                return item;
            }

            var availability = handler.CheckAvailability(entry, env);
            if (availability != TargetState.Ready)
            {
                if (availability == TargetState.NotAvailable)
                {
                    item.EligibilityState = EligibilityState.NOT_APPLICABLE;
                    item.Reason = $"Target '{entry.Target}' not found on this machine";
                }
                else if (availability == TargetState.Protected)
                {
                    item.EligibilityState = EligibilityState.PROTECTED_BY_POLICY;
                    item.Reason = $"Target '{entry.Target}' is protected by safety policy";
                }
                else
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Action not applicable to current environment";
                }
                return item;
            }

            // E. Current State vs Target State Check (Already Optimized & Authoritative Applied-State)
            try
            {
                string canTarget = GlobalOptimizationRegistry.NormalizeTarget(entry.Action, entry.Target);

                // Authoritative ownership gate: if this target is authoritatively assigned to a different category, mark ineligible
                if (!GlobalOptimizationRegistry.Instance.IsAuthoritativeForCategory(canTarget, currentProfileName))
                {
                    string authorOwner = GlobalOptimizationRegistry.Instance.GetAuthoritativeOwner(canTarget);
                    item.EligibilityState = EligibilityState.INELIGIBLE;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    item.Reason = $"Target is authoritatively owned by '{authorOwner}'. Cross-page execution prohibited.";
                    return item;
                }

                // Authoritative System Setting Registry policy validation gate (prevents scan/execution mismatch)
                if (!GlobalSystemSettingRegistry.Instance.ValidateExecution(currentProfileName, canTarget, item.TargetValue, out string policyRej))
                {
                    item.EligibilityState = EligibilityState.INELIGIBLE;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    item.Reason = policyRej;
                    return item;
                }

                string handlerVal = string.Empty;
                try { handlerVal = handler.GetCurrentValueDisplay(entry); } catch { }

                var globalStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, item.TargetValue, out var observedVal, handlerVal);

                if (!string.IsNullOrWhiteSpace(observedVal))
                {
                    item.CurrentValue = observedVal;
                }
                else
                {
                    item.CurrentValue = handlerVal;
                }

                bool isAppxAction = entry.Action.Equals("RemoveAppxPackage", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Action.Equals("UninstallAppx", StringComparison.OrdinalIgnoreCase);
                bool isAppxRemoved = isAppxAction &&
                                     (string.Equals(item.CurrentValue, "Removed", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(item.CurrentValue, "Not Installed", StringComparison.OrdinalIgnoreCase));

                if (globalStatus == GlobalAppliedStatus.ALREADY_APPLIED)
                {
                    item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    item.Reason = $"Applied and verified by ERROR OPTIMIZER (Current '{item.CurrentValue}' matches target '{item.TargetValue}')";
                    return item;
                }

                if (globalStatus == GlobalAppliedStatus.ALREADY_OPTIMAL || globalStatus == GlobalAppliedStatus.ACTIVE_PROFILE || IsAlreadyOptimal(item.CurrentValue, item.TargetValue) || isAppxRemoved)
                {
                    item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    item.Reason = $"Already optimized in system (Current '{item.CurrentValue}' matches target '{item.TargetValue}')";
                    // Persist verified AlreadyOptimal observation if not yet recorded
                    if (!GlobalAppliedStateManager.Instance.TryGetRecord(canTarget, out var existingRec) ||
                        !string.Equals(existingRec?.StateClassification, "AppliedAndVerified", StringComparison.OrdinalIgnoreCase))
                    {
                        GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                            canTarget,
                            item.Id,
                            currentProfileName,
                            currentProfileName,
                            item.CurrentValue,
                            item.TargetValue,
                            entry.Action);
                    }
                    return item;
                }

                if (globalStatus == GlobalAppliedStatus.CONFIGURATION_CHANGED || globalStatus == GlobalAppliedStatus.EXTERNALLY_MODIFIED)
                {
                    item.EligibilityState = EligibilityState.ELIGIBLE;
                    item.Reason = $"Configuration changed externally (Current '{item.CurrentValue}' differs from target '{item.TargetValue}')";
                    return item;
                }
            }
            catch (Exception ex)
            {
                item.CurrentValue = "Unknown";
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = $"Detection failed: Could not read current value ({ex.Message})";
                return item;
            }

            // Passed all checks: Item is fully eligible!
            item.EligibilityState = EligibilityState.ELIGIBLE;
            item.Reason = "Applicable to current system configuration and ready to optimize";
            return item;
        }

        private UniversalOptimizationItemDto EvaluateCatalogItem(
            OptimizationMetadata meta,
            HardwareSnapshot hw,
            EnvironmentContext env)
        {
            var item = new UniversalOptimizationItemDto
            {
                Id = meta.OptimizationId,
                Name = meta.Name,
                Category = meta.Category,
                Description = meta.Description,
                Risk = meta.RiskLevel,
                RequiresAdmin = meta.RequiresAdmin,
                RequiresRestart = meta.RequiresRestart,
                IsReversible = meta.RollbackSupport,
                Target = meta.OptimizationId,
                Action = meta.ExecutionMethod ?? "DirectRegistry",
                TargetValue = meta.ReadTargetState?.Invoke() ?? string.Empty,
                ExecutionType = "WINDOWS_AUTOMATIC",
                Scope = meta.Category.Equals("System", StringComparison.OrdinalIgnoreCase) || meta.Category.Equals("Bios", StringComparison.OrdinalIgnoreCase) ? "UEFI" : "WINDOWS"
            };

            // Check if read-only audit or diagnostic
            if (string.Equals(meta.ExecutionMethod, "DiagnosticReadOnly", StringComparison.OrdinalIgnoreCase) ||
                meta.OptimizationId.EndsWith("_AUDIT", StringComparison.OrdinalIgnoreCase))
            {
                item.EligibilityState = EligibilityState.READ_ONLY_AUDIT;
                item.ExecutionType = "READ_ONLY_AUDIT";
                item.TargetValue = "Diagnostic Audit Only";
                if (meta.OptimizationId.Contains("SECURE_BOOT", StringComparison.OrdinalIgnoreCase))
                {
                    var sb = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditSecureBoot();
                    item.CurrentValue = sb.DisplayValue;
                    item.Reason = $"{sb.DisplayValue} • Source: {sb.EvidenceSource} • {sb.Confidence}";
                }
                else
                {
                    var rebar = BiosOptimizer.Core.Implementations.BiosSafe.BiosSafeAuditor.AuditReBarAndAbove4G();
                    item.CurrentValue = rebar.PrimaryStatusSummary;
                    item.Reason = $"{rebar.PrimaryStatusSummary} • Above 4G: {rebar.Above4GDecodingStatus} • Source: {rebar.EvidenceSource} • {rebar.Confidence}";
                }
                return item;
            }

            // Hardware Vendor Gates (Strict hardware isolation: AMD vs NVIDIA vs Intel)
            if (meta.OptimizationId.StartsWith("CPU.AMD", StringComparison.OrdinalIgnoreCase))
            {
                if (!hw.Cpu.IsAmd)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires AMD Ryzen processor (Intel CPU detected)";
                    return item;
                }
                if (meta.OptimizationId.Contains("3D_VCACHE", StringComparison.OrdinalIgnoreCase) && !hw.Cpu.IsDualCcdAmd3DVcache)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires AMD Ryzen Dual-CCD / 3D V-Cache processor (single-CCD or standard processor detected)";
                    return item;
                }
            }
            else if (meta.OptimizationId.StartsWith("CPU.INTEL", StringComparison.OrdinalIgnoreCase))
            {
                if (!hw.Cpu.IsIntel)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires Intel Core processor (AMD CPU detected)";
                    return item;
                }
                if (meta.OptimizationId.Contains("THREAD_DIRECTOR", StringComparison.OrdinalIgnoreCase) && !hw.Cpu.IsHybridCpu)
                {
                    item.EligibilityState = EligibilityState.UNSUPPORTED;
                    item.Reason = "Requires Intel Hybrid Architecture with Performance and Efficient cores";
                    return item;
                }
            }
            else if (meta.VendorScope.Equals("Amd", StringComparison.OrdinalIgnoreCase) && !hw.HasAmd)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires AMD Radeon graphics hardware (not detected on this PC)";
                return item;
            }
            else if (meta.VendorScope.Equals("Nvidia", StringComparison.OrdinalIgnoreCase) && !hw.HasNvidia)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires NVIDIA GeForce graphics hardware (not detected on this PC)";
                return item;
            }
            else if (meta.VendorScope.Equals("Intel", StringComparison.OrdinalIgnoreCase) && !hw.HasIntel)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires Intel graphics hardware (not detected on this PC)";
                return item;
            }

            // Storage NVMe check
            if (meta.OptimizationId.Contains("NVME", StringComparison.OrdinalIgnoreCase) && !hw.Storage.HasNvmeDrive)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires NVMe solid-state storage (NVMe drive not detected)";
                return item;
            }

            // Touchscreen check
            if ((meta.OptimizationId.Contains("TOUCH", StringComparison.OrdinalIgnoreCase) ||
                 meta.OptimizationId.Contains("TABLET", StringComparison.OrdinalIgnoreCase) ||
                 meta.OptimizationId.Contains("PEN", StringComparison.OrdinalIgnoreCase)) && !hw.HasTouchScreen)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires touchscreen or active pen digitizer hardware (touch hardware not detected on this PC)";
                return item;
            }

            // Audio hardware check
            if ((meta.OptimizationId.Contains("AUDIO", StringComparison.OrdinalIgnoreCase) ||
                 meta.OptimizationId.Contains("SOUND", StringComparison.OrdinalIgnoreCase)) && !hw.HasAudioDevice)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires active audio playback or sound card hardware (audio hardware not detected on this PC)";
                return item;
            }

            // Discrete GPU check
            if (meta.OptimizationId.Contains("DISCRETE_GPU", StringComparison.OrdinalIgnoreCase) && !hw.HasDiscreteGpu)
            {
                item.EligibilityState = EligibilityState.UNSUPPORTED;
                item.Reason = "Requires dedicated discrete graphics processor (system operates on integrated graphics only)";
                return item;
            }

            // Power check (AC vs Battery)
            if ((meta.OptimizationId.StartsWith("POWER.", StringComparison.OrdinalIgnoreCase) ||
                 meta.OptimizationId.Contains("PERF", StringComparison.OrdinalIgnoreCase)) && hw.IsOnBattery)
            {
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = "Device is running on battery power (preserving battery autonomy and thermal envelope)";
                return item;
            }

            // Run item-specific evaluator
            if (meta.Evaluator != null)
            {
                try
                {
                    var eval = meta.Evaluator(hw);
                    item.CurrentValue = eval.CurrentState;
                    item.TargetValue = eval.TargetState;

                    if (eval.Status == OptimizationAuditStatus.AlreadyOptimized || IsAlreadyOptimal(eval.CurrentState, eval.TargetState))
                    {
                        item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                        item.Reason = string.IsNullOrWhiteSpace(eval.Reason) || !eval.Reason.Contains("Already", StringComparison.OrdinalIgnoreCase)
                            ? $"Already optimized (Current state '{item.CurrentValue}' matches target '{item.TargetValue}')"
                            : eval.Reason;
                        return item;
                    }

                    switch (eval.Status)
                    {
                        case OptimizationAuditStatus.ReadOnlyAudit:
                            item.EligibilityState = EligibilityState.READ_ONLY_AUDIT;
                            item.ExecutionType = "READ_ONLY_AUDIT";
                            item.Reason = eval.Reason;
                            return item;

                        case OptimizationAuditStatus.NotApplicable:
                            item.EligibilityState = EligibilityState.INELIGIBLE;
                            item.Reason = eval.Reason;
                            return item;

                        case OptimizationAuditStatus.Unsupported:
                            item.EligibilityState = EligibilityState.UNSUPPORTED;
                            item.Reason = eval.Reason;
                            return item;

                        case OptimizationAuditStatus.RequiresAdmin:
                            item.EligibilityState = EligibilityState.REQUIRES_ELEVATION;
                            item.Reason = eval.Reason;
                            return item;

                        case OptimizationAuditStatus.Applicable:
                            item.EligibilityState = EligibilityState.ELIGIBLE;
                            item.Reason = eval.Reason;
                            return item;
                    }
                }
                catch (Exception ex)
                {
                    item.EligibilityState = EligibilityState.INELIGIBLE;
                    item.Reason = $"Evaluator error: {ex.Message}";
                    return item;
                }
            }

            // Read state functions fallback
            try
            {
                item.CurrentValue = meta.ReadCurrentState?.Invoke() ?? "Default";
                item.TargetValue = meta.ReadTargetState?.Invoke() ?? "Optimized";
                bool isDone = (meta.Verify != null && meta.Verify()) || IsAlreadyOptimal(item.CurrentValue, item.TargetValue);
                if (isDone)
                {
                    item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                    item.Reason = $"Already optimized (Current state matches '{item.TargetValue}')";
                    return item;
                }
            }
            catch (Exception ex)
            {
                item.EligibilityState = EligibilityState.INELIGIBLE;
                item.Reason = $"Detection failed: {ex.Message}";
                return item;
            }

            // Authoritative global applied-state verification
            string action = meta.ExecutionMethod ?? "DirectRegistry";
            string rawTarget = meta.OptimizationId;
            string canTarget = GlobalOptimizationRegistry.NormalizeTarget(action, rawTarget);

            var globalStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, item.TargetValue, out var observedVal);
            if (!string.IsNullOrWhiteSpace(observedVal))
            {
                item.CurrentValue = observedVal;
            }

            if (globalStatus == GlobalAppliedStatus.ALREADY_APPLIED || globalStatus == GlobalAppliedStatus.ALREADY_OPTIMAL || globalStatus == GlobalAppliedStatus.ACTIVE_PROFILE)
            {
                item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                item.SelectionState = SelectionState.NOT_SELECTED;
                item.Reason = $"Already optimized in system (Current state matches '{item.TargetValue}')";
                return item;
            }

            item.EligibilityState = EligibilityState.ELIGIBLE;
            item.Reason = "Applicable to current system configuration and ready to optimize";
            return item;
        }

        // =========================================================================
        // 2. REGISTRY INTELLIGENCE EVALUATION (All 31 real registry tweaks)
        // =========================================================================

        public UniversalOptimizationPlanDto EvaluateRegistryIntelligence()
        {
            var plan = new UniversalOptimizationPlanDto
            {
                ModuleId = "RegistryIntelligence",
                ModuleTitle = "Registry Intelligence"
            };

            var hw = HardwareDetectionService.Instance.GetSnapshot(false);
            plan.HardwareSummary = $"Authoritative registry catalog · {hw.Windows?.FullDisplayString ?? "Windows"}";

            var tweakEngine = new RegistryTweakEngine(_backupManager);
            var tweakPlan = tweakEngine.PlanRegistryTweakAsync().GetAwaiter().GetResult();

            if (tweakPlan?.Tweaks != null)
            {
                foreach (var dto in tweakPlan.Tweaks)
                {
                    var item = new UniversalOptimizationItemDto
                    {
                        Id = dto.Id,
                        Name = dto.Name,
                        Category = dto.Category,
                        Description = dto.Description,
                        Risk = dto.RiskLevel,
                        RequiresAdmin = dto.RequiresAdmin,
                        RequiresRestart = dto.RequiresReboot,
                        Target = $"{dto.RootKey}\\{dto.SubKey}\\{dto.ValueName}",
                        Action = "SetRegistryValue",
                        CurrentValue = string.IsNullOrWhiteSpace(dto.CurrentValue) ? "Default" : dto.CurrentValue,
                        TargetValue = string.IsNullOrWhiteSpace(dto.TargetValue) ? "Optimized" : dto.TargetValue,
                        Scope = "WINDOWS",
                        ExecutionType = "WINDOWS_AUTOMATIC"
                    };

                    string canTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", $"{dto.RootKey}\\{dto.SubKey}\\{dto.ValueName}");
                    if (!GlobalOptimizationRegistry.Instance.IsAuthoritativeForCategory(canTarget, "RegistryIntelligence"))
                    {
                        string authorOwner = GlobalOptimizationRegistry.Instance.GetAuthoritativeOwner(canTarget);
                        item.EligibilityState = EligibilityState.INELIGIBLE;
                        item.SelectionState = SelectionState.NOT_SELECTED;
                        item.Reason = $"Authoritative owner is '{authorOwner}'. Cross-page duplication prevented.";
                        CategorizePlanItem(plan, item);
                        continue;
                    }

                    var globalStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, item.TargetValue, out var observedVal);
                    if (!string.IsNullOrWhiteSpace(observedVal))
                    {
                        item.CurrentValue = observedVal;
                    }

                    if (globalStatus == GlobalAppliedStatus.ALREADY_APPLIED || globalStatus == GlobalAppliedStatus.ALREADY_OPTIMAL || globalStatus == GlobalAppliedStatus.ACTIVE_PROFILE)
                    {
                        item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                        item.SelectionState = SelectionState.NOT_SELECTED;
                        item.Reason = globalStatus == GlobalAppliedStatus.ALREADY_APPLIED
                            ? "Already applied by ERROR OPTIMIZER and verified in live registry."
                            : "Current registry value already matches optimal target.";
                        CategorizePlanItem(plan, item);
                        continue;
                    }

                    bool isApplicable = !dto.Applicability.Equals("NOT APPLICABLE", StringComparison.OrdinalIgnoreCase) &&
                                        !dto.Applicability.Equals("NOT_APPLICABLE", StringComparison.OrdinalIgnoreCase) &&
                                        !dto.Applicability.Equals("UNSUPPORTED", StringComparison.OrdinalIgnoreCase) &&
                                        !dto.Status.Equals("NOT APPLICABLE", StringComparison.OrdinalIgnoreCase) &&
                                        !dto.Status.Equals("NOT_APPLICABLE", StringComparison.OrdinalIgnoreCase) &&
                                        !dto.Status.Equals("UNSUPPORTED", StringComparison.OrdinalIgnoreCase);

                    bool isOptimized = dto.Status.Equals("ALREADY OPTIMIZED", StringComparison.OrdinalIgnoreCase) ||
                                       dto.Status.Equals("ALREADY_OPTIMIZED", StringComparison.OrdinalIgnoreCase) ||
                                       dto.Status.Equals("ALREADY OPTIMAL", StringComparison.OrdinalIgnoreCase) ||
                                       dto.Status.Equals("ALREADY_OPTIMAL", StringComparison.OrdinalIgnoreCase) ||
                                       dto.VerificationState.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase) ||
                                       IsAlreadyOptimal(item.CurrentValue, item.TargetValue);

                    // Hardware Vendor & Subsystem Gating
                    bool isAmdCpuTweak = item.Id.Contains(".amd_", StringComparison.OrdinalIgnoreCase) ||
                                         item.Id.Contains("amd_cppc", StringComparison.OrdinalIgnoreCase) ||
                                         item.Id.Contains("cppc", StringComparison.OrdinalIgnoreCase) ||
                                         item.Name.Contains("AMD CPPC", StringComparison.OrdinalIgnoreCase) ||
                                         item.Name.Contains("CPPC", StringComparison.OrdinalIgnoreCase) ||
                                         item.Description.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase) ||
                                         item.Description.Contains("CPPC", StringComparison.OrdinalIgnoreCase);

                    bool isAmdGpuTweak = item.Id.Contains(".amd.", StringComparison.OrdinalIgnoreCase) ||
                                         item.Id.Contains("amd_powerplay", StringComparison.OrdinalIgnoreCase) ||
                                         item.Id.Contains("amd_anti_lag", StringComparison.OrdinalIgnoreCase) ||
                                         item.Name.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase) ||
                                         item.Description.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase);

                    bool isNvidiaGpuTweak = item.Id.Contains(".nvidia.", StringComparison.OrdinalIgnoreCase) ||
                                           item.Id.Contains("powermizer", StringComparison.OrdinalIgnoreCase) ||
                                           item.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                           item.Description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

                    bool isIntelGpuTweak = item.Id.Contains(".intel_arc", StringComparison.OrdinalIgnoreCase) ||
                                          item.Name.Contains("Intel Arc", StringComparison.OrdinalIgnoreCase);

                    bool isIntelCpuTweak = item.Id.Contains(".intel_", StringComparison.OrdinalIgnoreCase) ||
                                           item.Name.Contains("Intel Thread Director", StringComparison.OrdinalIgnoreCase) ||
                                           item.Description.Contains("Intel Thread Director", StringComparison.OrdinalIgnoreCase);

                    bool isNvmeTweak = item.Id.Contains("nvme", StringComparison.OrdinalIgnoreCase) ||
                                       item.Id.Contains("bypassio", StringComparison.OrdinalIgnoreCase) ||
                                       item.Name.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                       item.Name.Contains("BypassIO", StringComparison.OrdinalIgnoreCase);

                    bool isTouchTweak = item.Id.Contains("touch", StringComparison.OrdinalIgnoreCase) ||
                                        item.Id.Contains("tablet", StringComparison.OrdinalIgnoreCase) ||
                                        item.Id.Contains("pen", StringComparison.OrdinalIgnoreCase) ||
                                        item.Name.Contains("Touch", StringComparison.OrdinalIgnoreCase) ||
                                        item.Name.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ||
                                        item.Target.Contains("Touch", StringComparison.OrdinalIgnoreCase) ||
                                        item.Target.Contains("TabletInputService", StringComparison.OrdinalIgnoreCase);

                    bool isAudioTweak = item.Id.Contains("pro_audio", StringComparison.OrdinalIgnoreCase) ||
                                        item.Id.Contains("audio_latency", StringComparison.OrdinalIgnoreCase) ||
                                        item.Name.Contains("Pro Audio", StringComparison.OrdinalIgnoreCase) ||
                                        item.Target.Contains(@"\Tasks\Pro Audio", StringComparison.OrdinalIgnoreCase);

                    bool isDiscreteGpuTweak = item.Id.Contains("discrete_gpu", StringComparison.OrdinalIgnoreCase) ||
                                              item.Name.Contains("Discrete GPU", StringComparison.OrdinalIgnoreCase);

                    if (isAmdCpuTweak && !hw.Cpu.IsAmd)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires AMD Ryzen processor (Intel CPU detected)";
                    }
                    else if (isAmdGpuTweak && !hw.HasAmd)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires AMD Radeon graphics hardware (not detected on this PC)";
                    }
                    else if (isNvidiaGpuTweak && !hw.HasNvidia)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires NVIDIA GeForce graphics hardware (not detected on this PC)";
                    }
                    else if (isIntelGpuTweak && !hw.HasIntel)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires Intel graphics hardware (not detected on this PC)";
                    }
                    else if (isIntelCpuTweak && (!hw.Cpu.IsIntel || !hw.Cpu.IsHybridCpu))
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = !hw.Cpu.IsIntel ? "Requires Intel Core processor (AMD CPU detected)" : "Requires Intel Hybrid Architecture with Performance and Efficient cores";
                    }
                    else if (isNvmeTweak && !hw.Storage.HasNvmeDrive)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires NVMe solid-state storage (NVMe drive not detected)";
                    }
                    else if (isTouchTweak && !hw.HasTouchScreen)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires touchscreen or pen digitizer hardware (touch hardware not detected on this PC)";
                    }
                    else if (isAudioTweak && !hw.HasAudioDevice)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires active audio playback or sound card hardware (no audio hardware detected)";
                    }
                    else if (isDiscreteGpuTweak && !hw.HasDiscreteGpu)
                    {
                        item.EligibilityState = EligibilityState.UNSUPPORTED;
                        item.Reason = "Requires dedicated discrete graphics processor (system operates on integrated graphics only)";
                    }
                    else if (!isApplicable)
                    {
                        item.EligibilityState = EligibilityState.INELIGIBLE;
                        item.Reason = string.IsNullOrWhiteSpace(dto.ApplicabilityReason) ? "Registry path not applicable to this system" : dto.ApplicabilityReason;
                    }
                    else if (isOptimized)
                    {
                        item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                        item.Reason = $"Already configured to optimal target value '{item.TargetValue}'";
                        string regCanTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", item.Target);
                        GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                            regCanTarget,
                            item.Id,
                            "RegistryIntelligence",
                            "RegistryIntelligence",
                            item.CurrentValue,
                            item.TargetValue,
                            "SetRegistryValue");
                    }
                    else
                    {
                        item.EligibilityState = EligibilityState.ELIGIBLE;
                        item.Reason = "Applicable to current system registry and ready to optimize";
                    }

                    CategorizePlanItem(plan, item);
                }
            }

            FinalizePlanCounts(plan);
            return plan;
        }

        // =========================================================================
        // 3. INPUT OPTIMIZER EVALUATION
        // =========================================================================

        public UniversalOptimizationPlanDto EvaluateInputOptimizer()
        {
            var plan = new UniversalOptimizationPlanDto
            {
                ModuleId = "InputLatency",
                ModuleTitle = "Input Latency Optimization"
            };

            var items = new List<OptimizationMetadata>
            {
                OptimizationCatalog.Instance.GetById("INPUT.DISABLE_MOUSE_ACCEL")!,
                OptimizationCatalog.Instance.GetById("INPUT.KEYBOARD_REPEAT_RATE_MAX")!,
                OptimizationCatalog.Instance.GetById("INPUT.KEYBOARD_REPEAT_DELAY_MIN")!,
                OptimizationCatalog.Instance.GetById("INPUT.MOUSE_QUEUE_SIZE_100")!,
                OptimizationCatalog.Instance.GetById("INPUT.KEYBOARD_QUEUE_SIZE_100")!,
                OptimizationCatalog.Instance.GetById("INPUT.MOUSE_HOVER_TIME_MIN")!,
                OptimizationCatalog.Instance.GetById("INPUT.DWM_WAITABLE_OBJECT_QUEUE")!,
                OptimizationCatalog.Instance.GetById("INPUT.FILTER_KEYS_SHORTCUTS_DISABLE")!,
                OptimizationCatalog.Instance.GetById("INPUT.USB_ROOT_HUB_POWER_POLICY")!
            };

            var hw = HardwareDetectionService.Instance.GetSnapshot(false);
            var env = _envDetector.Detect();

            foreach (var meta in items.Where(m => m != null))
            {
                var item = EvaluateCatalogItem(meta, hw, env);
                CategorizePlanItem(plan, item);
            }

            FinalizePlanCounts(plan);
            return plan;
        }

        // =========================================================================
        // 4. DEBLOATER EVALUATION (Real package discovery via PackageManager)
        // =========================================================================

        public UniversalOptimizationPlanDto EvaluateDebloater(CancellationToken ct = default)
        {
            var plan = new UniversalOptimizationPlanDto
            {
                ModuleId = "Debloat",
                ModuleTitle = "Debloater"
            };

            var engine = new DebloatEngine(_backupManager);
            var report = engine.DiscoverInstalledPackagesAsync(ct).GetAwaiter().GetResult();

            var installedIds = new HashSet<string>(
                report.Where(p => p.IsInstalled).Select(p => p.PackageName),
                StringComparer.OrdinalIgnoreCase);

            var catalogPackages = new[]
            {
                ("Microsoft.BingNews", "Microsoft Bing News", "News and financial feed background processes"),
                ("Microsoft.BingWeather", "Microsoft Bing Weather", "Live tile and periodic background network updates"),
                ("Microsoft.GamingApp", "Xbox Gaming App", "Xbox companion and background service daemons"),
                ("Microsoft.GetHelp", "Microsoft Get Help", "Interactive troubleshooting and feedback services"),
                ("Microsoft.Getstarted", "Microsoft Tips / Get Started", "System promotional tips and notification services"),
                ("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "Bundled consumer games and ad services"),
                ("Microsoft.People", "Microsoft People", "Windows contacts hub synchronization"),
                ("Microsoft.WindowsFeedbackHub", "Windows Feedback Hub", "Telemetry feedback submission client"),
                ("Microsoft.ZuneMusic", "Windows Media Player / Groove Music", "Pre-installed media player suite"),
                ("Microsoft.ZuneVideo", "Films & TV Video Player", "Pre-installed video player suite"),
                ("Clipchamp.Clipchamp", "Clipchamp Video Editor", "Pre-installed cloud video editor client"),
                ("Microsoft.549981C3F5F10", "Cortana Voice Assistant", "Legacy Cortana assistant daemon"),
                ("Microsoft.Windows.Widgets", "Windows 11 Widgets Feed", "Taskbar news and widgets background host")
            };

            foreach (var (pkgName, title, desc) in catalogPackages)
            {
                var item = new UniversalOptimizationItemDto
                {
                    Id = pkgName,
                    Name = title,
                    Category = "Bloatware",
                    Description = desc,
                    Risk = "Low",
                    Scope = "APP",
                    ExecutionType = "WINDOWS_AUTOMATIC"
                };

                if (installedIds.Contains(pkgName))
                {
                    item.EligibilityState = EligibilityState.ELIGIBLE;
                    item.Reason = "Package is currently installed on this system and eligible for safe removal";
                    item.CurrentValue = "Installed";
                    item.TargetValue = "Removed";
                }
                else
                {
                    item.EligibilityState = EligibilityState.ALREADY_OPTIMIZED;
                    item.Reason = "Package already removed from current user profile";
                    item.CurrentValue = "Removed";
                    item.TargetValue = "Removed";
                }

                CategorizePlanItem(plan, item);
            }

            FinalizePlanCounts(plan);
            return plan;
        }

        // =========================================================================
        // 5. ONE-CLICK EVALUATION
        // =========================================================================

        public UniversalOptimizationPlanDto EvaluateOneClick()
        {
            var plan = new UniversalOptimizationPlanDto
            {
                ModuleId = "1-Click",
                ModuleTitle = "1-Click Optimization"
            };

            var actions = new[]
            {
                ("visual-tweaks", "Windows Visual Responsiveness", "System Performance", "Safe", "Reversible • Immediate"),
                ("bcd-platform-timers", "System Timer & Responsiveness", "System Performance", "Low", "Reversible • Requires restart"),
                ("shader-cache", "Graphics Shader Cache Cleanup", "System Performance", "Safe", "Rebuilt automatically"),
                ("fast-clean", "Temporary System Cache Cleanup", "System Performance", "Safe", "Unlocked temp files only"),
                ("deep-clean", "Deep System & Update Cache Cleanup", "System Performance", "Safe", "Preserves personal data"),
                ("background-workload", "Reduce Background Workload", "Background Workload", "Safe", "Protected apps excluded"),
                ("ram-standby", "Memory Cleanup & Refresh", "Memory", "Safe", "Releases standby memory"),
                ("power-performance", "Sustained Performance Mode", "Power / Performance", "Safe", "High performance AC scheme")
            };

            foreach (var (id, name, cat, risk, desc) in actions)
            {
                var item = new UniversalOptimizationItemDto
                {
                    Id = id,
                    Name = name,
                    Category = cat,
                    Description = desc,
                    Risk = risk,
                    EligibilityState = EligibilityState.ELIGIBLE,
                    Reason = "Standard one-click maintenance task ready to run",
                    Scope = "WINDOWS",
                    ExecutionType = "WINDOWS_AUTOMATIC"
                };
                CategorizePlanItem(plan, item);
            }

            FinalizePlanCounts(plan);
            return plan;
        }

        // =========================================================================
        // 6. UNIVERSAL EXECUTION & READBACK ENGINE
        // =========================================================================

        public async Task<UniversalExecutionResultDto> ExecutePlanAsync(
            string moduleId,
            HashSet<string>? selectedItemIds,
            bool dryRun = false,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string normalizedModule = ProfileRepository.NormalizeProfileId(moduleId);

            var res = new UniversalExecutionResultDto
            {
                ModuleId = normalizedModule,
                ModuleTitle = $"{normalizedModule} Optimization"
            };

            // Phase A: Pre-execution Runtime Re-evaluation (Rule 6: Never trust stale cache!)
            UniversalOptimizationPlanDto freshPlan;
            if (normalizedModule.Equals("RegistryIntelligence", StringComparison.OrdinalIgnoreCase))
                freshPlan = EvaluateRegistryIntelligence();
            else if (normalizedModule.Equals("InputLatency", StringComparison.OrdinalIgnoreCase))
                freshPlan = EvaluateInputOptimizer();
            else if (normalizedModule.Equals("Debloat", StringComparison.OrdinalIgnoreCase))
                freshPlan = EvaluateTierProfile("Debloat");
            else if (normalizedModule.Equals("1-Click", StringComparison.OrdinalIgnoreCase))
                freshPlan = EvaluateOneClick();
            else
                freshPlan = EvaluateTierProfile(normalizedModule);

            res.TotalCatalogCount = freshPlan.TotalCatalogCount;
            res.EligibleCount = freshPlan.EligibleCount;
            res.IneligibleCount = freshPlan.IneligibleCount;
            res.AlreadyOptimizedCount = 0; // will count requested items that are already optimal

            if (selectedItemIds != null)
            {
                res.SubmittedIds.AddRange(selectedItemIds);
            }

            bool hasExplicitSelection = selectedItemIds != null && selectedItemIds.Count > 0;
            var selectedSet = hasExplicitSelection
                ? new HashSet<string>(selectedItemIds!, StringComparer.OrdinalIgnoreCase)
                : null;

            var allPlanItems = freshPlan.Items.Concat(freshPlan.ExcludedItems).ToList();

            // Multi-Index Resolution Maps for robust identifier matching
            var idMap = new Dictionary<string, UniversalOptimizationItemDto>(StringComparer.OrdinalIgnoreCase);
            var nameMap = new Dictionary<string, UniversalOptimizationItemDto>(StringComparer.OrdinalIgnoreCase);
            var targetMap = new Dictionary<string, UniversalOptimizationItemDto>(StringComparer.OrdinalIgnoreCase);
            var canonicalTargetMap = new Dictionary<string, UniversalOptimizationItemDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in allPlanItems)
            {
                if (!string.IsNullOrEmpty(p.Id)) idMap[p.Id] = p;
                if (!string.IsNullOrEmpty(p.Name)) nameMap[p.Name] = p;
                if (!string.IsNullOrEmpty(p.Target)) targetMap[p.Target] = p;
                string canTgt = GlobalOptimizationRegistry.NormalizeTarget(!string.IsNullOrEmpty(p.Action) ? p.Action : "SetRegistryValue", !string.IsNullOrEmpty(p.Target) ? p.Target : p.Id);
                if (!string.IsNullOrEmpty(canTgt)) canonicalTargetMap[canTgt] = p;
            }

            UniversalOptimizationItemDto? ResolveItem(string queryId)
            {
                if (string.IsNullOrWhiteSpace(queryId)) return null;
                if (idMap.TryGetValue(queryId, out var it)) return it;
                if (nameMap.TryGetValue(queryId, out it)) return it;
                if (targetMap.TryGetValue(queryId, out it)) return it;
                string canTgt = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", queryId);
                if (canonicalTargetMap.TryGetValue(canTgt, out it)) return it;

                var targetMatch = allPlanItems.FirstOrDefault(p =>
                    p.Target.Equals(queryId, StringComparison.OrdinalIgnoreCase) ||
                    p.Target.Contains(queryId, StringComparison.OrdinalIgnoreCase) ||
                    queryId.Contains(p.Target, StringComparison.OrdinalIgnoreCase));
                if (targetMatch != null) return targetMatch;

                var canonicalInputId = InputOptimizerEngine.ResolveCanonicalFromFrontendAlias(queryId);
                if (!string.IsNullOrEmpty(canonicalInputId))
                {
                    if (idMap.TryGetValue(canonicalInputId, out it)) return it;
                    if (targetMap.TryGetValue(canonicalInputId, out it)) return it;
                }

                var suffixMatch = allPlanItems.FirstOrDefault(p =>
                    p.Id.EndsWith("." + queryId, StringComparison.OrdinalIgnoreCase) ||
                    queryId.EndsWith("." + p.Id, StringComparison.OrdinalIgnoreCase));
                if (suffixMatch != null) return suffixMatch;

                return null;
            }

            var resolvedRequestedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedItemIds != null)
            {
                foreach (var qId in selectedItemIds)
                {
                    var resolved = ResolveItem(qId);
                    if (resolved != null)
                    {
                        resolvedRequestedItemIds.Add(resolved.Id);
                        res.ResolvedIds.Add(resolved.Id);
                    }
                    else
                    {
                        res.RejectedIds.Add(qId);
                        res.RejectionReasons[qId] = $"Identifier '{qId}' not present in authoritative catalog for {normalizedModule}";
                    }
                }
            }

            // Separate eligible vs ineligible vs already optimal for the requested scope
            var itemsToExecute = new List<UniversalOptimizationItemDto>();

            foreach (var item in allPlanItems)
            {
                bool isRequested = selectedSet == null ||
                                   resolvedRequestedItemIds.Contains(item.Id) ||
                                   selectedSet.Contains(item.Id) ||
                                   selectedSet.Contains(item.Name);

                if (item.EligibilityState == EligibilityState.ELIGIBLE)
                {
                    if (isRequested)
                    {
                        res.ResolvedIds.Add(item.Id);
                        item.SelectionState = SelectionState.SELECTED;
                        itemsToExecute.Add(item);
                    }
                    else
                    {
                        item.SelectionState = SelectionState.USER_EXCLUDED;
                        item.FinalResult = FinalResultState.SKIPPED_BY_USER;
                        if (string.IsNullOrWhiteSpace(item.Reason))
                            item.Reason = "Excluded by user selection";
                        res.ExcludedOperations.Add(item);
                        res.ExcludedCount++;
                    }
                }
                else if (isRequested && selectedSet != null)
                {
                    // Item was explicitly requested but is NOT eligible
                    res.RejectedIds.Add(item.Id);
                    string reason = !string.IsNullOrWhiteSpace(item.Reason) ? item.Reason : "Item is not eligible for execution on this configuration";
                    res.RejectionReasons[item.Id] = reason;

                    if (item.EligibilityState == EligibilityState.ALREADY_OPTIMIZED)
                    {
                        res.AlreadyOptimizedCount++;
                    }
                    else if (item.EligibilityState == EligibilityState.PROTECTED_BY_POLICY)
                    {
                        res.ProtectedCount++;
                    }
                    else if (item.EligibilityState == EligibilityState.UNSUPPORTED || item.EligibilityState == EligibilityState.NOT_APPLICABLE)
                    {
                        res.UnsupportedCount++;
                    }
                    else if (item.EligibilityState == EligibilityState.READ_ONLY_AUDIT)
                    {
                        res.AuditCount++;
                    }
                    // Non-eligible items are NEVER added to res.Operations
                }
            }

            res.AttemptedIds = itemsToExecute.Select(i => i.Id).ToList();
            res.SelectedCount = itemsToExecute.Count;

            // Check if 0 eligible operations need execution
            if (itemsToExecute.Count == 0)
            {
                sw.Stop();
                res.DurationMs = sw.ElapsedMilliseconds;

                bool hasAudits = res.AuditCount > 0;
                if (hasAudits && res.AlreadyOptimizedCount == 0 && res.ProtectedCount == 0)
                {
                    res.Success = true;
                    res.Status = "NO_ACTIONABLE_OPTIMIZATIONS";
                    res.Message = "NO ACTIONABLE OPTIMIZATIONS • Read-only diagnostic audits performed without firmware mutations";
                    res.Error = null;
                    return res;
                }

                if (res.AlreadyOptimizedCount > 0 || res.ProtectedCount > 0)
                {
                    res.Success = true;
                    res.Status = "ALREADY_OPTIMAL";
                    res.VerifiedCount = 0;
                    res.SelectedCount = 0;
                    res.AttemptedCount = 0;
                    res.ExcludedCount = 0;
                    res.Message = res.ProtectedCount > 0
                        ? $"All {res.AlreadyOptimizedCount} actionable operations are already in their desired optimal state ({res.ProtectedCount} protected by system security policy)."
                        : $"All {res.AlreadyOptimizedCount} operations are already in their desired optimal state.";
                    res.Error = null;
                    return res;
                }

                res.Success = false;
                res.Status = "NO_ELIGIBLE_OPERATIONS";
                res.Message = "NO ELIGIBLE OPTIMIZATIONS";
                res.Error = freshPlan.EligibleCount == 0
                    ? "No eligible optimizations found for this machine configuration."
                    : "No eligible optimizations were selected for execution.";
                return res;
            }

            // Phase A.5: Authoritative Backend Execution Gating & Duplicate Prevention
            var validatedItems = new List<UniversalOptimizationItemDto>();
            foreach (var item in itemsToExecute)
            {
                string actionType = !string.IsNullOrEmpty(item.Action)
                    ? item.Action
                    : (normalizedModule.Equals("Debloat", StringComparison.OrdinalIgnoreCase) ? "RemoveAppxPackage" : (item.Scope == "UEFI" ? "BiosSafe" : "SetRegistryValue"));
                string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);

                // 1. Defender / Security policy protection gate
                var (isBlockedByDefender, defenderReason) = OptimizationSettingOwnership.Instance.EvaluateDefenderProtection(canTarget);
                if (isBlockedByDefender)
                {
                    res.RejectedIds.Add(item.Id);
                    res.RejectionReasons[item.Id] = defenderReason;
                    res.ProtectedCount++;
                    continue;
                }

                // 2. Global System Setting Registry validation gate
                if (!GlobalSystemSettingRegistry.Instance.ValidateExecution(normalizedModule, canTarget, item.TargetValue, out string regRejection))
                {
                    res.RejectedIds.Add(item.Id);
                    res.RejectionReasons[item.Id] = regRejection;
                    res.IneligibleCount++;
                    continue;
                }

                // 3. Global Applied State Manager duplicate / conflict gate
                if (!GlobalAppliedStateManager.Instance.CanExecute(normalizedModule, canTarget, item.TargetValue, out string rej))
                {
                    res.RejectedIds.Add(item.Id);
                    res.RejectionReasons[item.Id] = rej;
                    if (rej.Contains("already optimal", StringComparison.OrdinalIgnoreCase))
                    {
                        GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                            canTarget,
                            item.Id,
                            normalizedModule,
                            normalizedModule,
                            item.CurrentValue,
                            item.TargetValue,
                            actionType);
                        res.AlreadyOptimizedCount++;
                    }
                    else if (rej.Contains("protected", StringComparison.OrdinalIgnoreCase))
                    {
                        res.ProtectedCount++;
                    }
                    else
                    {
                        res.IneligibleCount++;
                    }
                    continue;
                }

                // 4. Live current-value readback gate: if live value equals target value, do not re-execute!
                var globalStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, item.TargetValue, out var liveObserved, item.CurrentValue);
                if (!string.IsNullOrWhiteSpace(liveObserved))
                {
                    item.CurrentValue = liveObserved;
                }

                if (globalStatus == GlobalAppliedStatus.ALREADY_APPLIED ||
                    globalStatus == GlobalAppliedStatus.ALREADY_OPTIMAL ||
                    globalStatus == GlobalAppliedStatus.ACTIVE_PROFILE ||
                    IsAlreadyOptimal(item.CurrentValue, item.TargetValue))
                {
                    GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                        canTarget,
                        item.Id,
                        normalizedModule,
                        normalizedModule,
                        item.CurrentValue,
                        item.TargetValue,
                        actionType);
                    res.RejectedIds.Add(item.Id);
                    res.RejectionReasons[item.Id] = $"Item '{item.Name}' is already in its optimal state (Current: '{item.CurrentValue}').";
                    res.AlreadyOptimizedCount++;
                    continue;
                }

                validatedItems.Add(item);
            }
            itemsToExecute = validatedItems;

            // Capture exact pre-execution rejected counts so Phase D never double-counts Phase C outcomes
            int preExecAlreadyOpt = res.AlreadyOptimizedCount;
            int preExecProtected = res.ProtectedCount;
            int preExecUnsupported = res.UnsupportedCount;
            int preExecAudit = res.AuditCount;
            int preExecIneligible = res.IneligibleCount;

            if (itemsToExecute.Count == 0)
            {
                sw.Stop();
                res.DurationMs = sw.ElapsedMilliseconds;
                res.Success = true;
                res.Status = res.AlreadyOptimizedCount > 0 ? "ALREADY_OPTIMAL" : "NO_ELIGIBLE_OPERATIONS";
                res.VerifiedCount = 0;
                res.SelectedCount = 0;
                res.AttemptedCount = 0;
                res.ExcludedCount = 0;
                res.SkippedCount = preExecProtected + preExecUnsupported + preExecAudit + preExecIneligible;
                res.Message = res.AlreadyOptimizedCount > 0
                    ? $"All {res.AlreadyOptimizedCount} operations are already in their desired optimal state."
                    : "No eligible operations remain for execution.";
                res.Error = null;
                return res;
            }

            // Phase B: Pre-optimization Application Vault Backup
            if (!dryRun)
            {
                try
                {
                    var backupTx = _backupManager.CreateApplicationSnapshot(
                        $"Pre-{normalizedModule} Optimization Backup",
                        $"Automatic snapshot before executing {itemsToExecute.Count} verified operations");
                    res.TransactionId = backupTx.TransactionId;
                }
                catch (Exception bkEx)
                {
                    sw.Stop();
                    res.DurationMs = sw.ElapsedMilliseconds;
                    res.Success = false;
                    res.Status = "FAILED";
                    res.AbortedBeforeExecutionCount = itemsToExecute.Count;
                    res.NotExecutedCount = itemsToExecute.Count;
                    res.SkippedCount = res.ExcludedCount + res.IneligibleCount + res.NotExecutedCount;
                    res.Error = $"Pre-optimization backup failed: {bkEx.Message}";
                    res.Message = "OPTIMIZATION FAILED • Backup could not be verified";
                    return res;
                }
            }

            // Phase C: Native Execution & Readback Verification
            res.AttemptedCount = itemsToExecute.Count;
            res.ExecutionAttempts = itemsToExecute.Count;
            var targetIds = new HashSet<string>(itemsToExecute.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);

            if (normalizedModule.Equals("RegistryIntelligence", StringComparison.OrdinalIgnoreCase))
            {
                var tweakEngine = new RegistryTweakEngine(_backupManager);
                var tweakRes = await tweakEngine.ExecuteRegistryTweaksAsync(targetIds.ToList()).ConfigureAwait(false);

                var opMap = (tweakRes.Operations ?? new List<RegistryTweakOperationResult>())
                    .ToDictionary(o => o.Id, o => o, StringComparer.OrdinalIgnoreCase);

                foreach (var item in itemsToExecute)
                {
                    if (opMap.TryGetValue(item.Id, out var op))
                    {
                        if (op.Applied && op.ReadbackVerified)
                        {
                            item.ExecutionState = ExecutionState.VERIFIED;
                            item.FinalResult = FinalResultState.APPLIED_AND_VERIFIED;
                            item.Reason = string.IsNullOrWhiteSpace(op.Detail) ? (op.Verification ?? "Applied and verified via registry readback") : op.Detail;
                            res.AppliedCount++;
                            res.VerifiedCount++;

                            string actionType = !string.IsNullOrEmpty(item.Action) ? item.Action : "SetRegistryValue";
                            string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                            string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);
                            GlobalAppliedStateManager.Instance.RecordApplied(
                                canTarget,
                                item.Id,
                                "RegistryIntelligence",
                                "RegistryIntelligence",
                                item.CurrentValue,
                                item.TargetValue,
                                verified: true,
                                rollbackAvailable: true,
                                res.TransactionId ?? string.Empty,
                                actionType);
                        }
                        else if (op.AlreadyDesired || op.AlreadyOptimized || op.State.Equals("ALREADY_OPTIMAL", StringComparison.OrdinalIgnoreCase))
                        {
                            item.ExecutionState = ExecutionState.APPLIED;
                            item.FinalResult = FinalResultState.ALREADY_OPTIMIZED;
                            item.Reason = "Already in optimal state";

                            string actionType = !string.IsNullOrEmpty(item.Action) ? item.Action : "SetRegistryValue";
                            string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                            string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);
                            GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                                canTarget,
                                item.Id,
                                "RegistryIntelligence",
                                "RegistryIntelligence",
                                item.CurrentValue,
                                item.TargetValue,
                                actionType);
                        }
                        else if (op.Skipped || op.State.Equals("NOT_APPLICABLE", StringComparison.OrdinalIgnoreCase) || op.State.Equals("SKIPPED", StringComparison.OrdinalIgnoreCase))
                        {
                            item.ExecutionState = ExecutionState.NOT_STARTED;
                            item.FinalResult = FinalResultState.INELIGIBLE;
                            item.Reason = op.Reason ?? op.Detail ?? "Not applicable on this system";
                            res.IneligibleCount++;
                        }
                        else if (op.Applied)
                        {
                            item.ExecutionState = ExecutionState.APPLIED;
                            item.FinalResult = FinalResultState.UNVERIFIED;
                            item.Reason = "Applied, but readback state was unconfirmed";
                            res.AppliedCount++;
                            res.UnverifiedCount++;
                        }
                        else
                        {
                            item.ExecutionState = ExecutionState.FAILED;
                            item.FinalResult = FinalResultState.FAILED;
                            item.Reason = op.FailureReason ?? op.Reason ?? "Registry write failed";
                            res.FailedCount++;
                        }
                    }
                    else
                    {
                        item.ExecutionState = ExecutionState.FAILED;
                        item.FinalResult = FinalResultState.FAILED;
                        item.Reason = "Operation was not executed by registry engine";
                        res.FailedCount++;
                    }
                    res.Operations.Add(item);
                }
            }
            else if (normalizedModule.Equals("Debloat", StringComparison.OrdinalIgnoreCase) && 
                     itemsToExecute.Count > 0 && 
                     itemsToExecute.All(i => i.Action.Equals("RemoveAppxPackage", StringComparison.OrdinalIgnoreCase) && 
                                            (i.Id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || 
                                             i.Id.StartsWith("Clipchamp.", StringComparison.OrdinalIgnoreCase) || 
                                             i.Target.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || 
                                             i.Target.StartsWith("Clipchamp.", StringComparison.OrdinalIgnoreCase))))
            {
                var debloatEngine = new DebloatEngine(_backupManager);
                var debRes = await debloatEngine.RemoveSelectedPackagesAsync(targetIds, ct).ConfigureAwait(false);

                foreach (var item in itemsToExecute)
                {
                    if (debRes.Success)
                    {
                        item.ExecutionState = ExecutionState.VERIFIED;
                        item.FinalResult = FinalResultState.APPLIED_AND_VERIFIED;
                        item.Reason = "AppX package removed and verified via package manager";
                        res.AppliedCount++;
                        res.VerifiedCount++;

                        string actionType = !string.IsNullOrEmpty(item.Action) ? item.Action : "RemoveAppxPackage";
                        string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                        string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);
                        GlobalAppliedStateManager.Instance.RecordApplied(
                            canTarget,
                            item.Id,
                            "Debloat",
                            "Debloat",
                            item.CurrentValue,
                            "Removed",
                            verified: true,
                            rollbackAvailable: true,
                            res.TransactionId ?? string.Empty,
                            actionType);
                    }
                    else
                    {
                        item.ExecutionState = ExecutionState.FAILED;
                        item.FinalResult = FinalResultState.FAILED;
                        item.Reason = string.IsNullOrWhiteSpace(debRes.Message) ? "Package removal failed" : debRes.Message;
                        res.FailedCount++;
                    }
                    res.Operations.Add(item);
                }
            }
            else
            {
                // Execute through TierEngine (or catalog direct)
                var rawResults = await _tierEngine.ApplyAsync(
                    normalizedModule,
                    new ConfirmationContext { IsConfirmed = true },
                    dryRun,
                    ct,
                    progress: null,
                    allowedItemIds: targetIds).ConfigureAwait(false);

                var rawMap = rawResults.ToDictionary(r => r.ItemId, r => r, StringComparer.OrdinalIgnoreCase);

                foreach (var item in itemsToExecute)
                {
                    if (rawMap.TryGetValue(item.Id, out var raw) || rawMap.TryGetValue(item.Name, out raw))
                    {
                        if (raw.Status == ResultStatus.Success || raw.Status == ResultStatus.Verified)
                        {
                            item.ExecutionState = ExecutionState.VERIFIED;
                            item.FinalResult = FinalResultState.APPLIED_AND_VERIFIED;
                            item.Reason = raw.Message;
                            res.AppliedCount++;
                            res.VerifiedCount++;

                            string actionType = !string.IsNullOrEmpty(item.Action) ? item.Action : (item.Scope == "UEFI" ? "BiosSafe" : "SetRegistryValue");
                            string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                            string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);
                            GlobalAppliedStateManager.Instance.RecordApplied(
                                canTarget,
                                item.Id,
                                normalizedModule,
                                normalizedModule,
                                item.CurrentValue,
                                item.TargetValue,
                                verified: true,
                                rollbackAvailable: true,
                                res.TransactionId ?? string.Empty,
                                actionType);
                        }
                        else if (raw.Status == ResultStatus.AlreadyOptimized)
                        {
                            item.ExecutionState = ExecutionState.APPLIED;
                            item.FinalResult = FinalResultState.ALREADY_OPTIMIZED;
                            item.Reason = raw.Message;

                            string actionType = !string.IsNullOrEmpty(item.Action) ? item.Action : (item.Scope == "UEFI" ? "BiosSafe" : "SetRegistryValue");
                            string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                            string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);
                            GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                                canTarget,
                                item.Id,
                                normalizedModule,
                                normalizedModule,
                                item.TargetValue,
                                item.TargetValue,
                                actionType);
                        }
                        else if (raw.Status == ResultStatus.Protected)
                        {
                            item.ExecutionState = ExecutionState.NOT_STARTED;
                            item.FinalResult = FinalResultState.PROTECTED;
                            item.Reason = raw.Message;
                            res.ProtectedCount++;
                        }
                        else if (raw.Status == ResultStatus.Unsupported)
                        {
                            item.ExecutionState = ExecutionState.NOT_STARTED;
                            item.FinalResult = FinalResultState.UNSUPPORTED;
                            item.Reason = raw.Message;
                            res.UnsupportedCount++;
                        }
                        else if (raw.Status == ResultStatus.Audit)
                        {
                            item.ExecutionState = ExecutionState.NOT_STARTED;
                            item.FinalResult = FinalResultState.READ_ONLY_AUDIT;
                            item.Reason = raw.Message;
                            res.AuditCount++;
                        }
                        else if (raw.Status == ResultStatus.Blocked)
                        {
                            item.ExecutionState = ExecutionState.NOT_STARTED;
                            item.FinalResult = FinalResultState.PROTECTED;
                            item.Reason = raw.Message;
                            res.ProtectedCount++;
                        }
                        else if (raw.Status == ResultStatus.NotApplicable || raw.Status == ResultStatus.NotAvailable)
                        {
                            item.ExecutionState = ExecutionState.NOT_STARTED;
                            item.FinalResult = FinalResultState.UNSUPPORTED;
                            item.Reason = raw.Message;
                            res.UnsupportedCount++;
                        }
                        else if (raw.Status == ResultStatus.Failed)
                        {
                            item.ExecutionState = ExecutionState.FAILED;
                            item.FinalResult = FinalResultState.FAILED;
                            item.Reason = raw.Message;
                        }
                        else
                        {
                            item.ExecutionState = ExecutionState.UNVERIFIED;
                            item.FinalResult = FinalResultState.UNVERIFIED;
                            item.Reason = raw.Message;
                        }
                    }
                    else
                    {
                        // Direct fallback to catalog metadata execution if registered
                        var meta = OptimizationCatalog.Instance.GetById(item.Id);
                        if (meta?.Execute != null)
                        {
                            var (success, msg, _) = meta.Execute(dryRun);
                            bool verified = false;
                            try { verified = meta.Verify != null && meta.Verify(); } catch { }

                            if (success && (verified || dryRun))
                            {
                                item.ExecutionState = ExecutionState.VERIFIED;
                                item.FinalResult = FinalResultState.APPLIED_AND_VERIFIED;
                                item.Reason = verified ? "Verified readback confirmed" : msg;
                                res.AppliedCount++;
                                res.VerifiedCount++;

                                string actionType = !string.IsNullOrEmpty(item.Action) ? item.Action : (item.Scope == "UEFI" ? "BiosSafe" : "SetRegistryValue");
                                string rawTarget = !string.IsNullOrEmpty(item.Target) ? item.Target : item.Id;
                                string canTarget = GlobalOptimizationRegistry.NormalizeTarget(actionType, rawTarget);
                                GlobalAppliedStateManager.Instance.RecordApplied(
                                    canTarget,
                                    item.Id,
                                    normalizedModule,
                                    normalizedModule,
                                    item.CurrentValue,
                                    item.TargetValue,
                                    verified: verified,
                                    rollbackAvailable: true,
                                    res.TransactionId ?? string.Empty,
                                    actionType);
                            }
                            else if (success)
                            {
                                item.ExecutionState = ExecutionState.APPLIED;
                                item.FinalResult = FinalResultState.UNVERIFIED;
                                item.Reason = "Applied without verified readback confirmation";
                                res.AppliedCount++;
                                res.UnverifiedCount++;
                            }
                            else
                            {
                                item.ExecutionState = ExecutionState.FAILED;
                                item.FinalResult = FinalResultState.FAILED;
                                item.Reason = msg;
                                res.FailedCount++;
                            }
                        }
                        else
                        {
                            item.ExecutionState = ExecutionState.FAILED;
                            item.FinalResult = FinalResultState.FAILED;
                            item.Reason = "No execution handler reached target";
                            res.FailedCount++;
                        }
                    }
                    res.Operations.Add(item);
                }
            }

            sw.Stop();
            res.DurationMs = sw.ElapsedMilliseconds;

            // Strict invariant recalculation across mutually exclusive outcome categories (zero double-counting!)
            res.VerifiedCount = res.Operations.Count(o => o.FinalResult == FinalResultState.APPLIED_AND_VERIFIED);
            res.AlreadyOptimizedCount = preExecAlreadyOpt + res.Operations.Count(o => o.FinalResult == FinalResultState.ALREADY_OPTIMIZED);
            res.ProtectedCount = preExecProtected + res.Operations.Count(o => o.FinalResult == FinalResultState.PROTECTED);
            res.UnsupportedCount = preExecUnsupported + res.Operations.Count(o => o.FinalResult == FinalResultState.UNSUPPORTED);
            res.AuditCount = preExecAudit + res.Operations.Count(o => o.FinalResult == FinalResultState.READ_ONLY_AUDIT || o.ExecutionType == "READ_ONLY_AUDIT");
            res.FailedCount = res.Operations.Count(o => o.FinalResult == FinalResultState.FAILED);
            res.UnverifiedCount = res.Operations.Count(o => o.FinalResult == FinalResultState.UNVERIFIED);
            res.SkippedCount = preExecProtected + preExecUnsupported + preExecAudit + preExecIneligible + res.Operations.Count(o =>
                o.FinalResult == FinalResultState.SKIPPED_BY_USER ||
                o.FinalResult == FinalResultState.DEFERRED ||
                o.FinalResult == FinalResultState.INELIGIBLE ||
                o.FinalResult == FinalResultState.UNSUPPORTED ||
                o.FinalResult == FinalResultState.PROTECTED ||
                o.FinalResult == FinalResultState.REQUIRES_ELEVATION ||
                o.FinalResult == FinalResultState.ABORTED ||
                o.FinalResult == FinalResultState.READ_ONLY_AUDIT);
            res.AppliedCount = res.VerifiedCount + res.UnverifiedCount;
            res.NotExecutedCount = Math.Max(0, res.SelectedCount - res.AttemptedCount);

            // Phase D: Shared Result Aggregation
            int successfulOps = res.VerifiedCount + res.AlreadyOptimizedCount;

            bool allAudits = res.Operations.Count > 0 && res.Operations.All(o => 
                o.FinalResult == FinalResultState.READ_ONLY_AUDIT || 
                o.ExecutionType == "READ_ONLY_AUDIT" ||
                o.FinalResult == FinalResultState.ALREADY_OPTIMIZED ||
                o.FinalResult == FinalResultState.PROTECTED ||
                o.FinalResult == FinalResultState.UNSUPPORTED) &&
                res.Operations.Any(o => o.FinalResult == FinalResultState.READ_ONLY_AUDIT || o.ExecutionType == "READ_ONLY_AUDIT") &&
                res.VerifiedCount == 0 && res.FailedCount == 0 && res.UnverifiedCount == 0 && res.AlreadyOptimizedCount == 0;

            bool allOptimal = res.Operations.Count > 0 && 
                             res.FailedCount == 0 && 
                             res.UnverifiedCount == 0 && 
                             res.VerifiedCount == 0 &&
                             (res.AlreadyOptimizedCount > 0 || res.ProtectedCount > 0);

            if (allAudits)
            {
                res.Success = true;
                res.Status = "NO_ACTIONABLE_OPTIMIZATIONS";
                res.Message = "NO ACTIONABLE OPTIMIZATIONS • Read-only diagnostic audits performed without firmware mutations";
            }
            else if (allOptimal)
            {
                res.Success = true;
                res.Status = "ALREADY_OPTIMAL";
                res.SelectedCount = 0;
                res.AttemptedCount = 0;
                res.ExcludedCount = 0;
                res.Message = res.ProtectedCount > 0
                    ? $"All {res.AlreadyOptimizedCount} actionable operations are already in their desired optimal state ({res.ProtectedCount} protected by system security policy)."
                    : $"All {res.AlreadyOptimizedCount} operations are already in their desired optimal state.";
            }
            else if (res.FailedCount == 0 && res.UnverifiedCount == 0 && (successfulOps + res.ProtectedCount + res.UnsupportedCount >= res.SelectedCount || (res.SelectedCount == 0 && res.AlreadyOptimizedCount > 0)))
            {
                res.Success = true;
                res.Status = "SUCCESS";
                if (res.AlreadyOptimizedCount > 0 && res.VerifiedCount > 0)
                {
                    res.Message = $"{res.VerifiedCount} APPLIED & VERIFIED • {res.AlreadyOptimizedCount} ALREADY OPTIMAL" + (res.ProtectedCount > 0 ? $" • {res.ProtectedCount} PROTECTED" : "");
                }
                else if (res.VerifiedCount > 0)
                {
                    res.Message = $"{res.VerifiedCount} OF {res.SelectedCount} ELIGIBLE OPERATIONS VERIFIED" + (res.ProtectedCount > 0 ? $" • {res.ProtectedCount} PROTECTED" : "");
                }
                else
                {
                    res.Message = $"All {res.AlreadyOptimizedCount} operations are already optimal" + (res.ProtectedCount > 0 ? $" • {res.ProtectedCount} protected by system policy" : "");
                }
            }
            else if (successfulOps > 0)
            {
                res.Success = false;
                res.Status = "PARTIAL";
                res.Message = $"{res.VerifiedCount} VERIFIED • {res.FailedCount} FAILED • {res.ProtectedCount} PROTECTED";
            }
            else
            {
                res.Success = false;
                res.Status = "FAILED";
                res.Message = $"{res.FailedCount} FAILED • 0 VERIFIED";
            }

            return res;
        }

        // =========================================================================
        // HELPER UTILITIES
        // =========================================================================

        private static void CategorizePlanItem(UniversalOptimizationPlanDto plan, UniversalOptimizationItemDto item)
        {
            plan.TotalCatalogCount++;

            switch (item.EligibilityState)
            {
                case EligibilityState.ELIGIBLE:
                    plan.EligibleCount++;
                    // ALL ELIGIBLE IS DEFAULT SELECTION
                    item.SelectionState = SelectionState.SELECTED;
                    plan.SelectedCount++;
                    plan.Items.Add(item);
                    break;

                case EligibilityState.ALREADY_OPTIMIZED:
                case EligibilityState.ALREADY_OPTIMAL:
                    plan.AlreadyOptimizedCount++;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    // STRICT REQUIREMENT: Already optimal items must NEVER exist in available catalog (plan.Items)!
                    plan.ExcludedItems.Add(item);
                    break;

                case EligibilityState.DEFERRED:
                case EligibilityState.TEMPORARILY_UNAVAILABLE:
                    plan.DeferredCount++;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    plan.DeferredItems.Add(item);
                    plan.ExcludedItems.Add(item);
                    break;

                case EligibilityState.PROTECTED_BY_POLICY:
                case EligibilityState.UNSUPPORTED:
                case EligibilityState.READ_ONLY_AUDIT:
                case EligibilityState.RECOMMENDATION_ONLY:
                case EligibilityState.NOT_APPLICABLE:
                case EligibilityState.INELIGIBLE:
                case EligibilityState.DRIVER_REQUIREMENT_NOT_MET:
                case EligibilityState.UNSUPPORTED_HARDWARE:
                case EligibilityState.UNSUPPORTED_OS:
                default:
                    plan.IneligibleCount++;
                    item.SelectionState = SelectionState.NOT_SELECTED;
                    // STRICT REQUIREMENT: Ineligible and protected items must NEVER exist in available catalog (plan.Items)!
                    plan.ExcludedItems.Add(item);
                    break;
            }
        }

        private static void FinalizePlanCounts(UniversalOptimizationPlanDto plan)
        {
            // NeedsOptimization = Selected + UserExcluded
            // UserExcludedCount tracks only eligible items the user chose not to include; never counts AlreadyOptimal or Ineligible
            plan.UserExcludedCount = Math.Max(0, plan.EligibleCount - plan.SelectedCount);

            // Authoritative mutually exclusive counter reconciliation invariant:
            // TotalCatalogCount = EligibleCount (Actionable) + AlreadyOptimizedCount + IneligibleCount + DeferredCount
            plan.TotalCatalogCount = plan.EligibleCount + plan.AlreadyOptimizedCount + plan.IneligibleCount + plan.DeferredCount;
        }

        private static bool IsAlreadyOptimal(string current, string target)
        {
            if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(target)) return true;
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(target)) return false;
            return StateNormalizer.IsSatisfied(current, target);
        }

        private static bool IsBlockedByDefenderProtection(OptimizationEntry entry, out string reason)
        {
            if (NeverTouchServices.Contains(entry.Target))
            {
                reason = $"Blocked by policy: '{entry.Target}' is a core Windows system/security service.";
                return true;
            }
            if (entry.Target.Contains("SecurityHealthService", StringComparison.OrdinalIgnoreCase) ||
                entry.Target.Contains("WinDefend", StringComparison.OrdinalIgnoreCase) ||
                entry.Target.Contains("Sense", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Blocked: Windows Defender core protection components are protected.";
                return true;
            }
            reason = string.Empty;
            return false;
        }

        public static bool IsOsSupported(OptimizationEntry entry, EnvironmentContext env)
        {
            if (entry.Os == null || entry.Os.Count == 0) return true;
            
            int build = Environment.OSVersion.Version.Build;
            if (build < 10000)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    if (k != null)
                    {
                        var curBuild = k.GetValue("CurrentBuildNumber")?.ToString() ?? k.GetValue("CurrentBuild")?.ToString();
                        if (int.TryParse(curBuild, out int regBuild)) build = regBuild;
                    }
                }
                catch { }
            }

            bool isWin11 = env.IsWindows11 || build >= 22000;
            bool isWin10 = env.IsWindows10 || (!isWin11 && build >= 10240);

            return entry.Os.Any(os =>
            {
                if (string.IsNullOrWhiteSpace(os)) return true;
                string norm = os.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                if (norm is "all" or "universal" or "any" or "both" or "windows" or "win") return true;
                if (isWin11 && (norm is "win11" or "windows11" or "w11" or "win11+" or "21h2" or "22h2" or "23h2" or "24h2" or "25h2" or "26h2")) return true;
                if (isWin10 && (norm is "win10" or "windows10" or "w10")) return true;
                return false;
            });
        }

        private static string BuildConditionReason(List<ConditionDef>? conditions, EnvironmentContext env)
        {
            if (conditions == null || conditions.Count == 0) return "Condition failed";
            var parts = conditions.Select(c => $"{c.Type} {c.Operator} {c.Value}");
            return $"Requirements not satisfied ({string.Join(", ", parts)})";
        }
    }
}
