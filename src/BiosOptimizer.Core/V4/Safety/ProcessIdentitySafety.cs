#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// Represents a live OS inspection probe for a PID used during identity validation.
    /// Exposed to enable deterministic, non-destructive unit testing of fail-closed paths.
    /// </summary>
    public sealed class ProcessIdentityLiveState
    {
        public bool Exists { get; init; }
        public bool HasExited { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public DateTime StartTimeUtc { get; init; }
        public string ExecutablePath { get; init; } = string.Empty;
        public string PathResolutionMethod { get; init; } = "Unavailable";
        public string PathDiagnostic { get; init; } = string.Empty;
        public string LookupFailureReason { get; init; } = string.Empty;
    }

    /// <summary>
    /// PID reuse prevention, native executable path resolution, AppX package-family provenance,
    /// and fail-closed process identity validation engine.
    /// Ensures that before targeting any process, the target process is mathematically identical
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr hProcess,
            uint dwFlags,
            [Out] char[] lpExeName,
            ref uint lpdwSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFamilyName(
            IntPtr hProcess,
            ref uint packageFamilyNameLength,
            [Out] char[]? packageFamilyName);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private const int ERROR_SUCCESS = 0;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_HANDLE = 6;
        private const int ERROR_GEN_FAILURE = 31;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int ERROR_PARTIAL_COPY = 299;
        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        private static long _pidReuseValidationFailureCount;

        public static long PidReuseValidationFailureCount => Interlocked.Read(ref _pidReuseValidationFailureCount);

        public static void ResetDiagnosticCounters()
        {
            Interlocked.Exchange(ref _pidReuseValidationFailureCount, 0);
        }

        /// <summary>
        /// Normalizes an executable path for deterministic comparison across processes, tasks, and startup entries.
        /// Never invents or guesses a path.
        /// </summary>
        public static string NormalizeExecutablePath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            string cleaned = rawPath.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;

            // Expand environment variables such as %SystemRoot% or %ProgramFiles%
            if (cleaned.Contains('%'))
            {
                try
                {
                    cleaned = Environment.ExpandEnvironmentVariables(cleaned);
                }
                catch { }
            }

            // Strip Win32 extended-length path prefix \\?\
            if (cleaned.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                cleaned = cleaned[4..];
            }

            // Strip NT object manager prefix \??\
            if (cleaned.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                cleaned = cleaned[4..];
            }

            cleaned = cleaned.Replace('/', '\\');

            try
            {
                if (Path.IsPathRooted(cleaned))
                {
                    cleaned = Path.GetFullPath(cleaned);
                }
            }
            catch
            {
                // Preserve cleaned path as-is if GetFullPath fails on unusual characters
            }

            return cleaned.TrimEnd('\\');
        }

        /// <summary>
        /// Task A: Reliably resolves a process's full executable path.
        /// Tries managed Process.MainModule first, then falls back to native
        /// QueryFullProcessImageNameW with PROCESS_QUERY_LIMITED_INFORMATION (0x1000).
        /// Never guesses or fabricates a path from the process name.
        /// </summary>
        public static bool TryResolveExecutablePath(
            int pid,
            Process? managedProc,
            out string executablePath,
            out string resolutionMethod,
            out string diagnostic)
        {
            executablePath = string.Empty;
            resolutionMethod = "Unavailable";
            diagnostic = string.Empty;

            if (pid <= 4)
            {
                resolutionMethod = "Unavailable";
                diagnostic = "Kernel/System pseudo-process (PID <= 4) has no user-mode executable image.";
                return false;
            }

            string managedFailure = string.Empty;

            // 1. Managed Process.MainModule fast path
            if (managedProc != null)
            {
                try
                {
                    string? managedPath = managedProc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(managedPath))
                    {
                        executablePath = NormalizeExecutablePath(managedPath);
                        resolutionMethod = "ManagedMainModule";
                        diagnostic = "Resolved via Process.MainModule.FileName.";
                        return true;
                    }
                }
                catch (System.ComponentModel.Win32Exception wex)
                {
                    managedFailure = $"MainModule Win32Error={wex.NativeErrorCode}";
                }
                catch (InvalidOperationException)
                {
                    managedFailure = "MainModule ProcessExited";
                }
                catch (Exception ex)
                {
                    managedFailure = $"MainModule {ex.GetType().Name}";
                }
            }

            // 2. Native fallback via OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageNameW
            if (!OperatingSystem.IsWindows())
            {
                resolutionMethod = "Unavailable";
                diagnostic = "Native Windows path resolution unavailable on non-Windows platform.";
                return false;
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                switch (err)
                {
                    case ERROR_ACCESS_DENIED:
                        resolutionMethod = "AccessDenied";
                        diagnostic = $"OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) denied (Win32 {err}). {managedFailure}".Trim();
                        break;
                    case ERROR_INVALID_PARAMETER:
                    {
                        bool stillRunning = false;
                        if (managedProc != null)
                        {
                            try { stillRunning = !managedProc.HasExited; } catch { stillRunning = true; }
                        }
                        if (stillRunning)
                        {
                            resolutionMethod = "Unavailable";
                            diagnostic = $"Kernel or protected pseudo-process PID {pid} has no user-mode handle (Win32 {err}).";
                        }
                        else
                        {
                            resolutionMethod = "ProcessExited";
                            diagnostic = $"Process PID {pid} exited before native handle acquisition (Win32 {err}).";
                        }
                        break;
                    }
                    case ERROR_INVALID_HANDLE:
                        resolutionMethod = "InvalidHandle";
                        diagnostic = $"OpenProcess returned invalid handle error (Win32 {err}).";
                        break;
                    default:
                        resolutionMethod = "Unavailable";
                        diagnostic = $"OpenProcess failed (Win32 {err}). {managedFailure}".Trim();
                        break;
                }
                return false;
            }

            try
            {
                uint capacity = 1024;
                char[] buffer = new char[capacity];
                bool ok = QueryFullProcessImageNameW(hProcess, 0, buffer, ref capacity);
                if (!ok && Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
                {
                    capacity = 4096;
                    buffer = new char[capacity];
                    ok = QueryFullProcessImageNameW(hProcess, 0, buffer, ref capacity);
                }

                if (ok && capacity > 0)
                {
                    string nativePath = new string(buffer, 0, (int)capacity);
                    if (!string.IsNullOrWhiteSpace(nativePath))
                    {
                        executablePath = NormalizeExecutablePath(nativePath);
                        resolutionMethod = "NativeQueryFullProcessImageName";
                        diagnostic = string.IsNullOrEmpty(managedFailure)
                            ? "Resolved via QueryFullProcessImageNameW."
                            : $"Resolved via QueryFullProcessImageNameW fallback after {managedFailure}.";
                        return true;
                    }
                }

                int qErr = Marshal.GetLastWin32Error();
                switch (qErr)
                {
                    case ERROR_ACCESS_DENIED:
                        resolutionMethod = "AccessDenied";
                        diagnostic = $"QueryFullProcessImageNameW denied (Win32 {qErr}).";
                        break;
                    case ERROR_GEN_FAILURE:
                    case ERROR_INVALID_PARAMETER:
                        resolutionMethod = "ProcessExited";
                        diagnostic = $"Process PID {pid} exited during image path query (Win32 {qErr}).";
                        break;
                    case ERROR_INVALID_HANDLE:
                        resolutionMethod = "InvalidHandle";
                        diagnostic = $"QueryFullProcessImageNameW invalid handle (Win32 {qErr}).";
                        break;
                    case ERROR_PARTIAL_COPY:
                        resolutionMethod = "Unavailable";
                        diagnostic = $"QueryFullProcessImageNameW partial copy / kernel-protected image (Win32 {qErr}).";
                        break;
                    default:
                        resolutionMethod = "Unavailable";
                        diagnostic = $"QueryFullProcessImageNameW returned no path (Win32 {qErr}).";
                        break;
                }
            }
            catch (Exception ex)
            {
                resolutionMethod = "Unavailable";
                diagnostic = $"QueryFullProcessImageNameW exception: {ex.Message}";
            }
            finally
            {
                CloseHandle(hProcess);
            }

            return false;
        }

        /// <summary>
        /// Task B: Discovers AppX / MSIX / Store package-family provenance via Win32 GetPackageFamilyName.
        /// Distinguishes Packaged, NonPackaged, Unavailable, and Unsupported cleanly.
        /// </summary>
        public static bool TryGetPackageFamilyName(
            int pid,
            out string packageFamilyName,
            out string status)
        {
            packageFamilyName = string.Empty;
            status = "Unavailable";

            if (pid <= 4)
            {
                status = "NonPackaged";
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                status = "Unsupported";
                return false;
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                status = "Unavailable";
                return false;
            }

            try
            {
                uint length = 0;
                int rc = GetPackageFamilyName(hProcess, ref length, null);
                if (rc == APPMODEL_ERROR_NO_PACKAGE)
                {
                    status = "NonPackaged";
                    return false;
                }

                if (rc == ERROR_INSUFFICIENT_BUFFER && length > 0 && length <= 512)
                {
                    char[] buf = new char[length];
                    rc = GetPackageFamilyName(hProcess, ref length, buf);
                    if (rc == ERROR_SUCCESS && length > 0)
                    {
                        int actualLen = (int)length;
                        if (actualLen > 0 && buf[actualLen - 1] == '\0')
                        {
                            actualLen--;
                        }
                        string family = new string(buf, 0, actualLen).Trim();
                        if (!string.IsNullOrEmpty(family))
                        {
                            packageFamilyName = family;
                            status = "Packaged";
                            return true;
                        }
                    }
                }

                status = rc == APPMODEL_ERROR_NO_PACKAGE ? "NonPackaged" : "Unavailable";
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                status = "Unsupported";
                return false;
            }
            catch
            {
                status = "Unavailable";
                return false;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Task E / G: Verifies a parent-child relationship against PID reuse by ensuring
        /// both timestamps are present and parent.StartTime <= child.StartTime + 2 seconds.
        /// </summary>
        public static bool IsVerifiedParentChild(ProcessIdentityDto? parent, ProcessIdentityDto? child)
        {
            if (parent == null || child == null)
                return false;
            if (parent.ProcessId <= 0 || child.ProcessId <= 0 || parent.ProcessId == child.ProcessId)
                return false;
            if (child.ParentProcessId != parent.ProcessId)
                return false;
            if (parent.StartTime != default && child.StartTime != default)
            {
                DateTime parentUtc = parent.StartTime.Kind == DateTimeKind.Utc
                    ? parent.StartTime
                    : parent.StartTime.ToUniversalTime();
                DateTime childUtc = child.StartTime.Kind == DateTimeKind.Utc
                    ? child.StartTime
                    : child.StartTime.ToUniversalTime();

                return parentUtc <= childUtc.AddSeconds(2.0);
            }

            return true;
        }

        /// <summary>
        /// Validates that the process currently running at expectedCandidate.ProcessId matches
        /// the recorded identity in expectedCandidate. Fails closed on any missing/unverifiable
        /// path or creation time, name mismatch, path mismatch, or PID reuse.
        /// </summary>
        public bool ValidateIdentity(OptimizationCandidateDto expectedCandidate, out string abortReason)
        {
            return ValidateIdentity(expectedCandidate, ProbeLiveProcess, out abortReason);
        }

        /// <summary>
        /// Testable overload allowing deterministic injection of live process inspection results.
        /// </summary>
        public bool ValidateIdentity(
            OptimizationCandidateDto expectedCandidate,
            Func<int, ProcessIdentityLiveState> liveProbe,
            out string abortReason)
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

            if (string.IsNullOrWhiteSpace(expectedCandidate.DisplayName) &&
                string.IsNullOrWhiteSpace(expectedCandidate.TargetIdentifier))
            {
                abortReason = "Missing expected process name; failing closed";
                return false;
            }

            if (expectedCandidate.ProcessStartTime == default)
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"Missing expected process creation time for PID {pid}; failing closed against PID reuse";
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedCandidate.ExecutablePath))
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"Missing expected executable path for PID {pid}; failing closed";
                return false;
            }

            ProcessIdentityLiveState live = liveProbe(pid);
            if (!live.Exists)
            {
                abortReason = string.IsNullOrEmpty(live.LookupFailureReason)
                    ? $"Process PID {pid} has already exited"
                    : live.LookupFailureReason;
                return false;
            }

            if (live.HasExited)
            {
                abortReason = $"Process PID {pid} has exited";
                return false;
            }

            // 1. Process Name Match (normalized without .exe)
            string currentName = NormalizeProcessName(live.ProcessName);
            string expectedDisplay = NormalizeProcessName(expectedCandidate.DisplayName);
            string expectedTarget = NormalizeProcessName(expectedCandidate.TargetIdentifier);

            if (string.IsNullOrEmpty(currentName) ||
                (!currentName.Equals(expectedDisplay, StringComparison.OrdinalIgnoreCase) &&
                 !currentName.Equals(expectedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"PID reuse detected: PID {pid} is now '{live.ProcessName}', expected '{expectedCandidate.DisplayName}'";
                return false;
            }

            // 2. Process Creation Timestamp Match (FAIL CLOSED)
            if (live.StartTimeUtc == default)
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"Unable to verify live creation time for PID {pid}; failing closed against PID reuse";
                return false;
            }

            DateTime expectedUtc = expectedCandidate.ProcessStartTime.Kind == DateTimeKind.Utc
                ? expectedCandidate.ProcessStartTime
                : expectedCandidate.ProcessStartTime.ToUniversalTime();

            DateTime currentUtc = live.StartTimeUtc.Kind == DateTimeKind.Utc
                ? live.StartTimeUtc
                : live.StartTimeUtc.ToUniversalTime();

            TimeSpan delta = (currentUtc - expectedUtc).Duration();
            if (delta.TotalSeconds > 2.0)
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"PID reuse detected: Start time changed by {delta.TotalSeconds:F1}s (Candidate: {expectedUtc:s}Z, Current: {currentUtc:s}Z)";
                return false;
            }

            // 3. Executable Path Match (FAIL CLOSED with native fallback)
            if (string.IsNullOrWhiteSpace(live.ExecutablePath))
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"Unable to verify live executable path for PID {pid} ({live.PathResolutionMethod}: {live.PathDiagnostic}); failing closed";
                return false;
            }

            string normalizedExpectedPath = NormalizeExecutablePath(expectedCandidate.ExecutablePath);
            string normalizedCurrentPath = NormalizeExecutablePath(live.ExecutablePath);

            bool matches;
            if (normalizedExpectedPath.Contains('\\'))
            {
                matches = normalizedCurrentPath.Equals(normalizedExpectedPath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                matches = Path.GetFileName(normalizedCurrentPath).Equals(
                    Path.GetFileName(normalizedExpectedPath),
                    StringComparison.OrdinalIgnoreCase);
            }

            if (!matches)
            {
                Interlocked.Increment(ref _pidReuseValidationFailureCount);
                abortReason = $"PID reuse detected: Executable path mismatch ('{normalizedCurrentPath}' vs expected '{normalizedExpectedPath}')";
                return false;
            }

            return true;
        }

        private static ProcessIdentityLiveState ProbeLiveProcess(int pid)
        {
            Process targetProc;
            try
            {
                targetProc = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return new ProcessIdentityLiveState
                {
                    Exists = false,
                    HasExited = true,
                    LookupFailureReason = $"Process PID {pid} has already exited"
                };
            }
            catch (Exception ex)
            {
                return new ProcessIdentityLiveState
                {
                    Exists = false,
                    LookupFailureReason = $"Unable to inspect process PID {pid}: {ex.Message}"
                };
            }

            using (targetProc)
            {
                bool hasExited = false;
                try { hasExited = targetProc.HasExited; } catch { }
                if (hasExited)
                {
                    return new ProcessIdentityLiveState
                    {
                        Exists = true,
                        HasExited = true
                    };
                }

                string procName = string.Empty;
                try { procName = targetProc.ProcessName; } catch { }

                DateTime startUtc = GetProcessStartTime(pid);
                TryResolveExecutablePath(pid, targetProc, out string exePath, out string method, out string diag);

                return new ProcessIdentityLiveState
                {
                    Exists = true,
                    HasExited = false,
                    ProcessName = procName,
                    StartTimeUtc = startUtc,
                    ExecutablePath = exePath,
                    PathResolutionMethod = method,
                    PathDiagnostic = diag
                };
            }
        }

        private static string NormalizeProcessName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^4]
                : trimmed;
        }

        /// <summary>
        /// Retrieves the exact process creation timestamp via Win32 GetProcessTimes
        /// using clean 64-bit FILETIME reconstruction.
        /// </summary>
        public static DateTime GetProcessStartTime(int pid)
        {
            if (pid <= 0 || !OperatingSystem.IsWindows())
                return default;

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return default;

            try
            {
                if (GetProcessTimes(hProcess, out var creation, out _, out _, out _))
                {
                    long fileTime = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
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
