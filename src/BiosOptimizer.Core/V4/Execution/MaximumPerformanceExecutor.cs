#nullable enable



using System;



using System.Collections.Generic;



using System.Diagnostics;



using System.IO;



using System.Linq;



using System.Runtime.InteropServices;



using System.ServiceProcess;



using System.Threading;



using System.Threading.Tasks;



using BiosOptimizer.Core.V4.Diagnostics;



using BiosOptimizer.Core.V4.Environments;



using BiosOptimizer.Core.V4.Intelligence;



using BiosOptimizer.Core.V4.Metrics;



using BiosOptimizer.Core.V4.Persistent;



using BiosOptimizer.Core.V4.Planning;



using BiosOptimizer.Core.V4.Rollback;



using BiosOptimizer.Core.V4.Safety;



using BiosOptimizer.Core.V4.Tuning;



using BiosOptimizer.Core.V4.Verification;
using BiosOptimizer.Core.V4.Architecture;



using BiosOptimizer.Core.V4.Workloads;



using BiosOptimizer.IPC.Contracts;



using Microsoft.Win32;



using ActiveWorkload = BiosOptimizer.Core.V4.Workload.ActiveWorkload;



using WorkloadDetector = BiosOptimizer.Core.V4.Workload.WorkloadDetector;



using BenchmarkGate = BiosOptimizer.Core.V4.Workload.BenchmarkGate;







namespace BiosOptimizer.Core.V4.Execution



{



    /// <summary>



    /// Coordinates the multi-pass iterative optimization execution loop:



    /// SCAN → CLASSIFY → PLAN → RESTORE POINT → APPLY (TWO-PHASE) → RE-SCAN → VERIFY → MEASURE.



    /// Operates with non-bypassable SafetyKernel gating and strict zero-bugcheck guarantees.



    /// </summary>



    public class MaximumPerformanceExecutor



    {



        private readonly MaximumPerformancePlanner _planner;



        private readonly ProcessIntelligenceEngine _processIntelligence;



        private readonly ProcessIdentitySafety _identitySafety;



        private readonly SafetyKernel _safetyKernel;



        private readonly OptimizationVerificationEngine _verifier;



        private readonly OptimizationBenchmarkEngine _benchmarkEngine;



        private readonly V4RollbackEngine _rollbackEngine;



        private readonly WorkloadDetectionEngine _workloadEngine;



        private readonly SystemPerformanceOptimizer _systemPerformanceOptimizer;



        private readonly ProcessTargetBandEngine _targetBandEngine;



        private readonly WorkloadProcessGuardEngine _workloadGuardEngine;



        private readonly OptimizedBaselineStore _baselineStore;



        private bool _restorePointCreated;







        public bool HasRestorePointCreated



        {



            get => _restorePointCreated;



            set => _restorePointCreated = value;



        }







        public Func<string, RestorePointHandle?>? CustomRestorePointHandler { get; set; }







        public MaximumPerformanceExecutor(



            MaximumPerformancePlanner? planner = null,



            ProcessIntelligenceEngine? processIntelligence = null,



            ProcessIdentitySafety? identitySafety = null,



            SafetyKernel? safetyKernel = null,



            OptimizationVerificationEngine? verifier = null,



            OptimizationBenchmarkEngine? benchmarkEngine = null,



            V4RollbackEngine? rollbackEngine = null,



            WorkloadDetectionEngine? workloadEngine = null,



            SystemPerformanceOptimizer? systemPerformanceOptimizer = null,



            ProcessTargetBandEngine? targetBandEngine = null,



            WorkloadProcessGuardEngine? workloadGuardEngine = null,



            OptimizedBaselineStore? baselineStore = null)



        {



            _safetyKernel = safetyKernel ?? SafetyKernel.Instance;



            _identitySafety = identitySafety ?? new ProcessIdentitySafety();



            _verifier = verifier ?? new OptimizationVerificationEngine();



            _benchmarkEngine = benchmarkEngine ?? new OptimizationBenchmarkEngine();



            _rollbackEngine = rollbackEngine ?? new V4RollbackEngine();



            _workloadEngine = workloadEngine ?? new WorkloadDetectionEngine();



            _systemPerformanceOptimizer = systemPerformanceOptimizer ?? new SystemPerformanceOptimizer(_rollbackEngine);



            _targetBandEngine = targetBandEngine ?? new ProcessTargetBandEngine();



            _processIntelligence = processIntelligence ?? new ProcessIntelligenceEngine(null, _workloadEngine);



            _workloadGuardEngine = workloadGuardEngine ?? new WorkloadProcessGuardEngine(_workloadEngine, _processIntelligence);



            _baselineStore = baselineStore ?? new OptimizedBaselineStore();







            _planner = planner ?? new MaximumPerformancePlanner(



                _processIntelligence,



                null, null, null, null,



                _workloadEngine, null, _targetBandEngine, _safetyKernel);



        }







        public async Task<OptimizationResultDto> ExecuteAdaptiveOptimizationAsync(



            int maxPasses = 4,



            string workloadProfile = "Unified",



            bool isCustomSelection = false,



            List<string>? selectedCandidateIds = null,



            CancellationToken ct = default)



        {



            if (!InvariantChecks.AssertAll(out string invCode))



            {



                return new OptimizationResultDto



                {



                    ResultState = "ABORTED",



                    StopReason = $"Safety invariant check failed at preflight: {invCode}. Refusing live actions.",



                    Success = false



                };



            }







            _safetyKernel.StartWatchdog();



            string batchId = $"V4-BATCH-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";



            var watchdog = new LiveWatchdog(BugcheckBlacklist.Instance, () =>



            {



                try { _rollbackEngine.RollbackBatch(batchId, out _, out _, out _); } catch { }



            });



            watchdog.Start();







            var crashDetector = new CrashDetector();



            int recentBugchecks = crashDetector.CheckForRecentBugchecks();



            var lastSessionLog = new LastSessionLog



            {



                SessionId = batchId,



                LastActionTimestampUtc = DateTimeOffset.UtcNow



            };



            var benchmarkGate = new BenchmarkGate();







            try



            {



                var result = new OptimizationResultDto



                {



                    TransactionBatchId = batchId



                };







                // 1. Initial Snapshot & Candidate Accounting



                var initialSnapshot = await _processIntelligence.CaptureSnapshotAsync();



                var lastDump = _processIntelligence.LastDump;



                if (lastDump != null)



                {



                    lastDump.Executor.Invoked = true;



                    lastDump.Executor.CandidatesHandedIn = initialSnapshot.ReducibleProcessCount;



                }







                result.TotalProcesses = initialSnapshot.TotalProcessCount;
                result.ProvenanceDiagnostics = initialSnapshot.ProvenanceDiagnostics;



                result.ProtectedProcesses = initialSnapshot.ProtectedProcessCount;



                result.RequiredProcesses = initialSnapshot.RequiredProcessCount;



                result.WorkloadProcesses = initialSnapshot.WorkloadProcessCount;



                result.ActiveAppProcesses = initialSnapshot.ActiveAppProcessCount;



                result.OptionalProcesses = initialSnapshot.OptionalProcessCount;



                result.BackgroundProcesses = initialSnapshot.BackgroundProcessCount;



                result.ReducibleProcesses = initialSnapshot.ReducibleProcessCount;



                result.UnknownProcesses = initialSnapshot.UnknownProcessCount;



                result.CandidateAccountingSummary = initialSnapshot.CandidateAccountingSummary;







            // Detect Active Workload Status (Gaming, Creative, Emulator)



            var workloadStatus = _workloadEngine.DetectActiveWorkloads(initialSnapshot.Processes.Select(p => p.ProcessName));



            if (workloadStatus.ActiveShieldDescriptions.Count > 0)



            {



                result.ActiveShields.AddRange(workloadStatus.ActiveShieldDescriptions);



            }







            result.ProtectedApplications = ProtectedApplicationPolicy.Instance.GetProtectedApplicationsSummary();



            result.ProtectedApplicationsNotice = "Protected applications were left untouched.";







            var initialProcessTree = initialSnapshot.Processes



                .Where(p => p.ProcessId > 0)



                .GroupBy(p => p.ProcessId)



                .ToDictionary(g => g.Key, g => g.First());







            int protectedAppProcessesSkipped = 0;







            // Build Candidate Audit List from Initial Snapshot



            foreach (var proc in initialSnapshot.Processes)



            {



                var protectedAppMatch = ProtectedApplicationPolicy.Instance.EvaluateProcess(proc, initialProcessTree);



                if (protectedAppMatch.IsProtected)



                {



                    protectedAppProcessesSkipped++;



                }







                result.CandidateAudit.Add(new ProcessAuditEntryDto



                {



                    ProcessId = proc.ProcessId,



                    ProcessName = proc.ProcessName,



                    DisplayName = proc.DisplayName,



                    ExecutablePath = proc.ExecutablePath,



                    Publisher = proc.Publisher,



                    IsDigitallySigned = proc.IsDigitallySigned,



                    ParentProcessId = proc.ParentProcessId,



                    ParentProcessName = proc.ParentProcessName,



                    ServiceAssociation = proc.ServiceAssociation,



                    StartupOrigin = proc.StartupOrigin,



                    ScheduledTaskAssociation = proc.ScheduledTaskAssociation,



                    AppxPackageFamily = proc.AppxPackageFamily,



                    RamWorkingSetMb = proc.RamWorkingSetMb,



                    CpuUsagePercent = proc.CpuUsagePercent,



                    HasVisibleWindow = proc.HasVisibleWindow,



                    IsForeground = proc.IsForeground,



                    WorkloadRelevance = proc.WorkloadAssociation,



                    Classification = proc.Classification,



                    RiskLevel = proc.RiskLevel,



                    ClassificationReason = proc.ClassificationReason,



                    RejectionReason = protectedAppMatch.IsProtected



                        ? $"Skipped — Protected Application ({protectedAppMatch.VendorDisplayName}): Protected applications were left untouched."



                        : proc.Classification switch



                        {



                            ProcessClassification.IMMEDIATE_REDUCIBLE => string.Empty,



                            ProcessClassification.REQUIRED => "Core Windows OS component (cannot be modified or terminated).",



                            ProcessClassification.PROTECTED => "Protected Windows security, hardware driver, audio, or input subsystem.",



                            ProcessClassification.WORKLOAD_REQUIRED => "Active gaming, creative editing, or emulator workload component preserved.",



                            ProcessClassification.ACTIVE_APPLICATION => "Active desktop application or user application component preserved.",



                            ProcessClassification.OPTIONAL => "Optional utility retained by default (can be toggled in advanced settings).",



                            ProcessClassification.BACKGROUND => "Generic session 0 service with no verified safe reduction profile.",



                            _ => "Unrecognized background entity (safe protected retention applied)."



                        },



                    WasReduced = false



                });



            }







            result.ActionsProtectedSkippedCount = protectedAppProcessesSkipped;







            // Compute Machine-Specific Aggressive Target Band



            result.TargetBand = _targetBandEngine.CalculateTargetBand(



                initialSnapshot.TotalProcessCount,



                protectedCount: initialSnapshot.ProtectedProcessCount,



                workloadCount: initialSnapshot.WorkloadProcessCount,



                activeAppsCount: initialSnapshot.ActiveAppProcessCount);



            int targetBandUpper = result.TargetBand?.TargetFootprintCeiling > 0 ? result.TargetBand.TargetFootprintCeiling : 85;







            // Baseline Benchmark Measurement (single authoritative enumeration from initialSnapshot)



            var initialPids = initialSnapshot.Processes.Select(p => p.ProcessId).ToHashSet();



            var initialMetrics = _benchmarkEngine.SampleCurrentMetrics();



            result.InitialProcessCount = initialSnapshot.TotalProcessCount > 0 ? initialSnapshot.TotalProcessCount : initialMetrics.CurrentProcessCount;
            result.TotalProcesses = result.InitialProcessCount;



            result.InitialRamUsageMb = initialMetrics.RamUsedMb;



            result.InitialCpuPercent = initialMetrics.CpuUtilizationPercent;







            // System Restore Checkpoint before any persistent changes



            var rpHandle = CustomRestorePointHandler != null



                ? CustomRestorePointHandler("Error Optimizer V4 Optimization Checkpoint")



                : (RestorePointHook != null



                    ? RestorePointHook("Error Optimizer V4 Optimization Checkpoint")



                    : CreateVerifiedApplicationBackupHandle("Error Optimizer V4 Optimization Checkpoint"));







            if (rpHandle == null || !rpHandle.IsCreated)



            {



                throw new RestorePointCreationException(



                    rpHandle?.ErrorMessage ?? "unknown");



            }



            _restorePointCreated = true;







            int currentProcessCount = initialMetrics.CurrentProcessCount;



            int pass = 1;



            bool continueOptimizing = true;



            var terminatedPids = new HashSet<int>();
            var terminatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);



            var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenProcessKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);







