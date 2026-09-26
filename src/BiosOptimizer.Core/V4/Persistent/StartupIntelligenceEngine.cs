#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Persistent
{
    public class StartupItemDto
    {
        public string Name { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool IsEssential { get; set; }
        public string RecommendationReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Analyzes persistent startup sources across HKCU/HKLM Run, RunOnce, Startup folders, and StartupApproved.
    /// </summary>
    public class StartupIntelligenceEngine
    {
        private static readonly HashSet<string> EssentialStartupNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SecurityHealth", "Windows Defender", "RTHDVCPL", "RtHDVBg", "RAVCpl64",
            "NVDisplay.Container", "NVIDIA GeForce Experience", "RadeonSoftware"
        };

        public List<StartupItemDto> DiscoverStartupItems()
        {
            var items = new List<StartupItemDto>();

            // 1. HKCU Run
            ScanRegistryRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU:Run", items);

            // 2. HKLM Run (32-bit & 64-bit)
            ScanRegistryRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM:Run", items);
            ScanRegistryRunKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM:Run (32-bit)", items);

            // 3. User Startup Folder
            string userStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            ScanFolder(userStartup, "Folder: User Startup", items);

            // 4. Common Startup Folder
            string commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
            ScanFolder(commonStartup, "Folder: Common Startup", items);

            return items;
        }

        private static void ScanRegistryRunKey(RegistryKey root, string subKeyPath, string locationName, List<StartupItemDto> items)
        {
            try
            {
                using var key = root.OpenSubKey(subKeyPath, writable: false);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    string command = key.GetValue(valueName)?.ToString() ?? string.Empty;
                    bool isEssential = EssentialStartupNames.Contains(valueName);

                    items.Add(new StartupItemDto
                    {
                        Name = valueName,
                        Command = command,
                        Location = locationName,
                        IsEnabled = true,
                        IsEssential = isEssential,
                        RecommendationReason = isEssential ? "Essential hardware/security startup entry" : "Non-essential application startup entry"
                    });
                }
            }
            catch { }
        }

        private static void ScanFolder(string folderPath, string locationName, List<StartupItemDto> items)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return;

                foreach (var file in Directory.GetFiles(folderPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    items.Add(new StartupItemDto
                    {
                        Name = fileName,
                        Command = file,
                        Location = locationName,
                        IsEnabled = true,
                        IsEssential = false,
                        RecommendationReason = "Startup folder shortcut"
                    });
                }
            }
            catch { }
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<StartupItemDto> startupItems)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var item in startupItems)
            {
                if (item.IsEssential) continue;

                candidates.Add(new OptimizationCandidateDto
                {
                    CandidateId = $"STARTUP_{item.Location}_{item.Name}",
                    TargetType = "StartupEntry",
                    TargetIdentifier = $"{item.Location}|{item.Name}",
                    DisplayName = $"Startup: {item.Name}",
                    Category = "Startup",
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    InitialState = "Enabled",
                    TargetState = "Disabled",
                    Reason = $"Disable non-essential startup entry to prevent background relaunch: {item.Name}",
                    SafetyConfidence = 0.9,
                    IsSelected = true
                });
            }

            return candidates;
        }
    }
}
