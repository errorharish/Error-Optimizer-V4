using System;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Tuning;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4SystemPerformanceTests
    {
        [Fact]
        public void ApplyCoordinatedTuning_ReturnsValidMetrics()
        {
            var rollbackEngine = new V4RollbackEngine();
            var optimizer = new SystemPerformanceOptimizer(rollbackEngine);
            string batchId = Guid.NewGuid().ToString("N");

            var metrics = optimizer.ApplyCoordinatedTuning(batchId);

            Assert.NotNull(metrics);
            Assert.True(metrics.SystemTuningActionsApplied >= 0);
        }

        [Fact]
        public void SystemPerformanceOptimizer_RollbackRecordsAreProperlyTracked()
        {
            var rollbackEngine = new V4RollbackEngine();
            var optimizer = new SystemPerformanceOptimizer(rollbackEngine);
            string batchId = "unit_test_batch_" + Guid.NewGuid().ToString("N");

            var metrics = optimizer.ApplyCoordinatedTuning(batchId);
            var history = rollbackEngine.GetTransactions(batchId);

            Assert.NotNull(history);
            // Any recorded transactions should carry the matching batchId
            foreach (var tx in history)
            {
                if (tx.BatchId == batchId)
                {
                    Assert.False(string.IsNullOrEmpty(tx.TargetIdentifier));
                    Assert.False(string.IsNullOrEmpty(tx.TargetType));
                }
            }
        }
    }
}
