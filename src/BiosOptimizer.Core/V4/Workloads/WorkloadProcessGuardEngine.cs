#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Workloads
{
    /// <summary>
    /// Continuous Workload Guard Engine for active Gaming and Editing workloads.
    /// Shields the active workload equation:
    ///    Protected Workload = [Optimized Baseline] + [Required Workload Processes] + [Required Dependencies]
    /// 
    /// Detects and suppresses background process creep / drift (e.g., 82 -> 115 -> 140)
    /// while preserving all legitimate game engine, anti-cheat, launcher, and rendering processes.
    /// </summary>
    public class WorkloadProcessGuardEngine
    {
        private readonly WorkloadDetectionEngine _workloadEngine;
        private readonly ProcessIntelligenceEngine _processIntelligence;
        private readonly ProtectedProcessPolicy _safetyPolicy;
        private readonly SafetyKernel _safetyKernel;

        private int _optimizedBaselineCount = 68;
        private int _suppressedDriftCount = 0;
        private bool _isGuardActive = true;

        public Func<Task<ProcessSnapshotDto>>? CustomSnapshotProvider { get; set; }
        public Func<ProcessIdentityDto, bool>? ProcessKillHandler { get; set; }
        public static Func<ProcessIdentityDto, bool>? ProcessKillHook { get; set; }

        public WorkloadProcessGuardEngine(
            WorkloadDetectionEngine? workloadEngine = null,
            ProcessIntelligenceEngine? processIntelligence = null,
            ProtectedProcessPolicy? safetyPolicy = null,
            SafetyKernel? safetyKernel = null)
        {
            _workloadEngine = workloadEngine ?? new WorkloadDetectionEngine();
            _safetyPolicy = safetyPolicy ?? new ProtectedProcessPolicy();
            _processIntelligence = processIntelligence ?? new ProcessIntelligenceEngine(_safetyPolicy, _workloadEngine);
            _safetyKernel = safetyKernel ?? SafetyKernel.Instance;
        }

        public int OptimizedBaselineCount
        {
            get => _optimizedBaselineCount;
            set => _optimizedBaselineCount = Math.Max(35, value);
        }

        public bool IsGuardActive
        {
            get => _isGuardActive;
            set => _isGuardActive = value;
        }

        public int SuppressedDriftCount => _suppressedDriftCount;

        /// <summary>
        /// Analyzes live running processes to measure workload health and detect background drift.
        /// </summary>
        public async Task<WorkloadGuardTelemetryDto> AnalyzeWorkloadStateAsync()
        {
            var snapshot = await _processIntelligence.CaptureSnapshotAsync();
            return EvaluateSnapshot(snapshot);
        }

        /// <summary>
        /// Evaluates a pre-captured snapshot against the active workload model.
        /// </summary>
        public WorkloadGuardTelemetryDto EvaluateSnapshot(ProcessSnapshotDto snapshot)
        {
            var workloadStatus = _workloadEngine.DetectActiveWorkloads(snapshot.Processes.Select(p => p.ProcessName));

            var telemetry = new WorkloadGuardTelemetryDto
            {
                IsGuardActive = _isGuardActive,
                OptimizedBaselineCount = _optimizedBaselineCount,
                CurrentTotalProcessCount = snapshot.TotalProcessCount,
                SuppressedDriftCount = _suppressedDriftCount
            };

            if (workloadStatus.IsEmulatorDetected)
            {
                telemetry.WorkloadType = "Emulator";
                telemetry.ShieldedWorkloadNames.AddRange(workloadStatus.ActiveWorkloadNames);
            }
            else if (workloadStatus.IsGamingDetected)
            {
                telemetry.WorkloadType = "Gaming";
                telemetry.ShieldedWorkloadNames.AddRange(workloadStatus.ActiveWorkloadNames);
            }
            else if (workloadStatus.IsEditingDetected)
            {
                telemetry.WorkloadType = "Editing";
                telemetry.ShieldedWorkloadNames.AddRange(workloadStatus.ActiveWorkloadNames);
            }
            else
            {
                telemetry.WorkloadType = "None";
            }

            // Count genuine workload processes and their child dependencies
            int genuineWorkload = 0;
            int requiredDeps = 0;
            var driftNames = new List<string>();

            foreach (var p in snapshot.Processes)
            {
                if (p.Classification == ProcessClassification.WORKLOAD_REQUIRED)
                {
                    genuineWorkload++;
                }
                else if (p.Classification == ProcessClassification.PROTECTED || p.Classification == ProcessClassification.REQUIRED)
                {
                    requiredDeps++;
                }
                else if (p.Classification == ProcessClassification.REDUCIBLE)
                {
                    driftNames.Add(p.ProcessName);
                }
            }

            telemetry.GenuineWorkloadProcessCount = genuineWorkload;
            telemetry.RequiredDependencyCount = requiredDeps;
            telemetry.DetectedDriftProcessNames = driftNames.Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();

            if (telemetry.WorkloadType != "None")
            {
                int expectedTotal = _optimizedBaselineCount + genuineWorkload;
                int drift = Math.Max(0, snapshot.TotalProcessCount - expectedTotal);

                if (drift > 5)
                {
                    telemetry.StatusSummary = $"Workload Active: Process drift detected (+{drift} unrelated background processes). Suppression recommended.";
                }
                else
                {
                    telemetry.StatusSummary = $"Workload Active: Optimal footprint ({snapshot.TotalProcessCount} procs: {_optimizedBaselineCount} baseline + {genuineWorkload} workload).";
                }
            }
            else
            {
                telemetry.StatusSummary = $"Idle Baseline: {snapshot.TotalProcessCount} active processes (Optimized baseline: {_optimizedBaselineCount}).";
            }

            return telemetry;
        }

        /// <summary>
        /// Suppresses unrelated background drift during an active Gaming or Editing session,
        /// ensuring game and editing binaries and their dependencies are strictly untouched.
        /// </summary>
        public async Task<int> SuppressBackgroundDriftAsync()
        {
            var snapshot = CustomSnapshotProvider != null
                ? await CustomSnapshotProvider()
                : await _processIntelligence.CaptureSnapshotAsync();
            var telemetry = EvaluateSnapshot(snapshot);

            if (telemetry.WorkloadType == "None" && snapshot.TotalProcessCount <= _optimizedBaselineCount)
            {
                return 0;
            }

            int suppressed = 0;
            bool isEmulator = telemetry.WorkloadType == "Emulator";
            bool isGaming = telemetry.WorkloadType == "Gaming" || isEmulator;
            bool isEditing = telemetry.WorkloadType == "Editing";

            foreach (var proc in snapshot.Processes)
            {
                // ONLY terminate verified REDUCIBLE candidates that are NOT part of the active workload or dependencies
                if (proc.Classification == ProcessClassification.REDUCIBLE &&
                    !_workloadEngine.IsWorkloadCritical(proc.ProcessName, isGaming, isEditing, isEmulatorActive: isEmulator) &&
                    !_safetyPolicy.IsProtectedProcess(proc.ProcessName))
                {
                    // Non-bypassable SafetyKernel evaluation enforcing R01-R27 invariants
                    var verdict = _safetyKernel.EvaluateProcessKill(
                        proc,
                        isGaming,
                        isEditing,
                        out string ruleCode,
                        out string reason);

                    if (verdict != SafetyVerdict.Allow)
                    {
                        continue;
                    }

                    try
                    {
                        if (ProcessKillHandler != null)
                        {
                            if (ProcessKillHandler(proc))
                            {
                                suppressed++;
                                _suppressedDriftCount++;
                            }
                        }
                        else if (ProcessKillHook != null)
                        {
                            if (ProcessKillHook(proc))
                            {
                                suppressed++;
                                _suppressedDriftCount++;
                            }
                        }
                        else
                        {
                            var p = Process.GetProcessById(proc.ProcessId);
                            using (p)
                            {
                                if (!p.HasExited)
                                {
                                    p.Kill();
                                    p.WaitForExit(500);
                                    suppressed++;
                                    _suppressedDriftCount++;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            return suppressed;
        }
    }
}
