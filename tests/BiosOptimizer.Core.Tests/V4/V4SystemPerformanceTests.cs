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

            var writtenKeys = new System.Collections.Generic.List<string>();
            SystemPerformanceOptimizer.RegistryWriteHook = (subKey, name, val, kind) =>
            {
                writtenKeys.Add($"{subKey}\\{name}");
            };
            SystemPerformanceOptimizer.PowerCfgRunnerHook = args =>
            {
                if (args.Contains("/getactivescheme"))
                    return "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)";
                return string.Empty;
            };
            SystemPerformanceOptimizer.StandbyListPurgeHook = () => 128;

            try
            {
                var metrics = optimizer.ApplyCoordinatedTuning(batchId);

                Assert.NotNull(metrics);
                Assert.True(metrics.SystemTuningActionsApplied >= 4);
                Assert.True(metrics.MmcssPriorityBoostEnabled);
                Assert.True(metrics.Win32PrioritySeparationConfigured);
                Assert.True(metrics.NetworkThrottlingIndexDisabled);
                Assert.True(metrics.HagsConfigured);
                Assert.True(metrics.HighPerformancePowerPlanActive);
                Assert.Equal(128.0, metrics.StandbyListPurgedMb);

                // Verify that the underlying real registry was NOT called and writes went through our double
                Assert.NotEmpty(writtenKeys);
                Assert.Contains(writtenKeys, k => k.Contains("SystemResponsiveness"));
                Assert.Contains(writtenKeys, k => k.Contains("Win32PrioritySeparation"));
                Assert.Contains(writtenKeys, k => k.Contains("NetworkThrottlingIndex"));
                Assert.Contains(writtenKeys, k => k.Contains("HwSchMode"));
            }
            finally
            {
                SystemPerformanceOptimizer.ResetTestHooks();
            }
        }

        [Fact]
        public void ApplyCoordinatedTuning_UnmockedHostMutation_ThrowsInvalidOperationException()
        {
            SystemPerformanceOptimizer.ResetTestHooks();
            var optimizer = new SystemPerformanceOptimizer();
            string batchId = Guid.NewGuid().ToString("N");

            Assert.Throws<InvalidOperationException>(() =>
            {
                optimizer.ApplyCoordinatedTuning(batchId);
            });
        }

        [Fact]
        public void SystemPerformanceOptimizer_RollbackRecordsAreProperlyTracked()
        {
            var rollbackEngine = new V4RollbackEngine();
            var optimizer = new SystemPerformanceOptimizer(rollbackEngine);
            string batchId = "unit_test_batch_" + Guid.NewGuid().ToString("N");

            SystemPerformanceOptimizer.RegistryWriteHook = (subKey, name, val, kind) => { };
            SystemPerformanceOptimizer.PowerCfgRunnerHook = args =>
            {
                if (args.Contains("/getactivescheme"))
                    return "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)";
                return string.Empty;
            };

            try
            {
                var metrics = optimizer.ApplyCoordinatedTuning(batchId);
                var history = rollbackEngine.GetTransactions(batchId);

                Assert.NotNull(history);
                Assert.NotEmpty(history);
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
            finally
            {
                SystemPerformanceOptimizer.ResetTestHooks();
            }
        }

        [Fact]
        public void T_PWR_1_SetActive_RecordsPreviousPlanBeforeSwitching()
        {
            var rollbackEngine = new V4RollbackEngine();
            var optimizer = new SystemPerformanceOptimizer(rollbackEngine);
            string batchId = "test_batch_pwr_" + Guid.NewGuid().ToString("N");

            var calls = new System.Collections.Generic.List<string>();
            const string initialGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

            SystemPerformanceOptimizer.RegistryWriteHook = (subKey, name, val, kind) => { };
            SystemPerformanceOptimizer.PowerCfgRunnerHook = args =>
            {
                calls.Add(args);
                if (args.Contains("/getactivescheme"))
                {
                    return $"Power Scheme GUID: {initialGuid}  (Balanced)";
                }
                return string.Empty;
            };

            try
            {
                var metrics = optimizer.ApplyCoordinatedTuning(batchId);

                // Verify /getactivescheme was called before /setactive
                Assert.True(calls.Count >= 2);
                Assert.Contains(calls[0], "/getactivescheme");
                Assert.Contains(calls[1], "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

                // Verify transaction recorded with initialGuid
                var txs = rollbackEngine.GetTransactions(batchId);
                var pwrTx = txs.Find(t => t.TargetType == "PowerPlan");
                Assert.NotNull(pwrTx);
                Assert.Equal(initialGuid, pwrTx.PreviousState);
                Assert.Equal("Balanced", pwrTx.TargetIdentifier);
            }
            finally
            {
                SystemPerformanceOptimizer.ResetTestHooks();
            }
        }

        [Fact]
        public void T_PWR_2_Rollback_RestoresPreviousPlan()
        {
            var rollbackEngine = new V4RollbackEngine();
            string batchId = "test_batch_pwr_rb_" + Guid.NewGuid().ToString("N");
            const string initialGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

            rollbackEngine.Register(batchId, new PowerPlanRollback(initialGuid, "Balanced"));

            var executedCommands = new System.Collections.Generic.List<string>();
            V4RollbackEngine.PowerCfgRunnerHook = args =>
            {
                executedCommands.Add(args);
                return (0, "Successfully set active scheme");
            };

            try
            {
                bool success = rollbackEngine.RollbackBatch(batchId, out int restored, out int failed, out string summary);

                Assert.True(success);
                Assert.Equal(1, restored);
                Assert.Equal(0, failed);
                Assert.Single(executedCommands);
                Assert.Equal($"/setactive {initialGuid}", executedCommands[0]);
            }
            finally
            {
                V4RollbackEngine.PowerCfgRunnerHook = null;
            }
        }

        [Fact]
        public void T_PWR_3_Rollback_WithPowerCfgFailure_LogsAndDoesNotThrow()
        {
            var rollbackEngine = new V4RollbackEngine();
            string batchId = "test_batch_pwr_fail_" + Guid.NewGuid().ToString("N");
            const string initialGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

            rollbackEngine.Register(batchId, new PowerPlanRollback(initialGuid, "Balanced"));

            var loggedMessages = new System.Collections.Generic.List<string>();
            V4RollbackEngine.LogAction = msg => loggedMessages.Add(msg);
            V4RollbackEngine.PowerCfgRunnerHook = args => (-1, "Access is denied");

            try
            {
                // Must not throw
                var exception = Record.Exception(() =>
                {
                    bool success = rollbackEngine.RollbackBatch(batchId, out int restored, out int failed, out string summary);
                    Assert.False(success);
                    Assert.Equal(0, restored);
                    Assert.Equal(1, failed);
                });

                Assert.Null(exception);

                // Verify failure logged with previous GUID so user can restore manually
                Assert.NotEmpty(loggedMessages);
                Assert.Contains(loggedMessages, m => m.Contains(initialGuid) && m.Contains("failed during rollback"));
            }
            finally
            {
                V4RollbackEngine.PowerCfgRunnerHook = null;
                V4RollbackEngine.LogAction = null;
            }
        }
    }
}
