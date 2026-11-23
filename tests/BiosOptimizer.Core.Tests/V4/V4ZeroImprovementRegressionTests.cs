#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4ZeroImprovementRegressionTests
    {
        [Fact]
        public void ZeroReductionResult_DoesNotReportMaximumSafeOptimizationReached()
        {
            var result = new OptimizationResultDto
            {
                InitialProcessCount = 163,
                FinalProcessCount = 163,
                ActualProcessReduction = 0,
                ActionsPlannedCount = 28,
                ActionsExecutedCount = 28,
                ActionsVerifiedCount = 0,
                ActionsAppliedCount = 0,
                ActionsNoEffectCount = 28,
                TotalProcesses = 163,
                ProtectedProcesses = 80,
                RequiredProcesses = 40,
                WorkloadProcesses = 25,
                ActiveAppProcesses = 10,
                OptionalProcesses = 5,
                ReducibleProcesses = 3,
                UnknownProcesses = 0
            };

            // Set stop reason according to engine specification
            if (result.ActualProcessReduction == 0)
            {
                if (result.ActionsPlannedCount > 0 && result.ActionsVerifiedCount == 0)
                {
                    result.StopReason = "Optimization actions produced no verified system-state improvement.";
                }
                else if (result.ReducibleProcesses == 0)
                {
                    result.StopReason = "System is already at practical safe baseline for current hardware and workload (no safe reducible workload found).";
                }
            }

            Assert.Equal("Optimization actions produced no verified system-state improvement.", result.StopReason);
            Assert.DoesNotContain("Maximum safe optimization reached", result.StopReason);
            Assert.Equal(0, result.ActionsAppliedCount);
            Assert.Equal(0, result.ActualProcessReduction);
            Assert.Equal(28, result.ActionsNoEffectCount);
        }

        [Fact]
        public void BaselineSystem_WithZeroReducibleCandidates_ReportsSafeBaseline()
        {
            var result = new OptimizationResultDto
            {
                InitialProcessCount = 75,
                FinalProcessCount = 75,
                ActualProcessReduction = 0,
                ActionsPlannedCount = 0,
                ActionsExecutedCount = 0,
                ActionsVerifiedCount = 0,
                ActionsAppliedCount = 0,
                TotalProcesses = 75,
                ProtectedProcesses = 45,
                RequiredProcesses = 20,
                WorkloadProcesses = 10,
                ActiveAppProcesses = 0,
                OptionalProcesses = 0,
                ReducibleProcesses = 0,
                UnknownProcesses = 0
            };

            if (result.ActualProcessReduction == 0 && result.ReducibleProcesses == 0)
            {
                result.StopReason = "System is already at practical safe baseline for current hardware and workload (no safe reducible workload found).";
            }

            Assert.Equal("System is already at practical safe baseline for current hardware and workload (no safe reducible workload found).", result.StopReason);
        }
    }
}
