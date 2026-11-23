#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Authoritative Universal Eligibility Engine for ERROR OPTIMIZER V4 Core Optimization.
    /// Evaluates hardware capabilities, OS builds, permissions, workload state, dependencies,
    /// and conflicts. Strictly ensures only genuinely ELIGIBLE candidates are actionable.
    /// </summary>
    public sealed class UniversalEligibilityEngine
    {
        private static readonly Lazy<UniversalEligibilityEngine> _instance = new(() => new UniversalEligibilityEngine());
        public static UniversalEligibilityEngine Instance => _instance.Value;

        private readonly OptimizationDependencyGraph _dependencyGraph = OptimizationDependencyGraph.Instance;
        private readonly OptimizationConflictEngine _conflictEngine = OptimizationConflictEngine.Instance;

        public UniversalEligibilityEngine()
        {
        }

        /// <summary>
        /// Recalculates eligibility for all candidates immediately before planning or execution.
        /// </summary>
        public void EvaluateEligibility(
            IEnumerable<OptimizationCandidateDto> candidates,
            string workloadProfile,
            bool isCustomSelection = false)
        {
            var hw = HardwareDetectionService.Instance.GetSnapshot();
            var memState = MemoryPressureIntelligenceEngine.Instance.SampleMemoryState();
            bool isAcPower = NativePowerMethods.IsOnAcPower();

            var candidateList = candidates.ToList();
            var candidateMap = candidateList.ToDictionary(c => c.CandidateId, StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidateList)
            {
                EvaluateCandidate(candidate, hw, memState, isAcPower, workloadProfile, candidateMap, isCustomSelection);
            }
        }

        /// <summary>
        /// Evaluates a single candidate's strict eligibility status against device and environment state.
        /// </summary>
        public void EvaluateCandidate(
            OptimizationCandidateDto candidate,
            HardwareSnapshot hw,
            MemoryPressureDiagnosticsDto memState,
            bool isAcPower,
            string workloadProfile,
            Dictionary<string, OptimizationCandidateDto> candidateMap,
            bool isCustomSelection)
        {
            // 1. Policy & Safety Gating
            if (candidate.IsProtectedFromOptimization)
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.PROTECTED_BY_POLICY;
                candidate.IsSelected = false;
                candidate.ExclusionExplanation = "Protected by safety policy or critical system identity.";
                return;
            }

            // 2. Battery / Power Gating for aggressive CPU & Power Plan operations
            if (!isAcPower && (
                candidate.CandidateId.StartsWith("TUNE_POWER_", StringComparison.OrdinalIgnoreCase) ||
                candidate.CandidateId == "TUNE_CORE_CPU_HETERO_SCHEDULING_POLICY" ||
                candidate.CandidateId == "TUNE_CORE_AMD_X3D_CACHE_SCHEDULER_LOCK" ||
                candidate.CandidateId == "TUNE_CPU_INTEL_THREAD_DIRECTOR" ||
                candidate.CandidateId == "TUNE_CPU_AMD_3D_VCACHE" ||
                candidate.CandidateId == "TUNE_CPU_ENERGY_PERF_PREFERENCE" ||
                candidate.CandidateId == "TUNE_CPU_CORE_PARKING_DISABLE"))
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.INELIGIBLE;
                candidate.IsSelected = false;
                candidate.ExclusionExplanation = "High-performance CPU/Power optimizations are disabled on DC battery power to preserve battery life and prevent thermal throttling.";
                return;
            }

            // 3. Hardware Architecture Prerequisites
            if (candidate.CandidateId == "TUNE_GPU_HAGS")
            {
                if (!hw.PrimaryGpu.SupportsHags)
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.UNSUPPORTED_HARDWARE;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = "Primary GPU or driver does not support WDDM 2.7+ hardware-accelerated scheduling.";
                    return;
                }
                if (hw.PrimaryGpu.VramGb < 6.0)
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.INELIGIBLE;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = $"Requires >=6.0 GB dedicated VRAM (detected {hw.PrimaryGpu.VramGb:F1} GB) to prevent memory oversubscription.";
                    return;
                }
            }

            if (candidate.CandidateId == "TUNE_CORE_CPU_HETERO_SCHEDULING_POLICY" ||
                candidate.CandidateId == "TUNE_CPU_INTEL_THREAD_DIRECTOR")
            {
                if (!hw.Cpu.IsHybridCpu)
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.UNSUPPORTED_HARDWARE;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = "Requires heterogeneous CPU architecture (Intel P-Core / E-Core hybrid topology).";
                    return;
                }
            }

            if (candidate.CandidateId == "TUNE_CORE_AMD_X3D_CACHE_SCHEDULER_LOCK" ||
                candidate.CandidateId == "TUNE_CPU_AMD_3D_VCACHE")
            {
                if (!hw.Cpu.IsDualCcdAmd3DVcache)
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.UNSUPPORTED_HARDWARE;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = hw.Cpu.L3CacheInstanceCount == 1 && hw.Cpu.Name.Contains("3D", StringComparison.OrdinalIgnoreCase)
                        ? "Single-CCD AMD X3D processor detected (unified 3D V-Cache across all cores). Core affinity locking is unnecessary and bypassed."
                        : "Requires dual-CCD AMD processor with asymmetric 3D V-Cache topology (Ryzen 9 7900X3D / 7950X3D).";
                    return;
                }
            }

            // 4. Memory Pressure & Active Workload Purge Gating
            if (candidate.CandidateId == "TUNE_STANDBY_CACHE_PURGE" ||
                candidate.CandidateId == "TUNE_CORE_MEMORY_STANDBY_DYNAMIC_PRESSURE_RELIEF")
            {
                if (memState.IsActiveWorkloadProtected)
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.ALREADY_OPTIMAL;
                    candidate.IsAlreadyOptimal = true;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = "Active gaming, emulator, or creative workload shielded; standby cache purge suppressed to prevent soft page fault stalls.";
                    return;
                }
                if (memState.Severity == MemoryPressureSeverity.OptimalBaseline)
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.ALREADY_OPTIMAL;
                    candidate.IsAlreadyOptimal = true;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = $"Memory baseline is already optimal ({memState.PhysicalLoadPercent:F0}% load, {memState.CommitHeadroomGb:F1} GB commit headroom). Cache purging unnecessary.";
                    return;
                }
            }

            // 5. Workload-Specific Network Gating
            if (candidate.CandidateId == "TUNE_NET_LOW_LATENCY_ACK" ||
                candidate.CandidateId == "TUNE_CORE_NETWORK_PACKET_PACING_CALIBRATE")
            {
                if (!workloadProfile.Equals("Gaming", StringComparison.OrdinalIgnoreCase) &&
                    !workloadProfile.Equals("Emulator", StringComparison.OrdinalIgnoreCase))
                {
                    candidate.EligibilityStatus = OptimizationEligibilityStatus.INELIGIBLE;
                    candidate.IsSelected = false;
                    candidate.ExclusionExplanation = "Low-latency TCP ACK pacing is restricted strictly to Gaming profiles to avoid throughput degradation during file transfers and rendering.";
                    return;
                }
            }

            // 6. Optional User-Preference Settings (never auto-selected)
            if (candidate.CandidateId == "TUNE_INPUT_MARKC_LINEAR_CURVE")
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.RECOMMENDATION_ONLY;
                if (!isCustomSelection)
                {
                    candidate.IsSelected = false;
                }
                candidate.ExclusionExplanation = "MarkC 1:1 linear curve modifies muscle memory. Classified as Optional; only applied when explicitly user-selected.";
                return;
            }

            // 7. Check Dependency Graph
            if (!_dependencyGraph.AreDependenciesSatisfied(candidate.CandidateId, candidateMap))
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.DEPENDENCY_NOT_MET;
                candidate.IsSelected = false;
                candidate.ExclusionExplanation = "One or more prerequisite optimizations are missing or failed.";
                return;
            }

            // 8. Check Conflict Engine
            if (_conflictEngine.HasConflict(candidate.CandidateId, workloadProfile, isAcPower, out string conflictReason))
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.CONFLICT_DETECTED;
                candidate.IsSelected = false;
                candidate.ExclusionExplanation = conflictReason;
                return;
            }

            // 8.5 Global Applied State & Ownership Cross-Category Evaluation
            string canTarget = GlobalOptimizationRegistry.NormalizeTarget(candidate.TargetType, candidate.TargetIdentifier);
            if (!GlobalOptimizationRegistry.Instance.IsAuthoritativeForCategory(canTarget, candidate.Category))
            {
                string authOwner = GlobalOptimizationRegistry.Instance.GetAuthoritativeOwner(canTarget);
                candidate.EligibilityStatus = OptimizationEligibilityStatus.INELIGIBLE;
                candidate.IsSelected = false;
                candidate.ExclusionExplanation = $"Target is authoritatively owned by '{authOwner}'. Cross-page execution prohibited.";
                return;
            }

            var gStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, candidate.TargetState ?? string.Empty, out _);
            if (gStatus == GlobalAppliedStatus.ALREADY_APPLIED ||
                gStatus == GlobalAppliedStatus.ALREADY_OPTIMAL ||
                gStatus == GlobalAppliedStatus.ACTIVE_PROFILE)
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.ALREADY_OPTIMAL;
                candidate.IsAlreadyOptimal = true;
                candidate.IsSelected = false;
                candidate.ExclusionExplanation = gStatus == GlobalAppliedStatus.ALREADY_APPLIED
                    ? "Target was previously optimized by ERROR OPTIMIZER and verified in live state."
                    : "Target setting is already verified at optimal state.";
                return;
            }

            // 9. Baseline / Already Optimal Check
            if (candidate.IsAlreadyOptimal)
            {
                candidate.EligibilityStatus = OptimizationEligibilityStatus.ALREADY_OPTIMAL;
                candidate.IsSelected = false;
                candidate.EligibilityExplanation = "Target setting is already verified at optimal state.";
                return;
            }

            // 10. Candidate is fully eligible and actionable
            candidate.EligibilityStatus = OptimizationEligibilityStatus.ELIGIBLE;
            if (!isCustomSelection)
            {
                candidate.IsSelected = candidate.Classification != "Optional";
            }
        }
    }

    internal static class NativePowerMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        public static bool IsOnAcPower()
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                return status.ACLineStatus != 0; // 1 = Online, 255 = Unknown (treated as AC)
            }
            return true;
        }
    }
}