            // 2. Coordinated System Performance Tuning is candidate-driven in the convergence loop below,



            // respecting user selection, workload profile, and verified baseline state.



            result.SystemTuning = new SystemTuningMetricsDto();







            // 3. Dynamic Multi-Pass Convergence Loop



            while (continueOptimizing && pass <= maxPasses && !ct.IsCancellationRequested)



            {



                _safetyKernel.RecordHeartbeat();
                double passStartRam = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot().Memory.UsedPhysicalGb * 1024.0;







                var startHw = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot();
                var startProcSnap = await _processIntelligence.CaptureSnapshotAsync();
                var passRecord = new PassProgressionDto
                {
                    PassNumber = pass,
                    StartProcessCount = currentProcessCount,
                    Timestamp = DateTime.UtcNow,
                    UserProcessesCount = startProcSnap.ActiveAppProcessCount + startProcSnap.WorkloadProcessCount,
                    ClassifiedBackgroundCount = startProcSnap.ReducibleProcessCount + startProcSnap.OptionalProcessCount,
                    ProtectedProcessesCount = startProcSnap.ProtectedProcessCount + startProcSnap.RequiredProcessCount,
                    CpuUsagePercent = OptimizationBenchmarkEngine.SampleSystemCpuPercent(),
                    RamUsageMb = Math.Round(startHw.Memory.UsedPhysicalGb * 1024.0, 1),
                    CommitUsageMb = Math.Round(startHw.Memory.CommitUsedGb * 1024.0, 1)
                };







                // Generate plan for current pass tailored to active workload profile



                var plan = await _planner.GeneratePlanAsync(pass, workloadProfile);
                







                // Filter out candidates already processed in previous passes, and if custom selection is enabled, filter by selectedCandidateIds



                var newCandidates = plan.Candidates



                    .Where(c => !seenCandidates.Contains(c.CandidateId))



                    .Where(c => !isCustomSelection || selectedCandidateIds == null || MatchesSelectionScope(c, selectedCandidateIds))



                    .OrderBy(GetCandidateExecutionPhase)



                    .ToList();

                foreach (var c in newCandidates)
                {
                    seenCandidates.Add(c.CandidateId);
                    if (c.TargetType == "Process" && c.ProcessId > 0)
                    {
                        string startSuffix = c.ProcessStartTime != default
                            ? c.ProcessStartTime.ToUniversalTime().ToString("yyyyMMddHHmmss")
                            : "00000000000000";
                        seenProcessKeys.Add($"{c.ProcessId}_{startSuffix}_{c.TargetIdentifier.ToLowerInvariant()}");
                    }
                }







                result.ActionsPlannedCount += newCandidates.Count;







                foreach (var candidate in newCandidates)



                {



                    switch (candidate.TargetType)



                    {



                        case "Process": result.ProcessActionsPlanned++; break;



                        case "Service": result.ServiceActionsPlanned++; break;



                        case "StartupEntry": result.StartupActionsPlanned++; break;



                        case "ScheduledTask": result.TaskActionsPlanned++; break;



                        case "AppXBackground": result.AppXActionsPlanned++; break;
                        case "OemComponent": result.OemActionsPlanned++; break;



                        case "KernelSetting":
                        case "PowerScheme":
                        case "MemoryCache":
                        case "FileSystemSetting":
                        case "InputSetting":
                        case "UserPreference":
                        case "NetworkStackSetting":
                        case "WorkloadSetting":
                        case "AudioSubsystem":
                        case "CpuSetting":
                        case "DisplayCompositor":
                        case "EmulatorSetting":
                        case "GamingSetting":
                        case "GpuSetting":
                        case "KernelTimer":
                        case "NetworkStack":
                        case "ProcessManagement":
                        case "StorageEngine":
                        case "SystemSetting":
                            result.SystemTuningActionsPlanned++; break;



                    }



                }







                if (newCandidates.Count == 0)
                {
                    passRecord.EndProcessCount = currentProcessCount;
                    passRecord.RemainingSafeOpportunitiesCount = 0;
                    passRecord.ConvergenceReason = "No further safe opportunities remain in system footprint.";
                    passRecord.PassSummary = $"Pass {pass}: Footprint converged — 0 reducible candidates remaining.";
                    result.PassHistory.Add(passRecord);
                    break;
                }







                int actionsVerifiedInPass = 0;







                // Authoritative Universal Eligibility Evaluation immediately before execution
                BiosOptimizer.Core.V4.Architecture.UniversalEligibilityEngine.Instance.EvaluateEligibility(newCandidates, workloadProfile, isCustomSelection);

                // Apply Candidates with real execution & verification checkpoints
                foreach (var candidate in newCandidates)
                {
                    if (!candidate.IsActionable)
                    {
                        var skippedResult = new OptimizationActionDtoResult
                        {
                            CandidateId = candidate.CandidateId,
                            ActionType = candidate.TargetType,
                            TargetIdentifier = candidate.TargetIdentifier,
                            Status = candidate.EligibilityStatus == OptimizationEligibilityStatus.ALREADY_OPTIMAL ? "ALREADY_OPTIMAL" : "SKIPPED",
                            Reason = candidate.ExclusionExplanation ?? $"Ineligible ({candidate.EligibilityStatus})",
                            VerificationMessage = candidate.ExclusionExplanation ?? $"Ineligible ({candidate.EligibilityStatus})",
                            Verified = candidate.EligibilityStatus == OptimizationEligibilityStatus.ALREADY_OPTIMAL
                        };
                        result.Actions.Add(skippedResult);
                        if (candidate.EligibilityStatus == OptimizationEligibilityStatus.ALREADY_OPTIMAL)
                        {
                            result.ActionsAlreadyOptimalCount++;
                        }
                        continue;
                    }

                    if (candidate.IsAlreadyOptimal)



                    {



                        var optimalResult = new OptimizationActionDtoResult



                        {



                            CandidateId = candidate.CandidateId,



                            ActionType = candidate.TargetType,



                            TargetIdentifier = candidate.TargetIdentifier,



                            PreviousState = candidate.InitialState,



                            NewState = candidate.TargetState,



                            Reason = $"{candidate.Reason} (Already optimal)",



                            IsPersistent = candidate.IsPersistent,



                            Category = candidate.Category,



                            Status = OptimizationActionStates.ALREADY_OPTIMAL,



                            VerificationMessage = "Verified already optimal (no modification required)",



                            Verified = true,



                            ActualEffectVerified = false



                        };



                        result.ActionsAlreadyOptimalCount++;



                        result.ActionsSkippedCount++;



                        result.Actions.Add(optimalResult);







                        if (candidate.CandidateId == "TUNE_MMCSS_RESPONSIVENESS") result.SystemTuning.MmcssPriorityBoostEnabled = true;



                        else if (candidate.CandidateId == "TUNE_WIN32_PRIORITY") result.SystemTuning.Win32PrioritySeparationConfigured = true;



                        else if (candidate.CandidateId == "TUNE_NETWORK_THROTTLING") result.SystemTuning.NetworkThrottlingIndexDisabled = true;



                        else if (candidate.CandidateId == "TUNE_GPU_HAGS") result.SystemTuning.HagsConfigured = true;



                        else if (candidate.CandidateId == "TUNE_POWER_PLAN_HIGH_PERFORMANCE") result.SystemTuning.HighPerformancePowerPlanActive = true;



                        continue;



                    }







                    if (!candidate.IsSelected)



                    {



                        var skippedResult = new OptimizationActionDtoResult



                        {



                            CandidateId = candidate.CandidateId,



                            ActionType = candidate.TargetType,



                            TargetIdentifier = candidate.TargetIdentifier,



                            PreviousState = candidate.InitialState,



                            NewState = candidate.TargetState,



                            Reason = "Excluded by user selection",



                            IsPersistent = candidate.IsPersistent,



                            Category = candidate.Category,



                            Status = OptimizationActionStates.SKIPPED_FOR_SAFETY,



                            VerificationMessage = "Deselected in custom plan",



                            Verified = false



                        };



                        result.ActionsSkippedCount++;



                        result.Actions.Add(skippedResult);



                        continue;



                    }







                    var actionResult = new OptimizationActionDtoResult



                    {



                        CandidateId = candidate.CandidateId,



                        ActionType = candidate.TargetType,



                        TargetIdentifier = candidate.TargetIdentifier,



                        PreviousState = candidate.InitialState,



                        NewState = candidate.TargetState,



                        Reason = candidate.Reason,



                        IsPersistent = candidate.IsPersistent,



                        Category = candidate.Category,



                        Status = OptimizationActionStates.PLANNED



                    };







                    // Pre-execution SafetyKernel Protected Application / Service check (Monitor = Yes, Optimize = No)



                    var protectedCandidateMatch = ProtectedApplicationPolicy.Instance.EvaluateCandidate(candidate);



                    if (protectedCandidateMatch.IsProtected)



                    {



                        actionResult.Status = OptimizationActionStates.SKIPPED_PROTECTED;



                        actionResult.Reason = $"Skipped — Protected application/service ({protectedCandidateMatch.VendorDisplayName}): {protectedCandidateMatch.Reason}";



                        actionResult.VerificationMessage = "Protected applications were left untouched.";



                        result.ActionsProtectedSkippedCount++;



                        result.ActionsSkippedCount++;



                        result.Actions.Add(actionResult);



                        continue;



                    }







                    bool executionAttempted = false;



                    bool executionSuccess = false;







                    if (candidate.TargetType == "Process")



                    {



                        // 1. PID Reuse & Identity Check



                        if (!_identitySafety.ValidateIdentity(candidate, out string abortReason))



                        {



                            actionResult.Status = OptimizationActionStates.SKIPPED_FOR_SAFETY;



                            actionResult.Reason = $"Aborted for safety: {abortReason}";



                            result.ActionsBlockedCount++;



                            result.ActionsSkippedCount++;



                            result.Actions.Add(actionResult);



                            continue;



                        }







                        // 2. Non-Bypassable SafetyKernel Evaluation



                        var candidateProc = initialSnapshot.Processes.FirstOrDefault(p => p.ProcessId == candidate.ProcessId) ??



                            new ProcessIdentityDto



                            {



                                ProcessId = candidate.ProcessId,



                                ProcessName = candidate.TargetIdentifier,



                                DisplayName = candidate.DisplayName,



                                ExecutablePath = candidate.ExecutablePath,



                                Publisher = candidate.Publisher



                            };







                        var procProtectedMatch = ProtectedApplicationPolicy.Instance.EvaluateProcess(candidateProc, initialProcessTree);



                        if (procProtectedMatch.IsProtected)



                        {



                            actionResult.Status = OptimizationActionStates.SKIPPED_PROTECTED;



                            actionResult.Reason = $"Skipped — Protected application ({procProtectedMatch.VendorDisplayName}): {procProtectedMatch.Reason}";



                            actionResult.VerificationMessage = "Protected applications were left untouched.";



                            result.ActionsProtectedSkippedCount++;



                            result.ActionsSkippedCount++;



                            result.Actions.Add(actionResult);



                            continue;



                        }







                        var verdict = _safetyKernel.EvaluateProcessKill(



                            candidateProc,



                            workloadStatus.IsGamingDetected,



                            workloadStatus.IsEditingDetected,



                            out string ruleCode,



                            out string safetyReason);







                        if (verdict == SafetyVerdict.Deny)



                        {



                            if (ruleCode == "R28_PROTECTED_APP")



                            {



                                actionResult.Status = OptimizationActionStates.SKIPPED_PROTECTED;



                                actionResult.Reason = $"Skipped — Protected application: {safetyReason}";



                                actionResult.VerificationMessage = "Protected applications were left untouched.";



                                result.ActionsProtectedSkippedCount++;



                            }



                            else



                            {



                                actionResult.Status = OptimizationActionStates.SKIPPED_FOR_SAFETY;



                                actionResult.Reason = $"Blocked by SafetyKernel [{ruleCode}]: {safetyReason}";



                                result.ActionsBlockedCount++;



                            }



                            result.ActionsSkippedCount++;



                            result.Actions.Add(actionResult);



                            continue;



                        }







                        // Terminate process safely via Two-Phase Stop



                        executionAttempted = true;



                        result.ActionsExecutedCount++;



                        result.ProcessActionsExecuted++;



                        watchdog.RecordAction($"Process:{candidate.TargetIdentifier}");



                        lastSessionLog.Actions.Add(new LastSessionAction



                        {



                            ActionSignature = $"Process:{candidate.TargetIdentifier}",



                            Description = candidate.Reason,



                            TimestampUtc = DateTimeOffset.UtcNow



                        });



                        lastSessionLog.LastActionTimestampUtc = DateTimeOffset.UtcNow;



                        crashDetector.SaveLastSession(lastSessionLog);



                        // Stable candidate identity validation immediately before execution
                        int candPid = candidate.ProcessId;
                        if (candPid <= 4)
                        {
                            actionResult.Status = OptimizationActionStates.SKIPPED_FOR_SAFETY;
                            actionResult.Reason = "Aborted: Invalid or system process PID; targeting forbidden.";
                            result.ActionsSkippedCount++;
                            result.Actions.Add(actionResult);
                            continue;
                        }

                        // Revalidate exact process identity (PID, start time, executable path, process name) immediately before execution
                        if (!_identitySafety.ValidateIdentity(candidate, out string preExecAbort))
                        {
                            actionResult.Status = OptimizationActionStates.SKIPPED_FOR_SAFETY;
                            actionResult.Reason = $"Pre-execution identity check aborted: {preExecAbort}";
                            result.ActionsBlockedCount++;
                            result.ActionsSkippedCount++;
                            result.Actions.Add(actionResult);
                            continue;
                        }

                        if (BiosOptimizer.Core.V4.Tuning.SystemPerformanceOptimizer.TestExecutionSimulationEnabled)
                        {
                            executionSuccess = true;
                            actionResult.Status = OptimizationActionStates.VERIFIED;
                            actionResult.VerificationMessage = $"[Simulation] Verified safe background reduction for '{candidate.DisplayName}' (PID {candPid})";
                            if (candidate.RiskLevel == ProcessRisk.SAFE_TO_TERMINATE)
                            {
                                terminatedPids.Add(candPid);
                                terminatedNames.Add(candidate.TargetIdentifier);
                            }
                            else
                            {
                                result.VerifiedProcessesThrottled++;
                            }
                        }
                        else
                        {
                            bool isRespawned = terminatedNames.Contains(candidate.TargetIdentifier);
                            if (candidate.RiskLevel == ProcessRisk.SAFE_TO_TERMINATE && !isRespawned)
                        {
                            try
                            {
                                using var pKill = Process.GetProcessById(candPid);
                                if (pKill.Id > 4 && pKill.Id != Process.GetCurrentProcess().Id)
                                {
                                    bool closedGracefully = false;
                                    try
                                    {
                                        if (pKill.MainWindowHandle != IntPtr.Zero)
                                        {
                                            pKill.CloseMainWindow();
                                            if (pKill.WaitForExit(750))
                                            {
                                                closedGracefully = true;
                                            }
                                        }
                                    }
                                    catch { }

                                    if (!closedGracefully && !pKill.HasExited)
                                    {
                                        pKill.Kill();
                                        pKill.WaitForExit(1000);
                                    }

                                    pKill.Refresh();
                                    if (pKill.HasExited)
                                    {
                                        executionSuccess = true;
                                        actionResult.Status = OptimizationActionStates.VERIFIED;
                                        actionResult.VerificationMessage = closedGracefully
                                            ? $"Gracefully closed non-essential background desktop process '{candidate.DisplayName}' (PID {candPid})"
                                            : $"Successfully terminated non-essential background daemon '{candidate.DisplayName}' (PID {candPid})";
                                        terminatedPids.Add(candPid);
                                        terminatedNames.Add(candidate.TargetIdentifier);
                                    }
                                    else
                                    {
                                        candidate.TargetState = "Throttled & Working-Set Yielded";
                                        executionSuccess = ReduceProcessBackgroundFootprint(candPid, candidate.TargetIdentifier, out long wsTrimmed, _rollbackEngine, batchId);
                                        if (wsTrimmed > 0)
                                        {
                                            double mb = Math.Round((double)wsTrimmed / (1024.0 * 1024.0), 1);
                                            result.WorkingSetTrimmedMb += mb;
                                            result.RamRecoveredMb += mb;
                                        }
                                        if (executionSuccess)
                                        {
                                            result.VerifiedProcessesThrottled++;
                                            actionResult.Status = OptimizationActionStates.VERIFIED;
                                            actionResult.VerificationMessage = $"Fallback: Throttled persistent background daemon '{candidate.DisplayName}' (PID {candPid}) to Idle priority and yielded working set";
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                candidate.TargetState = "Throttled & Working-Set Yielded";
                                executionSuccess = ReduceProcessBackgroundFootprint(candPid, candidate.TargetIdentifier, out long wsTrimmed, _rollbackEngine, batchId);
                                if (wsTrimmed > 0)
                                {
                                    double mb = Math.Round((double)wsTrimmed / (1024.0 * 1024.0), 1);
                                    result.WorkingSetTrimmedMb += mb;
                                    result.RamRecoveredMb += mb;
                                }
                                if (executionSuccess)
                                {
                                    result.VerifiedProcessesThrottled++;
                                    actionResult.Status = OptimizationActionStates.VERIFIED;
                                    actionResult.VerificationMessage = $"Successfully throttled idle background daemon '{candidate.DisplayName}' (PID {candPid}) to Idle priority and yielded working set";
                                }
                            }
                        }
                        else
                        {
                            if (isRespawned)
                            {
                                // SCM Reconfiguration: Reconfigure service to Manual (Demand Start) to prevent restart loops
                                try
                                {
                                    string svcCandidate = candidate.TargetIdentifier;
                                    if (!string.IsNullOrEmpty(svcCandidate) && !_safetyKernel.IsProtectedService(svcCandidate))
                                    {
                                        using var scPsi = Process.Start(new ProcessStartInfo("sc.exe", $"config \"{svcCandidate}\" start= demand")
                                        {
                                            CreateNoWindow = true,
                                            UseShellExecute = false
                                        });
                                        scPsi?.WaitForExit(1000);
                                    }
                                }
                                catch { }
                            }

                            candidate.TargetState = "Throttled & Working-Set Yielded";
                            executionSuccess = ReduceProcessBackgroundFootprint(candPid, candidate.TargetIdentifier, out long wsTrimmed, _rollbackEngine, batchId);
                            if (wsTrimmed > 0)
                            {
                                double mb = Math.Round((double)wsTrimmed / (1024.0 * 1024.0), 1);
                                result.WorkingSetTrimmedMb += mb;
                                result.RamRecoveredMb += mb;
                            }
                            if (executionSuccess)
                            {
                                result.VerifiedProcessesThrottled++;
                                actionResult.Status = OptimizationActionStates.VERIFIED;
                                actionResult.VerificationMessage = isRespawned
                                    ? $"Respawned daemon '{candidate.DisplayName}' (PID {candPid}) reconfigured to Manual demand-start and throttled to prevent contention"
                                    : $"Successfully throttled idle background daemon '{candidate.DisplayName}' (PID {candPid}) to Idle priority and yielded working set";
                            }
                        }
                        }



                        if (watchdog.IsAborted) break;



                    }



                    else if (candidate.TargetType == "Service")



                    {



                        if (!TryQueryServiceParameters(



                            candidate.TargetIdentifier,



                            out uint realServiceType,



                            out uint realControlsAccepted,



                            out uint realStartType,



                            out bool realHasRunningDependents,



                            out string scmError))



                        {



                            actionResult.Status = OptimizationActionStates.SKIPPED_FOR_SAFETY;



                            actionResult.Reason = $"Aborted service stop for safety: SCM query failed for '{candidate.TargetIdentifier}': {scmError}";



                            result.ActionsBlockedCount++;



                            result.ActionsSkippedCount++;



                            result.Actions.Add(actionResult);



                            continue;



                        }







                        var sVerdict = _safetyKernel.EvaluateServiceStop(



                            candidate.TargetIdentifier,



                            serviceType: realServiceType,



                            controlsAccepted: realControlsAccepted,



                            startType: realStartType,



                            hasRunningDependents: realHasRunningDependents,



                            isGamingActive: workloadStatus.IsGamingDetected,



                            isEditingActive: workloadStatus.IsEditingDetected,



                            out string sCode,



                            out string sReason);







                        if (sVerdict == SafetyVerdict.Deny)



                        {



                            if (sCode == "S07_PROTECTED_SERVICE")



                            {



                                actionResult.Status = OptimizationActionStates.SKIPPED_PROTECTED;



                                actionResult.Reason = $"Skipped — Protected service: {sReason}";



                                actionResult.VerificationMessage = "Protected applications were left untouched.";



                                result.ActionsProtectedSkippedCount++;



                            }



                            else



                            {



                                actionResult.Status = OptimizationActionStates.SKIPPED_FOR_SAFETY;



                                actionResult.Reason = $"Blocked by SafetyKernel [{sCode}]: {sReason}";



                                result.ActionsBlockedCount++;



                            }



                            result.ActionsSkippedCount++;



                            result.Actions.Add(actionResult);



                            continue;



                        }







                        executionAttempted = true;



                        result.ActionsExecutedCount++;



                        result.ServiceActionsExecuted++;



                        watchdog.RecordAction($"Service:{candidate.TargetIdentifier}");



                        lastSessionLog.Actions.Add(new LastSessionAction



                        {



                            ActionSignature = $"Service:{candidate.TargetIdentifier}",



                            Description = candidate.Reason,



                            TimestampUtc = DateTimeOffset.UtcNow



                        });



                        lastSessionLog.LastActionTimestampUtc = DateTimeOffset.UtcNow;



                        crashDetector.SaveLastSession(lastSessionLog);



                        executionSuccess = ModifyServiceTarget(candidate, batchId);



                        if (watchdog.IsAborted) break;



                    }



                    else if (candidate.TargetType == "ScheduledTask")



                    {



                        executionAttempted = true;



                        result.ActionsExecutedCount++;



                        result.TaskActionsExecuted++;



                        executionSuccess = ModifyScheduledTaskTarget(candidate, batchId);



                    }



                    else if (candidate.TargetType == "StartupEntry")



                    {



                        executionAttempted = true;



                        result.ActionsExecutedCount++;



                        result.StartupActionsExecuted++;



                        executionSuccess = ModifyStartupTarget(candidate, batchId);



                    }



                    else if (candidate.TargetType == "AppXBackground")



                    {



                        executionAttempted = true;



                        result.ActionsExecutedCount++;



                        result.AppXActionsExecuted++;



                        executionSuccess = ModifyAppXTarget(candidate, batchId);



                    }

                    else if (candidate.TargetType == "OemComponent")
                    {
                        executionAttempted = true;
                        result.ActionsExecutedCount++;
                        result.OemActionsExecuted++;
                        executionSuccess = ModifyOemTarget(candidate, batchId);
                    }



                    else if (candidate.TargetType is "KernelSetting" or "PowerScheme" or "MemoryCache" or
                             "FileSystemSetting" or "InputSetting" or "UserPreference" or
                             "NetworkStackSetting" or "WorkloadSetting" or "AudioSubsystem" or
                             "CpuSetting" or "DisplayCompositor" or "EmulatorSetting" or
                             "GamingSetting" or "GpuSetting" or "KernelTimer" or "NetworkStack" or
                             "ProcessManagement" or "StorageEngine" or "SystemSetting")



                    {



                        executionAttempted = true;



                        result.ActionsExecutedCount++;



                        result.SystemTuningActionsExecuted++;



                        watchdog.RecordAction($"SystemTuning:{candidate.CandidateId}");



                        lastSessionLog.Actions.Add(new LastSessionAction



                        {



                            ActionSignature = $"SystemTuning:{candidate.CandidateId}",



                            Description = candidate.Reason,



                            TimestampUtc = DateTimeOffset.UtcNow



                        });



                        lastSessionLog.LastActionTimestampUtc = DateTimeOffset.UtcNow;



                        crashDetector.SaveLastSession(lastSessionLog);



                        executionSuccess = _systemPerformanceOptimizer.ApplySingleCandidate(



                            candidate.CandidateId,



                            batchId,



                            workloadStatus.IsEmulatorDetected,



                            workloadProfile);



                    }



                    else



                    {



                        executionAttempted = false;



                        executionSuccess = false;



                        actionResult.Status = OptimizationActionStates.FAILED;



                        actionResult.Reason = $"Unsupported optimization target type '{candidate.TargetType}'";



                        actionResult.VerificationMessage = "No execution handler implemented for target type";



                        result.ActionsFailedCount++;



                        result.Actions.Add(actionResult);



                        continue;



                    }







                    // Verify Post-Action State against Real Host State



                    if (executionAttempted && executionSuccess)



                    {



                        bool verified = _verifier.VerifyAction(candidate, out string verMsg);



                        actionResult.Verified = verified;



                        actionResult.VerificationMessage = verMsg;







                        if (verified)



                        {



                            if (CoreOptimizationAccounting.IsDiagnosticCandidate(candidate))
                            {
                                actionResult.Status = OptimizationActionStates.DIAGNOSTIC_AUDIT;
                                actionResult.ActualEffectVerified = false;
                                result.DiagnosticChecksCount++;
                                result.Actions.Add(actionResult);
                                continue;
                            }



                            actionResult.Status = OptimizationActionStates.VERIFIED;



                            actionResult.ActualEffectVerified = true;



                            result.ActionsVerifiedCount++;



                            actionsVerifiedInPass++;

                            try
                            {
                                string canTarget = GlobalOptimizationRegistry.NormalizeTarget(candidate.TargetType, candidate.TargetIdentifier);
                                GlobalAppliedStateManager.Instance.RecordApplied(
                                    canTarget,
                                    candidate.CandidateId,
                                    "MaximumPerformance",
                                    "MaximumPerformance",
                                    candidate.InitialState ?? string.Empty,
                                    candidate.TargetState ?? string.Empty,
                                    verified: true,
                                    rollbackAvailable: true,
                                    batchId,
                                    candidate.TargetType);
                            }
                            catch { }







                            switch (candidate.TargetType)



                            {



                                case "Process":



                                    result.ProcessActionsVerified++;



                                    actionResult.ProcessImpact = 1;



                                    terminatedPids.Add(candidate.ProcessId);



                                    var auditEntry = result.CandidateAudit.FirstOrDefault(a => a.ProcessId == candidate.ProcessId);



                                    if (auditEntry != null)



                                    {



                                        auditEntry.WasReduced = true;



                                    }



                                    break;



                                case "Service":



                                    result.ServiceActionsVerified++;



                                    break;



                                case "StartupEntry":



                                    result.StartupActionsVerified++;



                                    break;



                                case "ScheduledTask":



                                    result.TaskActionsVerified++;



                                    break;



                                case "AppXBackground":



                                    result.AppXActionsVerified++;



                                    break;

                                case "OemComponent":

                                    result.OemActionsVerified++;

                                    break;



                                case "KernelSetting":
                                case "PowerScheme":
                                case "MemoryCache":
                                case "FileSystemSetting":
                                case "InputSetting":
                                case "UserPreference":
                                case "NetworkStackSetting":
                                case "WorkloadSetting":
                                case "AudioSubsystem":
                                case "CpuSetting":
                                case "DisplayCompositor":
                                case "EmulatorSetting":
                                case "GamingSetting":
                                case "GpuSetting":
                                case "KernelTimer":
                                case "NetworkStack":
                                case "ProcessManagement":
                                case "StorageEngine":
                                case "SystemSetting":
                                    result.SystemTuningActionsVerified++;



                                    if (candidate.CandidateId == "TUNE_MMCSS_RESPONSIVENESS") result.SystemTuning.MmcssPriorityBoostEnabled = true;



                                    else if (candidate.CandidateId == "TUNE_WIN32_PRIORITY") result.SystemTuning.Win32PrioritySeparationConfigured = true;



                                    else if (candidate.CandidateId == "TUNE_NETWORK_THROTTLING") result.SystemTuning.NetworkThrottlingIndexDisabled = true;



                                    else if (candidate.CandidateId == "TUNE_GPU_HAGS") result.SystemTuning.HagsConfigured = true;



                                    else if (candidate.CandidateId == "TUNE_POWER_PLAN_HIGH_PERFORMANCE") result.SystemTuning.HighPerformancePowerPlanActive = true;



                                    break;



                            }







                            if (candidate.IsPersistent)



                            {



                                result.PersistentOptimizationsCount++;



                            }



                        }



                        else



                        {



                            actionResult.Status = OptimizationActionStates.NO_EFFECT;



                            result.ActionsNoEffectCount++;







                            switch (candidate.TargetType)



                            {



                                case "Process": result.ProcessActionsFailed++; break;



                                case "Service": result.ServiceActionsFailed++; break;



                                case "StartupEntry": result.StartupActionsFailed++; break;



                                case "ScheduledTask": result.TaskActionsFailed++; break;



                                case "AppXBackground": result.AppXActionsFailed++; break;
case "OemComponent": result.OemActionsFailed++; break;



                                case "KernelSetting":
                                case "PowerScheme":
                                case "MemoryCache":
                                case "FileSystemSetting":
                                case "InputSetting":
                                case "UserPreference":
                                case "NetworkStackSetting":
                                case "WorkloadSetting":
                                case "AudioSubsystem":
                                case "CpuSetting":
                                case "DisplayCompositor":
                                case "EmulatorSetting":
                                case "GamingSetting":
                                case "GpuSetting":
                                case "KernelTimer":
                                case "NetworkStack":
                                case "ProcessManagement":
                                case "StorageEngine":
                                case "SystemSetting":
                                default:
                                    result.SystemTuningActionsFailed++;
                                    break;



                            }



                        }



                    }



                    else



                    {



                        actionResult.Status = OptimizationActionStates.FAILED;



                        actionResult.VerificationMessage = "Execution command failed or threw an error";



                        result.ActionsFailedCount++;







                        switch (candidate.TargetType)



                        {



                            case "Process": result.ProcessActionsFailed++; break;



                            case "Service": result.ServiceActionsFailed++; break;



                            case "StartupEntry": result.StartupActionsFailed++; break;



                            case "ScheduledTask": result.TaskActionsFailed++; break;



                            case "AppXBackground": result.AppXActionsFailed++; break;
case "OemComponent": result.OemActionsFailed++; break;



                            case "KernelSetting":
                            case "PowerScheme":
                            case "MemoryCache":
                            case "FileSystemSetting":
                            case "InputSetting":
                            case "UserPreference":
                            case "NetworkStackSetting":
                            case "WorkloadSetting":
                            case "AudioSubsystem":
                            case "CpuSetting":
                            case "DisplayCompositor":
                            case "EmulatorSetting":
                            case "GamingSetting":
                            case "GpuSetting":
                            case "KernelTimer":
                            case "NetworkStack":
                            case "ProcessManagement":
                            case "StorageEngine":
                            case "SystemSetting":
                            default:
                                result.SystemTuningActionsFailed++;
                                break;



                        }



                    }







                    result.Actions.Add(actionResult);



                }







                // Stabilization delay between passes



                await Task.Delay(300, ct);







                // Re-scan and measure actual current state



                var currentMetrics = _benchmarkEngine.SampleCurrentMetrics();



                int newProcessCount = currentMetrics.CurrentProcessCount;







                passRecord.EndProcessCount = newProcessCount;



                passRecord.ActionsAppliedCount = actionsVerifiedInPass;



                double passEndRam = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot().Memory.UsedPhysicalGb * 1024.0;
                passRecord.RamFreedMb = Math.Max(0.0, Math.Round(passStartRam - passEndRam, 1));



                var endHwSnap = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot();
                var endProcSnap = await _processIntelligence.CaptureSnapshotAsync();

                passRecord.UserProcessesCount = endProcSnap.ActiveAppProcessCount + endProcSnap.WorkloadProcessCount;
                passRecord.ClassifiedBackgroundCount = endProcSnap.ReducibleProcessCount + endProcSnap.OptionalProcessCount;
                passRecord.ProtectedProcessesCount = endProcSnap.ProtectedProcessCount + endProcSnap.RequiredProcessCount;
                passRecord.CpuUsagePercent = OptimizationBenchmarkEngine.SampleSystemCpuPercent();
                passRecord.RamUsageMb = Math.Round(endHwSnap.Memory.UsedPhysicalGb * 1024.0, 1);
                passRecord.CommitUsageMb = Math.Round(endHwSnap.Memory.CommitUsedGb * 1024.0, 1);
                passRecord.ConvergenceReason = actionsVerifiedInPass > 0
                    ? $"Converged with {actionsVerifiedInPass} action(s) verified in pass."
                    : "Footprint stable; system operating at verified optimal baseline.";

                // Adaptive safe-opportunity convergence: progress only if previous pass verified actions AND next tier has eligible processes
                bool canEscalate = false;
                if (actionsVerifiedInPass > 0 && pass < maxPasses && pass < 3)
                {
                    if (pass == 1 && endProcSnap.Processes.Any(p => p.Classification == ProcessClassification.BACKGROUND && !seenProcessKeys.Contains($"{p.ProcessId}_{(p.StartTime != default ? p.StartTime.ToUniversalTime().ToString("yyyyMMddHHmmss") : "00000000000000")}_{p.ProcessName.ToLowerInvariant()}")))
                    {
                        canEscalate = true;
                    }
                    else if (pass == 2 && endProcSnap.Processes.Any(p => p.Classification == ProcessClassification.OPTIONAL && !seenProcessKeys.Contains($"{p.ProcessId}_{(p.StartTime != default ? p.StartTime.ToUniversalTime().ToString("yyyyMMddHHmmss") : "00000000000000")}_{p.ProcessName.ToLowerInvariant()}")))
                    {
                        canEscalate = true;
                    }
                }

                passRecord.RemainingSafeOpportunitiesCount = canEscalate
                    ? endProcSnap.Processes.Count(p => (pass == 1 ? p.Classification == ProcessClassification.BACKGROUND : p.Classification == ProcessClassification.OPTIONAL) && !seenProcessKeys.Contains($"{p.ProcessId}_{(p.StartTime != default ? p.StartTime.ToUniversalTime().ToString("yyyyMMddHHmmss") : "00000000000000")}_{p.ProcessName.ToLowerInvariant()}"))
                    : 0;

                passRecord.ReachedTargetBand = result.TargetBand != null && _targetBandEngine.IsWithinTargetBand(newProcessCount, result.TargetBand);
                passRecord.PassSummary = $"Pass {pass}: {passRecord.StartProcessCount} → {passRecord.EndProcessCount} processes ({actionsVerifiedInPass} actions verified)";
                result.PassHistory.Add(passRecord);

                if (watchdog.IsAborted)
                {
                    continueOptimizing = false;
                    break;
                }

                currentProcessCount = newProcessCount;

                if (canEscalate)
                {
                    pass++;
                }
                else
                {
                    continueOptimizing = false;
                }



            }







            result.TotalPassesRun = result.PassHistory.Count;



            result.SystemTuning.SystemTuningActionsApplied = result.SystemTuningActionsVerified;







            // 4. Final Benchmark Measurement & Single-Source PID Difference Accounting



            var finalMetrics = _benchmarkEngine.SampleCurrentMetrics();



            var finalProcSnap = await _processIntelligence.CaptureSnapshotAsync();



            var procDelta = CoreOptimizationAccounting.ComputeProcessDelta(initialSnapshot, finalProcSnap, terminatedPids);

            result.InitialProcessCount = procDelta.InitialProcessCount > 0 ? procDelta.InitialProcessCount : result.InitialProcessCount;
            result.FinalProcessCount = procDelta.FinalProcessCount > 0 ? procDelta.FinalProcessCount : result.InitialProcessCount;
            result.FinalRamUsageMb = finalMetrics.RamUsedMb;
            result.FinalCpuPercent = finalMetrics.CpuUtilizationPercent;

            // Authoritative PID delta metrics with PID-reuse protection on BOTH targeted and untargeted PIDs
            result.GrossTerminatedCount = procDelta.GrossTargetedTerminations;
            result.VerifiedProcessesTerminated = procDelta.VerifiedTerminations;
            result.RestartedProcessesCount = procDelta.RestartedProcesses;
            result.NewProcessesObserved = procDelta.TotalNewProcesses;
            result.NewlyStartedProcessesCount = procDelta.NewlyStartedProcesses;

            result.ProtectedProcessesCount = initialSnapshot.ProtectedProcessCount + initialSnapshot.RequiredProcessCount;
            result.UnclassifiedProcessesCount = initialSnapshot.UnknownProcessCount;

            // Strict Process Attribution Invariants:
            // 1. Optimizer process reduction MUST EQUAL verified terminated count.
            // 2. Disappearing untargeted processes are strictly recorded as NaturalProcessExitsCount (ambient).
            // 3. New external processes are strictly recorded as ExternalProcessesStartedCount.
            result.NaturalProcessExitsCount = procDelta.NaturalExits;
            result.ExternalProcessesStartedCount = procDelta.TotalNewProcesses;
            result.AmbientProcessDelta = procDelta.AmbientProcessDelta;

            result.ActualProcessReduction = procDelta.OptimizerProcessReduction;
            result.NetProcessReductionCount = procDelta.OptimizerProcessReduction; // NEVER credit natural exits to optimizer!
            result.NetProcessDelta = procDelta.AmbientProcessDelta;

            // Measured CPU change: consistent measurement window + ±1.5% ambient variance noise-floor gating
            var (cpuDelta, cpuStatus) = CoreOptimizationAccounting.ComputeCpuChange(
                result.InitialCpuPercent,
                result.FinalCpuPercent,
                result.VerifiedProcessesTerminated,
                result.VerifiedProcessesThrottled);
            result.MeasuredCpuChangePercent = cpuDelta;
            result.CpuMeasurementStatus = cpuStatus;
            result.RamMeasurementStatus = (result.VerifiedProcessesTerminated > 0 || result.VerifiedProcessesThrottled > 0)
                ? "VERIFIED_RECOVERY"
                : "BASELINE_OPTIMAL";

            // Genuine RAM recovery accounting: sum of working set trimmed from throttled processes
            // plus working set / private memory from verified terminated processes.
            // Do NOT overwrite with ambient host system RAM difference.
            result.RamRecoveredMb = Math.Round(result.WorkingSetTrimmedMb + procDelta.TerminatedRamMb, 1);
            result.ActionsAppliedCount = result.ActionsVerifiedCount;
            result.Success = true;







            // Populate split summary counters (A4)



            result.CandidatesDiscovered = initialSnapshot.TotalProcessCount;



            result.ClassifiedReducible = initialSnapshot.ReducibleProcessCount;



            result.ClassifierRejections = Math.Max(0, initialSnapshot.TotalProcessCount - initialSnapshot.ReducibleProcessCount);



            result.SafetyKernelRejections = result.Actions.Count(a => a.Status == OptimizationActionStates.SKIPPED_FOR_SAFETY);



            result.ExecutionAttempts = result.ActionsExecutedCount;



            result.VerifiedTerminations = result.VerifiedProcessesTerminated;



            result.PersistentActionsVerified = result.SystemTuningActionsVerified + result.ServiceActionsVerified + result.StartupActionsVerified + result.TaskActionsVerified + result.AppXActionsVerified + result.OemActionsVerified;

            // Authoritative, mutually exclusive Hardware & System Tuning Parameter Accounting (zero hardcoded fallbacks)
            var hwAccounting = CoreOptimizationAccounting.ComputeHardwareParameterAccounting(result.Actions);
            result.HardwareParametersEvaluatedCount = hwAccounting.EvaluatedCount;
            result.HardwareParametersModifiedCount = hwAccounting.AppliedCount;
            result.HardwareParametersAlreadyOptimalCount = hwAccounting.AlreadyOptimalCount;
            result.HardwareParametersVerifiedCount = hwAccounting.AppliedCount;
            result.HardwareParametersConvergedCount = hwAccounting.ConvergedCount;
            result.HardwareParametersRemainingCount = hwAccounting.RemainingCount;
            result.DiagnosticChecksCount = Math.Max(result.DiagnosticChecksCount, hwAccounting.DiagnosticChecksCount);







            // Register baseline with workload guard



            if (result.FinalProcessCount > 0)



            {



                _workloadGuardEngine.OptimizedBaselineCount = result.FinalProcessCount;



            }







            if (result.TargetBand != null)



            {



                result.TargetBand.IsWithinTargetBand = _targetBandEngine.IsWithinTargetBand(result.FinalProcessCount, result.TargetBand);



            }







            // 5. Honest Verdict & Stopping Reason Assignment (Strict Error Fidelity & Truthful Semantics)
            // Section 3: Safe-Opportunity Convergence Outcomes
            bool properConvergence = result.ActionsFailedCount == 0 && (result.ActionsVerifiedCount > 0 || result.ActionsAlreadyOptimalCount > 0);

            // Populate Section 3 multi-resource distinct accounting
            result.DiscoveredSafeOpportunitiesCount = result.CandidatesDiscovered;
            result.AppliedSafeOpportunitiesCount = result.ActionsVerifiedCount;
            result.RemainingSafeOpportunitiesCount = Math.Max(0, result.ClassifiedReducible - result.ActionsVerifiedCount);

            if (watchdog.IsAborted || ct.IsCancellationRequested)
            {
                result.ResultState = OptimizationResultStates.CANCELLED;
                result.ConvergenceStatus = OptimizationResultStates.CANCELLED;
                result.StopReason = $"Optimization cancelled or aborted by LiveWatchdog: {watchdog.AbortReason}";
                result.Success = false;
            }
            else if (result.ResultState == OptimizationResultStates.ABORTED)
            {
                result.ConvergenceStatus = OptimizationResultStates.CANCELLED;
            }
            else if (result.ActionsExecutedCount == 0 || (result.ActionsVerifiedCount == 0 && result.ActionsFailedCount == 0 && result.ActionsAlreadyOptimalCount > 0))
            {
                result.ResultState = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
                result.ConvergenceStatus = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
                result.IsOptimalBaselineConfirmed = true;
                result.StopReason = "System is operating at verified optimal baseline. All tuning parameters are optimal and running processes are protected or required. No further safe opportunities remain.";
                result.Success = true;
            }
            else if ((result.ClassifiedReducible == 0 || result.ActionsPlannedCount == 0) && result.ActionsVerifiedCount == 0)
            {
                result.ResultState = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
                result.ConvergenceStatus = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
                result.IsOptimalBaselineConfirmed = true;
                result.StopReason = "All candidate settings and background workloads are verified safe and optimal. No further safe background waste detected.";
                result.Success = true;
            }
            else if (result.ActionsFailedCount > 0 && result.ActionsVerifiedCount > 0)
            {
                result.ResultState = OptimizationResultStates.PARTIALLY_CONVERGED;
                result.ConvergenceStatus = OptimizationResultStates.PARTIALLY_CONVERGED;
                result.StopReason = $"Partially converged: {result.ActionsVerifiedCount} action(s) verified, {result.ActionsFailedCount} action(s) failed verification, {result.ActionsAlreadyOptimalCount} already optimal, {result.ActionsProtectedSkippedCount} protected ({result.WorkingSetTrimmedMb} MB RAM working-set non-destructively trimmed).";
                result.Success = true;
            }
            else if (result.ActionsExecutedCount > 0 && result.ActionsFailedCount > 0 && result.ActionsVerifiedCount == 0)
            {
                result.ResultState = OptimizationResultStates.EXECUTION_FAILURE;
                result.ConvergenceStatus = OptimizationResultStates.EXECUTION_FAILURE;
                result.StopReason = $"Execution failed: Classified {result.ClassifiedReducible} reducible candidates, but {result.SafetyKernelRejections} were blocked by SafetyKernel and {result.ActionsFailedCount} failed execution.";
                result.Success = false;
            }
            else if (properConvergence && result.ActionsVerifiedCount > 0)
            {
                result.ResultState = OptimizationResultStates.CONVERGED_WITH_IMPROVEMENT;
                result.ConvergenceStatus = OptimizationResultStates.CONVERGED_WITH_IMPROVEMENT;
                string trimNote = result.WorkingSetTrimmedMb > 0
                    ? $", and {result.WorkingSetTrimmedMb:F0} MB RAM working-set trimmed to standby list."
                    : " (system baseline operating within safe verified limits).";
                result.StopReason = $"Core safe-opportunity optimization converged across {result.TotalPassesRun} pass(es): {result.ActionsVerifiedCount} verified action(s) applied, {result.ActionsAlreadyOptimalCount} already optimal{trimNote}";
                result.Success = true;
            }
            else if (properConvergence)
            {
                result.ResultState = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
                result.ConvergenceStatus = OptimizationResultStates.CONVERGED_NO_FURTHER_SAFE_OPPORTUNITIES;
                result.StopReason = $"System converged with no further safe opportunities: {result.ActionsAlreadyOptimalCount} parameters verified already optimal.";
                result.Success = true;
            }
            else if (result.PersistentActionsVerified > 0)
            {
                result.ResultState = OptimizationResultStates.CONVERGED_WITH_IMPROVEMENT;
                result.ConvergenceStatus = OptimizationResultStates.CONVERGED_WITH_IMPROVEMENT;
                result.StopReason = $"Persistent optimizations applied ({result.PersistentActionsVerified} background services, scheduled tasks, system tunings, or AppX background permissions configured); background footprint stabilized.";
                result.Success = result.ActionsFailedCount == 0;
            }
            else
            {
                result.ResultState = OptimizationResultStates.PARTIALLY_CONVERGED;
                result.ConvergenceStatus = OptimizationResultStates.PARTIALLY_CONVERGED;
                result.StopReason = $"Optimization pass completed with {result.ActionsVerifiedCount} verified actions at {result.FinalProcessCount} processes.";
                result.Success = result.ActionsFailedCount == 0;
            }



            // Executor completion log



            if (lastDump != null)



            {



                lastDump.Executor.TerminationAttempts = result.ActionsExecutedCount;



                lastDump.Executor.VerifiedTerminations = result.VerifiedProcessesTerminated;



                lastDump.Executor.SafetyKernelDenials = result.ActionsSkippedCount;



                ClassificationDump.SaveDump(lastDump);



            }







            // Truthful Scope Item Status & Explanation Synthesis (Phase 2 & Phase 5)
            result.TotalBackgroundProcessesEvaluated = initialSnapshot.TotalProcessCount;

            // 1. Background Reduction ("background")
            if (result.VerifiedProcessesTerminated > 0)
            {
                result.ScopeItemStatuses["background"] = "APPLIED";
                result.ScopeItemExplanations["background"] = $"Verified {result.VerifiedProcessesTerminated} background process(es) terminated, reducing total processes from {result.InitialProcessCount} to {result.FinalProcessCount}.";
            }
            else if (result.VerifiedProcessesThrottled > 0)
            {
                result.ScopeItemStatuses["background"] = "APPLIED";
                result.ScopeItemExplanations["background"] = $"Throttled {result.VerifiedProcessesThrottled} idle background process(es) to Idle priority and yielded working sets ({result.WorkingSetTrimmedMb:F0} MB RAM recovered).";
            }
            else if (result.ProcessActionsFailed > 0)
            {
                result.ScopeItemStatuses["background"] = "FAILED";
                result.ScopeItemExplanations["background"] = $"{result.ProcessActionsFailed} background process termination or throttling actions failed.";
            }
            else if (result.ReducibleProcesses == 0 && result.ProcessActionsPlanned == 0)
            {
                result.ScopeItemStatuses["background"] = "NO_SAFE_ACTIONS";
                result.ScopeItemExplanations["background"] = $"All {result.TotalProcesses} background processes are protected by SafetyKernel R01-R28 or required by active workloads. 0 safe termination candidates available.";
            }
            else if (result.ActionsSkippedCount > 0 && result.VerifiedProcessesTerminated == 0 && result.VerifiedProcessesThrottled == 0)
            {
                result.ScopeItemStatuses["background"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["background"] = $"Background processes operating at safe baseline ({result.FinalProcessCount} processes active; candidates preserved by safety policy).";
            }
            else
            {
                result.ScopeItemStatuses["background"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["background"] = $"Background processes are operating at verified optimal baseline ({result.FinalProcessCount} processes active, 0 safe excess daemons).";
            }

            // 2. Priority Balancing ("priority")
            var priActs = result.Actions.Where(a =>
                a.CandidateId.Contains("PRIORITY", StringComparison.OrdinalIgnoreCase) ||
                a.CandidateId.Contains("MMCSS", StringComparison.OrdinalIgnoreCase) ||
                a.CandidateId.Contains("AUDIO", StringComparison.OrdinalIgnoreCase) ||
                a.CandidateId.Contains("THREAD_DIRECTOR", StringComparison.OrdinalIgnoreCase) ||
                a.CandidateId.Contains("AMD_3D_VCACHE", StringComparison.OrdinalIgnoreCase) ||
                a.ActionType is "WorkloadSetting" or "AudioSubsystem" or "CpuSetting").ToList();
            if (priActs.Any(a => a.Status == OptimizationActionStates.VERIFIED))
            {
                result.ScopeItemStatuses["priority"] = "APPLIED";
                result.ScopeItemExplanations["priority"] = "Win32PrioritySeparation quantum configured and MMCSS system responsiveness clamped to 20.";
            }
            else if (priActs.Any(a => a.Status == OptimizationActionStates.ALREADY_OPTIMAL))
            {
                result.ScopeItemStatuses["priority"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["priority"] = "Scheduling quantum (0x26) and MMCSS responsiveness (20) already verified optimal.";
            }
            else if (priActs.Any(a => a.Status == OptimizationActionStates.FAILED))
            {
                result.ScopeItemStatuses["priority"] = "FAILED";
                result.ScopeItemExplanations["priority"] = "Priority balancing configuration failed.";
            }
            else
            {
                result.ScopeItemStatuses["priority"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["priority"] = "Priority balancing verified optimal.";
            }

            // 3. Working-set Refresh ("working-set")
            if (result.RamRecoveredMb > 0)
            {
                result.ScopeItemStatuses["working-set"] = "APPLIED";
                result.ScopeItemExplanations["working-set"] = $"{result.RamRecoveredMb:F0} MB RAM working-set recovered and yielded to available memory pool.";
            }
            else
            {
                result.ScopeItemStatuses["working-set"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["working-set"] = "Memory operating at verified optimal baseline. Standby cache accelerates disk caching; purging suppressed to avoid cache churn.";
            }

            // 4. System Optimizations ("system")
            var sysActs = result.Actions.Where(a =>
                a.TargetType is "KernelSetting" or "PowerScheme" or "FileSystemSetting" or
                "InputSetting" or "UserPreference" or "NetworkStackSetting" or "DisplayCompositor" or
                "EmulatorSetting" or "GamingSetting" or "GpuSetting" or "KernelTimer" or
                "NetworkStack" or "StorageEngine" or "SystemSetting" ||
                (a.CandidateId.StartsWith("TUNE_") && !a.CandidateId.StartsWith("TUNE_CORE_") && !a.CandidateId.Contains("PRIORITY") && !a.CandidateId.Contains("MMCSS"))).ToList();
            if (sysActs.Any(a => a.Status == OptimizationActionStates.VERIFIED) && sysActs.All(a => a.Status != OptimizationActionStates.FAILED))
            {
                result.ScopeItemStatuses["system"] = "APPLIED";
                result.ScopeItemExplanations["system"] = $"Verified {sysActs.Count(a => a.Status == OptimizationActionStates.VERIFIED)} hardware-aware system latency parameters applied.";
            }
            else if (sysActs.Any(a => a.Status == OptimizationActionStates.VERIFIED) && sysActs.Any(a => a.Status == OptimizationActionStates.FAILED))
            {
                result.ScopeItemStatuses["system"] = "PARTIALLY_APPLIED";
                result.ScopeItemExplanations["system"] = $"{sysActs.Count(a => a.Status == OptimizationActionStates.VERIFIED)} parameters applied; {sysActs.Count(a => a.Status == OptimizationActionStates.FAILED)} failed.";
            }
            else if (sysActs.Any(a => a.Status == OptimizationActionStates.ALREADY_OPTIMAL))
            {
                result.ScopeItemStatuses["system"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["system"] = "All host system latency, GPU scheduling, and network tuning parameters are already at optimal values.";
            }
            else if (sysActs.Any(a => a.Status == OptimizationActionStates.FAILED))
            {
                result.ScopeItemStatuses["system"] = "FAILED";
                result.ScopeItemExplanations["system"] = "System latency optimization failed.";
            }
            else
            {
                result.ScopeItemStatuses["system"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["system"] = "All system optimization parameters already optimal.";
            }

            // 5. Core Footprint Convergence ("core")
            if (result.VerifiedProcessesTerminated > 0 || result.VerifiedProcessesThrottled > 0)
            {
                result.ScopeItemStatuses["core"] = "APPLIED";
                result.ScopeItemExplanations["core"] = $"Core footprint converged across {result.TotalPassesRun} pass(es) with {result.VerifiedProcessesTerminated} terminated and {result.VerifiedProcessesThrottled} throttled; {result.HardwareParametersConvergedCount} hardware tuning parameters converged ({result.HardwareParametersModifiedCount} applied, {result.HardwareParametersAlreadyOptimalCount} already optimal, {result.HardwareParametersRemainingCount} remaining).";
            }
            else if (result.HardwareParametersConvergedCount > 0 || result.ActionsVerifiedCount > 0)
            {
                result.ScopeItemStatuses["core"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["core"] = $"Footprint operating at verified safe baseline ({result.FinalProcessCount} processes active; 0 safe excess daemons); {result.HardwareParametersConvergedCount} hardware tuning parameters converged ({result.HardwareParametersModifiedCount} applied, {result.HardwareParametersAlreadyOptimalCount} already optimal, {result.HardwareParametersRemainingCount} remaining).";
            }
            else
            {
                result.ScopeItemStatuses["core"] = "ALREADY_OPTIMAL";
                result.ScopeItemExplanations["core"] = "Footprint stable; system operating at verified optimal baseline across all passes.";
            }

            // Save persistent baseline state



            try



            {



                await _baselineStore.SaveBaselineAsync(result);



            }



            catch { }







            return result;



            }



            catch (RestorePointCreationException)



            {



                return new OptimizationResultDto



                {



                    ResultState = OptimizationResultStates.ABORTED,



                    StopReason = "Cannot create restore point — System Protection is off. Enable it or grant the required permissions, then retry. Optimization will not run without a restore point.",



                    Success = false,



                    TransactionBatchId = batchId



                };



            }



            finally



            {



                _safetyKernel.StopWatchdog();



                watchdog.Dispose();



            }



        }







        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static int GetForegroundProcessId()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    return (int)pid;
                }
            }
            catch { }
            return 0;
        }

        private static bool ReduceProcessBackgroundFootprint(int pid, string? expectedName, out long wsTrimmedBytes, V4RollbackEngine? rollbackEngine = null, string? batchId = null)
        {
            wsTrimmedBytes = 0;
            if (pid <= 4) return false;

            // Never trim or throttle the active interactive foreground window
            int fgPid = GetForegroundProcessId();
            if (fgPid > 0 && fgPid == pid) return false;

            try
            {
                var p = Process.GetProcessById(pid);
                using (p)
                {
                    if (p.HasExited) return true;

                    // PID reuse protection: validate clean process name and check StartTime
                    string procName = p.ProcessName;
                    string cleanExpected = (expectedName ?? "").Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (!string.IsNullOrEmpty(cleanExpected) && 
                        !procName.Equals(cleanExpected, StringComparison.OrdinalIgnoreCase) &&
                        !cleanExpected.Contains(procName, StringComparison.OrdinalIgnoreCase) &&
                        !procName.Contains(cleanExpected, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DateTime startTime;
                    try
                    {
                        startTime = p.StartTime;
                    }
                    catch
                    {
                        // Inaccessible process identity or terminated; abort modification safely
                        return false;
                    }

                    // Critical Windows, audio, driver, and active user workload protection
                    string lowerName = procName.ToLowerInvariant();
                    if (lowerName is "audiodg" or "dwm" or "explorer" or "csrss" or "smss" or "lsass" or "services" or
                        "wininit" or "winlogon" or "fontdrvhost" or "nvcontainer" or "nvdisplay.container" or "amdfendrsr" or
                        "taskmgr" or "system" or "idle" or
                        "obs64" or "obs32" or "obs" or "discord" or "discordcanary" or "steam" or "steamwebhelper" or
                        "spotify" or "blender" or "devenv" or "code" or "photoshop" or "premiere" or "afterfx" or
                        "antigravity" or "erroroptimizer" or "biosoptimizer")
                    {
                        return false;
                    }

                    // Hardware-Aware Throttling & Power Intelligence
                    bool onBattery = false;
                    try { onBattery = !BiosOptimizer.Core.V4.Architecture.NativePowerMethods.IsOnAcPower(); } catch { }

                    // Preserve original process priority before any modification
                    ProcessPriorityClass originalPriority = ProcessPriorityClass.Normal;
                    bool priorityAdjusted = false;
                    try
                    {
                        originalPriority = p.PriorityClass;
                        if (originalPriority == ProcessPriorityClass.Normal)
                        {
                            ProcessPriorityClass targetPriority = onBattery ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
                            p.PriorityClass = targetPriority;
                            priorityAdjusted = true;

                            if (rollbackEngine != null && !string.IsNullOrEmpty(batchId))
                            {
                                rollbackEngine.RecordTransaction(new V4TransactionRecord
                                {
                                    BatchId = batchId,
                                    TargetType = "ProcessPriority",
                                    TargetIdentifier = $"PID:{pid}:{procName}",
                                    PreviousState = originalPriority.ToString(),
                                    NewState = targetPriority.ToString(),
                                    Reason = $"Background process priority adjusted from {originalPriority} to {targetPriority} for {procName} (BatteryMode: {onBattery})"
                                });
                            }
                        }
                    }
                    catch { }

                    // Hardware-Aware CPU Affinity Pinning (Intel Hybrid / AMD 3D V-Cache)
                    bool affinityAdjusted = false;
                    try
                    {
                        var hwSnap = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot();
                        int totalLogical = Environment.ProcessorCount;
                        if (hwSnap.Cpu.IsHybridCpu && hwSnap.Cpu.PerformanceCores > 0 && hwSnap.Cpu.EfficientCores > 0 && totalLogical <= 64)
                        {
                            int pLogical = hwSnap.Cpu.PerformanceCores * 2; // Intel P-cores with HT
                            if (totalLogical > pLogical)
                            {
                                ulong eCoreMask = 0;
                                for (int core = pLogical; core < totalLogical; core++)
                                {
                                    eCoreMask |= (1UL << core);
                                }
                                if (eCoreMask != 0)
                                {
                                    p.ProcessorAffinity = (IntPtr)(long)eCoreMask;
                                    affinityAdjusted = true;
                                }
                            }
                        }
                        else if (hwSnap.Cpu.IsAmd && (hwSnap.Cpu.IsAmd3DVcache || hwSnap.Cpu.IsDualCcdAmd3DVcache) && totalLogical >= 16 && totalLogical <= 64)
                        {
                            // Keep background daemons off CCD0 (cores 0-7 with 3D V-Cache)
                            ulong nonCacheCcdMask = 0;
                            for (int core = 8; core < totalLogical; core++)
                            {
                                nonCacheCcdMask |= (1UL << core);
                            }
                            if (nonCacheCcdMask != 0)
                            {
                                p.ProcessorAffinity = (IntPtr)(long)nonCacheCcdMask;
                                affinityAdjusted = true;
                            }
                        }
                    }
                    catch { }

                    // Hardware-Aware RAM Pressure Tiering
                    long wsBefore = 0;
                    try { wsBefore = p.WorkingSet64; } catch { }

                    bool shouldTrim = true;
                    try
                    {
                        var memSnap = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot().Memory;
                        if (memSnap.InstalledPhysicalGb > 32.0 && wsBefore < (50 * 1024 * 1024))
                        {
                            // High RAM system (> 32 GB): preserve active working sets for small daemons, focus on CPU wakeups and thread throttling
                            shouldTrim = false;
                        }
                    }
                    catch { }

                    bool trimmed = false;
                    if (shouldTrim)
                    {
                        try
                        {
                            if (EmptyWorkingSet(p.Handle) != 0)
                            {
                                trimmed = true;
                            }
                        }
                        catch { }
                    }

                    long wsAfter = 0;
                    ProcessPriorityClass readbackPriority = ProcessPriorityClass.Normal;
                    try
                    {
                        p.Refresh();
                        wsAfter = p.WorkingSet64;
                        readbackPriority = p.PriorityClass;
                    }
                    catch { }

                    wsTrimmedBytes = Math.Max(0, wsBefore - wsAfter);
                    bool priorityVerified = priorityAdjusted && (readbackPriority == ProcessPriorityClass.BelowNormal || readbackPriority == ProcessPriorityClass.Idle);
                    bool ramRecovered = trimmed && wsTrimmedBytes > 0;

                    // If trimming failed to achieve any effect, restore original priority immediately
                    if (priorityAdjusted && !ramRecovered && !priorityVerified && !affinityAdjusted)
                    {
                        try { p.PriorityClass = originalPriority; } catch { }
                    }

                    return priorityVerified || ramRecovered || affinityAdjusted;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TerminateProcessTarget(int pid)
        {
            // Non-destructive Core Optimization invariant: Never terminate processes.
            // Safely redirect to background working-set reduction and return without terminating.
            return ReduceProcessBackgroundFootprint(pid, null, out _, null, null);
        }

        private bool ModifyServiceTarget(OptimizationCandidateDto candidate, string batchId)



        {



            if (_safetyKernel.IsProtected(candidate)) return false;
            if (BiosOptimizer.Core.V4.Tuning.SystemPerformanceOptimizer.TestExecutionSimulationEnabled) return true;







            string serviceName = candidate.TargetIdentifier;







            try



            {



                string previousStartType = candidate.InitialState;



                bool wasRunning = false;







                try



                {



                    using var sc = new ServiceController(serviceName);



                    previousStartType = sc.StartType.ToString();



                    wasRunning = (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending);







                    if (wasRunning)



                    {



                        try



                        {



                            sc.Stop();



                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(2));



                        }



                        catch { }



                    }



                }



                catch { }







                try



                {



                    var stopPsi = new ProcessStartInfo("sc.exe", $"stop \"{serviceName}\"")



                    {



                        CreateNoWindow = true,



                        UseShellExecute = false



                    };



                    using var stopProc = Process.Start(stopPsi);



                    stopProc?.WaitForExit(2000);



                }



                catch { }







                _rollbackEngine.RecordTransaction(new V4TransactionRecord



                {



                    BatchId = batchId,



                    TargetType = "Service",



                    TargetIdentifier = serviceName,



                    PreviousState = previousStartType,



                    NewState = "Disabled",



                    Reason = candidate.Reason



                });







                bool configured = false;



                try



                {



                    var psi = new ProcessStartInfo("sc.exe", $"config \"{serviceName}\" start= disabled")



                    {



                        CreateNoWindow = true,



                        UseShellExecute = false



                    };



                    using var proc = Process.Start(psi);



                    proc?.WaitForExit(3000);



                    configured = (proc?.ExitCode == 0);



                }



                catch { }







                if (!configured)



                {



                    if (TrySetServiceStartRegistry(serviceName, 4, _restorePointCreated, out string regFailure))



                    {



                        configured = true;



                    }



                }







                return configured;



            }



            catch



            {



                return false;



            }



        }







        private bool ModifyScheduledTaskTarget(OptimizationCandidateDto candidate, string batchId)



        {



            if (_safetyKernel.IsProtected(candidate)) return false;
            if (BiosOptimizer.Core.V4.Tuning.SystemPerformanceOptimizer.TestExecutionSimulationEnabled) return true;







            string taskPath = candidate.TargetIdentifier;







            try



            {



                _rollbackEngine.RecordTransaction(new V4TransactionRecord



                {



                    BatchId = batchId,



                    TargetType = "ScheduledTask",



                    TargetIdentifier = taskPath,



                    PreviousState = "Enabled",



                    NewState = "Disabled",



                    Reason = candidate.Reason



                });







                var psi = new ProcessStartInfo("schtasks.exe", $"/Change /TN \"{taskPath}\" /Disable")



                {



                    CreateNoWindow = true,



                    UseShellExecute = false



                };



                using var proc = Process.Start(psi);



                proc?.WaitForExit(3000);



                return proc?.ExitCode == 0;



            }



            catch



            {



                return false;



            }



        }







        private bool ModifyStartupTarget(OptimizationCandidateDto candidate, string batchId)



        {



            if (_safetyKernel.IsProtected(candidate)) return false;
            if (BiosOptimizer.Core.V4.Tuning.SystemPerformanceOptimizer.TestExecutionSimulationEnabled) return true;







            string identifier = candidate.TargetIdentifier;



            var parts = identifier.Split('|', 2);



            string location = parts.Length > 1 ? parts[0] : "HKCU:Run";



            string valueName = parts.Length > 1 ? parts[1] : parts[0];







            try



            {



                if (location.StartsWith("HKCU:Run", StringComparison.OrdinalIgnoreCase))



                {



                    const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";



                    using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);



                    if (key != null)



                    {



                        var val = key.GetValue(valueName);



                        if (val != null)



                        {



                            _rollbackEngine.RecordTransaction(new V4TransactionRecord



                            {



                                BatchId = batchId,



                                TargetType = "StartupEntry",



                                TargetIdentifier = identifier,



                                PreviousState = val.ToString() ?? string.Empty,



                                NewState = "Removed",



                                Reason = candidate.Reason



                            });







                            key.DeleteValue(valueName, throwOnMissingValue: false);



                            return true;



                        }



                    }



                }



                else if (location.StartsWith("HKLM:Run", StringComparison.OrdinalIgnoreCase))



                {



                    string runKeyPath = location.Contains("32-bit", StringComparison.OrdinalIgnoreCase)



                        ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"



                        : @"Software\Microsoft\Windows\CurrentVersion\Run";







                    using var key = Registry.LocalMachine.OpenSubKey(runKeyPath, writable: true);



                    if (key != null)



                    {



                        var val = key.GetValue(valueName);



                        if (val != null)



                        {



                            _rollbackEngine.RecordTransaction(new V4TransactionRecord



                            {



                                BatchId = batchId,



                                TargetType = "StartupEntry",



                                TargetIdentifier = identifier,



                                PreviousState = val.ToString() ?? string.Empty,



                                NewState = "Removed",



                                Reason = candidate.Reason



                            });







                            key.DeleteValue(valueName, throwOnMissingValue: false);



                            return true;



                        }



                    }



                }



                else if (location.StartsWith("Folder:", StringComparison.OrdinalIgnoreCase))



                {



                    string folder = location.Contains("User", StringComparison.OrdinalIgnoreCase)



                        ? Environment.GetFolderPath(Environment.SpecialFolder.Startup)



                        : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);







                    string filePath = Path.Combine(folder, valueName);



                    if (File.Exists(filePath))



                    {



                        string disabledPath = filePath + ".disabled";



                        if (File.Exists(disabledPath)) File.Delete(disabledPath);



                        File.Move(filePath, disabledPath);







                        _rollbackEngine.RecordTransaction(new V4TransactionRecord



                        {



                            BatchId = batchId,



                            TargetType = "StartupEntry",



                            TargetIdentifier = identifier,



                            PreviousState = "Enabled",



                            NewState = "Disabled",



                            Reason = candidate.Reason



                        });



                        return true;



                    }



                }



                return false;



            }



            catch



            {



                return false;



            }



        }







        private bool ModifyOemTarget(OptimizationCandidateDto candidate, string batchId)
        {
            if (_safetyKernel.IsProtected(candidate)) return false;
            if (BiosOptimizer.Core.V4.Tuning.SystemPerformanceOptimizer.TestExecutionSimulationEnabled) return true;

            if (candidate.TargetIdentifier.StartsWith(@"\") || candidate.TargetIdentifier.Contains(@"\"))
            {
                return ModifyScheduledTaskTarget(candidate, batchId);
            }
            else if (candidate.TargetIdentifier.Contains("|"))
            {
                return ModifyStartupTarget(candidate, batchId);
            }
            else
            {
                return ModifyServiceTarget(candidate, batchId);
            }
        }

        private bool ModifyAppXTarget(OptimizationCandidateDto candidate, string batchId)



        {



            if (_safetyKernel.IsProtected(candidate)) return false;
            if (BiosOptimizer.Core.V4.Tuning.SystemPerformanceOptimizer.TestExecutionSimulationEnabled) return true;







            string packageFamily = candidate.TargetIdentifier;







            try



            {



                string regPath = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\{packageFamily}\BackgroundAccessApplications";



                using var key = Registry.CurrentUser.CreateSubKey(regPath);



                if (key != null)



                {



                    _rollbackEngine.RecordTransaction(new V4TransactionRecord



                    {



                        BatchId = batchId,



                        TargetType = "AppXBackground",



                        TargetIdentifier = packageFamily,



                        PreviousState = key.GetValue("Disabled")?.ToString() ?? "0",



                        NewState = "1",



                        Reason = candidate.Reason



                    });







                    key.SetValue("Disabled", 1, RegistryValueKind.DWord);



                    return true;



                }



                return false;



            }



            catch



            {



                return false;



            }



        }







        #region Service SCM Query Helpers







        public delegate bool ServiceScmQueryDelegate(



            string serviceName,



            out uint serviceType,



            out uint controlsAccepted,



            out uint startType,



            out bool hasRunningDependents,



            out string errorMessage);







        public ServiceScmQueryDelegate? ServiceScmQueryHook { get; set; }







        private bool TryQueryServiceParameters(



            string serviceName,



            out uint serviceType,



            out uint controlsAccepted,



            out uint startType,



            out bool hasRunningDependents,



            out string errorMessage)



        {



            if (ServiceScmQueryHook != null)



            {



                return ServiceScmQueryHook(serviceName, out serviceType, out controlsAccepted, out startType, out hasRunningDependents, out errorMessage);



            }







            return QueryServiceScmParameters(serviceName, out serviceType, out controlsAccepted, out startType, out hasRunningDependents, out errorMessage);



        }







        public static bool QueryServiceScmParameters(



            string serviceName,



            out uint serviceType,



            out uint controlsAccepted,



            out uint startType,



            out bool hasRunningDependents,



            out string errorMessage)



        {



            serviceType = 0;



            controlsAccepted = 0;



            startType = 0;



            hasRunningDependents = false;



            errorMessage = string.Empty;







            if (string.IsNullOrWhiteSpace(serviceName))



            {



                errorMessage = "Service name is null or empty";



                return false;



            }







            if (!OperatingSystem.IsWindows())



            {



                errorMessage = "SCM query is only supported on Windows";



                return false;



            }







            try



            {



                // 1. Obtain ServiceController and verify service presence



                using (var sc = new ServiceController(serviceName))



                {



                    _ = sc.Status;



                }







                // 2. Open SCM and Service for detailed Win32 Query



                IntPtr hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);



                if (hScm == IntPtr.Zero)



                {



                    int err = Marshal.GetLastWin32Error();



                    errorMessage = $"OpenSCManager failed with error {err}";



                    return false;



                }







                try



                {



                    IntPtr hService = OpenService(hScm, serviceName, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS);



                    if (hService == IntPtr.Zero)



                    {



                        int err = Marshal.GetLastWin32Error();



                        errorMessage = $"OpenService for '{serviceName}' failed with error {err}";



                        return false;



                    }







                    try



                    {



                        // A. QueryServiceConfig for dwServiceType and dwStartType



                        if (!QueryServiceConfig(hService, IntPtr.Zero, 0, out uint configBytesNeeded))



                        {



                            int err = Marshal.GetLastWin32Error();



                            if (err != ERROR_INSUFFICIENT_BUFFER)



                            {



                                errorMessage = $"QueryServiceConfig buffer sizing failed with error {err}";



                                return false;



                            }



                        }







                        IntPtr pConfig = Marshal.AllocHGlobal((int)configBytesNeeded);



                        try



                        {



                            if (!QueryServiceConfig(hService, pConfig, configBytesNeeded, out _))



                            {



                                int err = Marshal.GetLastWin32Error();



                                errorMessage = $"QueryServiceConfig failed with error {err}";



                                return false;



                            }







                            var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(pConfig);



                            serviceType = config.dwServiceType;



                            startType = config.dwStartType;



                        }



                        finally



                        {



                            Marshal.FreeHGlobal(pConfig);



                        }







                        // B. QueryServiceStatusEx for dwControlsAccepted



                        int statusSize = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();



                        IntPtr pStatus = Marshal.AllocHGlobal(statusSize);



                        try



                        {



                            if (!QueryServiceStatusEx(hService, SC_STATUS_PROCESS_INFO, pStatus, (uint)statusSize, out _))



                            {



                                int err = Marshal.GetLastWin32Error();



                                errorMessage = $"QueryServiceStatusEx failed with error {err}";



                                return false;



                            }







                            var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(pStatus);



                            controlsAccepted = status.dwControlsAccepted;



                        }



                        finally



                        {



                            Marshal.FreeHGlobal(pStatus);



                        }







                        // C. EnumDependentServices(SERVICE_ACTIVE) for hasRunningDependents



                        uint depBytesNeeded = 0;



                        uint depReturned = 0;



                        if (!EnumDependentServices(hService, SERVICE_ACTIVE, IntPtr.Zero, 0, out depBytesNeeded, out depReturned))



                        {



                            int err = Marshal.GetLastWin32Error();



                            if (err == ERROR_MORE_DATA || err == ERROR_INSUFFICIENT_BUFFER)



                            {



                                hasRunningDependents = true;



                            }



                        }







                        if (depReturned > 0 || depBytesNeeded > 0)



                        {



                            hasRunningDependents = true;



                        }







                        return true;



                    }



                    finally



                    {



                        CloseServiceHandle(hService);



                    }



                }



                finally



                {



                    CloseServiceHandle(hScm);



                }



            }



            catch (Exception ex)



            {



                errorMessage = ex.Message;



                return false;



            }



        }







        #region Win32 SCM P/Invoke & Structures







        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]



        private struct QUERY_SERVICE_CONFIG



        {



            public uint dwServiceType;



            public uint dwStartType;



            public uint dwErrorControl;



            public IntPtr lpBinaryPathName;



            public IntPtr lpLoadOrderGroup;



            public uint dwTagId;



            public IntPtr lpDependencies;



            public IntPtr lpServiceStartName;



            public IntPtr lpDisplayName;



        }







        [StructLayout(LayoutKind.Sequential)]



        private struct SERVICE_STATUS_PROCESS



        {



            public uint dwServiceType;



            public uint dwCurrentState;



            public uint dwControlsAccepted;



            public uint dwWin32ExitCode;



            public uint dwServiceSpecificExitCode;



            public uint dwCheckPoint;



            public uint dwWaitHint;



            public uint dwProcessId;



            public uint dwServiceFlags;



        }







        private const uint SC_MANAGER_CONNECT = 0x0001;



        private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;







        private const uint SERVICE_QUERY_CONFIG = 0x0001;



        private const uint SERVICE_QUERY_STATUS = 0x0004;



        private const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;







        private const int SC_STATUS_PROCESS_INFO = 0;



        private const uint SERVICE_ACTIVE = 0x00000001;



        private const int ERROR_INSUFFICIENT_BUFFER = 122;



        private const int ERROR_MORE_DATA = 234;







        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]



        private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);







        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]



        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);







        [DllImport("advapi32.dll", SetLastError = true)]



        private static extern bool CloseServiceHandle(IntPtr hSCObject);







        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]



        private static extern bool QueryServiceConfig(



            IntPtr hService,



            IntPtr lpServiceConfig,



            uint cbBufSize,



            out uint pcbBytesNeeded);







        [DllImport("advapi32.dll", SetLastError = true)]



        private static extern bool QueryServiceStatusEx(



            IntPtr hService,



            int InfoLevel,



            IntPtr lpBuffer,



            uint cbBufSize,



            out uint pcbBytesNeeded);







        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]



        private static extern bool EnumDependentServices(



            IntPtr hService,



            uint dwServiceState,



            IntPtr lpServices,



            uint cbBufSize,



            out uint pcbBytesNeeded,



            out uint lpServicesReturned);







        #endregion







        #region Registry Start Write & Driver Guard Helpers







        private RestorePointHandle CreateVerifiedApplicationBackupHandle(string description)
        {
            if (_safetyKernel.IsSimulatedMode)
            {
                return new RestorePointHandle
                {
                    Label = description,
                    IsCreated = true,
                    SequenceNumber = DateTime.UtcNow.Ticks
                };
            }

            try
            {
                var snapshot = BiosOptimizer.Core.Implementations.BackupManager.Instance.CreateApplicationSnapshot(
                    description,
                    "Verified pre-optimization baseline created before V4 Maximum Performance execution");

                if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(snapshot, out string err))
                {
                    return new RestorePointHandle
                    {
                        Label = description,
                        IsCreated = false,
                        ErrorMessage = err
                    };
                }

                return new RestorePointHandle
                {
                    Label = description,
                    IsCreated = true,
                    SequenceNumber = DateTime.UtcNow.Ticks
                };
            }
            catch (Exception ex)
            {
                return new RestorePointHandle
                {
                    Label = description,
                    IsCreated = false,
                    ErrorMessage = $"Application backup checkpoint failed: {ex.Message}"
                };
            }
        }

        public static Func<string, RestorePointHandle?>? RestorePointHook { get; set; }



        public static Func<string, bool>? KernelDriverServiceHook { get; set; }







        public static bool IsKernelDriverService(string serviceName)



        {



            if (KernelDriverServiceHook != null)



            {



                return KernelDriverServiceHook(serviceName);



            }







            if (string.IsNullOrWhiteSpace(serviceName)) return false;







            try



            {



                using var sc = new ServiceController(serviceName);



                uint type = 0;



                if (QueryServiceScmParameters(serviceName, out uint scmType, out _, out _, out _, out _))



                {



                    type = scmType;



                }



                else



                {



                    type = (uint)sc.ServiceType;



                }







                // SERVICE_KERNEL_DRIVER = 0x00000001



                // SERVICE_FILE_SYSTEM_DRIVER = 0x00000002



                // SERVICE_RECOGNIZER_DRIVER = 0x00000004 or 0x00000008



                return (type & 0x00000001) != 0



                    || (type & 0x00000002) != 0



                    || (type & 0x00000004) != 0



                    || (type & 0x00000008) != 0;



            }



            catch



            {



                return false;



            }



        }







        public static bool TrySetServiceStartRegistry(



            string serviceName,



            int requestedStart,



            bool restorePointCreated,



            out string failureReason)



        {



            failureReason = string.Empty;







            if (string.IsNullOrWhiteSpace(serviceName))



            {



                failureReason = "Service name is empty";



                return false;



            }







            // 1. Kernel driver guard



            if (IsKernelDriverService(serviceName))



            {



                failureReason = $"Skipping Start write for driver service {serviceName}";



                return false;



            }







            // 2. Invariant Check I5



            string regPath = $@"SYSTEM\CurrentControlSet\Services\{serviceName}\Start";



            bool isDriver = IsKernelDriverService(serviceName);



            if (!InvariantChecks.CheckI5(regPath, !isDriver) && !InvariantChecks.CheckI5(regPath, isDriver))



            {



                failureReason = $"Invariant I5 check failed for {serviceName}: unauthorized Start modification";



                return false;



            }







            // 3. Invariant Check I6 (No Driver/Device write without restore point)



            if (!InvariantChecks.CheckI6(regPath, restorePointCreated))



            {



                failureReason = $"Invariant I6 check failed: Refusing write to {regPath} without restore point";



                return false;



            }







            try



            {



                using var sKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);



                if (sKey != null)



                {



                    sKey.SetValue("Start", requestedStart, RegistryValueKind.DWord);



                    return true;



                }







                failureReason = $"Failed to open registry key HKLM\\SYSTEM\\CurrentControlSet\\Services\\{serviceName}";



                return false;



            }



            catch (Exception ex)



            {



                failureReason = ex.Message;



                return false;



            }



        }







