using System;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4ProcessIntelligenceTests
    {
        [Fact]
        public async Task CaptureSnapshotAsync_ReturnsLiveProcessesWithClassifications()
        {
            var engine = new ProcessIntelligenceEngine();
            var snapshot = await engine.CaptureSnapshotAsync();

            Assert.NotNull(snapshot);
            Assert.True(snapshot.TotalProcessCount > 0, "Snapshot must discover running processes");
            Assert.True(snapshot.ProtectedProcessCount > 0, "Snapshot must detect protected processes");
            Assert.NotEmpty(snapshot.Processes);

            // Verify current process is captured
            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var currentIdentity = snapshot.Processes.Find(p => p.ProcessId == currentPid);
            Assert.NotNull(currentIdentity);
        }

        [Fact]
        public async Task ProcessClassification_RejectsNaiveSubstringMatches()
        {
            var engine = new ProcessIntelligenceEngine();
            var snapshot = await engine.CaptureSnapshotAsync();

            // Any unknown process with "Update" or "Helper" in its name that is NOT an explicit verified reducible
            // must NOT automatically be classified as REDUCIBLE.
            foreach (var proc in snapshot.Processes)
            {
                if (proc.ProcessName.Contains("Helper", StringComparison.OrdinalIgnoreCase) &&
                    proc.Classification == ProcessClassification.REDUCIBLE)
                {
                    // If marked REDUCIBLE, it must have verified rationale and safety confidence >= 0.9
                    Assert.True(proc.SafetyConfidence >= 0.9);
                    Assert.False(string.IsNullOrEmpty(proc.ClassificationReason));
                }
            }
        }

        [Fact]
        public void ProtectedProcessPolicy_ShieldsExistingUserApplications()
        {
            var policy = new ProtectedProcessPolicy();

            string[] userApps = new[]
            {
                "chrome", "msedge", "firefox", "brave", "opera", "discord", "slack", "teams",
                "steam", "obs64", "resolve", "blender", "photoshop", "bluestacks", "hd-player"
            };

            foreach (var app in userApps)
            {
                var identity = new ProcessIdentityDto
                {
                    ProcessId = 12345,
                    ProcessName = app,
                    HasVisibleWindow = false,
                    IsForeground = false
                };

                bool isProtected = policy.IsProcessProtected(identity, false, false);
                Assert.True(isProtected, $"User app '{app}' must be protected under the Existing-App Rule");
            }
        }

        [Fact]
        public void ProtectedProcessPolicy_IdentifiesStrictlyProtectedServices()
        {
            Assert.True(ProtectedProcessPolicy.IsStrictlyProtectedService("WinDefend"));
            Assert.True(ProtectedProcessPolicy.IsStrictlyProtectedService("RpcSs"));
            Assert.True(ProtectedProcessPolicy.IsStrictlyProtectedService("AudioSrv"));
            Assert.True(ProtectedProcessPolicy.IsStrictlyProtectedService("nvcontainer"));
            Assert.True(ProtectedProcessPolicy.IsStrictlyProtectedService("EasyAntiCheat"));

            Assert.False(ProtectedProcessPolicy.IsStrictlyProtectedService("DiagTrack"));
            Assert.False(ProtectedProcessPolicy.IsStrictlyProtectedService("dmwappushservice"));
            Assert.False(ProtectedProcessPolicy.IsStrictlyProtectedService("MapsBroker"));
            Assert.False(ProtectedProcessPolicy.IsStrictlyProtectedService("LGHUBUpdaterService"));
        }
    }
}
