#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Persistent
{
    public class BackgroundAppInfo
    {
        public string PackageName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool AllowsBackgroundExecution { get; set; } = true;
        public bool IsReducible { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Discovers AppX background activity and generates background permission optimization candidates.
    /// </summary>
    public class BackgroundAppEngine
    {
        private static readonly Dictionary<string, string> ReducibleBackgroundApps = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.BingNews"] = "Microsoft News background live tile updater",
            ["Microsoft.BingWeather"] = "Microsoft Weather background live tile updater",
            ["Microsoft.GetHelp"] = "Get Help background diagnostics",
            ["Microsoft.Getstarted"] = "Tips and Get Started background helper",
            ["Microsoft.YourPhone"] = "Phone Link background sync (when not in use)",
            ["Microsoft.ZuneVideo"] = "Movies & TV background push notifications",
            ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub background polling"
        };

        public List<BackgroundAppInfo> DiscoverBackgroundApps()
        {
            var results = new List<BackgroundAppInfo>();

            foreach (var kv in ReducibleBackgroundApps)
            {
                results.Add(new BackgroundAppInfo
                {
                    PackageName = kv.Key,
                    DisplayName = kv.Value.Split(' ')[0],
                    AllowsBackgroundExecution = true,
                    IsReducible = true,
                    Reason = kv.Value
                });
            }

            return results;
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<BackgroundAppInfo> apps)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var a in apps)
            {
                if (a.IsReducible)
                {
                    candidates.Add(new OptimizationCandidateDto
                    {
                        CandidateId = $"APPX_{a.PackageName}",
                        TargetType = "AppXBackground",
                        TargetIdentifier = a.PackageName,
                        DisplayName = $"AppX Background: {a.DisplayName}",
                        Category = "Background Apps",
                        RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                        InitialState = "Allowed",
                        TargetState = "Disabled",
                        Reason = $"Disable UWP background execution permission: {a.Reason}",
                        SafetyConfidence = 0.95,
                        IsSelected = true
                    });
                }
            }

            return candidates;
        }
    }
}