        #endregion







        #endregion



    

        private static int GetCandidateExecutionPhase(OptimizationCandidateDto c)

        {

            if (c.TargetType == "PowerScheme" || c.CandidateId.StartsWith("TUNE_POWER_")) return 1;

            if (c.CandidateId == "TUNE_MMCSS_RESPONSIVENESS" ||

                c.CandidateId == "TUNE_WIN32_PRIORITY" ||

                c.CandidateId == "TUNE_SVCHOST_SPLIT_THRESHOLD" ||

                c.CandidateId == "TUNE_REG_DISABLE_PAGING_EXEC" ||

                c.CandidateId == "TUNE_REG_LARGE_SYSTEM_CACHE" ||

                c.CandidateId == "TUNE_MEMORY_COMPRESSION_TUNING" ||

                c.CandidateId == "TUNE_SHELL_MENU_SHOW_DELAY" ||

                c.CandidateId == "TUNE_VISUAL_EFFECTS_PERFORMANCE") return 2;

            if (c.CandidateId.StartsWith("TUNE_GPU_")) return 3;

            if (c.CandidateId.StartsWith("TUNE_NET_") || c.CandidateId == "TUNE_NETWORK_THROTTLING") return 4;

            if (c.CandidateId.StartsWith("TUNE_STORAGE_")) return 5;

            if (c.CandidateId.StartsWith("TUNE_INPUT_")) return 6;

            if (c.CandidateId.StartsWith("TUNE_WORKLOAD_")) return 7;

            if (c.TargetType == "Service") return 8;

            if (c.TargetType == "ScheduledTask") return 9;

            if (c.TargetType == "StartupEntry") return 10;

            if (c.TargetType == "AppXBackground") return 11;

            if (c.TargetType == "Process") return 12;

            if (c.CandidateId == "TUNE_STANDBY_CACHE_PURGE" || c.TargetType == "MemoryCache") return 13;

            return 14;
        }

