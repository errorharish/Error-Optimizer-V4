#nullable enable
using System;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Verification;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class HardProtectTests
    {
        private readonly SafetyKernel _kernel = SafetyKernel.Instance;

        [Fact]
        public void T_HP_CONN_1_WlanSvc_NeverStopped()
        {
            var verdict = _kernel.EvaluateServiceStop(
                "wlansvc",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Contains("CONN", ruleCode);
            Assert.True(HardProtectRegistry.IsServiceProtected("wlansvc"));
        }

        [Fact]
        public void T_HP_CONN_2_BthServ_NeverStopped()
        {
            var verdict = _kernel.EvaluateServiceStop(
                "bthserv",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Contains("CONN", ruleCode);
            Assert.True(HardProtectRegistry.IsServiceProtected("bthserv"));
        }

        [Fact]
        public void T_HP_CONN_3_NetworkInterfaces_RemainUpAfterOptimization()
        {
            var verifier = new OptimizationVerificationEngine();
            bool connectivityOk = verifier.VerifyConnectivity(out string status);
            Assert.True(connectivityOk, $"Network interfaces should remain UP: {status}");
            Assert.False(string.IsNullOrEmpty(status));
        }

        [Fact]
        public void T_HP_AUD_1_Audiosrv_NeverStopped()
        {
            var verdict = _kernel.EvaluateServiceStop(
                "Audiosrv",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Contains("AUD", ruleCode);
            Assert.True(HardProtectRegistry.IsServiceProtected("Audiosrv"));
        }

        [Fact]
        public void T_HP_AUD_2_AudioEndpointBuilder_NeverStopped()
        {
            var verdict = _kernel.EvaluateServiceStop(
                "AudioEndpointBuilder",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Contains("AUD", ruleCode);
            Assert.True(HardProtectRegistry.IsServiceProtected("AudioEndpointBuilder"));
        }

        [Fact]
        public void T_HP_GPU_1_NvContainer_AmdFendrs_NeverStopped()
        {
            var nvVerdict = _kernel.EvaluateServiceStop(
                "NvContainerLocalSystem",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string nvCode,
                out _);

            Assert.Equal(SafetyVerdict.Deny, nvVerdict);
            Assert.Contains("GPU", nvCode);

            var amdVerdict = _kernel.EvaluateServiceStop(
                "AmdCrashDefenderService",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string amdCode,
                out _);

            Assert.Equal(SafetyVerdict.Deny, amdVerdict);
            Assert.Contains("GPU", amdCode);
        }

        [Fact]
        public void T_HP_STO_1_StorSvc_NeverStopped()
        {
            var verdict = _kernel.EvaluateServiceStop(
                "StorSvc",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out _);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Contains("STO", ruleCode);
            Assert.True(HardProtectRegistry.IsServiceProtected("StorSvc"));
        }

        [Fact]
        public void T_HP_INP_1_TextInputHost_NeverKilled()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 12345,
                ProcessName = "TextInputHost.exe",
                DisplayName = "Windows Text Input Application",
                Publisher = "Microsoft Corporation"
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.True(ruleCode == "R03" || ruleCode.Contains("INP"));
            Assert.True(HardProtectRegistry.IsProcessProtected("TextInputHost.exe"));
        }

        [Fact]
        public void T_HP_SEC_1_WinDefend_NeverKilled()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 2222,
                ProcessName = "MsMpEng.exe",
                DisplayName = "Antimalware Service Executable",
                Publisher = "Microsoft Windows"
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out _);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.True(ruleCode == "R04" || ruleCode.Contains("SEC"));
            Assert.True(HardProtectRegistry.IsProcessProtected("MsMpEng.exe"));
        }

        [Fact]
        public void T_HP_PWR_1_OemPowerServices_NeverStoppedOnLaptops()
        {
            var armoryVerdict = _kernel.EvaluateServiceStop(
                "ArmouryCrateControlInterface",
                serviceType: 0x20,
                controlsAccepted: 0x1,
                startType: 0x2,
                hasRunningDependents: false,
                isGamingActive: false,
                isEditingActive: false,
                out string code,
                out _);

            Assert.Equal(SafetyVerdict.Deny, armoryVerdict);
            Assert.Contains("PWR", code);
            Assert.True(HardProtectRegistry.IsServiceProtected("ArmouryCrateControlInterface"));
        }

        [Fact]
        public void T_HP_R26_1_Svchost_Hosting_WlanSvc_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 5001,
                ProcessName = "svchost.exe",
                DisplayName = "Host Process for Windows Services",
                ServiceAssociation = "WlanSvc,OtherSvc"
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R26", ruleCode);
            Assert.Contains("WlanSvc", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R26_2_Svchost_Hosting_BthServ_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 5002,
                ProcessName = "svchost.exe",
                DisplayName = "Host Process for Windows Services",
                ServiceAssociation = "bthserv"
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R26", ruleCode);
            Assert.Contains("bthserv", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R26_3_Svchost_Hosting_Audiosrv_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 5003,
                ProcessName = "svchost.exe",
                DisplayName = "Host Process for Windows Services",
                ServiceAssociation = "Audiosrv,AudioEndpointBuilder"
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R26", ruleCode);
            Assert.Contains("Audiosrv", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R26_4_Svchost_Hosting_NonProtectedService_NotDeniedByR26()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 5004,
                ProcessName = "svchost.exe",
                DisplayName = "Host Process for Windows Services",
                ServiceAssociation = "DiagTrack"
            };

            _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out _);

            Assert.NotEqual("R26", ruleCode);
        }

        [Fact]
        public void T_HP_R26_5_LiveSvchost_ProtectedServices_AlwaysProtected()
        {
            var protectedServices = HardProtectRegistry.LoadProtectedServiceNames();
            Assert.Contains("wlansvc", protectedServices);
            Assert.Contains("bthserv", protectedServices);
            Assert.Contains("audiosrv", protectedServices);
            Assert.Contains("audioendpointbuilder", protectedServices);
            Assert.Contains("dnscache", protectedServices);
            Assert.Contains("dhcp", protectedServices);
            Assert.Contains("storsvc", protectedServices);
        }

        [Fact]
        public void T_HP_R27_1_Process_Loading_Netwtw06Sys_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 6001,
                ProcessName = "ZeroConfigService.exe",
                DisplayName = "Intel WiFi ZeroConfig",
                LoadedModules = new List<string> { @"C:\Windows\System32\netwtw06.sys", @"C:\Windows\System32\kernel32.dll" }
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R27", ruleCode);
            Assert.Contains("netwtw06.sys", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R27_2_Process_Loading_RtwlaneSys_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 6002,
                ProcessName = "RtlDHCP.exe",
                DisplayName = "Realtek Wireless Service",
                LoadedModules = new List<string> { @"C:\Windows\System32\drivers\rtwlane.sys" }
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R27", ruleCode);
            Assert.Contains("rtwlane.sys", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R27_3_Process_Loading_BthportSys_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 6003,
                ProcessName = "btaux.exe",
                DisplayName = "Bluetooth Helper",
                LoadedModules = new List<string> { @"C:\Windows\System32\drivers\bthport.sys" }
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R27", ruleCode);
            Assert.Contains("bthport.sys", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R27_4_Process_Loading_Rtkvhd64Sys_Denied()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 6004,
                ProcessName = "RAVCbg64.exe",
                DisplayName = "Realtek HD Audio Background Process",
                LoadedModules = new List<string> { @"C:\Windows\System32\drivers\rtkvhd64.sys" }
            };

            var verdict = _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out string reason);

            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.Equal("R27", ruleCode);
            Assert.Contains("rtkvhd64.sys", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_HP_R27_5_Process_Loading_OnlyUserModeDlls_NotDeniedByR27()
        {
            var proc = new ProcessIdentityDto
            {
                ProcessId = 6005,
                ProcessName = "CalculatorApp.exe",
                DisplayName = "Calculator",
                LoadedModules = new List<string> { @"C:\Windows\System32\user32.dll", @"C:\Windows\System32\kernel32.dll" }
            };

            _kernel.EvaluateProcessKill(
                proc,
                isGamingActive: false,
                isEditingActive: false,
                out string ruleCode,
                out _);

            Assert.NotEqual("R27", ruleCode);
        }
    }
}
