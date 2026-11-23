#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace BiosOptimizer.Core.Implementations
{
    public sealed class InteractiveSessionState
    {
        public int SessionId { get; set; }
        public uint ForegroundProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public bool IsFullscreen { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        public bool IsFresh(int maxAgeSeconds = 10)
        {
            return (DateTime.UtcNow - TimestampUtc).TotalSeconds <= maxAgeSeconds;
        }
    }

    /// <summary>
    /// Thread-safe, multi-session interactive bridge that decouples Session 0 Windows background services
    /// from interactive user desktop window stations.
    /// In Windows, Session 0 services cannot call GetForegroundWindow() (returns IntPtr.Zero).
    /// This bridge accepts authenticated session updates from user-session agents (such as ERROR OPTIMIZER Host),
    /// maintains per-session active foreground process states, and exposes them to service-side AI Workload
    /// and RAM Limiter engines without cross-session security violations.
    /// </summary>
    public static class InteractiveSessionBridge
    {
        private static readonly ConcurrentDictionary<int, InteractiveSessionState> _sessions = new();
        private static int _lastReportedActiveSessionId = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WTSGetActiveConsoleSessionId();

        public static Func<uint>? LocalSessionForegroundPidProvider { get; set; }
        public static Func<bool>? LocalSessionFullscreenProvider { get; set; }

        public static int GetActiveConsoleSessionId()
        {
            try
            {
                uint sess = WTSGetActiveConsoleSessionId();
                if (sess != 0xFFFFFFFF)
                    return (int)sess;
            }
            catch { }

            return _lastReportedActiveSessionId >= 0 ? _lastReportedActiveSessionId : 1;
        }

        public static void UpdateSessionForeground(
            int sessionId,
            uint pid,
            string processName,
            string executablePath,
            string windowTitle,
            bool isFullscreen)
        {
            if (sessionId <= 0 && Process.GetCurrentProcess().SessionId > 0)
            {
                sessionId = Process.GetCurrentProcess().SessionId;
            }

            var state = new InteractiveSessionState
            {
                SessionId = sessionId,
                ForegroundProcessId = pid,
                ProcessName = processName ?? string.Empty,
                ExecutablePath = executablePath ?? string.Empty,
                WindowTitle = windowTitle ?? string.Empty,
                IsFullscreen = isFullscreen,
                TimestampUtc = DateTime.UtcNow
            };

            _sessions[sessionId] = state;
            _lastReportedActiveSessionId = sessionId;
        }

        public static void ClearSession(int sessionId)
        {
            _sessions.TryRemove(sessionId, out _);
            if (_lastReportedActiveSessionId == sessionId)
            {
                _lastReportedActiveSessionId = _sessions.Keys.FirstOrDefault();
            }
        }

        public static InteractiveSessionState? GetActiveSessionState()
        {
            int targetSession = GetActiveConsoleSessionId();
            if (_sessions.TryGetValue(targetSession, out var state) && state.IsFresh())
            {
                return state;
            }

            // Fallback to any fresh active session
            var fresh = _sessions.Values
                .Where(s => s.IsFresh() && s.SessionId > 0)
                .OrderByDescending(s => s.TimestampUtc)
                .FirstOrDefault();

            return fresh;
        }

        public static uint GetActiveForegroundPid()
        {
            // 1. If running in an interactive process with a local provider registered, use it
            if (LocalSessionForegroundPidProvider != null)
            {
                try
                {
                    uint localPid = LocalSessionForegroundPidProvider();
                    if (localPid > 0) return localPid;
                }
                catch { }
            }

            // 2. Query reported interactive session state from user-session agent
            var state = GetActiveSessionState();
            if (state != null && state.ForegroundProcessId > 0)
            {
                return state.ForegroundProcessId;
            }

            // 3. Fallback to direct Win32 GetForegroundWindow ONLY if caller is running in interactive session (> 0)
            int mySession = Process.GetCurrentProcess().SessionId;
            if (mySession > 0)
            {
                try
                {
                    IntPtr hwnd = NativeMethods.GetForegroundWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                        return pid;
                    }
                }
                catch { }
            }

            return 0;
        }

        public static bool IsActiveFullscreen()
        {
            if (LocalSessionFullscreenProvider != null)
            {
                try { return LocalSessionFullscreenProvider(); } catch { }
            }

            var state = GetActiveSessionState();
            if (state != null)
            {
                return state.IsFullscreen;
            }

            int mySession = Process.GetCurrentProcess().SessionId;
            if (mySession > 0)
            {
                try
                {
                    IntPtr hwnd = NativeMethods.GetForegroundWindow();
                    if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out RECT r))
                    {
                        int screenWidth = NativeMethods.GetSystemMetrics(0);
                        int screenHeight = NativeMethods.GetSystemMetrics(1);
                        return (r.Right - r.Left) >= screenWidth && (r.Bottom - r.Top) >= screenHeight;
                    }
                }
                catch { }
            }

            return false;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
