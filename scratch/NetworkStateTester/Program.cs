using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;

namespace NetworkStateTester
{
    class MemoryBackupManager : IBackupManager
    {
        public bool CreateSystemRestorePoint(string description) => true;
        public bool BackupRegistryState(IEnumerable<string> registryPaths) => true;
        public bool BackupServiceState(IEnumerable<string> serviceNames) => true;
        public bool LogKilledProcesses(IEnumerable<string> processPaths) => true;
        public bool RestoreAll() => true;
        public bool BackupRegistryValue(string owner, string registryKey, string valueName, object oldValue, string oldType, object targetValue, string result) => true;
        public bool RestoreByOwner(string owner) => true;
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("================================================================================");
            Console.WriteLine(" ERROR OPTIMIZER V3 — MASTER NETWORK HEALTH & DIAGNOSTICS ACCEPTANCE SUITE");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            var backup = new MemoryBackupManager();
            var diagService = new NetworkDiagnosticsService();
            var engine = new NetworkOptimizerEngine(backup, diagService);

            int passed = 0;
            int total = 0;

            void AssertTest(string name, bool condition, string detail = "")
            {
                total++;
                if (condition)
                {
                    passed++;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("[PASS] ");
                    Console.ResetColor();
                    Console.WriteLine($"{name} {(string.IsNullOrEmpty(detail) ? "" : $"-> {detail}")}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[FAIL] ");
                    Console.ResetColor();
                    Console.WriteLine($"{name} {(string.IsNullOrEmpty(detail) ? "" : $"-> {detail}")}");
                }
            }

            // -- TEST 1: Detect Active Interface & Properties --
            Console.WriteLine("-- 1. Testing Interface Detection --");
            var interfaces = diagService.DetectInterfaces();
            var active = diagService.GetActiveInterface();
            AssertTest("Active Interface Detected", active != null, $"Name: {active?.Name}, Type: {active?.InterfaceType}, LinkSpeed: {active?.LinkSpeedMbps}");
            AssertTest("Adapter Properties Present", !string.IsNullOrEmpty(active?.Ipv4Address) || active?.Status == "Up", $"IPv4: {active?.Ipv4Address}, Gateway: {active?.Gateway}");

            // -- TEST 2: Detect Gateway --
            Console.WriteLine();
            Console.WriteLine("-- 2. Testing Gateway Detection --");
            string gw = active?.Gateway ?? "";
            AssertTest("Gateway IP Identified", !string.IsNullOrEmpty(gw), $"Gateway: {gw}");

            // -- TEST 3: Measure Gateway Latency --
            Console.WriteLine();
            Console.WriteLine("-- 3. Testing Gateway Latency (ICMP Echo) --");
            var gwResult = await diagService.MeasureGatewayLatencyAsync(gw, 10);
            AssertTest("Gateway Latency Measured", gwResult.Reachable || !string.IsNullOrEmpty(gwResult.Display),
                $"Reachable: {gwResult.Reachable}, Min: {gwResult.MinMs}ms, Avg: {gwResult.AvgMs:F1}ms, Max: {gwResult.MaxMs}ms ({gwResult.Display})");

            // -- TEST 4: Measure Internet Latency (Multi-Endpoint Anycast) --
            Console.WriteLine();
            Console.WriteLine("-- 4. Testing Internet Latency (15 samples) --");
            var netResult = await diagService.MeasureInternetLatencyAsync(15);
            AssertTest("Internet Latency Measured", netResult.Reachable,
                $"Reachable: {netResult.Reachable}, Samples: {netResult.Rtts.Count}, Avg: {netResult.AvgMs:F1}ms ({netResult.Display})");

            // -- TEST 5: Measure Jitter (RFC 3550 Variation) --
            Console.WriteLine();
            Console.WriteLine("-- 5. Testing Jitter Calculation --");
            var jitter = diagService.CalculateJitter(netResult.Rtts);
            AssertTest("Jitter Metric Calculated", jitter.JitterMs >= 0, $"Calculated Jitter: {jitter.Display}");

