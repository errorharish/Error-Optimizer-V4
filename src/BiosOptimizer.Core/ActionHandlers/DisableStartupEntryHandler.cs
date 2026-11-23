using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Core.ActionHandlers;

public class DisableStartupEntryHandler : IActionHandler
{
    private readonly IStartupManager _startupManager;
    public DisableStartupEntryHandler(IStartupManager startupManager)
    {
        _startupManager = startupManager;
    }

    public string ActionName => "DisableStartupEntry";

    // Registry startup locations to search
    private static readonly (RegistryHive Hive, string Path)[] StartupPaths =
    [
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        return TargetState.Ready; 
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var result = new OptimizationResult { ItemId = entry.Id, DisplayName = entry.DisplayName };
        var currentValue = GetCurrentValueDisplay(entry);

        if (string.Equals(currentValue, "Not Found", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentValue, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = ResultStatus.AlreadyOptimized;
            result.Message = "Startup entry is already disabled or inactive.";
            return result;
        }

        string profile = entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual";

        // Prefer reversible Task Manager StartupApproved / Task Scheduler disable via _startupManager
        try
        {
            var matches = _startupManager.GetStartupEntries()
                .Where(e => e.State == StartupState.Enabled &&
                           (e.ValueName.Contains(entry.Target, StringComparison.OrdinalIgnoreCase) ||
                            e.DisplayName.Contains(entry.Target, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count > 0)
            {
                int disabledCount = 0;
                foreach (var match in matches)
                {
                    var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureTargetedGenericTweak(
                        profile,
                        $"startup.{match.ValueName}".ToLowerInvariant().Replace(' ', '.'),
                        $"Startup Entry: {match.ValueName}",
                        "Startup",
                        "StartupApproved",
                        match.State.ToString(),
                        "Disabled",
                        "StartupRestore",
                        true,
                        "SAFE",
                        targetKey: $"{match.Source}|{match.Path}",
                        valueName: match.ValueName);

                    if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
                    {
                        result.Status = ResultStatus.Failed;
                        result.Message = bkErr;
                        return result;
                    }

                    if (_startupManager.DisableEntry(match.Source, match.Path, match.ValueName))
                    {
                        disabledCount++;
                    }
                }

                if (disabledCount > 0)
                {
                    result.Status = ResultStatus.Success;
                    result.Message = $"Startup entry '{entry.Target}' disabled cleanly (reversible via StartupApproved).";
                    return result;
                }
            }
        }
        catch { }

        // Fallback: backup full Run-key registry value before deleting so BackupManager can restore it
        bool removed = TryBackupAndRemoveFromRegistry(profile, entry.Target);
        if (removed)
        {
            result.Status = ResultStatus.Success;
            result.Message = "Startup entry disabled and original registry value backed up.";
            return result;
        }

        result.Status = ResultStatus.NotApplicable;
        result.Message = "Startup entry not found or could not be disabled.";
        return result;
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            var matches = _startupManager.GetStartupEntries()
                .Where(e => e.ValueName.Contains(entry.Target, StringComparison.OrdinalIgnoreCase) ||
                            e.DisplayName.Contains(entry.Target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                return matches.Any(m => m.State == StartupState.Enabled) ? "Enabled" : "Disabled";
            }

            // Fallback raw registry check
            foreach (var (hive, path) in StartupPaths)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path, false);
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (valueName.Contains(entry.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        return "Enabled";
                    }
                }
            }
        }
        catch { }

        return "Not Found";
    }

    private static bool TryBackupAndRemoveFromRegistry(string profile, string entryName)
    {
        bool removed = false;
        foreach (var (hive, path) in StartupPaths)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path, true);
                if (key == null) continue;

                string hivePrefix = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                foreach (var valueName in key.GetValueNames())
                {
                    if (valueName.Contains(entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        object? oldVal = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        string oldKind = key.GetValueKind(valueName).ToString();
                        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureRegistryTweak(
                            profile,
                            $"Startup Entry: {valueName}",
                            hivePrefix,
                            path,
                            valueName,
                            oldVal?.ToString() ?? "",
                            oldKind,
                            "DELETED",
                            "SAFE"
                        );
                        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out _))
                        {
                            continue;
                        }

                        key.DeleteValue(valueName, false);
                        removed = true;
                    }
                }
            }
            catch { }
        }
        return removed;
    }
}

