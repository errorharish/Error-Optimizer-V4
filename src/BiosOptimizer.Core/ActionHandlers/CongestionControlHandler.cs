using System.Diagnostics;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class CongestionControlHandler : IActionHandler
{
    public string ActionName => "SetCongestionControl";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        // Require Windows 10/11
        return (context.IsWindows10 || context.IsWindows11) ? TargetState.Ready : TargetState.NotApplicable;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var targetValue = entry.Value?.ToString() ?? "ctcp"; // ctcp or cubic or default
        var currentValue = GetCurrentValueDisplay(entry);
        
        if (currentValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase))
        {
            return new OptimizationResult
            {
                Status = ResultStatus.AlreadyOptimized,
                ItemId = "CongestionControl",
                DisplayName = $"Congestion Provider already set to {targetValue}"
            };
        }

        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureNetworkTweak(
            entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
            $"Congestion Control Provider → {targetValue}",
            "TCP Supplemental Template",
            currentValue,
            targetValue,
            "SAFE"
        );
        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
        {
            return new OptimizationResult
            {
                Status = ResultStatus.Failed,
                ItemId = "CongestionControl",
                DisplayName = entry.DisplayName,
                Message = bkErr
            };
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"int tcp set supplemental template=internet congestionprovider={targetValue}",
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
                        ItemId = "CongestionControl",
                        DisplayName = $"Set Congestion Provider to {targetValue}",
                        Message = $"Verified TCP congestion provider = {newValue}"
                    };
                }
            }
        }
        catch { }

        return new OptimizationResult
        {
            Status = ResultStatus.Failed,
            ItemId = "CongestionControl",
            Message = "Failed to set Congestion Provider."
        };
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "int tcp show supplemental",
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
                    if (line.Contains("Congestion Control Provider"))
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
