using System.Diagnostics;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class TcpAutoTuningHandler : IActionHandler
{
    public string ActionName => "SetTcpAutoTuning";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        return TargetState.Ready;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var targetValue = entry.Value?.ToString() ?? "normal";
        var currentValue = GetCurrentValueDisplay(entry);

        if (currentValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase))
        {
            return new OptimizationResult
            {
                Status = ResultStatus.AlreadyOptimized,
                ItemId = "TcpAutoTuning",
                DisplayName = $"TCP Auto-Tuning already set to {targetValue}"
            };
        }

        // Capture before state into Universal Backup Manager and verify SHA-256 integrity before mutating
        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureNetworkTweak(
            entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
            $"TCP Auto-Tuning Level \u2192 {targetValue}",
            "TCP Global Settings",
            currentValue,
            targetValue,
            "SAFE"
        );
        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
        {
            return new OptimizationResult
            {
                Status = ResultStatus.Failed,
                ItemId = "TcpAutoTuning",
                DisplayName = entry.DisplayName,
                Message = bkErr
            };
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"int tcp set global autotuninglevel={targetValue}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p != null && p.WaitForExit(5000) && p.ExitCode == 0)
            {
                var newValue = GetCurrentValueDisplay(entry);
                if (newValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase))
                {
                    return new OptimizationResult
                    {
                        Status = ResultStatus.Success,
                        ItemId = "TcpAutoTuning",
                        DisplayName = $"Set TCP Auto-Tuning to {targetValue}",
                        Message = $"Verified TCP Auto-Tuning = {newValue}"
                    };
                }
            }
        }
        catch { }

        return new OptimizationResult
        {
            Status = ResultStatus.Failed,
            ItemId = "TcpAutoTuning",
            Message = "Failed to set TCP Auto-Tuning."
        };
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "int tcp show global",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            var output = p?.StandardOutput.ReadToEnd();
            p?.WaitForExit(4000);

            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("Receive Window Auto-Tuning Level"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1) return parts[1].Trim();
                    }
                }
            }
        }
        catch { }
        return "Unknown";
    }
}
