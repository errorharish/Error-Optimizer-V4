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
        PROTECTED,          // Windows kernel, security, active drivers, logon/shell, critical infra
        REQUIRED,           // Core OS system components, desktop window manager, explorer shell
        WORKLOAD_REQUIRED,  // Active gaming session, game launchers, anti-cheat, creative apps, licensing
        ACTIVE_APPLICATION, // Visible desktop GUI window or foreground process in active user use
        OPTIONAL,           // Secondary helper or updater with user context
        BACKGROUND,         // Pure background idle task/service without UI or active workload
        REDUCIBLE,          // Safe candidate for reduction (verified telemetry, bloat, idle helper)
        UNKNOWN             // Unidentified or unverified - defaults to safe handling (PROTECTED)
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
        public bool HasVisibleWindow { get; set; }
        public bool IsForeground { get; set; }
        public bool HasChildProcesses { get; set; }
        public List<int> ChildProcessIds { get; set; } = new();
        public string ServiceAssociation { get; set; } = string.Empty;
        public string ScheduledTaskAssociation { get; set; } = string.Empty;
        public string StartupOrigin { get; set; } = string.Empty;
        public string AppxPackageFamily { get; set; } = string.Empty;
        public string WorkloadAssociation { get; set; } = string.Empty;
        public ProcessClassification Classification { get; set; } = ProcessClassification.UNKNOWN;
        public ProcessRisk RiskLevel { get; set; } = ProcessRisk.CRITICAL_SYSTEM;
        public double SafetyConfidence { get; set; } = 0.0; // 0.0 to 1.0
        public string ClassificationReason { get; set; } = string.Empty;
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
        public int ReducibleProcessCount { get; set; }
        public int UnknownProcessCount { get; set; }
        public List<ProcessIdentityDto> Processes { get; set; } = new();
    }

    /// <summary>
    /// Candidate target for optimization across processes, services, startup, tasks, or system settings.
    /// </summary>
    public class OptimizationCandidateDto
    {
        public string CandidateId { get; set; } = string.Empty;
        public string TargetType { get; set; } = "Process"; // Process, Service, StartupEntry, ScheduledTask, AppXBackground, KernelSetting
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

        // Process identity safety checkpoint for PID verification
        public int ProcessId { get; set; }
        public DateTime ProcessStartTime { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
    }

    /// <summary>
    /// Adaptive optimization plan generated before execution.
    /// </summary>
    public class OptimizationPlanDto
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public int PassNumber { get; set; } = 1;
        public int InitialProcessCount { get; set; }
        public int EstimatedProcessReduction { get; set; }
        public int TargetSafeMinimumRangeLow { get; set; } = 70;
        public int TargetSafeMinimumRangeHigh { get; set; } = 85;
        public double EstimatedRamSavingsMb { get; set; }
        public List<OptimizationCandidateDto> Candidates { get; set; } = new();
        public List<string> WorkloadShieldsActive { get; set; } = new();
        public string SafetyNotes { get; set; } = string.Empty;
    }

    /// <summary>
    /// Individual action outcome during plan execution.
    /// </summary>
    public class OptimizationActionDtoResult
    {
        public string CandidateId { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string TargetIdentifier { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending"; // Applied, Verified, Skipped, Failed, RolledBack
        public string PreviousState { get; set; } = string.Empty;
        public string NewState { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool Verified { get; set; }
        public string VerificationMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Complete result of multi-pass maximum performance optimization.
    /// </summary>
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
        public string PassSummary { get; set; } = string.Empty;
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
        public int TotalPassesRun { get; set; }
        public int InitialProcessCount { get; set; }
        public int FinalProcessCount { get; set; }
        public int ActualProcessReduction { get; set; }
        public double InitialRamUsageMb { get; set; }
        public double FinalRamUsageMb { get; set; }
        public double RamRecoveredMb { get; set; }
        public double InitialCpuPercent { get; set; }
        public double FinalCpuPercent { get; set; }
        public int ActionsAppliedCount { get; set; }
        public int ActionsSkippedCount { get; set; }
        public int ActionsFailedCount { get; set; }
        public int ActionsVerifiedCount { get; set; }
        public string StopReason { get; set; } = string.Empty;
        public string TransactionBatchId { get; set; } = string.Empty;
        public List<OptimizationActionDtoResult> Actions { get; set; } = new();
        public List<PassProgressionDto> PassHistory { get; set; } = new();
        public SystemTuningMetricsDto SystemTuning { get; set; } = new();
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
        public int ReducibleProcessCount { get; set; }
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
    }
}
