#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.Implementations;

public sealed record SystemActionOutcome(bool Success, string Message, object? Data = null);

/// <summary>
/// Implements confirmed OS power/restart actions, safe File Explorer selection, About catalog/service audit,
/// and strict allow-listed external community link launching.
/// </summary>
public static class SystemPowerOperations
{
    private static readonly IReadOnlyDictionary<string, string> ApprovedExternalChannels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discord"] = "https://discord.gg/QxfMNGbm54",
            ["join discord"] = "https://discord.gg/QxfMNGbm54",
            ["https://discord.gg/QxfMNGbm54"] = "https://discord.gg/QxfMNGbm54",
            ["youtube"] = "https://youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve",
            ["watch youtube"] = "https://youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve",
            ["https://youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve"] = "https://youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve",
            ["https://www.youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve"] = "https://youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve",
            ["instagram"] = "https://www.instagram.com/errorexeff?utm_source=ig_web_button_share_sheet&xtok=ZDNlZDc0MzIxNw==",
            ["open instagram"] = "https://www.instagram.com/errorexeff?utm_source=ig_web_button_share_sheet&xtok=ZDNlZDc0MzIxNw==",
            ["https://www.instagram.com/errorexeff?utm_source=ig_web_button_share_sheet&xtok=ZDNlZDc0MzIxNw=="] = "https://www.instagram.com/errorexeff?utm_source=ig_web_button_share_sheet&xtok=ZDNlZDc0MzIxNw==",
            ["kick"] = "https://kick.com/errorexeff",
            ["watch on kick"] = "https://kick.com/errorexeff",
            ["https://kick.com/errorexeff"] = "https://kick.com/errorexeff"
        };

    public static SystemActionOutcome ScheduleRestart(int delaySeconds, bool confirmed, string? reason = null)
    {
        if (!confirmed)
        {
            return new SystemActionOutcome(false, "Explicit confirmation is required before scheduling a Windows restart.");
        }

        int clamped = Math.Clamp(delaySeconds <= 0 ? 60 : delaySeconds, 5, 600);
        string safeComment = string.IsNullOrWhiteSpace(reason)
            ? "ERROR OPTIMIZER requested restart to finalize optimizations."
            : reason.Replace("\"", "'").Trim();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /t {clamped} /c \"{safeComment}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return new SystemActionOutcome(false, "Failed to invoke shutdown.exe.");

            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode == 0)
            {
                return new SystemActionOutcome(
                    true,
                    $"Windows restart scheduled in {clamped} seconds.",
                    new { delaySeconds = clamped, scheduledAtUtc = DateTime.UtcNow, restartAtUtc = DateTime.UtcNow.AddSeconds(clamped) });
            }

            return new SystemActionOutcome(false, string.IsNullOrWhiteSpace(err) ? $"shutdown.exe exited with code {proc.ExitCode}." : err.Trim());
        }
        catch (Exception ex)
        {
            return new SystemActionOutcome(false, $"Failed to schedule restart: {ex.Message}");
        }
    }

    public static SystemActionOutcome CancelScheduledRestart()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/a",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return new SystemActionOutcome(false, "Failed to invoke shutdown.exe.");

            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode == 0)
                return new SystemActionOutcome(true, "Scheduled system restart cancelled.");

            return new SystemActionOutcome(false, string.IsNullOrWhiteSpace(err) ? "No pending system restart to cancel." : err.Trim());
        }
        catch (Exception ex)
        {
            return new SystemActionOutcome(false, $"Failed to cancel restart: {ex.Message}");
        }
    }

    public static SystemActionOutcome RevealInExplorer(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return new SystemActionOutcome(false, "No file or directory path supplied.");

        try
        {
            string fullPath = Path.GetFullPath(targetPath.Trim().Trim('"'));
            bool isFile = File.Exists(fullPath);
            bool isDir = !isFile && Directory.Exists(fullPath);
            if (!isFile && !isDir)
                return new SystemActionOutcome(false, $"Path does not exist: '{fullPath}'.");

            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };

            if (isFile)
                psi.ArgumentList.Add($"/select,{fullPath}");
            else
                psi.ArgumentList.Add(fullPath);

            Process.Start(psi);
            return new SystemActionOutcome(true, $"Revealed '{fullPath}' in File Explorer.", new { path = fullPath });
        }
        catch (Exception ex)
        {
            return new SystemActionOutcome(false, $"Unable to reveal path: {ex.Message}");
        }
    }

    public static SystemActionOutcome OpenApprovedChannelUrl(string? channelOrUrl)
    {
        if (string.IsNullOrWhiteSpace(channelOrUrl))
            return new SystemActionOutcome(false, "Channel identifier is required.");

        string normalized = channelOrUrl.Trim();
        string? resolved = null;

        if (ApprovedExternalChannels.TryGetValue(normalized, out var byKey))
        {
            resolved = byKey;
        }
        else
        {
            foreach (var kv in ApprovedExternalChannels)
            {
                if (normalized.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = kv.Value;
                    break;
                }
            }
        }

        if (resolved == null)
            return new SystemActionOutcome(false, $"External link '{channelOrUrl}' is not in the approved ERROR OPTIMIZER channel allowlist.");

        try
        {
            Process.Start(new ProcessStartInfo(resolved) { UseShellExecute = true });
            return new SystemActionOutcome(true, $"Opened {resolved}.", new { url = resolved });
        }
        catch (Exception ex)
        {
            return new SystemActionOutcome(false, $"Unable to open link: {ex.Message}");
        }
    }

    public static object EvaluateAboutCatalogAudit(bool isServiceConnected = false, string? serviceVersion = null)
    {
        var hw = HardwareDetectionService.Instance.GetSnapshot();
        var audit = OptimizationCatalog.Instance.RunFullAudit(hw, dryRun: true);
        var backupStats = BackupManager.Instance.GetSummaryStats();
        bool isElevated = IsCurrentProcessElevated();
        var entryAsm = Assembly.GetEntryAssembly();
        string asmVer = entryAsm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? entryAsm?.GetName().Version?.ToString()
            ?? typeof(SystemPowerOperations).Assembly.GetName().Version?.ToString()
            ?? "4.2.0.0";

        var primaryGpu = hw.Gpus.FirstOrDefault(g => g.IsPrimary) ?? hw.Gpus.FirstOrDefault();
        string osText = $"{hw.Windows.ProductName} {hw.Windows.DisplayVersion} (Build {hw.Windows.BuildNumber})".Trim();

        return new
        {
            machineName = hw.MachineName,
            architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            osVersion = string.IsNullOrWhiteSpace(osText) ? Environment.OSVersion.VersionString : osText,
            cpuName = string.IsNullOrWhiteSpace(hw.Cpu.Name) ? null : hw.Cpu.Name,
            gpuName = primaryGpu != null && !string.IsNullOrWhiteSpace(primaryGpu.Name) ? primaryGpu.Name : null,
            ramTotalGb = hw.Memory.InstalledPhysicalGb > 0 ? Math.Round(hw.Memory.InstalledPhysicalGb, 1) : (double?)null,
            powerSource = hw.IsLaptop ? (hw.IsOnBattery ? "Battery (DC)" : "AC Power (Laptop)") : "AC Power (Desktop)",
            isElevated,
            appVersion = asmVer,
            serviceConnected = isServiceConnected,
            serviceVersion,
            backupSummary = backupStats,
            catalog = new
            {
                total = audit.TotalOptimizations,
                supported = audit.SupportedCount,
                applicable = audit.ApplicableCount,
                alreadyOptimized = audit.AlreadyOptimizedCount,
                pending = audit.PendingCount,
                notApplicable = audit.NotApplicableCount,
                unsupported = audit.UnsupportedCount,
                requiresReview = audit.RequiresReviewCount
            },
            channels = ApprovedExternalChannels
        };
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
