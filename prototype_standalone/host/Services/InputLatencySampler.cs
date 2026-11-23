#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ErrorOptimizerPrototypeHost.Services;

public sealed class InputLatencyTestResult
{
    public int DurationSeconds { get; set; }
    public bool HasInput { get; set; }
    public bool Cancelled { get; set; }
    public int Samples { get; set; }
    public double? AvgIntervalMs { get; set; }
    public int? PollingHz { get; set; }
    public double? JitterMs { get; set; }
    public double? P99SpikeMs { get; set; }
    public double? TimerResolutionMs { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Interactive desktop-session Win32 <c>HWND_MESSAGE</c> Raw Input (<c>WM_INPUT</c>) polling &amp; jitter sampler
/// using high-resolution <c>QueryPerformanceCounter</c> timestamps and guaranteed cleanup in <c>finally</c>.
/// Runs strictly inside the interactive desktop host (never Session 0 Service) and never fabricates samples.
/// </summary>
public sealed class InputLatencySampler : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_QUIT = 0x0012;
    private const int WM_CLOSE = 0x0010;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly object _lock = new();
    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    public bool IsRunning
    {
        get { lock (_lock) return _activeCts != null && !_activeCts.IsCancellationRequested; }
    }

    public bool CancelCurrentTest()
    {
        lock (_lock)
        {
            if (_activeCts == null) return false;
            try { _activeCts.Cancel(); } catch { }
            return true;
        }
    }

    public async Task<InputLatencyTestResult> RunSamplingAsync(int durationSeconds, CancellationToken outerCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int clampedSeconds = durationSeconds switch
        {
            5 or 10 or 30 => durationSeconds,
            _ => Math.Clamp(durationSeconds, 1, 60)
        };

        CancellationTokenSource runCts;
        lock (_lock)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            runCts = _activeCts;
        }

        double? timerResMs = ReadTimerResolutionMs();

        try
        {
            var tcs = new TaskCompletionSource<InputLatencyTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    var res = CaptureRawMouseSamplesOnMessageThread(clampedSeconds, timerResMs, runCts.Token);
                    tcs.TrySetResult(res);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "EO-RawInputSampler"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_activeCts, runCts))
                {
                    _activeCts.Dispose();
                    _activeCts = null;
                }
            }
        }
    }

    private static InputLatencyTestResult CaptureRawMouseSamplesOnMessageThread(
        int durationSeconds,
        double? timerResMs,
        CancellationToken ct)
    {
        QueryPerformanceFrequency(out long qpcFreq);
        if (qpcFreq <= 0) qpcFreq = 10_000_000;

        var intervalsMs = new List<double>(4096);
        int rawEventCount = 0;
        long prevQpc = 0;

        string className = $"EO_RawInputMsgWin_{Guid.NewGuid():N}";
        IntPtr hwnd = IntPtr.Zero;
        bool registeredRaw = false;
        IntPtr hInstance = GetModuleHandle(null);
        WndProcDelegate? wndProcDelegate = null;

        uint nativeThreadId = GetCurrentThreadId();
        using var cancelReg = ct.Register(() =>
        {
            try { PostThreadMessage(nativeThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
        });

        try
        {
            wndProcDelegate = (hWnd, msg, wParam, lParam) =>
            {
                if (msg == WM_INPUT)
                {
                    rawEventCount++;
                    QueryPerformanceCounter(out long nowQpc);
                    if (prevQpc != 0)
                    {
                        double dtMs = (nowQpc - prevQpc) * 1000.0 / qpcFreq;
                        if (dtMs > 0.05 && dtMs < 1000.0)
                            intervalsMs.Add(dtMs);
                    }
                    prevQpc = nowQpc;
                }
                return DefWindowProc(hWnd, msg, wParam, lParam);
            };

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
                hInstance = hInstance,
                lpszClassName = className
            };

            if (RegisterClassEx(ref wc) == 0)
            {
                return new InputLatencyTestResult
                {
                    DurationSeconds = durationSeconds,
                    TimerResolutionMs = timerResMs,
                    Message = $"RegisterClassEx failed (Win32={Marshal.GetLastWin32Error()})."
                };
            }

            hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return new InputLatencyTestResult
                {
                    DurationSeconds = durationSeconds,
                    TimerResolutionMs = timerResMs,
                    Message = $"CreateWindowEx(HWND_MESSAGE) failed (Win32={Marshal.GetLastWin32Error()})."
                };
            }

            var rid = new RAWINPUTDEVICE[]
            {
                new()
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02, // Mouse
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = hwnd
                }
            };

            registeredRaw = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            if (!registeredRaw)
            {
                return new InputLatencyTestResult
                {
                    DurationSeconds = durationSeconds,
                    TimerResolutionMs = timerResMs,
                    Message = $"RegisterRawInputDevices failed (Win32={Marshal.GetLastWin32Error()})."
                };
            }

            // Timer posts stop sentinel via dedicated background timer
            using var durationTimer = new System.Threading.Timer(_ =>
            {
                try { PostThreadMessage(nativeThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            }, null, TimeSpan.FromSeconds(durationSeconds), Timeout.InfiniteTimeSpan);

            while (!ct.IsCancellationRequested && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (registeredRaw)
            {
                try
                {
                    var remove = new RAWINPUTDEVICE[]
                    {
                        new() { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero }
                    };
                    RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                }
                catch { }
            }

            if (hwnd != IntPtr.Zero)
            {
                try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                try { DestroyWindow(hwnd); } catch { }
            }

            try { UnregisterClass(className, hInstance); } catch { }
            GC.KeepAlive(wndProcDelegate);
        }

        if (ct.IsCancellationRequested)
        {
            return new InputLatencyTestResult
            {
                DurationSeconds = durationSeconds,
                Cancelled = true,
                Samples = rawEventCount,
                TimerResolutionMs = timerResMs,
                Message = "Input latency sampling cancelled."
            };
        }

        if (intervalsMs.Count < 2)
        {
            return new InputLatencyTestResult
            {
                DurationSeconds = durationSeconds,
                HasInput = false,
                Samples = rawEventCount,
                TimerResolutionMs = timerResMs,
                Message = "No mouse movement detected during the sampling window."
            };
        }

        double avg = intervalsMs.Average();
        double hzDouble = 1000.0 / Math.Max(0.1, avg);
        double variance = intervalsMs.Select(x => (x - avg) * (x - avg)).Average();
        double jitter = Math.Sqrt(variance);

        var sorted = intervalsMs.OrderBy(x => x).ToList();
        int p99Index = Math.Clamp((int)Math.Floor(sorted.Count * 0.99), 0, sorted.Count - 1);
        double p99Spike = sorted[p99Index];

        return new InputLatencyTestResult
        {
            DurationSeconds = durationSeconds,
            HasInput = true,
            Samples = rawEventCount,
            AvgIntervalMs = Math.Round(avg, 2),
            PollingHz = (int)Math.Round(hzDouble),
            JitterMs = Math.Round(jitter, 2),
            P99SpikeMs = Math.Round(p99Spike, 2),
            TimerResolutionMs = timerResMs,
            Message = $"Measured {rawEventCount} mouse HID reports (~{(int)Math.Round(hzDouble)} Hz, jitter {jitter:F2} ms)."
        };
    }

    private static double? ReadTimerResolutionMs()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out _, out uint current100Ns) == 0 && current100Ns > 0)
                return Math.Round(current100Ns / 10_000.0, 2);
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentTest();
        lock (_lock)
        {
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);
}
