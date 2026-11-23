#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace BiosOptimizer.GUI.Services
{
    public static class StartupLogger
    {
        private static readonly object _lock = new();
        private static string? _logPath;
        private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB rotation limit

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    try
                    {
                        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var dir = Path.Combine(localAppData, "ErrorOptimizer", "logs");
                        Directory.CreateDirectory(dir);
                        _logPath = Path.Combine(dir, "startup_diagnostic.log");
                    }
                    catch
                    {
                        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_diagnostic.log");
                    }
                }
                return _logPath;
            }
        }

        private static void RotateLogFileIfNeeded()
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Length > MaxLogSizeBytes)
                    {
                        string oldLog = LogPath + ".old";
                        if (File.Exists(oldLog))
                        {
                            File.Delete(oldLog);
                        }
                        File.Move(LogPath, oldLog);
                    }
                }
            }
            catch
            {
                // Rotation failure should never crash the app
            }
        }

        public static void LogEnvironment(string? webView2Version = null)
        {
            try
            {
                var appVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
                var osVer = Environment.OSVersion.ToString();
                var is64Os = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                var is64Proc = Environment.Is64BitProcess ? "x64" : "x86";
                var clrVer = Environment.Version.ToString();
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var wv2Ver = string.IsNullOrWhiteSpace(webView2Version) ? "Unknown / Not Detected" : webView2Version;

                Log("SYS_ENV", "================================================================================");
                Log("SYS_ENV", "ERROR OPTIMIZER STUDIO -- RUNTIME ENVIRONMENT INITIALIZATION");
                Log("SYS_ENV", $"App Version     : {appVer}");
                Log("SYS_ENV", $"OS Version      : {osVer} (OS Arch: {is64Os}, Process Arch: {is64Proc})");
                Log("SYS_ENV", $"CLR Runtime     : .NET {clrVer}");
                Log("SYS_ENV", $"Base Directory  : {baseDir}");
                Log("SYS_ENV", $"WebView2 Runtime: {wv2Ver}");
                Log("SYS_ENV", "================================================================================");
            }
            catch (Exception ex)
            {
                LogError("SYS_ENV", "Failed to log system environment.", ex);
            }
        }

        public static void Log(string stage, string message)
        {
            try
            {
                int pid = Environment.ProcessId;
                int threadId = Thread.CurrentThread.ManagedThreadId;
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string line = $"[{timestamp}] [PID:{pid}] [TID:{threadId}] [{stage}] {message}";

                Debug.WriteLine(line);
                Console.WriteLine(line);

                lock (_lock)
                {
                    RotateLogFileIfNeeded();
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Never crash because of logging
            }
        }

        public static void LogError(string stage, string message, Exception? ex = null)
        {
            try
            {
                int pid = Environment.ProcessId;
                int threadId = Thread.CurrentThread.ManagedThreadId;
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string exDetails = ex != null ? $"\n  Exception: {ex.GetType().FullName}: {ex.Message}\n  StackTrace: {ex.StackTrace}" : "";
                string line = $"[{timestamp}] [PID:{pid}] [TID:{threadId}] [ERROR:{stage}] {message}{exDetails}";

                Debug.WriteLine(line);
                Console.WriteLine(line);

                lock (_lock)
                {
                    RotateLogFileIfNeeded();
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Never crash because of logging
            }
        }
    }
}
