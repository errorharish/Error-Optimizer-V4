using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Storage;
using BiosOptimizer.IPC.Contracts;

namespace ErrorOptimizerPrototypeHost.Services;

/// <summary>
/// Provides real hardware discovery, one-shot <c>system:scan</c> snapshots, and continuous <c>telemetry</c>
/// samples from <c>BiosOptimizer.Service</c> IPC (when running) plus native Windows/DXGI/WMI/Core sensors.
/// Sensors that hardware or Windows do not expose are reported strictly as <c>null</c>.
/// </summary>
public sealed class SystemTelemetryService : IDisposable
{
    private readonly ServiceIpcClient _ipc;
    private readonly Action<string> _postJson;
    private readonly object _subLock = new();
    private readonly object _cpuTimeLock = new();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);

    private CancellationTokenSource? _telemetryCts;
    private Task? _telemetryTask;
    private int _intervalMs = 3000;
    private int _subscriberCount;
    private bool _disposed;
    private long _telemetrySeq;
    private DateTime _lastOfflineProbeUtc = DateTime.MinValue;
    private DateTime _lastAcpiMissUtc = DateTime.MinValue;
    private DateTime _lastSsdMissUtc = DateTime.MinValue;
    private DateTime _lastWmiGpuMissUtc = DateTime.MinValue;

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasPrevCpuTimes;

    public SystemTelemetryService(ServiceIpcClient ipc, Action<string> postJson)
    {
        _ipc = ipc;
        _postJson = postJson;
        TryPrimeCpuTimes();
        try { _ = HardwareTelemetryCollector.Instance.CollectSnapshot(); } catch { }
    }

    public void Subscribe(int? requestedIntervalMs, bool forceInterval = false)
    {
        if (_disposed) return;

        int? clamped = requestedIntervalMs.HasValue
            ? Math.Clamp(requestedIntervalMs.Value, 500, 10000)
            : null;

        bool alreadyRunning = false;
        bool startedNew = false;
        lock (_subLock)
        {
            _subscriberCount++;
            if (clamped.HasValue)
            {
                if (forceInterval || _subscriberCount <= 1 || clamped.Value < _intervalMs)
                    _intervalMs = clamped.Value;
            }
            if (_telemetryTask != null && !_telemetryTask.IsCompleted)
            {
                alreadyRunning = true;
            }
            else
            {
                startedNew = true;
                HardwareTelemetryCollector.Instance.AcquireEtwSubscription();

                _telemetryCts?.Dispose();
                _telemetryCts = new CancellationTokenSource();
                CancellationToken ct = _telemetryCts.Token;
                _telemetryTask = Task.Run(() => RunTelemetryLoopAsync(ct), ct);
            }
        }

        if (startedNew)
        {
            _ = _ipc.SendAsync(IpcMessageType.SetTelemetrySubscriptionActive, "{\"active\":true}", 1500, CancellationToken.None);
        }

        if (alreadyRunning)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = await SampleTelemetryAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!_disposed && payload != null)
                        _postJson(JsonSerializer.Serialize(payload));
                }
                catch { }
            });
        }
    }

    public void Unsubscribe()
    {
        bool stopped = false;
        lock (_subLock)
        {
            if (_subscriberCount > 0)
                _subscriberCount--;
            if (_subscriberCount > 0)
                return;

            stopped = StopTelemetryLoopLocked();
        }

        if (stopped)
        {
            _ = _ipc.SendAsync(IpcMessageType.SetTelemetrySubscriptionActive, "{\"active\":false}", 1500, CancellationToken.None);
        }
    }

    public void ResetSubscriptions()
    {
        bool stopped;
        lock (_subLock)
        {
            _subscriberCount = 0;
            stopped = StopTelemetryLoopLocked();
        }

        if (stopped)
        {
            _ = _ipc.SendAsync(IpcMessageType.SetTelemetrySubscriptionActive, "{\"active\":false}", 1500, CancellationToken.None);
        }
    }

    private bool StopTelemetryLoopLocked()
    {
        bool hadLoop = _telemetryTask != null || _telemetryCts != null;
        HardwareTelemetryCollector.Instance.ReleaseEtwSubscription();

        try { _telemetryCts?.Cancel(); } catch { }
        _telemetryCts?.Dispose();
        _telemetryCts = null;
        _telemetryTask = null;
        return hadLoop;
    }

    private async Task RunTelemetryLoopAsync(CancellationToken ct)
    {
        // Brief initial sampling window so first CPU delta is real rather than 0%.
        if (!_hasPrevCpuTimes)
            TryPrimeCpuTimes();
        await Task.Delay(250, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var payload = await SampleTelemetryAsync(ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    _postJson(JsonSerializer.Serialize(payload));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient sensor read errors should not tear down the subscription loop.
            }

            int delayMs;
            lock (_subLock)
                delayMs = _intervalMs;

            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<object> BuildScanReplyAsync(string requestId, CancellationToken ct)
    {
        void EmitProgress(int pct)
        {
            if (ct.IsCancellationRequested) return;
            _postJson(JsonSerializer.Serialize(new { id = requestId, progress = pct }));
        }

        EmitProgress(10);
        if (!_hasPrevCpuTimes)
            TryPrimeCpuTimes();

        // Briefly acquire ETW DPC subscription for the duration of this scan so elevated one-shot
        // health/dashboard scans capture real kernel DPC samples even before live telemetry subscribes.
        bool acquiredTempEtw = false;
        try
        {
            HardwareTelemetryCollector.Instance.AcquireEtwSubscription();
            acquiredTempEtw = true;
        }
        catch { }

        try
        {
            ServiceMetricsSnapshot? ipcMetrics = await TryFetchServiceMetricsAsync(ct).ConfigureAwait(false);
            var quickProfile = await Task.Run(() => HardwareProfiler.GetQuickProfile(forceRefresh: false), ct).ConfigureAwait(false);
            var hwSnap = await Task.Run(() => HardwareDetectionService.Instance.GetSnapshot(forceRefresh: false), ct).ConfigureAwait(false);

            EmitProgress(28);
            var mem = ReadMemoryGb();
            string? ramSpec = !string.IsNullOrWhiteSpace(hwSnap.Memory?.InstalledFormatted)
                ? hwSnap.Memory.InstalledFormatted
                : (quickProfile.RamTotalGb > 0 ? $"{quickProfile.RamTotalGb:F0} GB Physical Memory" : null);

            EmitProgress(46);
            List<DxgiAdapterInfo> dxgi = new();
            try { dxgi = DxgiGpuReader.EnumerateAdapters(); } catch { }
            DxgiAdapterInfo? primaryGpu = dxgi.FirstOrDefault(a => !a.IsSoftwareAdapter && a.IsDedicatedGpu)
                                          ?? dxgi.FirstOrDefault(a => !a.IsSoftwareAdapter)
                                          ?? dxgi.FirstOrDefault();

            double? vramTotalGb = primaryGpu != null && primaryGpu.DedicatedVideoMemoryBytes > 0
                ? Math.Round(primaryGpu.DedicatedVideoMemoryBytes / (1024.0 * 1024.0 * 1024.0), 2)
                : (ipcMetrics?.GpuVramTotalMb > 0 ? Math.Round(ipcMetrics.GpuVramTotalMb.Value / 1024.0, 2) : (double?)null);
            double? vramUsedGb = primaryGpu != null && primaryGpu.DedicatedVramUsageBytes > 0
                ? Math.Round(primaryGpu.DedicatedVramUsageBytes / (1024.0 * 1024.0 * 1024.0), 2)
                : (ipcMetrics?.GpuVramUsedMb > 0 ? Math.Round(ipcMetrics.GpuVramUsedMb.Value / 1024.0, 2) : (double?)null);
            string? gpuModel = !string.IsNullOrWhiteSpace(primaryGpu?.Description)
                ? primaryGpu.Description
                : (!string.IsNullOrWhiteSpace(ipcMetrics?.GpuName)
                    ? ipcMetrics.GpuName
                    : (quickProfile.GpuPresent && !string.IsNullOrWhiteSpace(quickProfile.GpuName) ? quickProfile.GpuName : null));

            if (string.IsNullOrWhiteSpace(ramSpec) && !string.IsNullOrWhiteSpace(hwSnap.Memory?.SpecFormatted) &&
                !string.Equals(hwSnap.Memory.SpecFormatted, "System Memory", StringComparison.OrdinalIgnoreCase))
            {
                ramSpec = hwSnap.Memory.SpecFormatted;
            }

            EmitProgress(64);
            var storageDrives = await Task.Run(() =>
            {
                try { return new StorageEngine().GetDrives(); } catch { return new List<StorageDriveInfo>(); }
            }, ct).ConfigureAwait(false);
            var storageAggregate = StorageEngine.ComputeUnifiedAggregate(storageDrives);
            StorageDriveInfo? sysDrive = storageDrives.FirstOrDefault(d => d.IsSystemDrive)
                                         ?? storageDrives.FirstOrDefault();

            EmitProgress(82);
            var kernelSnap = await TryFetchKernelTelemetryAsync(ct).ConfigureAwait(false);

            double? storageHealthPct = ComputeStorageHealthPct(storageAggregate, sysDrive, kernelSnap);
            var resolved = ResolveTelemetryValues(ipcMetrics, kernelSnap);
            DateTime sampleUtc = DateTime.UtcNow;
            var reconciledSensors = ReconcileSensors(kernelSnap, resolved, sampleUtc);
            double? cpuLoad = resolved.CpuLoad;
            double? cpuTemp = resolved.CpuTemp;
            double? ssdTemp = resolved.SsdTemp;
            double? timerMs = ReadTimerResolutionMs();
            int? bgProcs = ipcMetrics?.ProcessCount > 0 ? ipcMetrics.ProcessCount : ReadProcessCount();

            vramTotalGb ??= kernelSnap.GpuVramTotalMb > 0 ? Math.Round(kernelSnap.GpuVramTotalMb.Value / 1024.0, 2) : null;
            vramUsedGb ??= kernelSnap.GpuVramUsedMb > 0 ? Math.Round(kernelSnap.GpuVramUsedMb.Value / 1024.0, 2) : null;

            EmitProgress(94);

            double? totalRamGb = mem.TotalGb ?? (ipcMetrics?.RamTotalMb > 0 ? Math.Round(ipcMetrics.RamTotalMb.Value / 1024.0, 1) : (double?)null);
            double? usedRamGb = mem.UsedGb ?? (ipcMetrics?.RamUsedMb > 0 ? Math.Round(ipcMetrics.RamUsedMb.Value / 1024.0, 1) : (double?)null);
            double? installedRamGb = hwSnap.Memory?.InstalledPhysicalGb > 0 ? hwSnap.Memory.InstalledPhysicalGb : null;
            double? reservedRamGb = hwSnap.Memory?.HardwareReservedGb;

            int? cores = hwSnap.Cpu?.PhysicalCores > 0 ? hwSnap.Cpu.PhysicalCores : null;
            int? threads = hwSnap.Cpu?.LogicalProcessors > 0 ? hwSnap.Cpu.LogicalProcessors : Environment.ProcessorCount;
            string? cpuModel = !string.IsNullOrWhiteSpace(hwSnap.Cpu?.Name)
                ? hwSnap.Cpu.Name
                : (!string.IsNullOrWhiteSpace(quickProfile.CpuModel) ? quickProfile.CpuModel : null);

            return new
            {
                id = requestId,
                ok = true,
                timestampUtc = sampleUtc.ToString("O"),
                readings = new
                {
                    cpu = new
                    {
                        model = cpuModel,
                        cores,
                        threads,
                        loadPct = cpuLoad,
                        tempC = cpuTemp,
                        clockMhz = kernelSnap.CpuClockMhz,
                        maxClockMhz = kernelSnap.CpuMaxClockMhz,
                        perCoreLoadPct = kernelSnap.PerCoreCpuPct
                    },
                    ram = new
                    {
                        totalGb = totalRamGb,
                        usedGb = usedRamGb,
                        installedGb = installedRamGb,
                        reservedGb = reservedRamGb,
                        commitUsedGb = kernelSnap.CommitUsedGb,
                        commitTotalGb = kernelSnap.CommitTotalGb,
                        commitPct = kernelSnap.CommitPct,
                        spec = ramSpec
                    },
                    gpu = new
                    {
                        model = gpuModel,
                        loadPct = resolved.GpuLoad,
                        tempC = resolved.GpuTemp,
                        hotspotC = resolved.GpuHotspot,
                        vramUsedGb,
                        vramTotalGb,
                        clockMhz = kernelSnap.GpuClockMhz,
                        powerWatts = kernelSnap.GpuPowerWatts
                    },
                    storage = new
                    {
                        model = storageAggregate.VolumeCount > 1
                            ? (!string.IsNullOrWhiteSpace(sysDrive?.Model) ? $"{sysDrive.Model} · {storageAggregate.ScopeLabel}" : storageAggregate.ScopeLabel)
                            : (sysDrive?.Model ?? storageAggregate.ScopeLabel),
                        driveLetter = storageAggregate.VolumeCount > 1 ? storageAggregate.VolumeLettersSummary : sysDrive?.Letter,
                        volumeScope = storageAggregate.ScopeLabel,
                        volumeCount = storageAggregate.VolumeCount,
                        usedGb = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedUsedGb : (double?)null,
                        freeGb = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedFreeGb : (double?)null,
                        totalGb = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedTotalGb : (double?)null,
                        usagePct = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedUsagePercent : (double?)null,
                        unifiedTotalBytes = storageAggregate.UnifiedTotalBytes,
                        unifiedUsedBytes = storageAggregate.UnifiedUsedBytes,
                        unifiedFreeBytes = storageAggregate.UnifiedFreeBytes,
                        volumes = storageDrives.Where(d => d.IsFixedLocalDrive).Select(d => new
                        {
                            letter = d.Letter,
                            label = d.VolumeLabel,
                            model = d.Model,
                            usedGb = d.UsedGb,
                            freeGb = d.FreeGb,
                            totalGb = d.TotalGb,
                            usagePct = d.UsagePercentage,
                            healthStatus = d.HealthStatus,
                            smartStatus = d.SmartStatus,
                            temperature = d.TemperatureText
                        }).ToArray(),
                        physicalDiskTotalGb = sysDrive?.PhysicalDiskTotalGb > 0 ? sysDrive.PhysicalDiskTotalGb : (double?)null,
                        allFixedUsedGb = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedUsedGb : (double?)null,
                        allFixedFreeGb = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedFreeGb : (double?)null,
                        allFixedTotalGb = storageAggregate.VolumeCount > 0 ? storageAggregate.UnifiedTotalGb : (double?)null,
                        driveCount = storageAggregate.VolumeCount,
                        healthPct = storageHealthPct,
                        smartStatus = storageAggregate.SmartStatusSummary,
                        wearUsedPct = kernelSnap.NvmeWearUsedPct,
                        availableSparePct = kernelSnap.NvmeAvailableSparePct,
                        activePct = kernelSnap.DiskActivePct,
                        readKbps = kernelSnap.DiskReadKbps,
                        writeKbps = kernelSnap.DiskWriteKbps,
                        queueLength = kernelSnap.DiskQueueLength,
                        tempC = ssdTemp
                    },
                    latency = new
                    {
                        dpcUs = resolved.DpcUs,
                        dpcMaxUs = kernelSnap.DpcMaxUs,
                        isrMaxUs = kernelSnap.IsrMaxUs,
                        timerMs
                    },
                    sensors = reconciledSensors,
                    network = new
                    {
                        rxKbps = kernelSnap.NetworkRxKbps,
                        txKbps = kernelSnap.NetworkTxKbps,
                        adapter = kernelSnap.NetworkAdapterName,
                        linkMbps = kernelSnap.NetworkLinkMbps
                    },
                    power = new
                    {
                        onAc = kernelSnap.OnAcPower,
                        hasBattery = kernelSnap.HasBattery,
                        batteryPct = kernelSnap.BatteryPercent,
                        batteryLifeMinutes = kernelSnap.BatteryLifeMinutes,
                        powerSaver = kernelSnap.PowerSaverActive
                    },
                    uptimeSeconds = kernelSnap.UptimeSeconds,
                    sources = new
                    {
                        dpc = kernelSnap.DpcSource,
                        cpuTemp = resolved.CpuTempSource,
                        gpuTemp = resolved.GpuTempSource,
                        ssdTemp = resolved.SsdTempSource,
                        storageHealth = kernelSnap.StorageHealthSource,
                        collectorErrors = kernelSnap.CollectorErrors
                    },
                    backgroundProcesses = bgProcs
                }
            };
        }
        finally
        {
            if (acquiredTempEtw)
            {
                try { HardwareTelemetryCollector.Instance.ReleaseEtwSubscription(); } catch { }
            }
        }
    }

    private static double? ComputeStorageHealthPct(
        UnifiedStorageAggregate agg,
        StorageDriveInfo? sysDrive,
        KernelTelemetrySnapshot kernelSnap)
    {
        if (kernelSnap.NvmeCriticalWarning.HasValue && kernelSnap.NvmeCriticalWarning.Value != 0)
            return 25.0;
        if (agg.PhysicalHealthPct.HasValue)
        {
            if (kernelSnap.NvmeWearUsedPct.HasValue)
            {
                double wearHealth = Math.Round(Math.Clamp(100.0 - kernelSnap.NvmeWearUsedPct.Value, 0.0, 100.0), 1);
                return Math.Min(agg.PhysicalHealthPct.Value, wearHealth);
            }
            return agg.PhysicalHealthPct.Value;
        }
        if (kernelSnap.NvmeWearUsedPct.HasValue)
            return Math.Round(Math.Clamp(100.0 - kernelSnap.NvmeWearUsedPct.Value, 0.0, 100.0), 1);
        if (sysDrive == null || string.IsNullOrWhiteSpace(sysDrive.SmartStatus))
            return null;
        return sysDrive.SmartStatus.ToUpperInvariant() switch
        {
            "OK" => 100.0,
            "PREDICT_FAILURE" => 25.0,
            _ => null
        };
    }

    private sealed record ResolvedTelemetryValues(
        double? CpuLoad,
        double? CpuTemp,
        string CpuTempSource,
        double? GpuLoad,
        double? GpuTemp,
        string GpuTempSource,
        double? GpuHotspot,
        double? SsdTemp,
        string SsdTempSource,
        double? DpcUs);

    private ResolvedTelemetryValues ResolveTelemetryValues(ServiceMetricsSnapshot? ipcMetrics, KernelTelemetrySnapshot kernelSnap)
    {
        double? cpuLoad = ipcMetrics?.CpuPct ?? ReadCpuLoadPercent();

        double? cpuTemp = NormalizeValidTemp(kernelSnap.CpuTempC);
        string cpuTempSrc = kernelSnap.CpuTempSource ?? "Kernel Collector";
        if (!cpuTemp.HasValue)
        {
            cpuTemp = NormalizeValidTemp(ipcMetrics?.CpuTempC);
            if (cpuTemp.HasValue)
            {
                cpuTempSrc = !string.IsNullOrWhiteSpace(ipcMetrics?.CpuTempSource)
                    ? ipcMetrics!.CpuTempSource!
                    : "BiosOptimizer.Service IPC";
            }
            else
            {
                cpuTemp = ReadAcpiCpuTempC();
                cpuTempSrc = cpuTemp.HasValue ? "MSAcpi_ThermalZoneTemperature" : (kernelSnap.CpuTempSource ?? "Unavailable");
            }
        }

        var gpuSensors = ReadGpuSensors(ipcMetrics, kernelSnap);
        string gpuTempSrc = !string.IsNullOrWhiteSpace(gpuSensors.Source)
            ? gpuSensors.Source
            : (kernelSnap.GpuTempSource ?? "Unavailable");

        double? ssdTemp = NormalizeValidTemp(kernelSnap.SsdTempC);
        string ssdTempSrc = kernelSnap.SsdTempSource ?? "Kernel Collector";
        if (!ssdTemp.HasValue)
        {
            ssdTemp = NormalizeValidTemp(ipcMetrics?.SsdTempC);
            if (ssdTemp.HasValue)
            {
                ssdTempSrc = !string.IsNullOrWhiteSpace(ipcMetrics?.SsdTempSource)
                    ? ipcMetrics!.SsdTempSource!
                    : "BiosOptimizer.Service IPC";
            }
            else
            {
                ssdTemp = ReadSsdTempC();
                ssdTempSrc = ssdTemp.HasValue ? "MSFT_StorageReliabilityCounter" : (kernelSnap.SsdTempSource ?? "Unavailable");
            }
        }

        double? dpcUs = kernelSnap.DpcUs ?? ipcMetrics?.DpcLatencyUs;
        return new ResolvedTelemetryValues(
            cpuLoad, cpuTemp, cpuTempSrc,
            gpuSensors.LoadPct, gpuSensors.TempC, gpuTempSrc, gpuSensors.HotspotC,
            ssdTemp, ssdTempSrc, dpcUs);
    }

    private static Dictionary<string, SensorReadingMetadata> ReconcileSensors(
        KernelTelemetrySnapshot kernelSnap,
        ResolvedTelemetryValues resolved,
        DateTime sampleUtc)
    {
        var sensors = new Dictionary<string, SensorReadingMetadata>(StringComparer.OrdinalIgnoreCase);
        if (kernelSnap.Sensors != null)
        {
            foreach (var kv in kernelSnap.Sensors)
            {
                sensors[kv.Key] = new SensorReadingMetadata
                {
                    Device = kv.Value.Device,
                    SensorId = kv.Value.SensorId,
                    Unit = kv.Value.Unit,
                    Value = kv.Value.Value,
                    TimestampUtc = kv.Value.TimestampUtc,
                    Provider = kv.Value.Provider,
                    Availability = kv.Value.Availability,
                    Reason = kv.Value.Reason
                };
            }
        }

        void SyncSensor(string key, string device, string sensorId, double? val, string src, string missingReason)
        {
            sensors.TryGetValue(key, out var existing);
            sensors[key] = new SensorReadingMetadata
            {
                Device = existing != null && !string.IsNullOrWhiteSpace(existing.Device) ? existing.Device : device,
                SensorId = existing != null && !string.IsNullOrWhiteSpace(existing.SensorId) ? existing.SensorId : sensorId,
                Unit = existing != null && !string.IsNullOrWhiteSpace(existing.Unit) ? existing.Unit : "C",
                Value = val,
                TimestampUtc = sampleUtc,
                Provider = val.HasValue ? src : (existing?.Provider ?? "Unavailable"),
                Availability = val.HasValue ? "available" : (existing?.Availability == "unsupported" ? "unsupported" : "unavailable"),
                Reason = val.HasValue ? null : (existing?.Reason ?? missingReason)
            };
        }

        SyncSensor("cpuTemp", "CPU / ThermalZone", "cpu.temp", resolved.CpuTemp, resolved.CpuTempSource, "Hardware/ACPI thermal zone not exposed");
        SyncSensor("gpuTemp", kernelSnap.GpuDevice ?? "Primary GPU", "gpu.temp", resolved.GpuTemp, resolved.GpuTempSource, "GPU temperature sensor unavailable");
        SyncSensor("gpuHotspot", kernelSnap.GpuDevice ?? "Primary GPU", "gpu.hotspot", resolved.GpuHotspot, resolved.GpuTempSource, "GPU hotspot temperature requires vendor telemetry support");
        SyncSensor("ssdTemp", "System Storage", "ssd.temp", resolved.SsdTemp, resolved.SsdTempSource, "Drive temperature sensor unavailable");

        return sensors;
    }

    private async Task<object?> SampleTelemetryAsync(CancellationToken ct)
    {
        await _sampleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ServiceMetricsSnapshot? ipcMetrics = await TryFetchServiceMetricsAsync(ct).ConfigureAwait(false);
            var kernelSnap = await TryFetchKernelTelemetryAsync(ct).ConfigureAwait(false);
            var mem = ReadMemoryGb();
            DateTime sampleUtc = DateTime.UtcNow;
            var resolved = ResolveTelemetryValues(ipcMetrics, kernelSnap);
            var reconciledSensors = ReconcileSensors(kernelSnap, resolved, sampleUtc);
            double? timerMs = ReadTimerResolutionMs();
            int? bgProcs = ipcMetrics?.ProcessCount > 0 ? ipcMetrics.ProcessCount : ReadProcessCount();

            double? totalRamGb = mem.TotalGb ?? (ipcMetrics?.RamTotalMb > 0 ? Math.Round(ipcMetrics.RamTotalMb.Value / 1024.0, 1) : (double?)null);
            double? usedRamGb = mem.UsedGb ?? (ipcMetrics?.RamUsedMb > 0 ? Math.Round(ipcMetrics.RamUsedMb.Value / 1024.0, 1) : (double?)null);
            double? vramUsedMb = kernelSnap.GpuVramUsedMb ?? ipcMetrics?.GpuVramUsedMb;
            double? vramTotalMb = kernelSnap.GpuVramTotalMb ?? ipcMetrics?.GpuVramTotalMb;
            var storageAgg = StorageEngine.ComputeFastFixedVolumeAggregate();

            int activeIntervalMs;
            lock (_subLock)
                activeIntervalMs = _intervalMs;

            long seq = Interlocked.Increment(ref _telemetrySeq);

            return new
            {
                type = "telemetry",
                seq,
                timestampUtc = sampleUtc.ToString("O"),
                intervalMs = activeIntervalMs,
                source = ipcMetrics != null ? "service+host" : "host",
                cpu = new { loadPct = resolved.CpuLoad, tempC = resolved.CpuTemp, clockMhz = kernelSnap.CpuClockMhz, perCoreLoadPct = kernelSnap.PerCoreCpuPct },
                gpu = new { loadPct = resolved.GpuLoad, tempC = resolved.GpuTemp, hotspotC = resolved.GpuHotspot, vramUsedMb, vramTotalMb, clockMhz = kernelSnap.GpuClockMhz },
                ssd = new { tempC = resolved.SsdTemp, activePct = kernelSnap.DiskActivePct, readKbps = kernelSnap.DiskReadKbps, writeKbps = kernelSnap.DiskWriteKbps },
                storage = storageAgg.VolumeCount > 0 ? new
                {
                    usedGb = storageAgg.UnifiedUsedGb,
                    freeGb = storageAgg.UnifiedFreeGb,
                    totalGb = storageAgg.UnifiedTotalGb,
                    usagePct = storageAgg.UnifiedUsagePercent,
                    volumeCount = storageAgg.VolumeCount,
                    volumeScope = storageAgg.ScopeLabel
                } : null,
                latency = new { dpcUs = resolved.DpcUs, dpcMaxUs = kernelSnap.DpcMaxUs, timerMs },
                ram = new { usedGb = usedRamGb, totalGb = totalRamGb, commitPct = kernelSnap.CommitPct },
                network = new { rxKbps = kernelSnap.NetworkRxKbps, txKbps = kernelSnap.NetworkTxKbps },
                power = new { onAc = kernelSnap.OnAcPower, batteryPct = kernelSnap.BatteryPercent },
                uptimeSeconds = kernelSnap.UptimeSeconds,
                sensors = reconciledSensors,
                sources = new
                {
                    dpc = kernelSnap.DpcSource,
                    cpuTemp = resolved.CpuTempSource,
                    gpuTemp = resolved.GpuTempSource,
                    ssdTemp = resolved.SsdTempSource,
                    collectorErrors = kernelSnap.CollectorErrors
                },
                backgroundProcesses = bgProcs
            };
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    private async Task<KernelTelemetrySnapshot> TryFetchKernelTelemetryAsync(CancellationToken ct)
    {
        try
        {
            if (_ipc.State != BackendConnectionState.Offline)
            {
                var resp = await _ipc.SendAsync(IpcMessageType.GetKernelTelemetry, null, 1500, ct).ConfigureAwait(false);
                if (resp.Success && !string.IsNullOrWhiteSpace(resp.Data))
                {
                    var parsed = JsonSerializer.Deserialize<KernelTelemetrySnapshot>(resp.Data,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null) return parsed;
                }
            }
        }
        catch { }

        return await Task.Run(() => HardwareTelemetryCollector.Instance.CollectSnapshot(), ct).ConfigureAwait(false);
    }

    private async Task<ServiceMetricsSnapshot?> TryFetchServiceMetricsAsync(CancellationToken ct)
    {
        try
        {
            if (_ipc.State == BackendConnectionState.Offline)
            {
                if ((DateTime.UtcNow - _lastOfflineProbeUtc).TotalSeconds < 10)
                    return null;
                _lastOfflineProbeUtc = DateTime.UtcNow;
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(1500);
                var health = await _ipc.CheckHealthAsync(probeCts.Token).ConfigureAwait(false);
                if (!health.Healthy)
                    return null;
                bool hasSubs;
                lock (_subLock) hasSubs = _subscriberCount > 0;
                if (hasSubs)
                {
                    _ = _ipc.SendAsync(IpcMessageType.SetTelemetrySubscriptionActive, "{\"active\":true}", 1500, CancellationToken.None);
                }
            }

            IpcResponse resp = await _ipc.SendAsync(IpcMessageType.GetDashboard, null, 1500, ct).ConfigureAwait(false);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Data))
                return null;

            using var doc = JsonDocument.Parse(resp.Data);
            var root = doc.RootElement;

            double? cpuPct = TryGetDouble(root, "CpuUtilization");
            bool hasCpuTemp = root.TryGetProperty("HasCpuTemperature", out var hctEl) &&
                              (hctEl.ValueKind == JsonValueKind.True || hctEl.ValueKind == JsonValueKind.False) &&
                              hctEl.GetBoolean();
            double? rawCpuTemp = TryGetDouble(root, "CpuTemperatureC");
            double? cpuTemp = hasCpuTemp || (rawCpuTemp.HasValue && rawCpuTemp.Value > 0.1)
                ? NormalizeValidTemp(rawCpuTemp)
                : null;
            string? cpuTempSource = root.TryGetProperty("CpuTemperatureSource", out var ctsEl) && ctsEl.ValueKind == JsonValueKind.String
                ? ctsEl.GetString()
                : null;
            double? ssdTemp = NormalizeValidTemp(TryGetDouble(root, "SsdTemperatureC"));
            string? ssdTempSource = root.TryGetProperty("SsdTemperatureSource", out var stsEl) && stsEl.ValueKind == JsonValueKind.String
                ? stsEl.GetString()
                : null;
            double? dpcLatencyUs = TryGetDouble(root, "DpcLatencyUs");
            double? ramTotalMb = TryGetDouble(root, "RamTotalMb");
            double? ramUsedMb = TryGetDouble(root, "RamUsedMb");
            double? storagePct = TryGetDouble(root, "StoragePercentage");
            int? procCount = root.TryGetProperty("ProcessCount", out var pcEl) && pcEl.TryGetInt32(out int pc) ? pc : null;

            string? gpuName = null;
            double? gpuUtil = null;
            double? gpuTemp = null;
            double? gpuVramUsed = null;
            double? gpuVramTotal = null;

            if (root.TryGetProperty("Gpus", out var gpusEl) && gpusEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gpusEl.EnumerateArray())
                {
                    gpuName = g.TryGetProperty("Name", out var nEl) ? nEl.GetString() : null;
                    gpuUtil = TryGetDouble(g, "Utilization");
                    gpuTemp = NormalizeValidTemp(TryGetDouble(g, "TemperatureC"));
                    gpuVramUsed = TryGetDouble(g, "DedicatedMemoryUsedMb");
                    gpuVramTotal = TryGetDouble(g, "DedicatedMemoryTotalMb");
                    break;
                }
            }

            return new ServiceMetricsSnapshot(
                cpuPct, cpuTemp, cpuTempSource, ssdTemp, ssdTempSource, dpcLatencyUs,
                ramTotalMb, ramUsedMb, storagePct, procCount,
                gpuName, gpuUtil, gpuTemp, gpuVramUsed, gpuVramTotal);
        }
        catch
        {
            return null;
        }
    }

    private static double? TryGetDouble(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out double d) && double.IsFinite(d))
            return Math.Round(d, 1);
        return null;
    }

    private (double? LoadPct, double? TempC, double? HotspotC, string? Source) ReadGpuSensors(ServiceMetricsSnapshot? ipcMetrics, KernelTelemetrySnapshot? kernelSnap = null)
    {
        // Prefer direct NVIDIA NVML hardware counter when present, then aggregated WDDM/Service telemetry
        var nvml = NvmlProbe.TryReadPrimaryGpu();
        double? load = nvml.UtilizationPct
            ?? (kernelSnap?.GpuUtilPct.HasValue == true ? (double?)Math.Clamp(kernelSnap.GpuUtilPct.Value, 0, 100) : null)
            ?? (ipcMetrics?.GpuUtil.HasValue == true ? (double?)Math.Clamp(ipcMetrics.GpuUtil.Value, 0, 100) : null)
            ?? ReadWmiGpuUtilization();
        double? kTemp = NormalizeValidTemp(kernelSnap?.GpuTempC);
        double? iTemp = NormalizeValidTemp(ipcMetrics?.GpuTempC);
        double? nTemp = NormalizeValidTemp(nvml.TempC);
        double? temp = kTemp ?? iTemp ?? nTemp;
        string? source = kTemp.HasValue
            ? (kernelSnap?.GpuTempSource ?? "Kernel Collector")
            : iTemp.HasValue
                ? "BiosOptimizer.Service IPC"
                : nTemp.HasValue
                    ? "NVIDIA NVML"
                    : kernelSnap?.GpuTempSource;
        double? hotspot = NormalizeValidTemp(kernelSnap?.GpuHotspotC);

        return (load, temp, hotspot, source);
    }

    private static double? NormalizeValidTemp(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return null;
        // Values <= 1°C or >= 130°C represent unset 0°C sentinels or invalid thermal readings
        if (value.Value <= 1.0 || value.Value >= 130.0)
            return null;
        return Math.Round(value.Value, 1);
    }

    private void TryPrimeCpuTimes()
    {
        lock (_cpuTimeLock)
        {
            if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                _prevIdle = ToUInt64(idle);
                _prevKernel = ToUInt64(kernel);
                _prevUser = ToUInt64(user);
                _hasPrevCpuTimes = true;
            }
        }
    }

    private double? ReadCpuLoadPercent()
    {
        lock (_cpuTimeLock)
        {
            if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
                return null;

            ulong curIdle = ToUInt64(idle);
            ulong curKernel = ToUInt64(kernel);
            ulong curUser = ToUInt64(user);

            if (!_hasPrevCpuTimes)
            {
                _prevIdle = curIdle;
                _prevKernel = curKernel;
                _prevUser = curUser;
                _hasPrevCpuTimes = true;
                return null;
            }

            ulong idleDelta = curIdle >= _prevIdle ? curIdle - _prevIdle : 0;
            ulong kernelDelta = curKernel >= _prevKernel ? curKernel - _prevKernel : 0;
            ulong userDelta = curUser >= _prevUser ? curUser - _prevUser : 0;

            _prevIdle = curIdle;
            _prevKernel = curKernel;
            _prevUser = curUser;

            ulong total = kernelDelta + userDelta;
            if (total == 0)
                return null;

            double busy = total > idleDelta ? total - idleDelta : 0;
            double pct = Math.Clamp((busy / total) * 100.0, 0.0, 100.0);
            return Math.Round(pct, 1);
        }
    }

    private static (double? UsedGb, double? TotalGb) ReadMemoryGb()
    {
        var mem = new MEMORYSTATUSEX();
        if (!GlobalMemoryStatusEx(mem) || mem.ullTotalPhys == 0)
            return (null, null);

        double totalGb = Math.Round(mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGb = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0), 1);
        return (usedGb, totalGb);
    }

    private static double? ReadTimerResolutionMs()
    {
        try
        {
            int status = NtQueryTimerResolution(out _, out _, out uint current100Ns);
            if (status == 0 && current100Ns > 0)
                return Math.Round(current100Ns / 10000.0, 2);
        }
        catch { }
        return null;
    }

    private static int? ReadProcessCount()
    {
        try
        {
            var procs = Process.GetProcesses();
            int count = procs.Length;
            foreach (var p in procs) p.Dispose();
            return count;
        }
        catch
        {
            return null;
        }
    }

    private double? ReadWmiGpuUtilization()
    {
        if ((DateTime.UtcNow - _lastWmiGpuMissUtc).TotalSeconds < 30)
            return null;
        try
        {
            var adapters = DxgiGpuReader.EnumerateAdapters().Where(a => !a.IsSoftwareAdapter).ToList();
            string? preferredLuid = (adapters.FirstOrDefault(a => a.IsDedicatedGpu) ?? adapters.FirstOrDefault())?.AdapterLuidHex;

            var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"),
                opts);

            var samples = new List<(string? InstanceName, double Utilization)>();
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["UtilizationPercentage"] != null &&
                    double.TryParse(obj["UtilizationPercentage"].ToString(), out double u))
                {
                    samples.Add((obj["Name"]?.ToString(), u));
                }
            }

            double? util = GpuTelemetryHelper.AggregateWmiEngineUtilization(samples, preferredLuid);
            if (!util.HasValue)
                _lastWmiGpuMissUtc = DateTime.UtcNow;
            return util;
        }
        catch
        {
            _lastWmiGpuMissUtc = DateTime.UtcNow;
            return null;
        }
    }

    private double? ReadAcpiCpuTempC()
    {
        if ((DateTime.UtcNow - _lastAcpiMissUtc).TotalSeconds < 30)
            return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] != null &&
                    double.TryParse(obj["CurrentTemperature"].ToString(), out double tenthsK))
                {
                    double c = (tenthsK - 2732.0) / 10.0;
                    if (c > 1.0 && c < 130.0)
                        return Math.Round(c, 1);
                }
            }
        }
        catch { }
        _lastAcpiMissUtc = DateTime.UtcNow;
        return null;
    }

    private double? ReadSsdTempC()
    {
        if ((DateTime.UtcNow - _lastSsdMissUtc).TotalSeconds < 30)
            return null;
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT Temperature FROM MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["Temperature"] != null &&
                    double.TryParse(obj["Temperature"].ToString(), out double t) &&
                    t > 1.0 && t < 120.0)
                {
                    return Math.Round(t, 1);
                }
            }
        }
        catch { }
        _lastSsdMissUtc = DateTime.UtcNow;
        return null;
    }

    private static ulong ToUInt64(FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
    }

    private sealed record ServiceMetricsSnapshot(
        double? CpuPct,
        double? CpuTempC,
        string? CpuTempSource,
        double? SsdTempC,
        string? SsdTempSource,
        double? DpcLatencyUs,
        double? RamTotalMb,
        double? RamUsedMb,
        double? StoragePct,
        int? ProcessCount,
        string? GpuName,
        double? GpuUtil,
        double? GpuTempC,
        double? GpuVramUsedMb,
        double? GpuVramTotalMb);

    private static class NvmlProbe
    {
        private static int _initState; // 0 = untried, 1 = ok, -1 = unavailable
        private static readonly object _nvmlLock = new();

        public static (double? UtilizationPct, double? TempC) TryReadPrimaryGpu()
        {
            lock (_nvmlLock)
            {
                if (_initState == -1) return (null, null);

                try
                {
                    if (_initState == 0)
                    {
                        string? dllPath = ResolveNvmlPath();
                        if (string.IsNullOrEmpty(dllPath) || !NativeLibrary.TryLoad(dllPath, out _))
                        {
                            _initState = -1;
                            return (null, null);
                        }
                        if (nvmlInit_v2() != 0)
                        {
                            _initState = -1;
                            return (null, null);
                        }
                        _initState = 1;
                    }

                    if (nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0)
                        return (null, null);

                    if (nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device) != 0 || device == IntPtr.Zero)
                        return (null, null);

                    double? util = nvmlDeviceGetUtilizationRates(device, out NvmlUtilization u) == 0
                        ? Math.Clamp(u.Gpu, 0, 100)
                        : null;
                    double? temp = nvmlDeviceGetTemperature(device, 0, out uint t) == 0 && t > 0 && t < 150
                        ? t
                        : null;
                    return (util, temp);
                }
                catch
                {
                    _initState = -1;
                    return (null, null);
                }
            }
        }

        private static string? ResolveNvmlPath()
        {
            string sys = Path.Combine(Environment.SystemDirectory, "nvml.dll");
            if (File.Exists(sys)) return sys;
            string nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
            return File.Exists(nvsmi) ? nvsmi : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization { public uint Gpu; public uint Memory; }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minResolution, out uint maxResolution, out uint currentResolution);
}
