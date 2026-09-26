using System;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Safety.Tests.V4
{
    public class V4ProtectedProcessPolicyTests
    {
        private readonly ProtectedProcessPolicy _policy = new();

        [Theory]
        [InlineData("csrss")]
        [InlineData("wininit")]
        [InlineData("services")]
        [InlineData("lsass")]
        [InlineData("smss")]
        [InlineData("System")]
        [InlineData("Registry")]
        [InlineData("Memory Compression")]
        [InlineData("fontdrvhost")]
        [InlineData("dwm")]
        [InlineData("sihost")]
        [InlineData("explorer")]
        public void KernelAndCoreSubsystems_AreStrictlyProtected(string processName)
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 100,
                ProcessName = processName
            };

            bool isProtected = _policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false);
            Assert.True(isProtected, $"Core OS process '{processName}' must be strictly protected.");
        }

        [Theory]
        [InlineData("WinDefend")]
        [InlineData("MsMpEng")]
        [InlineData("SecurityHealthService")]
        [InlineData("Sense")]
        [InlineData("mpssvc")]
        [InlineData("CryptSvc")]
        [InlineData("RpcSs")]
        public void SecurityAndCoreServices_AreProtected(string serviceProcessName)
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 200,
                ProcessName = serviceProcessName
            };

            bool isProtected = _policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false);
            Assert.True(isProtected, $"Security/Infrastructure '{serviceProcessName}' must be protected.");
        }

        [Theory]
        [InlineData("nvcontainer")]
        [InlineData("NVDisplay.Container")]
        [InlineData("amdfendrsr")]
        [InlineData("igfxEM")]
        [InlineData("AudioSrv")]
        [InlineData("AudioEndpointBuilder")]
        [InlineData("RAVCbg64")]
        [InlineData("lghub_agent")]
        [InlineData("iCUE")]
        [InlineData("Razer Synapse Service")]
        public void HardwareDriversAndPeripherals_AreProtected(string driverName)
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 300,
                ProcessName = driverName
            };

            bool isProtected = _policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false);
            Assert.True(isProtected, $"Hardware driver/peripheral '{driverName}' must be protected.");
        }

        [Theory]
        [InlineData("EasyAntiCheat")]
        [InlineData("BEService")]
        [InlineData("vgc")]
        [InlineData("FACEITService")]
        [InlineData("EAAntiCheat.GameService")]
        public void AntiCheatEngines_AreProtected(string antiCheatName)
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 400,
                ProcessName = antiCheatName
            };

            bool isProtected = _policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false);
            Assert.True(isProtected, $"Anti-Cheat engine '{antiCheatName}' must be protected.");
        }

        [Fact]
        public void GamingPlatforms_AreProtected_WhenGamingWorkloadIsActive()
        {
            var steam = new ProcessIdentityDto { ProcessId = 501, ProcessName = "steam" };
            var epic = new ProcessIdentityDto { ProcessId = 502, ProcessName = "EpicGamesLauncher" };

            // When gaming is active -> Protected
            Assert.True(_policy.IsProcessProtected(steam, isGamingActive: true, isEditingActive: false));
            Assert.True(_policy.IsProcessProtected(epic, isGamingActive: true, isEditingActive: false));
        }

        [Fact]
        public void CreativeSuites_AreProtected_WhenEditingWorkloadIsActive()
        {
            var premiere = new ProcessIdentityDto { ProcessId = 601, ProcessName = "Adobe Premiere Pro" };
            var resolve = new ProcessIdentityDto { ProcessId = 602, ProcessName = "Resolve" };
            var blender = new ProcessIdentityDto { ProcessId = 603, ProcessName = "blender" };

            // When editing is active -> Protected
            Assert.True(_policy.IsProcessProtected(premiere, isGamingActive: false, isEditingActive: true));
            Assert.True(_policy.IsProcessProtected(resolve, isGamingActive: false, isEditingActive: true));
            Assert.True(_policy.IsProcessProtected(blender, isGamingActive: false, isEditingActive: true));
        }

        [Fact]
        public void SelfProcesses_AreAlwaysProtected()
        {
            var optimizerGui = new ProcessIdentityDto { ProcessId = 701, ProcessName = "ErrorOptimizer" };
            var optimizerSvc = new ProcessIdentityDto { ProcessId = 702, ProcessName = "ErrorOptimizerService" };

            Assert.True(_policy.IsProcessProtected(optimizerGui, false, false));
            Assert.True(_policy.IsProcessProtected(optimizerSvc, false, false));
        }
    }
}
