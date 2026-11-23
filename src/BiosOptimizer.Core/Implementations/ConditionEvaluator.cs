using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.Implementations;

public class ConditionEvaluator
{
    public bool Evaluate(List<ConditionDef> conditions, EnvironmentContext context)
    {
        if (conditions == null || conditions.Count == 0) return true;

        foreach (var condition in conditions)
        {
            if (!EvaluateCondition(condition, context))
                return false;
        }

        return true;
    }

    private static bool SafeParseBool(string valueStr, bool defaultValue = true)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return defaultValue;
        return bool.TryParse(valueStr, out bool result) ? result : defaultValue;
    }

    private static long SafeParseLong(string valueStr, long defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return defaultValue;
        return long.TryParse(valueStr, out long result) ? result : defaultValue;
    }

    private static int SafeParseInt(string valueStr, int defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return defaultValue;
        return int.TryParse(valueStr, out int result) ? result : defaultValue;
    }

    private bool EvaluateCondition(ConditionDef condition, EnvironmentContext context)
    {
        var valueStr = condition.Value?.ToString() ?? "";
        
        return condition.Type switch
        {
            "Windows10" => context.IsWindows10 == SafeParseBool(valueStr, true),
            "Windows11" => context.IsWindows11 == SafeParseBool(valueStr, true),
            "HasBattery" => context.HasBattery == SafeParseBool(valueStr, true),
            "IsLaptop" => context.IsLaptop == SafeParseBool(valueStr, true),
            "IsDesktop" => context.IsDesktop == SafeParseBool(valueStr, true),
            "IsDomainJoined" => context.IsDomainJoined == SafeParseBool(valueStr, true),
            "HasPrinter" => EvaluateDeviceState(context.HasPrinter, valueStr),
            "HasBluetooth" => EvaluateDeviceState(context.HasBluetooth, valueStr),
            "HasTouchscreen" => EvaluateDeviceState(context.HasTouchscreen, valueStr),
            "HasCamera" => EvaluateDeviceState(context.HasCamera, valueStr),
            "BitLockerActive" => context.BitLockerActive == SafeParseBool(valueStr, true),
            "MinimumRamGb" => context.RamSizeGb >= SafeParseLong(valueStr, 0),
            "MaximumRamGb" => context.RamSizeGb <= SafeParseLong(valueStr, long.MaxValue),
            "MinimumPhysicalCores" => context.PhysicalCoreCount >= SafeParseInt(valueStr, 0),
            "StorageType" => context.StorageType.Equals(valueStr, StringComparison.OrdinalIgnoreCase),
            "StorageIsSsd" => SafeParseBool(valueStr, true) == (context.StorageType.Equals("SSD", StringComparison.OrdinalIgnoreCase) || context.StorageType.Equals("NVMe", StringComparison.OrdinalIgnoreCase)),
            "StorageIsNvme" => SafeParseBool(valueStr, true) == context.StorageType.Equals("NVMe", StringComparison.OrdinalIgnoreCase),
            "StorageIsHdd" => SafeParseBool(valueStr, true) == context.StorageType.Equals("HDD", StringComparison.OrdinalIgnoreCase),
            "VirtualizationSupported" => context.VirtualizationSupported == SafeParseBool(valueStr, true),
            "VirtualizationEnabled" => context.VirtualizationEnabled == SafeParseBool(valueStr, true),
            "HasNvidiaGpu" => SafeParseBool(valueStr, true) == context.Gpus.Any(g =>
                g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)),
            "HasAmdGpu" => SafeParseBool(valueStr, true) == context.Gpus.Any(g =>
                g.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                g.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                g.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)),
            "HasIntelGpu" => SafeParseBool(valueStr, true) == context.Gpus.Any(g =>
                g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                g.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)),
            "GpuVendorContains" => context.Gpus.Any(g =>
                g.Name.Contains(valueStr, StringComparison.OrdinalIgnoreCase) ||
                g.Vendor.Contains(valueStr, StringComparison.OrdinalIgnoreCase)),
            "CpuVendorContains" => context.CpuName.Contains(valueStr, StringComparison.OrdinalIgnoreCase),
            "IsAmdCpu" or "HasAmdCpu" => SafeParseBool(valueStr, true) == (context.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || context.CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)),
            "IsIntelCpu" or "HasIntelCpu" => SafeParseBool(valueStr, true) == context.CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase),
            "IsDualCcdAmd3DVcache" => SafeParseBool(valueStr, true) == BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot(false).Cpu.IsDualCcdAmd3DVcache,
            "IsHybridCpu" => SafeParseBool(valueStr, true) == BiosOptimizer.Core.Implementations.HardwareDetectionService.Instance.GetSnapshot(false).Cpu.IsHybridCpu,
            "WorkloadDetected" => context.Workloads.Any(w => w.Detected && w.WorkloadType.Equals(valueStr, StringComparison.OrdinalIgnoreCase)),
            "WorkloadInstalled" => context.Workloads.Any(w => w.Detected && w.WorkloadType.Equals(valueStr, StringComparison.OrdinalIgnoreCase)),
            "WorkloadNotInstalled" => !context.Workloads.Any(w => w.Detected && w.WorkloadType.Equals(valueStr, StringComparison.OrdinalIgnoreCase)),
            "IsOnAc" => SafeParseBool(valueStr, true) != context.MachineProfile.IsOnBattery,
            "IsOnBattery" => SafeParseBool(valueStr, true) == context.MachineProfile.IsOnBattery,
            "ManufacturerContains" => context.Manufacturer.Contains(valueStr, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private bool EvaluateDeviceState(DeviceState state, string expectedBoolStr)
    {
        bool expected = SafeParseBool(expectedBoolStr, true);
        if (expected) return state == DeviceState.Available;
        return state == DeviceState.Unavailable || state == DeviceState.Unknown;
    }
}
