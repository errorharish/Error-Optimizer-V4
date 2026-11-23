#nullable enable
using System;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Power;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class InvariantTests
    {
        [Fact]
        public void T_INV_1_AssertAll_PassesOnHealthyHost()
        {
            InvariantChecks.InvariantTestHook = null;
            bool ok = InvariantChecks.AssertAll(out string failingCode);
            Assert.True(ok, $"Expected InvariantChecks.AssertAll to pass on host system, but failed with: {failingCode}");
            Assert.Empty(failingCode);
        }

        [Theory]
        [InlineData("I1")]
        [InlineData("I2")]
        [InlineData("I3")]
        [InlineData("I4")]
        [InlineData("I5")]
        [InlineData("I6")]
        [InlineData("I7")]
        [InlineData("I8")]
        [InlineData("I9")]
        [InlineData("I10")]
        public void T_INV_2_IndividualInvariantViolation_TriggersFalseInAssertAll(string invariantCode)
        {
            try
            {
                InvariantChecks.InvariantTestHook = code => code == invariantCode ? false : true;
                bool ok = InvariantChecks.AssertAll(out string failingCode);
                Assert.False(ok);
                Assert.Equal(invariantCode, failingCode);
            }
            finally
            {
                InvariantChecks.InvariantTestHook = null;
            }
        }

        [Fact]
        public async Task T_INV_3_StartupAbortPath_FiresWhenAssertAllFails()
        {
            try
            {
                InvariantChecks.InvariantTestHook = code => code == "I2" ? false : true;

                var executor = new MaximumPerformanceExecutor();
                var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 1);

                Assert.False(result.Success);
                Assert.Equal("ABORTED", result.ResultState);
                Assert.Contains("Safety invariant check failed at preflight: I2", result.StopReason);
            }
            finally
            {
                InvariantChecks.InvariantTestHook = null;
            }
        }

        [Fact]
        public void T_INV_2_LegacyMethodChecks_Preserved()
        {
            // I1: PID 0 and PID 4 must be flagged as non-terminable (CheckI1 returns false)
            Assert.False(InvariantChecks.CheckI1(0));
            Assert.False(InvariantChecks.CheckI1(4));
            Assert.True(InvariantChecks.CheckI1(1234));

            // I2: Driver service stop is rejected
            Assert.False(InvariantChecks.CheckI2(0x00000001)); // Kernel driver
            Assert.True(InvariantChecks.CheckI2(0x00000020));  // Win32 Share Process

            // I3: Boot/System start service stop is rejected
            Assert.False(InvariantChecks.CheckI3(0)); // Boot start
            Assert.False(InvariantChecks.CheckI3(1)); // System start
            Assert.True(InvariantChecks.CheckI3(3));  // Demand start

            // I4: Service that does not accept stop is rejected
            Assert.False(InvariantChecks.CheckI4(0x0)); // Does not accept stop
            Assert.True(InvariantChecks.CheckI4(0x1));  // Accepts stop

            // I10: Process with kernel callback cannot be terminated
            Assert.False(InvariantChecks.CheckI10(hasKernelCallback: true));
            Assert.True(InvariantChecks.CheckI10(hasKernelCallback: false));
        }

        [Fact]
        public void T_INV_3_BatteryOnLaptop_EnforcesPowerRules()
        {
            // When on laptop + battery, high performance aggressive plan is rejected by R14
            var decision = PowerPlanEngine.EvaluatePowerPlanAdjustment(isLaptop: true, isOnBattery: true);
            Assert.False(decision.AllowAggressivePlan);
            Assert.Contains("battery", decision.Reason, StringComparison.OrdinalIgnoreCase);

            // On AC / desktop, aggressive power plan is permitted
            var acDecision = PowerPlanEngine.EvaluatePowerPlanAdjustment(isLaptop: true, isOnBattery: false);
            Assert.True(acDecision.AllowAggressivePlan);

            // I9: System must always have a valid power plan
            Assert.True(InvariantChecks.CheckI9(Guid.NewGuid(), null));
            Assert.False(InvariantChecks.CheckI9(null, null));
        }

        [Fact]
        public void T_INV_4_AffinityAndWorkingSet_InvariantsPreserved()
        {
            // I7: Affinity mask with 0 processors is prohibited
            Assert.False(InvariantChecks.CheckI7(IntPtr.Zero));
            Assert.True(InvariantChecks.CheckI7((IntPtr)0x3));

            // I8: Target working set below minimum is prohibited
            Assert.False(InvariantChecks.CheckI8(10 * 1024)); // 10 KB
            Assert.True(InvariantChecks.CheckI8(500 * 1024)); // 500 KB
        }

        [Fact]
        public void T_REG_1_WriteToNonDriverServiceStart_Allowed()
        {
            MaximumPerformanceExecutor.KernelDriverServiceHook = svc => false;
            try
            {
                // Invariant check I5 directly verifies non-driver
                bool i5 = InvariantChecks.CheckI5(@"SYSTEM\CurrentControlSet\Services\NonDriverSvc\Start", true);
                Assert.True(i5);

                // Driver check
                bool isDriver = MaximumPerformanceExecutor.IsKernelDriverService("NonDriverSvc");
                Assert.False(isDriver);
            }
            finally
            {
                MaximumPerformanceExecutor.KernelDriverServiceHook = null;
            }
        }

        [Fact]
        public void T_REG_2_WriteToKernelDriverServiceStart_BlockedAndLogged()
        {
            MaximumPerformanceExecutor.KernelDriverServiceHook = svc => true;
            try
            {
                bool isDriver = MaximumPerformanceExecutor.IsKernelDriverService("nvlddmkm");
                Assert.True(isDriver);

                bool writeAttempt = MaximumPerformanceExecutor.TrySetServiceStartRegistry(
                    "nvlddmkm",
                    requestedStart: 4,
                    restorePointCreated: true,
                    out string failureReason);

                Assert.False(writeAttempt);
                Assert.Contains("Skipping Start write for driver service", failureReason);
            }
            finally
            {
                MaximumPerformanceExecutor.KernelDriverServiceHook = null;
            }
        }

        [Fact]
        public void T_REG_3_CheckI6Fails_NoRestorePoint_WriteBlocked()
        {
            // When path is under Driver or Device and no restore point was created
            bool i6Driver = InvariantChecks.CheckI6(@"\Driver\Disk", restorePointCreated: false);
            Assert.False(i6Driver);

            bool i6Device = InvariantChecks.CheckI6(@"\Device\HarddiskVolume1", restorePointCreated: false);
            Assert.False(i6Device);

            bool i6WithRestorePoint = InvariantChecks.CheckI6(@"\Driver\Disk", restorePointCreated: true);
            Assert.True(i6WithRestorePoint);

            // Test TrySetServiceStartRegistry with Driver service name and no restore point
            MaximumPerformanceExecutor.KernelDriverServiceHook = svc => false;
            try
            {
                bool writeAttempt = MaximumPerformanceExecutor.TrySetServiceStartRegistry(
                    "Driver",
                    requestedStart: 4,
                    restorePointCreated: false,
                    out string failureReason);

                Assert.False(writeAttempt);
                Assert.Contains("without restore point", failureReason);
            }
            finally
            {
                MaximumPerformanceExecutor.KernelDriverServiceHook = null;
            }
        }

        private class TestEmptyPlanner : MaximumPerformancePlanner
        {
            public override Task<OptimizationPlanDto> GeneratePlanAsync(int passNumber = 1)
            {
                return Task.FromResult(new OptimizationPlanDto
                {
                    PassNumber = passNumber,
                    InitialProcessCount = 100,
                    EstimatedProcessReduction = 0
                });
            }
        }

        [Fact]
        public async Task T_RP_1_RestorePointSuccess_SessionContinues()
        {
            var executor = new MaximumPerformanceExecutor(planner: new TestEmptyPlanner());
            executor.CustomRestorePointHandler = label => new RestorePointHandle
            {
                IsCreated = true,
                Label = label,
                SequenceNumber = 12345
            };

            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 1);

            Assert.True(executor.HasRestorePointCreated);
            Assert.NotEqual("Cannot create restore point — System Protection is off. Enable it or grant the required permissions, then retry. Optimization will not run without a restore point.", result.StopReason);
        }

        [Fact]
        public async Task T_RP_2_RestorePointFails_SessionAborts_NothingApplied()
        {
            var executor = new MaximumPerformanceExecutor(planner: new TestEmptyPlanner());
            executor.CustomRestorePointHandler = label => new RestorePointHandle
            {
                IsCreated = false,
                Label = label,
                ErrorMessage = "System Protection is off"
            };

            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 4);

            Assert.False(executor.HasRestorePointCreated);
            Assert.Equal(OptimizationResultStates.ABORTED, result.ResultState);
            Assert.Equal("Cannot create restore point — System Protection is off. Enable it or grant the required permissions, then retry. Optimization will not run without a restore point.", result.StopReason);
            Assert.False(result.Success);
            Assert.NotEmpty(result.TransactionBatchId);
            Assert.Equal(0, result.ActionsExecutedCount);
            Assert.Equal(0, result.PersistentActionsVerified);
            Assert.Equal(0, result.VerifiedProcessesTerminated);
        }

        [Fact]
        public async Task T_RP_3_AbortPath_LeavesNoPartialPersistentChanges()
        {
            var executor = new MaximumPerformanceExecutor(planner: new TestEmptyPlanner());
            executor.CustomRestorePointHandler = label => null; // Null handle also triggers abort

            var result = await executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 4);

            Assert.False(executor.HasRestorePointCreated);
            Assert.Equal(OptimizationResultStates.ABORTED, result.ResultState);
            Assert.Equal("Cannot create restore point — System Protection is off. Enable it or grant the required permissions, then retry. Optimization will not run without a restore point.", result.StopReason);
            Assert.False(result.Success);
            Assert.Equal(0, result.SystemTuningActionsPlanned);
            Assert.Equal(0, result.SystemTuningActionsExecuted);
            Assert.Equal(0, result.SystemTuningActionsVerified);
            Assert.Equal(0, result.PersistentActionsVerified);
            Assert.Equal(0, result.ActionsAppliedCount);
        }

        [Fact]
        public void T_R03_1_Lsass_Ppl_Denied_R03()
        {
            var kernel = new SafetyKernel();
            int lsassPid = 0;
            try
            {
                var lsassProcs = System.Diagnostics.Process.GetProcessesByName("lsass");
                if (lsassProcs.Length > 0) lsassPid = lsassProcs[0].Id;
            }
            catch { }

            var proc = new ProcessIdentityDto
            {
                ProcessId = lsassPid > 0 ? lsassPid : 9999,
                ProcessName = "lsass",
                DisplayName = "Local Security Authority Process",
                ExecutablePath = @"C:\Windows\System32\lsass.exe"
            };

            var verdict = kernel.EvaluateProcessKill(proc, false, false, out string ruleCode, out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R03", ruleCode);
        }

        [Fact]
        public void T_R03_2_NormalProcess_IsPplProtected_ReturnsFalse()
        {
            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            bool isPpl = SafetyKernel.IsPplProtected(currentPid);

            Assert.False(isPpl);
        }

        [Fact]
        public void T_R05_1_Svchost_HostingKernelDriverService_Denied_R05()
        {
            var kernel = new SafetyKernel();
            SafetyKernel.ServiceTypeQueryHook = svc => svc == "DriverHostService" ? 0x00000001u : 0x00000020u; // SERVICE_KERNEL_DRIVER

            try
            {
                var proc = new ProcessIdentityDto
                {
                    ProcessId = 12345,
                    ProcessName = "svchost",
                    DisplayName = "Host Process for Windows Services",
                    ServiceAssociation = "DriverHostService"
                };

                var verdict = kernel.EvaluateProcessKill(proc, false, false, out string ruleCode, out string reason);

                Assert.Equal(SafetyVerdict.Deny, verdict);
                Assert.Equal("R05", ruleCode);
                Assert.Contains("DriverHostService", reason);
            }
            finally
            {
                SafetyKernel.ServiceTypeQueryHook = null;
            }
        }

        [Fact]
        public void T_R05_2_Svchost_HostingUserModeService_NotBlockedByR05()
        {
            SafetyKernel.ServiceTypeQueryHook = svc => 0x00000020u; // SERVICE_WIN32_SHARE_PROCESS (User Mode)

            try
            {
                var proc = new ProcessIdentityDto
                {
                    ProcessId = 12346,
                    ProcessName = "svchost",
                    DisplayName = "Host Process for Windows Services",
                    ServiceAssociation = "UserModeService"
                };

                bool hostsDriver = SafetyKernel.HostsKernelDriverService(proc, out string driverSvcName);

                Assert.False(hostsDriver);
                Assert.Empty(driverSvcName);
            }
            finally
            {
                SafetyKernel.ServiceTypeQueryHook = null;
            }
        }
    }
}
