using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiosOptimizer.Core.ActionHandlers;

public class RemoveAppxPackageHandler : IActionHandler
{
    private readonly IPackageManager _packageManager;
    private readonly ISafetyPolicy _safetyPolicy;
    private readonly IVerificationEngine _verificationEngine;

    public string ActionName => "RemoveAppxPackage";

    public RemoveAppxPackageHandler(
        IPackageManager packageManager,
        ISafetyPolicy safetyPolicy,
        IVerificationEngine verificationEngine)
    {
        _packageManager = packageManager;
        _safetyPolicy = safetyPolicy;
        _verificationEngine = verificationEngine;
    }

    private static string? GetActiveUserSid()
    {
        var info = BiosOptimizer.Core.Services.InteractiveUserResolver.ResolveActiveUser();
        return info.IsAvailable ? info.UserSid : null;
    }

    private static bool IsGameDvrDisabled()
    {
        try
        {
            // 1. Check machine-level policy if present
            using (var polKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR"))
            {
                if (polKey?.GetValue("AllowGameDVR") is int polVal && polVal != 0)
                {
                    return false;
                }
            }

            int? dvrEnabled = null;
            int? appCapture = null;

            string? activeSid = GetActiveUserSid();
            if (!string.IsNullOrEmpty(activeSid))
            {
                using var userHive = Registry.Users.OpenSubKey(activeSid, false);
                if (userHive != null)
                {
                    using var cfgKey = userHive.OpenSubKey(@"System\GameConfigStore");
                    if (cfgKey?.GetValue("GameDVR_Enabled") is int v1) dvrEnabled = v1;

                    using var gvrKey = userHive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
                    if (gvrKey?.GetValue("AppCaptureEnabled") is int v2) appCapture = v2;
                }
            }

            if (dvrEnabled == null)
            {
                using var cfgKey = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
                if (cfgKey?.GetValue("GameDVR_Enabled") is int v1) dvrEnabled = v1;
            }

            if (appCapture == null)
            {
                using var gvrKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
                if (gvrKey?.GetValue("AppCaptureEnabled") is int v2) appCapture = v2;
            }

            return (dvrEnabled == null || dvrEnabled == 0) && (appCapture == null || appCapture == 0);
        }
        catch
        {
            return false;
        }
    }

    private static void DisableGameDvrRegistry()
    {
        try
        {
            string? activeSid = GetActiveUserSid();
            if (!string.IsNullOrEmpty(activeSid))
            {
                using var userHive = Registry.Users.OpenSubKey(activeSid, true);
                if (userHive != null)
                {
                    using var cfgKey = userHive.CreateSubKey(@"System\GameConfigStore");
                    cfgKey?.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);

                    using var gvrKey = userHive.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
                    gvrKey?.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
                }
            }

            using (var cfgKey = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore"))
            {
                cfgKey?.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
            }
            using (var gvrKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR"))
            {
                gvrKey?.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
            }

            using (var polKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR"))
            {
                polKey?.SetValue("AllowGameDVR", 0, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        if (entry.Target.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return TargetState.Protected;
        }

        var package = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
        if (package == null)
        {
            // When absent from system, target is not available for removal
            return TargetState.NotAvailable;
        }
        
        return (!package.IsFramework && !package.IsSystemComponent) ? TargetState.Ready : TargetState.Protected;
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        if (entry.Target.Contains("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase))
        {
            var pkg = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
            bool dvrDisabled = IsGameDvrDisabled();
            if (pkg == null && dvrDisabled) return "Removed";
            if (pkg != null) return $"Installed ({pkg.Version})";
            return dvrDisabled ? "Removed" : "Enabled (DVR Overlay)";
        }

        var package = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
        return package != null ? $"Installed ({package.Version})" : "Not Installed";
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var result = new OptimizationResult { ItemId = entry.Id, DisplayName = entry.DisplayName, Status = ResultStatus.Failed };

        if (entry.Target.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = ResultStatus.Blocked;
            result.Message = "Target is protected by policy: Spotify is shielded from debloater removal.";
            return result;
        }

        if (_safetyPolicy.IsProtectedTarget(entry.Target))
        {
            result.Status = ResultStatus.Blocked;
            result.Message = "Target is protected by Debloat safety policy.";
            return result;
        }

        // Special handling for Xbox Gaming Overlay to preserve GamingServices and Game Pass
        if (entry.Target.Contains("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase))
        {
            var existingPkg = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
            bool isDvrOff = IsGameDvrDisabled();

            if (existingPkg == null && isDvrOff)
            {
                result.Status = ResultStatus.AlreadyOptimized;
                result.Message = "Xbox Game Bar overlay is disabled; Microsoft Gaming Services and Game Pass dependencies preserved.";
                return result;
            }

            // Capture before state
            var dvrTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureGenericTweak(
                entry.SourceProfile is { Length: > 0 } sp1 ? sp1 : "Debloat",
                "appx.xboxgamingoverlay",
                "Xbox Game Bar Overlay Disablement",
                "Gaming",
                "RegistryAndAppx",
                isDvrOff ? "Disabled" : "Enabled",
                "Disabled",
                "Reversible",
                false,
                "SAFE"
            );
            _ = BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(dvrTx, out _);

            // Terminate active Game Bar processes
            try
            {
                foreach (var pName in new[] { "GameBar", "GameBarFT", "XboxGamingOverlay" })
                {
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName(pName))
                    {
                        try { proc.Kill(); proc.WaitForExit(1000); } catch { }
                    }
                }
            }
            catch { }

            // Disable registry hooks
            DisableGameDvrRegistry();

            // If package is installed for user, remove it while keeping system image & dependencies
            if (existingPkg != null)
            {
                _packageManager.RemovePackage(existingPkg.FullName, false);
            }

            result.Status = ResultStatus.Success;
            result.Message = "Xbox Game Bar overlay disabled; Microsoft Gaming Services and Game Pass dependencies preserved.";
            return result;
        }

        var package = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
        if (package == null)
        {
            result.Status = ResultStatus.AlreadyOptimized;
            result.Message = "Package is already removed from current user profile.";
            return result;
        }

        // Capture before state into Universal Backup Manager and verify SHA-256 record before removal
        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureGenericTweak(
            entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
            $"appx.{entry.Target}".ToLowerInvariant(),
            $"AppX Package Removal: {package.Name}",
            "Storage",
            "AppxPackage",
            $"Installed (v{package.Version})",
            "Removed",
            "NonReversible",
            false,
            "SAFE"
        );
        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
        {
            result.Status = ResultStatus.Failed;
            result.Message = bkErr;
            return result;
        }

        // Safely terminate active client processes to prevent deployment unregistration locks
        try
        {
            var pNames = new System.Collections.Generic.List<string>();
            if (package.Name.Contains("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase))
                pNames.AddRange(new[] { "GameBar", "GameBarFT", "XboxGamingOverlay" });
            else if (package.Name.Contains("XboxApp", StringComparison.OrdinalIgnoreCase))
                pNames.Add("XboxApp");
            else if (package.Name.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                pNames.Add("Spotify");
            else if (package.Name.Contains("Clipchamp", StringComparison.OrdinalIgnoreCase))
                pNames.Add("Clipchamp");

            foreach (var pName in pNames)
            {
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName(pName))
                {
                    try { proc.Kill(); proc.WaitForExit(1000); } catch { }
                }
            }
        }
        catch { }

        bool removed = _packageManager.RemovePackage(package.FullName, allUsers: true);
        
        var verifyPackage = _packageManager.GetPackage(entry.Target, activeUserOnly: true);
        if (verifyPackage == null)
        {
            result.Status = ResultStatus.Success;
            result.Message = "Package removed and verified from current user profile.";
        }
        else
        {
            result.Status = ResultStatus.Failed;
            result.Message = "Verification failed: Package still present after removal.";
        }

        return result;
    }
}