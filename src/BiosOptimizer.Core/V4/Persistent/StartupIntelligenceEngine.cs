#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using BiosOptimizer.Core.V4.Safety;
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
        public bool IsProtected { get; set; }
        public bool IsAlreadyOptimal { get; set; }
        public string Category { get; set; } = string.Empty;
        public string RecommendationReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Vector 3: Analyzes persistent startup sources across HKCU/HKLM Run, RunOnce, Startup folders, and StartupApproved.
    /// Strictly protects security health, audio panels, graphics display containers, and user-shielded apps.
    /// Skips already-disabled entries via StartupApproved binary evaluation.
    /// </summary>
    public class StartupIntelligenceEngine
    {
        private static readonly HashSet<string> EssentialStartupNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SecurityHealth", "Windows Defender", "RTHDVCPL", "RtHDVBg", "RAVCpl64",
            "NVDisplay.Container", "NVIDIA GeForce Experience", "RadeonSoftware"
        };

        public StartupItemDto EvaluateStartupEntry(string name, string command, string locationName, string? publisher = null, bool isAlreadyDisabled = false)
        {
            var protectedMatch = ProtectedApplicationPolicy.Instance.EvaluateStartupEntry(name, command, publisher, locationName);
            bool isHardwareEssential = EssentialStartupNames.Contains(name);
            bool isProtected = protectedMatch.IsProtected;
            bool isEssential = isHardwareEssential || isProtected;

            return new StartupItemDto
            {
                Name = name,
                Command = command,
                Location = locationName,
                Publisher = publisher ?? string.Empty,
                IsEnabled = !isAlreadyDisabled,
                IsEssential = isEssential,
                IsProtected = isProtected,
                IsAlreadyOptimal = isAlreadyDisabled,
                Category = isProtected ? "PROTECTED_STARTUP" : (isHardwareEssential ? "Essential" : "Startup"),
                RecommendationReason = isProtected
                    ? protectedMatch.Reason
                    : (isHardwareEssential ? "Essential hardware/security startup entry" : "Non-essential application startup entry")
            };
        }

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

        private void ScanRegistryRunKey(RegistryKey root, string subKeyPath, string locationName, List<StartupItemDto> items)
        {
            try
            {
                using var key = root.OpenSubKey(subKeyPath, writable: false);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    string command = key.GetValue(valueName)?.ToString() ?? string.Empty;
                    bool disabledInStartupApproved = IsStartupDisabledInStartupApproved(root, valueName);
                    items.Add(EvaluateStartupEntry(valueName, command, locationName, null, disabledInStartupApproved));
                }
            }
            catch { }
        }

        public static bool IsStartupDisabledInStartupApproved(RegistryKey root, string valueName)
        {
            try
            {
                using var appKey = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: false);
                if (appKey?.GetValue(valueName) is byte[] b && b.Length > 0)
                {
                    // Byte 0: 0x02 = Enabled, 0x03 = Disabled by Task Manager (odd byte = disabled)
                    return (b[0] & 1) == 1;
                }
            }
            catch { }

            try
            {
                using var appKey32 = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", writable: false);
                if (appKey32?.GetValue(valueName) is byte[] b32 && b32.Length > 0)
                {
                    return (b32[0] & 1) == 1;
                }
            }
            catch { }

            return false;
        }

        private void ScanFolder(string folderPath, string locationName, List<StartupItemDto> items)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return;

                foreach (var file in Directory.GetFiles(folderPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    bool isDisabledShortcut = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    items.Add(EvaluateStartupEntry(fileName, file, locationName, null, isDisabledShortcut));
                }
            }
            catch { }
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<StartupItemDto> startupItems)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var item in startupItems)
            {
                if (item.IsEssential || item.IsProtected || item.IsAlreadyOptimal ||
                    ProtectedApplicationPolicy.Instance.IsStartupEntryProtected(item.Name, item.Command, item.Publisher, item.Location))
                {
                    continue;
                }

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
                    IsSelected = true,
                    IsPersistent = true
                });
            }

            return candidates;
        }
    }
}
