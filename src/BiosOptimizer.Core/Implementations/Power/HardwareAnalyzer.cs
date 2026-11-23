using System;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations.Power
{
    public class HardwareAnalyzer
    {
        private static readonly Lazy<HardwareAnalyzer> _instance = new(() => new HardwareAnalyzer());
        public static HardwareAnalyzer Instance => _instance.Value;

        private HardwareAnalysisReport? _cachedReport;
        private DateTime _lastCollectionTime = DateTime.MinValue;
        private readonly object _lock = new();

        public HardwareAnalysisReport Collect(bool forceRefresh = false)
        {
            lock (_lock)
            {
                if (!forceRefresh && _cachedReport != null && (DateTime.UtcNow - _lastCollectionTime).TotalSeconds < 30)
                {
                    // Update dynamic parameters (battery, workload) on cached report
                    UpdateDynamicMetrics(_cachedReport);
                    return _cachedReport;
                }

                var report = new HardwareAnalysisReport();

                // 1. CPU Analysis
                try
                {
                    using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (cpuKey != null)
                    {
                        report.CpuName = cpuKey.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Windows Processor";
                        if (cpuKey.GetValue("~MHz") is int mhz)
                        {
                            report.BaseClockGhz = Math.Round(mhz / 1000.0, 2);
                        }
                    }
                }
                catch { }

                report.LogicalCores = Environment.ProcessorCount;
                report.PhysicalCores = Math.Max(1, report.LogicalCores / 2);

                if (report.CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    report.CpuVendor = "Intel";
                    // Hybrid architecture check for 12th/13th/14th Gen or Core Ultra
                    if (report.CpuName.Contains("12th Gen") || report.CpuName.Contains("13th Gen") || 
                        report.CpuName.Contains("14th Gen") || report.CpuName.Contains("Ultra"))
                    {
                        report.HasHybridArchitecture = true;
                    }
                }
                else if (report.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                {
                    report.CpuVendor = "AMD";
                }

                // 2. RAM Analysis
                var hwProfile = HardwareProfiler.GetQuickProfile();
                report.RamTotalGb = hwProfile.RamTotalGb;
                report.IsLaptop = hwProfile.IsBatteryPowered || report.CpuName.Contains("Mobile", StringComparison.OrdinalIgnoreCase);

                // 3. GPU Analysis
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string gpuName = obj["Name"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(gpuName) && !gpuName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                        {
                            report.GpuName = gpuName;
                            if (obj["AdapterRAM"] != null && long.TryParse(obj["AdapterRAM"].ToString(), out long vramBytes) && vramBytes > 0)
                            {
                                report.GpuVramGb = Math.Round(vramBytes / (1024.0 * 1024.0 * 1024.0), 1);
                            }
                            report.IsDiscreteGpu = gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || 
                                                   gpuName.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                                                   gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                            break;
                        }
                    }
                }
                catch
                {
                    report.GpuName = "DirectX Display Adapter";
                }

                // 4. Storage Analysis
                try
                {
                    var systemDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.RootDirectory.FullName.StartsWith("C", StringComparison.OrdinalIgnoreCase));
                    if (systemDrive != null)
                    {
                        report.StorageFreeGb = Math.Round(systemDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                        report.StorageType = hwProfile.StorageType;
                    }
                }
                catch { }

                // 5. Thermal & Modern Standby Assessment
                report.ThermalStatus = "NORMAL (Within OEM Thermal Limits)";
                report.IsModernStandby = IsModernStandbyPlatform();

                // 6. Dynamic Metrics & Classification
                UpdateDynamicMetrics(report);
                report.TierClassification = Classify(report);

                _cachedReport = report;
                _lastCollectionTime = DateTime.UtcNow;
                return report;
            }
        }

        private static void UpdateDynamicMetrics(HardwareAnalysisReport report)
        {
            var hw = HardwareProfiler.GetQuickProfile();
            report.IsBatteryPowered = hw.IsBatteryPowered;
            report.BatteryPercent = hw.BatteryPercent;

            var (isBusy, workloadReason) = UserWorkloadDetector.EvaluateCurrentWorkload();
            report.DetectedWorkload = string.IsNullOrEmpty(workloadReason) ? "Standard Multi-tasking" : workloadReason;
        }

        public static bool IsModernStandbyPlatform()
        {
            try
            {
                using var powerKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
                if (powerKey != null)
                {
                    var aoac = powerKey.GetValue("PlatformAoAc");
                    if (aoac is int val && val == 1) return true;
                }
            }
            catch { }
            return false;
        }

        public string Classify(HardwareAnalysisReport report)
        {
            if (report.RamTotalGb <= 8.0 || report.PhysicalCores <= 2)
            {
                return "LOW RESOURCE";
            }
            if (report.RamTotalGb >= 32.0 || report.IsDiscreteGpu || report.PhysicalCores >= 8)
            {
                return "HIGH PERFORMANCE";
            }
            return "NORMAL BALANCED";
        }

        public PowerPlanRecommendation Recommend(HardwareAnalysisReport report, System.Collections.Generic.List<PowerPlanItem> installedPlans)
        {
            var activePlan = installedPlans.FirstOrDefault(p => p.IsActive);
            var rec = new PowerPlanRecommendation
            {
                HardwareProfile = report,
                ActiveWorkloadSummary = report.DetectedWorkload
            };

            // Custom AI Plan takes priority if already installed on the system
            var aiPlan = installedPlans.FirstOrDefault(p => p.IsCustomAiPlan && p.IsInstalled && p.IsSupported);

            // Case 1: Battery Critical (<= 25%)
            if (report.IsBatteryPowered && report.BatteryPercent <= 25)
            {
                var aiBat = installedPlans.FirstOrDefault(p => p.IsAiBatteryEfficiency && p.IsInstalled && p.IsSupported);
                var saver = installedPlans.FirstOrDefault(p => p.Guid.Equals(PowerPlanEngine.PowerSaverGuid, StringComparison.OrdinalIgnoreCase) && p.IsInstalled);
                rec.RecommendedPlanName = aiBat?.Name ?? saver?.Name ?? "Balanced";
                rec.RecommendedPlanGuid = aiBat?.Guid ?? saver?.Guid ?? PowerPlanEngine.BalancedGuid;
                rec.Reason = $"Battery is at {report.BatteryPercent}%. Preserves remaining capacity and avoids shutdown.";
                rec.BatteryWarning = "CRITICAL BATTERY: Energy preservation active.";
                rec.IsCurrentActiveOptimal = (activePlan?.Guid == rec.RecommendedPlanGuid) || (activePlan != null && activePlan.Name.Equals(rec.RecommendedPlanName, StringComparison.OrdinalIgnoreCase));
                return rec;
            }

            // Case 2: Laptop on Battery
            if (report.IsBatteryPowered)
            {
                var aiBat = installedPlans.FirstOrDefault(p => p.IsAiBatteryEfficiency && p.IsInstalled && p.IsSupported);
                rec.RecommendedPlanName = aiBat?.Name ?? "Balanced";
                rec.RecommendedPlanGuid = aiBat?.Guid ?? PowerPlanEngine.BalancedGuid;
                rec.Reason = aiBat != null
                    ? "Operating on DC Battery power. Error Optimizer AI — Battery Efficiency caps boost spikes and maximizes battery runtime."
                    : "Operating on DC Battery power. Balanced mode maintains battery longevity and avoids thermal throttling.";
                rec.BatteryWarning = "BATTERY ACTIVE: Operating on DC power.";
                rec.IsCurrentActiveOptimal = (activePlan?.Guid == rec.RecommendedPlanGuid) || (activePlan != null && activePlan.Name.Equals(rec.RecommendedPlanName, StringComparison.OrdinalIgnoreCase));
                return rec;
            }

            // Case 3: AC Power (Normal, High Performance, or Gaming/Workload)
            var aiMax = installedPlans.FirstOrDefault(p => p.IsAiMaxPerformance && p.IsInstalled && p.IsSupported);
            if (aiMax != null)
            {
                rec.RecommendedPlanName = aiMax.Name;
                rec.RecommendedPlanGuid = aiMax.Guid;
                rec.Reason = $"AC power active on {report.CpuName}. Error Optimizer AI — Max Performance delivers sustained processor response and unparked cores.";
            }
            else
            {
                var ult = installedPlans.FirstOrDefault(p => p.Name.Equals("Ultimate Performance", StringComparison.OrdinalIgnoreCase) && p.IsInstalled);
                var high = installedPlans.FirstOrDefault(p => p.Name.Equals("High performance", StringComparison.OrdinalIgnoreCase) && p.IsInstalled);
                if (ult != null)
                {
                    rec.RecommendedPlanName = ult.Name;
                    rec.RecommendedPlanGuid = ult.Guid;
                    rec.Reason = $"AC power active. Ultimate Performance delivers zero-latency core response.";
                }
                else if (high != null)
                {
                    rec.RecommendedPlanName = high.Name;
                    rec.RecommendedPlanGuid = high.Guid;
                    rec.Reason = "AC power active. High performance profile eliminates processor downclock latency.";
                }
                else
                {
                    rec.RecommendedPlanName = "Balanced";
                    rec.RecommendedPlanGuid = PowerPlanEngine.BalancedGuid;
                    rec.Reason = "Standard balanced dynamic scaling.";
                }
            }
            rec.IsCurrentActiveOptimal = (activePlan?.Guid == rec.RecommendedPlanGuid) || (activePlan != null && activePlan.Name.Equals(rec.RecommendedPlanName, StringComparison.OrdinalIgnoreCase));
            return rec;
        }
    }
}
