using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BiosOptimizer.Service.Metrics
{
    public class LiveSystemMetricsService : BackgroundService
    {
        private readonly ILogger<LiveSystemMetricsService> _logger;
        
        public static LiveMetricsPayload CurrentMetrics { get; private set; } = new LiveMetricsPayload();

        private List<DxgiAdapterInfo> _cachedAdapters = new();
        private List<string> _cachedGpuNames = new();
        private DateTime _lastGpuDetect = DateTime.MinValue;
        private PerformanceCounterCategory? _gpuCategory;
        private double _baseCpuClockGhz = 2.4;
        private DateTime _lastCpuBaseDetect = DateTime.MinValue;

        public LiveSystemMetricsService(ILogger<LiveSystemMetricsService> logger)
        {
            _logger = logger;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
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
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LiveSystemMetricsService started.");
            
            PerformanceCounter? cpuCounter = null;
            PerformanceCounter? cpuPerfCounter = null;
            PerformanceCounter? diskTimeCounter = null;
            PerformanceCounter? diskReadCounter = null;
            PerformanceCounter? diskWriteCounter = null;
            List<PerformanceCounter> netRecvCounters = new();
            List<PerformanceCounter> netSentCounters = new();
            DateTime lastNetCategoryScan = DateTime.MinValue;
            
            try
            {
                if (PerformanceCounterCategory.Exists("Processor Information"))
                {
                    cpuCounter = new PerformanceCounter("Processor Information", "% Processor Time", "_Total");
                    cpuCounter.NextValue();
                    try
                    {
                        cpuPerfCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                        cpuPerfCounter.NextValue();
                    }
                    catch { }
                }
                else if (PerformanceCounterCategory.Exists("Processor"))
                {
                    cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    cpuCounter.NextValue();
                }
                
                if (PerformanceCounterCategory.Exists("PhysicalDisk"))
                {
                    diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
                    diskTimeCounter.NextValue();
                    diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    diskReadCounter.NextValue();
                    diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    diskWriteCounter.NextValue();
                }
                
                if (PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    _gpuCategory = new PerformanceCounterCategory("GPU Engine");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize standard PerformanceCounters.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. CPU Metrics
                    double cpuUsage = 0;
                    if (cpuCounter != null)
                    {
                        try { cpuUsage = Math.Min(100.0, Math.Max(0.0, Math.Round(cpuCounter.NextValue(), 1))); } catch { }
                    }

                    double cpuFreqGhz = DetectCpuFrequency(cpuPerfCounter);
                    double cpuTemp = DetectCpuTemperature();

                    // 2. RAM Metrics via GlobalMemoryStatusEx
                    ulong totalRamMb = 0;
                    ulong availRamMb = 0;
                    ulong usedRamMb = 0;
                    double ramPercentage = 0;
                    
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus))
                    {
                        totalRamMb = memStatus.ullTotalPhys / (1024 * 1024);
                        availRamMb = memStatus.ullAvailPhys / (1024 * 1024);
                        usedRamMb = totalRamMb > availRamMb ? totalRamMb - availRamMb : 0;
                        ramPercentage = memStatus.dwMemoryLoad;
                    }

                    // 3. Disk I/O & Storage Capacity
                    double diskActive = 0;
                    double diskReadBytesPerSec = 0;
                    double diskWriteBytesPerSec = 0;
                    if (diskTimeCounter != null)
                    {
                        try { diskActive = Math.Min(100.0, Math.Round(diskTimeCounter.NextValue(), 1)); } catch { }
                    }
                    if (diskReadCounter != null)
                    {
                        try { diskReadBytesPerSec = Math.Round(diskReadCounter.NextValue(), 0); } catch { }
                    }
                    if (diskWriteCounter != null)
                    {
                        try { diskWriteBytesPerSec = Math.Round(diskWriteCounter.NextValue(), 0); } catch { }
                    }

                    double storagePercentage = 0;
                    string storageInfo = "No fixed storage";
                    try
                    {
                        var agg = BiosOptimizer.Core.Implementations.Storage.StorageEngine.ComputeFastFixedVolumeAggregate();
                        if (agg.VolumeCount > 0 && agg.UnifiedTotalBytes > 0)
                        {
                            storagePercentage = agg.UnifiedUsagePercent;
                            storageInfo = $"{agg.UnifiedUsedGb:F1} GB / {agg.UnifiedTotalGb:F1} GB";
                        }
                    }
                    catch { }

                    // 4. Network I/O
                    if ((DateTime.UtcNow - lastNetCategoryScan).TotalSeconds > 30 || netRecvCounters.Count == 0)
                    {
                        RefreshNetworkCounters(ref netRecvCounters, ref netSentCounters);
                        lastNetCategoryScan = DateTime.UtcNow;
                    }

                    double netDownloadBps = 0;
                    double netUploadBps = 0;
                    foreach (var counter in netRecvCounters)
                    {
                        try { netDownloadBps += counter.NextValue(); } catch { }
                    }
                    foreach (var counter in netSentCounters)
                    {
                        try { netUploadBps += counter.NextValue(); } catch { }
                    }

                    var kernelSnap = BiosOptimizer.Core.Implementations.Diagnostics.HardwareTelemetryCollector.Instance.CollectSnapshot();
                    if (kernelSnap.CpuTempC.HasValue && kernelSnap.CpuTempC.Value > 0)
                        cpuTemp = kernelSnap.CpuTempC.Value;

                    int processCount = 0;
                    int threadCount = 0;
                    try
                    {
                        var procs = Process.GetProcesses();
                        processCount = procs.Length;
                        foreach (var proc in procs)
                        {
                            try { threadCount += proc.Threads.Count; } catch { threadCount++; }
                            try { proc.Dispose(); } catch { }
                        }
                    }
                    catch { }

                    // 5. Multi-GPU Metrics (combine PDH 3D counters + DXGI live VRAM + NVML temps/util)
                    var gpuMetrics = GetGpuMetrics(kernelSnap);

                    string powerPlan = GetActivePowerPlan();

                    CurrentMetrics = new LiveMetricsPayload
                    {
                        CpuUtilization = cpuUsage,
                        CpuFrequencyGhz = Math.Round(cpuFreqGhz, 2),
                        CpuTemperatureC = cpuTemp > 0 ? Math.Round(cpuTemp, 1) : 0,
                        HasCpuTemperature = cpuTemp > 0,
                        CpuTemperatureSource = kernelSnap.CpuTempSource,
                        SsdTemperatureC = kernelSnap.SsdTempC,
                        SsdTemperatureSource = kernelSnap.SsdTempSource,
                        DpcLatencyUs = kernelSnap.DpcUs,
                        RamTotalMb = totalRamMb,
                        RamAvailableMb = availRamMb,
                        RamUsedMb = usedRamMb,
                        RamPercentage = ramPercentage,
                        DiskActivePercentage = diskActive,
                        DiskReadBytesPerSec = diskReadBytesPerSec,
                        DiskWriteBytesPerSec = diskWriteBytesPerSec,
                        StoragePercentage = storagePercentage,
                        StorageInfo = storageInfo,
                        NetworkDownloadBps = Math.Round(netDownloadBps, 0),
                        NetworkUploadBps = Math.Round(netUploadBps, 0),
                        PowerPlan = powerPlan,
                        ProcessCount = processCount,
                        ThreadCount = threadCount,
                        Gpus = gpuMetrics,
                        Timestamp = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in LiveSystemMetricsService loop.");
                }

                await Task.Delay(1000, stoppingToken);
            }
        }

        private double DetectCpuFrequency(PerformanceCounter? perfCounter)
        {
            try
            {
                if ((DateTime.UtcNow - _lastCpuBaseDetect).TotalMinutes > 10)
                {
                    using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
                    foreach (var obj in searcher.Get())
                    {
                        if (double.TryParse(obj["MaxClockSpeed"]?.ToString(), out double maxMhz) && maxMhz > 500)
                        {
                            _baseCpuClockGhz = maxMhz / 1000.0;
                            break;
                        }
                    }
                    _lastCpuBaseDetect = DateTime.UtcNow;
                }

                if (perfCounter != null)
                {
                    double perfPct = perfCounter.NextValue();
                    if (perfPct > 0)
                    {
                        return Math.Max(0.8, _baseCpuClockGhz * (perfPct / 100.0));
                    }
                }
            }
            catch { }
            return _baseCpuClockGhz;
        }

        private double DetectCpuTemperature()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (var obj in searcher.Get())
                {
                    if (double.TryParse(obj["CurrentTemperature"]?.ToString(), out double kelvinTenths) && kelvinTenths > 2732)
                    {
                        return (kelvinTenths - 2732) / 10.0;
                    }
                }
            }
            catch { }
            return 0; // Not exposed by ACPI BIOS
        }

        private void RefreshNetworkCounters(ref List<PerformanceCounter> recv, ref List<PerformanceCounter> sent)
        {
            try
            {
                foreach (var c in recv) { try { c.Dispose(); } catch { } }
                foreach (var c in sent) { try { c.Dispose(); } catch { } }
                recv.Clear();
                sent.Clear();

                if (PerformanceCounterCategory.Exists("Network Interface"))
                {
                    var cat = new PerformanceCounterCategory("Network Interface");
                    var instances = cat.GetInstanceNames();
                    foreach (var inst in instances)
                    {
                        if (inst.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                            inst.Contains("isatap", StringComparison.OrdinalIgnoreCase) ||
                            inst.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            var r = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst);
                            r.NextValue();
                            recv.Add(r);

                            var s = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst);
                            s.NextValue();
                            sent.Add(s);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private string GetActivePowerPlan()
        {
            try
            {
                var (_, name) = BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.GetActiveSchemeNative();
                return !string.IsNullOrWhiteSpace(name) ? name : "Balanced";
            }
            catch
            {
                return "Balanced";
            }
        }


        private DateTime _lastGpuInstanceUpdate = DateTime.MinValue;
        private List<(int PhysIndex, string InstanceName, PerformanceCounter Counter)> _cachedGpuCounters = new();

        private List<GpuMetric> GetGpuMetrics(BiosOptimizer.Core.Implementations.Diagnostics.KernelTelemetrySnapshot? kernelSnap = null)
        {
            var gpus = new List<GpuMetric>();
            try
            {
                // Refresh DXGI adapters on interval so DedicatedVramUsageBytes stays fresh
                if ((DateTime.UtcNow - _lastGpuDetect).TotalSeconds > 60 || (_cachedAdapters.Count == 0 && _cachedGpuNames.Count == 0))
                {
                    try
                    {
                        var dxgiList = DxgiGpuReader.EnumerateAdapters();
                        if (dxgiList != null && dxgiList.Count > 0)
                        {
                            _cachedAdapters = dxgiList;
                        }
                    }
                    catch { }

                    if (_cachedAdapters.Count == 0)
                    {
                        var names = new List<string>();
                        try
                        {
                            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
                            foreach (var obj in searcher.Get())
                            {
                                var n = obj["Name"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(n) && !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                                {
                                    names.Add(n);
                                }
                            }
                        }
                        catch { }

                        _cachedGpuNames = names;
                    }

                    _lastGpuDetect = DateTime.UtcNow;
                }
                else if (_cachedAdapters.Count > 0)
                {
                    try
                    {
                        var liveDxgi = DxgiGpuReader.EnumerateAdapters();
                        if (liveDxgi.Count > 0) _cachedAdapters = liveDxgi;
                    }
                    catch { }
                }

                int gpuCount = _cachedAdapters.Count > 0 ? _cachedAdapters.Count : _cachedGpuNames.Count;
                var gpuLoads = new Dictionary<int, double>();
                for (int i = 0; i < gpuCount; i++)
                {
                    gpuLoads[i] = 0;
                }

                if (_gpuCategory != null)
                {
                    try
                    {
                        // Refresh counter instances every 15s using canonical WDDM engine queues (not raw 3D PID sum)
                        if ((DateTime.UtcNow - _lastGpuInstanceUpdate).TotalSeconds > 15 || _cachedGpuCounters.Count == 0)
                        {
                            var instances = _gpuCategory.GetInstanceNames();
                            var engineInstances = instances
                                .Where(i => i.Contains("engtype_", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            foreach (var item in _cachedGpuCounters) { try { item.Counter.Dispose(); } catch { } }
                            _cachedGpuCounters.Clear();

                            foreach (var inst in engineInstances)
                            {
                                try
                                {
                                    int targetIdx = GpuTelemetryHelper.ResolveAdapterIndex(inst, _cachedAdapters, gpuCount);
                                    if (targetIdx >= 0)
                                    {
                                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                                        pc.NextValue();
                                        _cachedGpuCounters.Add((targetIdx, inst, pc));
                                    }
                                }
                                catch { }
                            }
                            _lastGpuInstanceUpdate = DateTime.UtcNow;
                        }

                        var pdhSamples = new List<(int AdapterIndex, string InstanceName, double Utilization)>(_cachedGpuCounters.Count);
                        foreach (var item in _cachedGpuCounters)
                        {
                            try
                            {
                                double val = item.Counter.NextValue();
                                int targetIdx = item.PhysIndex < gpuCount ? item.PhysIndex : 0;
                                pdhSamples.Add((targetIdx, item.InstanceName, val));
                            }
                            catch { }
                        }

                        foreach (var kv in GpuTelemetryHelper.AggregateByAdapterIndex(pdhSamples))
                        {
                            gpuLoads[kv.Key] = kv.Value;
                        }
                    }
                    catch { }
                }

                if (_cachedAdapters.Count > 0)
                {
                    for (int i = 0; i < _cachedAdapters.Count; i++)
                    {
                        var ad = _cachedAdapters[i];
                        bool matchedKernel = kernelSnap != null && !string.IsNullOrEmpty(kernelSnap.GpuDevice) &&
                            string.Equals(ad.Description, kernelSnap.GpuDevice, StringComparison.OrdinalIgnoreCase);
                        double load = (matchedKernel && kernelSnap?.GpuUtilPct.HasValue == true)
                            ? Math.Round(Math.Clamp(kernelSnap.GpuUtilPct.Value, 0.0, 100.0), 1)
                            : (gpuLoads.TryGetValue(i, out var l) ? Math.Clamp(Math.Round(l, 1), 0.0, 100.0) : 0);

                        double vramUsedMb = ad.DedicatedVramUsageBytes > 0
                            ? Math.Round(ad.DedicatedVramUsageBytes / (1024.0 * 1024.0), 0)
                            : (matchedKernel && kernelSnap?.GpuVramUsedMb.HasValue == true ? kernelSnap.GpuVramUsedMb.Value : 0);

                        gpus.Add(new GpuMetric
                        {
                            Name = ad.Description,
                            Utilization = load,
                            PhysicalIndex = i,
                            DedicatedMemoryUsedMb = vramUsedMb,
                            DedicatedMemoryTotalMb = ad.DedicatedVideoMemoryBytes / (1024.0 * 1024.0),
                            TemperatureC = matchedKernel && kernelSnap?.GpuTempC is > 0.1 and < 150.0
                                ? Math.Round(kernelSnap.GpuTempC.Value, 1)
                                : 0
                        });
                    }
                }
                else
                {
                    for (int i = 0; i < _cachedGpuNames.Count; i++)
                    {
                        double load = gpuLoads.TryGetValue(i, out var l) ? Math.Min(100.0, Math.Round(l, 1)) : 0;
                        gpus.Add(new GpuMetric
                        {
                            Name = _cachedGpuNames[i],
                            Utilization = load,
                            PhysicalIndex = i,
                            DedicatedMemoryTotalMb = 0
                        });
                    }
                }
            }
            catch
            {
            }
            return gpus;
        }
    }

    public class LiveMetricsPayload
    {
        public double CpuUtilization { get; set; }
        public double CpuFrequencyGhz { get; set; }
        public double CpuTemperatureC { get; set; }
        public bool HasCpuTemperature { get; set; }
        public string? CpuTemperatureSource { get; set; }
        public double? SsdTemperatureC { get; set; }
        public string? SsdTemperatureSource { get; set; }
        public double? DpcLatencyUs { get; set; }
        public ulong RamTotalMb { get; set; }
        public ulong RamAvailableMb { get; set; }
        public ulong RamUsedMb { get; set; }
        public double RamPercentage { get; set; }
        public double DiskActivePercentage { get; set; }
        public double DiskReadBytesPerSec { get; set; }
        public double DiskWriteBytesPerSec { get; set; }
        public double StoragePercentage { get; set; }
        public string StorageInfo { get; set; } = "";
        public double NetworkDownloadBps { get; set; }
        public double NetworkUploadBps { get; set; }
        public string PowerPlan { get; set; } = "";
        public int ProcessCount { get; set; }
        public int ThreadCount { get; set; }
        public List<GpuMetric> Gpus { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    public class GpuMetric
    {
        public string Name { get; set; } = "";
        public double Utilization { get; set; }
        public int PhysicalIndex { get; set; }
        public double DedicatedMemoryUsedMb { get; set; }
        public double DedicatedMemoryTotalMb { get; set; }
        public double SharedMemoryUsedMb { get; set; }
        public double TemperatureC { get; set; }
    }
}
