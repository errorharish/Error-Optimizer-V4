#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BiosOptimizer.Core.Implementations.Power;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations.Profiles
{
    public class NormalOptimizationDiagnostics
    {
        public bool IsOnBattery { get; set; }
        public int RedundantStartupEntriesDetected { get; set; }
        public int ActiveBackgroundAppsCount { get; set; }
        public double RamUsagePercent { get; set; }
        public bool StorageSenseActive { get; set; }
        public bool NewsAndInterestsDisabled { get; set; }
        public bool P2PUploadRestricted { get; set; }
        public List<string> RedundantStartupNames { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    public class NormalOptimizationEngine
    {
        private static readonly Lazy<NormalOptimizationEngine> _instance = new(() => new NormalOptimizationEngine());
        public static NormalOptimizationEngine Instance => _instance.Value;

        private static readonly string[] CommonStartupLocations = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        };

        private static readonly string[] RedundantStartupSignatures = new[]
        {
            "Teams", "Zoom", "EpicGamesLauncher", "OneDrive", "Cortana", "Skype"
        };

        public NormalOptimizationDiagnostics RunDiagnostics()
        {
            var diag = new NormalOptimizationDiagnostics();

            // 1. Power & Battery Status
            try
            {
                var snap = new HardwareDetectionService().GetSnapshot();
                diag.IsOnBattery = snap.IsOnBattery;
            }
            catch
            {
                diag.IsOnBattery = false;
            }

            // 2. Redundant Startup Entry Discovery
            var discoveredStartups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var loc in CommonStartupLocations)
                {
                    try
                    {
                        using var key = hive.OpenSubKey(loc);
                        if (key != null)
                        {
                            foreach (var valName in key.GetValueNames())
                            {
                                if (BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.Instance.IsStartupEntryProtected(valName))
                                    continue;

                                if (RedundantStartupSignatures.Any(sig => valName.Contains(sig, StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (discoveredStartups.Add(valName))
                                    {
                                        diag.RedundantStartupNames.Add(valName);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            diag.RedundantStartupEntriesDetected = diag.RedundantStartupNames.Count;

            // 3. RAM Commit / Memory Load
            try
            {
                var mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref mem))
                {
                    diag.RamUsagePercent = mem.dwMemoryLoad;
                }
            }
            catch { }

            // 4. Windows Feature Checks (Storage Sense, News, P2P)
            try
            {
                using var ssKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
                diag.StorageSenseActive = Convert.ToInt32(ssKey?.GetValue("01") ?? 0) == 1;

                using var newsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds");
                diag.NewsAndInterestsDisabled = Convert.ToInt32(newsKey?.GetValue("EnableFeeds") ?? 1) == 0;

                using var doKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization");
                int doMode = Convert.ToInt32(doKey?.GetValue("DODownloadMode") ?? -1);
                diag.P2PUploadRestricted = doMode == 0 || doMode == 99;
            }
            catch { }

            // 5. Build Recommendations
            if (diag.RedundantStartupEntriesDetected > 0)
                diag.Recommendations.Add($"Disable {diag.RedundantStartupEntriesDetected} redundant startup background tasks ({string.Join(", ", diag.RedundantStartupNames.Take(3))}) to accelerate boot time.");

            if (!diag.StorageSenseActive)
                diag.Recommendations.Add("Enable Windows Storage Sense automatic maintenance to periodically purge temporary cache files.");

            if (!diag.NewsAndInterestsDisabled)
                diag.Recommendations.Add("Disable News & Interests taskbar background feed to save 100MB+ idle RAM.");

            if (!diag.P2PUploadRestricted)
                diag.Recommendations.Add("Restrict Delivery Optimization background P2P upload seeding to preserve network bandwidth.");

            return diag;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
