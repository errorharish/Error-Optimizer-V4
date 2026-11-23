#nullable enable
using System;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Authoritative Network Stack Intelligence Engine for ERROR OPTIMIZER V4.
    /// Manages network autotuning and gates low-latency packet pacing strictly to gaming profiles.
    /// </summary>
    public sealed class NetworkCapabilityEngine
    {
        private static readonly Lazy<NetworkCapabilityEngine> _instance = new(() => new NetworkCapabilityEngine());
        public static NetworkCapabilityEngine Instance => _instance.Value;

        public bool IsLowLatencyAckEligible(string workloadProfile, out string reason)
        {
            if (workloadProfile.Equals("Gaming", StringComparison.OrdinalIgnoreCase) ||
                workloadProfile.Equals("Emulator", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Eligible: Gaming/Emulator profile active. TCP ACK frequency and packet pacing tuned for minimum interactive latency.";
                return true;
            }

            reason = "Ineligible: Low-latency ACK frequency is disabled for desktop and creative workloads to prevent TCP throughput reduction.";
            return false;
        }
    }
}
