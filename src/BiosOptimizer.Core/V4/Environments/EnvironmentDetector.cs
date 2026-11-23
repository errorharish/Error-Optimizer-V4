#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Environments
{
    public sealed class EnvironmentInfo
    {
        public bool IsWindows11 { get; init; }
        public int BuildNumber { get; init; }
        public bool IsLaptop { get; init; }
        public bool HasBattery { get; init; }
        public bool IsOnAcPower { get; init; }
        public int PhysicalCoreCount { get; init; }
        public int LogicalProcessorCount { get; init; }
        public bool HasHyperThreading { get; init; }
        public bool HasDiscreteGpu { get; init; }
        public string PrimaryGpuVendor { get; init; } = string.Empty;
        public long TotalRamMb { get; init; }
        public bool IsDomainJoined { get; init; }
        public bool HasBitLocker { get; init; }
        public string SystemManufacturer { get; init; } = string.Empty;
        public string SystemModel { get; init; } = string.Empty;
    }

    public class EnvironmentDetector
    {
        private static readonly Lazy<EnvironmentDetector> _instance = new(() => new EnvironmentDetector());
        public static EnvironmentDetector Instance => _instance.Value;

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        public EnvironmentInfo CurrentInfo { get; private set; }

        public EnvironmentDetector()
        {
            CurrentInfo = DetectEnvironment();
        }

        public EnvironmentInfo DetectEnvironment()
        {
            int build = Environment.OSVersion.Version.Build;
            // Also check registry for Windows 10/11 release build
            try
            {
                using var cvKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (cvKey != null)
                {
                    var cbr = cvKey.GetValue("CurrentBuildNumber")?.ToString() ?? cvKey.GetValue("CurrentBuild")?.ToString();
                    if (int.TryParse(cbr, out int parsedBuild))
                    {
                        build = parsedBuild;
                    }
                }
            }
            catch { }

            bool isWin11 = build >= 22000;

            // Power & Laptop Detection
            bool hasBattery = false;
            bool isOnAc = true;
            bool isLaptop = false;

            if (GetSystemPowerStatus(out var powerStatus))
            {
                // BatteryFlag 128 = No system battery, 255 = Unknown status
                hasBattery = (powerStatus.BatteryFlag != 128 && powerStatus.BatteryFlag != 255);
                isOnAc = (powerStatus.ACLineStatus == 1);
                isLaptop = hasBattery;
            }

            // CPU & Cores
            int logicalProcessors = Environment.ProcessorCount;
            int physicalCores = BiosOptimizer.Core.Implementations.HardwareDetectionService.DetectPhysicalCoreCount() ?? logicalProcessors;

            // GPU & RAM Detection
            bool hasDiscreteGpu = false;
            string gpuVendor = "Unknown";
            long ramMb = 0;
            try
            {
                var memSnap = BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot().Memory;
                double effectiveGb = memSnap.UsablePhysicalGb > 0 ? memSnap.UsablePhysicalGb : memSnap.InstalledPhysicalGb;
                if (effectiveGb > 0)
                    ramMb = (long)Math.Round(effectiveGb * 1024.0);
            }
            catch { }

            try
            {
                var dxgi = BiosOptimizer.Core.Implementations.DxgiGpuReader.EnumerateAdapters();
                if (dxgi.Count > 0)
                {
                    var primary = dxgi.Find(a => a.IsDedicatedGpu) ?? dxgi[0];
                    hasDiscreteGpu = dxgi.Exists(a => a.IsDedicatedGpu);
                    gpuVendor = primary.VendorId switch
                    {
                        0x10DE => "NVIDIA",
                        0x1002 => "AMD",
                        0x8086 => "Intel",
                        _ => primary.Description
                    };
                }
            }
            catch { }

            if (gpuVendor == "Unknown")
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                    foreach (var obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? string.Empty;
                        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDiscreteGpu = true;
                            gpuVendor = "NVIDIA";
                        }
                        else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) && !name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) && !name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDiscreteGpu = name.Contains("RX", StringComparison.OrdinalIgnoreCase) || name.Contains("PRO", StringComparison.OrdinalIgnoreCase);
                            gpuVendor = "AMD";
                        }
                        else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && gpuVendor == "Unknown")
                        {
                            if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) hasDiscreteGpu = true;
                            gpuVendor = "Intel";
                        }
                    }
                }
                catch { }
            }

            if (ramMb <= 0)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                    foreach (var obj in searcher.Get())
                    {
                        if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes) && bytes > 0)
                        {
                            ramMb = (long)(bytes / (1024 * 1024));
                            break;
                        }
                    }
                }
                catch { }
            }

            // OEM / Manufacturer
            string manufacturer = string.Empty;
            string model = string.Empty;
            try
            {
                using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (biosKey != null)
                {
                    manufacturer = biosKey.GetValue("SystemManufacturer")?.ToString() ?? string.Empty;
                    model = biosKey.GetValue("SystemProductName")?.ToString() ?? string.Empty;
                }
            }
            catch { }

            if (string.IsNullOrEmpty(manufacturer))
            {
                try
                {
                    using var csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                    foreach (var obj in csSearcher.Get())
                    {
                        manufacturer = obj["Manufacturer"]?.ToString() ?? string.Empty;
                        model = obj["Model"]?.ToString() ?? string.Empty;
                    }
                }
                catch { }
            }

            // Domain joined check
            bool isDomainJoined = false;
            try
            {
                string domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
                if (!string.IsNullOrEmpty(domain) && !domain.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase) && !domain.Equals("local", StringComparison.OrdinalIgnoreCase))
                {
                    isDomainJoined = true;
                }
            }
            catch { }

            // BitLocker Check
            bool hasBitLocker = false;
            try
            {
                using var blSearcher = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'");
                foreach (var obj in blSearcher.Get())
                {
                    if (obj["ProtectionStatus"]?.ToString() == "1")
                    {
                        hasBitLocker = true;
                    }
                }
            }
            catch { }

            return new EnvironmentInfo
            {
                IsWindows11 = isWin11,
                BuildNumber = build,
                IsLaptop = isLaptop,
                HasBattery = hasBattery,
                IsOnAcPower = isOnAc,
                PhysicalCoreCount = physicalCores,
                LogicalProcessorCount = logicalProcessors,
                HasHyperThreading = logicalProcessors > physicalCores,
                HasDiscreteGpu = hasDiscreteGpu,
                PrimaryGpuVendor = gpuVendor,
                TotalRamMb = ramMb,
                IsDomainJoined = isDomainJoined,
                HasBitLocker = hasBitLocker,
                SystemManufacturer = manufacturer,
                SystemModel = model
            };
        }

        public IReadOnlyList<string> GetOsReducibleProcessNames()
        {
            var list = new List<string>();
            string dataDir = FindDataDirectory();
            string fileName = CurrentInfo.IsWindows11 ? "reducible_windows11.json" : "reducible_windows10.json";
            string path = Path.Combine(dataDir, fileName);

            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("reducibleProcesses", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (el.TryGetProperty("name", out var n))
                            {
                                list.Add(n.GetString()!);
                            }
                        }
                    }
                }
                catch { }
            }

            // Fallbacks
            if (list.Count == 0)
            {
                if (CurrentInfo.IsWindows11)
                {
                    list.AddRange(new[] { "Widgets.exe", "WidgetService.exe", "WidgetsPlatformRuntime.exe", "WebExperience.exe", "PhoneExperienceHost.exe", "GameBar.exe", "GameBarFTServer.exe" });
                }
                else
                {
                    list.AddRange(new[] { "SearchUI.exe", "YourPhone.exe", "GameBar.exe", "GameBarFTServer.exe", "XboxGamingOverlay.exe", "People.exe" });
                }
            }

            return list;
        }

        private static string FindDataDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(baseDir, "data");
            if (Directory.Exists(p1)) return p1;

            string cwd = Directory.GetCurrentDirectory();
            string p2 = Path.Combine(cwd, "data");
            if (Directory.Exists(p2)) return p2;

            return baseDir;
        }
    }
}
