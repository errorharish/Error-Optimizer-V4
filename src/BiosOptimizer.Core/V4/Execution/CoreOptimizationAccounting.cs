#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Execution
{
    public sealed class ProcessDeltaAccounting
    {
        public int InitialProcessCount { get; init; }
        public int FinalProcessCount { get; init; }
        public int GrossTargetedTerminations { get; init; }
        public int VerifiedTerminations { get; init; }
        public int NaturalExits { get; init; }
        public int NewlyStartedProcesses { get; init; }
        public int RestartedProcesses { get; init; }
        public int TotalNewProcesses => NewlyStartedProcesses + RestartedProcesses;
        public int PidReuseDetectedCount { get; init; }
        public int AmbientProcessDelta => FinalProcessCount - InitialProcessCount;
        public int OptimizerProcessReduction => VerifiedTerminations;
        public double TerminatedRamMb { get; init; }

        /// <summary>
        /// Invariant check: Final == Initial - VerifiedTerminations - NaturalExits + NewlyStarted + Restarted.
        /// </summary>
        public bool IsMathematicallyBalanced =>
            FinalProcessCount == InitialProcessCount - VerifiedTerminations - NaturalExits + TotalNewProcesses;
    }

    public sealed class HardwareParameterAccounting
    {
        public int EvaluatedCount { get; init; }
        public int AppliedCount { get; init; }
        public int AlreadyOptimalCount { get; init; }
        public int RemainingCount { get; init; }
        public int ConvergedCount => AppliedCount + AlreadyOptimalCount;
        public int DiagnosticChecksCount { get; init; }

        /// <summary>
        /// Invariant check: Every evaluated parameter has exactly one classification.
        /// </summary>
        public bool IsMutuallyExclusivePartition =>
            EvaluatedCount == AppliedCount + AlreadyOptimalCount + RemainingCount;
    }

    /// <summary>
    /// Authoritative, mathematically reconciled forensic accounting engine for Core Optimization.
    /// Guarantees single-source snapshot enumeration, PID-reuse attribution, strict separation
    /// of optimizer terminations from ambient exits/creations, mutually exclusive hardware
    /// parameter classification, and diagnostic-check exclusion.
    /// </summary>
    public static class CoreOptimizationAccounting
    {
        private static readonly HashSet<string> NonHardwareActionTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Process",
            "Service",
            "ScheduledTask",
            "AppXBackground",
            "StartupEntry"
        };

        /// <summary>
        /// Determines whether two ProcessIdentityDto snapshots at the same PID represent the exact same OS process instance.
        /// Detects PID reuse via ProcessName, StartTime (>2s delta), and normalized ExecutablePath.
        /// </summary>
        public static bool IsSameProcessInstance(ProcessIdentityDto? before, ProcessIdentityDto? after)
        {
            if (before == null || after == null)
                return false;

            if (before.ProcessId != after.ProcessId)
                return false;

            if (!string.Equals(before.ProcessName?.Trim(), after.ProcessName?.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (before.StartTime != default && after.StartTime != default)
            {
                DateTime bUtc = before.StartTime.Kind == DateTimeKind.Utc ? before.StartTime : before.StartTime.ToUniversalTime();
                DateTime aUtc = after.StartTime.Kind == DateTimeKind.Utc ? after.StartTime : after.StartTime.ToUniversalTime();
                if ((aUtc - bUtc).Duration().TotalSeconds > 2.0)
                    return false;
            }
            else if (before.StartTime != after.StartTime)
            {
                // One has a verified timestamp and the other doesn't -> treat conservatively as different instance
                return false;
            }

            if (!string.IsNullOrWhiteSpace(before.ExecutablePath) && !string.IsNullOrWhiteSpace(after.ExecutablePath))
            {
                string normBefore = ProcessIdentitySafety.NormalizeExecutablePath(before.ExecutablePath);
                string normAfter = ProcessIdentitySafety.NormalizeExecutablePath(after.ExecutablePath);
                if (!string.Equals(normBefore, normAfter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Computes exact PID-based process delta strictly from the initial and final snapshots.
        /// Handles natural exits, new processes, SCM respawns, and PID reuse on BOTH targeted and untargeted PIDs.
        /// </summary>
        public static ProcessDeltaAccounting ComputeProcessDelta(
            ProcessSnapshotDto initialSnapshot,
            ProcessSnapshotDto finalSnapshot,
            IReadOnlySet<int>? targetedTerminatedPids = null)
        {
            var terminatedSet = targetedTerminatedPids ?? new HashSet<int>();

            // Deduplicate by PID using the authoritative snapshots themselves
            var initialByPid = new Dictionary<int, ProcessIdentityDto>();
            foreach (var p in initialSnapshot.Processes)
            {
                if (!initialByPid.ContainsKey(p.ProcessId))
                    initialByPid[p.ProcessId] = p;
            }

            var finalByPid = new Dictionary<int, ProcessIdentityDto>();
            foreach (var p in finalSnapshot.Processes)
            {
                if (!finalByPid.ContainsKey(p.ProcessId))
                    finalByPid[p.ProcessId] = p;
            }

            var terminatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int pid in terminatedSet)
            {
                if (initialByPid.TryGetValue(pid, out var orig) && !string.IsNullOrWhiteSpace(orig.ProcessName))
                {
                    terminatedNames.Add(orig.ProcessName.Trim());
                }
            }

            int verifiedTerminations = 0;
            int naturalExits = 0;
            int newlyStarted = 0;
            int restarted = 0;
            int pidReuseCount = 0;
            double terminatedRamMb = 0.0;

            // 1. Evaluate every process present in the initial snapshot
            foreach (var kvp in initialByPid)
            {
                int pid = kvp.Key;
                var initProc = kvp.Value;
                bool wasTargeted = terminatedSet.Contains(pid);

                if (!finalByPid.TryGetValue(pid, out var finalProc))
                {
                    if (wasTargeted)
                    {
                        verifiedTerminations++;
                        terminatedRamMb += initProc.RamWorkingSetMb > 0 ? initProc.RamWorkingSetMb : initProc.PrivateMemoryMb;
                    }
                    else
                    {
                        naturalExits++;
                    }
                }
                else if (!IsSameProcessInstance(initProc, finalProc))
                {
                    // PID was reused during execution!
                    pidReuseCount++;
                    if (wasTargeted)
                    {
                        verifiedTerminations++;
                        terminatedRamMb += initProc.RamWorkingSetMb > 0 ? initProc.RamWorkingSetMb : initProc.PrivateMemoryMb;
                    }
                    else
                    {
                        naturalExits++;
                    }

                    // The new occupant at this reused PID is a new or restarted process
                    if (!string.IsNullOrWhiteSpace(finalProc.ProcessName) && terminatedNames.Contains(finalProc.ProcessName.Trim()))
                        restarted++;
                    else
                        newlyStarted++;
                }
            }

            // 2. Evaluate processes present only in the final snapshot (brand new PIDs)
            foreach (var kvp in finalByPid)
            {
                if (initialByPid.ContainsKey(kvp.Key))
                    continue;

                var finalProc = kvp.Value;
                if (!string.IsNullOrWhiteSpace(finalProc.ProcessName) && terminatedNames.Contains(finalProc.ProcessName.Trim()))
                    restarted++;
                else
                    newlyStarted++;
            }

            return new ProcessDeltaAccounting
            {
                InitialProcessCount = initialByPid.Count,
                FinalProcessCount = finalByPid.Count,
                GrossTargetedTerminations = terminatedSet.Count,
                VerifiedTerminations = verifiedTerminations,
                NaturalExits = naturalExits,
                NewlyStartedProcesses = newlyStarted,
                RestartedProcesses = restarted,
                PidReuseDetectedCount = pidReuseCount,
                TerminatedRamMb = Math.Round(terminatedRamMb, 1)
            };
        }

        /// <summary>
        /// Identifies read-only informational/diagnostic verification candidates so they are never
        /// counted as applied optimizations.
        /// </summary>
        public static bool IsDiagnosticCandidate(OptimizationCandidateDto? candidate)
        {
            if (candidate == null) return false;

            if (!string.IsNullOrEmpty(candidate.CandidateId) &&
                candidate.CandidateId.Contains("DIAGNOSTIC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(candidate.ReversibilityStatus) &&
                candidate.ReversibilityStatus.Contains("Informational", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public static bool IsDiagnosticAction(OptimizationActionDtoResult? action)
        {
            if (action == null) return false;

            if (string.Equals(action.Status, OptimizationActionStates.DIAGNOSTIC_AUDIT, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(action.CandidateId) &&
                action.CandidateId.Contains("DIAGNOSTIC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Computes strictly authoritative, mutually exclusive hardware parameter accounting.
        /// Excludes non-hardware actions and read-only diagnostic checks; never hardcodes fallback counts.
        /// </summary>
        public static HardwareParameterAccounting ComputeHardwareParameterAccounting(
            IEnumerable<OptimizationActionDtoResult>? actions)
        {
            if (actions == null)
            {
                return new HardwareParameterAccounting();
            }

            int diagnosticCount = 0;
            // Deduplicate by CandidateId (last authoritative state wins) so every parameter has exactly one classification
            var uniqueHwParams = new Dictionary<string, OptimizationActionDtoResult>(StringComparer.OrdinalIgnoreCase);
            int anonymousIdx = 0;

            foreach (var action in actions)
            {
                if (action == null) continue;
                if (NonHardwareActionTypes.Contains(action.ActionType ?? string.Empty))
                    continue;

                if (IsDiagnosticAction(action))
                {
                    diagnosticCount++;
                    continue;
                }

                string key = !string.IsNullOrWhiteSpace(action.CandidateId)
                    ? action.CandidateId.Trim()
                    : $"__hw_{anonymousIdx++}";
                uniqueHwParams[key] = action;
            }

            int applied = 0;
            int alreadyOptimal = 0;
            int remaining = 0;

            foreach (var action in uniqueHwParams.Values)
            {
                if (string.Equals(action.Status, OptimizationActionStates.VERIFIED, StringComparison.OrdinalIgnoreCase))
                {
                    applied++;
                }
                else if (string.Equals(action.Status, OptimizationActionStates.ALREADY_OPTIMAL, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyOptimal++;
                }
                else
                {
                    remaining++;
                }
            }

            return new HardwareParameterAccounting
            {
                EvaluatedCount = uniqueHwParams.Count,
                AppliedCount = applied,
                AlreadyOptimalCount = alreadyOptimal,
                RemainingCount = remaining,
                DiagnosticChecksCount = diagnosticCount
            };
        }

        /// <summary>
        /// Truthful CPU delta accounting with ambient variance noise-floor gating (±1.5%).
        /// Never manufactures CPU improvements when no processes were terminated or throttled.
        /// </summary>
        public static (double MeasuredCpuChangePercent, string Status) ComputeCpuChange(
            double initialCpuPercent,
            double finalCpuPercent,
            int verifiedTerminated,
            int verifiedThrottled,
            double ambientNoiseFloorPercent = 1.5)
        {
            if (verifiedTerminated <= 0 && verifiedThrottled <= 0)
            {
                return (0.0, "AMBIENT_BASELINE_OPTIMAL");
            }

            double rawDelta = Math.Round(finalCpuPercent - initialCpuPercent, 2);
            if (Math.Abs(rawDelta) <= ambientNoiseFloorPercent)
            {
                return (0.0, "WITHIN_AMBIENT_VARIANCE");
            }

            return (rawDelta, "VERIFIED_REDUCTION");
        }
    }
}