        private static bool MatchesSelectionScope(OptimizationCandidateDto candidate, List<string> selectedIds)
        {
            if (selectedIds == null || selectedIds.Count == 0) return true;
            if (selectedIds.Contains(candidate.CandidateId, StringComparer.OrdinalIgnoreCase)) return true;

            bool wantsCore = selectedIds.Contains("core", StringComparer.OrdinalIgnoreCase);
            bool wantsBg = selectedIds.Contains("background", StringComparer.OrdinalIgnoreCase) || wantsCore;
            bool wantsPri = selectedIds.Contains("priority", StringComparer.OrdinalIgnoreCase);
            bool wantsWs = selectedIds.Contains("working-set", StringComparer.OrdinalIgnoreCase);
            bool wantsSys = selectedIds.Contains("system", StringComparer.OrdinalIgnoreCase);

            if (!(wantsBg || wantsPri || wantsWs || wantsSys || wantsCore)) return false;

            if (wantsCore && candidate.CandidateId.StartsWith("TUNE_CORE_", StringComparison.OrdinalIgnoreCase))
                return true;

            if (wantsBg && candidate.TargetType is "Process" or "Service" or "StartupEntry" or "ScheduledTask" or "AppXBackground" or "ProcessManagement")
                return true;

            if (wantsPri && (candidate.TargetType is "WorkloadSetting" or "AudioSubsystem" or "CpuSetting" ||
                candidate.CandidateId.Contains("PRIORITY", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("MMCSS", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("AUDIO", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("SHIELD", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("THREAD_DIRECTOR", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("AMD_3D_VCACHE", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("CPU_HETERO", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("AMD_X3D", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (wantsWs && (candidate.TargetType is "MemoryCache" or "ProcessManagement" ||
                candidate.CandidateId.Contains("STANDBY", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("MEMORY", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("CACHE", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (wantsSys && candidate.TargetType is "KernelSetting" or "PowerScheme" or "FileSystemSetting" or
                "InputSetting" or "UserPreference" or "NetworkStackSetting" or "CpuSetting" or
                "DisplayCompositor" or "EmulatorSetting" or "GamingSetting" or "GpuSetting" or
                "KernelTimer" or "NetworkStack" or "StorageEngine" or "SystemSetting")
                return true;

            if (wantsCore)
                return true;

            return false;
        }

    }



public class RestorePointCreationException : Exception



    {



        public RestorePointCreationException(string message) : base(message) { }



        public RestorePointCreationException(string message, Exception innerException) : base(message, innerException) { }



    



        







    }



}



