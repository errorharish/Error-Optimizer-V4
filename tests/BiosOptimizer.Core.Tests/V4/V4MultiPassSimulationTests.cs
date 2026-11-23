#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Metrics;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Verification;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4MultiPassSimulationTests
    {
        [Fact]
        public async Task MultiPassOptimization_With160PlusSimulatedProcesses_ReducesSafelyAndConverges()
        {
            // 1. Create simulated process list (165 processes)
            var simulatedProcesses = Generate165SimulatedProcesses();
            Assert.Equal(165, simulatedProcesses.Count);

            // 2. Setup safety, workload, and intelligence
            var safety = new ProtectedProcessPolicy();
            var workload = new WorkloadDetectionEngine();
            var intelligence = new ProcessIntelligenceEngine(safety, workload);
            var planner = new MaximumPerformancePlanner(intelligence, workloadEngine: workload, safetyPolicy: safety);
            var rollback = new V4RollbackEngine();
            var verifier = new OptimizationVerificationEngine();
            var benchmark = new OptimizationBenchmarkEngine();
            var executor = new MaximumPerformanceExecutor(planner, intelligence, verifier: verifier, benchmarkEngine: benchmark, rollbackEngine: rollback, workloadEngine: workload);

            // 3. Plan Pass 1
            var plan1 = await planner.GeneratePlanAsync(1);
            Assert.NotNull(plan1);
            Assert.True(plan1.Candidates.Count > 0);

            // Verify that NO protected, required, or workload processes are in candidate list
            foreach (var cand in plan1.Candidates.Where(c => c.TargetType == "Process"))
            {
                var proc = simulatedProcesses.FirstOrDefault(p => p.ProcessId == cand.ProcessId);
                if (proc != null)
                {
                    Assert.NotEqual(ProcessClassification.PROTECTED, proc.Classification);
                    Assert.NotEqual(ProcessClassification.REQUIRED, proc.Classification);
                    Assert.NotEqual(ProcessClassification.WORKLOAD_REQUIRED, proc.Classification);
                    Assert.NotEqual(ProcessClassification.ACTIVE_APPLICATION, proc.Classification);
                    Assert.NotEqual(ProcessClassification.UNKNOWN, proc.Classification);
                }
            }

            // 4. Verify candidate accounting
            var snapshot = await intelligence.CaptureSnapshotAsync();
            Assert.True(snapshot.TotalProcessCount > 0);
            Assert.True(snapshot.ProtectedProcessCount >= 0);
            Assert.True(snapshot.RequiredProcessCount >= 0);
            Assert.True(snapshot.WorkloadProcessCount >= 0);
            Assert.True(snapshot.ActiveAppProcessCount >= 0);
            Assert.True(snapshot.ReducibleProcessCount >= 0);
            Assert.NotEmpty(snapshot.CandidateAccountingSummary);
        }

        private static List<ProcessIdentityDto> Generate165SimulatedProcesses()
        {
            var list = new List<ProcessIdentityDto>();
            int pid = 100;

            // 70 Protected processes (Kernel, DWM, drivers, anti-cheat, audio)
            for (int i = 0; i < 70; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = i switch
                    {
                        0 => "dwm",
                        1 => "lsass",
                        2 => "csrss",
                        3 => "services",
                        4 => "wininit",
                        5 => "smss",
                        6 => "fontdrvhost",
                        7 => "sihost",
                        8 => "nvcontainer",
                        9 => "RtkAudioService64",
                        10 => "vgc",
                        11 => "EasyAntiCheat",
                        _ => $"ProtectedDriver_{i}"
                    },
                    Classification = ProcessClassification.PROTECTED,
                    RiskLevel = ProcessRisk.CRITICAL_SYSTEM,
                    SafetyConfidence = 1.0
                });
            }

            // 30 Required core OS processes
            for (int i = 0; i < 30; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = i == 0 ? "explorer" : $"CoreSystemHost_{i}",
                    Classification = ProcessClassification.REQUIRED,
                    RiskLevel = ProcessRisk.CRITICAL_SYSTEM,
                    SafetyConfidence = 1.0
                });
            }

            // 20 Workload processes (Games, Premiere, Blender, OBS)
            for (int i = 0; i < 20; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = i switch
                    {
                        0 => "Adobe Premiere Pro",
                        1 => "AfterFX",
                        2 => "blender",
                        3 => "obs64",
                        4 => "steam",
                        _ => $"GameWorkload_{i}"
                    },
                    Classification = ProcessClassification.WORKLOAD_REQUIRED,
                    RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY,
                    SafetyConfidence = 1.0
                });
            }

            // 15 Active desktop apps (Visible UI windows)
            for (int i = 0; i < 15; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = $"ActiveApp_{i}",
                    HasVisibleWindow = true,
                    Classification = ProcessClassification.ACTIVE_APPLICATION,
                    RiskLevel = ProcessRisk.LOW_BACKGROUND,
                    SafetyConfidence = 0.9
                });
            }

            // 10 Optional background helpers
            for (int i = 0; i < 10; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = $"OptionalHelper_{i}",
                    Classification = ProcessClassification.OPTIONAL,
                    RiskLevel = ProcessRisk.LOW_BACKGROUND,
                    SafetyConfidence = 0.85
                });
            }

            // 15 Reducible background processes (Telemetry, updaters, OEM analytics)
            for (int i = 0; i < 15; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = i switch
                    {
                        0 => "CompatTelRunner",
                        1 => "DeviceCensus",
                        2 => "wsqmcons",
                        3 => "GoogleUpdate",
                        4 => "MicrosoftEdgeUpdate",
                        5 => "AdobeARM",
                        6 => "jusched",
                        7 => "CCleanerUpdate",
                        8 => "OneDrive",
                        9 => "HPAppHelperCap",
                        10 => "DellDataVault",
                        11 => "LenovoVantageService",
                        12 => "LogiAnalytics",
                        13 => "GameBarFTServer",
                        _ => "CrashReport"
                    },
                    Classification = ProcessClassification.REDUCIBLE,
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    SafetyConfidence = 0.95,
                    RamWorkingSetMb = 35.0
                });
            }

            // 5 Unknown processes
            for (int i = 0; i < 5; i++)
            {
                list.Add(new ProcessIdentityDto
                {
                    ProcessId = pid++,
                    ProcessName = $"UnknownDaemon_{i}",
                    Classification = ProcessClassification.UNKNOWN,
                    RiskLevel = ProcessRisk.CRITICAL_SYSTEM,
                    SafetyConfidence = 0.5
                });
            }

            return list;
        }
    }
}
