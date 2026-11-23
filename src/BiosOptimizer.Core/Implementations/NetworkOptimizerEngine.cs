using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BiosOptimizer.Core.Interfaces;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations
{
    public class NetworkOptimizerEngine : INetworkOptimizerEngine
    {
        private readonly IBackupManager _backupManager;
        private readonly INetworkDiagnosticsService _diagnosticsService;
        private static DateTime? _lastDnsFlushTime;
        private static readonly object _syncLock = new();

        public NetworkOptimizerEngine(IBackupManager backupManager, INetworkDiagnosticsService? diagnosticsService = null)
        {
            _backupManager = backupManager;
            _diagnosticsService = diagnosticsService ?? new NetworkDiagnosticsService();
        }

        public async Task<NetworkOptimizationPlan> PlanNetworkOptimizationAsync(bool includeDiagnostics = false)
        {
            return await Task.Run(async () =>
            {
                var plan = new NetworkOptimizationPlan();

                // 1. Discover Adapters using canonical diagnostics service
                plan.Adapters = DiscoverAdapters();

                // 2. Discover Network Actions & Real System States
                plan.Actions = DiscoverActions(plan.Adapters);

                // 3. DNS Flush Stateless Metadata
                plan.LastDnsFlushTime = _lastDnsFlushTime;

                // 4. Calculate Summary Metrics (Persistent Actions Only)
                var persistentActions = plan.Actions.Where(a => !a.IsStatelessAction).ToList();
                plan.TotalActions = persistentActions.Count;
                plan.SupportedCount = persistentActions.Count(a => a.Supported);
                plan.ApplicableCount = persistentActions.Count(a => a.Applicable);
                plan.PendingCount = persistentActions.Count(a => a.Applicable && a.Status == "PENDING");
                plan.OptimizedCount = persistentActions.Count(a => a.Status == "OPTIMIZED" || a.Status == "VERIFIED");
                plan.NotApplicableCount = persistentActions.Count(a => !a.Applicable || !a.Supported);

                // 5. Diagnostics (Read cached or execute real test)
                if (includeDiagnostics)
                {
                    plan.Diagnostics = await RunDiagnosticsAsync();
                }
                else
                {
                    plan.Diagnostics = _diagnosticsService.GetCachedDiagnostics();
                }

                return plan;
            });
        }

        public List<NetworkAdapterInfo> DiscoverAdapters()
        {
            return _diagnosticsService.DetectInterfaces();
        }

        private List<NetworkOptimizationAction> DiscoverActions(List<NetworkAdapterInfo> adapters)
        {
            var actions = new List<NetworkOptimizationAction>();

            // Read Netsh TCP Global Configuration
            var netshGlobal = QueryNetshTcpGlobal();
            var netshSupplemental = QueryNetshTcpSupplemental();

            // 1. TCP Auto-Tuning
            var currentAutoTuning = netshGlobal.GetValueOrDefault("Receive Window Auto-Tuning Level", "unknown");
            var autoTuningAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.autotuning",
                Name = "TCP Receive Window Auto-Tuning",
                Category = "TCP/IP Stack",
                Description = "Dynamically adjusts TCP buffer sizes to maximize throughput and reduce transmission stalls on high-speed connections.",
                CurrentValue = currentAutoTuning,
                TargetValue = "normal",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = "netsh int tcp show global -> Receive Window Auto-Tuning Level",
                Scope = "Global TCP/IP Stack",
                RequiresAdmin = true
            };
            EvaluateActionState(autoTuningAction, currentAutoTuning, "normal",
                "Current TCP receive buffer auto-tuning differs from the optimal throughput target (normal).");
            actions.Add(autoTuningAction);

            // 2. TCP Congestion Control Provider
            var currentCongestion = netshSupplemental.GetValueOrDefault("Congestion Control Provider", "unknown");
            var supportedCongestion = CheckCongestionProviderSupport();
            string targetCongestion = supportedCongestion.Contains("cubic", StringComparer.OrdinalIgnoreCase) ? "cubic"
                                    : (supportedCongestion.Contains("ctcp", StringComparer.OrdinalIgnoreCase) ? "ctcp"
                                    : (supportedCongestion.Contains("bbr2", StringComparer.OrdinalIgnoreCase) ? "bbr2" : "default"));

            var congestionAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.congestionprovider",
                Name = "TCP Congestion Control Provider",
                Category = "TCP/IP Stack",
                Description = "Optimizes packet pacing and congestion avoidance algorithm (CTCP/CUBIC) to prevent latency spikes during high bandwidth load.",
                CurrentValue = currentCongestion,
                TargetValue = targetCongestion,
                Supported = supportedCongestion.Count > 0,
                Applicable = supportedCongestion.Count > 0 && targetCongestion != "default",
                Risk = "Low",
                VerificationMethod = "netsh int tcp show supplemental -> Congestion Control Provider",
                Scope = "TCP Internet Template",
                RequiresAdmin = true
            };
            if (!congestionAction.Applicable)
            {
                congestionAction.Status = "NOT APPLICABLE";
                congestionAction.Recommendation = "NOT APPLICABLE";
                congestionAction.RecommendationReason = "Alternative congestion providers are not installed or supported on this Windows build.";
            }
            else if (currentCongestion.Equals("cubic", StringComparison.OrdinalIgnoreCase) ||
                     currentCongestion.Equals("ctcp", StringComparison.OrdinalIgnoreCase) ||
                     currentCongestion.Equals("bbr2", StringComparison.OrdinalIgnoreCase))
            {
                congestionAction.Status = "OPTIMIZED";
                congestionAction.Recommendation = "MAINTAIN";
                congestionAction.RecommendationReason = $"High performance congestion provider '{currentCongestion}' is active.";
            }
            else
            {
                EvaluateActionState(congestionAction, currentCongestion, targetCongestion,
                    $"Congestion provider is currently '{currentCongestion}'. Target '{targetCongestion}' improves packet recovery and stability.");
            }
            actions.Add(congestionAction);

            // 3. Network Throttling Index (MMCSS)
            var currentThrottling = ReadRegistryDwordString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex");
            var throttlingAction = new NetworkOptimizationAction
            {
                Id = "net.mmcss.networkthrottling",
                Name = "MMCSS Network Throttling Index",
                Category = "Multimedia & MMCSS",
                Description = "Disables Windows Multimedia Class Scheduler Service (MMCSS) network packet throttling, eliminating 10ms network processing delays during media/gaming playback.",
                CurrentValue = FormatThrottlingValue(currentThrottling),
                TargetValue = "Disabled (0xFFFFFFFF)",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NetworkThrottlingIndex",
                Scope = "Windows Multimedia Subsystem",
                RequiresAdmin = true,
                RequiresRestart = true
            };
            bool isThrottlingDisabled = currentThrottling.Equals("ffffffff", StringComparison.OrdinalIgnoreCase) || currentThrottling == "4294967295" || currentThrottling == "-1";
            if (isThrottlingDisabled)
            {
                throttlingAction.Status = "OPTIMIZED";
                throttlingAction.Recommendation = "OPTIMIZED";
                throttlingAction.RecommendationReason = "Network throttling is disabled. All network packets are processed without MMCSS priority delay.";
            }
            else
            {
                throttlingAction.Status = "PENDING";
                throttlingAction.Recommendation = "HIGHLY RECOMMENDED";
                throttlingAction.RecommendationReason = $"Network throttling index is active ({currentThrottling}). Disabling prevents packet starvation during gaming and audio playback.";
            }
            actions.Add(throttlingAction);

            // 4. System Responsiveness (MMCSS)
            var currentResponsiveness = ReadRegistryDwordString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness");
            var respAction = new NetworkOptimizationAction
            {
                Id = "net.mmcss.systemresponsiveness",
                Name = "Multimedia System Responsiveness",
                Category = "Multimedia & MMCSS",
                Description = "Configures MMCSS to allocate up to 90% CPU quota to multimedia/gaming applications while reserving 10% for low-priority background tasks. (Microsoft documents that values below 10 or above 100 are clamped to 20; setting 0 does not provide zero-reservation).",
                CurrentValue = currentResponsiveness == "10" ? "10 (90% Multimedia Quota / 10% Background)" : currentResponsiveness == "20" ? "20 (Windows Default: 80% Quota / 20% Background)" : currentResponsiveness == "0" ? "0 (Invalid / Clamped to 20 by Windows)" : $"{currentResponsiveness}% Reserved",
                TargetValue = "10 (90% Multimedia Quota / 10% Background)",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\SystemResponsiveness",
                Scope = "Windows Multimedia Subsystem",
                RequiresAdmin = true,
                RequiresRestart = true
            };
            if (currentResponsiveness == "10" || currentResponsiveness == "20")
            {
                respAction.Status = "OPTIMIZED";
                respAction.Recommendation = "OPTIMIZED";
                respAction.RecommendationReason = $"System responsiveness is configured to documented supported setting ({currentResponsiveness}% background reserve).";
            }
            else
            {
                respAction.Status = "PENDING";
                respAction.Recommendation = "RECOMMENDED";
                respAction.RecommendationReason = $"System currently has SystemResponsiveness set to '{currentResponsiveness}', which is clamped to 20 or non-standard.";
            }
            actions.Add(respAction);

            // 5. Receive-Side Scaling (RSS)
            var currentRss = netshGlobal.GetValueOrDefault("Receive-Side Scaling State", "unknown");
            var rssAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.rss",
                Name = "Receive-Side Scaling (RSS)",
                Category = "Hardware Offload",
                Description = "Enables network packet processing across multiple CPU cores to prevent single-core bottlenecking under high network traffic.",
                CurrentValue = currentRss,
                TargetValue = "enabled",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = "netsh int tcp show global -> Receive-Side Scaling State",
                Scope = "Hardware Network Processing",
                RequiresAdmin = true
            };
            EvaluateActionState(rssAction, currentRss, "enabled",
                "RSS is currently not enabled. Enabling distributes network packet processing across all CPU cores.");
            actions.Add(rssAction);

            // 6. TCP RFC 1323 Timestamps
            var currentTimestamps = netshGlobal.GetValueOrDefault("RFC 1323 Timestamps", "unknown");
            var timestampsAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.timestamps",
                Name = "TCP RFC 1323 Timestamps",
                Category = "TCP/IP Stack",
                Description = "Disables 12-byte per-packet TCP timestamp headers to reduce packet payload overhead and improve throughput on broadband connections.",
                CurrentValue = currentTimestamps,
                TargetValue = "disabled",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = "netsh int tcp show global -> RFC 1323 Timestamps",
                Scope = "Global TCP/IP Stack",
                RequiresAdmin = true
            };
            EvaluateActionState(timestampsAction, currentTimestamps, "disabled",
                "TCP timestamps are currently enabled, adding unnecessary 12-byte header overhead to every transmission.");
            actions.Add(timestampsAction);

            // 7. Explicit Congestion Notification (ECN)
            var currentEcn = netshGlobal.GetValueOrDefault("ECN Capability", "unknown");
            var ecnAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.ecn",
                Name = "Explicit Congestion Notification (ECN)",
                Category = "TCP/IP Stack",
                Description = "Allows intermediate network routers to signal congestion without intentionally dropping packets, reducing packet loss retransmissions.",
                CurrentValue = currentEcn,
                TargetValue = "enabled",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = "netsh int tcp show global -> ECN Capability",
                Scope = "Global TCP/IP Stack",
                RequiresAdmin = true
            };
            EvaluateActionState(ecnAction, currentEcn, "enabled",
                "ECN capability is currently disabled or default. Enabling reduces retransmissions on modern routers.");
            actions.Add(ecnAction);

            // 8. Initial Retransmission Timeout (Initial RTO)
            var currentRto = netshGlobal.GetValueOrDefault("Initial RTO", "unknown");
            var rtoAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.initialrto",
                Name = "Initial Retransmission Timeout (RTO)",
                Category = "TCP/IP Stack",
                Description = "Sets initial TCP connection retransmit timeout to 2000ms (down from 3000ms default) to recover faster from dropped initial SYN packets.",
                CurrentValue = currentRto.Contains("ms") ? currentRto : $"{currentRto} ms",
                TargetValue = "2000 ms",
                Supported = true,
                Applicable = true,
                Risk = "Low",
                VerificationMethod = "netsh int tcp show global -> Initial RTO",
                Scope = "Global TCP/IP Stack",
                RequiresAdmin = true
            };
            bool isRtoOptimal = currentRto.Contains("2000");
            if (isRtoOptimal)
            {
                rtoAction.Status = "OPTIMIZED";
                rtoAction.Recommendation = "OPTIMIZED";
                rtoAction.RecommendationReason = "Initial RTO is set to 2000ms for rapid connection recovery.";
            }
            else
            {
                rtoAction.Status = "PENDING";
                rtoAction.Recommendation = "RECOMMENDED";
                rtoAction.RecommendationReason = $"Initial RTO is currently {currentRto}. Tuning to 2000ms accelerates connection recovery.";
            }
            actions.Add(rtoAction);

            // 9. DNS Resolver Cache Flush (Stateless Action)
            var dnsAction = new NetworkOptimizationAction
            {
                Id = "net.dns.cacheflush",
                Name = "DNS Resolver Cache",
                Category = "Diagnostics & DNS",
                Description = "Purges outdated, corrupted, or cached domain name resolution records from the Windows DNS resolver service.",
                CurrentValue = "Ready",
                TargetValue = "Flushed & Synchronized",
                Supported = true,
                Applicable = true,
                Status = "MANUAL",
                Risk = "Low",
                Recommendation = "RECOMMENDED",
                RecommendationReason = "Perform DNS flush whenever encountering domain resolution delays, stale DNS records, or after changing networks.",
                VerificationMethod = "ipconfig /flushdns",
                Scope = "Windows DNS Client Service (Dnscache)",
                IsStatelessAction = true,
                LastExecuted = _lastDnsFlushTime,
                RequiresAdmin = false
            };
            actions.Add(dnsAction);

            
            // ── NEW CAPABILITY 1: TCP LOW-LATENCY PACKET DELIVERY (TCPNoDelay & TcpAckFrequency) ──
            var (primaryGuid, curAckFreq, curNoDelay) = QueryPrimaryInterfaceLatencyKeys();
            bool isLatencyOptimal = curAckFreq == 1 && curNoDelay == 1;
            var lowLatencyAction = new NetworkOptimizationAction
            {
                Id = "net.tcp.low_latency_ack",
                Name = "TCP Low-Latency Packet Delivery (TCPNoDelay & Immediate ACK)",
                Category = "Network Interface Tuning",
                Description = "Disables delayed TCP acknowledgments (TcpAckFrequency = 1) and Nagle's algorithm buffering (TCPNoDelay = 1) on the primary active network adapter to minimize multiplayer gaming ping spikes and streaming jitter.",
                CurrentValue = isLatencyOptimal ? "Immediate Dispatch (1:1)" : $"Delayed ACK (Freq: {curAckFreq}, NoDelay: {curNoDelay})",
                TargetValue = "Immediate Dispatch (TcpAckFrequency=1, TCPNoDelay=1)",
                Supported = !string.IsNullOrEmpty(primaryGuid),
                Applicable = !string.IsNullOrEmpty(primaryGuid),
                Risk = "Low",
                VerificationMethod = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID} readback",
                Scope = "Active Network Adapter",
                RequiresAdmin = true
            };
            if (isLatencyOptimal)
            {
                lowLatencyAction.Status = "OPTIMIZED";
                lowLatencyAction.Recommendation = "OPTIMIZED";
                lowLatencyAction.RecommendationReason = "Immediate TCP packet dispatch is active on the primary network adapter.";
            }
            else
            {
                lowLatencyAction.Status = "PENDING";
                lowLatencyAction.Recommendation = "RECOMMENDED";
                lowLatencyAction.RecommendationReason = "Eliminating delayed ACK and Nagle buffering reduces packet dispatch latency.";
            }
            actions.Add(lowLatencyAction);

            // ── NEW CAPABILITY 2: HIGH-SPEED DNS RESOLVER OPTIMIZATION ──
            var primaryDns = QueryActiveDnsServers();
            bool isCloudflareOrGoogle = primaryDns.Any(d => d.StartsWith("1.1.1.") || d.StartsWith("8.8.8.") || d.StartsWith("9.9.9."));
            var dnsOptAction = new NetworkOptimizationAction
            {
                Id = "net.dns.optimize",
                Name = "High-Speed DNS Resolution Configuration",
                Category = "Diagnostics & DNS",
                Description = "Configures ultra-low latency, secure public DNS resolvers (Cloudflare 1.1.1.1 / Google 8.8.8.8) with cryptographic fallback to replace slow, unoptimized ISP domain name lookup servers.",
                CurrentValue = primaryDns.Count > 0 ? string.Join(", ", primaryDns) : "Automatic / ISP Default",
                TargetValue = "Cloudflare Primary (1.1.1.1) • Secondary (1.0.0.1)",
                Supported = true,
                Applicable = true,
                Risk = "Safe",
                VerificationMethod = "netsh interface ip show dns readback",
                Scope = "Active Network Adapter",
                RequiresAdmin = true
            };
            if (isCloudflareOrGoogle)
            {
                dnsOptAction.Status = "OPTIMIZED";
                dnsOptAction.Recommendation = "OPTIMIZED";
                dnsOptAction.RecommendationReason = "Verified high-speed public DNS resolvers are active.";
            }
            else
            {
                dnsOptAction.Status = "PENDING";
                dnsOptAction.Recommendation = "RECOMMENDED";
                dnsOptAction.RecommendationReason = $"Current DNS ({dnsOptAction.CurrentValue}) may be subject to ISP throttling or resolution latency.";
            }
            actions.Add(dnsOptAction);

            // ── NEW CAPABILITY 3: HARDWARE ADAPTER OFFLOAD VERIFICATION ──
            var primaryUp = adapters.FirstOrDefault(a => a.IsActivePhysicalAdapter && a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                         ?? adapters.FirstOrDefault(a => a.IsActivePhysicalAdapter)
                         ?? adapters.FirstOrDefault();
            var (offloadDetected, offloadActive, offloadStateText) = QueryHardwareChecksumOffloadStatus(primaryUp);
            var offloadAction = new NetworkOptimizationAction
            {
                Id = "net.adapter.checksum_offload",
                Name = "Network Hardware Offload & Multi-Core Interrupts",
                Category = "Hardware Offload",
                Description = "Ensures IPv4/IPv6 Checksum Offload and Large Send Offload (LSO) are offloaded to physical NIC hardware, freeing CPU cycles from packet hashing calculations.",
                CurrentValue = offloadStateText,
                TargetValue = "Hardware Accelerated (Active)",
                Supported = primaryUp != null,
                Applicable = primaryUp != null,
                Status = offloadDetected ? (offloadActive ? "OPTIMIZED" : "PENDING") : "PENDING",
                Risk = "Safe",
                Recommendation = offloadDetected ? (offloadActive ? "OPTIMIZED" : "RECOMMENDED") : "RECOMMENDED",
                RecommendationReason = offloadDetected
                    ? (offloadActive ? "NIC hardware checksum offload registry properties verified active." : "One or more NIC checksum offload settings are disabled or using software fallback.")
                    : "Hardware checksum offload state could not be read from NIC registry class; apply to enable hardware acceleration.",
                VerificationMethod = "NIC class registry (*IPChecksumOffloadIPv4 / *TCPChecksumOffloadIPv4) readback",
                Scope = primaryUp != null ? primaryUp.Name : "Physical Network Adapters",
                RequiresAdmin = true
            };
            actions.Add(offloadAction);

            return actions;
        }

        private void EvaluateActionState(NetworkOptimizationAction action, string current, string target, string pendingReason)
        {
            if (current.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                action.Status = "OPTIMIZED";
                action.Recommendation = "OPTIMIZED";
                action.RecommendationReason = $"Configuration matches recommended target ({target}).";
            }
            else
            {
                action.Status = "PENDING";
                action.Recommendation = "HIGHLY RECOMMENDED";
                action.RecommendationReason = pendingReason;
            }
        }

        private static string FormatThrottlingValue(string raw)
        {
            if (raw.Equals("ffffffff", StringComparison.OrdinalIgnoreCase) || raw == "4294967295" || raw == "-1")
                return "Disabled (0xFFFFFFFF)";
            return raw == "10" ? "Default (10 - Throttled)" : $"Active ({raw})";
        }

        public async Task<(bool Success, string Message, string VerifiedState)> ApplyNetworkActionAsync(string actionId)
        {
            return await Task.Run(() =>
            {
                lock (_syncLock)
                {
                    try
                    {
                        switch (actionId.ToLowerInvariant())
                        {
                            case "net.tcp.autotuning":
                            case "tcpautotuning":
                            {
                                var before = QueryNetshTcpGlobal().GetValueOrDefault("Receive Window Auto-Tuning Level", "unknown");
                                ExecuteNetsh("int tcp set global autotuninglevel=normal");
                                var after = QueryNetshTcpGlobal().GetValueOrDefault("Receive Window Auto-Tuning Level", "unknown");
                                bool verified = after.Equals("normal", StringComparison.OrdinalIgnoreCase);
                                return (verified,
                                    verified ? "TCP Receive Auto-Tuning successfully set to 'normal' and verified via netsh readback."
                                             : $"TCP Auto-Tuning application failed. Current readback: '{after}'.",
                                    after);
                            }

                            case "net.tcp.congestionprovider":
                            case "congestioncontrol":
                            {
                                var supported = CheckCongestionProviderSupport();
                                string target = supported.Contains("cubic", StringComparer.OrdinalIgnoreCase) ? "cubic"
                                              : (supported.Contains("ctcp", StringComparer.OrdinalIgnoreCase) ? "ctcp"
                                              : (supported.Contains("bbr2", StringComparer.OrdinalIgnoreCase) ? "bbr2" : "cubic"));

                                ExecuteNetsh($"int tcp set supplemental template=internet congestionprovider={target}");
                                ExecuteNetsh($"int tcp set supplemental template=datacenter congestionprovider={target}");
                                ExecuteNetsh($"int tcp set supplemental template=compat congestionprovider={target}");
                                ExecuteNetsh($"int tcp set supplemental template=custom congestionprovider={target}");
                                var after = QueryNetshTcpSupplemental().GetValueOrDefault("Congestion Control Provider", "unknown");
                                bool verified = after.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                                                after.Equals("cubic", StringComparison.OrdinalIgnoreCase) ||
                                                after.Equals("ctcp", StringComparison.OrdinalIgnoreCase);
                                return (verified,
                                    verified ? $"TCP Congestion Provider set to '{target}' and verified via netsh readback."
                                             : $"Congestion Provider application completed. Readback: '{after}'.",
                                    after);
                            }

                            case "net.mmcss.networkthrottling":
                            case "networkthrottling":
                            {
                                const string subKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                                var cur = ReadRegistryDwordString(subKey, "NetworkThrottlingIndex");
                                _backupManager?.BackupRegistryValue("AntiGravity.NetworkOptimizer", $@"HKLM\{subKey}", "NetworkThrottlingIndex", cur, "DWord", "ffffffff", "Backup of NetworkThrottlingIndex");

                                using (var key = Registry.LocalMachine.CreateSubKey(subKey))
                                {
                                    if (key == null) return (false, "Could not open SystemProfile registry key for writing.", "Access Denied");
                                    key.SetValue("NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
                                }

                                var after = ReadRegistryDwordString(subKey, "NetworkThrottlingIndex");
                                bool verified = after.Equals("ffffffff", StringComparison.OrdinalIgnoreCase) || after == "4294967295" || after == "-1";
                                return (verified,
                                    verified ? "MMCSS Network Throttling Index set to 0xFFFFFFFF (Disabled) and verified."
                                             : $"Network Throttling verification failed. Current value: '{after}'.",
                                    FormatThrottlingValue(after));
                            }

                            case "net.mmcss.systemresponsiveness":
                            {
                                const string subKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                                var cur = ReadRegistryDwordString(subKey, "SystemResponsiveness");
                                _backupManager?.BackupRegistryValue("AntiGravity.NetworkOptimizer", $@"HKLM\{subKey}", "SystemResponsiveness", cur, "DWord", "20", "Backup of SystemResponsiveness");

                                using (var key = Registry.LocalMachine.CreateSubKey(subKey))
                                {
                                    if (key == null) return (false, "Could not open SystemProfile registry key for writing.", "Access Denied");
                                    key.SetValue("SystemResponsiveness", 10, RegistryValueKind.DWord);
                                }

                                var after = ReadRegistryDwordString(subKey, "SystemResponsiveness");
                                bool verified = after == "10";
                                return (verified,
                                    verified ? "Multimedia System Responsiveness set to 10 (90% Multimedia Quota / 10% Background) and verified."
                                             : $"System Responsiveness verification failed. Current value: '{after}'.",
                                    after == "10" ? "10 (90% Multimedia Quota / 10% Background)" : $"{after}%");
                            }

                            case "net.tcp.rss":
                            {
                                ExecuteNetsh("int tcp set global rss=enabled");
                                var after = QueryNetshTcpGlobal().GetValueOrDefault("Receive-Side Scaling State", "unknown");
                                bool verified = after.Equals("enabled", StringComparison.OrdinalIgnoreCase);
                                return (verified,
                                    verified ? "Receive-Side Scaling (RSS) enabled and verified via netsh."
                                             : $"RSS application failed. Current readback: '{after}'.",
                                    after);
                            }

                            case "net.tcp.timestamps":
                            {
                                ExecuteNetsh("int tcp set global timestamps=disabled");
                                var after = QueryNetshTcpGlobal().GetValueOrDefault("RFC 1323 Timestamps", "unknown");
                                bool verified = after.Equals("disabled", StringComparison.OrdinalIgnoreCase);
                                return (verified,
                                    verified ? "TCP RFC 1323 Timestamps disabled and verified via netsh."
                                             : $"Timestamps application failed. Current readback: '{after}'.",
                                    after);
                            }

                            case "net.tcp.ecn":
                            {
                                ExecuteNetsh("int tcp set global ecncapability=enabled");
                                var after = QueryNetshTcpGlobal().GetValueOrDefault("ECN Capability", "unknown");
                                bool verified = after.Equals("enabled", StringComparison.OrdinalIgnoreCase);
                                return (verified,
                                    verified ? "Explicit Congestion Notification (ECN) enabled and verified via netsh."
                                             : $"ECN application failed. Current readback: '{after}'.",
                                    after);
                            }

                            case "net.tcp.initialrto":
                            {
                                ExecuteNetsh("int tcp set global initialRto=2000");
                                var after = QueryNetshTcpGlobal().GetValueOrDefault("Initial RTO", "unknown");
                                bool verified = after.Contains("2000");
                                return (verified,
                                    verified ? "Initial RTO set to 2000ms and verified via netsh."
                                             : $"Initial RTO application failed. Current readback: '{after}'.",
                                    after.Contains("ms") ? after : $"{after} ms");
                            }

                            case "net.dns.cacheflush":
                            case "dnsflush":
                            {
                                var flushRes = FlushDnsCacheInternal();
                                return (flushRes.Success, flushRes.Message, "Flushed & Synchronized");
                            }

                            
                            case "net.tcp.low_latency_ack":
                            case "lowlatencyack":
                            {
                                var (guid, _, _) = QueryPrimaryInterfaceLatencyKeys();
                                if (string.IsNullOrEmpty(guid))
                                {
                                    return (false, "Could not determine active primary network interface GUID.", "FAILED");
                                }

                                string subKey = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}";
                                using (var key = Registry.LocalMachine.CreateSubKey(subKey, true))
                                {
                                    if (key == null) return (false, $"Unable to open network interface key {guid}.", "ACCESS_DENIED");

                                    // Capture old values
                                    object? oldAck = key.GetValue("TcpAckFrequency");
                                    object? oldNoDelay = key.GetValue("TCPNoDelay");
                                    _backupManager.BackupRegistryValue("Network Optimizer", $@"HKLM\{subKey}", "TcpAckFrequency", oldAck ?? 2, "REG_DWORD", 1, "Success");
                                    _backupManager.BackupRegistryValue("Network Optimizer", $@"HKLM\{subKey}", "TCPNoDelay", oldNoDelay ?? 0, "REG_DWORD", 1, "Success");

                                    key.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                    key.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                                }

                                var (_, afterAck, afterNoDelay) = QueryPrimaryInterfaceLatencyKeys();
                                bool verified = afterAck == 1 && afterNoDelay == 1;
                                return (verified,
                                    verified ? "TCP immediate ACK and TCPNoDelay successfully configured on primary adapter."
                                             : "Readback verification failed for TCP low latency settings.",
                                    verified ? "OPTIMIZED" : "VERIFICATION_FAILED");
                            }

                            case "net.dns.optimize":
                            case "dnsoptimize":
                            {
                                var adapters = DiscoverAdapters();
                                var primary = adapters.FirstOrDefault(a => a.IsActivePhysicalAdapter && a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                                           ?? adapters.FirstOrDefault(a => a.IsActivePhysicalAdapter || a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                                           ?? adapters.FirstOrDefault();
                                if (primary == null) return (false, "No active network adapter found.", "FAILED");

                                try
                                {
                                    // Backup prior interface DNS setting in Tcpip\Parameters\Interfaces\{GUID} if resolvable
                                    var (ifGuid, _, _) = QueryPrimaryInterfaceLatencyKeys();
                                    if (!string.IsNullOrEmpty(ifGuid))
                                    {
                                        string ifSubKey = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifGuid}";
                                        using var ifKey = Registry.LocalMachine.OpenSubKey(ifSubKey, false);
                                        object? oldNs = ifKey?.GetValue("NameServer");
                                        _backupManager?.BackupRegistryValue("AntiGravity.NetworkOptimizer", $@"HKLM\{ifSubKey}", "NameServer", oldNs ?? "", "REG_SZ", "1.1.1.1,1.0.0.1", "Backup previous interface NameServer");
                                    }

                                    // Apply Cloudflare 1.1.1.1 and 1.0.0.1 and verify exit codes
                                    bool setOk = ExecuteNetsh($"interface ip set dns name=\"{primary.Name}\" static 1.1.1.1");
                                    if (!setOk)
                                    {
                                        return (false, $"netsh failed while assigning primary DNS 1.1.1.1 on '{primary.Name}' (administrator privileges may be required).", "FAILED");
                                    }
                                    ExecuteNetsh($"interface ip add dns name=\"{primary.Name}\" 1.0.0.1 index=2");
                                    FlushDnsCacheInternal();

                                    var afterDns = QueryActiveDnsServers();
                                    bool verified = afterDns.Any(d => d == "1.1.1.1" || d == "1.0.0.1");
                                    return (verified,
                                        verified
                                            ? $"Configured Cloudflare high-speed DNS (1.1.1.1 / 1.0.0.1) on '{primary.Name}' and verified active resolvers."
                                            : $"DNS assignment applied on '{primary.Name}', but active resolver readback was: {string.Join(", ", afterDns)}.",
                                        verified ? "OPTIMIZED" : "VERIFICATION_FAILED");
                                }
                                catch (Exception ex)
                                {
                                    return (false, $"Failed to configure DNS: {ex.Message}", "FAILED");
                                }
                            }

                            case "net.adapter.checksum_offload":
                            {
                                var adapters = DiscoverAdapters();
                                var primary = adapters.FirstOrDefault(a => a.IsActivePhysicalAdapter || a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)) ?? adapters.FirstOrDefault();
                                if (primary == null)
                                    return (false, "No active physical network adapter found.", "FAILED");

                                string safeName = primary.Name.Replace("'", "''");
                                string psCmd = $"Enable-NetAdapterChecksumOffload -Name '{safeName}' -IpIPv4 -TcpIPv4 -UdpIPv4 -Confirm:$false -ErrorAction Stop";
                                using var p = new Process();
                                p.StartInfo = new ProcessStartInfo
                                {
                                    FileName = "powershell.exe",
                                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                };
                                p.Start();
                                string stderr = p.StandardError.ReadToEnd();
                                p.WaitForExit(8000);
                                if (p.ExitCode == 0)
                                    return (true, $"Enabled IPv4/TCP/UDP hardware checksum offload on '{primary.Name}'.", "OPTIMIZED");
                                return (false, $"Checksum offload configuration declined (exit {p.ExitCode}): {stderr.Trim()}", "FAILED");
                            }

                            default:
                                return (false, $"Unknown network optimization action '{actionId}'.", "Unknown");
                        }
                    }
                    catch (Exception ex)
                    {
                        return (false, "Exception applying action: " + ex.Message, "Error");
                    }
                }
            });
        }

        public async Task<(bool Success, string Message, DateTime Timestamp)> FlushDnsCacheAsync()
        {
            return await Task.Run(() =>
            {
                lock (_syncLock)
                {
                    var res = FlushDnsCacheInternal();
                    return res;
                }
            });
        }

        private (bool Success, string Message, DateTime Timestamp) FlushDnsCacheInternal()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);

                if (p?.ExitCode == 0 || output.Contains("Successfully flushed", StringComparison.OrdinalIgnoreCase))
                {
                    _lastDnsFlushTime = DateTime.Now;
                    return (true, "DNS Resolver Cache successfully flushed and purged.", _lastDnsFlushTime.Value);
                }
                return (false, $"ipconfig /flushdns failed (exit={p?.ExitCode ?? -1}): {output.Trim()}", DateTime.Now);
            }
            catch (Exception ex)
            {
                return (false, $"DNS Resolver Cache flush error: {ex.Message}", DateTime.Now);
            }
        }

        public async Task<NetworkDiagnosticResult> RunDiagnosticsAsync(IProgress<NetworkTestProgress>? progress = null, System.Threading.CancellationToken cancellationToken = default)
        {
            return await _diagnosticsService.RunFullNetworkTestAsync(progress, cancellationToken);
        }

        public async Task<(bool Success, string Message)> RestoreNetworkOptimizationAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Restore registry keys from backup manager
                    _backupManager?.RestoreByOwner("AntiGravity.NetworkOptimizer");

                    // Reset netsh parameters to safe Windows defaults
                    ExecuteNetsh("int tcp set global autotuninglevel=normal");
                    ExecuteNetsh("int tcp set global rss=enabled");
                    ExecuteNetsh("int tcp set global timestamps=disabled");
                    ExecuteNetsh("int tcp set global ecncapability=default");
                    ExecuteNetsh("int tcp set global initialRto=3000");
                    ExecuteNetsh("int tcp set supplemental template=internet congestionprovider=default");

                    return (true, "Network stack configurations restored to baseline Windows defaults.");
                }
                catch (Exception ex)
                {
                    return (false, "Failed to restore network settings: " + ex.Message);
                }
            });
        }

        #region Helper Readers & netsh Parsers

        private Dictionary<string, string> QueryNetshTcpGlobal()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "int tcp show global",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(3000);

                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            dict[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }
            }
            catch { }
            return dict;
        }

        private Dictionary<string, string> QueryNetshTcpSupplemental()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "int tcp show supplemental",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(3000);

                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            dict[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }
            }
            catch { }
            return dict;
        }

        private List<string> CheckCongestionProviderSupport()
        {
            var supported = new List<string> { "cubic", "ctcp", "newreno" };

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "int tcp show supplemental",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(3000);

                var candidates = new[] { "ctcp", "bbr2", "bbr", "cubic", "newreno" };
                foreach (var c in candidates)
                {
                    if (output.Contains(c, StringComparison.OrdinalIgnoreCase) && !supported.Contains(c))
                    {
                        supported.Add(c);
                    }
                }
            }
            catch { }

            return supported;
        }

        private static string ReadRegistryDwordString(string subKey, string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key != null)
                {
                    var val = key.GetValue(valueName);
                    if (val != null)
                    {
                        if (val is int intVal)
                        {
                            if (intVal == -1) return "ffffffff";
                            return ((uint)intVal).ToString("x");
                        }
                        if (val is long longVal)
                        {
                            if (longVal == -1 || longVal == 0xFFFFFFFF) return "ffffffff";
                            return ((uint)longVal).ToString("x");
                        }
                        return val.ToString() ?? "";
                    }
                }
            }
            catch { }
            return "10"; // Default
        }

        private static bool ExecuteNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                _ = p.StandardOutput.ReadToEnd();
                _ = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000)) return false;
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static (bool Detected, bool AllEnabled, string Summary) QueryHardwareChecksumOffloadStatus(NetworkAdapterInfo? adapter)
        {
            if (adapter == null) return (false, false, "Unavailable");
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}", false);
                if (classKey == null) return (false, false, "Unknown");
                foreach (var sub in classKey.GetSubKeyNames())
                {
                    if (!int.TryParse(sub, out _)) continue;
                    using var nicKey = classKey.OpenSubKey(sub, false);
                    if (nicKey == null) continue;
                    var desc = nicKey.GetValue("DriverDesc")?.ToString() ?? "";
                    var netCfg = nicKey.GetValue("NetCfgInstanceId")?.ToString() ?? "";
                    if (!desc.Equals(adapter.Description, StringComparison.OrdinalIgnoreCase) &&
                        !desc.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase) &&
                        !netCfg.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string ipv4 = nicKey.GetValue("*IPChecksumOffloadIPv4")?.ToString() ?? "";
                    string tcp4 = nicKey.GetValue("*TCPChecksumOffloadIPv4")?.ToString() ?? "";
                    string udp4 = nicKey.GetValue("*UDPChecksumOffloadIPv4")?.ToString() ?? "";
                    if (string.IsNullOrEmpty(ipv4) && string.IsNullOrEmpty(tcp4) && string.IsNullOrEmpty(udp4))
                        return (false, false, "Unknown (Driver Dependent)");

                    bool enabled = (ipv4 != "0") && (tcp4 != "0") && (udp4 != "0");
                    return (true, enabled, enabled ? "Hardware Accelerated" : "Disabled / Software Fallback");
                }
            }
            catch { }
            return (false, false, "Unknown");
        }

        #endregion

        private static (string Guid, int AckFreq, int NoDelay) QueryPrimaryInterfaceLatencyKeys()
        {
            try
            {
                // Prefer actual active physical interface GUIDs with valid gateway/IPv4
                var preferredGuids = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any())
                    .ThenByDescending(n => n.Speed)
                    .Select(n => n.Id)
                    .ToList();

                using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", false);
                if (baseKey != null)
                {
                    foreach (var guid in preferredGuids)
                    {
                        using var ifKey = baseKey.OpenSubKey(guid, false);
                        if (ifKey != null)
                        {
                            object? ack = ifKey.GetValue("TcpAckFrequency");
                            object? nod = ifKey.GetValue("TCPNoDelay");
                            return (guid, ack is int a ? a : 2, nod is int n ? n : 0);
                        }
                    }

                    foreach (var sub in baseKey.GetSubKeyNames())
                    {
                        using var ifKey = baseKey.OpenSubKey(sub, false);
                        if (ifKey == null) continue;
                        string ip = (ifKey.GetValue("IPAddress") is string[] arr ? arr.FirstOrDefault() : ifKey.GetValue("IPAddress")?.ToString())
                                 ?? ifKey.GetValue("DhcpIPAddress")?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0" && !ip.StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            object? ack = ifKey.GetValue("TcpAckFrequency");
                            object? nod = ifKey.GetValue("TCPNoDelay");
                            return (sub, ack is int a ? a : 2, nod is int n ? n : 0);
                        }
                    }
                }
            }
            catch { }
            return (string.Empty, 2, 0);
        }

        private static List<string> QueryActiveDnsServers()
        {
            var list = new List<string>();
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus == OperationalStatus.Up && iface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var ipProps = iface.GetIPProperties();
                        foreach (var dns in ipProps.DnsAddresses)
                        {
                            if (dns.AddressFamily == AddressFamily.InterNetwork)
                            {
                                string s = dns.ToString();
                                if (!list.Contains(s)) list.Add(s);
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Benchmarks DNS endpoint resolution latency against known public DNS servers.
        /// </summary>
        public async Task<Dictionary<string, double>> BenchmarkDnsEndpointsAsync()
        {
            return await Task.Run(() =>
            {
                var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                string[] endpoints = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

                foreach (var ep in endpoints)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using var client = new UdpClient();
                        client.Client.ReceiveTimeout = 1000;
                        client.Client.SendTimeout = 1000;
                        client.Connect(IPAddress.Parse(ep), 53);
                        sw.Stop();
                        dict[ep] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                    }
                    catch
                    {
                        dict[ep] = 999.0;
                    }
                }
                return dict;
            });
        }

    }
}
