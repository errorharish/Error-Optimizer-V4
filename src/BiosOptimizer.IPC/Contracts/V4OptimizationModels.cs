#nullable enable
using System;
using System.Collections.Generic;

namespace BiosOptimizer.IPC.Contracts
{
    /// <summary>
    /// Formal classification categories for Windows processes under V4.
    /// </summary>
    public enum ProcessClassification
    {
        // 1. Critical — Core OS kernel and critical subsystems. Never terminate.
        REQUIRED = 0,
        CRITICAL = 0,

        // 2. Security protected — Antivirus, EDR, anti-cheat, Windows Defender. Never terminate.
        PROTECTED = 1,
        SECURITY_PROTECTED = 1,

        // 3. Driver protected — Hardware drivers, audio engine, GPU management, peripheral control.
        DRIVER_PROTECTED = 2,

        // 4. Active workload — Processes associated with detected active games, rendering suites, or developer tooling.
        WORKLOAD_REQUIRED = 3,
        ACTIVE_WORKLOAD = 3,

        // 5. Active Desktop Applications — Interactive UI windows
        ACTIVE_APPLICATION = 4,

        // 6. Optional secondary utilities
        OPTIONAL = 5,

        // 7. Service dependent — Processes hosting Windows services. Preserve unless service is safely configurable.
        BACKGROUND = 6,
        SERVICE_DEPENDENT = 6,

        // 8. Conditionally eligible — Safe candidate for termination or throttling after complete safety validation.
        IMMEDIATE_REDUCIBLE = 7,
        REDUCIBLE = 7,
        CONDITIONALLY_ELIGIBLE = 7,

        // 9. Unclassified — Unknown processes. Preserve by default.
        UNKNOWN = 8,
        UNCLASSIFIED = 8,

        // 10. Already optimized — Previously optimized or throttled. No action required.
        ALREADY_OPTIMIZED = 9
    }

    /// <summary>
    /// Formal verdict returned exclusively by SafetyKernel before any process termination or service stop.
    /// </summary>
    public enum SafetyVerdict
    {
        Allow,
        AllowWithBenchmarkGate,
        Deny
    }

    /// <summary>
    /// Bugcheck codes structurally guarded against by SafetyKernel.
    /// </summary>
    public static class BugcheckCodes
    {
        public const string CRITICAL_OBJECT_TERMINATION = "0x000000F4";
        public const string STATUS_SYSTEM_PROCESS_TERMINATED = "0xC000021A";
        public const string CRITICAL_PROCESS_DIED = "0x000000EF";
        public const string DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS = "0x000000CE";
        public const string DRIVER_IRQL_NOT_LESS_OR_EQUAL = "0x000000D1";
        public const string SYSTEM_THREAD_EXCEPTION_NOT_HANDLED = "0x0000007E";
        public const string VIDEO_TDR_FAILURE = "0x00000116";
        public const string SYSTEM_SERVICE_EXCEPTION = "0x0000003B";
    }

