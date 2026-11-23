#nullable enable
using System;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Authoritative GPU Capability and Graphics Pipeline Engine for ERROR OPTIMIZER V4.
    /// Manages HAGS eligibility, VRAM pressure, and graphics presentation timing.
    /// </summary>
    public sealed class GPUCapabilityAndPressureEngine
    {
        private static readonly Lazy<GPUCapabilityAndPressureEngine> _instance = new(() => new GPUCapabilityAndPressureEngine());
        public static GPUCapabilityAndPressureEngine Instance => _instance.Value;

        public bool IsHagsEligible(out string reason)
        {
            var snap = HardwareDetectionService.Instance.GetSnapshot();

            if (!snap.PrimaryGpu.SupportsHags)
            {
                reason = "Primary GPU driver or WDDM version does not support Hardware-Accelerated GPU Scheduling (WDDM 2.7+ required).";
                return false;
            }

            if (snap.PrimaryGpu.VramGb < 6.0)
            {
                reason = $"Primary GPU dedicated VRAM ({snap.PrimaryGpu.VramGb:F1} GB) is below the 6.0 GB safety threshold. HAGS disabled to prevent VRAM paging stalls.";
                return false;
            }

            reason = $"GPU verified eligible for HAGS ({snap.PrimaryGpu.Name}, {snap.PrimaryGpu.VramGb:F1} GB VRAM, WDDM {snap.PrimaryGpu.WddmVersion:F1}).";
            return true;
        }

        public bool IsDiscreteGpuPreferredOnHybrid(out string reason)
        {
            var snap = HardwareDetectionService.Instance.GetSnapshot();
            if (snap.Gpus.Count > 1 && snap.PrimaryGpu.IsDiscrete)
            {
                reason = "Multi-GPU hybrid setup detected. High-performance DirectX preference recommended for discrete GPU.";
                return true;
            }

            reason = "Single GPU or integrated-only graphics configuration.";
            return false;
        }
    }
}
