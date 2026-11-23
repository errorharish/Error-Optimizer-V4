using System;
using System.Collections.Generic;

namespace BiosOptimizer.Core.Interfaces;

public enum ProcessCategory
{
    SystemCritical,
    OsRequired,
    Browser,
    ActiveApp,
    SafeToKill,
    Unknown
}

public class ProcessCandidate
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public ProcessCategory Category { get; set; }
    public string SafetyLevel { get; set; } = "SAFE TO TERMINATE"; // SAFE TO TERMINATE, CAUTION, PROTECTED, SYSTEM
    public bool HasVisibleWindow { get; set; }
    public bool IsForeground { get; set; }
    public double CpuUsagePercent { get; set; }
    public double RamUsageMb { get; set; }
    public bool IsSelected { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string DetectionTime { get; set; } = string.Empty;
    public bool IsDuplicate { get; set; }
    public int DuplicateCount { get; set; }
    public int ParentProcessId { get; set; }
    public string ParentProcessName { get; set; } = string.Empty;
    public bool HasAutoRestartPolicy { get; set; }
    public string AutoRestartTrigger { get; set; } = string.Empty;
}

public interface IProcessReductionEngine
{
    System.Threading.Tasks.Task<List<ProcessCandidate>> ScanProcessesAsync();
    int TerminateProcesses(IEnumerable<int> processIdsToKill);
    bool SuppressBackgroundDaemonsForWorkload(string workloadName, IEnumerable<string> daemonNames);
    int RestoreSuppressedDaemons();
}
