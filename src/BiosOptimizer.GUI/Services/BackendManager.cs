using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace BiosOptimizer.GUI.Services
{
    /// <summary>
    /// Manages the lifecycle of the backend service (BiosOptimizer.Service.exe).
    /// Supports both Windows Service (SCM) mode and direct child-process mode.
    /// Logs all lifecycle transitions to %LOCALAPPDATA%\ErrorOptimizer\ui.log.
    /// </summary>
    public sealed class BackendManager : IDisposable
    {
        // Location is determined at RUNTIME based on where ErrorOptimizer.exe lives.
        // Production layout:
        //   dist\win-x64\ERROR OPTIMIZER.exe
        //   dist\win-x64\core\BiosOptimizer.Service.exe
        private readonly string _backendExePath;
        private readonly string _backendWorkDir;

        private Process _process;
        private volatile bool _disposed;
        private volatile bool _intentionalShutdown;

        public string SessionId { get; private set; }
        public int BackendPid { get; private set; }

        public BackendManager()
        {
            _backendExePath = ResolveBackendServicePath(out _backendWorkDir);
            UiLogger.Log($"BackendManager initialized. Backend binary path: '{_backendExePath}' (WorkDir: '{_backendWorkDir}')");
        }

        private static string ResolveBackendServicePath(out string workDir)
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

            // 1. AppContext base directory
            var corePath = Path.Combine(baseDir, "core", "BiosOptimizer.Service.exe");
            if (File.Exists(corePath))
            {
                workDir = Path.GetDirectoryName(corePath)!;
                return corePath;
            }

            var siblingPath = Path.Combine(baseDir, "BiosOptimizer.Service.exe");
            if (File.Exists(siblingPath))
            {
                workDir = baseDir;
                return siblingPath;
            }

            // 2. Process MainModule directory
            try
            {
                var mainMod = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainMod))
                {
                    var modDir = Path.GetDirectoryName(mainMod);
                    if (!string.IsNullOrEmpty(modDir))
                    {
                        var modCore = Path.Combine(modDir, "core", "BiosOptimizer.Service.exe");
                        if (File.Exists(modCore))
                        {
                            workDir = Path.GetDirectoryName(modCore)!;
                            return modCore;
                        }
                        var modSibling = Path.Combine(modDir, "BiosOptimizer.Service.exe");
                        if (File.Exists(modSibling))
                        {
                            workDir = modDir;
                            return modSibling;
                        }
                    }
                }
            }
            catch { }

            // 3. Program Files installed location
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installedCore = Path.Combine(progFiles, "Error Optimizer V3", "core", "BiosOptimizer.Service.exe");
            if (File.Exists(installedCore))
            {
                workDir = Path.GetDirectoryName(installedCore)!;
                return installedCore;
            }

            // 4. Inno Setup Uninstall Registry Location
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Error Optimizer V3_is1")
                             ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Error Optimizer V3_is1");
                if (key != null)
                {
                    var loc = key.GetValue("InstallLocation")?.ToString();
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        var regCore = Path.Combine(loc, "core", "BiosOptimizer.Service.exe");
                        if (File.Exists(regCore))
                        {
                            workDir = Path.GetDirectoryName(regCore)!;
                            return regCore;
                        }
                    }
                }
            }
            catch { }

            workDir = baseDir;
            return corePath;
        }

        /// <summary>
        /// Attempts SCM service start or launches a fresh hidden backend child process.
        /// </summary>
        public void StartBackend()
        {
            if (_disposed) return;

            UiLogger.Log("StartBackend() invoked.");

            if (!File.Exists(_backendExePath))
            {
                var errorMsg = $"Backend service binary not found at '{_backendExePath}'";
                UiLogger.Log($"[FATAL] {errorMsg}");
                BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogCrash(
                    new FileNotFoundException(errorMsg), 
                    "BackendManager.StartBackend", 
                    false);

                if (!App.IsStartupBackground)
                {
                    try
                    {
                        MessageBox.Show(
                            $"Backend service binary not found:\n\n{_backendExePath}\n\n" +
                            $"Please reinstall Error Optimizer.",
                            "Error Optimizer – Warning",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { }
                }
                return;
            }

            // Clean up stale instances
            KillStaleInstances();

            SessionId = Guid.NewGuid().ToString("N");
            BackendPid = 0;

            // Attempt to ensure Windows Service registration/startup in SCM if applicable
            TryStartScmService();

            try
            {
                UiLogger.Log($"Launching backend child process: '{_backendExePath}' --session {SessionId}");
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName         = _backendExePath,
                        Arguments        = $"--session {SessionId}",
                        WorkingDirectory = _backendWorkDir,
                        CreateNoWindow   = true,
                        UseShellExecute  = false,
                        WindowStyle      = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    },
                    EnableRaisingEvents = true
                };

                _process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        UiLogger.Log($"[BACKEND_STDOUT] {args.Data}");
                    }
                };
                _process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        UiLogger.Log($"[BACKEND_STDERR] {args.Data}");
                    }
                };
                _process.Exited += OnProcessExited;

                _process.Start();
                BackendPid = _process.Id;
                UiLogger.Log($"Backend child process started successfully (PID: {BackendPid}).");
                
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                UiLogger.LogError("Failed to launch backend child process", ex);
                BiosOptimizer.Core.Implementations.Diagnostics.ApplicationCrashLogger.Instance.LogCrash(ex, "BackendManager.StartBackend", false);
                if (!App.IsStartupBackground)
                {
                    try
                    {
                        MessageBox.Show(
                            $"Failed to start backend service:\n\n{ex.Message}",
                            "Error Optimizer", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    catch { }
                }
            }
        }

        private void TryStartScmService()
        {
            try
            {
                // Check if BiosOptimizerService exists in SCM
                using var queryProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query BiosOptimizerService",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                });
                queryProc?.WaitForExit(1000);
                var output = queryProc?.StandardOutput.ReadToEnd() ?? "";

                if (output.Contains("STATE"))
                {
                    UiLogger.Log("BiosOptimizerService found in SCM. Attempting to start if not running...");
                    using var startProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = "start BiosOptimizerService",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    startProc?.WaitForExit(2000);
                }
                else
                {
                    UiLogger.Log("BiosOptimizerService not registered in SCM; child-process mode will handle execution.");
                }
            }
            catch (Exception ex)
            {
                UiLogger.Log($"SCM query/start skipped or failed ({ex.Message}); proceeding with child process mode.");
            }
        }

        private void KillStaleInstances()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("BiosOptimizer.Service"))
                {
                    try
                    {
                        UiLogger.Log($"Terminating stale BiosOptimizer.Service process (PID {proc.Id})");
                        proc.Kill(entireProcessTree: true); 
                        proc.WaitForExit(3000); 
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                System.Threading.Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                UiLogger.Log($"KillStaleInstances warning: {ex.Message}");
            }
        }

        private async void OnProcessExited(object sender, EventArgs e)
        {
            if (_intentionalShutdown || _disposed) return;

            UiLogger.Log($"Backend child process exited unexpectedly. Restarting in 2s...");

            await Task.Delay(2000);

            if (_intentionalShutdown || _disposed) return;

            DisposeProcess();
            StartBackend();
        }

        public void Shutdown()
        {
            _intentionalShutdown = true;
            UiLogger.Log("BackendManager.Shutdown() called.");
            DisposeProcess();
        }

        private void DisposeProcess()
        {
            var proc = _process;
            _process = null;
            if (proc == null) return;

            proc.Exited -= OnProcessExited;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Shutdown();
        }
    }
}
