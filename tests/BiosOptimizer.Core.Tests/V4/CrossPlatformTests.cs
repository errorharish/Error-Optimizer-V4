#nullable enable
using System;
using System.IO;
using System.Text.Json;
using BiosOptimizer.Core.V4.Environments;
using BiosOptimizer.Core.V4.Power;
using BiosOptimizer.Core.V4.Safety;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class CrossPlatformTests
    {
        [Fact]
        public void T_OS_1_Windows10_TargetsFile_ValidAndContainsWin10Specifics()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "data", "reducible_windows10.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), "data", "reducible_windows10.json");
            }

            Assert.True(File.Exists(path), $"Win10 target file must exist: {path}");
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("targets", out var targets) || root.TryGetProperty("reducibleProcesses", out targets));
            Assert.True(targets.GetArrayLength() > 0);

            // Windows 10 targets Cortana and YourPhone
            bool hasCortana = false;
            foreach (var t in targets.EnumerateArray())
            {
                string id = (t.TryGetProperty("id", out var idProp) ? idProp.GetString() : null)
                         ?? (t.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null)
                         ?? (t.TryGetProperty("category", out var catProp) ? catProp.GetString() : null)
                         ?? "";
                if (id.Contains("Cortana", StringComparison.OrdinalIgnoreCase) || id.Contains("YourPhone", StringComparison.OrdinalIgnoreCase))
                {
                    hasCortana = true;
                    break;
                }
            }
            Assert.True(hasCortana, "Win10 targets must include Cortana or YourPhone");
        }

        [Fact]
        public void T_OS_2_Windows11_TargetsFile_ValidAndContainsWin11Specifics()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "data", "reducible_windows11.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), "data", "reducible_windows11.json");
            }

            Assert.True(File.Exists(path), $"Win11 target file must exist: {path}");
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("targets", out var targets) || root.TryGetProperty("reducibleProcesses", out targets));
            Assert.True(targets.GetArrayLength() > 0);

            // Windows 11 targets Widgets and WebExperience
            bool hasWidgets = false;
            foreach (var t in targets.EnumerateArray())
            {
                string id = (t.TryGetProperty("id", out var idProp) ? idProp.GetString() : null)
                         ?? (t.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null)
                         ?? (t.TryGetProperty("category", out var catProp) ? catProp.GetString() : null)
                         ?? "";
                if (id.Contains("Widgets", StringComparison.OrdinalIgnoreCase) || id.Contains("WebExperience", StringComparison.OrdinalIgnoreCase))
                {
                    hasWidgets = true;
                    break;
                }
            }
            Assert.True(hasWidgets, "Win11 targets must include Widgets or WebExperience");
        }

        [Fact]
        public void T_FORM_1_LaptopDetectsBattery_LimitsPowerPlan()
        {
            var decision = PowerPlanEngine.EvaluatePowerPlanAdjustment(isLaptop: true, isOnBattery: true);
            Assert.False(decision.AllowAggressivePlan);
            Assert.Equal("Balanced (OEM/ACPI Safe)", decision.SelectedPlanName);
            Assert.Contains("battery", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_FORM_2_Desktop_AllowsAggressiveCpuPowerPlan()
        {
            var decision = PowerPlanEngine.EvaluatePowerPlanAdjustment(isLaptop: false, isOnBattery: false);
            Assert.True(decision.AllowAggressivePlan);
            Assert.Equal("Ultimate Performance", decision.SelectedPlanName);
        }

        [Fact]
        public void T_FORM_3_ModernStandby_ProtectsConnectedStandbyServices()
        {
            // StorSvc, DcomLaunch, brokerinfrastructure are protected for Modern Standby systems
            Assert.True(HardProtectRegistry.IsServiceProtected("StorSvc"));
            Assert.True(HardProtectRegistry.IsServiceProtected("BrokerInfrastructure"));
            Assert.True(HardProtectRegistry.IsServiceProtected("DcomLaunch"));
        }

        [Fact]
        public void T_OEM_1_OemPowerServices_RecognizedAndProtectedPerVendor()
        {
            // ASUS Armoury Crate
            Assert.True(HardProtectRegistry.IsServiceProtected("ArmouryCrateControlInterface"));
            Assert.True(HardProtectRegistry.IsServiceProtected("AsusROGLSLService"));

            // Lenovo Vantage
            Assert.True(HardProtectRegistry.IsServiceProtected("LenovoVantageService"));

            // Dell / Alienware Command Center
            Assert.True(HardProtectRegistry.IsServiceProtected("AWCCService"));
            Assert.True(HardProtectRegistry.IsServiceProtected("DellDataVault"));

            // HP OMEN Gaming Hub
            Assert.True(HardProtectRegistry.IsServiceProtected("OmenHSA"));
            Assert.True(HardProtectRegistry.IsServiceProtected("OmenCap"));

            // Razer Synapse
            Assert.True(HardProtectRegistry.IsServiceProtected("Razer Synapse Service"));
        }
    }
}
