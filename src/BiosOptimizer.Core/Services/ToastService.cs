#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace BiosOptimizer.Core.Services
{
    public enum ToastKind
    {
        Success,
        Warning,
        Error,
        Info
    }

    public interface IToastHost
    {
        void Show(string title, string? subtitle = null, ToastKind kind = ToastKind.Success, TimeSpan? duration = null);
        void Clear();
    }

    public static class ToastService
    {
        private static IToastHost? _host;
        public static IToastHost? Host
        {
            get => _host;
            set
            {
                _host = value;
                if (_host != null)
                {
                    FlushPending();
                }
            }
        }

        public static Action<string, string?, ToastKind, TimeSpan?>? OnToastShown { get; set; }

        private static readonly Queue<(string Title, string? Subtitle, ToastKind Kind, TimeSpan? Duration)> _pending = new();
        private static readonly object _queueLock = new();

        private static readonly object _backupLock = new();
        private static DateTimeOffset _lastBackupTime = DateTimeOffset.MinValue;
        private static int _recentBackupCount = 0;
        private static readonly List<string> _recentBackupTargets = new();
        private static string? _recentEmuKind;

        private static void LogToast(string title, bool hostSet, string result)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ErrorOptimizer");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                string logPath = Path.Combine(logDir, "toast.log");
                string hostStatus = hostSet ? "set" : "null";
                string line = $"[{DateTimeOffset.UtcNow:O}] Show(\"{title}\") — Host={hostStatus} — result={result}{Environment.NewLine}";
                File.AppendAllText(logPath, line);
            }
            catch { }
        }

        public static void Show(string title, string? subtitle = null,
            ToastKind kind = ToastKind.Success,
            TimeSpan? duration = null)
        {
            OnToastShown?.Invoke(title, subtitle, kind, duration);

            lock (_queueLock)
            {
                if (Host == null)
                {
                    LogToast(title, hostSet: false, result: "queued");
                    _pending.Enqueue((title, subtitle, kind, duration));
                    return;
                }
                LogToast(title, hostSet: true, result: "shown");
                Host.Show(title, subtitle, kind, duration);
            }
        }

        public static void FlushPending()
        {
            lock (_queueLock)
            {
                if (Host == null) return;
                while (_pending.Count > 0)
                {
                    var (t, s, k, d) = _pending.Dequeue();
                    LogToast(t, hostSet: true, result: "shown");
                    Host.Show(t, s, k, d);
                }
            }
        }

        public static void Clear()
        {
            lock (_backupLock)
            {
                _recentBackupCount = 0;
                _recentBackupTargets.Clear();
                _lastBackupTime = DateTimeOffset.MinValue;
                _recentEmuKind = null;
            }
            lock (_queueLock)
            {
                _pending.Clear();
            }
            Host?.Clear();
        }

        public static void NotifyBackupSuccess(string emuKindOrComponent, string fileName, long bytes)
        {
            lock (_backupLock)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastBackupTime).TotalMilliseconds <= 500 && _recentBackupCount > 0)
                {
                    _recentBackupCount++;
                    if (!_recentBackupTargets.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    {
                        _recentBackupTargets.Add(fileName);
                    }
                    string title = $"{_recentBackupCount} backups created";
                    string subtitle = $"{_recentEmuKind} — {string.Join(", ", _recentBackupTargets)}";
                    _lastBackupTime = now;
                    Show(title, subtitle, ToastKind.Success, TimeSpan.FromSeconds(5));
                }
                else
                {
                    _recentBackupCount = 1;
                    _recentBackupTargets.Clear();
                    _recentBackupTargets.Add(fileName);
                    _recentEmuKind = emuKindOrComponent;
                    _lastBackupTime = now;
                    string title = "Backup created";
                    string subtitle = $"{emuKindOrComponent} — {fileName} ({FormatSize(bytes)})";
                    Show(title, subtitle, ToastKind.Success, TimeSpan.FromSeconds(5));
                }
            }
        }

        public static void NotifyBackupFailure(string reason)
        {
            Show("Backup failed", reason, ToastKind.Error, TimeSpan.FromSeconds(8));
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
            return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F1} GB";
        }

        public static void ResetBackupDebounce()
        {
            lock (_backupLock)
            {
                _recentBackupCount = 0;
                _recentBackupTargets.Clear();
                _lastBackupTime = DateTimeOffset.MinValue;
                _recentEmuKind = null;
            }
        }
    }
}
