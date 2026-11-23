using System;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Core.ActionHandlers;

public class ManagedRegistryHealthHandler : IActionHandler
{
    public string ActionName => "RepairRegistryHealth";

    private static readonly (string SubKey, string ValueName, int DefaultDword)[] ManagedKeys =
    {
        (@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26),
        (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20),
    };

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        return TargetState.Ready;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        int issuesFound = 0;
        int repaired = 0;
        try
        {
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var (subKey, valName, defVal) in ManagedKeys)
            {
                using var readKey = hklm64.OpenSubKey(subKey, false);
                if (readKey == null) continue;
                var kind = readKey.GetValue(valName) != null ? readKey.GetValueKind(valName) : RegistryValueKind.Unknown;
                if (kind != RegistryValueKind.Unknown && kind != RegistryValueKind.DWord)
                {
                    issuesFound++;
                    using var writeKey = hklm64.OpenSubKey(subKey, true);
                    if (writeKey != null)
                    {
                        writeKey.SetValue(valName, defVal, RegistryValueKind.DWord);
                        repaired++;
                    }
                }
            }

            if (issuesFound == 0)
            {
                return new OptimizationResult
                {
                    Status = ResultStatus.AlreadyOptimized,
                    ItemId = "RegistryHealth",
                    DisplayName = "All managed Windows registry values validated intact"
                };
            }

            return new OptimizationResult
            {
                Status = repaired == issuesFound ? ResultStatus.Success : ResultStatus.Failed,
                ItemId = "RegistryHealth",
                DisplayName = $"Repaired {repaired}/{issuesFound} corrupt managed registry value types",
                Message = repaired == issuesFound ? "" : "Some keys could not be repaired without elevation."
            };
        }
        catch (Exception ex)
        {
            return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "RegistryHealth", Message = ex.Message };
        }
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var (subKey, valName, _) in ManagedKeys)
            {
                using var readKey = hklm64.OpenSubKey(subKey, false);
                var raw = readKey?.GetValue(valName);
                if (raw != null && readKey!.GetValueKind(valName) != RegistryValueKind.DWord)
                    return $"InvalidType:{valName}";
            }
            return "Healthy";
        }
        catch { return "Unchecked"; }
    }
}
