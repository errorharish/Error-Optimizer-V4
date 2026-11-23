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

        [Theory]
        [InlineData("Cinema 4D", @"C:\Program Files\Maxon Cinema 4D 2024\Cinema 4D.exe", "Maxon Computer GmbH")]
        [InlineData("Maxon App", @"C:\Program Files\Maxon\Maxon App\Maxon App.exe", "Maxon Computer GmbH")]
        [InlineData("Maxon Install Helper", @"C:\Program Files\Maxon\Tools\Maxon Install Helper.exe", "Maxon Computer GmbH")]
        [InlineData("Redshift", @"C:\ProgramData\Redshift\bin\redshiftBenchmark.exe", "Maxon Computer GmbH")]
        [InlineData("ZBrush", @"C:\Program Files\Maxon ZBrush 2024\ZBrush.exe", "Maxon Computer GmbH")]
        public void Test01_MaxonProtection_ProcessesAndServicesAreProtected(string processName, string exePath, string publisher)
        {
            var safetyKernel = new SafetyKernel(_policy);
            var proc = new ProcessIdentityDto
            {
                ProcessId = 1101,
                ProcessName = processName,
                ExecutablePath = exePath,
                Publisher = publisher,
                IsDigitallySigned = true
            };

            Assert.True(_policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false));
            Assert.True(safetyKernel.IsProtected(proc));
            var verdict = safetyKernel.EvaluateProcessKill(proc, false, false, out string ruleCode, out _);
            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R28_PROTECTED_APP", ruleCode);

            Assert.True(safetyKernel.IsProtectedService("Maxon Install Helper", "Maxon Install Helper", exePath));
        }

        [Theory]
        [InlineData("Red Giant Service")]
        [InlineData("RedGiantService")]
        public void Test02_RedGiantProtection_ServicesAreProtectedAndNeverStopped(string serviceName)
        {
            var safetyKernel = new SafetyKernel(_policy);
            Assert.True(ProtectedProcessPolicy.IsStrictlyProtectedService(serviceName));
            Assert.True(safetyKernel.IsProtectedService(serviceName, serviceName, @"C:\Program Files\Red Giant\Services\Red Giant Service.exe"));

            var verdict = safetyKernel.EvaluateServiceStop(
                serviceName,
                serviceType: 0x10u,
                controlsAccepted: 0x01u,
                startType: 2u,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out _);
            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("S07_PROTECTED_SERVICE", ruleCode);
        }

        [Theory]
        [InlineData("Adobe Premiere Pro", @"C:\Program Files\Adobe\Adobe Premiere Pro 2024\Adobe Premiere Pro.exe")]
        [InlineData("AfterFX", @"C:\Program Files\Adobe\Adobe After Effects 2024\Support Files\AfterFX.exe")]
        [InlineData("Photoshop", @"C:\Program Files\Adobe\Adobe Photoshop 2024\Photoshop.exe")]
        [InlineData("Adobe Media Encoder", @"C:\Program Files\Adobe\Adobe Media Encoder 2024\Adobe Media Encoder.exe")]
        [InlineData("CCXProcess", @"C:\Program Files\Adobe\Adobe Creative Cloud Experience\CCXProcess.exe")]
        [InlineData("CoreSync", @"C:\Program Files (x86)\Adobe\Adobe Sync\CoreSync\CoreSync.exe")]
        [InlineData("AdobeIPCBroker", @"C:\Program Files (x86)\Common Files\Adobe\Adobe Desktop Common\IPCBox\AdobeIPCBroker.exe")]
        public void Test03_AdobeProtection_ProcessesAndServicesAreProtectedEvenWhenIdle(string processName, string exePath)
        {
            var safetyKernel = new SafetyKernel(_policy);
            var proc = new ProcessIdentityDto
            {
                ProcessId = 1301,
                ProcessName = processName,
                ExecutablePath = exePath,
                Publisher = "Adobe Inc.",
                IsDigitallySigned = true
            };

            Assert.True(_policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false));
            Assert.True(safetyKernel.IsProtected(proc));

            Assert.True(safetyKernel.IsProtectedService("AdobeARMservice"));
            Assert.True(safetyKernel.IsProtectedService("AGMService"));
            Assert.True(safetyKernel.IsProtectedService("AGSService"));
            Assert.True(safetyKernel.IsProtectedService("AdobeUpdateService"));
        }

        [Fact]
        public void Test04_TopazProtection_ProcessesAndChildFfmpegWorkersAreProtected()
        {
            var safetyKernel = new SafetyKernel(_policy);
            var topazParent = new ProcessIdentityDto
            {
                ProcessId = 2000,
                ProcessName = "Topaz Video AI",
                ExecutablePath = @"C:\Program Files\Topaz Labs LLC\Topaz Video AI\Topaz Video AI.exe",
                Publisher = "Topaz Labs LLC",
                IsDigitallySigned = true
            };
            var childFfmpeg = new ProcessIdentityDto
            {
                ProcessId = 2001,
                ParentProcessId = 2000,
                ParentProcessName = "Topaz Video AI",
                ProcessName = "ffmpeg",
                ExecutablePath = @"C:\Program Files\Topaz Labs LLC\Topaz Video AI\ffmpeg.exe",
                Publisher = "Topaz Labs LLC",
                IsDigitallySigned = true
            };
            var photoAi = new ProcessIdentityDto
            {
                ProcessId = 2002,
                ProcessName = "Topaz Photo AI",
                ExecutablePath = @"C:\Program Files\Topaz Labs LLC\Topaz Photo AI\Topaz Photo AI.exe"
            };
            var gigapixelAi = new ProcessIdentityDto
            {
                ProcessId = 2003,
                ProcessName = "Topaz Gigapixel AI",
                ExecutablePath = @"C:\Program Files\Topaz Labs LLC\Topaz Gigapixel AI\Topaz Gigapixel AI.exe"
            };

            var processMap = new System.Collections.Generic.Dictionary<int, ProcessIdentityDto>
            {
                [2000] = topazParent,
                [2001] = childFfmpeg
            };

            Assert.True(safetyKernel.IsProtected(topazParent, processMap));
            Assert.True(safetyKernel.IsProtected(childFfmpeg, processMap));
            Assert.True(safetyKernel.IsProtected(photoAi));
            Assert.True(safetyKernel.IsProtected(gigapixelAi));
        }

        [Theory]
        [InlineData("Resolve", @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe")]
        [InlineData("blender", @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe")]
        public void Test05_InstalledEditingApps_AreAlwaysProtected(string processName, string exePath)
        {
            var safetyKernel = new SafetyKernel(_policy);
            var proc = new ProcessIdentityDto
            {
                ProcessId = 2100,
                ProcessName = processName,
                ExecutablePath = exePath
            };

            Assert.True(safetyKernel.IsProtected(proc));
            Assert.True(_policy.IsProcessProtected(proc, isGamingActive: false, isEditingActive: false));
        }

        [Fact]
        public void Test06_UltraViewerProtection_ProcessesAndServicesNeverDisconnected()
        {
            var safetyKernel = new SafetyKernel(_policy);
            var uvDesktop = new ProcessIdentityDto
            {
                ProcessId = 2201,
                ProcessName = "UltraViewer_Desktop",
                ExecutablePath = @"C:\Program Files (x86)\UltraViewer\UltraViewer_Desktop.exe",
                Publisher = "DucFabulous"
            };
            var uvService = new ProcessIdentityDto
            {
                ProcessId = 2202,
                ProcessName = "UltraViewer_Service",
                ExecutablePath = @"C:\Program Files (x86)\UltraViewer\UltraViewer_Service.exe"
            };

            Assert.True(safetyKernel.IsProtected(uvDesktop));
            Assert.True(safetyKernel.IsProtected(uvService));
            Assert.True(safetyKernel.IsProtectedService("UltraViewService"));
            Assert.True(safetyKernel.IsProtectedService("UltraViewer_Service"));
        }

        [Theory]
        [InlineData("Spotify")]
        [InlineData("SpotifyWebHelper")]
        [InlineData("SpotifyStartupTask")]
        [InlineData("SpotifyMigrator")]
        public void Test07_SpotifyProtection_PlaybackNeverInterrupted(string processName)
        {
            var safetyKernel = new SafetyKernel(_policy);
            var proc = new ProcessIdentityDto
            {
                ProcessId = 2301,
                ProcessName = processName,
                ExecutablePath = @"C:\Users\User\AppData\Roaming\Spotify\" + processName + ".exe",
                Publisher = "Spotify AB"
            };

            Assert.True(safetyKernel.IsProtected(proc));
        }

        [Fact]
        public void Test08_UrbanVpnProtection_ProcessesAndServicesNeverDropped()
        {
            var safetyKernel = new SafetyKernel(_policy);
            var urbanApp = new ProcessIdentityDto
            {
                ProcessId = 2401,
                ProcessName = "urban-vpn",
                ExecutablePath = @"C:\Program Files\UrbanVPN\urban-vpn.exe"
            };
            var urbanSvcProc = new ProcessIdentityDto
            {
                ProcessId = 2402,
                ProcessName = "urban-vpn-service",
                ExecutablePath = @"C:\Program Files\UrbanVPN\bin\urban-vpn-service.exe"
            };
            var openVpnHelper = new ProcessIdentityDto
            {
                ProcessId = 2403,
                ParentProcessId = 2402,
                ParentProcessName = "urban-vpn-service",
                ProcessName = "openvpn",
                ExecutablePath = @"C:\Program Files\UrbanVPN\bin\openvpn.exe"
            };

            Assert.True(safetyKernel.IsProtected(urbanApp));
            Assert.True(safetyKernel.IsProtected(urbanSvcProc));
            Assert.True(safetyKernel.IsProtected(openVpnHelper));
            Assert.True(safetyKernel.IsProtectedService("UrbanVPN-Service"));
            Assert.True(safetyKernel.IsProtectedService("urban-vpn-service"));
        }

        [Theory]
        [InlineData("steam")]
        [InlineData("steamservice")]
        [InlineData("steamwebhelper")]
        [InlineData("GameOverlayUI")]
        public void Test09_SteamProtection_ProcessesAndServicesAlwaysProtected(string processName)
        {
            var safetyKernel = new SafetyKernel(_policy);
            var proc = new ProcessIdentityDto
            {
                ProcessId = 2501,
                ProcessName = processName,
                ExecutablePath = @"C:\Program Files (x86)\Steam\" + processName + ".exe",
                Publisher = "Valve Corp."
            };

            Assert.True(safetyKernel.IsProtected(proc));
            Assert.True(safetyKernel.IsProtectedService("Steam Client Service"));
        }

        [Theory]
        [InlineData("LenovoVantage")]
        [InlineData("LenovoVantageService")]
        [InlineData("Lenovo.Modern.ImController")]
        [InlineData("Lenovo.Vantage.AddinHost")]
        public void Test10_LenovoVantageProtection_ProcessesAndServicesProtected(string processName)
        {
            var safetyKernel = new SafetyKernel(_policy);
            var proc = new ProcessIdentityDto
            {
                ProcessId = 2601,
                ProcessName = processName,
                ExecutablePath = @"C:\Program Files (x86)\Lenovo\VantageService\4.0\" + processName + ".exe",
                Publisher = "Lenovo"
            };

            Assert.True(safetyKernel.IsProtected(proc));
            Assert.True(safetyKernel.IsProtectedService("LenovoVantageService"));
            Assert.True(safetyKernel.IsProtectedService("ImControllerService"));
        }

        [Theory]
        [InlineData(@"\Adobe Acrobat Update Task", "AdobeUpdater", @"C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\AdobeARM.exe")]
        [InlineData(@"\Maxon\Maxon App Updater", "MaxonUpdater", @"C:\Program Files\Maxon\Maxon App\Maxon App.exe")]
        [InlineData(@"\Lenovo\ImController\Lenovo iM Controller Monitor", "LenovoMonitor", @"C:\Windows\System32\Lenovo.Modern.ImController.exe")]
        [InlineData(@"\Steam\SteamClientTask", "SteamClientTask", @"C:\Program Files (x86)\Steam\steam.exe")]
        public void Test11_ScheduledTaskProtection_ProtectedVendorTasksAreNeverDisabled(string taskPath, string taskName, string authorOrCommand)
        {
            var safetyKernel = new SafetyKernel(_policy);
            Assert.True(safetyKernel.IsProtectedScheduledTask(taskPath, taskName, authorOrCommand));
        }

        [Theory]
        [InlineData("Spotify", @"C:\Users\User\AppData\Roaming\Spotify\SpotifyStartupTask.exe", "Spotify AB")]
        [InlineData("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", "Valve Corp.")]
        [InlineData("AdobeAAMUpdater-1.0", @"C:\Program Files (x86)\Common Files\Adobe\OOBE\PDApp\UWA\UpdaterStartupUtility.exe", "Adobe Inc.")]
        [InlineData("UltraViewer", @"C:\Program Files (x86)\UltraViewer\UltraViewer_Desktop.exe", "UltraViewer")]
        public void Test12_StartupEntryProtection_ProtectedVendorStartupsAreNeverDisabled(string name, string commandPath, string publisher)
        {
            var safetyKernel = new SafetyKernel(_policy);
            Assert.True(safetyKernel.IsProtectedStartupEntry(name, commandPath, publisher));
        }

        [Theory]
        [InlineData("SpotifyAB.SpotifyMusic", "Spotify", "CN=Spotify AB")]
        [InlineData("E046963F.LenovoCompanion", "Lenovo Vantage", "CN=Lenovo")]
        [InlineData("E0469640.LenovoUtility", "Lenovo Hotkeys", "CN=Lenovo")]
        [InlineData("AdobeSystemsIncorporated.AdobeCreativeCloud", "Adobe Creative Cloud", "CN=Adobe Inc.")]
        public void Test13_AppXPackageProtection_ProtectedStorePackagesAreNeverRestricted(string packageFamily, string displayName, string publisher)
        {
            var safetyKernel = new SafetyKernel(_policy);
            Assert.True(safetyKernel.IsProtectedAppXPackage(packageFamily, displayName, publisher));
        }

        [Fact]
        public void Test14_ProcessTreeProtection_GrandchildRenderWorkerInheritsProtection()
        {
            var c4d = new ProcessIdentityDto
            {
                ProcessId = 3000,
                ProcessName = "Cinema 4D",
                ExecutablePath = @"C:\Program Files\Maxon Cinema 4D 2024\Cinema 4D.exe",
                Classification = ProcessClassification.PROTECTED
            };
            var commandLineRender = new ProcessIdentityDto
            {
                ProcessId = 3001,
                ParentProcessId = 3000,
                ParentProcessName = "Cinema 4D",
                ProcessName = "Commandline",
                ExecutablePath = @"C:\Program Files\Maxon Cinema 4D 2024\Commandline.exe",
                Classification = ProcessClassification.BACKGROUND
            };
            var customWorker = new ProcessIdentityDto
            {
                ProcessId = 3002,
                ParentProcessId = 3001,
                ParentProcessName = "Commandline",
                ProcessName = "custom_shader_compiler",
                ExecutablePath = @"C:\RenderCache\bin\custom_shader_compiler.exe",
                Classification = ProcessClassification.REDUCIBLE
            };

            var list = new System.Collections.Generic.List<ProcessIdentityDto> { c4d, commandLineRender, customWorker };
            BiosOptimizer.Core.V4.Intelligence.ProcessIntelligenceEngine.PropagateProtectedProcessTree(list);

            Assert.Equal(ProcessClassification.PROTECTED, commandLineRender.Classification);
            Assert.Equal(ProcessClassification.PROTECTED, customWorker.Classification);
        }

        [Fact]
        public void Test15_FalsePositiveRejection_GenericNamesAndUntrustedTempSpoofsAreNotProtected()
        {
            var safetyKernel = new SafetyKernel(_policy);

            // 1. Generic names like helper.exe, service.exe, media_tool.exe must NOT be protected
            var genericHelper = new ProcessIdentityDto
            {
                ProcessId = 4001,
                ProcessName = "helper",
                ExecutablePath = @"C:\SomeRandomApp\helper.exe",
                Publisher = "Unknown Corp"
            };
            var genericMedia = new ProcessIdentityDto
            {
                ProcessId = 4002,
                ProcessName = "media_converter",
                ExecutablePath = @"C:\Tools\media_converter.exe",
                Publisher = "Unknown Corp"
            };
            Assert.False(safetyKernel.IsProtected(genericHelper));
            Assert.False(safetyKernel.IsProtected(genericMedia));

            // 2. Unsigned spoofed binary in Temp folder pretending to be Spotify.exe must NOT be protected
            var spoofedTempSpotify = new ProcessIdentityDto
            {
                ProcessId = 4003,
                ProcessName = "Spotify",
                ExecutablePath = @"C:\Users\User\AppData\Local\Temp\malware\Spotify.exe",
                Publisher = "Untrusted Unknown",
                IsDigitallySigned = false
            };
            Assert.False(safetyKernel.IsProtected(spoofedTempSpotify));

            // 3. Unrelated safe reducible service (e.g., RetailDemo) must NOT be marked protected
            Assert.False(safetyKernel.IsProtectedService("RetailDemo", "Retail Demo Service", @"C:\Windows\System32\svchost.exe"));
        }
    }
}
