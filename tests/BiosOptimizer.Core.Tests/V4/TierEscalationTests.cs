#nullable enable
using System;
using System.Collections.Generic;
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
    public class TierEscalationTests
    {
        private class ConfigurableTierPlanner : MaximumPerformancePlanner
        {
            private readonly Func<int, OptimizationPlanDto> _planGenerator;

            public ConfigurableTierPlanner(Func<int, OptimizationPlanDto> planGenerator)
            {
                _planGenerator = planGenerator;
            }

            public override Task<OptimizationPlanDto> GeneratePlanAsync(int passNumber = 1)
            {
                return Task.FromResult(_planGenerator(passNumber));
            }
        }

        [Fact]
        public async Task T_TIER_1_Tier1ReturnsZero_EscalatesToTier2()
        {
            int plannedPasses = 0;
            var planner = new ConfigurableTierPlanner(pass =>
            {
                plannedPasses++;
                var plan = new OptimizationPlanDto
                {
                    PassNumber = pass,
                    InitialProcessCount = 120
                };

                // Tier 1 (pass 1) produces 0 candidates; Tier 2 (pass 2) produces 1 candidate
                if (pass == 2)
                {
                    plan.Candidates.Add(new OptimizationCandidateDto
                    {
                        CandidateId = "Tier2-Svc1",
                        TargetType = "StartupEntry",
                        TargetIdentifier = "MockStartupApp",
                        InitialState = "Enabled",
                        TargetState = "Disabled",
                        Category = "Startup",
                        Reason = "Tier 2 candidate"
                    });
                }
                return plan;
            });

            var executor = new MaximumPerformanceExecutor(planner: planner);
            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 3);

            Assert.True(plannedPasses >= 2, $"Expected at least 2 passes attempted for escalation, ran {plannedPasses}");
            Assert.True(result.TotalPassesRun >= 2);
        }

        [Fact]
        public async Task T_TIER_2_Tier2ReturnsZero_EscalatesToTier3()
        {
            int plannedPasses = 0;
            var planner = new ConfigurableTierPlanner(pass =>
            {
                plannedPasses++;
                var plan = new OptimizationPlanDto
                {
                    PassNumber = pass,
                    InitialProcessCount = 130
                };

                // Tier 1 and Tier 2 return 0 candidates; Tier 3 returns 1 candidate
                if (pass == 3)
                {
                    plan.Candidates.Add(new OptimizationCandidateDto
                    {
                        CandidateId = "Tier3-AppX1",
                        TargetType = "StartupEntry",
                        TargetIdentifier = "MockOptionalStartup",
                        InitialState = "Enabled",
                        TargetState = "Disabled",
                        Category = "Optional",
                        Reason = "Tier 3 candidate"
                    });
                }
                return plan;
            });

            var executor = new MaximumPerformanceExecutor(planner: planner);
            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 3);

            Assert.True(plannedPasses >= 3, $"Expected escalation to Tier 3, ran {plannedPasses}");
            Assert.True(result.TotalPassesRun >= 3);
        }

        private class CustomBandEngine : ProcessTargetBandEngine
        {
            public override ProcessTargetBandDto CalculateTargetBand(int startingProcessCount, int protectedCount = 0, int workloadCount = 0, int activeAppsCount = 0)
            {
                var band = base.CalculateTargetBand(startingProcessCount, protectedCount, workloadCount, activeAppsCount);
                band.TargetFootprintCeiling = 9999;
                return band;
            }
        }

        [Fact]
        public async Task T_TIER_3_TargetBandReachedOnTier2_DoesNotRunTier3()
        {
            int passesRun = 0;
            var planner = new ConfigurableTierPlanner(pass =>
            {
                passesRun++;
                var plan = new OptimizationPlanDto
                {
                    PassNumber = pass,
                    InitialProcessCount = 70 // Within low target band
                };
                return plan;
            });

            var bandEngine = new CustomBandEngine();
            var executor = new MaximumPerformanceExecutor(planner: planner, targetBandEngine: bandEngine);
            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 4);

            // Since process count was within target band and 0 candidates returned, it should not greedily escalate to pass 4
            Assert.True(result.TotalPassesRun <= 2, $"Expected <= 2 passes, but ran {result.TotalPassesRun}");
        }

        [Fact]
        public async Task T_TIER_4_AllThreeTiersReturnZero_HonestDiscoveryStarved()
        {
            var planner = new ConfigurableTierPlanner(pass => new OptimizationPlanDto
            {
                PassNumber = pass,
                InitialProcessCount = 140
            });

            var executor = new MaximumPerformanceExecutor(planner: planner);
            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 3);

            // Verdict must be DISCOVERY_STARVED because zero reducible candidates were ever planned across all tiers
            Assert.Equal(OptimizationResultStates.DISCOVERY_STARVED, result.ResultState);
            Assert.Contains("rejected during classification", result.StopReason);
            Assert.NotEqual(OptimizationResultStates.MAXIMUM_SAFE_REACHED, result.ResultState);
        }
    }
}
