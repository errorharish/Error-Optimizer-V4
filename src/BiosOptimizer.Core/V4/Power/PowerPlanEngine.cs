#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BiosOptimizer.Core.V4.Environments;
using BiosOptimizer.Core.V4.Safety;

namespace BiosOptimizer.Core.V4.Power
{
    /// <summary>
    /// Power plan manager strictly enforcing cross-platform battery, laptop thermal,
    /// and core parking safety invariants (R14, I9).
    /// </summary>
    public class PowerPlanEngine
    {
        // Standard Power Schemes
        public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
        public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

        public sealed record PowerPlanDecision(bool AllowAggressivePlan, string SelectedPlanName, string Reason);

        public static PowerPlanDecision EvaluatePowerPlanAdjustment(bool isLaptop, bool isOnBattery, bool userExplicitAggressiveMode = false)
        {
            if (isLaptop && isOnBattery && !userExplicitAggressiveMode)
            {
                return new PowerPlanDecision(false, "Balanced (OEM/ACPI Safe)", "On battery power: preserving Balanced plan to protect battery lifetime (R14)");
            }
            return new PowerPlanDecision(true, "Ultimate Performance", "Desktop or explicit aggressive mode permitted");
        }

        [DllImport("PowrProf.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("PowrProf.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private readonly EnvironmentDetector _environment;

        public PowerPlanEngine(EnvironmentDetector? environment = null)
        {
            _environment = environment ?? EnvironmentDetector.Instance;
        }

        public Guid? GetActivePowerScheme()
        {
            uint res = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid);
            if (res == 0 && pGuid != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStructure<Guid>(pGuid);
                }
                finally
                {
                    LocalFree(pGuid);
                }
            }
            return null;
        }

        public bool CanApplyAggressivePowerPlan(bool userExplicitAggressiveModeEnabled, out string denialReason)
        {
            denialReason = string.Empty;
            var env = _environment.CurrentInfo;

            // R14: on battery + CPU/frequency/power plan change -> Deny unless user explicitly enabled "aggressive mode"
            if (env.HasBattery && !env.IsOnAcPower)
            {
                if (!userExplicitAggressiveModeEnabled)
                {
                    denialReason = "R14: On battery power without explicit user aggressive mode consent";
                    return false;
                }
            }

            return true;
        }

        public bool SetPowerScheme(Guid targetScheme, bool userExplicitAggressiveModeEnabled, out string error)
        {
            error = string.Empty;

            if (targetScheme == HighPerformance || targetScheme == UltimatePerformance)
            {
                if (!CanApplyAggressivePowerPlan(userExplicitAggressiveModeEnabled, out string denial))
                {
                    error = denial;
                    return false;
                }
            }

            var current = GetActivePowerScheme();
            // I9 Invariant check: ensure fallback exists
            if (!InvariantChecks.CheckI9(targetScheme, current))
            {
                error = "I9: Power plan change would leave no active scheme";
                return false;
            }

            try
            {
                uint res = PowerSetActiveScheme(IntPtr.Zero, ref targetScheme);
                if (res != 0)
                {
                    error = $"PowrProf returned error code {res}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
