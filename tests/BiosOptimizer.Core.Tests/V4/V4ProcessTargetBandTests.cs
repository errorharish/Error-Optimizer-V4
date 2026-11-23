#nullable enable
using System;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4ProcessTargetBandTests
    {
        private readonly ProcessOpportunityEngine _engine = new();

        [Theory]
        [InlineData(350, 40, 10, 5)]
        [InlineData(300, 35, 8, 4)]
        [InlineData(200, 30, 5, 3)]
        public void AdaptiveBaseline_EstablishesDynamicProtectedFloor(int startingCount, int protectedCount, int workloadCount, int activeApps)
        {
            var baseline = _engine.CalculateAdaptiveBaseline(startingCount, protectedCount, workloadCount, activeApps, 0);

            Assert.True(baseline.TargetFootprintFloor >= protectedCount + workloadCount + activeApps, "Floor must preserve protected drivers and workloads");
            Assert.Equal(startingCount, baseline.TargetFootprintCeiling);
            Assert.True(baseline.CanReduceFurtherBeyondTarget, "Engine must allow reductions beyond baseline floor");
        }

        [Fact]
        public void AdaptiveBaseline_AdaptsToActiveGamingOrEditingWorkload()
        {
            int startingCount = 220;
            int workloadCount = 15;
            int activeApps = 5;
            int protectedDrivers = 60;

            var baseline = _engine.CalculateAdaptiveBaseline(
                startingCount,
                protectedCount: protectedDrivers,
                workloadCount: workloadCount,
                activeAppsCount: activeApps);

            Assert.True(baseline.TargetFootprintFloor >= protectedDrivers + workloadCount + activeApps, "Floor should dynamically account for workload");
            Assert.True(baseline.CanReduceFurtherBeyondTarget, "Engine must continue searching for safe opportunities beyond baseline");
        }

        [Fact]
        public void AdaptiveBaseline_NeverActsAsStoppingRule()
        {
            var baseline = _engine.CalculateAdaptiveBaseline(250, 30, 5, 2);

            bool isWithin = _engine.IsWithinTargetBand(65, baseline);
            Assert.True(isWithin, "65 should be within the adaptive baseline for 250 starting procs");
            Assert.True(baseline.CanReduceFurtherBeyondTarget, "Engine must continue searching for reductions based on safe opportunities");
        }

        [Fact]
        public void SafeOpportunityConvergence_SucceedsWhenNoRemainingOpportunities()
        {
            bool converged = _engine.IsSafeOpportunityConverged(0, false);
            Assert.True(converged, "System must converge when remaining safe opportunities reach zero");
        }
    }
}
