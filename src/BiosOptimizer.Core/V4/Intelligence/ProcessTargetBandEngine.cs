#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Intelligence
{
    /// <summary>
    /// Adaptive Safe-Opportunity Engine.
    /// Completely eliminates artificial fixed process-count targets (50-70, 70-90, 90-120, etc.).
    /// Grounded in physical hardware drivers, protected Windows security subsystems,
    /// active gaming/creative workloads, and running user applications.
    /// 
    /// CORE ARCHITECTURE:
    /// 1. There is NO arbitrary or universal process-count target: valid process count depends on
    ///    hardware drivers, installed devices, active workloads, and user applications.
    /// 2. Convergence is strictly safe-opportunity-driven: defined by the elimination of verified
    ///    safe background waste, startup bloat, redundant instances, or suspended zombie task hosts.
    /// 3. Never terminate protected, system, or active workload processes to achieve numeric targets.
    /// 4. An unchanged process count is a 100% valid, successful convergence outcome when no further
    ///    safe opportunities exist.
    /// </summary>
    public class ProcessOpportunityEngine
    {
        public virtual ProcessTargetBandDto CalculateAdaptiveBaseline(
            int startingProcessCount,
            int protectedCount = 0,
            int workloadCount = 0,
            int activeAppsCount = 0,
            int safeOpportunitiesFound = 0)
        {
            var baseline = new ProcessTargetBandDto
            {
                StartingProcessCount = startingProcessCount,
                CanReduceFurtherBeyondTarget = true,
                ContinuationRule = "Safe-opportunity convergence active: engine terminates when all verified safe opportunities have been addressed."
            };

            // Ground the baseline dynamically in physically protected system components, drivers, and user workloads
            int safeProtectedBaseline = Math.Max(protectedCount + workloadCount + activeAppsCount, 25);
            baseline.AdaptiveProtectedBaseline = safeProtectedBaseline;
            baseline.TargetFootprintFloor = safeProtectedBaseline;
            baseline.TargetFootprintCeiling = startingProcessCount;
            baseline.SafeOpportunitiesDiscovered = safeOpportunitiesFound;
            baseline.TargetBandFormula = $"Evidence-driven baseline: {protectedCount} protected system components, {workloadCount} active workload processes, {activeAppsCount} active applications";
            baseline.TargetBandDescription = $"Adaptive Baseline: ~{safeProtectedBaseline} essential processes (Preserving all protected drivers, audio/input, and active workloads)";
            baseline.IsWithinTargetBand = true;

            return baseline;
        }

        public virtual bool IsSafeOpportunityConverged(
            int remainingOpportunitiesCount,
            bool anyActionsFailed = false)
        {
            // Convergence is achieved when there are no remaining verified safe opportunities
            return remainingOpportunitiesCount <= 0 && !anyActionsFailed;
        }

        public virtual bool IsWithinTargetBand(int currentProcessCount, ProcessTargetBandDto band)
        {
            if (band == null) return true;
            // Adaptive baseline invariant: system is within baseline as long as essential components are preserved
            return currentProcessCount >= band.TargetFootprintFloor;
        }
    }

    /// <summary>
    /// Backward-compatible wrapper for ProcessOpportunityEngine.
    /// Preserves existing method signatures while delegating to the Adaptive Safe-Opportunity Engine.
    /// </summary>
    public class ProcessTargetBandEngine : ProcessOpportunityEngine
    {
        public virtual ProcessTargetBandDto CalculateTargetBand(
            int startingProcessCount,
            int protectedCount = 0,
            int workloadCount = 0,
            int activeAppsCount = 0)
        {
            return CalculateAdaptiveBaseline(startingProcessCount, protectedCount, workloadCount, activeAppsCount, 0);
        }
    }
}
