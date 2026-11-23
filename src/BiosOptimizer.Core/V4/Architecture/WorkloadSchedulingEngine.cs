#nullable enable
using System;
using System.Diagnostics;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;

namespace BiosOptimizer.Core.V4.Architecture
{
    /// <summary>
    /// Authoritative Workload Scheduling Engine for ERROR OPTIMIZER V4.
    /// Manages active game, emulator, and creative process priority balancing,
    /// ensuring background tasks are contained without starving worker threads.
    /// </summary>
    public sealed class WorkloadSchedulingEngine
    {
        private static readonly Lazy<WorkloadSchedulingEngine> _instance = new(() => new WorkloadSchedulingEngine());
        public static WorkloadSchedulingEngine Instance => _instance.Value;

        public bool ElevateForegroundWorkload(Process targetProcess, string workloadType, out string diagnostic)
        {
            try
            {
                if (targetProcess.HasExited)
                {
                    diagnostic = "Target workload process has already exited.";
                    return false;
                }

                // Never set RealTime priority under any circumstances (violates SafetyKernel R04)
                if (targetProcess.PriorityClass != ProcessPriorityClass.AboveNormal &&
                    targetProcess.PriorityClass != ProcessPriorityClass.High)
                {
                    targetProcess.PriorityClass = ProcessPriorityClass.AboveNormal;
                    diagnostic = $"Elevated process {targetProcess.ProcessName} (PID {targetProcess.Id}) to AboveNormal priority for {workloadType}.";
                    return true;
                }

                diagnostic = $"Process {targetProcess.ProcessName} (PID {targetProcess.Id}) is already operating at {targetProcess.PriorityClass}.";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = $"Failed to calibrate priority for {targetProcess.ProcessName}: {ex.Message}";
                return false;
            }
        }
    }
}
