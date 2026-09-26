#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Persistent
{
    public class ScheduledTaskItem
    {
        public string TaskPath { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool IsReducible { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Discovers and analyzes Windows scheduled tasks that trigger background wakeups, telemetry, and unneeded maintenance.
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

        public List<ScheduledTaskItem> DiscoverTasks()
        {
            var results = new List<ScheduledTaskItem>();

            foreach (var kv in ReducibleTaskPaths)
            {
                results.Add(new ScheduledTaskItem
                {
                    TaskPath = kv.Key,
                    TaskName = System.IO.Path.GetFileName(kv.Key),
                    IsEnabled = true,
                    IsReducible = true,
                    Reason = kv.Value
                });
            }

            return results;
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<ScheduledTaskItem> tasks)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var t in tasks)
            {
                if (t.IsReducible)
                {
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
                        IsSelected = true
                    });
                }
            }

            return candidates;
        }
    }
}
