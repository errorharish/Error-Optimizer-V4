#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BiosOptimizer.Core.Implementations
{
    public sealed class JobObjectSafetyReport
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public bool IsInExistingJob { get; init; }
        public bool IsHardLimitSupported { get; init; }
        public string FrameworkType { get; init; } = "Unknown";
        public string TechnicalLimitation { get; init; } = string.Empty;
        public string RecommendedMechanism { get; init; } = "SoftWorkingSetManagement";
    }

    /// <summary>
    /// Forensic evaluation of Windows Job Objects compatibility for Discord, Spotify, and desktop applications.
    /// Evaluates whether hard process/job memory limits (JOB_OBJECT_LIMIT_JOB_MEMORY / JOB_OBJECT_LIMIT_PROCESS_MEMORY)
    /// can be safely enforced without causing allocator aborts, access denied errors, or process termination.
    /// </summary>
    public static class JobObjectMemorySafety
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr ProcessHandle, IntPtr JobHandle, out bool Result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// Evaluates whether a process can safely receive hard Job Object memory limits.
        /// </summary>
        public static JobObjectSafetyReport EvaluateCompatibility(Process process)
        {
            if (process == null)
            {
                return new JobObjectSafetyReport
                {
                    ProcessId = 0,
                    ProcessName = "Null",
                    IsInExistingJob = false,
                    IsHardLimitSupported = false,
                    FrameworkType = "None",
                    TechnicalLimitation = "Process instance is null."
                };
            }

            int pid = 0;
            string name = string.Empty;
            try
            {
                pid = process.Id;
                name = process.ProcessName;
            }
            catch { }

            bool isDiscord = name.Contains("Discord", StringComparison.OrdinalIgnoreCase);
            bool isSpotify = name.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
            bool isElectronChromium = isDiscord || isSpotify;

            bool inJob = false;
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (hProc != IntPtr.Zero)
            {
                try
                {
                    IsProcessInJob(hProc, IntPtr.Zero, out inJob);
                }
                catch { }
                finally
                {
                    CloseHandle(hProc);
                }
            }

            if (isElectronChromium)
            {
                return new JobObjectSafetyReport
                {
                    ProcessId = pid,
                    ProcessName = name,
                    IsInExistingJob = inJob,
                    IsHardLimitSupported = false, // Hard Job Object caps are unsafe for Electron/Chromium
                    FrameworkType = isDiscord ? "Electron / Chromium Multi-Process Architecture" : "CEF / Chromium Architecture",
                    TechnicalLimitation =
                        "Discord and Spotify utilize multi-process Chromium/Electron architectures with internal sandbox Job Objects. " +
                        "Enforcing hard Windows Job Object memory caps (JOB_OBJECT_LIMIT_JOB_MEMORY / PROCESS_MEMORY) causes Chromium's " +
                        "PartitionAlloc and V8 allocators to abort immediately (exit code 0xC0000005 / 0x80000003) upon allocation failure, " +
                        "and causes child process spawn failures (ERROR_ACCESS_DENIED) due to nested job constraints. " +
                        "Soft Working-Set Management via SetProcessWorkingSetSizeEx / EmptyWorkingSet is the supported, non-destructive path.",
                    RecommendedMechanism = "SoftWorkingSetManagement"
                };
            }

            return new JobObjectSafetyReport
            {
                ProcessId = pid,
                ProcessName = name,
                IsInExistingJob = inJob,
                IsHardLimitSupported = false, // Never enable hard limits by default for third party desktop apps
                FrameworkType = "Standard Windows Desktop",
                TechnicalLimitation = inJob
                    ? "Process is already assigned to a Windows Job Object. Nested assignment without breakaway flag can cause process assignment failure."
                    : "Hard memory limits risk process termination if memory allocations fail. Soft working-set management is recommended.",
                RecommendedMechanism = "SoftWorkingSetManagement"
            };
        }
    }
}
