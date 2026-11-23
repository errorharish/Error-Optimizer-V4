using System;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Core.ActionHandlers;

public class GpuPerformanceHandler : IActionHandler
{
    public string ActionName => "SetGpuPerformance";

    private const string MmcssGamesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        return !string.IsNullOrWhiteSpace(context?.HardwareProfile?.GPU) ? TargetState.Ready : TargetState.NotApplicable;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        string current = GetCurrentValueDisplay(entry);
        if (current == "GPU Priority=8, Scheduling=High")
        {
            return new OptimizationResult
            {
                Status = ResultStatus.AlreadyOptimized,
                ItemId = "GPUPerformance",
                DisplayName = "MMCSS Games GPU scheduling priority already maximized"
            };
        }

        try
        {
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            string prevGpu = "VALUE DID NOT EXIST";
            string prevSched = "VALUE DID NOT EXIST";
            using (var readKey = hklm64.OpenSubKey(MmcssGamesKey, false))
            {
                prevGpu = readKey?.GetValue("GPU Priority")?.ToString() ?? "VALUE DID NOT EXIST";
                prevSched = readKey?.GetValue("Scheduling Category")?.ToString() ?? "VALUE DID NOT EXIST";
            }

            string profileName = entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual";
            var bkTx1 = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureRegistryTweak(
                profileName,
                "MMCSS Games GPU Priority",
                "HKLM",
                MmcssGamesKey,
                "GPU Priority",
                prevGpu,
                "DWord",
                8,
                "SAFE");
            var bkTx2 = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureRegistryTweak(
                profileName,
                "MMCSS Games Scheduling Category",
                "HKLM",
                MmcssGamesKey,
                "Scheduling Category",
                prevSched,
                "String",
                "High",
                "SAFE");
            if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx1, out string bkErr1))
            {
                return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "GPUPerformance", Message = bkErr1 };
            }
            if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx2, out string bkErr2))
            {
                return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "GPUPerformance", Message = bkErr2 };
            }

            using var writeKey = hklm64.CreateSubKey(MmcssGamesKey, true);
            if (writeKey == null)
            {
                return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "GPUPerformance", Message = "Unable to open MMCSS Games registry key." };
            }

            writeKey.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
            writeKey.SetValue("Priority", 6, RegistryValueKind.DWord);
            writeKey.SetValue("Scheduling Category", "High", RegistryValueKind.String);
            writeKey.SetValue("SFIO Priority", "High", RegistryValueKind.String);

            if (GetCurrentValueDisplay(entry) == "GPU Priority=8, Scheduling=High")
            {
                return new OptimizationResult
                {
                    Status = ResultStatus.Success,
                    ItemId = "GPUPerformance",
                    DisplayName = "Maximized MMCSS GPU Scheduling Priority (8 / High) — Verified"
                };
            }

            return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "GPUPerformance", Message = "Registry readback verification failed." };
        }
        catch (UnauthorizedAccessException)
        {
            return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "GPUPerformance", Message = "Administrator privilege required to configure MMCSS GPU scheduling." };
        }
        catch (Exception ex)
        {
            return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "GPUPerformance", Message = ex.Message };
        }
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm64.OpenSubKey(MmcssGamesKey, false);
            if (key != null)
            {
                object? gpuPri = key.GetValue("GPU Priority");
                string? sched = key.GetValue("Scheduling Category")?.ToString();
                if (gpuPri != null && int.TryParse(gpuPri.ToString(), out int g) && g == 8 &&
                    string.Equals(sched, "High", StringComparison.OrdinalIgnoreCase))
                {
                    return "GPU Priority=8, Scheduling=High";
                }
                return $"GPU Priority={gpuPri ?? "default"}, Scheduling={sched ?? "default"}";
            }
        }
        catch { }
        return "Default";
    }
}
