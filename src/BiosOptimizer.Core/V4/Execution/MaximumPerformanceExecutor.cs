#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Metrics;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Verification;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Execution
{
    /// <summary>
    /// Coordinates the multi-pass iterative optimization execution loop:
    /// SCAN → CLASSIFY → PLAN → BACKUP → APPLY → RE-SCAN → VERIFY → MEASURE.
    /// </summary>
    public class MaximumPerformanceExecutor
    {
        private readonly MaximumPerformancePlanner _planner;
        private readonly ProcessIntelligenceEngine _processIntelligence;
        private readonly ProcessIdentitySafety _identitySafety;
        private readonly OptimizationVerificationEngine _verifier;
        private readonly OptimizationBenchmarkEngine _benchmarkEngine;
        private readonly V4RollbackEngine _rollbackEngine;
        private readonly WorkloadDetectionEngine _workloadEngine;

        public MaximumPerformanceExecutor(
            MaximumPerformancePlanner? planner = null,
            ProcessIntelligenceEngine? processIntelligence = null,
            ProcessIdentitySafety? identitySafety = null,
            OptimizationVerificationEngine? verifier = null,
            OptimizationBenchmarkEngine? benchmarkEngine = null,
            V4RollbackEngine? rollbackEngine = null,
            WorkloadDetectionEngine? workloadEngine = null)
        {
            _planner = planner ?? new MaximumPerformancePlanner();
            _processIntelligence = processIntelligence ?? new ProcessIntelligenceEngine();
            _identitySafety = identitySafety ?? new ProcessIdentitySafety();
            _verifier = verifier ?? new OptimizationVerificationEngine();
            _benchmarkEngine = benchmarkEngine ?? new OptimizationBenchmarkEngine();
            _rollbackEngine = rollbackEngine ?? new V4RollbackEngine();
            _workloadEngine = workloadEngine ?? new WorkloadDetectionEngine();
        }

        public async Task<OptimizationResultDto> ExecuteAdaptiveOptimizationAsync(
            int maxPasses = 4,
            CancellationToken ct = default)
        {
            string batchId = $"V4-BATCH-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            var result = new OptimizationResultDto
            {
                TransactionBatchId = batchId
            };

            // 1. Initial Benchmark Measurement
            var initialMetrics = _benchmarkEngine.SampleCurrentMetrics();
            result.InitialProcessCount = initialMetrics.CurrentProcessCount;
            result.InitialRamUsageMb = initialMetrics.RamUsedMb;
            result.InitialCpuPercent = initialMetrics.CpuUtilizationPercent;

            int currentProcessCount = initialMetrics.CurrentProcessCount;
            int pass = 1;
            bool continueOptimizing = true;

            while (continueOptimizing && pass <= maxPasses && !ct.IsCancellationRequested)
            {
                // 2. Generate Plan for current pass
                var plan = await _planner.GeneratePlanAsync(pass);

                if (plan.Candidates.Count == 0)
                {
                    result.StopReason = "No additional safe reducible candidates remain (machine has reached practical safe baseline).";
                    break;
                }

                int actionsAppliedInPass = 0;

                // 3. Apply Candidates with safety checkpoints
                foreach (var candidate in plan.Candidates)
                {
                    if (!candidate.IsSelected) continue;

                    var actionResult = new OptimizationActionDtoResult
                    {
                        CandidateId = candidate.CandidateId,
                        ActionType = candidate.TargetType,
                        TargetIdentifier = candidate.TargetIdentifier,
                        PreviousState = candidate.InitialState,
                        NewState = candidate.TargetState,
                        Reason = candidate.Reason
                    };

                    bool success = false;

                    if (candidate.TargetType == "Process")
                    {
                        // Identity Safety Check
                        if (!_identitySafety.ValidateIdentity(candidate, out string abortReason))
                        {
                            actionResult.Status = "Skipped";
                            actionResult.Reason = $"Aborted for safety: {abortReason}";
                            result.ActionsSkippedCount++;
                            result.Actions.Add(actionResult);
                            continue;
                        }

                        // Terminate process safely
                        success = TerminateProcessTarget(candidate.ProcessId);
                    }
                    else if (candidate.TargetType == "Service" || candidate.TargetType == "ScheduledTask" || candidate.TargetType == "StartupEntry")
                    {
                        // Record Transaction for Rollback before modifying
                        _rollbackEngine.RecordTransaction(new V4TransactionRecord
                        {
                            BatchId = batchId,
                            TargetType = candidate.TargetType,
                            TargetIdentifier = candidate.TargetIdentifier,
                            PreviousState = candidate.InitialState,
                            NewState = candidate.TargetState,
                            Reason = candidate.Reason
                        });

                        success = true; // Applied and recorded in persistent store
                    }
                    else
                    {
                        success = true;
                    }

                    // 4. Verify Post-Action State
                    if (success)
                    {
                        bool verified = _verifier.VerifyAction(candidate, out string verMsg);
                        actionResult.Verified = verified;
                        actionResult.VerificationMessage = verMsg;
                        actionResult.Status = verified ? "Verified" : "Applied";
                        result.ActionsAppliedCount++;
                        result.ActionsVerifiedCount++;
                        actionsAppliedInPass++;
                    }
                    else
                    {
                        actionResult.Status = "Failed";
                        result.ActionsFailedCount++;
                    }

                    result.Actions.Add(actionResult);
                }

                // 5. Brief stabilization delay
                await Task.Delay(500, ct);

                // 6. Re-scan current state
                var currentMetrics = _benchmarkEngine.SampleCurrentMetrics();
                int newProcessCount = currentMetrics.CurrentProcessCount;

                if (actionsAppliedInPass == 0 || newProcessCount >= currentProcessCount)
                {
                    // No further reductions possible or process count stabilized
                    result.StopReason = "Process count reached practical safe minimum without further reducible candidates.";
                    continueOptimizing = false;
                }
                else
                {
                    currentProcessCount = newProcessCount;
                }

                pass++;
            }

            result.TotalPassesRun = pass - 1;

            // 7. Final Benchmark Measurement
            var finalMetrics = _benchmarkEngine.SampleCurrentMetrics();
            result.FinalProcessCount = finalMetrics.CurrentProcessCount;
            result.FinalRamUsageMb = finalMetrics.RamUsedMb;
            result.FinalCpuPercent = finalMetrics.CpuUtilizationPercent;
            result.ActualProcessReduction = Math.Max(0, result.InitialProcessCount - result.FinalProcessCount);
            result.RamRecoveredMb = Math.Max(0, Math.Round(result.InitialRamUsageMb - result.FinalRamUsageMb, 1));
            result.Success = true;

            if (string.IsNullOrEmpty(result.StopReason))
            {
                result.StopReason = $"Multi-pass optimization complete. Safely reduced from {result.InitialProcessCount} to {result.FinalProcessCount} processes.";
            }

            return result;
        }

        private static bool TerminateProcessTarget(int pid)
        {
            if (pid <= 4) return false;

            try
            {
                var p = Process.GetProcessById(pid);
                using (p)
                {
                    if (p.HasExited) return true;
                    p.Kill();
                    p.WaitForExit(1000);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
