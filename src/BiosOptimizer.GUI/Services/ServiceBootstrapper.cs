#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BiosOptimizer.GUI.Services
{
    public enum ServiceBootStatus
    {
        Running,
        RegisteredAndStarted,
        RegistrationFailed,
        ElevationDenied,
        ServiceExeMissing,
        ScmError
    }

    /// <summary>
    /// Ensures the BiosOptimizer.Service backend is registered and running on Windows SCM.
    /// Performs one-time elevated registration if the service is not yet registered.
    /// </summary>
    public static class ServiceBootstrapper
    {
        public static string? LastError { get; set; }

        // Test hooks
        public static Func<string, CancellationToken, Task<(int ExitCode, string Output)>>? ScRunner { get; set; }
        public static Func<string, CancellationToken, Task<(bool Success, bool ElevationDenied)>>? ElevateRunner { get; set; }
        public static Func<string?>? ServiceExeLocator { get; set; }

        public static string GetLogPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "ErrorOptimizer", "service_boot.log");
        }

        public static void Log(string message)
        {
            try
            {
                string logPath = GetLogPath();
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, line);
            }
            catch { }
        }

        public static string? LocateServiceExe()
        {
            if (ServiceExeLocator != null) return ServiceExeLocator();

            string baseDir = AppContext.BaseDirectory;
            string corePath = Path.Combine(baseDir, "core", "BiosOptimizer.Service.exe");
            if (File.Exists(corePath)) return corePath;

            string directPath = Path.Combine(baseDir, "BiosOptimizer.Service.exe");
            if (File.Exists(directPath)) return directPath;

            return null;
        }

        public static async Task<ServiceBootStatus> EnsureRunningAsync(CancellationToken ct = default)
        {
            LastError = null;
            Log("=== ServiceBootstrapper.EnsureRunningAsync started ===");

            if (ScRunner == null && Process.GetProcessesByName("BiosOptimizer.Service").Length > 0)
            {
                Log("BiosOptimizer.Service process is already running.");
                return ServiceBootStatus.Running;
            }

            // Step 1: Run sc.exe query BiosOptimizer.Service
            var (queryExitCode, queryOut) = await RunScCommandAsync("query BiosOptimizer.Service", ct).ConfigureAwait(false);
            Log($"sc query exit code: {queryExitCode}, output: {queryOut.Trim()}");

            // If output contains RUNNING -> return Running
            if (queryOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                Log("Service is already RUNNING.");
                return ServiceBootStatus.Running;
            }

            // If output contains STOPPED -> sc.exe start -> return RegisteredAndStarted
            if (queryOut.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                Log("Service is STOPPED. Attempting sc.exe start BiosOptimizer.Service...");
                var (startCode, startOut) = await RunScCommandAsync("start BiosOptimizer.Service", ct).ConfigureAwait(false);
                Log($"sc start exit code: {startCode}, output: {startOut.Trim()}");

                if (startCode == 0 || startOut.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) || startOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Service started successfully.");
                    return ServiceBootStatus.RegisteredAndStarted;
                }

                // If non-elevated start gets access denied, attempt elevated start
                if (startOut.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || startOut.Contains("error 5", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Access denied starting service. Attempting elevated start...");
                    var (elevatedOk, elevationDenied) = await RunElevatedScAsync("sc start BiosOptimizer.Service", isRawCommand: true, ct: ct).ConfigureAwait(false);
                    if (elevationDenied)
                    {
                        Log("Elevation denied for service start.");
                        return ServiceBootStatus.ElevationDenied;
                    }

                    // Re-check
                    var (_, recheckOut) = await RunScCommandAsync("query BiosOptimizer.Service", ct).ConfigureAwait(false);
                    if (recheckOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || recheckOut.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Service started after elevation.");
                        return ServiceBootStatus.RegisteredAndStarted;
                    }
                }

                return ServiceBootStatus.RegisteredAndStarted;
            }

            // Step 2: Locate service EXE if not registered (1060 or not running)
            Log("Service not registered or query returned non-running. Locating service executable...");
            string? svcPath = LocateServiceExe();
            if (string.IsNullOrEmpty(svcPath) || !File.Exists(svcPath))
            {
                Log($"Service executable not found in install folder. Looked at: core/BiosOptimizer.Service.exe and BiosOptimizer.Service.exe (BaseDir={AppContext.BaseDirectory})");
                LastError = "ServiceExeMissing";
                return ServiceBootStatus.ServiceExeMissing;
            }
            Log($"Found service executable at: {svcPath}");

            // Step 3: Elevate and register via cmd.exe
            Log("Elevating and registering service via cmd.exe runas...");
            try
            {
                if (ElevateRunner != null)
                {
                    var (elevateOk, elevationDenied) = await ElevateRunner(svcPath, ct).ConfigureAwait(false);
                    if (elevationDenied)
                    {
                        Log("Elevation denied by user.");
                        return ServiceBootStatus.ElevationDenied;
                    }
                    if (!elevateOk)
                    {
                        Log("ElevateRunner returned false (RegistrationFailed).");
                        return ServiceBootStatus.RegistrationFailed;
                    }
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c sc create BiosOptimizer.Service binPath= \"\\\"{svcPath}\\\" --service\" start= auto && sc start BiosOptimizer.Service",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        bool exited = proc.WaitForExit(30000);
                        Log($"Elevated process completed. Exited: {exited}, ExitCode: {(exited ? proc.ExitCode.ToString() : "timeout")}");
                    }
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223 || ex.ErrorCode == -2147467259 || ex.Message.Contains("cancelled") || ex.Message.Contains("canceled"))
            {
                Log($"Elevation denied (Win32Exception): {ex.Message}");
                return ServiceBootStatus.ElevationDenied;
            }
            catch (Exception ex)
            {
                Log($"Registration failed with exception: {ex}");
                LastError = ex.Message;
                return ServiceBootStatus.RegistrationFailed;
            }

            // Step 4: Re-run sc.exe query -> if RUNNING, return Running. Otherwise return ScmError.
            Log("Re-querying service status after registration...");
            var (verifyCode, verifyOut) = await RunScCommandAsync("query BiosOptimizer.Service", ct).ConfigureAwait(false);
            Log($"sc query verify exit code: {verifyCode}, output: {verifyOut.Trim()}");

            if (verifyOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                Log("Service is RUNNING after registration.");
                return ServiceBootStatus.Running;
            }

            if (verifyOut.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
            {
                Log("Service is START_PENDING after registration.");
                return ServiceBootStatus.RegisteredAndStarted;
            }

            Log("Service failed to reach RUNNING state after registration (ScmError).");
            return ServiceBootStatus.ScmError;
        }

        private static async Task<(int ExitCode, string Output)> RunScCommandAsync(string arguments, CancellationToken ct = default)
        {
            if (ScRunner != null)
            {
                return await ScRunner(arguments, ct).ConfigureAwait(false);
            }

            try
            {
                var psi = new ProcessStartInfo("sc.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (-1, "Failed to start sc.exe");

                string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                return (proc.ExitCode, $"{stdout}\n{stderr}".Trim());
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        private static async Task<(bool Success, bool ElevationDenied)> RunElevatedScAsync(
            string rawCommand, 
            bool isRawCommand, 
            CancellationToken ct = default)
        {
            if (ElevateRunner != null)
            {
                return await ElevateRunner(rawCommand, ct).ConfigureAwait(false);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {rawCommand}",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                return (true, false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223 || ex.ErrorCode == -2147467259 || ex.Message.Contains("cancelled") || ex.Message.Contains("canceled"))
            {
                return (false, true);
            }
            catch (Exception)
            {
                return (false, false);
            }
        }
    }
}
