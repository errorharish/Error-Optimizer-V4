using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class CpuParkingHandler : IActionHandler
{
    public string ActionName => "SetCpuParking";

    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        if (context.PhysicalCoreCount < 4) return TargetState.NotApplicable;
        return TargetState.Ready;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        int targetPct = int.TryParse(entry.Value?.ToString(), out int v) ? Math.Clamp(v, 0, 100) : 100;
        int? currentAc = ReadCurrentAcMinCoresPct();
        if (currentAc.HasValue && currentAc.Value == targetPct)
        {
            return new OptimizationResult
            {
                Status = ResultStatus.AlreadyOptimized,
                ItemId = "CoreParking",
                DisplayName = $"CPU Core Parking Min Cores already {targetPct}%"
            };
        }

        try
        {
            var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureGenericTweak(
                entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
                "cpu.core_parking_min_cores",
                $"CPU Core Parking Min Cores → {targetPct}%",
                "Power",
                "PowerSetting",
                currentAc?.ToString() ?? "Unknown",
                targetPct.ToString(),
                "PowerSettingRestore",
                false,
                "SAFE");
            if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
            {
                return new OptimizationResult
                {
                    Status = ResultStatus.Failed,
                    ItemId = "CoreParking",
                    DisplayName = entry.DisplayName,
                    Message = bkErr
                };
            }

            int e1 = RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubProcessor} {CpMinCores} {targetPct}");
            int e2 = RunPowerCfg("/setactive SCHEME_CURRENT");
            int? afterAc = ReadCurrentAcMinCoresPct();

            if (e1 == 0 && e2 == 0 && afterAc.HasValue && afterAc.Value == targetPct)
            {
                return new OptimizationResult
                {
                    Status = ResultStatus.Success,
                    ItemId = "CoreParking",
                    DisplayName = $"Configured & verified CPU Core Parking Min Cores = {targetPct}%"
                };
            }

            return new OptimizationResult
            {
                Status = ResultStatus.Failed,
                ItemId = "CoreParking",
                Message = $"Failed to set CPU Core Parking (powercfg exit={e1}/{e2}, readback={afterAc?.ToString() ?? "unknown"})."
            };
        }
        catch (Exception ex)
        {
            return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "CoreParking", Message = ex.Message };
        }
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        int? pct = ReadCurrentAcMinCoresPct();
        return pct.HasValue ? $"{pct.Value}%" : "Unknown";
    }

    private static int? ReadCurrentAcMinCoresPct()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/query SCHEME_CURRENT {SubProcessor} {CpMinCores}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);
            var m = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out int val))
                return val;
        }
        catch { }
        return null;
    }

    private static int RunPowerCfg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        p?.WaitForExit(4000);
        return p?.ExitCode ?? -1;
    }
}
