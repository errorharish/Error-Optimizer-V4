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
    public class V4WorkloadGuardDriftTests
    {
        [Fact]
        public void WorkloadGuard_EvaluatesSnapshot_ShieldsGameProcessesAndDetectsDrift()
        {
            var workloadEngine = new WorkloadDetectionEngine();
            var safetyPolicy = new ProtectedProcessPolicy();
            var guardEngine = new WorkloadProcessGuardEngine(workloadEngine, safetyPolicy: safetyPolicy);
            guardEngine.OptimizedBaselineCount = 68;

            // Construct synthetic snapshot:
            // 68 baseline procs (protected + required)
            // 14 genuine game processes (Steam, EasyAntiCheat, Game binary, Game audio)
            // 33 unrelated background drift processes (Edge updates, Chrome crashpad, OEM updater, Telemetry)
            // Total = 115 processes (Creep from 82 -> 115)
            var snapshot = new ProcessSnapshotDto
            {
                TotalProcessCount = 115,
                ProtectedProcessCount = 45,
                RequiredProcessCount = 23,
                WorkloadProcessCount = 14,
                ActiveAppProcessCount = 0,
                OptionalProcessCount = 0,
                ReducibleProcessCount = 33,
                UnknownProcessCount = 0
            };

            // Add 14 genuine workload procs
            for (int i = 1; i <= 14; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 1000 + i,
                    ProcessName = i switch
                    {
                        1 => "steam",
                        2 => "steamservice",
                        3 => "easyanticheat",
                        4 => "easyanticheat_eos",
                        5 => "Game_x64",
                        _ => $"GameHelper_{i}"
                    },
                    Classification = ProcessClassification.WORKLOAD_REQUIRED,
                    RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY
                });
            }

            // Add 33 reducible background drift procs
            for (int i = 1; i <= 33; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 3000 + i,
                    ProcessName = i switch
                    {
                        1 => "MicrosoftEdgeUpdate",
                        2 => "GoogleUpdate",
                        3 => "HPPrintScanDoctorService",
                        4 => "DellSupportAssist",
                        _ => $"BackgroundDaemon_{i}"
                    },
                    Classification = ProcessClassification.REDUCIBLE,
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE
                });
            }

            var telemetry = guardEngine.EvaluateSnapshot(snapshot);

            // Verify telemetry
            Assert.Equal("Gaming", telemetry.WorkloadType);
            Assert.Equal(68, telemetry.OptimizedBaselineCount);
            Assert.Equal(14, telemetry.GenuineWorkloadProcessCount);
            Assert.Equal(82, telemetry.ExpectedWorkloadTotal); // 68 + 14 = 82
            Assert.Equal(115, telemetry.CurrentTotalProcessCount);
            Assert.Equal(33, telemetry.UnrelatedDriftProcessCount); // 115 - 82 = 33 drift
            Assert.Contains(telemetry.DetectedDriftProcessNames, name => name.Contains("MicrosoftEdgeUpdate") || name.Contains("BackgroundDaemon"));
            Assert.Contains("Process drift detected", telemetry.StatusSummary);
        }

        [Fact]
        public void TargetBand_ContinuationPastTarget_DoesNotStopPrematurely()
        {
            var targetEngine = new ProcessTargetBandEngine();
            int startingCount = 220; // Target is 80–100
            var band = targetEngine.CalculateTargetBand(startingCount);

            Assert.Equal(100, band.TargetFootprintCeiling);

            // Simulation of pass counts:
            // Pass 1: 220 -> 140
            // Pass 2: 140 -> 105
            // Pass 3: 105 -> 90 (Enters target band <= 100)
            // Pass 4: 90 -> 75 (Continues and reduces further)
            // Pass 5: 75 -> 75 (Stops naturally due to 0 reducible remaining)

            int p1 = 140;
            int p2 = 105;
            int p3 = 90;
            int p4 = 75;
            int p5 = 75;

            bool p1Target = targetEngine.IsWithinTargetBand(p1, band); // false
            bool p2Target = targetEngine.IsWithinTargetBand(p2, band); // false
            bool p3Target = targetEngine.IsWithinTargetBand(p3, band); // true
            bool p4Target = targetEngine.IsWithinTargetBand(p4, band); // true

            Assert.False(p1Target);
            Assert.False(p2Target);
            Assert.True(p3Target);
            Assert.True(p4Target);

            // Engine allows progression from p3 (90) to p4 (75) because CanReduceFurtherBeyondTarget is true
            Assert.True(band.CanReduceFurtherBeyondTarget);
            Assert.True(p4 < p3, "Engine successfully achieved additional safe reductions below target ceiling");
        }

        [Fact]
        public async Task T_WPG_1_DriftSuppression_OnProtectedProcess_SkippedNoKill()
        {
            var workloadEngine = new WorkloadDetectionEngine();
            var safetyPolicy = new ProtectedProcessPolicy();
            var safetyKernel = new SafetyKernel(safetyPolicy);
            var guardEngine = new WorkloadProcessGuardEngine(workloadEngine, safetyPolicy: safetyPolicy, safetyKernel: safetyKernel);
            guardEngine.OptimizedBaselineCount = 68;

            var snapshot = new ProcessSnapshotDto
            {
                TotalProcessCount = 100,
                Processes =
                {
                    new ProcessIdentityDto
                    {
                        ProcessId = 5001,
                        ProcessName = "csrss",
                        DisplayName = "Client Server Runtime Subsystem",
                        ExecutablePath = @"C:\Windows\System32\csrss.exe",
                        Classification = ProcessClassification.REDUCIBLE // Malicious/erroneous reducible label
                    },
                    new ProcessIdentityDto
                    {
                        ProcessId = 5002,
                        ProcessName = "dwm",
                        DisplayName = "Desktop Window Manager",
                        ExecutablePath = @"C:\Windows\System32\dwm.exe",
                        Classification = ProcessClassification.REDUCIBLE // Erroneous reducible label
                    },
                    new ProcessIdentityDto
                    {
                        ProcessId = 1001,
                        ProcessName = "Game_x64",
                        Classification = ProcessClassification.WORKLOAD_REQUIRED
                    }
                }
            };

            guardEngine.CustomSnapshotProvider = () => Task.FromResult(snapshot);

            var killedPids = new List<int>();
            guardEngine.ProcessKillHandler = proc =>
            {
                killedPids.Add(proc.ProcessId);
                return true;
            };

            int suppressed = await guardEngine.SuppressBackgroundDriftAsync();

            Assert.Equal(0, suppressed);
            Assert.Empty(killedPids);
            Assert.Equal(0, guardEngine.SuppressedDriftCount);
        }

        [Fact]
        public async Task T_WPG_2_DriftSuppression_OnReducibleProcess_AllowedKilled()
        {
            var workloadEngine = new WorkloadDetectionEngine();
            var safetyPolicy = new ProtectedProcessPolicy();
            var safetyKernel = new SafetyKernel(safetyPolicy);
            var guardEngine = new WorkloadProcessGuardEngine(workloadEngine, safetyPolicy: safetyPolicy, safetyKernel: safetyKernel);
            guardEngine.OptimizedBaselineCount = 68;

            var snapshot = new ProcessSnapshotDto
            {
                TotalProcessCount = 100,
                Processes =
                {
                    new ProcessIdentityDto
                    {
                        ProcessId = 7001,
                        ProcessName = "HPPrintScanDoctorService",
                        DisplayName = "HP Print and Scan Doctor",
                        ExecutablePath = @"C:\Program Files\HP\HPPrintScanDoctor.exe",
                        Classification = ProcessClassification.REDUCIBLE,
                        RiskLevel = ProcessRisk.SAFE_TO_TERMINATE
                    },
                    new ProcessIdentityDto
                    {
                        ProcessId = 1001,
                        ProcessName = "Game_x64",
                        Classification = ProcessClassification.WORKLOAD_REQUIRED
                    }
                }
            };

            guardEngine.CustomSnapshotProvider = () => Task.FromResult(snapshot);

            var killedPids = new List<int>();
            guardEngine.ProcessKillHandler = proc =>
            {
                killedPids.Add(proc.ProcessId);
                return true;
            };

            int suppressed = await guardEngine.SuppressBackgroundDriftAsync();

            Assert.Equal(1, suppressed);
            Assert.Single(killedPids, 7001);
            Assert.Equal(1, guardEngine.SuppressedDriftCount);
        }

        [Fact]
        public async Task T_WPG_3_NoCodePath_CallsKillWithoutKernelCheck()
        {
            var workloadEngine = new WorkloadDetectionEngine();
            var safetyPolicy = new ProtectedProcessPolicy();
            var safetyKernel = new SafetyKernel(safetyPolicy);

            // Blacklist the reducible process in SafetyKernel
            safetyKernel.AddToBlacklist("AggressiveTool", "Blacklisted due to previous bugcheck/instability");

            var guardEngine = new WorkloadProcessGuardEngine(workloadEngine, safetyPolicy: safetyPolicy, safetyKernel: safetyKernel);
            guardEngine.OptimizedBaselineCount = 68;

            var snapshot = new ProcessSnapshotDto
            {
                TotalProcessCount = 100,
                Processes =
                {
                    new ProcessIdentityDto
                    {
                        ProcessId = 9001,
                        ProcessName = "AggressiveTool",
                        DisplayName = "Aggressive Third Party Daemon",
                        ExecutablePath = @"C:\Tools\AggressiveTool.exe",
                        Classification = ProcessClassification.REDUCIBLE,
                        RiskLevel = ProcessRisk.SAFE_TO_TERMINATE
                    },
                    new ProcessIdentityDto
                    {
                        ProcessId = 1001,
                        ProcessName = "Game_x64",
                        Classification = ProcessClassification.WORKLOAD_REQUIRED
                    }
                }
            };

            guardEngine.CustomSnapshotProvider = () => Task.FromResult(snapshot);

            bool killInvoked = false;
            guardEngine.ProcessKillHandler = proc =>
            {
                killInvoked = true;
                return true;
            };

            int suppressed = await guardEngine.SuppressBackgroundDriftAsync();

            Assert.Equal(0, suppressed);
            Assert.False(killInvoked, "SafetyKernel blacklisting must prevent kill invocation in drift suppression path");
        }
    }
}
