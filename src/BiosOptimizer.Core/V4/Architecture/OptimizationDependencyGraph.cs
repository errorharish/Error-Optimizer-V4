#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Tracks and enforces prerequisite dependencies between optimization candidates.
    /// Prevents dependent optimizations from running if their prerequisites are missing, ineligible, or failed.
    /// </summary>
    public sealed class OptimizationDependencyGraph
    {
        private static readonly Lazy<OptimizationDependencyGraph> _instance = new(() => new OptimizationDependencyGraph());
        public static OptimizationDependencyGraph Instance => _instance.Value;

        private readonly Dictionary<string, List<string>> _dependencies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TUNE_POWER_CORE_PARKING_UNPARK"] = new() { "TUNE_POWER_PLAN_HIGH_PERFORMANCE" },
            ["TUNE_CORE_STORAGE_TRIM_REALTIME_PASS_THROUGH"] = new() { "TUNE_STORAGE_TRIM_ENABLE" },
            ["TUNE_CORE_AUDIO_LOW_LATENCY_BUFFER_SHIELD"] = new() { "TUNE_MMCSS_RESPONSIVENESS" },
            ["TUNE_CORE_AUDIO_GLITCH_RESILIENCE_LOCK"] = new() { "TUNE_MMCSS_RESPONSIVENESS" },
            ["TUNE_CORE_AMD_X3D_CACHE_SCHEDULER_LOCK"] = new() { "TUNE_CPU_AMD_3D_VCACHE" },
            ["TUNE_CORE_CPU_HETERO_SCHEDULING_POLICY"] = new() { "TUNE_CPU_INTEL_THREAD_DIRECTOR" },
            ["TUNE_CORE_NETWORK_PACKET_PACING_CALIBRATE"] = new() { "TUNE_NET_LOW_LATENCY_ACK" },
            ["TUNE_GPU_DXGI_FLIP_MODEL_VRR"] = new() { "TUNE_GPU_DWM_PRESENTATION" },
            ["TUNE_CORE_DWM_FRAME_LATENCY_WAITABLE_OBJECT"] = new() { "TUNE_CORE_DWM_DOUBLE_BUFFER_LOW_LATENCY" }
        };

        public IReadOnlyList<string> GetDependencies(string candidateId)
        {
            if (_dependencies.TryGetValue(candidateId, out var deps))
            {
                return deps;
            }
            return Array.Empty<string>();
        }

        public bool AreDependenciesSatisfied(string candidateId, Dictionary<string, OptimizationCandidateDto> candidateMap)
        {
            if (!_dependencies.TryGetValue(candidateId, out var deps) || deps.Count == 0)
            {
                return true;
            }

            foreach (var depId in deps)
            {
                if (candidateMap.TryGetValue(depId, out var depCandidate))
                {
                    if (depCandidate.EligibilityStatus == OptimizationEligibilityStatus.UNSUPPORTED_HARDWARE ||
                        depCandidate.EligibilityStatus == OptimizationEligibilityStatus.UNSUPPORTED_OS ||
                        depCandidate.EligibilityStatus == OptimizationEligibilityStatus.FAILED_VALIDATION)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
