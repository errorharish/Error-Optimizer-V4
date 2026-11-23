#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace BiosOptimizer.Core.Implementations.Diagnostics;

public sealed class SensorReadingMetadata
{
    public string Device { get; set; } = "";
    public string SensorId { get; set; } = "";
    public string Unit { get; set; } = "";
    public double? Value { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Provider { get; set; } = "Unavailable";
    public string Availability { get; set; } = "unavailable"; // available | unavailable | unsupported | permission_denied | error
    public string? Reason { get; set; }
}

public sealed class KernelTelemetrySnapshot
{
    public double? DpcUs { get; set; }
    public double? DpcMaxUs { get; set; }
    public double? IsrMaxUs { get; set; }
    public int DpcSamples { get; set; }
    public bool EtwActive { get; set; }
    public string? DpcSource { get; set; }

    public double? SsdTempC { get; set; }
    public string? SsdTempSource { get; set; }
    public string? SsdDevice { get; set; }
    public double? NvmeWearUsedPct { get; set; }
    public double? NvmeAvailableSparePct { get; set; }
    public int? NvmeCriticalWarning { get; set; }
    public string? StorageHealthSource { get; set; }

    public double? CpuTempC { get; set; }
    public string? CpuTempSource { get; set; }
    public double[]? PerCoreCpuPct { get; set; }
    public double? CpuClockMhz { get; set; }
    public double? CpuMaxClockMhz { get; set; }

    public double? CommitUsedGb { get; set; }
    public double? CommitTotalGb { get; set; }
    public double? CommitPct { get; set; }

    public double? DiskActivePct { get; set; }
    public double? DiskReadKbps { get; set; }
    public double? DiskWriteKbps { get; set; }
    public int? DiskQueueLength { get; set; }

    public double? NetworkRxKbps { get; set; }
    public double? NetworkTxKbps { get; set; }
    public string? NetworkAdapterName { get; set; }
    public double? NetworkLinkMbps { get; set; }

    public bool? OnAcPower { get; set; }
    public bool? HasBattery { get; set; }
    public int? BatteryPercent { get; set; }
    public int? BatteryLifeMinutes { get; set; }
    public bool? PowerSaverActive { get; set; }

    public double? UptimeSeconds { get; set; }

    public double? GpuTempC { get; set; }
    public double? GpuHotspotC { get; set; }
    public string? GpuTempSource { get; set; }
    public string? GpuDevice { get; set; }
    public double? GpuUtilPct { get; set; }
    public string? GpuUtilSource { get; set; }
    public double? GpuVramUsedMb { get; set; }
    public double? GpuVramTotalMb { get; set; }
    public uint? GpuClockMhz { get; set; }
    public double? GpuPowerWatts { get; set; }

