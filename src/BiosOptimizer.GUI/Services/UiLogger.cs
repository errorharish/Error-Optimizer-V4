using System;
using System.IO;

namespace BiosOptimizer.GUI.Services
{
    public static class UiLogger
    {
        private static readonly object _lock = new();
        private static string _logPath;

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    try
                    {
                        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var dir = Path.Combine(localAppData, "ErrorOptimizer");
                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        _logPath = Path.Combine(dir, "ui.log");
                    }
                    catch
                    {
                        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui.log");
                    }
                }
                return _logPath;
            }
        }

        public static void Log(string message)
        {
            try
            {
                var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [UI] {message}";
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Non-fatal if logging fails
            }
        }

        public static void LogError(string context, Exception ex)
        {
            try
            {
                var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [UI_ERROR] {context}: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}";
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Non-fatal
            }
        }
    }
}
