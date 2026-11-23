using System.Runtime.InteropServices;
using Microsoft.Win32;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class KeyboardOptimizationHandler : IActionHandler
{
    public string ActionName => "SetKeyboardOptimization";

    private const string TargetKey = @"Control Panel\Keyboard";
    private const uint SPI_SETKEYBOARDDELAY = 0x0017;
    private const uint SPI_SETKEYBOARDSPEED = 0x000B;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        return TargetState.Ready;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        try
        {
            var (k, _, _, _) = BiosOptimizer.Core.Implementations.RegistryTweakEngine.IsRunningAsSystem()
                ? BiosOptimizer.Core.Implementations.RegistryTweakEngine.OpenTargetKeyWithPreflight("HKCU", TargetKey, writable: true)
                : (null, Microsoft.Win32.RegistryView.Registry64, "", "");
            using var key = k ?? Registry.CurrentUser.CreateSubKey(TargetKey);
            if (key != null)
            {
                var prevDelay = key.GetValue("KeyboardDelay")?.ToString() ?? "";
                var prevSpeed = key.GetValue("KeyboardSpeed")?.ToString() ?? "";
                if (prevDelay == "0" && prevSpeed == "31")
                {
                    return new OptimizationResult
                    {
                        Status = ResultStatus.AlreadyOptimized,
                        ItemId = "KeyboardSpeed",
                        DisplayName = "Keyboard Optimization already applied"
                    };
                }

                var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureInputTweak(
                    entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
                    "Keyboard Optimization",
                    "HKCU\\Control Panel\\Keyboard",
                    $"Delay={prevDelay},Speed={prevSpeed}",
                    "Delay=0,Speed=31",
                    "SAFE"
                );
                if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
                {
                    return new OptimizationResult { Status = ResultStatus.Failed, ItemId = "KeyboardSpeed", DisplayName = entry.DisplayName, Message = bkErr };
                }

                key.SetValue("KeyboardDelay", "0", RegistryValueKind.String);
                key.SetValue("KeyboardSpeed", "31", RegistryValueKind.String);

                bool spiDelay = SystemParametersInfo(SPI_SETKEYBOARDDELAY, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                bool spiSpeed = SystemParametersInfo(SPI_SETKEYBOARDSPEED, 31, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

                var readDelay = key.GetValue("KeyboardDelay")?.ToString();
                var readSpeed = key.GetValue("KeyboardSpeed")?.ToString();
                if (readDelay == "0" && readSpeed == "31")
                {
                    return new OptimizationResult
                    {
                        Status = ResultStatus.Success,
                        ItemId = "KeyboardSpeed",
                        DisplayName = "Optimized Keyboard Delay and Repeat Rate",
                        Message = $"Verified KeyboardDelay=0, KeyboardSpeed=31 (SPI applied: delay={spiDelay}, speed={spiSpeed})."
                    };
                }
            }
        }
        catch (Exception ex)
        {
            return new OptimizationResult { Status = ResultStatus.Failed, Message = ex.Message };
        }

        return new OptimizationResult { Status = ResultStatus.Failed, Message = "Could not open or verify key" };
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
                var val = key.GetValue("KeyboardDelay");
                if (val != null) return $"Delay {val}";
            }
        }
        catch { }
        return "Unknown";
    }
}
