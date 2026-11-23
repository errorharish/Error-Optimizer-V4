#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class BugcheckBlacklistTests
    {
        [Fact]
        public void T_BC_1_BlacklistPersistsAndReloadsFromDisk()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"eos_bc_{Guid.NewGuid():N}.json");

            try
            {
                var blacklist1 = new BugcheckBlacklist(tempPath);
                blacklist1.Add("FatalService", "0x0000007E System Thread Exception");
                blacklist1.Add("BadProcess.exe", "0x000000EF Critical Process Died");

                Assert.True(File.Exists(tempPath));

                // Load second instance pointing to same file
                var blacklist2 = new BugcheckBlacklist(tempPath);
                Assert.True(blacklist2.IsBlacklisted("FatalService"));
                Assert.True(blacklist2.IsBlacklisted("BadProcess.exe"));
                Assert.False(blacklist2.IsBlacklisted("SafeProcess.exe"));
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        [Fact]
        public void T_BC_2_BlacklistedAction_RejectedBySafetyKernel()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"eos_bc_{Guid.NewGuid():N}.json");

            try
            {
                // Register into global BugcheckBlacklist singleton
                BugcheckBlacklist.Instance.Add("LethalCrashProcess.exe", "0x000000EF");

                var proc = new ProcessIdentityDto
                {
                    ProcessId = 9999,
                    ProcessName = "LethalCrashProcess.exe",
                    DisplayName = "Crash Inducer"
                };

                var kernel = SafetyKernel.Instance;
                var verdict = kernel.EvaluateProcessKill(
                    proc,
                    isGamingActive: false,
                    isEditingActive: false,
                    out string ruleCode,
                    out string reason);

                Assert.Equal(SafetyVerdict.Deny, verdict);
                Assert.True(ruleCode == "R10" || ruleCode == "BL_BUGCHECK_BLACKLIST");
                Assert.Contains("blacklist", reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        [Fact]
        public void T_BC_3_CrashDetector_CorrelatesEvent1001WithLastSessionAction()
        {
            string tempBl = Path.Combine(Path.GetTempPath(), $"eos_bc_{Guid.NewGuid():N}.json");
            string tempSession = Path.Combine(Path.GetTempPath(), $"eos_session_{Guid.NewGuid():N}.json");

            try
            {
                var blacklist = new BugcheckBlacklist(tempBl);
                var detector = new CrashDetector(blacklist, tempSession);

                var session = new LastSessionLog
                {
                    SessionId = "BATCH-TEST-001",
                    LastActionTimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
                    Actions = new List<LastSessionAction>
                    {
                        new()
                        {
                            ActionSignature = "Process:faulty_driver_agent.exe",
                            Description = "Terminated agent",
                            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-30)
                        }
                    }
                };

                detector.SaveLastSession(session);

                // Mock a bugcheck occurring 15s after that action (well within 120s window)
                var mockEvents = new List<(string Code, DateTimeOffset Timestamp)>
                {
                    ("0x000000EF", DateTimeOffset.UtcNow.AddSeconds(-15))
                };

                int blacklistedCount = detector.CheckForRecentBugchecks(mockEvents);

                Assert.Equal(1, blacklistedCount);
                Assert.True(blacklist.IsBlacklisted("Process:faulty_driver_agent.exe") ||
                            blacklist.IsBlacklisted("faulty_driver_agent.exe"));
            }
            finally
            {
                try { if (File.Exists(tempBl)) File.Delete(tempBl); } catch { }
                try { if (File.Exists(tempSession)) File.Delete(tempSession); } catch { }
            }
        }
    }
}