    public Dictionary<string, SensorReadingMetadata>? Sensors { get; set; }
    public Dictionary<string, string>? CollectorErrors { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Bounded on-demand ETW DPC/ISR trace sampler + inbox Windows storage IOCTL / NVMe SMART &amp; vendor thermal collector.
/// Strictly returns <c>null</c> for unexposed sensors and never steals existing system loggers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareTelemetryCollector : IDisposable
{
    private static readonly Lazy<HardwareTelemetryCollector> _instance = new(() => new HardwareTelemetryCollector());
    public static HardwareTelemetryCollector Instance => _instance.Value;

    private const string EtwSessionName = "ErrorOptimizer_DpcSampler";
    private static readonly Guid PerfInfoProviderGuid = new("ce1dbfcf-4088-429a-9624-92df88937baa");
    private static readonly Guid SessionGuid = new("7d5983c5-9d8f-4e68-b4b6-3f1d0e8f2a10");

    private const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
    private const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private readonly object _etwLock = new();
    private readonly object _sampleLock = new();
    private readonly Queue<(DateTime utc, double us, bool isIsr)> _dpcWindow = new();
    private readonly Queue<KernelTelemetrySnapshot> _recentSnapshots = new();
    private int _subscriberCount;
    private ulong _sessionHandle;
    private ulong _traceHandle = ulong.MaxValue;
    private Thread? _traceThread;
    private bool _running;
    private bool _disposed;
    private EventRecordCallbackDelegate? _recordCallback;

    private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[]? _prevCoreTimes;
    private long _prevNetBytesRx;
    private long _prevNetBytesTx;
    private long _prevNetTicks;
    private bool _hasPrevNet;
    private int _nvmlLoadState; // 0 = untried, 1 = loaded, -1 = unavailable

    public IReadOnlyList<KernelTelemetrySnapshot> GetRecentSnapshots()
    {
        lock (_sampleLock)
        {
            return _recentSnapshots.ToArray();
        }
    }

    public void AcquireEtwSubscription()
    {
        if (_disposed) return;
        lock (_etwLock)
        {
            _subscriberCount++;
            if (!_running && SystemPowerOperations.IsCurrentProcessElevated())
                TryStartEtwSessionLocked();
        }
    }

    public void ReleaseEtwSubscription()
    {
        lock (_etwLock)
        {
            if (_subscriberCount > 0) _subscriberCount--;
            if (_subscriberCount == 0 && _running)
                StopEtwSessionLocked();
        }
    }

    public void StopAll()
    {
        lock (_etwLock)
        {
            _subscriberCount = 0;
            if (_running)
                StopEtwSessionLocked();
        }
    }

    public KernelTelemetrySnapshot CollectSnapshot()
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DateTime nowUtc = DateTime.UtcNow;

        var (dpcP95, dpcMax, isrMax, samples, etwActive) = ReadDpcMetrics();
        var (ssdTemp, ssdSrc, nvmeWear, nvmeSpare, nvmeCrit, nvmeHealthSrc, ssdDevice) = ReadStorageHealthAndTemperature(errors);
        var (cpuTemp, cpuSrc) = ReadCpuTemperature(errors);
        var gpuSnap = ReadGpuThermals(errors);
        var perCoreCpu = ReadPerCoreCpuLoad(errors);
        var (clockMhz, maxClockMhz) = ReadCpuClockMhz(errors);
        var (commitUsedGb, commitTotalGb, commitPct) = ReadCommitCharge(errors);
        var (diskActivePct, diskReadKbps, diskWriteKbps, diskQueueLen) = ReadDiskActivity(errors);
        var (netRxKbps, netTxKbps, netAdapter, netLinkMbps) = ReadNetworkThroughput(errors);
        var (onAc, hasBatt, battPct, battMins, powerSaver) = ReadPowerStatus(errors);
        double uptimeSec = Math.Round(Environment.TickCount64 / 1000.0, 0);

        var sensors = new Dictionary<string, SensorReadingMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["cpuTemp"] = new SensorReadingMetadata
            {
                Device = "CPU / ThermalZone",
                SensorId = "cpu.temp",
                Unit = "C",
                Value = cpuTemp,
                TimestampUtc = nowUtc,
                Provider = cpuSrc ?? "Windows ACPI / WMI ThermalZone",
                Availability = cpuTemp.HasValue
                    ? "available"
                    : (errors.ContainsKey("cpuTempAcpi") || errors.ContainsKey("cpuTempWmi") ? "unsupported" : "unavailable"),
                Reason = cpuTemp.HasValue ? null : (errors.TryGetValue("cpuTempAcpi", out var ce) ? ce : "Hardware/ACPI thermal zone not exposed without ring-0 driver")
            },
            ["gpuTemp"] = new SensorReadingMetadata
            {
                Device = gpuSnap.DeviceName ?? "Primary GPU",
                SensorId = "gpu.temp",
                Unit = "C",
                Value = gpuSnap.TempC,
                TimestampUtc = nowUtc,
                Provider = gpuSnap.Source ?? "NVIDIA NVML / WDDM",
                Availability = gpuSnap.TempC.HasValue ? "available" : (_nvmlLoadState == -1 ? "unsupported" : "unavailable"),
                Reason = gpuSnap.TempC.HasValue ? null : (errors.TryGetValue("gpuTempNvml", out var ge) ? ge : "NVML thermal provider unavailable for this GPU")
            },
            ["gpuHotspot"] = new SensorReadingMetadata
            {
                Device = gpuSnap.DeviceName ?? "Primary GPU",
                SensorId = "gpu.hotspot",
                Unit = "C",
                Value = gpuSnap.HotspotC,
                TimestampUtc = nowUtc,
                Provider = gpuSnap.HotspotC.HasValue ? (gpuSnap.Source ?? "GPU Sensor") : "Not exposed via public Windows API",
                Availability = gpuSnap.HotspotC.HasValue ? "available" : "unsupported",
                Reason = gpuSnap.HotspotC.HasValue ? null : "GPU hotspot temperature requires vendor telemetry support"
            },
            ["ssdTemp"] = new SensorReadingMetadata
            {
                Device = ssdDevice ?? "PhysicalDrive0",
                SensorId = "ssd.temp",
                Unit = "C",
                Value = ssdTemp,
                TimestampUtc = nowUtc,
                Provider = ssdSrc ?? "Windows Storage / NVMe IOCTL",
                Availability = ssdTemp.HasValue ? "available" : "unavailable",
                Reason = ssdTemp.HasValue ? null : (errors.TryGetValue("storageTemp", out var se) ? se : "Drive temperature sensor unavailable or permission denied")
            },
            ["dpcLatency"] = new SensorReadingMetadata
            {
                Device = "Kernel ETW PerfInfo",
                SensorId = "kernel.dpc_p95",
                Unit = "us",
                Value = dpcP95,
                TimestampUtc = nowUtc,
                Provider = "ETW Kernel PerfInfo DPC",
                Availability = etwActive && dpcP95.HasValue ? "available" : (SystemPowerOperations.IsCurrentProcessElevated() ? "unavailable" : "permission_denied"),
                Reason = etwActive && dpcP95.HasValue ? null : (SystemPowerOperations.IsCurrentProcessElevated() ? "Awaiting DPC trace window" : "Administrator privilege required for kernel ETW DPC trace")
            }
        };

        var snap = new KernelTelemetrySnapshot
        {
            DpcUs = dpcP95,
            DpcMaxUs = dpcMax,
            IsrMaxUs = isrMax,
            DpcSamples = samples,
            EtwActive = etwActive,
            DpcSource = etwActive && dpcP95.HasValue ? "ETW Kernel PerfInfo DPC" : null,
            SsdTempC = ssdTemp,
            SsdTempSource = ssdSrc,
            SsdDevice = ssdDevice,
            NvmeWearUsedPct = nvmeWear,
            NvmeAvailableSparePct = nvmeSpare,
            NvmeCriticalWarning = nvmeCrit,
            StorageHealthSource = nvmeHealthSrc,
            CpuTempC = cpuTemp,
            CpuTempSource = cpuSrc,
            PerCoreCpuPct = perCoreCpu,
            CpuClockMhz = clockMhz,
            CpuMaxClockMhz = maxClockMhz,
            CommitUsedGb = commitUsedGb,
            CommitTotalGb = commitTotalGb,
            CommitPct = commitPct,
            DiskActivePct = diskActivePct,
            DiskReadKbps = diskReadKbps,
            DiskWriteKbps = diskWriteKbps,
            DiskQueueLength = diskQueueLen,
            NetworkRxKbps = netRxKbps,
            NetworkTxKbps = netTxKbps,
            NetworkAdapterName = netAdapter,
            NetworkLinkMbps = netLinkMbps,
            OnAcPower = onAc,
            HasBattery = hasBatt,
            BatteryPercent = battPct,
            BatteryLifeMinutes = battMins,
            PowerSaverActive = powerSaver,
            UptimeSeconds = uptimeSec,
            GpuTempC = gpuSnap.TempC,
            GpuHotspotC = gpuSnap.HotspotC,
            GpuTempSource = gpuSnap.Source,
            GpuDevice = gpuSnap.DeviceName,
            GpuUtilPct = gpuSnap.UtilPct,
            GpuUtilSource = gpuSnap.UtilSource,
            GpuVramUsedMb = gpuSnap.VramUsedMb,
            GpuVramTotalMb = gpuSnap.VramTotalMb,
            GpuClockMhz = gpuSnap.ClockMhz,
            GpuPowerWatts = gpuSnap.PowerWatts,
            Sensors = sensors,
            CollectorErrors = errors.Count > 0 ? errors : null,
            TimestampUtc = nowUtc
        };

        lock (_sampleLock)
        {
            _recentSnapshots.Enqueue(snap);
            while (_recentSnapshots.Count > 30)
                _recentSnapshots.Dequeue();
        }

        return snap;
    }