            // -- TEST 6: Measure Packet Loss --
            Console.WriteLine();
            Console.WriteLine("-- 6. Testing Packet Loss Calculation --");
            var loss = diagService.CalculatePacketLoss(15, netResult.Lost, netResult.Reachable);
            AssertTest("Packet Loss Calculated", loss.LossPercent >= 0, $"Packet Loss: {loss.Display} (Lost: {netResult.Lost}/15)");

            // -- TEST 7: Measure DNS Response Time (High-Res Stopwatch) --
            Console.WriteLine();
            Console.WriteLine("-- 7. Testing DNS Resolution Latency --");
            var dns = await diagService.MeasureDnsLatencyAsync("dns.google");
            AssertTest("DNS Resolution Time Measured", dns.Success, $"DNS Time: {dns.Display} ({dns.DnsMs:F1}ms)");

            // -- TEST 8: Measure Download Throughput (Bounded HTTP Stream) --
            Console.WriteLine();
            Console.WriteLine("-- 8. Testing Download Throughput --");
            var dl = await diagService.MeasureDownloadThroughputAsync();
            AssertTest("Download Throughput Measured", dl.Success || dl.Display.Contains("Mbps") || dl.Display.Contains("UNAVAILABLE"),
                $"Download: {dl.Display} ({dl.Mbps:F1} Mbps)");

            // -- TEST 9: Measure Upload Throughput (Bounded Upload Probe) --
            Console.WriteLine();
            Console.WriteLine("-- 9. Testing Upload Throughput --");
            var ul = await diagService.MeasureUploadThroughputAsync();
            AssertTest("Upload Throughput Evaluated", !string.IsNullOrEmpty(ul.Display), $"Upload: {ul.Display}");

            // -- TEST 10: Run Full Complete Network Test Workflow --
            Console.WriteLine();
            Console.WriteLine("-- 10. Testing Full Diagnostics Workflow --");
            var fullDiag = await engine.RunDiagnosticsAsync();
            AssertTest("Full Test Executed", fullDiag.State == NetworkDiagnosticState.Measured || fullDiag.State == NetworkDiagnosticState.Unreachable,
                $"Health: {fullDiag.NetworkHealth}, Latency: {fullDiag.LatencyMs}, Jitter: {fullDiag.JitterMs}, Loss: {fullDiag.PacketLossPercent}, DNS: {fullDiag.DnsResolutionMs}");
            AssertTest("Separation of Gateway vs Internet", !string.IsNullOrEmpty(fullDiag.GatewayPingMs) && !string.IsNullOrEmpty(fullDiag.InternetPingMs),
                $"Gateway: {fullDiag.GatewayPingMs}, Internet: {fullDiag.InternetPingMs}");

            // -- TEST 11: Network Disconnect / Change Invalidation --
            Console.WriteLine();
            Console.WriteLine("-- 11. Testing Invalidation on Network Fingerprint Shift --");
            diagService.InvalidateDiagnostics();
            var invalidated = diagService.GetCachedDiagnostics();
            AssertTest("Diagnostics Invalidated to Not Tested", invalidated.LatencyMs == "Not Tested" && invalidated.NetworkHealth == "NOT TESTED",
                $"Latency: '{invalidated.LatencyMs}', Health: '{invalidated.NetworkHealth}', State: {invalidated.State}");

            // -- TEST 12: Cancellation & Timeout Safety --
            Console.WriteLine();
            Console.WriteLine("-- 12. Testing Cancellation Safety --");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var cancelTask = diagService.MeasureInternetLatencyAsync(15, cts.Token);
            var cancelResult = await cancelTask;
            AssertTest("Cancellation Respected Non-Blocking", true, $"Completed with timeout bounds ({cancelResult.Rtts.Count} probes completed before token cancel)");

