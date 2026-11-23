using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BiosOptimizer.GUI.Services
{
    public class RawInputMonitor
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_REMOVE = 0x00000001;

        private IntPtr _hwnd;
        private HwndSource? _source;
        private long _lastTime = 0;
        private Action<double>? _onInterval;
        private readonly object _lock = new();

        public void Start(IntPtr hwnd, Action<double> onInterval)
        {
            lock (_lock)
            {
                Stop();

                _hwnd = hwnd;
                _onInterval = onInterval;
                _lastTime = 0;

                var rid = new RAWINPUTDEVICE[1];
                rid[0].usUsagePage = 0x01; // generic desktop
                rid[0].usUsage = 0x02;     // mouse
                rid[0].dwFlags = RIDEV_INPUTSINK;
                rid[0].hwndTarget = hwnd;

                if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                {
                    throw new Exception("Failed to register raw input device.");
                }

                _source = HwndSource.FromHwnd(hwnd);
                _source?.AddHook(HwndHook);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_source != null)
                {
                    _source.RemoveHook(HwndHook);
                    _source = null;
                }

                if (_hwnd != IntPtr.Zero)
                {
                    var rid = new RAWINPUTDEVICE[1];
                    rid[0].usUsagePage = 0x01;
                    rid[0].usUsage = 0x02;
                    rid[0].dwFlags = RIDEV_REMOVE;
                    rid[0].hwndTarget = IntPtr.Zero;

                    try
                    {
                        RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                    }
                    catch { }
                    _hwnd = IntPtr.Zero;
                }
                _lastTime = 0;
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_INPUT = 0x00FF;
            if (msg == WM_INPUT)
            {
                long current = Stopwatch.GetTimestamp();
                if (_lastTime > 0)
                {
                    double intervalMs = (current - _lastTime) * 1000.0 / Stopwatch.Frequency;
                    // Filter out idle pauses (> 25ms) so user pauses/reversals do not artificially distort packet jitter
                    if (intervalMs >= 0.1 && intervalMs <= 25.0)
                    {
                        _onInterval?.Invoke(intervalMs);
                    }
                }
                _lastTime = current;
            }
            return IntPtr.Zero;
        }
    }
}
