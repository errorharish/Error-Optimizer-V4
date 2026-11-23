using System;
using System.Runtime.InteropServices;

namespace BiosOptimizer.GUI.Services
{
    public class TimerResolutionManager
    {
        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtQueryTimerResolution(out uint MaximumTime, out uint MinimumTime, out uint CurrentTime);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSetTimerResolution(uint DesiredTime, bool SetResolution, out uint CurrentTime);

        private readonly object _lock = new();
        private bool _isHighResolutionActive;
        private uint _requestedTimeUnits = 5000; // 0.5ms (5000 * 100ns)
        private double _baselineResolutionMs = 15.625;

        public TimerResolutionManager()
        {
            CaptureBaseline();
        }

        public void CaptureBaseline()
        {
            lock (_lock)
            {
                NtQueryTimerResolution(out _, out _, out uint current);
                _baselineResolutionMs = current / 10000.0;
            }
        }

        public double BaselineResolutionMs => _baselineResolutionMs;
        public bool IsHighResolutionActive => _isHighResolutionActive;

        public double GetCurrentResolutionMs()
        {
            NtQueryTimerResolution(out _, out _, out uint current);
            return current / 10000.0;
        }

        public (double MaxMs, double MinMs, double CurrentMs) GetResolutionRange()
        {
            NtQueryTimerResolution(out uint max, out uint min, out uint current);
            return (max / 10000.0, min / 10000.0, current / 10000.0);
        }

        public void RequestHighResolution()
        {
            lock (_lock)
            {
                if (_isHighResolutionActive) return;

                NtQueryTimerResolution(out _, out uint minUnits, out _);
                uint targetUnits = Math.Max(minUnits, _requestedTimeUnits);

                int status = NtSetTimerResolution(targetUnits, true, out _);
                if (status == 0) // STATUS_SUCCESS
                {
                    _isHighResolutionActive = true;
                }
            }
        }

        public void ReleaseHighResolution()
        {
            lock (_lock)
            {
                if (!_isHighResolutionActive) return;

                NtQueryTimerResolution(out _, out uint minUnits, out _);
                NtSetTimerResolution(minUnits, false, out _);
                _isHighResolutionActive = false;
            }
        }
    }
}