    /// <summary>
    /// Audit log record for SafetyKernel evaluation and bugcheck tracking.
    /// </summary>
    public class ActionRecord
    {
        public string ActionKey { get; set; } = string.Empty;
        public string TargetType { get; set; } = "Process"; // Process, Service, etc.
        public string TargetIdentifier { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string ImagePath { get; set; } = string.Empty;
        public string Sha256Prefix { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string SessionId { get; set; } = string.Empty;
        public SafetyVerdict Verdict { get; set; } = SafetyVerdict.Deny;
        public string RuleCode { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Handle for Windows System Restore checkpoint.
    /// </summary>
    public class RestorePointHandle
    {
        public long SequenceNumber { get; set; }
        public string Label { get; set; } = string.Empty;
        public bool IsCreated { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Risk level for process and background optimization candidates.
    /// </summary>
    public enum ProcessRisk
    {
        CRITICAL_SYSTEM,
        ELEVATED_DRIVER,
        WORKLOAD_DEPENDENCY,
        LOW_BACKGROUND,
        SAFE_TO_TERMINATE
    }

    /// <summary>
    /// Complete identity and runtime state of a process to protect against PID reuse and naive heuristics.
    /// </summary>
    public class ProcessIdentityDto
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public bool IsDigitallySigned { get; set; }
        public string SignerName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public int ParentProcessId { get; set; }
        public string ParentProcessName { get; set; } = string.Empty;
        public int SessionId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public double CpuUsagePercent { get; set; }
        public double RamWorkingSetMb { get; set; }
        public double PrivateMemoryMb { get; set; }
        public double PeakWorkingSetMb { get; set; }
        public bool IsPersistentBackgroundConsumer { get; set; }
        public bool IsDuplicateInstance { get; set; }
        public bool IsRedundantLauncherOrUpdater { get; set; }
        public bool HasVisibleWindow { get; set; }
        public bool IsForeground { get; set; }
        public bool HasChildProcesses { get; set; }
        public List<int> ChildProcessIds { get; set; } = new();
        public string ServiceAssociation { get; set; } = string.Empty;
        public string ScheduledTaskAssociation { get; set; } = string.Empty;
        public string StartupOrigin { get; set; } = string.Empty;
        public string AppxPackageFamily { get; set; } = string.Empty;
        public string WorkloadAssociation { get; set; } = string.Empty;
        public List<string> LoadedModules { get; set; } = new();
        public double GpuUsagePercent { get; set; }
        public bool HasKernelCallback { get; set; }
        public ProcessClassification Classification { get; set; } = ProcessClassification.UNKNOWN;
        public ProcessRisk RiskLevel { get; set; } = ProcessRisk.CRITICAL_SYSTEM;
        public double SafetyConfidence { get; set; } = 0.0; // 0.0 to 1.0
        public string ClassificationReason { get; set; } = string.Empty;

        // Stage 2: Authoritative Process Provenance & Diagnostic Metadata
        public string ExecutablePathResolutionMethod { get; set; } = "Unresolved"; // ManagedMainModule, NativeQueryFullProcessImageName, AccessDenied, ProcessExited, InvalidHandle, Unavailable
        public string ExecutablePathDiagnostic { get; set; } = string.Empty;
        public string AppxPackageStatus { get; set; } = "Unavailable"; // Packaged, NonPackaged, Unavailable, Unsupported
        public List<ScheduledTaskProvenanceDto> ScheduledTaskAssociations { get; set; } = new();
        public List<StartupProvenanceDto> StartupOrigins { get; set; } = new();
        public string ProvenanceCompleteness { get; set; } = "Partial"; // Complete, Partial, Minimal
        public string ProtectionRuleCode { get; set; } = string.Empty;
        public string ProtectionDecision { get; set; } = string.Empty; // Protected, Allowed, FailClosed
        public List<string> MissingProvenanceFields { get; set; } = new();
        public string ClassificationFailureReason { get; set; } = string.Empty;
        public bool IsVerifiedSelfOrDescendant { get; set; }
        public bool IsShieldedDescendant { get; set; }
    }

    public class ScheduledTaskProvenanceDto
    {
        public string TaskPath { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public string ExecutableCommand { get; set; } = string.Empty;
        public string NormalizedExecutablePath { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string Author { get; set; } = string.Empty;
    }

    public class StartupProvenanceDto
    {
        public string EntryName { get; set; } = string.Empty;
        public string OriginalCommand { get; set; } = string.Empty;
        public string ResolvedExecutablePath { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty; // HKCU:Run, HKLM:Run, HKLM:RunOnce, WOW6432Node:Run, Folder:UserStartup, Folder:CommonStartup, UWP:StartupTask
        public string Scope { get; set; } = string.Empty; // User, Machine
        public string Publisher { get; set; } = string.Empty;
        public string EnabledStatus { get; set; } = "Unknown"; // Enabled, Disabled, Unknown
    }

    public class ProvenanceDiagnosticsDto
    {
        public int TotalProcessesInspected { get; set; }
        public int ExecutablePathsResolved { get; set; }
        public int ExecutablePathsResolvedNativeFallback { get; set; }
        public int ExecutablePathsUnresolved { get; set; }
        public int AppxAssociationsDiscovered { get; set; }
        public int ScheduledTaskAssociationsDiscovered { get; set; }
        public int StartupAssociationsDiscovered { get; set; }
        public int ParentChildRelationshipsVerified { get; set; }
        public int ShieldedDescendantsIdentified { get; set; }
        public int SelfProtectionChecksPassed { get; set; }
        public int PidReuseValidationFailures { get; set; }
        public int UnknownClassifications { get; set; }
        public Dictionary<string, int> ProtectionReasonsByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Formal verification and action lifecycle states for V4.
    /// </summary>
    public static class OptimizationActionStates
    {
        public const string PLANNED = "PLANNED";
        public const string EXECUTED = "EXECUTED";
        public const string VERIFIED = "VERIFIED";
        public const string FAILED = "FAILED";
        public const string NO_EFFECT = "NO_EFFECT";
        public const string ROLLED_BACK = "ROLLED_BACK";
        public const string SKIPPED_FOR_SAFETY = "SKIPPED_FOR_SAFETY";
        public const string SKIPPED_PROTECTED = "SKIPPED — PROTECTED APPLICATION";
        public const string ALREADY_OPTIMAL = "ALREADY_OPTIMAL";
        public const string DIAGNOSTIC_AUDIT = "DIAGNOSTIC_AUDIT";
    }

    /// <summary>
    /// Summary of a protected application/vendor group exposed in V4 advanced details.
    /// </summary>
    public class ProtectedApplicationSummaryDto
    {
        public string VendorId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string CategoryLabel { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ProtectionStatus { get; set; } = "Protected (Monitor = Yes, Optimize = No)";

        public string Category
        {
            get => CategoryLabel;
            set => CategoryLabel = value;
        }

        public string Status
        {
            get => ProtectionStatus;
            set => ProtectionStatus = value;
        }
    }

    /// <summary>
    /// Comprehensive system process snapshot.
    /// </summary>
    public class ProcessSnapshotDto
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int TotalProcessCount { get; set; }
        public double TotalRamMb { get; set; }
        public double FreeRamMb { get; set; }
        public double UsedRamMb { get; set; }
        public double CpuUtilizationPercent { get; set; }
        public int ProtectedProcessCount { get; set; }
        public int RequiredProcessCount { get; set; }
        public int WorkloadProcessCount { get; set; }
        public int ActiveAppProcessCount { get; set; }
        public int OptionalProcessCount { get; set; }
        public int BackgroundProcessCount { get; set; }
        public int ReducibleProcessCount { get; set; }
        public int UnknownProcessCount { get; set; }
        public List<ProcessIdentityDto> Processes { get; set; } = new();
        public ProvenanceDiagnosticsDto ProvenanceDiagnostics { get; set; } = new();

        public string CandidateAccountingSummary =>
            $"{TotalProcessCount} total ({ProtectedProcessCount} protected, {RequiredProcessCount} required, {WorkloadProcessCount} workload, {ActiveAppProcessCount} active apps, {OptionalProcessCount} optional, {BackgroundProcessCount} background, {ReducibleProcessCount} reducible, {UnknownProcessCount} unknown)";
    }

    public enum WorkloadProfileType
    {
        Unified,
        Gaming,
        VideoEditing,
        Streaming,
        Emulator,
        GeneralPerformance
    }

    /// <summary>
    /// Explicit connection and discovery states for Android emulators (MSI App Player 5, BlueStacks).
    /// </summary>
    public enum EmulatorConnectionState
    {
        EmulatorNotInstalled = 0,
        EmulatorInstalledStopped = 1,
        EmulatorRunningAdbDisconnected = 2,
        AdbConnectedGuestInventoryUnavailable = 3,
        GuestAvailableAndReady = 4,
        OperationUnsupportedByGuest = 5,

        // 14 explicit states for ADB & Guest Lifecycle parity
        NOT_INSTALLED = 0,
        INSTALLED_NOT_RUNNING = 1,
        EMULATOR_STARTING = 10,
        RUNNING_ADB_DISCONNECTED = 2,
        ADB_SERVER_STARTING = 11,
        ADB_CONNECTING = 12,
        ADB_CONNECTED = 13,
        ANDROID_BOOTING = 14,
        ANDROID_READY = 4,
        ADB_UNAUTHORIZED = 15,
        ADB_OFFLINE = 16,
        ADB_CONNECTION_ERROR = 17,
        EMULATOR_STOPPING = 18,
        DISCONNECTED = 19
    }

    /// <summary>
    /// Telemetry and capability inventory discovered directly from the Android guest OS.
    /// </summary>
    public class GuestSystemInfoDto
    {
        public string AndroidVersion { get; set; } = string.Empty;
        public int SdkLevel { get; set; }
        public string DeviceModel { get; set; } = string.Empty;
        public string CpuAbi { get; set; } = string.Empty;
        public string RootMode { get; set; } = "None";
        public bool FreeFireInstalled { get; set; }
        public string FreeFirePackage { get; set; } = string.Empty;
        public bool SurfaceFlingerAvailable { get; set; }
        public bool CanSetProperties { get; set; }
        public string ActiveRenderer { get; set; } = string.Empty;
        public int ConfiguredMaxFps { get; set; } = 60;
    }

    /// <summary>
    /// Strict Eligibility Statuses for Universal Core Optimization.
    /// Only ELIGIBLE operations may be actionable in execution flows.
    /// </summary>
    public enum OptimizationEligibilityStatus
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
        RECOMMENDATION_ONLY,
        DEFERRED,
        FAILED_VALIDATION
    }

    /// <summary>
    /// Candidate target for optimization across processes, services, startup, tasks, or system settings.
    /// </summary>
    public class OptimizationCandidateDto
    {
        public string CandidateId { get; set; } = string.Empty;
        public string TargetType { get; set; } = "Process"; // Process, Service, StartupEntry, ScheduledTask, AppXBackground, KernelSetting, MemoryCache, PowerScheme
        public string TargetIdentifier { get; set; } = string.Empty; // PID or ServiceName or RegistryKey or TaskPath
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public ProcessRisk RiskLevel { get; set; } = ProcessRisk.LOW_BACKGROUND;
        public string InitialState { get; set; } = string.Empty;
        public string TargetState { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public double SafetyConfidence { get; set; } = 1.0;
        public bool IsSelected { get; set; } = true;
        public bool RequiresReboot { get; set; } = false;
        public bool IsPersistent { get; set; } = false;

        // Strict Universal Eligibility and Actionability Contract
        public OptimizationEligibilityStatus EligibilityStatus { get; set; } = OptimizationEligibilityStatus.ELIGIBLE;
        public bool IsActionable => EligibilityStatus == OptimizationEligibilityStatus.ELIGIBLE;
        public string Subcategory { get; set; } = string.Empty;
        public List<string> Dependencies { get; set; } = new();
        public List<string> Conflicts { get; set; } = new();
        public string CpuRequirements { get; set; } = "Universal (x86_64 / x64)";
        public string GpuRequirements { get; set; } = "Universal";
        public string OsRequirements { get; set; } = "Windows 10 / Windows 11 (All Builds)";
        public string DriverRequirements { get; set; } = "Standard Windows Drivers";
        public string MemoryStorageRequirements { get; set; } = "Standard Memory & Storage";
        public string WorkloadRequirements { get; set; } = "All Supported Workloads";
        public string RollbackLimitation { get; set; } = "None - Fully Reversible via Rollback Journal";
        public string EvidenceClassification { get; set; } = "RUNTIME VERIFIED";

        // Rich Workload & Candidate Metadata
        public string WorkloadCompatibility { get; set; } = "All"; // All, Gaming, VideoEditing, Streaming
        public string Classification { get; set; } = "Recommended"; // Recommended, Optional, AlreadyOptimized, Protected, Unsupported
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
        public double ExpectedBenefitScore { get; set; } = 1.0;
        public double RiskScore { get; set; } = 0.1;
        public string EvidenceQuality { get; set; } = "measured"; // measured, detected, inferred, unavailable, unknown

        // Process identity safety checkpoint for PID verification
        public int ProcessId { get; set; }
        public DateTime ProcessStartTime { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lightweight hardware profile summary for user interface display.
    /// </summary>
    public class HardwareSummaryDto
    {
        public string CpuModel { get; set; } = "Processor";
        public int PhysicalCores { get; set; } = 4;
        public int LogicalProcessors { get; set; } = 8;
        public string CpuArchitecture { get; set; } = "x64";
        public double TotalRamGb { get; set; } = 16.0;
        public double AvailableRamGb { get; set; } = 8.0;
        public double RamLoadPercent { get; set; } = 50.0;
        public string PrimaryGpuName { get; set; } = "Graphics Adapter";
        public double VramGb { get; set; } = 4.0;
        public bool SupportsHags { get; set; } = false;
        public bool IsLaptop { get; set; } = false;
        public bool IsOnBattery { get; set; } = false;
        public int BatteryPercentage { get; set; } = 100;
        public string WindowsProductName { get; set; } = "Windows 11";
        public string WindowsDisplayVersion { get; set; } = "";
        public string BottleneckSummary { get; set; } = string.Empty;
        public bool IsMemoryConstrained { get; set; } = false;
        public bool IsCpuConstrained { get; set; } = false;
        public bool IsGpuConstrained { get; set; } = false;
        public bool IsStorageConstrained { get; set; } = false;
        public string TelemetryEvidenceQuality { get; set; } = "measured";
        public string EvidenceSource { get; set; } = "Win32 GlobalMemoryStatusEx & Hardware Inspection";
    }

    /// <summary>
    /// Execution request payload for V4 Maximum Performance.
    /// </summary>
    public class V4OptimizationExecutionRequestDto
    {
        public int MaxPasses { get; set; } = 4;
        public string WorkloadProfile { get; set; } = "Unified";
        public bool IsCustomSelection { get; set; } = false;
        public List<string>? SelectedCandidateIds { get; set; }
    }

    /// <summary>
    /// Adaptive optimization plan generated before execution.
    /// </summary>
    public class OptimizationPlanDto
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public int PassNumber { get; set; } = 1;
        public string WorkloadProfile { get; set; } = "Gaming";
        public int InitialProcessCount { get; set; }
        public int EstimatedProcessReduction { get; set; }
        public int TargetSafeMinimumRangeLow { get; set; } = 70;
        public int TargetSafeMinimumRangeHigh { get; set; } = 85;
        public double EstimatedRamSavingsMb { get; set; }
        public List<OptimizationCandidateDto> Candidates { get; set; } = new();
        public List<string> WorkloadShieldsActive { get; set; } = new();
        public string SafetyNotes { get; set; } = string.Empty;
        public ProcessTargetBandDto TargetBand { get; set; } = new();
        public List<ProtectedApplicationSummaryDto> ProtectedApplications { get; set; } = new();
        public string ProtectedApplicationsNotice { get; set; } = "These applications are protected from automatic optimization.";
        public HardwareSummaryDto? HardwareSummary { get; set; }
    }

    /// <summary>
    /// Adaptive Safe-Opportunity Baseline Model.
    /// Dynamically establishes the protected operating baseline grounded in physical hardware drivers,
    /// protected Windows security subsystems, active gaming/creative workloads, and running user applications.
    /// Contains NO arbitrary numeric targets (no 50-70, 70-90, 90-120).
    /// </summary>
    public class ProcessTargetBandDto
    {
        public int StartingProcessCount { get; set; }
        public int TargetFootprintFloor { get; set; }
        public int TargetFootprintCeiling { get; set; }
        public int AdaptiveProtectedBaseline { get; set; }
        public int SafeOpportunitiesDiscovered { get; set; }
        public int SafeOpportunitiesApplied { get; set; }
        public string TargetBandDescription { get; set; } = string.Empty;
        public string TargetBandFormula { get; set; } = string.Empty;
        public bool IsWithinTargetBand { get; set; } = true;
        public bool CanReduceFurtherBeyondTarget { get; set; } = true;
        public string ContinuationRule { get; set; } = "Safe-opportunity convergence active: engine terminates when all verified safe opportunities have been addressed.";
    }

    /// <summary>
    /// Live continuous workload guard telemetry for active Gaming and Editing sessions.
    /// Shields (Optimized Baseline + Workload Processes + Dependencies) and suppresses background drift.
    /// </summary>
    public class WorkloadGuardTelemetryDto
    {
        public bool IsGuardActive { get; set; }
        public string WorkloadType { get; set; } = "None"; // "Gaming", "Editing", "None"
        public int OptimizedBaselineCount { get; set; }
        public int GenuineWorkloadProcessCount { get; set; }
        public int RequiredDependencyCount { get; set; }
        public int ExpectedWorkloadTotal => OptimizedBaselineCount + GenuineWorkloadProcessCount + RequiredDependencyCount;
        public int CurrentTotalProcessCount { get; set; }
        public int UnrelatedDriftProcessCount => Math.Max(0, CurrentTotalProcessCount - ExpectedWorkloadTotal);
        public int SuppressedDriftCount { get; set; }
        public List<string> ShieldedWorkloadNames { get; set; } = new();
        public List<string> DetectedDriftProcessNames { get; set; } = new();
        public string StatusSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Explicit result states for optimization execution.
    /// Incorporates safe-opportunity convergence semantics without arbitrary numeric targets.
    /// </summary>
    public static class OptimizationResultStates
    {
        // Section 3 Required Distinct Convergence Outcomes:
        public const string CONVERGED_WITH_IMPROVEMENT = "CONVERGED_WITH_IMPROVEMENT";
        public const string CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES = "CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES";
        public const string PARTIALLY_CONVERGED = "PARTIALLY_CONVERGED";
        public const string EXECUTION_FAILURE = "EXECUTION_FAILURE";
        public const string VERIFICATION_FAILURE = "VERIFICATION_FAILURE";
        public const string CANCELLED = "CANCELLED";
        public const string ROLLED_BACK = "ROLLED_BACK";

        // Baseline & Fallback Result States:
        public const string TARGET_REACHED_MORE_AVAILABLE = "TARGET_REACHED_MORE_AVAILABLE";
        public const string TARGET_NOT_REACHED_REDUCIBLE_EXIST = "TARGET_NOT_REACHED_REDUCIBLE_EXIST";
        public const string TARGET_NOT_REACHED_SAFE_BASELINE_REACHED = "TARGET_NOT_REACHED_SAFE_BASELINE_REACHED";
        public const string TARGET_REACHED_CONVERGED = "TARGET_REACHED_CONVERGED";
        public const string OPTIMAL_BASELINE_CONFIRMED = "OPTIMAL_BASELINE_CONFIRMED";
        public const string MAXIMUM_SAFE_REACHED = "MAXIMUM_SAFE_REACHED";
        public const string DISCOVERY_STARVED = "DISCOVERY_STARVED";
        public const string EXECUTION_BLOCKED = "EXECUTION_BLOCKED";
        public const string PARTIAL_PROGRESS = "PARTIAL_PROGRESS";
        public const string PARTIAL = "PARTIAL";
        public const string ABORTED = "ABORTED";
        public const string NO_VERIFIED_IMMEDIATE_REDUCTIONS_IDENTIFIED = "NO_VERIFIED_IMMEDIATE_REDUCTIONS_IDENTIFIED";
        public const string PERSISTENT_OPTIMIZATIONS_APPLIED = "PERSISTENT_OPTIMIZATIONS_APPLIED";
        public const string NO_EFFECT_ACTIONS_DETECTED = "NO_EFFECT_ACTIONS_DETECTED";
        public const string ERROR = "ERROR";
    }

    /// <summary>
    /// Full diagnostic audit record for an inspected process.
    /// </summary>
    public class ProcessAuditEntryDto
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public bool IsDigitallySigned { get; set; }
        public int ParentProcessId { get; set; }
        public string ParentProcessName { get; set; } = string.Empty;
        public string ServiceAssociation { get; set; } = string.Empty;
        public string StartupOrigin { get; set; } = string.Empty;
        public string ScheduledTaskAssociation { get; set; } = string.Empty;
        public string AppxPackageFamily { get; set; } = string.Empty;
        public double RamWorkingSetMb { get; set; }
        public double CpuUsagePercent { get; set; }
        public bool HasVisibleWindow { get; set; }
        public bool IsForeground { get; set; }
        public string WorkloadRelevance { get; set; } = string.Empty;
        public ProcessClassification Classification { get; set; } = ProcessClassification.UNKNOWN;
        public ProcessRisk RiskLevel { get; set; } = ProcessRisk.CRITICAL_SYSTEM;
        public string ClassificationReason { get; set; } = string.Empty;
        public string RejectionReason { get; set; } = string.Empty;
        public bool WasReduced { get; set; }
    }

    /// <summary>
    /// Individual action outcome during plan execution.
    /// </summary>
    public class OptimizationActionDtoResult
    {
        public string CandidateId { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string TargetType
        {
            get => ActionType;
            set => ActionType = value;
        }
        public string TargetIdentifier { get; set; } = string.Empty;
        public string Status { get; set; } = OptimizationActionStates.PLANNED;
        public string PreviousState { get; set; } = string.Empty;
        public string NewState { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool Verified { get; set; }
        public string VerificationMessage { get; set; } = string.Empty;
        public bool IsPersistent { get; set; }
        public string Category { get; set; } = string.Empty;
        public int ProcessImpact { get; set; }
        public double RamImpactMb { get; set; }
        public bool ActualEffectVerified { get; set; }
    }

    /// <summary>
    /// Step-by-step progression record for a single optimization pass.
    /// </summary>
    public class PassProgressionDto
    {
        public int PassNumber { get; set; }
        public int StartProcessCount { get; set; }
        public int EndProcessCount { get; set; }
        public int ProcessesReduced => Math.Max(0, StartProcessCount - EndProcessCount);
        public double RamFreedMb { get; set; }
        public double CpuLoadChangePercent { get; set; }
        public int ActionsAppliedCount { get; set; }
        public int UserProcessesCount { get; set; }
        public int ClassifiedBackgroundCount { get; set; }
        public int ProtectedProcessesCount { get; set; }
        public double CpuUsagePercent { get; set; }
        public double RamUsageMb { get; set; }
        public double CommitUsageMb { get; set; }
        public double GpuActivityPercent { get; set; }
        public double StorageIoMb { get; set; }
        public double NetworkKbps { get; set; }
        public int RemainingSafeOpportunitiesCount { get; set; }
        public string ConvergenceReason { get; set; } = string.Empty;
        public string PassSummary { get; set; } = string.Empty;
        public bool ReachedTargetBand { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Telemetry for coordinated OS system performance tuning.
    /// </summary>
    public class SystemTuningMetricsDto
    {
        public bool MmcssPriorityBoostEnabled { get; set; } = true;
        public bool NetworkThrottlingIndexDisabled { get; set; } = true;
        public bool HighPerformancePowerPlanActive { get; set; } = true;
        public double StandbyListPurgedMb { get; set; }
        public bool HagsConfigured { get; set; } = true;
        public bool Win32PrioritySeparationConfigured { get; set; } = true;
        public int SystemTuningActionsApplied { get; set; }
    }

    /// <summary>
    /// Complete result of multi-pass maximum performance optimization.
    /// </summary>
    public class OptimizationResultDto
    {
        public bool Success { get; set; }
        public string ResultState { get; set; } = OptimizationResultStates.TARGET_NOT_REACHED_SAFE_BASELINE_REACHED;
        public int TotalPassesRun { get; set; }
        public int InitialProcessCount { get; set; }
        public int FinalProcessCount { get; set; }
        private int? _actualProcessReduction;
        public int ActualProcessReduction
        {
            get => _actualProcessReduction ?? VerifiedProcessesTerminated;
            set => _actualProcessReduction = value;
        }
        public int ImmediateProcessReduction => ActualProcessReduction;
        public int OptimizerProcessReduction => VerifiedProcessesTerminated;
        public int VerifiedProcessesTerminated { get; set; }
        public int NetProcessDelta { get; set; }
        public int AmbientProcessDelta { get; set; }
        public int NaturalProcessExitsCount { get; set; }
        public int ExternalProcessesStartedCount { get; set; }
        public int HardwareParametersEvaluatedCount { get; set; }
        public int HardwareParametersModifiedCount { get; set; }
        public int HardwareParametersAlreadyOptimalCount { get; set; }
        public int HardwareParametersVerifiedCount { get; set; }
        public int HardwareParametersConvergedCount { get; set; }
        public int HardwareParametersRemainingCount { get; set; }
        public string RamMeasurementStatus { get; set; } = "BASELINE_OPTIMAL";
        public string CpuMeasurementStatus { get; set; } = "AMBIENT_BASELINE_OPTIMAL";
        public int NewProcessesObserved { get; set; }
        public int PersistentOptimizationsCount { get; set; }
        public double InitialRamUsageMb { get; set; }
        public double FinalRamUsageMb { get; set; }
        public double RamRecoveredMb { get; set; }
        public double WorkingSetTrimmedMb { get; set; }
        public double InitialCpuPercent { get; set; }
        public double FinalCpuPercent { get; set; }
        public double InitialCommitMb { get; set; }
        public double FinalCommitMb { get; set; }
        public double CommitChangeMb { get; set; }
        public double GpuActivityChangePercent { get; set; }
        public double StorageIoChangeMb { get; set; }
        public double NetworkActivityChangeKbps { get; set; }
        public string ConvergenceStatus { get; set; } = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
        public int DiscoveredSafeOpportunitiesCount { get; set; }
        public int AppliedSafeOpportunitiesCount { get; set; }
        public int RemainingSafeOpportunitiesCount { get; set; }
        public int ActionsPlannedCount { get; set; }
        public int ActionsExecutedCount { get; set; }
        public int ActionsAppliedCount { get; set; }
        public int ActionsSkippedCount { get; set; }
        public int ActionsProtectedSkippedCount { get; set; }
        public int ActionsFailedCount { get; set; }
        public int ActionsNoEffectCount { get; set; }
        public int ActionsVerifiedCount { get; set; }
        public int ActionsAlreadyOptimalCount { get; set; }
        public int GrossTerminatedCount { get; set; }
        public int NetProcessReductionCount { get; set; }
        public int ActionsBlockedCount { get; set; }
        public int ActionsRolledBackCount { get; set; }
        public bool IsOptimalBaselineConfirmed { get; set; }
        public string ProtectedApplicationsNotice { get; set; } = "Protected applications were left untouched.";
        public List<ProtectedApplicationSummaryDto> ProtectedApplications { get; set; } = new();

        // Split Action Accounting by Category (Planned / Executed / Verified / Failed)
        public int ProcessActionsPlanned { get; set; }
        public int ProcessActionsExecuted { get; set; }
        public int ProcessActionsVerified { get; set; }
        public int ProcessActionsFailed { get; set; }

        public int ServiceActionsPlanned { get; set; }
        public int ServiceActionsExecuted { get; set; }
        public int ServiceActionsVerified { get; set; }
        public int ServiceActionsFailed { get; set; }

        public int StartupActionsPlanned { get; set; }
        public int StartupActionsExecuted { get; set; }
        public int StartupActionsVerified { get; set; }
        public int StartupActionsFailed { get; set; }

        public int TaskActionsPlanned { get; set; }
        public int TaskActionsExecuted { get; set; }
        public int TaskActionsVerified { get; set; }
        public int TaskActionsFailed { get; set; }

        public int AppXActionsPlanned { get; set; }
        public int AppXActionsExecuted { get; set; }
        public int AppXActionsVerified { get; set; }
        public int AppXActionsFailed { get; set; }

        public int OemActionsPlanned { get; set; }
        public int OemActionsExecuted { get; set; }
        public int OemActionsVerified { get; set; }
        public int OemActionsFailed { get; set; }

        public int ServicesChangedCount => ServiceActionsVerified;
        public int StartupEntriesChangedCount => StartupActionsVerified;
        public int TasksChangedCount => TaskActionsVerified;
        public int AppXPoliciesChangedCount => AppXActionsVerified;
        public int OemComponentsChangedCount => OemActionsVerified;

        public int SystemTuningActionsPlanned { get; set; }
        public int SystemTuningActionsExecuted { get; set; }
        public int SystemTuningActionsVerified { get; set; }
        public int SystemTuningActionsFailed { get; set; }

        // Split Summary Counters for Honest Diagnostic Auditing
        public int CandidatesDiscovered { get; set; }
        public int ClassifiedReducible { get; set; }
        public int ClassifierRejections { get; set; }
        public int SafetyKernelRejections { get; set; }
        public int ExecutionAttempts { get; set; }
        public int VerifiedTerminations { get; set; }
        public int PersistentActionsVerified { get; set; }
        public int PersistentActionsRolledBack { get; set; }
        public int ProcessCountBefore
        {
            get => InitialProcessCount;
            set => InitialProcessCount = value;
        }
        public int ProcessCountAfter
        {
            get => FinalProcessCount;
            set => FinalProcessCount = value;
        }
        public int WorkloadsActiveCount { get; set; }
        public int WorkloadsImpairedCount { get; set; }
        public string ConnectivityStatus { get; set; } = "protected";

        public string FormattedSummaryLine =>
            $"Persistent actions: {PersistentActionsVerified} · Process terminations: {VerifiedTerminations} verified " +
            $"· Process count: {ProcessCountBefore} → {ProcessCountAfter} (net {NetProcessDelta}) " +
            $"· {CandidatesDiscovered} candidates classified, {ClassifiedReducible} reducible, {ActionsProtectedSkippedCount} skipped-protected, {SafetyKernelRejections} safety-blocked " +
            $"· Workloads: {WorkloadsActiveCount} active, {WorkloadsImpairedCount} impaired · Connectivity: {ConnectivityStatus}";

        // Candidate Accounting Breakdown
        public int TotalProcesses { get; set; }
        public int ProtectedProcesses { get; set; }
        public int RequiredProcesses { get; set; }
        public int WorkloadProcesses { get; set; }
        public int ActiveAppProcesses { get; set; }
        public int OptionalProcesses { get; set; }
        public int BackgroundProcesses { get; set; }
        public int ReducibleProcesses { get; set; }
        public int UnknownProcesses { get; set; }
        public int DiagnosticChecksCount { get; set; }
        public string CandidateAccountingSummary { get; set; } = string.Empty;

        // Process Target Band Results
        public ProcessTargetBandDto TargetBand { get; set; } = new();

        public string StopReason { get; set; } = string.Empty;
        public string TransactionBatchId { get; set; } = string.Empty;
        public Dictionary<string, string> ScopeItemStatuses { get; set; } = new();
        public Dictionary<string, string> ScopeItemExplanations { get; set; } = new();
        public int VerifiedProcessesThrottled { get; set; }
        public int TotalBackgroundProcessesEvaluated { get; set; }
        public int RestartedProcessesCount { get; set; }
        public int NewlyStartedProcessesCount { get; set; }
        public int ProtectedProcessesCount { get; set; }
        public int UnclassifiedProcessesCount { get; set; }
        public double MeasuredCpuChangePercent { get; set; }
        public List<OptimizationActionDtoResult> Actions { get; set; } = new();
        public List<ProcessAuditEntryDto> CandidateAudit { get; set; } = new();
        public List<PassProgressionDto> PassHistory { get; set; } = new();
        public SystemTuningMetricsDto SystemTuning { get; set; } = new();
        public ProvenanceDiagnosticsDto? ProvenanceDiagnostics { get; set; }
        public List<string> ActiveShields { get; set; } = new();
        public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Live metrics telemetry for dashboard.
    /// </summary>
    public class OptimizationMetricsDto
    {
        public int CurrentProcessCount { get; set; }
        public int ProtectedProcessCount { get; set; }
        public int RequiredProcessCount { get; set; }
        public int WorkloadProcessCount { get; set; }
        public int OptionalProcessCount { get; set; }
        public int ReducibleProcessCount { get; set; }
        public int UnknownProcessCount { get; set; }
        public double CpuUtilizationPercent { get; set; }
        public double RamUsedMb { get; set; }
        public double RamTotalMb { get; set; }
        public double RamAvailableMb { get; set; }
        public double RamUsagePercent => RamTotalMb > 0 ? (RamUsedMb / RamTotalMb) * 100.0 : 0;
        public bool IsGamingWorkloadActive { get; set; }
        public bool IsEditingWorkloadActive { get; set; }
        public string ActiveWorkloadSummary { get; set; } = string.Empty;
        public int SafeMinimumEstimateLow { get; set; } = 70;
        public int SafeMinimumEstimateHigh { get; set; } = 85;
        public string SafeMinimumConstraintReason { get; set; } = string.Empty;
        public ProcessTargetBandDto TargetBand { get; set; } = new();
        public WorkloadGuardTelemetryDto WorkloadGuard { get; set; } = new();
        public EmulatorMetricsDto? EmulatorMetrics { get; set; }
        public List<ProtectedApplicationSummaryDto> ProtectedApplications { get; set; } = new();
    }

    /// <summary>
    /// Live performance metrics for Android Emulators (BlueStacks 5, MSI App Player, etc.).
    /// Used for benchmark-driven optimization and regression protection.
    /// </summary>
    public class EmulatorMetricsDto
    {
        public string EmulatorName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public double Fps { get; set; }
        public double OnePercentLowFps { get; set; }
        public double FrameTimeMs { get; set; }
        public double FrameTimeJitterMs { get; set; }
        public double CpuUtilizationPercent { get; set; }
        public double GpuUtilizationPercent { get; set; }
        public double RamWorkingSetMb { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsMeasured { get; set; }
        public string MeasurementSource { get; set; } = "unavailable";
        public string? MeasurementFailureReason { get; set; }
    }

    /// <summary>
    /// Benchmark evaluation result for an emulator configuration change.
    /// </summary>
    public class EmulatorOptimizationResultDto
    {
        public bool IsOptimal { get; set; }
        public bool RollbackTriggered { get; set; }
        public string StatusSummary { get; set; } = string.Empty;
        public EmulatorMetricsDto Baseline { get; set; } = new();
        public EmulatorMetricsDto PostOptimization { get; set; } = new();
        public double FpsDelta => PostOptimization.Fps - Baseline.Fps;
        public double OnePercentLowDelta => PostOptimization.OnePercentLowFps - Baseline.OnePercentLowFps;
        public double FrameTimeDelta => Baseline.FrameTimeMs - PostOptimization.FrameTimeMs;
    }
}
