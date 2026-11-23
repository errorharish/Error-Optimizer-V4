#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Safety;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class WatchdogTests
    {
        [Fact]
        public async Task T_WD_1_Watchdog_TriggersOnSimulatedCriticalProcessLoss()
        {
            var blacklist = new BugcheckBlacklist(Path.Combine(Path.GetTempPath(), $"eos_bc_{Guid.NewGuid():N}.json"));
            bool rollbackInvoked = false;

            using var watchdog = new LiveWatchdog(blacklist, () => rollbackInvoked = true);
            // Simulate that csrss is missing
            watchdog.ProcessPresenceChecker = proc => proc != "csrss";

            watchdog.Start();

            // Wait up to 1 second for watchdog 250ms cadence loop
            for (int i = 0; i < 10 && !watchdog.IsAborted; i++)
            {
                await Task.Delay(100);
            }

            Assert.True(watchdog.IsAborted);
            Assert.Contains("csrss", watchdog.AbortReason);
            Assert.True(rollbackInvoked);
        }

        [Fact]
        public async Task T_WD_2_Watchdog_TriggersRollbackCallback()
        {
            var blacklist = new BugcheckBlacklist(Path.Combine(Path.GetTempPath(), $"eos_bc_{Guid.NewGuid():N}.json"));
            int rollbackCount = 0;

            using var watchdog = new LiveWatchdog(blacklist, () => Interlocked.Increment(ref rollbackCount));
            // Simulate that dwm is missing
            watchdog.ProcessPresenceChecker = proc => proc != "dwm";

            watchdog.Start();

            for (int i = 0; i < 10 && !watchdog.IsAborted; i++)
            {
                await Task.Delay(100);
            }

            Assert.True(watchdog.IsAborted);
            Assert.Equal(1, rollbackCount);
        }

        [Fact]
        public async Task T_WD_3_Watchdog_WritesAbortDumpAndBlacklistsLastActions()
        {
            string tempBl = Path.Combine(Path.GetTempPath(), $"eos_bc_{Guid.NewGuid():N}.json");
            var blacklist = new BugcheckBlacklist(tempBl);

            using var watchdog = new LiveWatchdog(blacklist, () => { });
            watchdog.RecordAction("Process:suspect_tool.exe");
            watchdog.RecordAction("Service:suspect_driver_service");

            // Simulate explorer failure
            watchdog.ProcessPresenceChecker = proc => proc != "explorer";
            watchdog.Start();

            for (int i = 0; i < 10 && !watchdog.IsAborted; i++)
            {
                await Task.Delay(100);
            }

            Assert.True(watchdog.IsAborted);

            // Both recorded actions must now be in the bugcheck blacklist
            Assert.True(blacklist.IsBlacklisted("Process:suspect_tool.exe") || blacklist.IsBlacklisted("suspect_tool.exe"));
            Assert.True(blacklist.IsBlacklisted("Service:suspect_driver_service") || blacklist.IsBlacklisted("suspect_driver_service"));

            // Check that abort dump was created
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localAppData, "ErrorOptimizer");
            if (Directory.Exists(dir))
            {
                var abortFiles = Directory.GetFiles(dir, "abort_dump_*.json");
                Assert.NotEmpty(abortFiles);
            }
        }
    }
}
