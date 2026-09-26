using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Planning;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4MaximumPerformancePlannerTests
    {
        [Fact]
        public async Task GeneratePlanAsync_CalculatesAdaptiveSafeFloorAndCandidates()
        {
            var planner = new MaximumPerformancePlanner();
            var plan = await planner.GeneratePlanAsync(passNumber: 1);

            Assert.NotNull(plan);
            Assert.True(plan.InitialProcessCount > 0, "Initial process count must be recorded");
            Assert.True(plan.TargetSafeMinimumRangeLow >= 65, "Target safe minimum floor should be at least 65");
            Assert.True(plan.TargetSafeMinimumRangeHigh > plan.TargetSafeMinimumRangeLow, "Target high range must exceed low range");
            Assert.NotNull(plan.Candidates);
        }
    }
}
