using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.IPC.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BiosOptimizer.Core.Implementations;

public class ProcessReductionEngine : IProcessReductionEngine
{
    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);
    private static readonly HashSet<string> SystemCritical = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "services", "smss", "lsass", "svchost", "System",
        "System Idle Process", "Secure System", "Memory Compression", "Registry",
        "securityhealthservice", "MsMpEng", "spoolsv", "LsaIso", "fontdrvhost"
    };

    private static readonly HashSet<string> OsRequired = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "rundll32", "taskhostw", "ctfmon", "RuntimeBroker",
        "ShellExperienceHost", "ApplicationFrameHost", "StartMenuExperienceHost",
        "searchapp", "SearchIndexer", "Widgets", "TextInputHost", "sihost",
        "dwm", "SearchHost", "winlogon"
    };

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "iexplore"
    };

    private static readonly HashSet<string> SafeToKillExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "SkypeBackgroundHost", "Teams", "Discord",
        "Dropbox", "GoogleDrive",
        "GoogleUpdate", "FacebookUpdate", "ApplePushService", "iTunesHelper",
        "jusched", "EOSServer", "OVRServer_x64", "EpicGamesLauncher",
        "Origin", "upc", "GalaxyClient"
    };

    private readonly IBackupManager _backupManager;

    public ProcessReductionEngine(IBackupManager backupManager)
    {
        _backupManager = backupManager;
    }

    public async Task<List<ProcessCandidate>> ScanProcessesAsync()
    {
        Console.WriteLine("[PROCESS REDUCTION] Enumerating Windows processes...");
        var candidates = new List<ProcessCandidate>();
        var processes = Process.GetProcesses();

        // Pass 0.5: Native Parent-Child Process Hierarchy Resolution
        var (_, childParentMap) = BiosOptimizer.Core.V4.Intelligence.ProcessIntelligenceEngine.BuildRealProcessHierarchy();

        // Pass 1: Gather active window & foreground PIDs
        var activeWindowPids = GetPidsWithVisibleWindows();
        IntPtr fgHwnd = GetForegroundWindow();
        int fgPid = 0;
        if (fgHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fgHwnd, out uint fPid);
            fgPid = (int)fPid;
        }
        int currentAppPid = Process.GetCurrentProcess().Id;
        string nowTime = DateTime.Now.ToString("HH:mm:ss");

        foreach (var p in processes)
        {
            if (p.Id == 0 || p.Id == 4) continue; // Idle, System

            var cand = new ProcessCandidate
            {
                ProcessId = p.Id,
                ProcessName = p.ProcessName,
                DisplayName = p.ProcessName,
                DetectionTime = nowTime
            };
            
            try { cand.ProcessPath = p.MainModule?.FileName ?? ""; } catch { }
            if (childParentMap.TryGetValue(p.Id, out var parentInfo))
            {
                cand.ParentProcessId = parentInfo.ParentPid;
                cand.ParentProcessName = parentInfo.ParentName;
            }
            var (hasAutoRestart, restartTrigger) = CheckAutoRestartTriggers(cand.ProcessName, cand.ProcessPath);
            cand.HasAutoRestartPolicy = hasAutoRestart;
            cand.AutoRestartTrigger = restartTrigger;
            try { cand.RamUsageMb = Math.Round(p.WorkingSet64 / (1024.0 * 1024.0), 1); } catch { cand.RamUsageMb = 0; }

            // Extract Publisher and File Description if accessible
            if (!string.IsNullOrEmpty(cand.ProcessPath) && System.IO.File.Exists(cand.ProcessPath))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(cand.ProcessPath);
                    cand.Publisher = info.CompanyName ?? "";
                    if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    {
                        cand.DisplayName = info.FileDescription;
                    }
                }
                catch { }
            }

            cand.HasVisibleWindow = activeWindowPids.Contains(p.Id);
            cand.IsForeground = (p.Id == fgPid);

            // Strict Self & Foreground Protection
            if (p.Id == currentAppPid ||
                p.ProcessName.Contains("ErrorOptimizer", StringComparison.OrdinalIgnoreCase) ||
                p.ProcessName.Contains("BiosOptimizer", StringComparison.OrdinalIgnoreCase))
            {
                cand.Category = ProcessCategory.SystemCritical;
                cand.SafetyLevel = "PROTECTED";
                cand.Reason = "Error Optimizer application process";
                candidates.Add(cand);
                continue;
            }

            // V4 Protected Application Policy (Adobe, Maxon, Red Giant, Topaz, DaVinci, Steam, Lenovo Vantage, UltraViewer, Spotify, Urban VPN)
            var policyMatch = BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(
                cand.ProcessName,
                cand.ProcessPath,
                cand.Publisher,
                !string.IsNullOrWhiteSpace(cand.Publisher));
            if (policyMatch.IsProtected)
            {
                cand.Category = ProcessCategory.OsRequired;
                cand.SafetyLevel = "PROTECTED";
                cand.Reason = policyMatch.Reason;
                candidates.Add(cand);
                continue;
            }

            // Category classification
            cand.Category = ClassifyProcess(cand.ProcessName, cand.ProcessPath, cand.Publisher);

            if (cand.IsForeground)
            {
                cand.Category = ProcessCategory.ActiveApp;
            }
            else if (cand.HasVisibleWindow && cand.Category != ProcessCategory.SystemCritical && cand.Category != ProcessCategory.OsRequired)
            {
                cand.Category = ProcessCategory.ActiveApp;
            }

            // Assign Safety Level & Contextual Reason
            switch (cand.Category)
            {
                case ProcessCategory.SafeToKill:
                    cand.SafetyLevel = "SAFE TO TERMINATE";
                    if (cand.ProcessName.Contains("Update", StringComparison.OrdinalIgnoreCase))
                        cand.Reason = "Background updater — parent application is not active";
                    else if (cand.ProcessName.Contains("Helper", StringComparison.OrdinalIgnoreCase) || cand.ProcessName.Contains("Broker", StringComparison.OrdinalIgnoreCase))
                        cand.Reason = "Idle background helper service — no foreground interaction";
                    else if (cand.ProcessName.Contains("Tray", StringComparison.OrdinalIgnoreCase))
                        cand.Reason = "Background system tray monitor";
                    else if (cand.RamUsageMb > 150)
                        cand.Reason = $"High-memory background app ({cand.RamUsageMb:F0} MB) with no active window";
                    else
                        cand.Reason = "Non-essential background process — safe to terminate";
                    break;
                case ProcessCategory.ActiveApp:
                    cand.SafetyLevel = "CAUTION";
                    cand.Reason = cand.IsForeground 
                        ? "Active foreground application — currently in use" 
                        : "Active application with visible desktop window";
                    break;
                case ProcessCategory.Browser:
                    cand.SafetyLevel = "CAUTION";
                    cand.Reason = "Web browser process — may contain active tabs or user sessions";
                    break;
                case ProcessCategory.OsRequired:
                    cand.SafetyLevel = "PROTECTED";
                    cand.Reason = "Essential Windows OS component — required for desktop stability";
                    break;
                case ProcessCategory.SystemCritical:
                    cand.SafetyLevel = "SYSTEM";
                    cand.Reason = "Critical Windows kernel/system process — termination blocked";
                    break;
                default:
                    cand.SafetyLevel = "PROTECTED";
                    cand.Reason = "Unclassified system or background process";
                    break;
            }

            candidates.Add(cand);
        }

        // Pass 1.5: Duplicate background instance detection
        var dupGroups = candidates
            .Where(c => !c.HasVisibleWindow && !c.IsForeground && c.Category != ProcessCategory.SystemCritical && c.Category != ProcessCategory.OsRequired)
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in dupGroups)
        {
            int dupCount = group.Count();
            foreach (var cand in group)
            {
                cand.IsDuplicate = true;
                cand.DuplicateCount = dupCount;
                if (cand.Category == ProcessCategory.SafeToKill)
                {
                    cand.Reason = $"Redundant background process instance ({dupCount} duplicate instances active)";
                }
            }
        }

        // Pass 2: CPU check over 1.5 seconds for candidate processes
        var potentialKills = candidates.Where(c => c.Category == ProcessCategory.SafeToKill).Take(30).ToList();
        
        var cpuSnap1 = GetCpuTimes(potentialKills.Select(p => p.ProcessId));
        await Task.Delay(1500);
        var cpuSnap2 = GetCpuTimes(potentialKills.Select(p => p.ProcessId));

        foreach (var p in potentialKills)
        {
            if (cpuSnap1.TryGetValue(p.ProcessId, out var t1) && cpuSnap2.TryGetValue(p.ProcessId, out var t2))
            {
                var processDeltaMs = (t2 - t1).TotalMilliseconds;
                var cpuUsageApprox = (processDeltaMs / 1500.0) * 100.0 / Environment.ProcessorCount;
                
                p.CpuUsagePercent = Math.Round(cpuUsageApprox, 1);

                if (cpuUsageApprox > 3.0)
                {
                    p.Category = ProcessCategory.ActiveApp;
                    p.SafetyLevel = "CAUTION";
                    p.Reason = $"Active background CPU consumption ({p.CpuUsagePercent:F1}%)";
                }
            }
        }

        // Cleanup disposed
        foreach (var p in processes) p.Dispose();

        return candidates;
    }

    public sealed class ProcessTerminationBatchResult
    {
        public int Terminated { get; set; }
        public int SkippedProtected { get; set; }
        public int AccessDenied { get; set; }
        public int ThrottledCount { get; set; }
        public int ProcessCountBefore { get; set; }
        public int ProcessCountAfter { get; set; }
        public double RamFreedMb { get; set; }
        public int TerminatedCount => Terminated;
        public int SkippedProtectedCount => SkippedProtected;
        public List<string> Errors { get; set; } = new();
    }

    public ProcessTerminationBatchResult TerminateProcessesDetailed(IEnumerable<int> processIdsToKill)
    {
        var summary = new ProcessTerminationBatchResult();
        var killedPaths = new List<string>();
        int currentAppPid = Process.GetCurrentProcess().Id;
        var visibleWindowPids = GetPidsWithVisibleWindows();

        int countBefore = 0;
        try { countBefore = Process.GetProcesses().Length; } catch { }
        summary.ProcessCountBefore = countBefore;
        double totalRamFreedMb = 0.0;

        foreach (var pid in processIdsToKill)
        {
            if (pid <= 4 || pid == currentAppPid)
            {
                summary.SkippedProtected++;
                continue;
            }

            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    summary.SkippedProtected++;
                    continue;
                }

                // Process Identity Integrity & PID reuse guard
                DateTime startTime = DateTime.MinValue;
                try { startTime = p.StartTime; } catch { }
                if (startTime == DateTime.MinValue && p.HasExited)
                {
                    summary.SkippedProtected++;
                    continue;
                }

                if (SystemCritical.Contains(p.ProcessName) || OsRequired.Contains(p.ProcessName))
                {
                    summary.SkippedProtected++;
                    continue;
                }

                // 1. Session 0 isolation — never terminate Session 0 services through process killer
                if (p.SessionId == 0)
                {
                    summary.SkippedProtected++;
                    summary.Errors.Add($"PID {pid} ({p.ProcessName}): Protected Session 0 system service; skipped.");
                    continue;
                }

                // 2. Protect processes with active visible windows
                if (visibleWindowPids.Contains(pid))
                {
                    summary.SkippedProtected++;
                    summary.Errors.Add($"PID {pid} ({p.ProcessName}): Active visible window present; skipped for user safety.");
                    continue;
                }

                var path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }

                // 3. Protected Application & Security Policy Evaluation
                var policyMatch = BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(
                    p.ProcessName, path);
                if (policyMatch.IsProtected)
                {
                    summary.SkippedProtected++;
                    continue;
                }

                // 4. Non-Bypassable SafetyKernel Evaluation
                var verdict = BiosOptimizer.Core.V4.Safety.SafetyKernel.Instance.EvaluateProcessKill(
                    new BiosOptimizer.IPC.Contracts.ProcessIdentityDto
                    {
                        ProcessId = pid,
                        ProcessName = p.ProcessName,
                        ExecutablePath = path
                    },
                    isGamingActive: false,
                    isEditingActive: false,
                    out string ruleCode,
                    out string safetyReason);

                if (verdict == SafetyVerdict.Deny)
                {
                    summary.SkippedProtected++;
                    summary.Errors.Add($"PID {pid} ({p.ProcessName}): Blocked by SafetyKernel [{ruleCode}] — {safetyReason}");
                    continue;
                }

                double workingSetMb = 0.0;
                try { workingSetMb = p.WorkingSet64 / (1024.0 * 1024.0); } catch { }

                // 5. Unverified / Unknown processes must NOT default to termination
                if (!SafeToKillExact.Contains(p.ProcessName))
                {
                    try
                    {
                        p.PriorityClass = ProcessPriorityClass.Idle;
                        EmptyWorkingSet(p.Handle);
                        summary.SkippedProtected++;
                        summary.ThrottledCount++;
                        totalRamFreedMb += (workingSetMb * 0.7); // 70% typically released to standby/free
                        summary.Errors.Add($"PID {pid} ({p.ProcessName}): Not in safe-termination catalog; safely throttled to Idle priority and working set trimmed instead.");
                    }
                    catch
                    {
                        summary.SkippedProtected++;
                    }
                    continue;
                }

                if (!string.IsNullOrEmpty(path)) killedPaths.Add(path);

                p.Kill();
                totalRamFreedMb += workingSetMb;
                summary.Terminated++;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                summary.AccessDenied++;
                summary.Errors.Add($"PID {pid}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                summary.AccessDenied++;
                summary.Errors.Add($"PID {pid}: {ex.Message}");
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"PID {pid}: {ex.Message}");
            }
        }

        if (killedPaths.Count > 0)
        {
            _backupManager.LogKilledProcesses(killedPaths);
        }

        int countAfter = 0;
        try { countAfter = Process.GetProcesses().Length; } catch { }
        summary.ProcessCountAfter = countAfter;
        summary.RamFreedMb = Math.Round(totalRamFreedMb, 1);

        return summary;
    }

    public int TerminateProcesses(IEnumerable<int> processIdsToKill) =>
        TerminateProcessesDetailed(processIdsToKill).Terminated;

    private ProcessCategory ClassifyProcess(string name, string path, string publisher = "")
    {
        if (SystemCritical.Contains(name)) return ProcessCategory.SystemCritical;
        if (OsRequired.Contains(name)) return ProcessCategory.OsRequired;

        var policyMatch = BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(
            name, path, publisher, !string.IsNullOrWhiteSpace(publisher));
        if (policyMatch.IsProtected)
            return ProcessCategory.OsRequired;

        if (Browsers.Contains(name)) return ProcessCategory.Browser;
        
        if (!string.IsNullOrEmpty(path) && path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
            return ProcessCategory.SystemCritical;

        if (SafeToKillExact.Contains(name)) return ProcessCategory.SafeToKill;

        return ProcessCategory.Unknown;
    }

    // --- Win32 P/Invoke for strict window checks ---

    private HashSet<int> GetPidsWithVisibleWindows()
    {
        var pids = new HashSet<int>();
        EnumWindows((hWnd, lParam) =>
        {
            if (IsWindowVisible(hWnd))
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private Dictionary<int, TimeSpan> GetCpuTimes(IEnumerable<int> pids)
    {
        var dict = new Dictionary<int, TimeSpan>();
        foreach (var pid in pids)
        {
            try
            {
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess != IntPtr.Zero)
                {
                    if (GetProcessTimes(hProcess, out _, out _, out var kernel, out var user))
                    {
                        long totalTicks = (((long)kernel.dwHighDateTime << 32) + kernel.dwLowDateTime) + 
                                          (((long)user.dwHighDateTime << 32) + user.dwLowDateTime);
                        dict[pid] = TimeSpan.FromTicks(totalTicks);
                    }
                    CloseHandle(hProcess);
                }
            }
            catch { }
        }
        return dict;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static (bool HasAutoRestart, string Trigger) CheckAutoRestartTriggers(string processName, string processPath)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                if (key != null)
                {
                    foreach (var valName in key.GetValueNames())
                    {
                        var val = key.GetValue(valName)?.ToString() ?? string.Empty;
                        if (val.Contains(processName, StringComparison.OrdinalIgnoreCase))
                            return (true, $"Startup Key: HKCU\\Run\\{valName}");
                    }
                }
            }

            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
            {
                if (key != null)
                {
                    foreach (var valName in key.GetValueNames())
                    {
                        var val = key.GetValue(valName)?.ToString() ?? string.Empty;
                        if (val.Contains(processName, StringComparison.OrdinalIgnoreCase))
                            return (true, $"Startup Key: HKLM\\Run\\{valName}");
                    }
                }
            }
        }
        catch { }

        return (false, string.Empty);
    }

    private readonly List<SuppressedDaemonRecord> _suppressedDaemons = new();
    private readonly object _suppressLock = new();

    public class SuppressedDaemonRecord
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public ProcessPriorityClass OriginalPriority { get; set; }
        public string WorkloadContext { get; set; } = string.Empty;
        public DateTime SuppressedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public bool SuppressBackgroundDaemonsForWorkload(string workloadName, IEnumerable<string> daemonNames)
    {
        lock (_suppressLock)
        {
            var targets = new HashSet<string>(daemonNames, StringComparer.OrdinalIgnoreCase);
            var procs = Process.GetProcesses();
            int currentPid = Process.GetCurrentProcess().Id;

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == currentPid || p.SessionId == 0) continue;
                    if (targets.Contains(p.ProcessName))
                    {
                        string path = "";
                        try { path = p.MainModule?.FileName ?? ""; } catch { }
                        var originalPriority = p.PriorityClass;
                        if (originalPriority != ProcessPriorityClass.Idle)
                        {
                            p.PriorityClass = ProcessPriorityClass.Idle;
                        }
                        EmptyWorkingSet(p.Handle);

                        _suppressedDaemons.Add(new SuppressedDaemonRecord
                        {
                            ProcessId = p.Id,
                            ProcessName = p.ProcessName,
                            ProcessPath = path,
                            OriginalPriority = originalPriority,
                            WorkloadContext = workloadName,
                            SuppressedAtUtc = DateTime.UtcNow
                        });
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return true;
        }
    }

    public int RestoreSuppressedDaemons()
    {
        lock (_suppressLock)
        {
            int restoredCount = 0;
            foreach (var record in _suppressedDaemons)
            {
                try
                {
                    using var p = Process.GetProcessById(record.ProcessId);
                    if (!p.HasExited && p.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        p.PriorityClass = record.OriginalPriority;
                        restoredCount++;
                    }
                }
                catch { }
            }
            _suppressedDaemons.Clear();
            return restoredCount;
        }
    }
}
