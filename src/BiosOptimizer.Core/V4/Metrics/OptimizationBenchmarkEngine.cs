#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Metrics
{
    /// <summary>
    /// Measures actual physical system metrics (before and after) to guarantee that reported
    /// optimizations are real and verified, not merely planned estimates.
    /// </summary>
    public class OptimizationBenchmarkEngine
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        private static readonly object _cpuLock = new();

        /// <summary>
        /// Samples host CPU utilization over a consistent, self-contained multi-sample window
        /// (fresh baseline per call so before/after comparisons always span identical intervals).
        /// </summary>
        public static double SampleSystemCpuPercent(int samples = 3, int intervalMs = 50)
        {
            lock (_cpuLock)
            {
                try
                {
                    if (!GetSystemTimes(out long prevIdle, out long prevKernel, out long prevUser))
                        return 0.0;

                    double totalPct = 0.0;
                    int validSamples = 0;
                    int targetSamples = Math.Max(1, samples);
                    int sleepMs = Math.Clamp(intervalMs, 20, 250);

                    for (int s = 0; s < targetSamples; s++)
                    {
                        System.Threading.Thread.Sleep(sleepMs);
                        if (!GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
                            break;

                        long usrDelta = userTime - prevUser;
                        long kerDelta = kernelTime - prevKernel;
                        long idlDelta = idleTime - prevIdle;

                        prevIdle = idleTime;
                        prevKernel = kernelTime;
                        prevUser = userTime;

                        long sysTotal = usrDelta + kerDelta;
                        if (sysTotal > 0)
                        {
                            long busy = sysTotal - idlDelta;
                            double pct = Math.Clamp((double)busy / sysTotal * 100.0, 0.0, 100.0);
                            totalPct += pct;
                            validSamples++;
                        }
                    }

                    if (validSamples > 0)
                    {
                        return Math.Clamp(Math.Round(totalPct / validSamples, 1), 0.0, 100.0);
                    }
                }
                catch { }
                return 0.0;
            }
        }

        public OptimizationMetricsDto SampleCurrentMetrics(int cpuSamples = 3)
        {
            var dto = new OptimizationMetricsDto();

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
                dto.CurrentProcessCount = processes.Length;
            }
            catch
            {
                processes = Array.Empty<Process>();
                dto.CurrentProcessCount = 0;
            }

            // RAM Stats
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref mem))
            {
                dto.RamTotalMb = Math.Round(mem.ullTotalPhys / (1024.0 * 1024.0), 1);
                dto.RamAvailableMb = Math.Round(mem.ullAvailPhys / (1024.0 * 1024.0), 1);
                dto.RamUsedMb = Math.Round(dto.RamTotalMb - dto.RamAvailableMb, 1);
            }

            // Real physical multi-sample smoothed CPU utilization measurement
            dto.CpuUtilizationPercent = SampleSystemCpuPercent(cpuSamples, 35);

            // Dispose handles
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { }
            }

            return dto;
        }

        public (double totalMb, double availMb, double usedMb) GetMemoryMetrics()
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref mem))
            {
                double total = Math.Round(mem.ullTotalPhys / (1024.0 * 1024.0), 1);
                double avail = Math.Round(mem.ullAvailPhys / (1024.0 * 1024.0), 1);
                return (total, avail, total - avail);
            }
            return (0, 0, 0);
        }
    }
}
