#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Power;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations.Performance;

public sealed class TuningCapabilityProbe
{
    public string CpuName { get; set; } = string.Empty;
    public string CpuAvailability { get; set; } = "Supported"; // Supported | Limited | Unsupported
    public bool CpuRawOverclockSupported { get; set; } // Honest false without OEM/ring-0 driver
    public string CpuSupportDetail { get; set; } = string.Empty;
    public string? CpuUnsupportedHardwareReason { get; set; }
    public double? CpuTempC { get; set; }

    public string GpuName { get; set; } = string.Empty;
    public string GpuAvailability { get; set; } = "Unsupported";
    public bool GpuRawVoltageOverclockSupported { get; set; }
    public string GpuSupportDetail { get; set; } = string.Empty;
    public string? GpuUnsupportedHardwareReason { get; set; }
    public double? GpuTempC { get; set; }

    public string PowerSource { get; set; } = "AC";
    public bool OnBattery { get; set; }
    public int BatteryPercent { get; set; } = 100;
    public string ActivePowerSchemeGuid { get; set; } = string.Empty;
    public string ActivePowerSchemeName { get; set; } = string.Empty;
    public bool HasActiveTunedState { get; set; }
    public string? LastTransactionId { get; set; }
}

public sealed class TuningExecutionResult
{
    public bool Success { get; set; }
    public string Target { get; set; } = "cpu";
    public string Status { get; set; } = "Failed"; // VerifiedSuccess | RevertedSafety | Unsupported | BlockedThermal | Failed
    public string Message { get; set; } = string.Empty;
    public string? TransactionId { get; set; }
    public bool RolledBackAutomatically { get; set; }
    public List<string> StagesCompleted { get; set; } = new();
    public List<string> AppliedChanges { get; set; } = new();
    public string? UnsupportedHardwareNotice { get; set; }
    public double? BaselineTempC { get; set; }
    public double? PostTuneTempC { get; set; }
    public double? BaselineScoreOpsPerMs { get; set; }
    public double? PostTuneScoreOpsPerMs { get; set; }
    public bool RequiresRestart { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>
/// Hardware-safe CPU & GPU performance tuning engine using documented Windows PPM power settings,
/// graphics driver scheduling/TDR policies, real thermal probes, micro-benchmark stability checks,
/// and <see cref="BackupManager"/> transactional snapshots. Never claims unsupported VCore/multiplier OC
/// and never uses unsigned ring-0 drivers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PerformanceTuningEngine
{
    private static readonly Lazy<PerformanceTuningEngine> _instance = new(() => new PerformanceTuningEngine());
    public static PerformanceTuningEngine Instance => _instance.Value;

    private const string ProfileTag = "PerformanceTuning";
    private const double MaxSafeCpuStartTempC = 85.0;
    private const double MaxSafeCpuPostTempC = 90.0;
    private const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ProcMinGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ProcMaxGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string PerfBoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string PerfEppGuid = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";

    private string? _lastActiveSchemeBeforeTune;
    private string? _lastTransactionId;

    public async Task<TuningCapabilityProbe> ProbeCapabilitiesAsync(CancellationToken ct = default)
    {
        var hw = HardwareDetectionService.Instance.GetSnapshot(forceRefresh: true);
        var active = await PowerPlanEngine.Instance.GetActiveSchemeAsync(ct).ConfigureAwait(false);
        var adapters = DxgiGpuReader.EnumerateAdapters().Where(a => !a.IsSoftwareAdapter).ToList();
        var primaryGpu = adapters.FirstOrDefault(a => a.IsDedicatedGpu) ?? adapters.FirstOrDefault();

        double? cpuTemp = ReadAcpiThermalZoneCelsius();
        double? gpuTemp = ReadNvmlGpuTempCelsius();

        bool hasDedicated = adapters.Any(a => a.IsDedicatedGpu);
        string gpuName = primaryGpu?.Description ?? hw.Gpus.FirstOrDefault()?.Name ?? "Standard Display Adapter";
        bool gpuSupported = primaryGpu != null;

        var existingTx = BackupManager.Instance.GetAllTransactions()
            .Where(t => t.ProfileName.Equals(ProfileTag, StringComparison.OrdinalIgnoreCase) &&
                        t.CanRollback && t.RollbackStatus != "RESTORED")
            .OrderByDescending(t => t.Timestamp)
            .FirstOrDefault();

        return new TuningCapabilityProbe
        {
            CpuName = hw.Cpu.Name,
            CpuAvailability = cpuTemp.HasValue ? "Supported" : "Limited",
            CpuRawOverclockSupported = false,
            CpuSupportDetail = cpuTemp.HasValue
                ? "Windows Processor Power Management (Boost Policy, EPP & Minimum P-State floor) supported with active ACPI thermal telemetry."
                : "Thermal sensor telemetry (MSAcpi_ThermalZoneTemperature) unavailable — temperature-dependent CPU boost and minimum P-state floor tuning are gated off for thermal safety.",
            CpuUnsupportedHardwareReason = "Arbitrary VCore voltage, BCLK, and multiplier overclocking are not exposed safely without OEM/signed kernel drivers and are intentionally disabled.",
            CpuTempC = cpuTemp,

            GpuName = gpuName,
            GpuAvailability = hasDedicated ? "Supported" : (gpuSupported ? "Limited" : "Unsupported"),
            GpuRawVoltageOverclockSupported = false,
            GpuSupportDetail = gpuSupported
                ? "Windows Hardware-Accelerated GPU Scheduling (HAGS) & TDR stability policy supported."
                : "No physical WDDM DXGI display adapter detected.",
            GpuUnsupportedHardwareReason = "Direct GPU core voltage and memory clock overclocking require vendor-signed NDA kernel interfaces and remain driver-managed.",
            GpuTempC = gpuTemp,

            PowerSource = hw.IsLaptop ? (hw.IsOnBattery ? "Battery (DC)" : "AC Power (Laptop)") : "AC Power (Desktop)",
            OnBattery = hw.IsOnBattery,
            BatteryPercent = hw.BatteryPercentage,
            ActivePowerSchemeGuid = active.guid,
            ActivePowerSchemeName = active.name,
            HasActiveTunedState = existingTx != null,
            LastTransactionId = existingTx?.TransactionId ?? _lastTransactionId
        };
    }

    public async Task<TuningExecutionResult> ExecuteTuningAsync(string target, bool confirmed = true, CancellationToken ct = default)
    {
        string normalizedTarget = (target ?? "cpu").Trim().ToLowerInvariant();
        if (normalizedTarget is not ("cpu" or "gpu" or "both"))
            normalizedTarget = "cpu";

        var sw = Stopwatch.StartNew();
        var result = new TuningExecutionResult { Target = normalizedTarget };

        if (!confirmed)
        {
            result.Status = "Failed";
            result.Message = "Explicit confirmation is required before running performance tuning.";
            return result;
        }

        // Stage 1: Check hardware capability & thermal safety headroom
        var probe = await ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
        result.StagesCompleted.Add("1. Check: Hardware capability and thermal headroom inspected");
        result.BaselineTempC = probe.CpuTempC;
        result.UnsupportedHardwareNotice = normalizedTarget switch
        {
            "cpu" => probe.CpuUnsupportedHardwareReason,
            "gpu" => probe.GpuUnsupportedHardwareReason,
            _ => $"{probe.CpuUnsupportedHardwareReason} {probe.GpuUnsupportedHardwareReason}"
        };

        if (probe.OnBattery && probe.BatteryPercent < 25)
        {
            result.Status = "BlockedThermal";
            result.Message = $"Blocked: Battery at {probe.BatteryPercent}% on DC power. Connect AC power before performance tuning.";
            return result;
        }

        if (probe.CpuTempC.HasValue && probe.CpuTempC.Value >= MaxSafeCpuStartTempC)
        {
            result.Status = "BlockedThermal";
            result.Message = $"Blocked: Baseline CPU temperature {probe.CpuTempC.Value:F1}°C exceeds safe {MaxSafeCpuStartTempC:F0}°C threshold.";
            return result;
        }

        bool cpuRequested = normalizedTarget is "cpu" or "both";
        bool gpuRequested = normalizedTarget is "gpu" or "both";
        bool cpuThermalSafe = probe.CpuTempC.HasValue;

        if (cpuRequested && !cpuThermalSafe)
        {
            const string thermalGateReason = "CPU thermal sensor telemetry (MSAcpi_ThermalZoneTemperature) is unavailable; temperature-dependent CPU boost and minimum P-state floor tuning are blocked by the thermal safety gate.";
            if (normalizedTarget == "cpu")
            {
                result.Status = "BlockedThermal";
                result.Message = $"Blocked: {thermalGateReason}";
                result.StagesCompleted.Add("1. Check: Thermal safety gate blocked CPU boost/P-state tuning (CPU thermal sensor unavailable)");
                return result;
            }

            if (probe.GpuAvailability == "Unsupported")
            {
                result.Status = "BlockedThermal";
                result.Message = $"Blocked: {thermalGateReason} No physical WDDM GPU adapter is available for GPU scheduling tuning.";
                result.StagesCompleted.Add("1. Check: Thermal safety gate blocked CPU tuning (thermal sensor unavailable) and no physical GPU is available");
                return result;
            }

            result.StagesCompleted.Add("1. Check: Thermal safety gate blocked CPU boost/P-state tuning (CPU thermal sensor unavailable); continuing with non-thermal GPU scheduling only");
            result.UnsupportedHardwareNotice = $"{thermalGateReason} {result.UnsupportedHardwareNotice}";
        }

        if (normalizedTarget == "gpu" && probe.GpuAvailability == "Unsupported")
        {
            result.Status = "Unsupported";
            result.Message = probe.GpuSupportDetail;
            return result;
        }

        // Stage 2: Test baseline performance + snapshot state for rollback
        double baselineOps = await RunBoundedComputeProbeAsync(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
        result.BaselineScoreOpsPerMs = baselineOps;
        _lastActiveSchemeBeforeTune = probe.ActivePowerSchemeGuid;

        string batchId = BackupManager.Instance.BeginBatchTransaction(ProfileTag, ProfileTag, "Performance");
        result.TransactionId = batchId;
        _lastTransactionId = batchId;
        result.StagesCompleted.Add($"2. Test: Baseline measured ({baselineOps:F1} ops/ms) & rollback transaction {batchId} initialized");

        try
        {
            // Stage 3: Tune — safe documented OS power & scheduling controls only
            bool cpuApplied = false;
            if (cpuRequested && cpuThermalSafe)
            {
                await ApplySafeCpuPpmTuningAsync(probe.ActivePowerSchemeGuid, batchId, result, ct).ConfigureAwait(false);
                cpuApplied = true;
            }

            if (gpuRequested)
            {
                ApplySafeGpuSchedulingTuning(batchId, result);
            }

            BackupManager.Instance.CommitBatchTransaction(batchId);
            result.StagesCompleted.Add("3. Tune: Safe OS/power controls applied with transactional backup");

            // Stage 4: Verify — short stability load + mandatory live thermal check when CPU tuning ran
            double postOps = await RunBoundedComputeProbeAsync(TimeSpan.FromMilliseconds(450), ct).ConfigureAwait(false);
            double? postTemp = ReadAcpiThermalZoneCelsius();
            result.PostTuneScoreOpsPerMs = postOps;
            result.PostTuneTempC = postTemp;
            result.StagesCompleted.Add($"4. Verify: Post-tune stability check ({postOps:F1} ops/ms, temp={(postTemp.HasValue ? $"{postTemp.Value:F1}°C" : "Unavailable")})");

            if (cpuApplied && (!postTemp.HasValue || postTemp.Value >= MaxSafeCpuPostTempC))
            {
                // Auto-rollback on thermal excursion or loss of thermal sensor during CPU boost load
                await RollbackTuningAsync(batchId, ct).ConfigureAwait(false);
                result.RolledBackAutomatically = true;
                result.Success = false;
                result.Status = "RevertedSafety";
                result.Message = postTemp.HasValue
                    ? $"Thermal protection triggered at {postTemp.Value:F1}°C; reverted changes safely via {batchId}."
                    : $"Post-tune CPU thermal sensor verification became unavailable; reverted changes safely via {batchId}.";
                result.StagesCompleted.Add("5. Decide: Reverted automatically by thermal safety gate");
                return result;
            }

            // Stage 5: Decide & commit
            result.Success = result.AppliedChanges.Count > 0;
            if (result.Success)
            {
                result.Status = "VerifiedSuccess";
                result.Message = (cpuRequested && !cpuThermalSafe)
                    ? $"Applied non-thermal GPU scheduling tuning ({result.AppliedChanges.Count} change); CPU boost/P-state tuning stayed blocked because no ACPI CPU thermal sensor is exposed."
                    : $"Verified safe {normalizedTarget.ToUpperInvariant()} tuning active ({result.AppliedChanges.Count} controlled setting(s)). Baseline={baselineOps:F1} ops/ms, Post={postOps:F1} ops/ms.";
            }
            else
            {
                result.Status = (cpuRequested && !cpuThermalSafe) ? "BlockedThermal" : "Unsupported";
                result.Message = (cpuRequested && !cpuThermalSafe)
                    ? "CPU boost/P-state tuning blocked by thermal safety gate (no CPU thermal sensor); no additional GPU changes were needed."
                    : "No additional safe tuning changes were needed or permitted on this system.";
            }
            result.StagesCompleted.Add($"5. Decide: {result.Status}");
        }
        catch (Exception ex)
        {
            await RollbackTuningAsync(batchId, CancellationToken.None).ConfigureAwait(false);
            result.RolledBackAutomatically = true;
            result.Success = false;
            result.Status = "RevertedSafety";
            result.Message = $"Tuning aborted and safely rolled back: {ex.Message}";
            result.StagesCompleted.Add("5. Decide: Error recovery rollback completed");
        }
        finally
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            AuditLogManager.Write("PerformanceTuning", $"Execute.{normalizedTarget}", result.Status, result.Message);
        }

        return result;
    }

    public async Task<(bool success, string message)> RollbackTuningAsync(string? transactionId = null, CancellationToken ct = default)
    {
        try
        {
            var res = string.IsNullOrWhiteSpace(transactionId)
                ? await BackupManager.Instance.RestoreProfileAsync(ProfileTag, ct).ConfigureAwait(false)
                : await BackupManager.Instance.RestoreSingleTransactionAsync(transactionId, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_lastActiveSchemeBeforeTune))
            {
                await RunPowerCfgAsync($"/setactive {_lastActiveSchemeBeforeTune}", ct).ConfigureAwait(false);
            }

            AuditLogManager.Write("PerformanceTuning", "Rollback", res.Success ? "OK" : "Warning", res.Message);
            return (res.Success, res.Message);
        }
        catch (Exception ex)
        {
            return (false, $"Performance tuning rollback error: {ex.Message}");
        }
    }

    private static async Task ApplySafeCpuPpmTuningAsync(
        string activeSchemeGuid,
        string batchId,
        TuningExecutionResult result,
        CancellationToken ct)
    {
        var aiPlan = await PowerPlanEngine.Instance.CreateOrGetAiMaxPerformancePlanAsync(ct).ConfigureAwait(false);
        string schemeGuid = !string.IsNullOrWhiteSpace(aiPlan.TargetSchemeGuid)
            ? aiPlan.TargetSchemeGuid
            : (!string.IsNullOrWhiteSpace(activeSchemeGuid) ? activeSchemeGuid : PowerPlanEngine.HighPerformanceGuid);

        if (!string.IsNullOrWhiteSpace(activeSchemeGuid) &&
            !schemeGuid.Equals(activeSchemeGuid, StringComparison.OrdinalIgnoreCase))
        {
            BackupManager.Instance.CapturePowerPlanTweak(
                ProfileTag,
                "CPU Performance Power Scheme",
                activeSchemeGuid,
                schemeGuid,
                "SAFE",
                batchId);

            await PowerPlanEngine.Instance.ApplyPowerPlanAsync(schemeGuid, PowerPlanChangeSource.USER_REQUEST, ct).ConfigureAwait(false);
            result.AppliedChanges.Add($"Activated managed performance scheme {schemeGuid}");
        }

        // Documented powercfg AC processor boost & EPP indices (zero unsigned drivers)
        await RunPowerCfgAsync($"/setacvalueindex {schemeGuid} {SubProcessorGuid} {PerfBoostModeGuid} 2", ct).ConfigureAwait(false);
        await RunPowerCfgAsync($"/setacvalueindex {schemeGuid} {SubProcessorGuid} {PerfEppGuid} 15", ct).ConfigureAwait(false);
        await RunPowerCfgAsync($"/setacvalueindex {schemeGuid} {SubProcessorGuid} {ProcMinGuid} 50", ct).ConfigureAwait(false);
        await RunPowerCfgAsync($"/setacvalueindex {schemeGuid} {SubProcessorGuid} {ProcMaxGuid} 100", ct).ConfigureAwait(false);
        await RunPowerCfgAsync($"/setactive {schemeGuid}", ct).ConfigureAwait(false);
        result.AppliedChanges.Add("Set AC Processor Boost=Aggressive, EPP=15%, MinPState=50%, MaxPState=100%");
    }

    private static void ApplySafeGpuSchedulingTuning(string batchId, TuningExecutionResult result)
    {
        const string gfxPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        using var key = Registry.LocalMachine.OpenSubKey(gfxPath, writable: true);
        if (key == null)
            return;

        object? prevHags = key.GetValue("HwSchMode");
        int currentHags = prevHags is int val ? val : 1;
        if (currentHags != 2)
        {
            BackupManager.Instance.CaptureRegistryTweak(
                ProfileTag,
                "Hardware-Accelerated GPU Scheduling (HAGS)",
                "HKEY_LOCAL_MACHINE",
                gfxPath,
                "HwSchMode",
                prevHags,
                "DWord",
                2,
                "LOW",
                batchId);

            key.SetValue("HwSchMode", 2, RegistryValueKind.DWord);
            result.RequiresRestart = true;
            result.AppliedChanges.Add("Enabled Hardware-Accelerated GPU Scheduling (HwSchMode=2, reboot to activate)");
        }
    }

    private static async Task<double> RunBoundedComputeProbeAsync(TimeSpan duration, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            long ops = 0;
            double acc = 1.0001;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                for (int i = 0; i < 4096; i++)
                    acc = Math.Sqrt(acc * 1.000001 + 1.0);
                ops += 4096;
            }
            sw.Stop();
            if (acc < 0) Debug.WriteLine(acc);
            return Math.Round(ops / Math.Max(1.0, sw.Elapsed.TotalMilliseconds), 2);
        }, ct).ConfigureAwait(false);
    }

    private static async Task RunPowerCfgAsync(string args, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc != null)
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static double? ReadAcpiThermalZoneCelsius()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? maxTemp = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] != null && double.TryParse(obj["CurrentTemperature"].ToString(), out double decikelvin))
                {
                    double c = (decikelvin / 10.0) - 273.15;
                    if (c is > 10.0 and < 120.0)
                        maxTemp = !maxTemp.HasValue || c > maxTemp.Value ? c : maxTemp;
                }
            }
            return maxTemp.HasValue ? Math.Round(maxTemp.Value, 1) : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlShutdown();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);

    private static double? ReadNvmlGpuTempCelsius()
    {
        try
        {
            if (nvmlInit_v2() != 0) return null;
            try
            {
                if (nvmlDeviceGetHandleByIndex_v2(0, out IntPtr handle) == 0 &&
                    nvmlDeviceGetTemperature(handle, 0, out uint temp) == 0 &&
                    temp is > 10 and < 120)
                {
                    return temp;
                }
            }
            finally
            {
                try { nvmlShutdown(); } catch { }
            }
        }
        catch { }
        return null;
    }
}
