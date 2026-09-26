using System;
using BiosOptimizer.Core.V4.Rollback;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4RollbackTests
    {
        [Fact]
        public void RecordAndQueryTransactions_WorksSuccessfully()
        {
            var rollback = new V4RollbackEngine();
            string batchId = $"TEST-BATCH-{Guid.NewGuid():N}";

            var record = new V4TransactionRecord
            {
                BatchId = batchId,
                TargetType = "Service",
                TargetIdentifier = "TestDiagTrack",
                PreviousState = "Automatic",
                NewState = "Disabled",
                Reason = "Test telemetry service disabling"
            };

            rollback.RecordTransaction(record);

            var transactions = rollback.GetTransactions(batchId);
            Assert.NotEmpty(transactions);
            Assert.Contains(transactions, t => t.TargetIdentifier == "TestDiagTrack");
        }
    }
}
