#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Coordinator;
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
    public class V4ForensicExecutionPipelineTests
    {
        [Fact]
        public async Task ControlledCandidate_TerminatesSuccessfully_AndIncrementsVerifiedReduction()
        {
            // 1. Launch a real controlled safe dummy process
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };

            var dummyProc = Process.Start(psi);
            Assert.NotNull(dummyProc);
            int dummyPid = dummyProc.Id;

            try
            {
                // Ensure it is running
                Assert.False(dummyProc.HasExited);

                var safetyPolicy = new ProtectedProcessPolicy();
                var identitySafety = new ProcessIdentitySafety();
                var verifier = new OptimizationVerificationEngine();
                var benchmarkEngine = new OptimizationBenchmarkEngine();
                var rollbackEngine = new V4RollbackEngine();
                var workloadEngine = new WorkloadDetectionEngine();

                // Create a plan with this controlled candidate
                DateTime procStartTime = DateTime.UtcNow;
                try { procStartTime = dummyProc.StartTime; } catch { }
                string execPath = "cmd.exe";
                try { execPath = dummyProc.MainModule?.FileName ?? "cmd.exe"; } catch { }

                var candidate = new OptimizationCandidateDto
                {
                    CandidateId = $"PROC_CONTROLLED_{dummyPid}",
                    TargetType = "Process",
                    TargetIdentifier = "cmd",
                    DisplayName = "Controlled Test Process",
                    Category = "Running Processes",
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    InitialState = "Running",
                    TargetState = "Terminated",
                    Reason = "Controlled Integration Test Candidate",
                    SafetyConfidence = 1.0,
                    ProcessId = dummyPid,
                    ProcessStartTime = procStartTime,
                    ExecutablePath = execPath,
                    Publisher = "Microsoft Corporation",
                    IsSelected = true
                };

                var plan = new OptimizationPlanDto
                {
                    PassNumber = 1,
                    InitialProcessCount = 100,
                    EstimatedProcessReduction = 1,
                    Candidates = { candidate }
                };

                // Mock planner that returns our plan on pass 1, then empty on pass 2
                var mockPlanner = new MockControlledPlanner(plan);

                var executor = new MaximumPerformanceExecutor(
                    planner: mockPlanner,
                    processIntelligence: new ProcessIntelligenceEngine(safetyPolicy, workloadEngine),
                    identitySafety: identitySafety,
                    verifier: verifier,
                    benchmarkEngine: benchmarkEngine,
                    rollbackEngine: rollbackEngine,
                    workloadEngine: workloadEngine);

                var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 2);

                // Verification assertions
                Assert.True(result.Success);
                Assert.True(result.VerifiedProcessesTerminated == 1,
                    $"Expected 1 termination but got {result.VerifiedProcessesTerminated}. Actions count: {result.Actions.Count}. Details: {string.Join(" | ", result.Actions.Select(a => $"{a.TargetIdentifier}:{a.Status}:{a.Reason}:{a.VerificationMessage}"))}");
                Assert.Equal(1, result.ActualProcessReduction);
                Assert.Equal(1, result.ProcessActionsPlanned);
                Assert.Equal(1, result.ProcessActionsExecuted);
                Assert.Equal(1, result.ProcessActionsVerified);
                Assert.Equal(0, result.ProcessActionsFailed);

                // Verify dummy process was actually killed
                dummyProc.Refresh();
                Assert.True(dummyProc.HasExited);
            }
            finally
            {
                try
                {
                    if (!dummyProc.HasExited)
                    {
                        dummyProc.Kill(entireProcessTree: true);
                    }
                    dummyProc.Dispose();
                }
                catch { }
            }
        }

        [Fact]
        public async Task ZeroCandidates_ReportsAccurateReason_WithoutFalseMaximumOptimization()
        {
            var safetyPolicy = new ProtectedProcessPolicy();
            var identitySafety = new ProcessIdentitySafety();
            var verifier = new OptimizationVerificationEngine();
            var benchmarkEngine = new OptimizationBenchmarkEngine();
            var rollbackEngine = new V4RollbackEngine();
            var workloadEngine = new WorkloadDetectionEngine();

            var emptyPlan = new OptimizationPlanDto
            {
                PassNumber = 1,
                InitialProcessCount = 134,
                EstimatedProcessReduction = 0
            };

            var mockPlanner = new MockControlledPlanner(emptyPlan);

            var executor = new MaximumPerformanceExecutor(
                planner: mockPlanner,
                processIntelligence: new ProcessIntelligenceEngine(safetyPolicy, workloadEngine),
                identitySafety: identitySafety,
                verifier: verifier,
                benchmarkEngine: benchmarkEngine,
                rollbackEngine: rollbackEngine,
                workloadEngine: workloadEngine);

            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 1);

            Assert.True(result.Success);
            Assert.Equal(0, result.VerifiedProcessesTerminated);
            Assert.Equal(0, result.ActualProcessReduction);
            Assert.Equal(0, result.ProcessActionsPlanned);
            Assert.Equal(0, result.ProcessActionsVerified);

            // Must NOT report false "TARGET_REACHED_CONVERGED"
            Assert.NotEqual(OptimizationResultStates.TARGET_REACHED_CONVERGED, result.ResultState);
            Assert.True(
                result.ResultState == OptimizationResultStates.NO_VERIFIED_IMMEDIATE_REDUCTIONS_IDENTIFIED ||
                result.ResultState == OptimizationResultStates.PERSISTENT_OPTIMIZATIONS_APPLIED ||
                result.ResultState == OptimizationResultStates.DISCOVERY_STARVED,
                $"Expected NO_VERIFIED_IMMEDIATE_REDUCTIONS_IDENTIFIED, PERSISTENT_OPTIMIZATIONS_APPLIED, or DISCOVERY_STARVED but got {result.ResultState}");
        }

        private class MockControlledPlanner : MaximumPerformancePlanner
        {
            private readonly OptimizationPlanDto _initialPlan;
            private int _callCount;

            public MockControlledPlanner(OptimizationPlanDto initialPlan)
            {
                _initialPlan = initialPlan;
            }

            public override Task<OptimizationPlanDto> GeneratePlanAsync(int passNumber = 1)
            {
                _callCount++;
                if (_callCount == 1)
                {
                    return Task.FromResult(_initialPlan);
                }

                return Task.FromResult(new OptimizationPlanDto
                {
                    PassNumber = passNumber,
                    Candidates = { }
                });
            }
        }
    }
}
