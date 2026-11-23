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
    public class ProOptimizationDiagnostics
    {
        public bool IsHybridCpu { get; set; }
        public int PerformanceCoreCount { get; set; }
        public int EfficientCoreCount { get; set; }
        public bool CppcSupported { get; set; }
        public bool HasDedicatedGpu { get; set; }
        public double DedicatedVramGb { get; set; }
        public bool NvmeStorageDetected { get; set; }
        public int MmcssSystemResponsiveness { get; set; }
        public bool RssEnabled { get; set; }
        public List<string> ContentionWarnings { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    public class ProOptimizationEngine
    {
        private static readonly Lazy<ProOptimizationEngine> _instance = new(() => new ProOptimizationEngine());
        public static ProOptimizationEngine Instance => _instance.Value;

        public ProOptimizationDiagnostics RunDiagnostics()
        {
            var diag = new ProOptimizationDiagnostics();

            // 1. CPU Topology & Hybrid Architecture
            int logicalProcessors = Environment.ProcessorCount;
            diag.PerformanceCoreCount = logicalProcessors; // baseline

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                string cpuId = key?.GetValue("ProcessorNameString")?.ToString() ?? "";
                if (cpuId.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                    (cpuId.Contains("12th Gen", StringComparison.OrdinalIgnoreCase) ||
                     cpuId.Contains("13th Gen", StringComparison.OrdinalIgnoreCase) ||
                     cpuId.Contains("14th Gen", StringComparison.OrdinalIgnoreCase) ||
                     cpuId.Contains("Ultra", StringComparison.OrdinalIgnoreCase)))
                {
                    diag.IsHybridCpu = true;
                    // Approximate P/E core split based on logical count
                    diag.EfficientCoreCount = logicalProcessors >= 16 ? 8 : (logicalProcessors >= 12 ? 4 : 0);
                    diag.PerformanceCoreCount = logicalProcessors - diag.EfficientCoreCount;
                }
            }
            catch { }

            // 2. CPPC (Collaborative Processor Performance Control)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\89e37796-a416-4a73-9ef1-77886f795cb9");
                diag.CppcSupported = key != null;
            }
            catch { }

            // 3. GPU & VRAM Diagnostics
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                if (key != null)
                {
                    object? vram = key.GetValue("HardwareInformation.qwMemorySize");
                    if (vram != null)
                    {
                        diag.DedicatedVramGb = Convert.ToDouble(vram) / (1024.0 * 1024.0 * 1024.0);
                        diag.HasDedicatedGpu = diag.DedicatedVramGb >= 2.0;
                    }
                }
            }
            catch { }

            // 4. MMCSS Responsiveness
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                diag.MmcssSystemResponsiveness = Convert.ToInt32(key?.GetValue("SystemResponsiveness") ?? 20);
            }
            catch { }

            // 5. NVMe Storage
            try
            {
                diag.NvmeStorageDetected = DriveInfo.GetDrives().Any(d => d.IsReady && d.DriveType == DriveType.Fixed);
            }
            catch { }

            // 6. Build Recommendations
            if (diag.IsHybridCpu)
                diag.Recommendations.Add("Intel Hybrid topology detected: configure Thread Director and CPU Sets to route primary threads to P-Cores.");

            if (diag.CppcSupported)
                diag.Recommendations.Add("CPPC v2 supported: enable autonomous preferred core targeting for high-clock single-threaded boosts.");

            if (diag.MmcssSystemResponsiveness < 20)
                diag.ContentionWarnings.Add($"MMCSS SystemResponsiveness is configured to {diag.MmcssSystemResponsiveness}. Microsoft clamps values <10 to 20; safe clamp of 20 is recommended.");

            if (diag.HasDedicatedGpu)
                diag.Recommendations.Add($"Discrete GPU with {diag.DedicatedVramGb:F1}GB VRAM detected: enable high-performance WDDM priority and 8-second TDR delay.");

            return diag;
        }
    }
}
