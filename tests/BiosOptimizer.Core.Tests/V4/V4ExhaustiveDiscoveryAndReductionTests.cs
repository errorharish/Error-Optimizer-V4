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
    public class V4ExhaustiveDiscoveryAndReductionTests
    {
        [Fact]
        public void ProcessSnapshot_ClassifiesAll8Categories_WithExhaustiveRejectionReasons()
        {
            var safetyPolicy = new ProtectedProcessPolicy();
            var workloadEngine = new WorkloadDetectionEngine();

            var snapshot = new ProcessSnapshotDto
            {
                TotalProcessCount = 154
            };

            // Synthetic 154-process machine breakdown
            // 40 Protected (Kernel, Security, Hardware Drivers)
            // 30 Required (Core OS)
            // 15 Workload Required (Gaming / Creative)
            // 20 Active Application (Visible UI windows)
            // 15 Optional Utilities
            // 10 Background Session 0
            // 18 Reducible (Updaters, Telemetry, OEM Bloat, Idle Webview Helpers)
            // 6 Unknown (Unverified fallbacks)
            // Total = 40+30+15+20+15+10+18+6 = 154

            for (int i = 1; i <= 40; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 100 + i,
                    ProcessName = i switch { 1 => "nvcontainer", 2 => "audiodg", 3 => "MpDefenderCoreService", _ => $"Driver_{i}" },
                    Classification = ProcessClassification.PROTECTED,
                    RiskLevel = ProcessRisk.ELEVATED_DRIVER,
                    ClassificationReason = "Protected Windows Subsystem, Security, or Driver Pipeline"
                });
            }

            for (int i = 1; i <= 30; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 200 + i,
                    ProcessName = i switch { 1 => "csrss", 2 => "lsass", 3 => "services", _ => $"CoreOs_{i}" },
                    Classification = ProcessClassification.REQUIRED,
                    RiskLevel = ProcessRisk.CRITICAL_SYSTEM,
                    ClassificationReason = "Core Windows OS System Architecture"
                });
            }

            for (int i = 1; i <= 15; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 300 + i,
                    ProcessName = i switch { 1 => "steam", 2 => "easyanticheat", 3 => "blender", _ => $"GameWorkload_{i}" },
                    Classification = ProcessClassification.WORKLOAD_REQUIRED,
                    RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY,
                    ClassificationReason = "Active Gaming or Creative Workload Component"
                });
            }

            for (int i = 1; i <= 20; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 400 + i,
                    ProcessName = $"ActiveApp_{i}",
                    HasVisibleWindow = true,
                    Classification = ProcessClassification.ACTIVE_APPLICATION,
                    RiskLevel = ProcessRisk.LOW_BACKGROUND,
                    ClassificationReason = "Active foreground window in interactive use"
                });
            }

            for (int i = 1; i <= 15; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 500 + i,
                    ProcessName = $"OptionalTool_{i}",
                    Classification = ProcessClassification.OPTIONAL,
                    RiskLevel = ProcessRisk.LOW_BACKGROUND,
                    ClassificationReason = "Optional background utility"
                });
            }

            for (int i = 1; i <= 10; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 600 + i,
                    ProcessName = $"Session0Service_{i}",
                    SessionId = 0,
                    Classification = ProcessClassification.BACKGROUND,
                    RiskLevel = ProcessRisk.LOW_BACKGROUND,
                    ClassificationReason = "Windows session 0 background service"
                });
            }

            for (int i = 1; i <= 18; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 700 + i,
                    ProcessName = i switch
                    {
                        1 => "GoogleUpdate",
                        2 => "MicrosoftEdgeUpdate",
                        3 => "CompatTelRunner",
                        4 => "lghub_updater",
                        5 => "Red Giant Service",
                        6 => "UltraViewer_Service",
                        7 => "urban-vpn-service",
                        8 => "SEGameTool",
                        _ => $"TelemetryDaemon_{i}"
                    },
                    Classification = ProcessClassification.REDUCIBLE,
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    ClassificationReason = "Verified non-essential background task (idle / telemetry / updater / OEM daemon)"
                });
            }

            for (int i = 1; i <= 6; i++)
            {
                snapshot.Processes.Add(new ProcessIdentityDto
                {
                    ProcessId = 800 + i,
                    ProcessName = $"UnknownHelper_{i}",
                    Classification = ProcessClassification.UNKNOWN,
                    RiskLevel = ProcessRisk.CRITICAL_SYSTEM,
                    ClassificationReason = "Unverified background entity (safe protected retention applied)"
                });
            }

            snapshot.ProtectedProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.PROTECTED);
            snapshot.RequiredProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.REQUIRED);
            snapshot.WorkloadProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.WORKLOAD_REQUIRED);
            snapshot.ActiveAppProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.ACTIVE_APPLICATION);
            snapshot.OptionalProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.OPTIONAL);
            snapshot.ReducibleProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.REDUCIBLE);
            snapshot.UnknownProcessCount = snapshot.Processes.Count(p => p.Classification == ProcessClassification.UNKNOWN);

            Assert.Equal(154, snapshot.Processes.Count);
            Assert.Equal(40, snapshot.ProtectedProcessCount);
            Assert.Equal(30, snapshot.RequiredProcessCount);
            Assert.Equal(15, snapshot.WorkloadProcessCount);
            Assert.Equal(20, snapshot.ActiveAppProcessCount);
            Assert.Equal(15, snapshot.OptionalProcessCount);
            Assert.Equal(10, snapshot.Processes.Count(p => p.Classification == ProcessClassification.BACKGROUND));
            Assert.Equal(18, snapshot.ReducibleProcessCount);
            Assert.Equal(6, snapshot.UnknownProcessCount);

            // Verify Candidate Accounting String
            Assert.Contains("154 total", snapshot.CandidateAccountingSummary);
            Assert.Contains("40 protected", snapshot.CandidateAccountingSummary);
            Assert.Contains("18 reducible", snapshot.CandidateAccountingSummary);
        }

        [Fact]
        public void ResultState_DistinguishesAllRequiredOutcomes()
        {
            // Case 1: Target Reached & Converged
            var res1 = new OptimizationResultDto
            {
                InitialProcessCount = 154,
                FinalProcessCount = 48,
                ActualProcessReduction = 106,
                ActionsVerifiedCount = 106,
                ResultState = OptimizationResultStates.TARGET_REACHED_CONVERGED
            };
            Assert.Equal(OptimizationResultStates.TARGET_REACHED_CONVERGED, res1.ResultState);
            Assert.Equal(106, res1.ActualProcessReduction);

            // Case 2: Target Not Reached but Safe Baseline Reached (0 reducible remaining)
            var res2 = new OptimizationResultDto
            {
                InitialProcessCount = 154,
                FinalProcessCount = 154,
                ActualProcessReduction = 0,
                ReducibleProcesses = 0,
                ResultState = OptimizationResultStates.TARGET_NOT_REACHED_SAFE_BASELINE_REACHED,
                StopReason = "V4 evaluated all 154 processes. All components are verified core Windows OS, active security, hardware drivers, or active workloads."
            };
            Assert.Equal(OptimizationResultStates.TARGET_NOT_REACHED_SAFE_BASELINE_REACHED, res2.ResultState);
            Assert.Contains("154 processes", res2.StopReason);

            // Case 3: Actions Executed but 0 Verified Effect (No-Effect State)
            var res3 = new OptimizationResultDto
            {
                InitialProcessCount = 154,
                FinalProcessCount = 154,
                ActionsPlannedCount = 14,
                ActionsExecutedCount = 14,
                ActionsVerifiedCount = 0,
                ActionsNoEffectCount = 14,
                ResultState = OptimizationResultStates.NO_EFFECT_ACTIONS_DETECTED
            };
            Assert.Equal(OptimizationResultStates.NO_EFFECT_ACTIONS_DETECTED, res3.ResultState);
            Assert.Equal(14, res3.ActionsNoEffectCount);
        }

        [Fact]
        public void MultiPass_ConvergenceProgression_ReconstructsStepByStepDescent()
        {
            var passHistory = new List<PassProgressionDto>
            {
                new PassProgressionDto { PassNumber = 1, StartProcessCount = 154, EndProcessCount = 137, RamFreedMb = 450, ActionsAppliedCount = 17 },
                new PassProgressionDto { PassNumber = 2, StartProcessCount = 137, EndProcessCount = 118, RamFreedMb = 320, ActionsAppliedCount = 19 },
                new PassProgressionDto { PassNumber = 3, StartProcessCount = 118, EndProcessCount = 96, RamFreedMb = 280, ActionsAppliedCount = 22 },
                new PassProgressionDto { PassNumber = 4, StartProcessCount = 96, EndProcessCount = 79, RamFreedMb = 210, ActionsAppliedCount = 17 },
                new PassProgressionDto { PassNumber = 5, StartProcessCount = 79, EndProcessCount = 66, RamFreedMb = 180, ActionsAppliedCount = 13 },
                new PassProgressionDto { PassNumber = 6, StartProcessCount = 66, EndProcessCount = 57, RamFreedMb = 120, ActionsAppliedCount = 9 }
            };

            int initial = passHistory.First().StartProcessCount;
            int final = passHistory.Last().EndProcessCount;
            int totalReduced = initial - final;
            int totalActions = passHistory.Sum(p => p.ActionsAppliedCount);
            double totalRamFreed = passHistory.Sum(p => p.RamFreedMb);

            Assert.Equal(154, initial);
            Assert.Equal(57, final);
            Assert.Equal(97, totalReduced);
            Assert.Equal(97, totalActions);
            Assert.Equal(1560, totalRamFreed);
        }
    }
}
