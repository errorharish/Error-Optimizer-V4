#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// PID reuse prevention and identity validation engine.
    /// Ensures that before terminating any process, the target process is mathematically identical
    /// to the candidate that was classified and approved in the plan.
    /// </summary>
    public class ProcessIdentitySafety
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr hProcess,
            out FILETIME lpCreationTime,
            out FILETIME lpExitTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// Validates that the process currently running at <paramref name="candidatePid"/> matches
        /// the recorded identity in <paramref name="expectedCandidate"/>.
        /// </summary>
        public bool ValidateIdentity(OptimizationCandidateDto expectedCandidate, out string abortReason)
        {
            abortReason = string.Empty;

            if (expectedCandidate == null)
            {
                abortReason = "Null candidate specification";
                return false;
            }

            int pid = expectedCandidate.ProcessId;
            if (pid <= 4)
            {
                abortReason = "System kernel PID (0 or 4) cannot be targeted";
                return false;
            }

            Process targetProc;
            try
            {
                targetProc = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                abortReason = $"Process PID {pid} has already exited";
                return false;
            }
            catch (Exception ex)
            {
                abortReason = $"Unable to inspect process PID {pid}: {ex.Message}";
                return false;
            }

            using (targetProc)
            {
                if (targetProc.HasExited)
                {
                    abortReason = $"Process PID {pid} has exited";
                    return false;
                }

                // 1. Check Process Name Match
                string currentName = targetProc.ProcessName;
                if (!string.IsNullOrEmpty(expectedCandidate.DisplayName) &&
                    !expectedCandidate.DisplayName.Equals(currentName, StringComparison.OrdinalIgnoreCase) &&
                    !expectedCandidate.TargetIdentifier.Equals(currentName, StringComparison.OrdinalIgnoreCase))
                {
                    abortReason = $"PID reuse detected: PID {pid} is now '{currentName}', expected '{expectedCandidate.DisplayName}'";
                    return false;
                }

                // 2. Check Process Start Time Match
                DateTime currentStartTime = GetProcessStartTime(pid);
                if (expectedCandidate.ProcessStartTime != default && currentStartTime != default)
                {
                    TimeSpan delta = (currentStartTime - expectedCandidate.ProcessStartTime).Duration();
                    if (delta.TotalSeconds > 2.0)
                    {
                        abortReason = $"PID reuse detected: Start time changed by {delta.TotalSeconds:F1}s (Candidate: {expectedCandidate.ProcessStartTime:s}, Current: {currentStartTime:s})";
                        return false;
                    }
                }

                // 3. Check Executable Path Match
                if (!string.IsNullOrEmpty(expectedCandidate.ExecutablePath))
                {
                    string currentPath = "";
                    try
                    {
                        currentPath = targetProc.MainModule?.FileName ?? "";
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(currentPath) &&
                        !currentPath.Equals(expectedCandidate.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        abortReason = $"PID reuse detected: Executable path mismatch ('{currentPath}' vs expected '{expectedCandidate.ExecutablePath}')";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Retrieves the exact process creation timestamp via Win32 GetProcessTimes.
        /// </summary>
        public static DateTime GetProcessStartTime(int pid)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return default;

            try
            {
                if (GetProcessTimes(hProcess, out var creation, out _, out _, out _))
                {
                    long fileTime = ((long)creation.dwHighDateTime << 32) + creation.dwLowDateTime;
                    if (fileTime > 0)
                    {
                        return DateTime.FromFileTimeUtc(fileTime);
                    }
                }
            }
            catch { }
            finally
            {
                CloseHandle(hProcess);
            }

            return default;
        }
    }
}