    private (double? P95Us, double? MaxUs, double? IsrMaxUs, int Samples, bool Active) ReadDpcMetrics()
    {
        lock (_etwLock)
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-3);
            while (_dpcWindow.Count > 0 && _dpcWindow.Peek().utc < cutoff)
                _dpcWindow.Dequeue();

            if (_dpcWindow.Count == 0)
                return (null, null, null, 0, _running);

            var dpcs = _dpcWindow.Where(x => !x.isIsr).Select(x => x.us).OrderBy(x => x).ToList();
            var isrs = _dpcWindow.Where(x => x.isIsr).Select(x => x.us).ToList();

            double? p95 = dpcs.Count > 0
                ? Math.Round(dpcs[Math.Clamp((int)Math.Floor(dpcs.Count * 0.95), 0, dpcs.Count - 1)], 1)
                : null;
            double? maxDpc = dpcs.Count > 0 ? Math.Round(dpcs[^1], 1) : null;
            double? maxIsr = isrs.Count > 0 ? Math.Round(isrs.Max(), 1) : null;

            return (p95, maxDpc, maxIsr, _dpcWindow.Count, _running);
        }
    }

    private void TryStartEtwSessionLocked()
    {
        try
        {
            StopStaleNamedSessionOnly();

            int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 1024;
            IntPtr propsBuf = Marshal.AllocHGlobal(propsSize);
            try
            {
                RtlZeroMemory(propsBuf, (IntPtr)propsSize);

                var props = new EVENT_TRACE_PROPERTIES
                {
                    BufferSizeField = (uint)propsSize,
                    Guid = SessionGuid,
                    ClientContext = 1, // QPC timestamps
                    Flags = WNODE_FLAG_TRACED_GUID,
                    BufferSize = 64,
                    MinimumBuffers = 2,
                    MaximumBuffers = 8,
                    LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE,
                    FlushTimer = 1,
                    EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT,
                    LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
                };

                Marshal.StructureToPtr(props, propsBuf, false);
                byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(EtwSessionName + "\0");
                Marshal.Copy(nameBytes, 0, IntPtr.Add(propsBuf, (int)props.LoggerNameOffset), nameBytes.Length);

                uint status = StartTraceW(out ulong sessionHandle, EtwSessionName, propsBuf);
                if (status != 0)
                    return;

                _sessionHandle = sessionHandle;
                QueryPerformanceFrequency(out long qpcFreq);
                if (qpcFreq <= 0) qpcFreq = 10_000_000;

                _recordCallback = recordPtr =>
                {
                    if (recordPtr == IntPtr.Zero) return;
                    var rec = Marshal.PtrToStructure<EVENT_RECORD>(recordPtr);
                    if (rec.EventHeader.ProviderId != PerfInfoProviderGuid) return;
                    byte op = rec.EventHeader.EventDescriptor.Opcode;
                    if (op is not (66 or 67 or 68)) return; // 66=DPC, 67=ISR, 68=TimerDPC
                    if (rec.UserData == IntPtr.Zero || rec.UserDataLength < 8) return;

                    long initialTime = Marshal.ReadInt64(rec.UserData);
                    long deltaTicks = rec.EventHeader.TimeStamp - initialTime;
                    if (deltaTicks <= 0) return;

                    double us = (deltaTicks * 1_000_000.0) / qpcFreq;
                    if (us is <= 0.1 or >= 250_000.0) return;

                    lock (_etwLock)
                    {
                        _dpcWindow.Enqueue((DateTime.UtcNow, us, op == 67));
                        while (_dpcWindow.Count > 2000)
                            _dpcWindow.Dequeue();
                    }
                };

                var logfile = new EVENT_TRACE_LOGFILEW
                {
                    LoggerName = EtwSessionName,
                    ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP,
                    EventRecordCallback = _recordCallback
                };

                _traceHandle = OpenTraceW(ref logfile);
                if (_traceHandle == ulong.MaxValue)
                {
                    StopTraceHandleLocked();
                    return;
                }

                _running = true;
                ulong localTrace = _traceHandle;
                _traceThread = new Thread(() =>
                {
                    try { ProcessTrace(new[] { localTrace }, 1, IntPtr.Zero, IntPtr.Zero); }
                    catch { }
                })
                {
                    IsBackground = true,
                    Name = "EO-EtwDpcTrace"
                };
                _traceThread.Start();
            }
            finally
            {
                Marshal.FreeHGlobal(propsBuf);
            }
        }
        catch
        {
            _running = false;
        }
    }

    private void StopEtwSessionLocked()
    {
        _running = false;
        if (_traceHandle != ulong.MaxValue)
        {
            try { CloseTrace(_traceHandle); } catch { }
            _traceHandle = ulong.MaxValue;
        }
        StopTraceHandleLocked();
        try { _traceThread?.Join(500); } catch { }
        _traceThread = null;
        _recordCallback = null;
        _dpcWindow.Clear();
    }

    private void StopTraceHandleLocked()
    {
        if (_sessionHandle == 0) return;
        int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 1024;
        IntPtr buf = Marshal.AllocHGlobal(propsSize);
        try
        {
            RtlZeroMemory(buf, (IntPtr)propsSize);
            Marshal.WriteInt32(buf, 0, propsSize);
            Marshal.WriteInt32(buf, 116, Marshal.SizeOf<EVENT_TRACE_PROPERTIES>());
            ControlTraceW(_sessionHandle, EtwSessionName, buf, EVENT_TRACE_CONTROL_STOP);
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(buf);
            _sessionHandle = 0;
        }
    }

    private static void StopStaleNamedSessionOnly()
    {
        int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 1024;
        IntPtr buf = Marshal.AllocHGlobal(propsSize);
        try
        {
            RtlZeroMemory(buf, (IntPtr)propsSize);
            Marshal.WriteInt32(buf, 0, propsSize);
            Marshal.WriteInt32(buf, 116, Marshal.SizeOf<EVENT_TRACE_PROPERTIES>());
            ControlTraceW(0, EtwSessionName, buf, EVENT_TRACE_CONTROL_STOP);
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static (double? TempC, string? TempSource, double? WearUsedPct, double? AvailableSparePct, int? CriticalWarning, string? HealthSource, string? DeviceName) ReadStorageHealthAndTemperature(
        Dictionary<string, string> errors)
    {
        double? bestTemp = null;
        string? tempSource = null;
        double? wearPct = null;
        double? sparePct = null;
        int? critWarn = null;
        string? healthSource = null;
        string? deviceName = null;

        // 1. Direct NVMe SMART Health Info Log Page 0x02 (PropertyId = 49) yields BOTH temp + wear + spare + critical warning
        for (int drive = 0; drive < 8; drive++)
        {
            var smart = QueryNvmeSmartLog(drive);
            if (smart.HasData)
            {
                bestTemp ??= smart.TempC;
                if (bestTemp.HasValue && tempSource == null) tempSource = "NVMe SMART Log Page 0x02 IOCTL";
                wearPct ??= smart.WearUsedPct;
                sparePct ??= smart.AvailableSparePct;
                critWarn ??= smart.CriticalWarning;
                healthSource ??= "NVMe SMART Log Page 0x02 IOCTL";
                deviceName ??= $"PhysicalDrive{drive}";
                break;
            }
        }

        // 2. Standard Windows StorageDeviceTemperatureProperty (PropertyId = 52)
        if (!bestTemp.HasValue)
        {
            for (int drive = 0; drive < 8; drive++)
            {
                double? t = QueryDriveTemperatureProperty(drive);
                if (t.HasValue)
                {
                    bestTemp = t.Value;
                    tempSource = "Windows Storage Temperature IOCTL";
                    deviceName ??= $"PhysicalDrive{drive}";
                    break;
                }
            }
        }

        // 3. WMI MSFT_StorageReliabilityCounter fallback
        if (!bestTemp.HasValue || !wearPct.HasValue)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = true };
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Temperature, Wear FROM MSFT_StorageReliabilityCounter"), opts);
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (!bestTemp.HasValue && obj["Temperature"] != null && double.TryParse(obj["Temperature"].ToString(), out double t) && t is > 0 and < 120)
                    {
                        bestTemp = Math.Round(t, 1);
                        tempSource ??= "WMI StorageReliabilityCounter";
                        deviceName ??= "System Storage Volume";
                    }
                    if (!wearPct.HasValue && obj["Wear"] != null && double.TryParse(obj["Wear"].ToString(), out double w) && w is >= 0 and <= 100)
                    {
                        wearPct = Math.Round(w, 1);
                        healthSource ??= "WMI StorageReliabilityCounter";
                    }
                }
            }
            catch (Exception ex)
            {
                if (!bestTemp.HasValue)
                    errors["storageTemp"] = ex.Message;
            }
        }

        return (bestTemp, tempSource, wearPct, sparePct, critWarn, healthSource, deviceName);
    }

    private static double? QueryDriveTemperatureProperty(int physicalDriveIndex)
    {
        IntPtr handle = CreateFile($"\\\\.\\PhysicalDrive{physicalDriveIndex}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return null;

        try
        {
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = 52, QueryType = 0 }; // StorageDeviceTemperatureProperty
            byte[] outBuf = new byte[1024];
            uint inSize = (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            IntPtr inPtr = Marshal.AllocHGlobal((int)inSize);
            try
            {
                Marshal.StructureToPtr(query, inPtr, false);
                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, inPtr, inSize, outBuf, (uint)outBuf.Length, out uint bytesReturned, IntPtr.Zero))
                    return null;
                if (bytesReturned < 24) return null;

                ushort infoCount = BitConverter.ToUInt16(outBuf, 12);
                if (infoCount == 0) return null;

                // STORAGE_TEMPERATURE_INFO[0] starts at offset 24 (Version[4]+Size[4]+Crit[2]+Warn[2]+Count[2]+Res0[2]+Res1[8] = 24)
                short tempC = BitConverter.ToInt16(outBuf, 26); // Index[2] + Temperature[2]
                if (tempC is > 0 and < 125)
                    return Math.Round((double)tempC, 1);
            }
            finally
            {
                Marshal.FreeHGlobal(inPtr);
            }
        }
        catch { }
        finally
        {
            CloseHandle(handle);
        }
        return null;
    }

    private static (bool HasData, double? TempC, double? WearUsedPct, double? AvailableSparePct, int? CriticalWarning) QueryNvmeSmartLog(int physicalDriveIndex)
    {
        IntPtr handle = CreateFile($"\\\\.\\PhysicalDrive{physicalDriveIndex}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return default;

        try
        {
            const int headerSize = 8 + 40; // STORAGE_PROPERTY_QUERY(8) + STORAGE_PROTOCOL_SPECIFIC_DATA(40)
            const int logLen = 512;
            byte[] buffer = new byte[headerSize + logLen];

            // STORAGE_PROPERTY_QUERY
            BitConverter.GetBytes(49).CopyTo(buffer, 0); // StorageAdapterProtocolSpecificProperty
            BitConverter.GetBytes(0).CopyTo(buffer, 4);  // PropertyStandardQuery

            // STORAGE_PROTOCOL_SPECIFIC_DATA starts at offset 8
            BitConverter.GetBytes(3).CopyTo(buffer, 8);       // ProtocolTypeNvme = 3
            BitConverter.GetBytes(2).CopyTo(buffer, 12);      // NVMeDataTypeLogPage = 2
            BitConverter.GetBytes(0x02).CopyTo(buffer, 16);   // Log Page 0x02 (SMART / Health)
            BitConverter.GetBytes(0).CopyTo(buffer, 20);      // SubValue
            BitConverter.GetBytes(40).CopyTo(buffer, 24);     // ProtocolDataOffset (from STORAGE_PROTOCOL_SPECIFIC_DATA start)
            BitConverter.GetBytes(logLen).CopyTo(buffer, 28); // ProtocolDataLength

            IntPtr bufPtr = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, bufPtr, buffer.Length);
                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, bufPtr, (uint)buffer.Length, bufPtr, (uint)buffer.Length, out uint retBytes, IntPtr.Zero))
                    return default;

                if (retBytes < headerSize + 6) return default;
                Marshal.Copy(bufPtr, buffer, 0, buffer.Length);

                int critWarn = buffer[headerSize + 0];
                ushort kelvin = BitConverter.ToUInt16(buffer, headerSize + 1);
                double celsius = kelvin - 273.15;
                double? tempC = celsius is > 0 and < 120 ? Math.Round(celsius, 1) : null;
                byte spare = buffer[headerSize + 3];
                byte used = buffer[headerSize + 5];
                double? sparePct = spare <= 100 ? spare : null;
                double? wearPct = used <= 255 ? used : null;
                return (true, tempC, wearPct, sparePct, critWarn);
            }
            finally
            {
                Marshal.FreeHGlobal(bufPtr);
            }
        }
        catch { }
        finally
        {
            CloseHandle(handle);
        }
        return default;
    }

    private static (double? TempC, string? Source) ReadCpuTemperature(Dictionary<string, string> errors)
    {
        try
        {
            var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"), new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"), opts);
            double? maxTemp = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] != null && double.TryParse(obj["CurrentTemperature"].ToString(), out double dk))
                {
                    double c = (dk - 2732.0) / 10.0;
                    if (c is > 5 and < 125)
                        maxTemp = !maxTemp.HasValue || c > maxTemp.Value ? c : maxTemp;
                }
            }
            if (maxTemp.HasValue) return (Math.Round(maxTemp.Value, 1), "ACPI ThermalZone");
        }
        catch (Exception ex) { errors["cpuTempAcpi"] = ex.Message; }

        try
        {
            var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery("SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"), opts);
            double? maxTemp = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["Temperature"] != null && double.TryParse(obj["Temperature"].ToString(), out double k))
                {
                    double c = k - 273.15;
                    if (c is > 5 and < 125)
                        maxTemp = !maxTemp.HasValue || c > maxTemp.Value ? c : maxTemp;
                }
            }
            if (maxTemp.HasValue) return (Math.Round(maxTemp.Value, 1), "WMI ThermalZone Counter");
        }
        catch (Exception ex) { errors["cpuTempWmi"] = ex.Message; }

        return (null, null);
    }

    private sealed record GpuSensorSample(
        double? TempC,
        double? HotspotC,
        string? Source,
        string? DeviceName,
        double? UtilPct,
        string? UtilSource,
        double? VramUsedMb,
        double? VramTotalMb,
        uint? ClockMhz,
        double? PowerWatts);

    private NvmlGetUtilizationDelegate? _nvmlGetUtil;
    private NvmlGetMemoryDelegate? _nvmlGetMem;
    private NvmlGetClockDelegate? _nvmlGetClock;
    private NvmlGetPowerDelegate? _nvmlGetPower;
    private NvmlGetTempDelegate? _nvmlGetTemp;
    private NvmlGetHandleDelegate? _nvmlGetHandle;

    private GpuSensorSample ReadGpuThermals(Dictionary<string, string> errors)
    {
        double? tempC = null;
        string? tempSrc = null;
        string? deviceName = null;
        double? utilPct = null;
        string? utilSrc = null;
        double? vramUsedMb = null;
        double? vramTotalMb = null;
        uint? clockMhz = null;
        double? powerWatts = null;

        try
        {
            var dxgi = DxgiGpuReader.EnumerateAdapters();
            if (dxgi.Count > 0)
            {
                var primary = dxgi.Find(a => a.IsDedicatedGpu) ?? dxgi[0];
                deviceName = primary.Description;
                if (primary.DedicatedVideoMemoryBytes > 0)
                    vramTotalMb = Math.Round(primary.DedicatedVideoMemoryBytes / (1024.0 * 1024.0), 0);
                if (primary.DedicatedVramUsageBytes > 0)
                    vramUsedMb = Math.Round(primary.DedicatedVramUsageBytes / (1024.0 * 1024.0), 0);
            }
        }
        catch { }

        if (_nvmlLoadState != -1)
        {
            try
            {
                if (_nvmlLoadState == 0)
                {
                    string? nvmlPath = ResolveNvmlPath();
                    if (string.IsNullOrEmpty(nvmlPath) || !NativeLibrary.TryLoad(nvmlPath, out IntPtr libHandle))
                    {
                        _nvmlLoadState = -1;
                    }
                    else
                    {
                        IntPtr pHandle = IntPtr.Zero;
                        IntPtr pTemp = IntPtr.Zero;
                        bool initOk = NativeLibrary.TryGetExport(libHandle, "nvmlInit_v2", out IntPtr pInit) &&
                                      NativeLibrary.TryGetExport(libHandle, "nvmlDeviceGetHandleByIndex_v2", out pHandle) &&
                                      NativeLibrary.TryGetExport(libHandle, "nvmlDeviceGetTemperature", out pTemp);
                        if (initOk && Marshal.GetDelegateForFunctionPointer<NvmlInitDelegate>(pInit)() == 0)
                        {
                            _nvmlGetHandle = Marshal.GetDelegateForFunctionPointer<NvmlGetHandleDelegate>(pHandle);
                            _nvmlGetTemp = Marshal.GetDelegateForFunctionPointer<NvmlGetTempDelegate>(pTemp);
                            if (NativeLibrary.TryGetExport(libHandle, "nvmlDeviceGetUtilizationRates", out IntPtr pUtil))
                                _nvmlGetUtil = Marshal.GetDelegateForFunctionPointer<NvmlGetUtilizationDelegate>(pUtil);
                            if (NativeLibrary.TryGetExport(libHandle, "nvmlDeviceGetMemoryInfo", out IntPtr pMem))
                                _nvmlGetMem = Marshal.GetDelegateForFunctionPointer<NvmlGetMemoryDelegate>(pMem);
                            if (NativeLibrary.TryGetExport(libHandle, "nvmlDeviceGetClockInfo", out IntPtr pClk))
                                _nvmlGetClock = Marshal.GetDelegateForFunctionPointer<NvmlGetClockDelegate>(pClk);
                            if (NativeLibrary.TryGetExport(libHandle, "nvmlDeviceGetPowerUsage", out IntPtr pPwr))
                                _nvmlGetPower = Marshal.GetDelegateForFunctionPointer<NvmlGetPowerDelegate>(pPwr);
                            _nvmlLoadState = 1;
                        }
                        else
                        {
                            _nvmlLoadState = -1;
                        }
                    }
                }

                if (_nvmlLoadState == 1 && _nvmlGetHandle != null && _nvmlGetHandle(0, out IntPtr dev) == 0 && dev != IntPtr.Zero)
                {
                    if (_nvmlGetTemp != null && _nvmlGetTemp(dev, 0, out uint t) == 0 && t is > 5 and < 130)
                    {
                        tempC = t;
                        tempSrc = "NVIDIA NVML";
                    }
                    if (_nvmlGetUtil != null && _nvmlGetUtil(dev, out NVML_UTILIZATION u) == 0 && u.Gpu <= 100)
                    {
                        utilPct = u.Gpu;
                        utilSrc = "NVIDIA NVML";
                    }
                    if (_nvmlGetMem != null && _nvmlGetMem(dev, out NVML_MEMORY mem) == 0 && mem.Total > 0)
                    {
                        vramUsedMb = Math.Round(mem.Used / (1024.0 * 1024.0), 0);
                        vramTotalMb = Math.Round(mem.Total / (1024.0 * 1024.0), 0);
                    }
                    if (_nvmlGetClock != null && _nvmlGetClock(dev, 0, out uint mhz) == 0 && mhz > 0)
                        clockMhz = mhz;
                    if (_nvmlGetPower != null && _nvmlGetPower(dev, out uint mw) == 0 && mw > 0)
                        powerWatts = Math.Round(mw / 1000.0, 1);
                }
            }
            catch (Exception ex)
            {
                _nvmlLoadState = -1;
                errors["gpuTempNvml"] = ex.Message;
            }
        }

        return new GpuSensorSample(tempC, null, tempSrc, deviceName, utilPct, utilSrc, vramUsedMb, vramTotalMb, clockMhz, powerWatts);
    }

    private static string? ResolveNvmlPath()
    {
        string sys = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (File.Exists(sys)) return sys;
        string nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(nvsmi) ? nvsmi : null;
    }

    private double[]? ReadPerCoreCpuLoad(Dictionary<string, string> errors)
    {
        try
        {
            int count = Environment.ProcessorCount;
            if (count <= 0) return null;
            int structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
            int totalSize = structSize * count;
            IntPtr buf = Marshal.AllocHGlobal(totalSize);
            try
            {
                int status = NtQuerySystemInformation(8, buf, (uint)totalSize, out _);
                if (status != 0) return null;

                var current = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[count];
                for (int i = 0; i < count; i++)
                    current[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(IntPtr.Add(buf, i * structSize));

                lock (_sampleLock)
                {
                    if (_prevCoreTimes == null || _prevCoreTimes.Length != count)
                    {
                        _prevCoreTimes = current;
                        return null;
                    }

                    var loads = new double[count];
                    for (int i = 0; i < count; i++)
                    {
                        long idleDelta = Math.Max(0L, current[i].IdleTime - _prevCoreTimes[i].IdleTime);
                        long kernelDelta = Math.Max(0L, current[i].KernelTime - _prevCoreTimes[i].KernelTime);
                        long userDelta = Math.Max(0L, current[i].UserTime - _prevCoreTimes[i].UserTime);
                        long total = kernelDelta + userDelta;
                        if (total <= 0)
                        {
                            loads[i] = 0.0;
                            continue;
                        }
                        long busy = Math.Max(0L, total - idleDelta);
                        loads[i] = Math.Round(Math.Clamp((busy * 100.0) / total, 0.0, 100.0), 1);
                    }
                    _prevCoreTimes = current;
                    return loads;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            errors["perCoreCpu"] = ex.Message;
            return null;
        }
    }

    private static (double? CurrentMhz, double? MaxMhz) ReadCpuClockMhz(Dictionary<string, string> errors)
    {
        try
        {
            int count = Environment.ProcessorCount;
            int structSize = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
            int bufSize = structSize * Math.Max(1, count);
            IntPtr buf = Marshal.AllocHGlobal(bufSize);
            try
            {
                int status = CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)bufSize);
                if (status == 0)
                {
                    double sumCur = 0;
                    double maxMhz = 0;
                    int valid = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(IntPtr.Add(buf, i * structSize));
                        if (info.CurrentMhz > 0)
                        {
                            sumCur += info.CurrentMhz;
                            valid++;
                        }
                        if (info.MaxMhz > maxMhz) maxMhz = info.MaxMhz;
                    }
                    if (valid > 0)
                        return (Math.Round(sumCur / valid, 0), maxMhz > 0 ? Math.Round(maxMhz, 0) : null);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            errors["cpuClock"] = ex.Message;
        }
        return (null, null);
    }

    private static (double? UsedGb, double? TotalGb, double? Pct) ReadCommitCharge(Dictionary<string, string> errors)
    {
        try
        {
            var pi = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
            if (GetPerformanceInfo(out pi, pi.cb) && pi.CommitLimit.ToUInt64() > 0 && pi.PageSize.ToUInt64() > 0)
            {
                ulong pageSize = pi.PageSize.ToUInt64();
                double usedBytes = pi.CommitTotal.ToUInt64() * (double)pageSize;
                double limitBytes = pi.CommitLimit.ToUInt64() * (double)pageSize;
                double usedGb = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
                double totalGb = Math.Round(limitBytes / (1024.0 * 1024.0 * 1024.0), 1);
                double pct = Math.Round(Math.Clamp((usedBytes / limitBytes) * 100.0, 0.0, 100.0), 1);
                return (usedGb, totalGb, pct);
            }
        }
        catch (Exception ex)
        {
            errors["commitCharge"] = ex.Message;
        }
        return (null, null, null);
    }

    private static (double? ActivePct, double? ReadKbps, double? WriteKbps, int? QueueLength) ReadDiskActivity(Dictionary<string, string> errors)
    {
        try
        {
            var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT PercentDiskTime, DiskReadBytesPersec, DiskWriteBytesPersec, CurrentDiskQueueLength FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'"),
                opts);
            foreach (ManagementObject obj in searcher.Get())
            {
                double? activePct = obj["PercentDiskTime"] != null && double.TryParse(obj["PercentDiskTime"].ToString(), out double pct)
                    ? Math.Round(Math.Clamp(pct, 0.0, 100.0), 1) : null;
                double? readKbps = obj["DiskReadBytesPersec"] != null && double.TryParse(obj["DiskReadBytesPersec"].ToString(), out double rb)
                    ? Math.Round(rb / 1024.0, 1) : null;
                double? writeKbps = obj["DiskWriteBytesPersec"] != null && double.TryParse(obj["DiskWriteBytesPersec"].ToString(), out double wb)
                    ? Math.Round(wb / 1024.0, 1) : null;
                int? qLen = obj["CurrentDiskQueueLength"] != null && int.TryParse(obj["CurrentDiskQueueLength"].ToString(), out int ql)
                    ? ql : null;
                return (activePct, readKbps, writeKbps, qLen);
            }
        }
        catch (Exception ex)
        {
            errors["diskIo"] = ex.Message;
        }
        return (null, null, null, null);
    }

    private (double? RxKbps, double? TxKbps, string? AdapterName, double? LinkMbps) ReadNetworkThroughput(Dictionary<string, string> errors)
    {
        try
        {
            var activeIfs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();
            if (activeIfs.Count == 0)
                return (null, null, null, null);

            var primary = activeIfs.OrderByDescending(n => n.Speed).First();
            long totalRx = 0;
            long totalTx = 0;
            foreach (var nic in activeIfs)
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    totalRx += stats.BytesReceived;
                    totalTx += stats.BytesSent;
                }
                catch { }
            }

            long nowTicks = Stopwatch.GetTimestamp();
            double? linkMbps = primary.Speed > 0 ? Math.Round(primary.Speed / 1_000_000.0, 0) : null;

            lock (_sampleLock)
            {
                if (!_hasPrevNet || nowTicks <= _prevNetTicks)
                {
                    _prevNetBytesRx = totalRx;
                    _prevNetBytesTx = totalTx;
                    _prevNetTicks = nowTicks;
                    _hasPrevNet = true;
                    return (null, null, primary.Name, linkMbps);
                }

                double elapsedSec = (double)(nowTicks - _prevNetTicks) / Stopwatch.Frequency;
                double? rxKbps = elapsedSec > 0.05 && totalRx >= _prevNetBytesRx
                    ? Math.Round(((totalRx - _prevNetBytesRx) * 8.0) / (elapsedSec * 1000.0), 1) : null;
                double? txKbps = elapsedSec > 0.05 && totalTx >= _prevNetBytesTx
                    ? Math.Round(((totalTx - _prevNetBytesTx) * 8.0) / (elapsedSec * 1000.0), 1) : null;

                _prevNetBytesRx = totalRx;
                _prevNetBytesTx = totalTx;
                _prevNetTicks = nowTicks;
                return (rxKbps, txKbps, primary.Name, linkMbps);
            }
        }
        catch (Exception ex)
        {
            errors["networkIo"] = ex.Message;
            return (null, null, null, null);
        }
    }

    private static (bool? OnAc, bool? HasBattery, int? BatteryPct, int? LifeMin, bool? PowerSaver) ReadPowerStatus(Dictionary<string, string> errors)
    {
        try
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps))
            {
                bool? onAc = sps.ACLineStatus switch { 0 => false, 1 => true, _ => null };
                bool hasBatt = (sps.BatteryFlag & 128) == 0 && sps.BatteryFlag != 255;
                int? battPct = hasBatt && sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : null;
                int? lifeMin = hasBatt && sps.BatteryLifeTime != uint.MaxValue ? (int)(sps.BatteryLifeTime / 60) : null;
                bool powerSaver = (sps.SystemStatusFlag & 1) != 0;
                return (onAc, hasBatt, battPct, lifeMin, powerSaver);
            }
        }
        catch (Exception ex)
        {
            errors["power"] = ex.Message;
        }
        return (null, null, null, null, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }

    private delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct EVENT_TRACE_PROPERTIES
    {
        [FieldOffset(0)] public uint BufferSizeField;
        [FieldOffset(24)] public Guid Guid;
        [FieldOffset(40)] public uint ClientContext;
        [FieldOffset(44)] public uint Flags;
        [FieldOffset(48)] public uint BufferSize;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimer;
        [FieldOffset(72)] public uint EnableFlags;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public uint BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_LOGFILEW
    {
        public IntPtr LogFileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)] public byte[]? CurrentEvent;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)] public byte[]? LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        [MarshalAs(UnmanagedType.FunctionPtr)] public EventRecordCallbackDelegate? EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int InformationLevel, IntPtr InputBuffer, uint InputBufferLength, IntPtr OutputBuffer, uint OutputBufferLength);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong traceHandle, string instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    private static extern void RtlZeroMemory(IntPtr destination, IntPtr length);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, [Out] byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NVML_UTILIZATION
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NVML_MEMORY
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetHandleDelegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetTempDelegate(IntPtr device, uint sensorType, out uint temp);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetUtilizationDelegate(IntPtr device, out NVML_UTILIZATION utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetMemoryDelegate(IntPtr device, out NVML_MEMORY memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetClockDelegate(IntPtr device, uint clockType, out uint clockMHz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetPowerDelegate(IntPtr device, out uint powerMilliWatts);
}
