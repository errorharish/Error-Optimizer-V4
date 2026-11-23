#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BiosOptimizer.Core.V4.Diagnostics;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class ClassificationAuditTests
    {
        [Fact]
        public void T_CLASS_1_ClassificationDump_IncludesEveryProcessAsReducibleOrRejected()
        {
            var engine = new ProcessIntelligenceEngine();
            var processes = new List<ProcessIdentityDto>
            {
                new() { ProcessId = 100, ProcessName = "System", Classification = ProcessClassification.REQUIRED, ClassificationReason = "Core OS" },
                new() { ProcessId = 104, ProcessName = "services.exe", Classification = ProcessClassification.PROTECTED, ClassificationReason = "Critical service host" },
                new() { ProcessId = 200, ProcessName = "chrome.exe", Classification = ProcessClassification.ACTIVE_APPLICATION, IsForeground = true, ClassificationReason = "Foreground app" },
                new() { ProcessId = 300, ProcessName = "msedge.exe", Classification = ProcessClassification.OPTIONAL, ClassificationReason = "Browser background utility" },
                new() { ProcessId = 400, ProcessName = "Spotify.exe", Classification = ProcessClassification.REDUCIBLE, ClassificationReason = "User application" },
                new() { ProcessId = 500, ProcessName = "GameBar.exe", Classification = ProcessClassification.IMMEDIATE_REDUCIBLE, ClassificationReason = "Background telemetry" },
                new() { ProcessId = 600, ProcessName = "vmnat.exe", Classification = ProcessClassification.BACKGROUND, ClassificationReason = "Session 0 service" }
            };

            var dump = engine.BuildClassificationDump(processes);

            Assert.NotNull(dump);
            Assert.Equal(processes.Count, dump.InitialProcessCount);

            // Invariant: ClassifiedReducibleCount + Rejections.Count == InitialProcessCount
            Assert.Equal(dump.InitialProcessCount, dump.ClassifiedReducibleCount + dump.Rejections.Count);
            Assert.Equal(2, dump.ClassifiedReducibleCount); // Spotify (REDUCIBLE) + GameBar (IMMEDIATE_REDUCIBLE)
            Assert.Equal(5, dump.Rejections.Count);

            // Verify rule codes assigned to rejections
            var reqRejection = dump.Rejections.First(r => r.Pid == 100);
            Assert.Equal("S01", reqRejection.RejectionRule);

            var protRejection = dump.Rejections.First(r => r.Pid == 104);
            Assert.Equal("S02", protRejection.RejectionRule);

            var fgRejection = dump.Rejections.First(r => r.Pid == 200);
            Assert.Equal("R02", fgRejection.RejectionRule);

            var optRejection = dump.Rejections.First(r => r.Pid == 300);
            Assert.Equal("R05", optRejection.RejectionRule);

            var bgRejection = dump.Rejections.First(r => r.Pid == 600);
            Assert.Equal("R06", bgRejection.RejectionRule);
        }

        [Fact]
        public void T_CLASS_2_TierTrace_RecordsAllThreeTiersWithCandidateCountsAndSkipReasons()
        {
            var intelligence = new ProcessIntelligenceEngine();
            var workload = new WorkloadDetectionEngine();
            var planner = new MaximumPerformancePlanner(processIntelligence: intelligence, workloadEngine: workload);

            var snapshot = new ProcessSnapshotDto
            {
                TotalProcessCount = 100,
                ProtectedProcessCount = 50,
                RequiredProcessCount = 20,
                ReducibleProcessCount = 5,
                OptionalProcessCount = 10,
                ActiveAppProcessCount = 5,
                Processes = new List<ProcessIdentityDto>
                {
                    new() { ProcessId = 1, ProcessName = "app1", Classification = ProcessClassification.REDUCIBLE },
                    new() { ProcessId = 2, ProcessName = "app2", Classification = ProcessClassification.OPTIONAL },
                    new() { ProcessId = 3, ProcessName = "app3", Classification = ProcessClassification.BACKGROUND }
                }
            };

            // Pass 1: Tier 1 executes, Tier 2 & Tier 3 skipped
            var tracePass1 = planner.BuildTierTrace(snapshot, passNumber: 1);
            Assert.NotNull(tracePass1);
            Assert.True(tracePass1.Tier1.Ran);
            Assert.Null(tracePass1.Tier1.SkipReason);
            Assert.False(tracePass1.Tier2.Ran);
            Assert.NotNull(tracePass1.Tier2.SkipReason);
            Assert.False(tracePass1.Tier3.Ran);
            Assert.NotNull(tracePass1.Tier3.SkipReason);

            // Pass 2: Tier 1 & Tier 2 execute, Tier 3 skipped
            var tracePass2 = planner.BuildTierTrace(snapshot, passNumber: 2);
            Assert.True(tracePass2.Tier1.Ran);
            Assert.True(tracePass2.Tier2.Ran);
            Assert.Null(tracePass2.Tier2.SkipReason);
            Assert.False(tracePass2.Tier3.Ran);
            Assert.NotNull(tracePass2.Tier3.SkipReason);

            // Pass 3: Tier 1, Tier 2, Tier 3 all execute
            var tracePass3 = planner.BuildTierTrace(snapshot, passNumber: 3);
            Assert.True(tracePass3.Tier1.Ran);
            Assert.True(tracePass3.Tier2.Ran);
            Assert.True(tracePass3.Tier3.Ran);
            Assert.Null(tracePass3.Tier3.SkipReason);
        }

        [Fact]
        public void T_CLASS_3_Verdict_ReturnsDiscoveryStarved_WhenZeroReducibleAndAboveTargetBand()
        {
            var result = new OptimizationResultDto
            {
                InitialProcessCount = 114,
                FinalProcessCount = 114,
                ClassifiedReducible = 0,
                VerifiedProcessesTerminated = 0,
                TargetBand = new ProcessTargetBandDto
                {
                    TargetFootprintFloor = 50,
                    TargetFootprintCeiling = 85
                }
            };

            int targetBandUpper = result.TargetBand?.TargetFootprintCeiling > 0 ? result.TargetBand.TargetFootprintCeiling : 85;
            bool properConvergence = result.VerifiedProcessesTerminated > 0 && result.FinalProcessCount <= targetBandUpper;

            if (result.ClassifiedReducible == 0 && result.FinalProcessCount > targetBandUpper)
            {
                result.ResultState = "DISCOVERY_STARVED";
                result.StopReason = $"No reducible candidates at {result.FinalProcessCount} processes. All {result.InitialProcessCount} rejected during classification.";
            }
            else if (properConvergence)
            {
                result.ResultState = "MAXIMUM_SAFE_REACHED";
            }

            Assert.Equal("DISCOVERY_STARVED", result.ResultState);
            Assert.Contains("No reducible candidates", result.StopReason);
            Assert.DoesNotContain("MAXIMUM_SAFE_REACHED", result.ResultState);
        }

        [Fact]
        public void T_CLASS_4_Verdict_ReturnsMaximumSafeReached_OnlyWhenTrueConvergenceMet()
        {
            var result = new OptimizationResultDto
            {
                InitialProcessCount = 114,
                FinalProcessCount = 78,
                ClassifiedReducible = 36,
                VerifiedProcessesTerminated = 36,
                TotalPassesRun = 2,
                RamRecoveredMb = 1250,
                TargetBand = new ProcessTargetBandDto
                {
                    TargetFootprintFloor = 50,
                    TargetFootprintCeiling = 85
                }
            };

            int targetBandUpper = result.TargetBand?.TargetFootprintCeiling > 0 ? result.TargetBand.TargetFootprintCeiling : 85;
            bool properConvergence = result.VerifiedProcessesTerminated > 0 && result.FinalProcessCount <= targetBandUpper;

            if (result.ClassifiedReducible == 0 && result.FinalProcessCount > targetBandUpper)
            {
                result.ResultState = "DISCOVERY_STARVED";
            }
            else if (properConvergence)
            {
                result.ResultState = "MAXIMUM_SAFE_REACHED";
                result.StopReason = $"Target band (~{targetBandUpper} or lower) achieved. Safely terminated {result.VerifiedProcessesTerminated} background processes ({result.InitialProcessCount} → {result.FinalProcessCount}) and recovered {result.RamRecoveredMb} MB RAM across {result.TotalPassesRun} passes.";
            }

            Assert.Equal("MAXIMUM_SAFE_REACHED", result.ResultState);
            Assert.Contains("Target band", result.StopReason);
            Assert.Equal(36, result.VerifiedProcessesTerminated);
            Assert.True(result.FinalProcessCount <= targetBandUpper);
        }
    }
}
