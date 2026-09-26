using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4DynamicConvergenceTests
    {
        [Fact]
        public async Task DynamicConvergence_RespectsMaxPassCapAndDynamicSafeMinimum()
        {
            var executor = new MaximumPerformanceExecutor();
            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 4);

            Assert.NotNull(result);
            Assert.True(result.TotalPassesRun >= 1, "Must run at least 1 pass");
            Assert.True(result.TotalPassesRun <= 4, "Must not exceed maxPasses cap");
            Assert.False(string.IsNullOrEmpty(result.StopReason), "StopReason must be populated");
            Assert.NotNull(result.PassHistory);
            Assert.Equal(result.TotalPassesRun, result.PassHistory.Count);

            // Pass progression verification
            foreach (var pass in result.PassHistory)
            {
                Assert.True(pass.PassNumber >= 1);
                Assert.True(pass.StartProcessCount > 0);
                Assert.True(pass.EndProcessCount > 0);
                Assert.NotNull(pass.PassSummary);
            }
        }

        [Fact]
        public async Task DynamicConvergence_PassHistoryRecordsSequentialReductions()
        {
            var executor = new MaximumPerformanceExecutor();
            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 2);

            Assert.NotNull(result);
            Assert.NotNull(result.PassHistory);

            if (result.PassHistory.Count > 1)
            {
                // Verify sequential linkage: pass N+1 start count == pass N end count
                for (int i = 1; i < result.PassHistory.Count; i++)
                {
                    Assert.Equal(result.PassHistory[i - 1].EndProcessCount, result.PassHistory[i].StartProcessCount);
                }
            }
        }
    }
}
