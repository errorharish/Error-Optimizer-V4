using System.Runtime.InteropServices;
using Microsoft.Win32;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class MouseOptimizationHandler : IActionHandler
{
    public string ActionName => "SetMouseOptimization";

    private const string TargetKey = @"Control Panel\Mouse";
    private const string ValueName = "MouseSensitivity";

    private const uint SPI_GETMOUSESPEED = 0x0070;
    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        return TargetState.Ready;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var targetStr = entry.Value?.ToString() ?? "10";
        int targetInt = Math.Clamp(int.TryParse(targetStr, out int p) ? p : 10, 1, 20);
        try
        {
            var (k, _, _, _) = BiosOptimizer.Core.Implementations.RegistryTweakEngine.IsRunningAsSystem()
                ? BiosOptimizer.Core.Implementations.RegistryTweakEngine.OpenTargetKeyWithPreflight("HKCU", TargetKey, writable: true)
                : (null, Microsoft.Win32.RegistryView.Registry64, "", "");
            using var key = k ?? Registry.CurrentUser.CreateSubKey(TargetKey);
            if (key != null)
            {
                var prevSens = key.GetValue(ValueName)?.ToString() ?? "Unknown";
                var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureInputTweak(
                    entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
                    "Mouse Sensitivity",
                    "HKCU\\Control Panel\\Mouse",
                    $"{ValueName}:{prevSens}",
                    $"{ValueName}:{targetInt}",
                    "SAFE"
                );
                if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
                {
                    return new OptimizationResult { Status = ResultStatus.Failed, ItemId = ValueName, DisplayName = entry.DisplayName, Message = bkErr };
                }

                key.SetValue(ValueName, targetInt.ToString(), RegistryValueKind.String);
                bool spiApplied = SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(targetInt), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

                string? regReadBack = key.GetValue(ValueName)?.ToString();
                int liveSpeed = -1;
                bool spiRead = SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref liveSpeed, 0);
                bool verified = string.Equals(regReadBack, targetInt.ToString(), StringComparison.Ordinal) &&
                                (!spiRead || liveSpeed == targetInt);

                if (!verified || !spiApplied)
                {
                    return new OptimizationResult
                    {
                        Status = ResultStatus.Failed,
                        ItemId = ValueName,
                        DisplayName = "Mouse Sensitivity optimization could not be verified",
                        Message = $"Registry readback='{regReadBack}', SPI applied={spiApplied}, liveSpeed={liveSpeed}"
                    };
                }

                return new OptimizationResult
                {
                    Status = ResultStatus.Success,
                    ItemId = ValueName,
                    DisplayName = $"Optimized Mouse Sensitivity ({targetInt}) with live User32 application",
                    Message = $"MouseSensitivity verified at {regReadBack} (live SPI: {liveSpeed})."
                };
            }
        }
        catch (Exception ex)
        {
            return new OptimizationResult { Status = ResultStatus.Failed, Message = ex.Message };
        }

        return new OptimizationResult { Status = ResultStatus.Failed, Message = "Could not open key" };
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        try
        {
            var (k, _, _, _) = BiosOptimizer.Core.Implementations.RegistryTweakEngine.IsRunningAsSystem()
                ? BiosOptimizer.Core.Implementations.RegistryTweakEngine.OpenTargetKeyWithPreflight("HKCU", TargetKey, writable: false)
                : (null, Microsoft.Win32.RegistryView.Registry64, "", "");
            using var key = k ?? Registry.CurrentUser.OpenSubKey(TargetKey);
            if (key != null)
            {
                var val = key.GetValue(ValueName);
                if (val != null) return val.ToString() ?? "Unknown";
            }
        }
        catch { }
        return "Unknown";
    }
}
