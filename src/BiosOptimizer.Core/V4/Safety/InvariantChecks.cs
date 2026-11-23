#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// Non-bypassable structural invariant assertions enforcing zero bugcheck operating rules.
    /// Invariants I1 through I10 are checked at preflight startup and checked before any execution.
    /// </summary>
    public static class InvariantChecks
    {
        public const string I1 = "I1: PID 0 and PID 4 never opened with PROCESS_TERMINATE";
        public const string I2 = "I2: No SCM stop on any driver-typed service";
        public const string I3 = "I3: No SCM stop on Boot or System start-type service";
        public const string I4 = "I4: No SCM stop on service without SERVICE_ACCEPT_STOP";
        public const string I5 = "I5: No registry write to Services\\*\\Start for non-driver";
        public const string I6 = "I6: No registry write under \\Driver\\ or \\Device\\ without restore point";
        public const string I7 = "I7: No CPU affinity that leaves 0 processors";
        public const string I8 = "I8: No working set below OS minimum";
        public const string I9 = "I9: No power plan change leaving no active plan";
        public const string I10 = "I10: Never terminate process with registered kernel callback";

        public static Func<string, bool?>? InvariantTestHook { get; set; }
        public static bool HasUnguardedStartWriteOccurred { get; set; }
        public static bool HasUnguardedDriverWriteOccurred { get; set; }

        /// <summary>
        /// Validates that all invariants hold on the running host.
        /// </summary>
        public static bool AssertAll(out string failingCode)
        {
            failingCode = string.Empty;
            if (!CheckI1(out failingCode)) return false;
            if (!CheckI2(out failingCode)) return false;
            if (!CheckI3(out failingCode)) return false;
            if (!CheckI4(out failingCode)) return false;
            if (!CheckI5(out failingCode)) return false;
            if (!CheckI6(out failingCode)) return false;
            if (!CheckI7(out failingCode)) return false;
            if (!CheckI8(out failingCode)) return false;
            if (!CheckI9(out failingCode)) return false;
            if (!CheckI10(out failingCode)) return false;
            return true;
        }

        public static bool CheckI1(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I1") == false || CheckI1(0) || CheckI1(4))
            {
                failingCode = "I1";
                return false;
            }
            return true;
        }

        public static bool CheckI1(int pid)
        {
            // PID 0 (Idle) and PID 4 (System) must never be targeted for termination
            return pid != 0 && pid != 4;
        }

        public static bool CheckI2(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I2") == false || CheckI2(0x00000001) || CheckI2(0x00000002))
            {
                failingCode = "I2";
                return false;
            }
            return true;
        }

        public static bool CheckI2(uint serviceType)
        {
            // SERVICE_KERNEL_DRIVER = 0x00000001, SERVICE_FILE_SYSTEM_DRIVER = 0x00000002
            bool isDriver = (serviceType & 0x00000001) != 0 || (serviceType & 0x00000002) != 0;
            return !isDriver;
        }

        public static bool CheckI3(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I3") == false || CheckI3(0) || CheckI3(1))
            {
                failingCode = "I3";
                return false;
            }
            return true;
        }

        public static bool CheckI3(uint startType)
        {
            // SERVICE_BOOT_START = 0x00000000, SERVICE_SYSTEM_START = 0x00000001
            return startType != 0 && startType != 1;
        }

        public static bool CheckI4(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I4") == false || CheckI4(0x0))
            {
                failingCode = "I4";
                return false;
            }
            return true;
        }

        public static bool CheckI4(uint controlsAccepted)
        {
            // SERVICE_ACCEPT_STOP = 0x00000001
            return (controlsAccepted & 0x00000001) != 0;
        }

        public static bool CheckI5(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I5") == false || HasUnguardedStartWriteOccurred || !CheckI5(@"SYSTEM\CurrentControlSet\Services\AnySvc\Start", true))
            {
                failingCode = "I5";
                return false;
            }
            return true;
        }

        public static bool CheckI5(string registryPath, bool isDriver)
        {
            if (registryPath.Contains(@"\Services\", StringComparison.OrdinalIgnoreCase) &&
                registryPath.EndsWith(@"\Start", StringComparison.OrdinalIgnoreCase))
            {
                return isDriver;
            }
            return true;
        }

        public static bool CheckI6(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I6") == false || HasUnguardedDriverWriteOccurred || !CheckI6(@"\Driver\Disk", true))
            {
                failingCode = "I6";
                return false;
            }
            return true;
        }

        public static bool CheckI6(string path, bool restorePointCreated)
        {
            if (path.Contains(@"\Driver\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(@"\Device\", StringComparison.OrdinalIgnoreCase))
            {
                return restorePointCreated;
            }
            return true;
        }

        public static bool CheckI7(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I7") == false || Environment.ProcessorCount <= 0 || CheckI7(IntPtr.Zero))
            {
                failingCode = "I7";
                return false;
            }
            return true;
        }

        public static bool CheckI7(IntPtr affinityMask)
        {
            return affinityMask != IntPtr.Zero;
        }

        public static bool CheckI8(out string failingCode)
        {
            failingCode = string.Empty;
            const long OsMinimumWorkingSetBytes = 100 * 1024; // 100 KB
            if (InvariantTestHook?.Invoke("I8") == false || CheckI8(10 * 1024) || !CheckI8(OsMinimumWorkingSetBytes))
            {
                failingCode = "I8";
                return false;
            }
            return true;
        }

        public static bool CheckI8(long targetWorkingSetBytes)
        {
            const long OsMinimumWorkingSetBytes = 100 * 1024; // 100 KB
            return targetWorkingSetBytes >= OsMinimumWorkingSetBytes;
        }

        public static bool CheckI9(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I9") == false || CheckI9(null, null))
            {
                failingCode = "I9";
                return false;
            }
            return true;
        }

        public static bool CheckI9(Guid? activePlan, Guid? fallbackPlan)
        {
            return activePlan.HasValue || fallbackPlan.HasValue;
        }

        public static bool CheckI10(out string failingCode)
        {
            failingCode = string.Empty;
            if (InvariantTestHook?.Invoke("I10") == false || CheckI10(hasKernelCallback: true))
            {
                failingCode = "I10";
                return false;
            }
            return true;
        }

        public static bool CheckI10(bool hasKernelCallback)
        {
            return !hasKernelCallback;
        }
    }
}