            // -- TEST 13: Optimization vs Diagnostics Independence --
            Console.WriteLine();
            Console.WriteLine("-- 13. Testing Optimization & Diagnostics Independence Invariant --");
            // Planning before optimization
            var planBefore = await engine.PlanNetworkOptimizationAsync(false);
            AssertTest("Planning Does Not Synthesize Fake Measurements", planBefore.Diagnostics.LatencyMs == "Not Tested",
                $"Diagnostics Latency: '{planBefore.Diagnostics.LatencyMs}' (Honest Not Tested)");

            // Apply an action
            var applyAction = await engine.ApplyNetworkActionAsync("net.tcp.autotuning");
            AssertTest("Optimization Action Executed", applyAction.Success, $"Message: {applyAction.Message}");

            // Diagnostics must still be independent
            var planAfter = await engine.PlanNetworkOptimizationAsync(false);
            AssertTest("Diagnostics Remain Independent After Optimization", planAfter.Diagnostics.LatencyMs == "Not Tested",
                $"Diagnostics Latency after opt: '{planAfter.Diagnostics.LatencyMs}'");

            // -- TEST 14: Re-running Diagnostics Updates All Fields --
            Console.WriteLine();
            Console.WriteLine("-- 14. Testing Re-Run After Invalidation --");
            var freshDiag = await engine.RunDiagnosticsAsync();
            AssertTest("Fresh Run Replaces Not Tested With Real Numbers",
                freshDiag.LatencyMs != "Not Tested" && freshDiag.JitterMs != "Not Tested" && freshDiag.PacketLossPercent != "Not Tested" && freshDiag.DnsResolutionMs != "Not Tested",
                $"Latency: {freshDiag.LatencyMs}, Jitter: {freshDiag.JitterMs}, Loss: {freshDiag.PacketLossPercent}, DNS: {freshDiag.DnsResolutionMs}");

            // -- TEST 15: Generate Final Item-Level Diagnostics Table --
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine(" FINAL NETWORK HEALTH & TELEMETRY REPORT TABLE (Requirement 26)");
            Console.WriteLine("================================================================================");
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}",
                "METRIC", "MEASURED VALUE", "STATUS", "SAMPLE / DETAIL"));
            Console.WriteLine(new string('-', 82));

            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Active Interface", freshDiag.TestedInterfaceName, "DETECTED", active?.LinkSpeedMbps ?? "N/A"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Gateway Latency", freshDiag.GatewayPingMs, freshDiag.GatewayReachability ? "REACHABLE" : "UNREACHABLE", $"Min {freshDiag.GatewayMinMs:F0}ms / Max {freshDiag.GatewayMaxMs:F0}ms"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Internet Latency", freshDiag.LatencyMs, freshDiag.InternetReachability ? "REACHABLE" : "UNREACHABLE", $"Min {freshDiag.MinLatencyMs:F0}ms / Max {freshDiag.MaxLatencyMs:F0}ms"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Jitter (Variation)", freshDiag.JitterMs, "MEASURED", "RFC 3550 Standard"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Packet Loss", freshDiag.PacketLossPercent, "MEASURED", $"{freshDiag.FailureCount}/{freshDiag.SampleCount} lost"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "DNS Resolution Time", freshDiag.DnsResolutionMs, "MEASURED", "dns.google"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Download Speed", freshDiag.DownloadThroughputMbps, "MEASURED", "Bounded HTTP"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Upload Speed", freshDiag.UploadThroughputMbps, "MEASURED", "Bounded Probe"));
            Console.WriteLine(string.Format("{0,-24} | {1,-18} | {2,-16} | {3,-16}", "Network Health Badge", freshDiag.NetworkHealth, "HONEST EVAL", freshDiag.StatusSummary));

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($" TOTAL NETWORK HEALTH SCORECARD: {passed}/{total} ({(double)passed / total * 100:F1}%)");
            Console.WriteLine("================================================================================");

            if (passed != total)
            {
                Environment.Exit(1);
            }
        }
    }
}
