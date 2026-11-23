#nullable enable
using System;
using System.Collections.Generic;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Detects and resolves conflicts between workload profiles, environmental states (AC/Battery),
    /// and mutually exclusive optimization settings.
    /// </summary>
    public sealed class OptimizationConflictEngine
    {
        private static readonly Lazy<OptimizationConflictEngine> _instance = new(() => new OptimizationConflictEngine());
        public static OptimizationConflictEngine Instance => _instance.Value;

        public bool HasConflict(
            string candidateId,
            string workloadProfile,
            bool isAcPower,
            out string conflictReason)
        {
            conflictReason = string.Empty;

            // 1. Creative Rendering vs Gaming Quantum Conflict
            if (candidateId == "TUNE_WIN32_PRIORITY")
            {
                if (workloadProfile.Equals("VideoEditing", StringComparison.OrdinalIgnoreCase) ||
                    workloadProfile.Equals("CreativeWorkflow", StringComparison.OrdinalIgnoreCase))
                {
                    // In Creative mode, priority quantum must be 0x18 (Equal 1:1) rather than gaming 0x26 (3:1 boost)
                    // The optimizer automatically adapts the quantum value, but flag if a conflicting gaming rule is requested
                }
            }

            // 2. Mobile Battery vs High Performance Power Scheme
            if (!isAcPower && (candidateId == "TUNE_POWER_PLAN_HIGH_PERFORMANCE" ||
                              candidateId == "TUNE_POWER_CORE_PARKING_UNPARK" ||
                              candidateId == "TUNE_POWER_DISABLE_SLEEP_AC"))
            {
                conflictReason = "Conflicts with battery power conservation state.";
                return true;
            }

            // 3. Creative Render Disk I/O vs System Realtime Buffers
            if (candidateId == "TUNE_CREATIVE_RENDER_DISK_IO_PRIORITY" &&
                workloadProfile.Equals("Gaming", StringComparison.OrdinalIgnoreCase))
            {
                // In gaming, render disk I/O elevation is not applicable
                conflictReason = "Creative render I/O prioritization is inactive during gaming workloads.";
                return true;
            }

            return false;
        }
    }
}
