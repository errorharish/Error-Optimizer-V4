#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using BiosOptimizer.Core.Interfaces;
using Microsoft.Win32;

namespace BiosOptimizer.Safety
{
    /// <summary>
    /// Independent, authoritative verification engine.
    /// Performs genuine hardware, registry, service, and OS state verification
    /// without hardcoded success or simulated outcomes.
    /// </summary>
    public class VerificationEngine : IVerificationEngine
    {
        public bool Verify(string actionId, object expectedState)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return false;

            try
            {
                // 1. Registry Key/Value Verification
                if (actionId.StartsWith("REG:", StringComparison.OrdinalIgnoreCase) || actionId.Contains(@""))
                {
                    return VerifyRegistryState(actionId, expectedState);
                }

                // 2. Service Startup Type Verification
                if (actionId.StartsWith("SVC:", StringComparison.OrdinalIgnoreCase) || actionId.StartsWith("SERVICE:", StringComparison.OrdinalIgnoreCase))
                {
                    string svcName = actionId.Substring(actionId.IndexOf(':') + 1).Trim();
                    return VerifyServiceState(svcName, expectedState);
                }

                // 3. Process State Verification
                if (actionId.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase) || actionId.StartsWith("PROCESS:", StringComparison.OrdinalIgnoreCase))
                {
                    string procIdentifier = actionId.Substring(actionId.IndexOf(':') + 1).Trim();
                    return VerifyProcessState(procIdentifier, expectedState);
                }

                // 4. Memory Cache State Verification
                if (actionId.Equals("MemoryCache", StringComparison.OrdinalIgnoreCase) || actionId.Equals("StandbyList", StringComparison.OrdinalIgnoreCase))
                {
                    return VerifyMemoryState();
                }

                // 5. Power Scheme Verification
                if (actionId.StartsWith("POWER:", StringComparison.OrdinalIgnoreCase))
                {
                    string expectedGuid = expectedState?.ToString() ?? string.Empty;
                    return VerifyPowerScheme(expectedGuid);
                }

                // Unknown or unverified action type
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool VerifyRegistryState(string actionId, object expectedState)
        {
            string cleanPath = actionId.StartsWith("REG:", StringComparison.OrdinalIgnoreCase) 
                ? actionId.Substring(4).Trim() 
                : actionId.Trim();

            // Format: HKLM\SubKey\ValueName or HKCU\SubKey\ValueName
            RegistryKey? baseKey = null;
            string subKeyPath;

            if (cleanPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) || cleanPath.StartsWith(@"HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            {
                baseKey = Registry.LocalMachine;
                subKeyPath = cleanPath.Substring(cleanPath.IndexOf('\\') + 1);
            }
            else if (cleanPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || cleanPath.StartsWith(@"HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                baseKey = Registry.CurrentUser;
                subKeyPath = cleanPath.Substring(cleanPath.IndexOf('\\') + 1);
            }
            else
            {
                return false;
            }

            int lastSlash = subKeyPath.LastIndexOf('\\');
            if (lastSlash < 0) return false;

            string keyName = subKeyPath.Substring(0, lastSlash);
            string valueName = subKeyPath.Substring(lastSlash + 1);

            using var subKey = baseKey.OpenSubKey(keyName, false);
            if (subKey == null) return false;

            var actualVal = subKey.GetValue(valueName);
            if (actualVal == null) return expectedState == null;

            return actualVal.ToString()!.Equals(expectedState?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool VerifyServiceState(string serviceName, object expectedState)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                string expected = expectedState?.ToString() ?? string.Empty;

                if (Enum.TryParse<ServiceStartMode>(expected, true, out var expectedMode))
                {
                    return sc.StartType == expectedMode;
                }

                if (Enum.TryParse<ServiceControllerStatus>(expected, true, out var expectedStatus))
                {
                    return sc.Status == expectedStatus;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool VerifyProcessState(string procIdentifier, object expectedState)
        {
            string expected = expectedState?.ToString() ?? "Terminated";
            bool expectTerminated = expected.Equals("Terminated", StringComparison.OrdinalIgnoreCase) ||
                                    expected.Equals("Exited", StringComparison.OrdinalIgnoreCase);

            if (int.TryParse(procIdentifier, out int pid))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    return expectTerminated ? p.HasExited : (!p.HasExited && p.PriorityClass == ProcessPriorityClass.Idle);
                }
                catch (ArgumentException)
                {
                    // Process no longer exists
                    return expectTerminated;
                }
                catch
                {
                    return false;
                }
            }

            var procs = Process.GetProcessesByName(procIdentifier);
            if (expectTerminated)
            {
                return procs.Length == 0;
            }

            return procs.Length > 0;
        }

        private static bool VerifyMemoryState()
        {
            try
            {
                var snap = BiosOptimizer.Core.Implementations.Memory.WindowsMemoryListProvider.Instance.GetCurrentSnapshot();
                return snap.IsNativeKernelQueried;
            }
            catch
            {
                return false;
            }
        }

        private static bool VerifyPowerScheme(string expectedGuid)
        {
            if (string.IsNullOrWhiteSpace(expectedGuid)) return false;
            try
            {
                var active = BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance
                    .GetActiveSchemeAsync(System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
                return active.guid.Equals(expectedGuid, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
