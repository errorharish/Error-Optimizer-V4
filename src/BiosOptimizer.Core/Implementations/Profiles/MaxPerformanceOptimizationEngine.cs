#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations.Profiles
{
    public class MaxPerformanceDiagnostics
    {
        public bool GameModeActive { get; set; }
        public bool GameDvrDisabled { get; set; }
        public bool HighPrecisionTimerActive { get; set; }
        public bool EmulatorDetected { get; set; }
        public bool CreativeAppDetected { get; set; }
        public string PrimaryBottleneck { get; set; } = "Balanced";
        public List<string> ActiveWorkloadProcesses { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    public class MaxPerformanceOptimizationEngine
    {
        private static readonly Lazy<MaxPerformanceOptimizationEngine> _instance = new(() => new MaxPerformanceOptimizationEngine());
        public static MaxPerformanceOptimizationEngine Instance => _instance.Value;

        private static readonly string[] EmulatorSignatures = new[]
        {
            "HD-Player", "HD-Adb", "dnplayer", "ldboxheadless", "Nox", "MEmuConsole", "MuMuPlayer"
        };

        private static readonly string[] CreativeSignatures = new[]
        {
            "Adobe Premiere Pro", "AfterFX", "Resolve", "blender", "photoshop"
        };

        public MaxPerformanceDiagnostics RunDiagnostics()
        {
            var diag = new MaxPerformanceDiagnostics();

            // 1. Game Mode Check
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
                diag.GameModeActive = Convert.ToInt32(key?.GetValue("AllowAutoGameMode") ?? 1) == 1;

                using var dvrKey = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
                diag.GameDvrDisabled = Convert.ToInt32(dvrKey?.GetValue("GameDVR_Enabled") ?? 1) == 0;
            }
            catch { }

            // 2. Workload Process Detection
            try
            {
                var procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    try
                    {
                        string pName = p.ProcessName;
                        if (EmulatorSignatures.Any(sig => pName.Contains(sig, StringComparison.OrdinalIgnoreCase)))
                        {
                            diag.EmulatorDetected = true;
                            diag.ActiveWorkloadProcesses.Add(pName);
                        }
                        else if (CreativeSignatures.Any(sig => pName.Contains(sig, StringComparison.OrdinalIgnoreCase)))
                        {
                            diag.CreativeAppDetected = true;
                            diag.ActiveWorkloadProcesses.Add(pName);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 3. Bottleneck Analysis
            if (diag.EmulatorDetected)
            {
                diag.PrimaryBottleneck = "CPU_CORE_AFFINITY";
                diag.Recommendations.Add("Active Android emulator detected: bind worker threads to physical P-cores to eliminate frame pacing jitter.");
            }
            else if (diag.CreativeAppDetected)
            {
                diag.PrimaryBottleneck = "STORAGE_IO_QUEUE";
                diag.Recommendations.Add("Active creative render suite detected: prioritize file write streams and assign long fixed CPU quantums (0x02).");
            }
            else
            {
                diag.PrimaryBottleneck = "HARDWARE_BALANCED";
                diag.Recommendations.Add("Engage 0.5ms multimedia timer resolution and 0x26 CPU quantum for maximum interactive gaming responsiveness.");
            }

            if (!diag.GameDvrDisabled)
                diag.Recommendations.Add("Disable Xbox Game DVR background recording to reclaim 5-10% GPU encoding overhead.");

            return diag;
        }
    }
}
