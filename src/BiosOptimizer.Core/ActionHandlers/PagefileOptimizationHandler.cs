using System;
using System.Management;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class PagefileOptimizationHandler : IActionHandler
{
    public string ActionName => "SetPagefile";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        // Require sufficient RAM or an SSD to apply aggressive pagefile optimization
        if (!context.HardwareProfile.IsSSD && context.HardwareProfile.RAM < (16L * 1024 * 1024 * 1024))
        {
            return TargetState.NotApplicable;
        }
        return TargetState.Ready;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        string targetValue = entry.Target?.ToString() ?? "Auto";
        string currentValue = GetCurrentValueDisplay(entry);

        if (currentValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase))
        {
            return new OptimizationResult
            {
                Status = ResultStatus.AlreadyOptimized,
                ItemId = "Pagefile",
                DisplayName = "Pagefile already " + targetValue
            };
        }

        // Capture before state into Universal Backup Manager and verify SHA-256 integrity before mutating
        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureGenericTweak(
            entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
            "pagefile.automatic_managed",
            $"Pagefile Management \u2192 {targetValue}",
            "Memory",
            "WmiSetting",
            $"AutomaticManagedPagefile: {currentValue}",
            $"AutomaticManagedPagefile: {targetValue}",
            "WmiSettingRestore",
            true,
            "SAFE"
        );
        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
        {
            return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "Pagefile", DisplayName = entry.DisplayName, Message = bkErr };
        }

        try
        {
            bool setAuto = targetValue.Equals("Auto", StringComparison.OrdinalIgnoreCase);

            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    obj["AutomaticManagedPagefile"] = setAuto;
                    obj.Put();
                }
            }

            string readBack = GetCurrentValueDisplay(entry);
            if (!readBack.Equals(setAuto ? "Auto" : "Manual", StringComparison.OrdinalIgnoreCase))
            {
                return new OptimizationResult
                {
                    Status = ResultStatus.Failed,
                    ItemId = "Pagefile",
                    Message = $"WMI readback mismatch: expected {(setAuto ? "Auto" : "Manual")}, observed '{readBack}'."
                };
            }

            return new OptimizationResult
            {
                Status = ResultStatus.RequiresReboot,
                ItemId = "Pagefile",
                DisplayName = $"Set Pagefile optimization to {targetValue}",
                Message = $"Verified AutomaticManagedPagefile={readBack} (reboot recommended).",
                RequiresReboot = true
            };
        }
        catch (Exception ex)
        {
            return new OptimizationResult
            {
                Status = ResultStatus.Failed,
                ItemId = "Pagefile",
                Message = $"WMI Error: {ex.Message}"
            };
        }
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var isAuto = (bool)obj["AutomaticManagedPagefile"];
                return isAuto ? "Auto" : "Manual";
            }
        }
        catch { }
        return "Unknown";
    }
}
