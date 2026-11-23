#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Persistent
{
    public class ScheduledTaskItem
    {
        public string TaskPath { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool IsProtected { get; set; }
        public bool IsReducible { get; set; }
        public bool IsAlreadyOptimal { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Vector 4: Discovers and analyzes Windows scheduled tasks that trigger background wakeups, telemetry, and unneeded maintenance.
    /// Uses real Task Scheduler COM state to detect enabled vs already-disabled tasks.
    /// Strictly protects security, backup, maintenance, and workload-required tasks.
    /// </summary>
    public class ScheduledTaskIntelligenceEngine
    {
        private static readonly Dictionary<string, string> ReducibleTaskPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"] = "Customer Experience Improvement Program telemetry collector",
            [@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"] = "Application telemetry updater",
            [@"\Microsoft\Windows\Autochk\Proxy"] = "SQM diagnostic proxy",
            [@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] = "Usage telemetry data consolidator",
            [@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"] = "USB hardware CEIP telemetry collector",
            [@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"] = "Disk diagnostic telemetry collector",
            [@"\Microsoft\Windows\Feedback\Siuf\DmClient"] = "Windows feedback reminder client",
            [@"\Microsoft\Windows\Maps\MapsUpdateTask"] = "Downloaded maps background updater"
        };

        public ScheduledTaskItem EvaluateTask(string taskPath, string? taskName = null, string? author = null, string? executeCommand = null, bool? isEnabled = null)
        {
            string resolvedName = string.IsNullOrWhiteSpace(taskName) ? Path.GetFileName(taskPath) : taskName;
            var protectedMatch = ProtectedApplicationPolicy.Instance.EvaluateScheduledTask(taskPath, resolvedName, author, executeCommand);
            if (protectedMatch.IsProtected)
            {
                return new ScheduledTaskItem
                {
                    TaskPath = taskPath,
                    TaskName = resolvedName,
                    IsEnabled = isEnabled ?? true,
                    IsProtected = true,
                    IsReducible = false,
                    IsAlreadyOptimal = false,
                    Category = "PROTECTED_TASK",
                    Reason = protectedMatch.Reason
                };
            }

            bool isReducible = ReducibleTaskPaths.TryGetValue(taskPath, out var reason);
            bool actualEnabled = isEnabled ?? true;

            return new ScheduledTaskItem
            {
                TaskPath = taskPath,
                TaskName = resolvedName,
                IsEnabled = actualEnabled,
                IsProtected = false,
                IsReducible = isReducible,
                IsAlreadyOptimal = !actualEnabled,
                Category = isReducible ? "Reducible" : "General",
                Reason = reason ?? "Standard scheduled task"
            };
        }

        public List<ScheduledTaskItem> DiscoverTasks()
        {
            var results = new List<ScheduledTaskItem>();

            dynamic? scheduler = null;
            try
            {
                var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType != null)
                {
                    scheduler = Activator.CreateInstance(schedulerType);
                    scheduler?.Connect();
                }
            }
            catch { }

            foreach (var kv in ReducibleTaskPaths)
            {
                bool? liveEnabled = null;
                if (scheduler != null)
                {
                    try
                    {
                        string folderPath = Path.GetDirectoryName(kv.Key)?.Replace('/', '\\') ?? "\\";
                        string tName = Path.GetFileName(kv.Key);
                        dynamic folder = scheduler.GetFolder(folderPath);
                        dynamic task = folder.GetTask(tName);
                        if (task != null)
                        {
                            liveEnabled = (bool)task.Enabled;
                        }
                    }
                    catch { }
                }

                var item = EvaluateTask(kv.Key, Path.GetFileName(kv.Key), null, null, liveEnabled);
                if (!item.IsProtected)
                {
                    item.Reason = kv.Value;
                    results.Add(item);
                }
            }

            return results;
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<ScheduledTaskItem> tasks)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var t in tasks)
            {
                if (t.IsProtected || t.IsAlreadyOptimal || !t.IsReducible ||
                    ProtectedApplicationPolicy.Instance.IsScheduledTaskProtected(t.TaskPath, t.TaskName))
                {
                    continue;
                }

                candidates.Add(new OptimizationCandidateDto
                {
                    CandidateId = $"TASK_{t.TaskName}",
                    TargetType = "ScheduledTask",
                    TargetIdentifier = t.TaskPath,
                    DisplayName = $"Scheduled Task: {t.TaskName}",
                    Category = "Scheduled Tasks",
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    InitialState = "Enabled",
                    TargetState = "Disabled",
                    Reason = $"Disable background telemetry scheduled task: {t.Reason}",
                    SafetyConfidence = 0.95,
                    IsSelected = true,
                    IsPersistent = true,
                    WorkloadCompatibility = "All",
                    Classification = "Recommended",
                    ExpectedBenefit = "Disables background scheduled telemetry or update checks.",
                    PotentialRisk = "Task will not run automatically on timer.",
                    RequiredPermissions = "Administrator / Background Service",
                    ReversibilityStatus = "Reversible via Rollback Journal",
                    BackendHandler = "ScheduledTaskIntelligenceEngine / TaskScheduler",
                    VerificationMethod = "Task State Query (Disabled)",
                    EvidenceQuality = "detected",
                    EligibilityExplanation = "Telemetry or non-essential updater scheduled task."
                });
            }

            return candidates;
        }
    }
}
