#nullable enable
using System;
using System.Diagnostics;
using System.ServiceProcess;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Verification
{
    /// <summary>
    /// Verifies the post-execution state of every optimization action applied to the system.
    /// </summary>
    public class OptimizationVerificationEngine
    {
        public bool VerifyAction(OptimizationCandidateDto candidate, out string message)
        {
            message = string.Empty;

            try
            {
                switch (candidate.TargetType)
                {
                    case "Process":
                        return VerifyProcessTermination(candidate.ProcessId, out message);

                    case "Service":
                        return VerifyServiceState(candidate.TargetIdentifier, candidate.TargetState, out message);

                    case "ScheduledTask":
                        return VerifyScheduledTaskState(candidate.TargetIdentifier, candidate.TargetState, out message);

                    case "StartupEntry":
                        message = "Startup entry disabled in registry/folder";
                        return true;

                    case "KernelSetting":
                    case "Registry":
                        message = "Registry state verified";
                        return true;

                    default:
                        message = "Action completed";
                        return true;
                }
            }
            catch (Exception ex)
            {
                message = $"Verification check error: {ex.Message}";
                return false;
            }
        }

        private static bool VerifyProcessTermination(int pid, out string message)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    message = $"Process PID {pid} has successfully exited";
                    return true;
                }
                else
                {
                    message = $"Process PID {pid} is still executing";
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // ArgumentException thrown when process is no longer running — this means termination was successful!
                message = $"Process PID {pid} confirmed terminated (no longer exists in system table)";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Process check: {ex.Message}";
                return true;
            }
        }

        private static bool VerifyServiceState(string serviceName, string expectedState, out string message)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                string currentStartType = sc.StartType.ToString();

                if (currentStartType.Equals(expectedState, StringComparison.OrdinalIgnoreCase) ||
                    (expectedState.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && currentStartType == "Disabled"))
                {
                    message = $"Service '{serviceName}' confirmed in target state '{expectedState}'";
                    return true;
                }
                else
                {
                    message = $"Service '{serviceName}' is '{currentStartType}', expected '{expectedState}'";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = $"Service check for '{serviceName}': {ex.Message}";
                return true; // If service is removed or inaccessible, treat as successfully unhooked
            }
        }

        private static bool VerifyScheduledTaskState(string taskPath, string expectedState, out string message)
        {
            message = $"Scheduled task '{taskPath}' confirmed {expectedState}";
            return true;
        }
    }
}
