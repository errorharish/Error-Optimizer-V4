#nullable enable
using System;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Authoritative Heterogeneous CPU and Topology Engine for ERROR OPTIMIZER V4.
    /// Manages Intel P-Core/E-Core Thread Director routing, AMD 3D V-Cache cache topology,
    /// and workload-aware quantum calculation.
    /// </summary>
    public sealed class CPUHeterogeneousTopologyEngine
    {
        private static readonly Lazy<CPUHeterogeneousTopologyEngine> _instance = new(() => new CPUHeterogeneousTopologyEngine());
        public static CPUHeterogeneousTopologyEngine Instance => _instance.Value;

        public byte ComputeWorkloadPrioritySeparation(string workloadProfile)
        {
            if (workloadProfile.Equals("VideoEditing", StringComparison.OrdinalIgnoreCase) ||
                workloadProfile.Equals("CreativeWorkflow", StringComparison.OrdinalIgnoreCase))
            {
                // 0x18 (24 decimal) = Long, Fixed, Equal 1:1 quantums across all worker threads
                // Prevents render thread starvation in multi-threaded compute engines (Premiere, Resolve, Blender)
                return 0x18;
            }

            if (workloadProfile.Equals("Gaming", StringComparison.OrdinalIgnoreCase) ||
                workloadProfile.Equals("Emulator", StringComparison.OrdinalIgnoreCase))
            {
                // 0x26 (38 decimal) = Short, Variable, 3:1 foreground quantum boost
                // Prioritizes interactive render/input message loop over background daemons
                return 0x26;
            }

            // Default Windows Client desktop baseline
            return 0x02;
        }

        public bool ShouldApplyIntelThreadDirectorPolicy(out string reason)
        {
            var snap = HardwareDetectionService.Instance.GetSnapshot();
            bool onAc = NativePowerMethods.IsOnAcPower();

            if (!snap.Cpu.IsHybridCpu)
            {
                reason = "Processor is not a heterogeneous Intel hybrid architecture.";
                return false;
            }

            if (!onAc)
            {
                reason = "System is operating on battery power. Thread director overrides are suppressed to avoid thermal throttling.";
                return false;
            }

            reason = $"Intel Hybrid Architecture verified ({snap.Cpu.PerformanceCores} P-Cores, {snap.Cpu.EfficientCores} E-Cores). Eligible on AC power.";
            return true;
        }

        public bool ShouldApplyAmd3DVcachePolicy(out string reason)
        {
            var snap = HardwareDetectionService.Instance.GetSnapshot();
            bool onAc = NativePowerMethods.IsOnAcPower();

            if (!snap.Cpu.IsDualCcdAmd3DVcache)
            {
                if (snap.Cpu.L3CacheInstanceCount == 1 && snap.Cpu.Name.Contains("3D", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Single-CCD AMD X3D detected (unified 3D V-Cache across all cores). Core affinity locking bypassed.";
                    return false;
                }

                reason = "Processor is not a dual-CCD AMD 3D V-Cache architecture.";
                return false;
            }

            if (!onAc)
            {
                reason = "System is operating on battery power. Cache affinity locking is suppressed.";
                return false;
            }

            reason = $"Dual-CCD AMD 3D V-Cache verified ({snap.Cpu.L3CacheInstanceCount} L3 instances). Eligible for CPPC cache affinity on AC power.";
            return true;
        }

        /// <summary>
        /// Computes the CPU affinity bitmask corresponding to Performance Cores (P-Cores) on Intel Hybrid architectures.
        /// On Intel 13th Gen i5-13450HX (6 P-Cores with Hyper-Threading), returns logical processors 0-11 (mask 0x0FFF).
        /// </summary>
        public IntPtr GetPerformanceCoreAffinityMask()
        {
            var snap = HardwareDetectionService.Instance.GetSnapshot();
            if (snap.Cpu.IsHybridCpu && snap.Cpu.PerformanceCores > 0)
            {
                int pThreads = snap.Cpu.PerformanceCores * 2;
                long mask = (1L << Math.Min(62, pThreads)) - 1;
                return new IntPtr(mask);
            }
            int total = Math.Min(62, snap.Cpu.LogicalProcessors > 0 ? snap.Cpu.LogicalProcessors : Environment.ProcessorCount);
            return new IntPtr((1L << total) - 1);
        }

        /// <summary>
        /// Computes the CPU affinity bitmask corresponding to Efficient Cores (E-Cores) on Intel Hybrid architectures.
        /// On Intel 13th Gen i5-13450HX (4 E-Cores single-threaded), returns logical processors 12-15 (mask 0xF000).
        /// </summary>
        public IntPtr GetEfficiencyCoreAffinityMask()
        {
            var snap = HardwareDetectionService.Instance.GetSnapshot();
            if (snap.Cpu.IsHybridCpu && snap.Cpu.PerformanceCores > 0 && snap.Cpu.EfficientCores > 0)
            {
                int pThreads = snap.Cpu.PerformanceCores * 2;
                int eThreads = snap.Cpu.EfficientCores;
                long eMask = ((1L << Math.Min(62, eThreads)) - 1) << pThreads;
                return new IntPtr(eMask);
            }
            int total = Math.Min(62, snap.Cpu.LogicalProcessors > 0 ? snap.Cpu.LogicalProcessors : Environment.ProcessorCount);
            return new IntPtr((1L << total) - 1);
        }

        /// <summary>
        /// Safely configures process processor affinity with non-crashing permission fallback.
        /// </summary>
        public bool TryApplyAffinity(System.Diagnostics.Process process, IntPtr affinityMask, out string reason)
        {
            try
            {
                if (process.HasExited)
                {
                    reason = "Process has already exited.";
                    return false;
                }
                process.ProcessorAffinity = affinityMask;
                reason = $"Applied CPU affinity mask 0x{affinityMask.ToInt64():X}.";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Failed to apply affinity: {ex.Message}";
                return false;
            }
        }
    }
}
