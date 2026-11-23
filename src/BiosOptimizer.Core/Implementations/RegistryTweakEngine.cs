using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Interop;
using BiosOptimizer.Core.Services;
using BiosOptimizer.Core.V4.Architecture;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;

namespace BiosOptimizer.Core.Implementations
{
    public class RegistryTweakOperationResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = "succeeded"; // "succeeded", "failed", "skipped", "unverified"
        public string State { get; set; } = string.Empty; // "APPLIED", "ALREADY_OPTIMAL", "SKIPPED", "BLOCKED", "FAILED"
        public string? Reason { get; set; }
        public string? Verification { get; set; }
        public bool Applied { get; set; }
        public bool AlreadyDesired { get; set; }
        public bool ReadbackVerified { get; set; }
        public bool Failed { get; set; }
        public bool Skipped { get; set; }
        public bool Unverified { get; set; }
        public bool RollbackAvailable { get; set; }
        public string? FailureReason { get; set; }
        public string CurrentValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public string Hive { get; set; } = "HKLM";
        public string KeyPath { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;
        public string OriginalValue { get; set; } = string.Empty;
        public string RequestedValue { get; set; } = string.Empty;
        public bool WriteAttempted { get; set; }
        public bool WriteSucceeded { get; set; }
        public string ReadbackValue { get; set; } = string.Empty;
        public string VerificationResult { get; set; } = "UNVERIFIED";
        public bool AlreadyOptimized => AlreadyDesired;
        public string? ErrorReason => FailureReason;
        public string Detail { get; set; } = string.Empty;
    }

    public class RegistryTweakExecutionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int RequestedCount { get; set; }
        public int AttemptedCount { get; set; }
        public int AppliedCount { get; set; }
        public int AlreadyDesiredCount { get; set; }
        public int VerifiedCount { get; set; }
        public int RestartRequiredCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public string? TransactionId { get; set; }
        public RollbackInfoDto? Rollback => !string.IsNullOrEmpty(TransactionId) ? new RollbackInfoDto { Available = true, RestorePoint = TransactionId, TransactionId = TransactionId } : null;
        public List<RegistryTweakOperationResult> Operations { get; set; } = new();
        public List<RegistryTweakOperationResult> Results => Operations;
    }

    public class RegistryTransactionEntry
    {
        public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
        public string OptimizationId { get; set; } = string.Empty;
        public string Hive { get; set; } = "HKLM";
        public string Path { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;
        public string ValueType { get; set; } = "REG_DWORD";
        public string BeforeValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string Applicability { get; set; } = "APPLICABLE";
        public string ApplyState { get; set; } = "APPLIED";
        public bool RequiresReboot { get; set; } = false;
        public string VerificationState { get; set; } = "UNVERIFIED";
        public string RegistryView { get; set; } = "Registry64";
        public string ErrorCode { get; set; } = "0x00000000";
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public DateTime? RebootVerifiedTimestamp { get; set; }
    }

    public class RegistryDiagnosticEntry
    {
        public string OptimizationId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Hive { get; set; } = "HKLM";
        public string SubKey { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;
        public string RequiredType { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public string WindowsBuild { get; set; } = string.Empty;
        public string RegistryView { get; set; } = string.Empty;
        public string PermissionState { get; set; } = string.Empty;
        public string ApplicabilityResult { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string VerificationState { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = "0x00000000";
        public string DiagnosticDetail { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class RegistryTransactionJournal
    {
        private static readonly object _sync = new object();
        private readonly string _journalPath;
        private readonly string _diagnosticsPath;
        private const string RegistryKeyPath = @"Software\ErrorOptimizer\RegistryOptimizer\Transactions";

        public RegistryTransactionJournal()
        {
            string dir = BiosOptimizer.Core.Common.AppEnvironment.ResolveWritableDataDirectory("RegistryOptimizer");
            _journalPath = Path.Combine(dir, "registry_transaction_journal.json");
            _diagnosticsPath = Path.Combine(dir, "registry_diagnostics.json");
        }

        public List<RegistryTransactionEntry> LoadEntries()
        {
            lock (_sync)
            {
                var list = new Dictionary<string, RegistryTransactionEntry>(StringComparer.OrdinalIgnoreCase);

                // 1. Load from file
                try
                {
                    if (File.Exists(_journalPath))
                    {
                        var json = File.ReadAllText(_journalPath);
                        var fromFile = JsonSerializer.Deserialize<List<RegistryTransactionEntry>>(json);
                        if (fromFile != null)
                        {
                            foreach (var item in fromFile)
                            {
                                if (!string.IsNullOrEmpty(item.OptimizationId))
                                    list[item.OptimizationId] = item;
                            }
                        }
                    }
                }
                catch { }

                // 2. Load from registry fallback mirror
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
                    if (key != null)
                    {
                        foreach (var subName in key.GetSubKeyNames())
                        {
                            using var sub = key.OpenSubKey(subName, false);
                            if (sub != null)
                            {
                                string optId = sub.GetValue("OptimizationId")?.ToString() ?? subName;
                                if (!list.ContainsKey(optId))
                                {
                                    list[optId] = new RegistryTransactionEntry
                                    {
                                        TransactionId = sub.GetValue("TransactionId")?.ToString() ?? Guid.NewGuid().ToString("N"),
                                        OptimizationId = optId,
                                        Hive = sub.GetValue("Hive")?.ToString() ?? "HKLM",
                                        Path = sub.GetValue("Path")?.ToString() ?? "",
                                        ValueName = sub.GetValue("ValueName")?.ToString() ?? "",
                                        ValueType = sub.GetValue("ValueType")?.ToString() ?? "REG_DWORD",
                                        BeforeValue = sub.GetValue("BeforeValue")?.ToString() ?? "",
                                        TargetValue = sub.GetValue("TargetValue")?.ToString() ?? "",
                                        CurrentValue = sub.GetValue("CurrentValue")?.ToString() ?? "",
                                        Applicability = sub.GetValue("Applicability")?.ToString() ?? "APPLICABLE",
                                        ApplyState = sub.GetValue("ApplyState")?.ToString() ?? "APPLIED",
                                        RequiresReboot = Convert.ToBoolean(sub.GetValue("RequiresReboot") ?? false),
                                        VerificationState = sub.GetValue("VerificationState")?.ToString() ?? "UNVERIFIED",
                                        RegistryView = sub.GetValue("RegistryView")?.ToString() ?? "Registry64",
                                        ErrorCode = sub.GetValue("ErrorCode")?.ToString() ?? "0x00000000",
                                        ErrorMessage = sub.GetValue("ErrorMessage")?.ToString() ?? "",
                                        Timestamp = DateTime.TryParse(sub.GetValue("Timestamp")?.ToString(), out var dt) ? dt : DateTime.UtcNow
                                    };
                                }
                            }
                        }
                    }
                }
                catch { }

                return list.Values.ToList();
            }
        }

        public void SaveEntry(RegistryTransactionEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.OptimizationId)) return;

            lock (_sync)
            {
                var all = LoadEntries();
                int idx = all.FindIndex(x => string.Equals(x.OptimizationId, entry.OptimizationId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) all[idx] = entry;
                else all.Add(entry);

                // 1. Write to JSON
                try
                {
                    string json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_journalPath, json);
                }
                catch { }

                // 2. Mirror to HKCU Registry
                try
                {
                    using var root = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, true);
                    if (root != null)
                    {
                        using var sub = root.CreateSubKey(entry.OptimizationId, true);
                        if (sub != null)
                        {
                            sub.SetValue("TransactionId", entry.TransactionId);
                            sub.SetValue("OptimizationId", entry.OptimizationId);
                            sub.SetValue("Hive", entry.Hive);
                            sub.SetValue("Path", entry.Path);
                            sub.SetValue("ValueName", entry.ValueName);
                            sub.SetValue("ValueType", entry.ValueType);
                            sub.SetValue("BeforeValue", entry.BeforeValue ?? "");
                            sub.SetValue("TargetValue", entry.TargetValue ?? "");
                            sub.SetValue("CurrentValue", entry.CurrentValue ?? "");
                            sub.SetValue("Applicability", entry.Applicability);
                            sub.SetValue("ApplyState", entry.ApplyState);
                            sub.SetValue("RequiresReboot", entry.RequiresReboot ? 1 : 0, RegistryValueKind.DWord);
                            sub.SetValue("VerificationState", entry.VerificationState);
                            sub.SetValue("RegistryView", entry.RegistryView ?? "Registry64");
                            sub.SetValue("ErrorCode", entry.ErrorCode ?? "0x00000000");
                            sub.SetValue("ErrorMessage", entry.ErrorMessage ?? "");
                            sub.SetValue("Timestamp", entry.Timestamp.ToString("O"));
                            if (entry.RebootVerifiedTimestamp.HasValue)
                                sub.SetValue("RebootVerifiedTimestamp", entry.RebootVerifiedTimestamp.Value.ToString("O"));
                        }
                    }
                }
                catch { }
            }
        }

        public void SaveDiagnostics(List<RegistryDiagnosticEntry> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0) return;
            lock (_sync)
            {
                try
                {
                    string json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_diagnosticsPath, json);
                }
                catch { }
            }
        }
    }

    public class RegistryTweakEngine : IRegistryTweakEngine
    {
        private readonly IBackupManager _backupManager;
        private readonly RegistryTransactionJournal _journal;

        public class RegistryTweakDefinition
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string RootKey { get; set; } = "HKLM"; // "HKLM" or "HKCU"
            public string SubKey { get; set; } = string.Empty;
            public string ValueName { get; set; } = string.Empty;
            public RegistryValueKind ValueKind { get; set; } = RegistryValueKind.DWord;
            public string DefaultValueStr { get; set; } = string.Empty;
            public string TargetValueStr { get; set; } = string.Empty;
            public string DisplayTargetStr { get; set; } = string.Empty;
            public string RiskLevel { get; set; } = "Low";
            public bool RequiresRestart { get; set; } = false;
            public bool RequiresAdmin { get; set; } = true;
            public string SupportedOS { get; set; } = "Windows 10 / 11 (x64 / ARM64)";
            public string HardwarePrerequisites { get; set; } = "None";
            public MissingValueBehavior MissingBehavior { get; set; } = MissingValueBehavior.SafeToCreate;
            public RegistryView PreferredView { get; set; } = RegistryView.Registry64;
            public Func<(bool IsApplicable, string Reason)>? CustomApplicability { get; set; }
            public Func<(bool IsRecommended, string Reason)>? CustomRecommendation { get; set; }
        }

        private readonly List<RegistryTweakDefinition> _tweakDatabase = new List<RegistryTweakDefinition>
        {
            // --- CPU & SCHEDULING ---
            new RegistryTweakDefinition
            {
                Id = "tweak.process.priority",
                Name = "Win32PrioritySeparation (Foreground Quantum)",
                Description = "Optimizes CPU priority quantum allocation for active foreground applications, mitigating micro-stutters during intensive interactive tasks.",
                Category = "Process",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl",
                ValueName = "Win32PrioritySeparation",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "38",
                DisplayTargetStr = "38 (Hex 26 • Optimal Quantum)",
                RiskLevel = "Medium",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Workstation / Desktop OS",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.cpu.coreparking",
                Name = "Disable Core Parking",
                Description = "Forces active multi-core execution by setting core parking max headroom to 0, preventing thread wake-up stalls.",
                Category = "CPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\0cc5b647-c1df-4637-891a-dec35c318583",
                ValueName = "ValueMax",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "100",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Unparked)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Multi-Core CPU",
                MissingBehavior = MissingValueBehavior.NotApplicable,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckCoreParkingApplicability()
            },

            // --- NETWORK & MMCSS ---
            new RegistryTweakDefinition
            {
                Id = "tweak.mmcss.networkthrottling",
                Name = "Network Throttling Index",
                Description = "Disables Multimedia Class Scheduler Service (MMCSS) network packet throttling to ensure consistent packet dispatching.",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                ValueName = "NetworkThrottlingIndex",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "10",
                TargetValueStr = "ffffffff",
                DisplayTargetStr = "0xFFFFFFFF (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Network Interface",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.mmcss.systemresponsiveness",
                Name = "MMCSS Background CPU Reservation (SystemResponsiveness)",
                Description = "Configures MMCSS reserved CPU bandwidth for background tasks to 10% (the lowest documented effective setting; values < 10 clamp to 20), allocating up to 90% processor execution time to active multimedia and gaming tasks while protecting critical background services.",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                ValueName = "SystemResponsiveness",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "20",
                TargetValueStr = "10",
                DisplayTargetStr = "10 (10% Background Reserved / 90% Multimedia Quota)",
                RiskLevel = "Medium",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.network.tcpnodelay",
                Name = "TCP No Delay (Nagle's Algorithm)",
                Description = "Disables Nagle's algorithm packet coalescing in Windows TCP/IP stack for immediate socket dispatch.",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                ValueName = "TCPNoDelay",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (No Delay)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "TCP/IP Stack",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = null
            },

            // --- GAMING & GPU ---
            new RegistryTweakDefinition
            {
                Id = "tweak.gaming.gamedvr",
                Name = "Disable Xbox GameDVR",
                Description = "Disables Windows Xbox GameDVR background frame capture to free GPU VRAM and reduce encoder overhead.",
                Category = "Gaming",
                RootKey = "HKCU",
                SubKey = @"System\GameConfigStore",
                ValueName = "GameDVR_Enabled",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "DirectX 11/12 GPU",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gaming.gamedvr_fse",
                Name = "GameDVR FSEBehavior Mode",
                Description = "Configures GameDVR FSE mode to enforce true exclusive fullscreen display presentation for lowest input latency.",
                Category = "Gaming",
                RootKey = "HKCU",
                SubKey = @"System\GameConfigStore",
                ValueName = "GameDVR_FSEBehaviorMode",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Exclusive)",
                RiskLevel = "Medium",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gpu.hags",
                Name = "Hardware Accelerated GPU Scheduling",
                Description = "Enables hardware-level GPU scheduling on supported WDDM 2.7+ drivers to reduce frame render queue overhead.",
                Category = "GPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                ValueName = "HwSchMode",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Enabled)",
                RiskLevel = "Medium",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 Build 19041+ / Windows 11",
                HardwarePrerequisites = "WDDM 2.7+ Compatible GPU",
                MissingBehavior = MissingValueBehavior.Unsupported,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckHagsApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gpu.preemption",
                Name = "Disable GPU Preemption",
                Description = "Directs the GPU scheduler to minimize compute context-switch interrupts during rendering bursts.",
                Category = "GPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler",
                ValueName = "EnablePreemption",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "High",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Dedicated Graphics Card",
                MissingBehavior = MissingValueBehavior.NotApplicable,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckGpuSchedulerApplicability()
            },

            // --- MEMORY & PROCESS ---
            new RegistryTweakDefinition
            {
                Id = "tweak.ram.svchost",
                Name = "SvcHost Split Threshold",
                Description = "Increases per-service host split threshold in KB so Windows hosts system services cleanly on systems with 4GB+ RAM.",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control",
                ValueName = "SvcHostSplitThresholdInKB",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "380000",
                TargetValueStr = "16777216",
                DisplayTargetStr = "16777216 (16GB RAM Threshold)",
                RiskLevel = "Medium",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 Build 1703+ / Windows 11",
                HardwarePrerequisites = "RAM >= 4GB",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckSvcHostSplitApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.ram.clearpagefile",
                Name = "Clear Pagefile on Shutdown",
                Description = "Disables clearing the pagefile at shutdown to eliminate prolonged shutdown delays and prevent multi-gigabyte SSD write cycles.",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "ClearPageFileAtShutdown",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled • Instant Shutdown & SSD Health)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.memory.crashdump_minidump",
                Name = "Windows Crash Dump Kernel Minidump Alignment",
                Description = "Aligns Windows error reporting to generate small kernel minidumps (256KB) instead of allocating multi-gigabyte full system memory dumps on storage.",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\CrashControl",
                ValueName = "CrashDumpEnabled",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "3",
                DisplayTargetStr = "3 (Small Kernel Dump 256KB)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All Storage Configurations",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },

            // --- STORAGE & NTFS ---
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.ntfs8dot3",
                Name = "Disable NTFS 8.3 Name Creation",
                Description = "Disables legacy MS-DOS 8.3 short filename generation across NTFS volumes, reducing disk metadata overhead.",
                Category = "NTFS",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
                ValueName = "NtfsDisable8dot3NameCreation",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Disabled)",
                RiskLevel = "Medium",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "NTFS Volume",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckNtfsVolumeApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.lastaccess",
                Name = "Disable NTFS Last Access Update",
                Description = "Prevents NTFS timestamp write updates whenever files or directories are read, saving SSD/HDD write operations.",
                Category = "NTFS",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
                ValueName = "NtfsDisableLastAccessUpdate",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "NTFS Volume",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckNtfsVolumeApplicability()
            },

            // --- PRIVACY & TELEMETRY ---
            new RegistryTweakDefinition
            {
                Id = "tweak.privacy.telemetry",
                Name = "Disable AllowTelemetry Policy",
                Description = "Sets diagnostic data policy collection to Security/Disabled level to eliminate background telemetry bandwidth.",
                Category = "Privacy",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                ValueName = "AllowTelemetry",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.privacy.advertisingid",
                Name = "Disable Windows Advertising ID",
                Description = "Disables user advertising profiling identifier in current user profile.",
                Category = "Privacy",
                RootKey = "HKCU",
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                ValueName = "Enabled",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.telemetry.diagtrack",
                Name = "Disable DiagTrack Service Startup",
                Description = "Sets Connected User Experiences and Telemetry service startup configuration to Disabled in registry.",
                Category = "Telemetry",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\DiagTrack",
                ValueName = "Start",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "4",
                DisplayTargetStr = "4 (Disabled)",
                RiskLevel = "Medium",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "DiagTrack Service",
                MissingBehavior = MissingValueBehavior.NotApplicable,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckServiceKeyExists(@"SYSTEM\CurrentControlSet\Services\DiagTrack", "DiagTrack service detected.", "DiagTrack service is not installed on this system.")
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.telemetry.dmwappushservice",
                Name = "Disable WAP Push Service Startup",
                Description = "Sets Device Management Wireless Application Protocol (WAP) push message service startup to Disabled.",
                Category = "Telemetry",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\dmwappushservice",
                ValueName = "Start",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "3",
                TargetValueStr = "4",
                DisplayTargetStr = "4 (Disabled)",
                RiskLevel = "Medium",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "dmwappushservice",
                MissingBehavior = MissingValueBehavior.NotApplicable,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckServiceKeyExists(@"SYSTEM\CurrentControlSet\Services\dmwappushservice", "dmwappushservice detected.", "dmwappushservice is not installed on this system.")
            },

            // --- SYSTEM & VISUAL EFFECTS ---
            new RegistryTweakDefinition
            {
                Id = "tweak.explorer.search",
                Name = "Disable Web Search in Start Menu",
                Description = "Disables Bing web search queries in Start Menu search box, eliminating background internet lookup delay.",
                Category = "System",
                RootKey = "HKCU",
                SubKey = @"SOFTWARE\Policies\Microsoft\Windows\Explorer",
                ValueName = "DisableSearchBoxSuggestions",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.explorer.cortana",
                Name = "Disable Cortana Policy",
                Description = "Disables Cortana background search index assistant integration in group policy registry.",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowCortana",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.visuals.animations",
                Name = "Disable Window Animations",
                Description = "Disables minimize and maximize window animation transitions for immediate window rendering.",
                Category = "Visual",
                RootKey = "HKCU",
                SubKey = @"Control Panel\Desktop\WindowMetrics",
                ValueName = "MinAnimate",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "\"0\" (Instant)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Desktop Session",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.visuals.menushowdelay",
                Name = "Menu Show Delay",
                Description = "Reduces context menu display delay to 0 ms for instant popup response on click.",
                Category = "Visual",
                RootKey = "HKCU",
                SubKey = @"Control Panel\Desktop",
                ValueName = "MenuShowDelay",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "400",
                TargetValueStr = "0",
                DisplayTargetStr = "\"0\" (0 ms)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },

            // --- INPUT & POWER ---
            new RegistryTweakDefinition
            {
                Id = "tweak.input.mousehover",
                Name = "Mouse Hover Time",
                Description = "Reduces mouse hover tooltip activation delay from 400ms to 10ms for responsive UI interaction feedback.",
                Category = "Input",
                RootKey = "HKCU",
                SubKey = @"Control Panel\Mouse",
                ValueName = "MouseHoverTime",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "400",
                TargetValueStr = "10",
                DisplayTargetStr = "\"10\" (10 ms)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Mouse Subsystem",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.power.hibernation",
                Name = "Disable System Hibernation",
                Description = "Disables Windows hibernation to prevent continuous hiberfil.sys disk writes, saving storage space and SSD endurance.",
                Category = "Power",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power",
                ValueName = "HibernateEnabled",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Medium",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "ACPI Power Management",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.power.faststartup",
                Name = "Disable Fast Startup (Hiberboot)",
                Description = "Disables kernel state hybrid hibernation caching across shutdowns to ensure a fresh, clean hardware and driver boot.",
                Category = "Power",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                ValueName = "HiberbootEnabled",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Medium",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },

            // --- NEW CAPABILITY 1: MEMORY MANAGEMENT SUBSYSTEM ---
            new RegistryTweakDefinition
            {
                Id = "tweak.ram.largesystemcache",
                Name = "LargeSystemCache (File Cache Expansion)",
                Description = "Allocates a larger working set for the Windows system file cache, maximizing read cache hit ratios on multi-gigabyte disk transfers and video editing assets.",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "LargeSystemCache",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Expanded Cache)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "16 GB+ Physical RAM",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckLargeSystemCacheApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.ram.disablepagingexec",
                Name = "DisablePagingExecutive (Keep Kernel in RAM)",
                Description = "Forces Windows kernel executive and loaded drivers to remain resident in physical RAM rather than being paged to disk, preventing page fault latency.",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "DisablePagingExecutive",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Resident in RAM)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "16 GB+ Physical RAM",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckDisablePagingExecutiveApplicability()
            },

            // --- NEW CAPABILITY 2: HIGH-PERFORMANCE NTFS FILESYSTEM PARAMETERS ---
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.ntfsmemoryusage",
                Name = "NtfsMemoryUsage (NTFS Metadata Cache Boost)",
                Description = "Expands the NTFS paged-pool lookaside lists for metadata caching, boosting responsiveness during deep directory traversal and creative timeline scrubbing.",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
                ValueName = "NtfsMemoryUsage",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Expanded Cache Pool)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "16 GB+ Physical RAM",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckNtfsMemoryUsageApplicability()
            },

            // --- NEW CAPABILITY 3: DESKTOP & SHELL RESPONSIVENESS SUBSYSTEM ---
            new RegistryTweakDefinition
            {
                Id = "tweak.system.waittokillapptimeout",
                Name = "WaitToKillAppTimeout (Rapid Hung App Termination)",
                Description = "Sets hung application wait threshold to 2000ms, terminating frozen processes swiftly without delaying system shutdowns.",
                Category = "Process",
                RootKey = "HKCU",
                SubKey = @"Control Panel\Desktop",
                ValueName = "WaitToKillAppTimeout",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "5000",
                TargetValueStr = "2000",
                DisplayTargetStr = "2000 (2000ms)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.system.hungapptimeout",
                Name = "HungAppTimeout (Desktop Freeze Prevention)",
                Description = "Shortens unresponsive application timeout from 5000ms to 2000ms to prompt recovery faster without interrupting transient heavy operations.",
                Category = "Process",
                RootKey = "HKCU",
                SubKey = @"Control Panel\Desktop",
                ValueName = "HungAppTimeout",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "5000",
                TargetValueStr = "2000",
                DisplayTargetStr = "2000 (2000ms)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gpu.hardwareflipqueue",
                Name = "Direct3D Hardware Flip Queue (1 Frame)",
                Description = "Limits Direct3D prerendered flip queue depth to 1 frame to eliminate input latency between mouse input and GPU frame scanout.",
                Category = "GPU",
                RootKey = "HKCU",
                SubKey = @"Software\Microsoft\Direct3D",
                ValueName = "MaxFrameLatency",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "3",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Low Latency)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Dedicated Graphics Card",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.network.tcpackfrequency",
                Name = "TCP Immediate ACK Frequency (No Batch Delay)",
                Description = "Configures TCP ACK frequency to 1 to transmit packet acknowledgments immediately, eliminating 200ms delayed ACK queuing in online games.",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\Default",
                ValueName = "TcpAckFrequency",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Immediate ACK)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Active Network Adapter",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.explorer.autorestartshell",
                Name = "Auto-Restart Explorer on Hang",
                Description = "Instructs Windows Winlogon to automatically restart explorer.exe if the desktop shell hangs or crashes.",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                ValueName = "AutoRestartShell",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Auto-Restart Enabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "None",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.disable_compression_overhead",
                Name = "NTFS Volume Compression Overhead Elimination",
                Description = "Disables background file system compression routines on NTFS volumes, eliminating CPU cycles and latency during asset reads.",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
                ValueName = "NtfsDisableCompression",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Compression Disabled / Fast I/O)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "NVMe / SSD Storage",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckNvmeOrSsdStorageApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.mmcss.pro_audio_quantum",
                Name = "Pro Audio Real-Time Thread Priority",
                Description = "Assigns High scheduling category to the Windows Pro Audio MMCSS task to guarantee glitch-free low latency ASIO and WASAPI buffers.",
                Category = "Audio",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Pro Audio",
                ValueName = "Scheduling Category",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "Medium",
                TargetValueStr = "High",
                DisplayTargetStr = "\"High\"",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Windows Audio",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.system.timerresolutionrequests",
                Name = "Lock Global NT High Precision Timer",
                Description = "Enforces global high-precision kernel interrupt timer requests across all multimedia and DirectX applications.",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                ValueName = "GlobalTimerResolutionRequests",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Locked)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "High Precision Event Timer (HPET)",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.power.pcieaspm_disable",
                Name = "Disable PCIe ASPM Link State Power Management",
                Description = "Exposes and disables PCIe Active State Power Management on AC to eliminate GPU and NVMe bus wake latency.",
                Category = "Power",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\501a4d13-42af-4429-9fd5-6921fb825d05\ee12f906-d277-404b-b6da-f55a15196015",
                ValueName = "Attributes",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Exposed & Tuned)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "PCIe Bus Devices",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.visuals.transparency_off",
                Name = "Disable Desktop Acrylic Transparency Blur",
                Description = "Disables desktop window acrylic blur composition to minimize DWM fillrate overhead during gaming and rendering.",
                Category = "Visual",
                RootKey = "HKCU",
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                ValueName = "EnableTransparency",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Desktop Session",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gaming.fullscreenexclusive_optimizations",
                Name = "DXGI Modern Independent Flip Upgrade",
                Description = "Upgrades legacy windowed DX11 presentation to Modern Flip Model with hardware independent flip and variable refresh rate.",
                Category = "Gaming",
                RootKey = "HKCU",
                SubKey = @"Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings",
                ValueName = "SwapEffectUpgrade",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Modern Flip Active)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "WDDM 2.7+ GPU",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.cpu.intel_threaddirector_pcore",
                Name = "Intel Thread Director Heterogeneous Core Scheduling Calibration",
                Description = "Exposes processor heterogeneous core scheduling policy controls to prioritize Performance Cores for foreground gaming and emulator workloads.",
                Category = "CPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\7f2f4c9f-3e54-4a2b-9e0c-8d3a5a7b1e2f",
                ValueName = "Attributes",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Exposed & Tuned)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Heterogeneous Architecture CPU",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckIntelHeteroCpuApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gpu.d3d9ex_latency",
                Name = "Direct3D Frame Latency Reduction",
                Description = "Limits DirectX queued pre-rendered frames to 1, minimizing input latency and visual queue buffer lag in Direct3D applications.",
                Category = "Gaming",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Direct3D",
                ValueName = "MaxFrameLatency",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "3",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Low Latency Buffer)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "DirectX Compatible GPU",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.memory.pagedpoolsize",
                Name = "Kernel Paged Pool Size Optimization",
                Description = "Dynamically sizes kernel paged pool allocations to prevent pool exhaustion during heavy gaming, emulator, and rendering sessions.",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "PagedPoolSize",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Dynamic Auto-Allocation)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "16GB+ RAM",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.network.sackopts",
                Name = "TCP Selective Acknowledgments (SACK)",
                Description = "Enables TCP Selective Acknowledgments to recover from packet drops faster and lower network jitter during online competitive matches.",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                ValueName = "SackOpts",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Enabled)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Ethernet / Wi-Fi Adapter",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.io.priority_boost",
                Name = "Executive I/O Priority Boost Threshold",
                Description = "Adjusts executive storage worker I/O priority boost to prioritize foreground read/write requests over background indexing operations.",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\I/O System",
                ValueName = "PriorityBoostThreshold",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Foreground Prioritized)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "SSD / NVMe Storage",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gaming.directstorage_bypass",
                Name = "DirectStorage Win32 BypassIO Enabler",
                Description = "Enables OS-level BypassIO driver fast paths allowing NVMe storage devices to stream assets directly into GPU VRAM without CPU bottlenecking when supported by storage drivers.",
                Category = "Gaming",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
                ValueName = "BypassIoEnabler",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (BypassIO Active)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 11 / Windows 10 21H2+",
                HardwarePrerequisites = "NVMe PCIe 3.0+ SSD & NTFS",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckBypassIoApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.cpu.amd_cppc_preferred",
                Name = "AMD CPPC Preferred Cores Dynamic Affinity Exposure",
                Description = "Exposes Collaborative Processor Performance Control (CPPC) preferred core scheduling knobs to maximize single-thread clock boost.",
                Category = "CPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\893dee8e-2bef-41e0-89c6-b50d09294504",
                ValueName = "Attributes",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Exposed & Tuned)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "AMD Ryzen Processor",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckAmdProcessorApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.input.hid_mousedataqueue",
                Name = "Mouse Class Driver Buffer Size Optimization",
                Description = "Optimizes mouclass driver input packet buffer size to 20 packets, reducing input buffering delay.",
                Category = "Input",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters",
                ValueName = "MouseDataQueueSize",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "100",
                TargetValueStr = "20",
                DisplayTargetStr = "20 (Low-Latency Queue)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "USB / PS/2 Mouse",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.input.hid_keyboarddataqueue",
                Name = "Keyboard Class Driver Buffer Size Calibration",
                Description = "Optimizes kbdclass driver event queue size to 20 packets, eliminating keystroke latency.",
                Category = "Input",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters",
                ValueName = "KeyboardDataQueueSize",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "100",
                TargetValueStr = "20",
                DisplayTargetStr = "20 (Low-Latency Queue)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "USB / PS/2 Keyboard",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.network.rss_hash_toeplitz",
                Name = "NDIS Max Receive-Side Scaling Processors",
                Description = "Sets maximum RSS CPU count to expand hardware multi-queue network packet handling across available physical processor cores.",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\Ndis\Parameters",
                ValueName = "MaxNumRssCpus",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "4",
                TargetValueStr = "16",
                DisplayTargetStr = "16 (Multi-Queue Dynamic)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Multi-Core CPU & RSS NIC",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.memory.working_set_trim",
                Name = "System Working Set Dynamic Memory Trimming",
                Description = "Maintains optimal paged memory working sets to prevent aggressive swap-out during high memory allocation rendering or emulator tasks.",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "PoolUsageMaximum",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "60",
                DisplayTargetStr = "60 (Dynamic Paging Threshold)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "8GB+ RAM",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.desktop.dwm_waitable_buffer",
                Name = "DWM Presentation Maximum Queued Buffers",
                Description = "Constrains Desktop Window Manager frame queue to a single waitable buffer, decreasing compositor display pipeline delay.",
                Category = "Gaming",
                RootKey = "HKCU",
                SubKey = @"Software\Microsoft\Windows\DWM",
                ValueName = "MaxQueuedBuffers",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Single Queued Buffer)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "WDDM 2.0+ GPU",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Default
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gpu.dxgi_presentation_budget",
                Name = "GPU Compute Watchdog Execution Timeout (TdrDelay for 3D/Video Rendering)",
                Description = "Configures GPU execution watchdog quantum threshold (TdrDelay) to 8 seconds specifically for long-running CUDA/DirectCompute rendering jobs (DaVinci/Blender/After Effects) to prevent false driver resets. Not recommended for standard gaming.",
                Category = "Creative / 3D Compute Diagnostics",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                ValueName = "TdrDelay",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "8",
                DisplayTargetStr = "8s (Render Crash Prevention)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Dedicated GPU & 3D/Video Rendering Workload",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomRecommendation = () => CheckTdrRecommendation()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.audio.wasapi_exclusive_affinity",
                Name = "MMCSS Low-Latency Audio Core Affinity Mask",
                Description = "Locks Windows Audio multimedia class thread scheduling to high-efficiency affinity masks to prevent sound dropouts during gaming.",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Audio",
                ValueName = "Affinity",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Dynamic Balanced Affinity)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "High Definition Audio Device",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.iopagelocklimit",
                Name = "Storage High I/O Page Lock Limit (16MB)",
                Description = "Expands maximum pinned physical memory pages for disk controller transfers to 16MB for ultra-fast NVMe throughput.",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "IoPageLockLimit",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "16777216",
                DisplayTargetStr = "16MB (High-Performance I/O Lock)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "16GB+ RAM & NVMe SSD",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => { var r = CheckLargeSystemCacheApplicability(); if (!r.IsApplicable) return r; return CheckNvmeStorageApplicability(); }
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.system.app_launch_prefetch_boost",
                Name = "Application Launch Prefetch Engine Configuration",
                Description = "Disables legacy rotational HDD prefetcher to eliminate background disk cache read overhead on modern SSD/NVMe storage.",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                ValueName = "EnablePrefetcher",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "3",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (Disabled for SSD / NVMe Performance)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All Storage Devices",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.gaming.fullscreen_optimizations",
                Name = "DirectX Fullscreen Optimizations Toggle",
                Description = "Ensures legacy borderless and windowed DirectX games run with native low-latency swapchains without composition overhead.",
                Category = "Gaming",
                RootKey = "HKCU",
                SubKey = @"System\GameConfigStore",
                ValueName = "GameDVR_DXGIHonorFSEWindowsCompatible",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Low Latency Direct Swapchain)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All GPUs",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.graphics.hardware_gpu_scheduler_preference",
                Name = "Hardware-Accelerated GPU Scheduling (HAGS) Preference",
                Category = "GPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                ValueName = "HwSchMode",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (HAGS Enabled)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 Build 2004+ / Windows 11",
                HardwarePrerequisites = "WDDM 2.7+ GPU with >= 6GB VRAM",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.input.keyboard_queue_size",
                Name = "Keyboard Data Buffer Queue Size Expansion",
                Category = "Input",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters",
                ValueName = "KeyboardDataQueueSize",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "100",
                TargetValueStr = "20",
                DisplayTargetStr = "20 Packets (Low-Latency)",
                RiskLevel = "Safe",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Keyboard",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.input.mouse_queue_size",
                Name = "Mouse Data Buffer Queue Size Expansion",
                Category = "Input",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters",
                ValueName = "MouseDataQueueSize",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "100",
                TargetValueStr = "20",
                DisplayTargetStr = "20 Packets (Low-Latency)",
                RiskLevel = "Safe",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Mouse",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.nvme_storport_idle",
                Name = "NVMe StorPort Miniport Idle Latency Tuning",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device",
                ValueName = "IdlePowerMode",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (High Responsiveness)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "NVMe Storage",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckNvmeStorageApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.network.tcp_ack_frequency_gaming",
                Name = "Immediate TCP ACK Frequency (TcpAckFrequency = 1)",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                ValueName = "TcpAckFrequency",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "2",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Immediate ACK)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Ethernet / Wi-Fi",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.network.nagle_algorithm",
                Name = "Disable Nagle Algorithm (TCPNoDelay = 1)",
                Category = "Network",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                ValueName = "TCPNoDelay",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (No Delay)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Ethernet / Wi-Fi",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.memory.paging_executive_lock",
                Name = "Lock Windows Executive Drivers in Physical RAM",
                Category = "Memory",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "DisablePagingExecutive",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Locked in RAM)",
                RiskLevel = "Low",
                RequiresRestart = true,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "RAM >= 16GB",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.system.explorer_auto_restart",
                Name = "Automatic Windows Explorer Restart on Crash",
                Category = "System",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                ValueName = "AutoRestartShell",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Enabled)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All Systems",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.system.error_reporting_telemetry",
                Name = "Disable Windows Error Reporting Background Telemetry",
                Category = "Privacy",
                RootKey = "HKLM",
                SubKey = @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                ValueName = "Disabled",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "1",
                DisplayTargetStr = "1 (Disabled)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All Systems",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.cpu.cppc_preferred_cores",
                Name = "CPPC Autonomous Preferred Core Targeting",
                Category = "CPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\89e37796-a416-4a73-9ef1-77886f795cb9",
                ValueName = "Attributes",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Autonomous)",
                RiskLevel = "Low",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "CPPC Supported CPU",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckAmdProcessorApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.cpu.quantum_interactive_gaming",
                Name = "Interactive Gaming CPU Quantum (0x26)",
                Category = "CPU",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl",
                ValueName = "Win32PrioritySeparation",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "24",
                TargetValueStr = "38",
                DisplayTargetStr = "38 (0x26 Interactive)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All CPUs",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.storage.bypassio_stack",
                Name = "Windows DirectStorage BypassIO Storage Stack Enable",
                Category = "Storage",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
                ValueName = "BypassIoFlags",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "0",
                TargetValueStr = "0",
                DisplayTargetStr = "0 (BypassIO Enabled)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 11",
                HardwarePrerequisites = "NVMe Storage",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64,
                CustomApplicability = () => CheckBypassIoApplicability()
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.power.cooling_policy_active",
                Name = "Active System Cooling Policy Enforcement",
                Category = "Power",
                RootKey = "HKLM",
                SubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\94d3a615-a899-4ac5-ae2b-e4d8f634367f",
                ValueName = "Attributes",
                ValueKind = RegistryValueKind.DWord,
                DefaultValueStr = "1",
                TargetValueStr = "2",
                DisplayTargetStr = "2 (Active Cooling)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = true,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "All Systems",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            },
            new RegistryTweakDefinition
            {
                Id = "tweak.input.filter_keys_shortcuts",
                Name = "Disable Sticky & Filter Keys Accessibility Shortcuts",
                Category = "Input",
                RootKey = "HKCU",
                SubKey = @"Control Panel\Accessibility\StickyKeys",
                ValueName = "Flags",
                ValueKind = RegistryValueKind.String,
                DefaultValueStr = "510",
                TargetValueStr = "506",
                DisplayTargetStr = "506 (Disabled)",
                RiskLevel = "Safe",
                RequiresRestart = false,
                RequiresAdmin = false,
                SupportedOS = "Windows 10 / 11",
                HardwarePrerequisites = "Keyboard",
                MissingBehavior = MissingValueBehavior.SafeToCreate,
                PreferredView = RegistryView.Registry64
            }
        };

        public RegistryTweakEngine(IBackupManager backupManager)
        {
            _backupManager = backupManager;
            _journal = new RegistryTransactionJournal();
        }

        #region Pre-Flight & Subsystem Applicability Checks

        public static bool IsRunningAsSystem()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.IsSystem;
            }
            catch
            {
                return false;
            }
        }

        public static List<string> GetInteractiveUserSids()
        {
            var sids = new List<string>();
            try
            {
                using var usersBase = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                foreach (var name in usersBase.GetSubKeyNames())
                {
                    if (name.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    {
                        using var testSub = usersBase.OpenSubKey(name, false);
                        if (testSub != null)
                        {
                            sids.Add(name);
                        }
                    }
                }
            }
            catch { }
            return sids;
        }

        private static bool IsWindowsVersionSupported(string supportedOS)
        {
            try
            {
                int major = Environment.OSVersion.Version.Major;
                return major >= 10;
            }
            catch
            {
                return true;
            }
        }

        private static (bool IsApplicable, string Reason) CheckNtfsVolumeApplicability()
        {
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
                bool hasNtfs = drives.Any(d => string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase));
                return (hasNtfs, hasNtfs ? "Fixed NTFS volume detected." : "No active NTFS volumes detected on this machine.");
            }
            catch (Exception ex)
            {
                return (true, $"Volume check pass: {ex.Message}");
            }
        }

        private static (bool IsApplicable, string Reason) CheckMsmqApplicability()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\MSMQ", false);
                if (k != null) return (true, "Microsoft Message Queuing (MSMQ) subsystem detected.");
                return (false, "MSMQ subsystem is not installed on this Windows installation.");
            }
            catch
            {
                return (false, "MSMQ subsystem check unavailable.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckCoreParkingApplicability()
        {
            if (Environment.ProcessorCount <= 1)
                return (false, "Single-core processor detected; Core Parking is not applicable.");

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\0cc5b647-c1df-4637-891a-dec35c318583", false);
                if (k != null) return (true, "Multi-core processor with configurable Core Parking power scheme detected.");

                return (false, "Core parking power settings key is not supported by current Windows power configuration.");
            }
            catch
            {
                return (false, "Core parking power settings key inaccessible.");
            }
        }

        
        private static (bool IsApplicable, string Reason) CheckLargeSystemCacheApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (hw.Memory.InstalledPhysicalGb < 16.0)
                    return (false, $"Requires at least 16 GB of physical RAM (Detected {hw.Memory.InstalledPhysicalGb:F1} GB).");
                return (true, "System has 16+ GB RAM suitable for expanded file system caching.");
            }
            catch
            {
                return (true, "Applicable on systems with adequate memory.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckDisablePagingExecutiveApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (hw.Memory.InstalledPhysicalGb < 16.0)
                    return (false, $"Requires at least 16 GB of physical RAM (Detected {hw.Memory.InstalledPhysicalGb:F1} GB) to prevent kernel pool exhaustion.");
                return (true, "System has 16+ GB RAM suitable for keeping kernel executive resident in physical memory.");
            }
            catch
            {
                return (true, "Applicable on systems with adequate memory.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckNtfsMemoryUsageApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (hw.Memory.InstalledPhysicalGb < 16.0)
                    return (false, $"Requires at least 16 GB of physical RAM (Detected {hw.Memory.InstalledPhysicalGb:F1} GB).");
                return (true, "System has 16+ GB RAM suitable for expanded NTFS paged-pool cache quota.");
            }
            catch
            {
                return (true, "Applicable on systems with adequate memory.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckHagsApplicability()
        {
            try
            {
                if (Environment.OSVersion.Version.Major < 10 || (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build < 19041))
                    return (false, "Windows 10 Build 19041+ or Windows 11 required for Hardware Accelerated GPU Scheduling.");

                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", false);
                if (k == null) return (false, "GraphicsDrivers registry tree not found on this machine.");

                return (true, "Hardware Accelerated GPU Scheduling supported.");
            }
            catch (Exception ex)
            {
                return (false, $"HAGS check failed: {ex.Message}");
            }
        }

        private static (bool IsApplicable, string Reason) CheckGpuSchedulerApplicability()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var schedKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler", false);
                if (schedKey != null) return (true, "Graphics scheduler engine present and accessible.");

                return (false, "GraphicsDrivers\\Scheduler subkey is not supported or present in current display driver stack.");
            }
            catch (Exception ex)
            {
                return (false, $"GPU scheduler check failed: {ex.Message}");
            }
        }

        private static (bool IsApplicable, string Reason) CheckSvcHostSplitApplicability()
        {
            try
            {
                var gcMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                bool hasEnoughRam = gcMem >= (3L * 1024 * 1024 * 1024);
                return (hasEnoughRam, hasEnoughRam ? "Sufficient physical RAM detected (>= 3.5GB)." : "RAM is under 4GB; SvcHost split is not recommended.");
            }
            catch
            {
                return (true, "Memory threshold check standard pass.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckPagingExecApplicability()
        {
            try
            {
                var memBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                bool has8Gb = memBytes >= (7L * 1024 * 1024 * 1024);
                return (has8Gb, has8Gb ? "Physical RAM is 8GB or greater." : "System has less than 8GB RAM; keeping paging executive enabled is recommended for low-memory stability.");
            }
            catch
            {
                return (true, "RAM check passed.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckBypassIoApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.Storage.HasNvmeDrive)
                    return (false, "BypassIO requires NVMe storage. SATA SSD / HDD storage cannot utilize BypassIO fast paths.");
                if (Environment.OSVersion.Version.Build < 22000)
                    return (false, "BypassIO requires Windows 11 Build 22000 or newer.");
                return (true, "System has NVMe storage on Windows 11 supporting DirectStorage BypassIO.");
            }
            catch
            {
                return (false, "Unable to verify NVMe BypassIO storage prerequisites.");
            }
        }
        private static (bool IsApplicable, string Reason) CheckAmdProcessorApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.Cpu.IsAmd)
                    return (false, "Requires AMD Ryzen processor (Intel CPU detected).");
                return (true, "AMD Ryzen processor detected with CPPC preferred core capabilities.");
            }
            catch
            {
                return (false, "Unable to verify AMD processor prerequisites.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckIntelHeteroCpuApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.Cpu.IsIntel)
                    return (false, "Requires Intel Core processor (AMD CPU detected).");
                if (!hw.Cpu.IsHybridCpu)
                    return (false, "Requires Intel Hybrid Architecture with Performance and Efficient cores.");
                return (true, "Intel Hybrid processor with Thread Director scheduling detected.");
            }
            catch
            {
                return (false, "Unable to verify Intel Thread Director prerequisites.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckNvmeStorageApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.Storage.HasNvmeDrive)
                    return (false, "Requires NVMe solid-state storage (NVMe drive not detected).");
                return (true, "NVMe solid-state storage detected.");
            }
            catch
            {
                return (false, "Unable to verify NVMe storage prerequisites.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckNvmeOrSsdStorageApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.Storage.HasNvmeDrive && !hw.Storage.HasSataSsd)
                    return (false, "Requires solid-state storage (SSD/NVMe). Mechanical hard disk drives do not benefit from this configuration.");
                return (true, "Solid-state storage detected.");
            }
            catch
            {
                return (false, "Unable to verify solid-state storage prerequisites.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckDedicatedNvidiaGpuApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.HasNvidia)
                    return (false, "Requires NVIDIA GeForce graphics hardware (not detected on this PC).");
                return (true, "NVIDIA GeForce graphics hardware detected.");
            }
            catch
            {
                return (false, "Unable to verify NVIDIA GPU prerequisites.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckDedicatedAmdGpuApplicability()
        {
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                if (!hw.HasAmd)
                    return (false, "Requires AMD Radeon graphics hardware (not detected on this PC).");
                return (true, "AMD Radeon graphics hardware detected.");
            }
            catch
            {
                return (false, "Unable to verify AMD GPU prerequisites.");
            }
        }


        private static (bool IsRecommended, string Reason) CheckTdrRecommendation()
        {
            try
            {
                var procs = Process.GetProcesses();
                bool hasRendering = procs.Any(p =>
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    return name.Contains("afterfx") || name.Contains("premiere") || name.Contains("resolve") ||
                           name.Contains("blender") || name.Contains("topaz");
                });

                if (hasRendering)
                    return (true, "Recommended for detected creative/rendering application to prevent premature TDR driver reset during heavy compute passes.");
                return (false, "Not recommended for general gaming or daily desktop use; 8s TDR delay will prolong screen freeze during driver recovery.");
            }
            catch
            {
                return (false, "TDR delay modification is reserved for heavy 3D/video rendering workloads.");
            }
        }

        private static (bool IsApplicable, string Reason) CheckServiceKeyExists(string subKey, string foundReason, string missingReason)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(subKey, false);
                if (k != null) return (true, foundReason);
                return (false, missingReason);
            }
            catch
            {
                return (false, missingReason);
            }
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Robust Cross-PC Registry Access & Read-Back

        public static (RegistryKey? Key, RegistryView ResolvedView, string ErrorCode, string ErrorDetail) OpenTargetKeyWithPreflight(
            string root, string subKey, bool writable, RegistryView preferredView = RegistryView.Registry64)
        {
            var viewsToTry = new[] { preferredView, preferredView == RegistryView.Registry64 ? RegistryView.Registry32 : RegistryView.Registry64 };

            foreach (var view in viewsToTry)
            {
                try
                {
                    if (string.Equals(root, "HKCU", StringComparison.OrdinalIgnoreCase))
                    {
                        // 1. If running under NT AUTHORITY\SYSTEM (Service), resolve active user profile in HKEY_USERS
                        if (IsRunningAsSystem())
                        {
                            var sids = GetInteractiveUserSids();
                            if (sids.Count > 0)
                            {
                                var usersBase = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
                                string primarySid = sids[0];
                                string userSubKey = $"{primarySid}\\{subKey}";

                                try
                                {
                                    var key = writable ? usersBase.CreateSubKey(userSubKey, true) : usersBase.OpenSubKey(userSubKey, false);
                                    if (key != null)
                                    {
                                        if (writable && sids.Count > 1)
                                        {
                                            for (int i = 1; i < sids.Count; i++)
                                            {
                                                try
                                                {
                                                    using var mirrorKey = usersBase.CreateSubKey($"{sids[i]}\\{subKey}", true);
                                                }
                                                catch { }
                                            }
                                        }
                                        return (key, view, "0x00000000", "Success via HKEY_USERS interactive SID");
                                    }
                                }
                                catch (UnauthorizedAccessException ex)
                                {
                                    return (null, view, "0x80070005", $"Access Denied opening HKCU\\{subKey}: {ex.Message}");
                                }
                                catch (Exception ex)
                                {
                                    return (null, view, "0x80004005", $"Error accessing HKEY_USERS: {ex.Message}");
                                }
                            }
                        }

                        // 2. Interactive user context (GUI or standard user)
                        try
                        {
                            var cuBase = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                            var key = writable ? cuBase.CreateSubKey(subKey, true) : cuBase.OpenSubKey(subKey, false);
                            if (key != null) return (key, view, "0x00000000", "Success");
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            return (null, view, "0x80070005", $"Access Denied opening HKCU\\{subKey}: {ex.Message}");
                        }
                        catch (System.Security.SecurityException ex)
                        {
                            return (null, view, "0x80070005", $"Security error opening HKCU\\{subKey}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Local Machine hive via explicit RegistryView
                        try
                        {
                            var lmBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                            var key = writable ? lmBase.CreateSubKey(subKey, true) : lmBase.OpenSubKey(subKey, false);
                            if (key != null) return (key, view, "0x00000000", "Success");
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            return (null, view, "0x80070005", $"Access Denied opening HKLM\\{subKey}: {ex.Message}");
                        }
                        catch (System.Security.SecurityException ex)
                        {
                            return (null, view, "0x80070005", $"Security error opening HKLM\\{subKey}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (null, view, "0x80004005", $"Exception opening base key: {ex.Message}");
                }
            }

            return (null, preferredView, "0x80070002", $"Registry key '{root}\\{subKey}' not found.");
        }

        private static DateTime GetSystemBootTimeUtc()
        {
            try
            {
                return DateTime.UtcNow - TimeSpan.FromMilliseconds((double)Environment.TickCount64);
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        private static object ParseTargetValue(string targetStr, RegistryValueKind kind)
        {
            if (kind == RegistryValueKind.DWord)
            {
                string trimmed = targetStr.Trim();
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(2);

                if (uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint uHex) &&
                    (targetStr.Contains("f", StringComparison.OrdinalIgnoreCase) || targetStr.Contains("x", StringComparison.OrdinalIgnoreCase)))
                {
                    return (int)uHex;
                }
                if (uint.TryParse(targetStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint uDec))
                {
                    return (int)uDec;
                }
                if (int.TryParse(targetStr, out int iDec))
                {
                    return iDec;
                }
                return 0;
            }
            else if (kind == RegistryValueKind.QWord)
            {
                if (long.TryParse(targetStr, out long qVal)) return qVal;
                if (ulong.TryParse(targetStr, out ulong uqVal)) return (long)uqVal;
                return 0L;
            }
            else if (kind == RegistryValueKind.String)
            {
                string s = targetStr.Trim();
                if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
                {
                    s = s.Substring(1, s.Length - 2);
                }
                return s;
            }
            return targetStr;
        }

        private (bool Matches, string DisplayVal) EvaluateValue(RegistryTweakDefinition def, object? val)
        {
            if (val == null) return (false, "NOT CONFIGURED");

            string sVal = val.ToString() ?? "";
            bool isMatch = StateNormalizer.IsSatisfied(sVal, def.TargetValueStr);

            string display;
            if (sVal == "4294967295" || sVal == "-1" || (def.TargetValueStr.Equals("ffffffff", StringComparison.OrdinalIgnoreCase) && isMatch))
            {
                display = "0xFFFFFFFF (Disabled)";
            }
            else if (def.ValueKind == RegistryValueKind.String)
            {
                display = $"\"{sVal}\"";
            }
            else
            {
                display = sVal;
            }

            return (isMatch, display);
        }

        #endregion

        public async Task<RegistryTweakResponse> ReconcileStartupAsync()
        {
            return await Task.Run(() =>
            {
                var journalEntries = _journal.LoadEntries();
                var bootTime = GetSystemBootTimeUtc();

                foreach (var entry in journalEntries)
                {
                    var def = _tweakDatabase.FirstOrDefault(t => string.Equals(t.Id, entry.OptimizationId, StringComparison.OrdinalIgnoreCase));
                    if (def == null) continue;

                    var (key, _, _, _) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, false, def.PreferredView);
                    if (key != null)
                    {
                        using (key)
                        {
                            var liveVal = key.GetValue(def.ValueName);
                            var (matches, displayVal) = EvaluateValue(def, liveVal);
                            entry.CurrentValue = displayVal;

                            if (entry.RequiresReboot && (entry.ApplyState == "APPLIED_PENDING_REBOOT" || entry.VerificationState == "PENDING_REBOOT"))
                            {
                                if (bootTime > entry.Timestamp)
                                {
                                    if (matches)
                                    {
                                        entry.VerificationState = "VERIFIED";
                                        entry.ApplyState = "APPLIED";
                                        entry.RebootVerifiedTimestamp = DateTime.UtcNow;
                                    }
                                    else
                                    {
                                        entry.VerificationState = "VERIFICATION_FAILED";
                                    }
                                }
                            }
                            else if (entry.VerificationState == "VERIFIED" && !matches)
                            {
                                entry.VerificationState = "EXTERNALLY_CHANGED";
                            }

                            _journal.SaveEntry(entry);
                        }
                    }
                }

                return PlanRegistryTweakInternal();
            });
        }

        public async Task<RegistryTweakResponse> PlanRegistryTweakAsync()
        {
            return await Task.Run(() => PlanRegistryTweakInternal());
        }

        private RegistryTweakResponse PlanRegistryTweakInternal()
        {
            var response = new RegistryTweakResponse { Success = true, Message = "Plan generated from authoritative live registry pre-flight." };
            var journalMap = _journal.LoadEntries().ToDictionary(x => x.OptimizationId, x => x, StringComparer.OrdinalIgnoreCase);
            var bootTime = GetSystemBootTimeUtc();
            bool isAdmin = IsRunningAsAdmin() || IsRunningAsSystem();
            string winVerStr = Environment.OSVersion.VersionString;

            var diagnosticsList = new List<RegistryDiagnosticEntry>();

            int possibleCount = _tweakDatabase.Count;
            int applicableCount = 0;
            int recommendedCount = 0;
            int alreadyOptimizedCount = 0;
            int restartRequiredCount = 0;
            int pendingCount = 0;
            int notApplicableCount = 0;
            int unsupportedCount = 0;
            int blockedCount = 0;
            int attentionCount = 0;
            int failedCount = 0;

            foreach (var def in _tweakDatabase)
            {
                var dto = new RegistryTweakDto
                {
                    Id = def.Id,
                    Name = def.Name,
                    Description = def.Description,
                    Category = def.Category,
                    RiskLevel = def.RiskLevel,
                    DefaultValue = def.DefaultValueStr,
                    TargetValue = string.IsNullOrEmpty(def.DisplayTargetStr) ? def.TargetValueStr : def.DisplayTargetStr,
                    RootKey = def.RootKey,
                    SubKey = def.SubKey,
                    ValueName = def.ValueName,
                    ValueType = def.ValueKind == RegistryValueKind.DWord ? "REG_DWORD" : (def.ValueKind == RegistryValueKind.QWord ? "REG_QWORD" : "REG_SZ"),
                    SupportedOS = def.SupportedOS,
                    HardwarePrerequisites = def.HardwarePrerequisites,
                    RequiresAdmin = def.RequiresAdmin,
                    RequiresReboot = def.RequiresRestart,
                    RollbackSupported = true,
                    MissingBehavior = def.MissingBehavior.ToString(),
                    RegistryView = def.PreferredView.ToString(),
                    PermissionState = isAdmin ? "Elevated (Admin/Service)" : "Standard User"
                };

                // 1. OS & Build compatibility pre-flight
                if (!IsWindowsVersionSupported(def.SupportedOS))
                {
                    dto.Applicability = "UNSUPPORTED";
                    dto.Status = "UNSUPPORTED";
                    dto.CurrentValue = "UNSUPPORTED OS BUILD";
                    dto.ApplicabilityReason = $"Requires {def.SupportedOS}. Current OS: {winVerStr}";
                    dto.VerificationState = "NOT_APPLICABLE";
                    dto.ErrorCode = "0x80070032";
                    dto.DiagnosticsDetail = "OS Version requirement not satisfied.";
                    unsupportedCount++;
                    response.Tweaks.Add(dto);

                    diagnosticsList.Add(CreateDiagEntry(def, dto, "SKIPPED_UNSUPPORTED"));
                    continue;
                }

                // 2. Custom Applicability Check (Hardware / Services / Volumes / Subsystems)
                if (def.CustomApplicability != null)
                {
                    var (appOk, appReason) = def.CustomApplicability();
                    if (!appOk)
                    {
                        dto.Applicability = "NOT_APPLICABLE";
                        dto.Status = "NOT APPLICABLE";
                        dto.CurrentValue = "NOT APPLICABLE ON THIS PC";
                        dto.ApplicabilityReason = appReason;
                        dto.VerificationState = "NOT_APPLICABLE";
                        dto.ErrorCode = "0x00000000";
                        dto.DiagnosticsDetail = appReason;

                        notApplicableCount++;
                        response.Tweaks.Add(dto);
                        diagnosticsList.Add(CreateDiagEntry(def, dto, "SKIPPED_NOT_APPLICABLE"));
                        continue;
                    }
                    dto.ApplicabilityReason = appReason;
                }

                // 2.5 Authoritative Global Applied-State & Ownership Pre-flight
                string canTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", $"{def.RootKey}\\{def.SubKey}\\{def.ValueName}");
                var globalStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, def.TargetValueStr, out var observedVal);
                if (!string.IsNullOrWhiteSpace(observedVal))
                {
                    dto.CurrentValue = observedVal;
                }

                if (globalStatus == GlobalAppliedStatus.ALREADY_APPLIED || globalStatus == GlobalAppliedStatus.ALREADY_OPTIMAL || globalStatus == GlobalAppliedStatus.ACTIVE_PROFILE)
                {
                    dto.Applicability = "ALREADY_OPTIMAL";
                    dto.Status = "ALREADY OPTIMIZED";
                    dto.VerificationState = "VERIFIED";
                    dto.ApplicabilityReason = globalStatus == GlobalAppliedStatus.ALREADY_APPLIED
                        ? "Previously applied by ERROR OPTIMIZER and verified in live registry."
                        : "Current registry value already matches optimal target.";
                    dto.ErrorCode = "0x00000000";
                    dto.DiagnosticsDetail = "Target state verified optimal.";
                    alreadyOptimizedCount++;
                    applicableCount++;
                    response.Tweaks.Add(dto);
                    diagnosticsList.Add(CreateDiagEntry(def, dto, dto.Applicability));
                    continue;
                }
                else if (globalStatus == GlobalAppliedStatus.CONFIGURATION_CHANGED || globalStatus == GlobalAppliedStatus.EXTERNALLY_MODIFIED)
                {
                    dto.Status = "EXTERNALLY CHANGED";
                    dto.Applicability = "APPLICABLE";
                }

                // 3. Target Key Read-Only Pre-Flight (Do not open writable during plan)
                var (readKey, resolvedView, readErrCode, readErrDetail) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, false, def.PreferredView);
                dto.RegistryView = resolvedView.ToString();

                if (readKey == null)
                {
                    if (def.MissingBehavior == MissingValueBehavior.SafeToCreate)
                    {
                        dto.CurrentValue = "NOT CONFIGURED (Default)";
                        dto.Applicability = "APPLICABLE";
                        dto.Status = "RECOMMENDED";
                        dto.VerificationState = "UNVERIFIED";
                        dto.ErrorCode = "0x00000000";
                        dto.DiagnosticsDetail = "Target key absent but supported and safe to create.";
                        applicableCount++;
                        recommendedCount++;
                        pendingCount++;
                    }
                    else
                    {
                        dto.CurrentValue = "KEY NOT PRESENT";
                        dto.Applicability = "NOT_APPLICABLE";
                        dto.Status = "NOT APPLICABLE";
                        dto.ApplicabilityReason = $"Subsystem key does not exist on this machine: {readErrDetail}";
                        dto.VerificationState = "NOT_APPLICABLE";
                        dto.ErrorCode = readErrCode;
                        dto.DiagnosticsDetail = readErrDetail;
                        notApplicableCount++;
                    }

                    response.Tweaks.Add(dto);
                    diagnosticsList.Add(CreateDiagEntry(def, dto, dto.Applicability));
                    continue;
                }

                // 4. Live Value Evaluation & State Verification
                using (readKey)
                {
                    var liveVal = readKey.GetValue(def.ValueName);
                    
                    if (liveVal == null)
                    {
                        if (def.MissingBehavior == MissingValueBehavior.SafeToCreate)
                        {
                            dto.CurrentValue = "NOT SET (Default)";
                            dto.Applicability = "APPLICABLE";
                            dto.Status = "RECOMMENDED";
                            dto.VerificationState = "UNVERIFIED";
                            dto.ErrorCode = "0x00000000";
                            dto.DiagnosticsDetail = "Value not set; Windows uses system default.";
                            applicableCount++;
                            recommendedCount++;
                            pendingCount++;
                        }
                        else
                        {
                            dto.CurrentValue = "NOT CONFIGURED";
                            dto.Applicability = "NOT_APPLICABLE";
                            dto.Status = "NOT APPLICABLE";
                            dto.ApplicabilityReason = "Value does not exist on this subsystem.";
                            dto.VerificationState = "NOT_APPLICABLE";
                            dto.ErrorCode = "0x00000000";
                            dto.DiagnosticsDetail = "Subsystem value absent.";
                            notApplicableCount++;
                        }
                    }
                    else
                    {
                        var (matches, displayVal) = EvaluateValue(def, liveVal);
                        dto.CurrentValue = displayVal;

                        journalMap.TryGetValue(def.Id, out var journalEntry);
                        dto.BeforeValue = journalEntry?.BeforeValue ?? def.DefaultValueStr;

                        if (matches)
                        {
                            if (journalEntry != null && def.RequiresRestart && (journalEntry.ApplyState == "APPLIED_PENDING_REBOOT" || journalEntry.VerificationState == "PENDING_REBOOT"))
                            {
                                if (bootTime > journalEntry.Timestamp)
                                {
                                    journalEntry.VerificationState = "VERIFIED";
                                    journalEntry.ApplyState = "APPLIED";
                                    journalEntry.RebootVerifiedTimestamp = DateTime.UtcNow;
                                    _journal.SaveEntry(journalEntry);

                                    dto.Applicability = "ALREADY_OPTIMAL";
                                    dto.Status = "ALREADY OPTIMIZED";
                                    dto.VerificationState = "VERIFIED";
                                    dto.ApplicabilityReason = "Optimal value verified following system reboot.";
                                    dto.ErrorCode = "0x00000000";
                                    dto.DiagnosticsDetail = "Target state fully verified after reboot.";
                                    alreadyOptimizedCount++;
                                    applicableCount++;
                                }
                                else
                                {
                                    dto.Applicability = "APPLICABLE";
                                    dto.Status = "RESTART REQUIRED";
                                    dto.VerificationState = "PENDING_REBOOT";
                                    dto.ApplicabilityReason = "Value written to registry; system restart required for full kernel effect.";
                                    dto.ErrorCode = "0x00000000";
                                    dto.DiagnosticsDetail = "Target state written; reboot pending.";
                                    applicableCount++;
                                    restartRequiredCount++;
                                }
                            }
                            else
                            {
                                dto.Applicability = "ALREADY_OPTIMAL";
                                dto.Status = "ALREADY OPTIMIZED";
                                dto.VerificationState = "VERIFIED";
                                dto.ApplicabilityReason = "Current registry value already matches optimal target.";
                                dto.ErrorCode = "0x00000000";
                                dto.DiagnosticsDetail = "Target state fully verified.";
                                alreadyOptimizedCount++;
                                applicableCount++;
                            }
                        }
                        else
                        {
                            dto.Applicability = "APPLICABLE";
                            dto.Status = (journalEntry != null && journalEntry.VerificationState == "VERIFIED") ? "EXTERNALLY CHANGED" : "RECOMMENDED";
                            dto.VerificationState = "UNVERIFIED";
                            dto.ErrorCode = "0x00000000";
                            dto.DiagnosticsDetail = "Key is applicable and ready to be optimized.";
                            applicableCount++;
                            recommendedCount++;
                            pendingCount++;
                        }
                    }
                }

                response.Tweaks.Add(dto);
                diagnosticsList.Add(CreateDiagEntry(def, dto, dto.Applicability));
            }

            response.TotalCount = possibleCount;
            response.ApplicableCount = applicableCount;
            response.RecommendedCount = recommendedCount;
            response.AlreadyOptimizedCount = alreadyOptimizedCount;
            response.RestartRequiredCount = restartRequiredCount;
            response.PendingCount = pendingCount;
            response.NotApplicableCount = notApplicableCount;
            response.UnsupportedCount = unsupportedCount;
            response.BlockedCount = blockedCount;
            response.AttentionCount = attentionCount;
            response.FailedCount = failedCount;

            _journal.SaveDiagnostics(diagnosticsList);

            return response;
        }

        private static RegistryDiagnosticEntry CreateDiagEntry(RegistryTweakDefinition def, RegistryTweakDto dto, string result)
        {
            return new RegistryDiagnosticEntry
            {
                OptimizationId = def.Id,
                Name = def.Name,
                Hive = def.RootKey,
                SubKey = def.SubKey,
                ValueName = def.ValueName,
                RequiredType = dto.ValueType,
                CurrentValue = dto.CurrentValue,
                TargetValue = def.TargetValueStr,
                WindowsBuild = Environment.OSVersion.VersionString,
                RegistryView = dto.RegistryView,
                PermissionState = dto.PermissionState,
                ApplicabilityResult = dto.Applicability,
                Status = dto.Status,
                VerificationState = dto.VerificationState,
                ErrorCode = dto.ErrorCode,
                DiagnosticDetail = dto.DiagnosticsDetail,
                Timestamp = DateTime.UtcNow
            };
        }

        public async Task<(bool Success, string Message, string Status, int AppliedCount, int VerifiedCount, int RestartRequiredCount, int FailedCount)> ApplyRegistryTweaksAsync(List<string> tweakIds)
        {
            return await Task.Run(() =>
            {
                int appliedCount = 0;
                int verifiedCount = 0;
                int restartRequiredCount = 0;
                int failedCount = 0;
                int skippedCount = 0;
                int blockedCount = 0;

                // 1. Resolve candidates strictly according to applicability
                List<RegistryTweakDefinition> candidates;
                if (tweakIds == null || tweakIds.Count == 0)
                {
                    // Automatic Normal Mode: filter strictly to applicable tweaks
                    candidates = _tweakDatabase.Where(def =>
                    {
                        if (!IsWindowsVersionSupported(def.SupportedOS)) return false;
                        if (def.CustomApplicability != null && !def.CustomApplicability().IsApplicable) return false;
                        return true;
                    }).ToList();
                }
                else
                {
                    // Explicit Selection: only include requested IDs that are supported & applicable
                    candidates = _tweakDatabase.Where(def => tweakIds.Contains(def.Id, StringComparer.OrdinalIgnoreCase)).ToList();
                }

                foreach (var def in candidates)
                {
                    // Pre-flight check
                    if (!IsWindowsVersionSupported(def.SupportedOS))
                    {
                        skippedCount++;
                        continue;
                    }

                    if (def.CustomApplicability != null)
                    {
                        var (appOk, _) = def.CustomApplicability();
                        if (!appOk)
                        {
                            skippedCount++;
                            continue;
                        }
                    }

                    try
                    {
                        // 2. Pre-flight Read: Check if already optimal
                        var (readKey, resolvedView, _, _) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, false, def.PreferredView);
                        if (readKey == null && def.MissingBehavior != MissingValueBehavior.SafeToCreate)
                        {
                            // Not applicable on this machine (subsystem key missing)
                            skippedCount++;
                            continue;
                        }

                        if (readKey != null)
                        {
                            using (readKey)
                            {
                                var existingVal = readKey.GetValue(def.ValueName);
                                var (alreadyDone, currentDisplay) = EvaluateValue(def, existingVal);
                                if (alreadyDone)
                                {
                                    verifiedCount++;
                                    var optEntry = new RegistryTransactionEntry
                                    {
                                        OptimizationId = def.Id,
                                        Hive = def.RootKey,
                                        Path = def.SubKey,
                                        ValueName = def.ValueName,
                                        ValueType = def.ValueKind.ToString(),
                                        BeforeValue = currentDisplay,
                                        TargetValue = def.TargetValueStr,
                                        CurrentValue = currentDisplay,
                                        Applicability = "ALREADY_OPTIMAL",
                                        ApplyState = "ALREADY_OPTIMIZED",
                                        VerificationState = "VERIFIED",
                                        RegistryView = resolvedView.ToString(),
                                        ErrorCode = "0x00000000",
                                        Timestamp = DateTime.UtcNow
                                    };
                                    _journal.SaveEntry(optEntry);
                                    continue;
                                }
                            }
                        }

                        // 3. Open Writable Key with Explicit View
                        var (key, writeView, errCode, errDetail) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, true, def.PreferredView);
                        if (key == null)
                        {
                            if (errCode == "0x80070005")
                            {
                                blockedCount++;
                                var blockEntry = new RegistryTransactionEntry
                                {
                                    OptimizationId = def.Id,
                                    Hive = def.RootKey,
                                    Path = def.SubKey,
                                    ValueName = def.ValueName,
                                    ValueType = def.ValueKind.ToString(),
                                    TargetValue = def.TargetValueStr,
                                    ApplyState = "BLOCKED",
                                    VerificationState = "BLOCKED",
                                    RegistryView = writeView.ToString(),
                                    ErrorCode = errCode,
                                    ErrorMessage = $"Access Denied: {errDetail}",
                                    Timestamp = DateTime.UtcNow
                                };
                                _journal.SaveEntry(blockEntry);
                            }
                            else
                            {
                                failedCount++;
                                var failEntry = new RegistryTransactionEntry
                                {
                                    OptimizationId = def.Id,
                                    Hive = def.RootKey,
                                    Path = def.SubKey,
                                    ValueName = def.ValueName,
                                    ValueType = def.ValueKind.ToString(),
                                    TargetValue = def.TargetValueStr,
                                    ApplyState = "FAILED",
                                    VerificationState = "VERIFICATION_FAILED",
                                    RegistryView = writeView.ToString(),
                                    ErrorCode = errCode,
                                    ErrorMessage = errDetail,
                                    Timestamp = DateTime.UtcNow
                                };
                                _journal.SaveEntry(failEntry);
                            }
                            continue;
                        }

                        using (key)
                        {
                            // 4. Backup Original Value
                            var old = key.GetValue(def.ValueName);
                            var oldKind = old != null ? key.GetValueKind(def.ValueName) : def.ValueKind;
                            string oldStr = old == null ? "VALUE DID NOT EXIST" : BackupManager.FormatRegistryValue(old);
                            string ownerTag = $"AntiGravity.RegistryTweak.{def.Id}";
                            _backupManager.BackupRegistryValue(ownerTag, $"{def.RootKey}\\{def.SubKey}", def.ValueName, oldStr, oldKind.ToString(), def.TargetValueStr, $"Backup of {def.Name}");

                            // 5. Parse & Write Target Value with exact type
                            object valObj = ParseTargetValue(def.TargetValueStr, def.ValueKind);
                            key.SetValue(def.ValueName, valObj, def.ValueKind);
                            try { key.Flush(); } catch { }

                            if (def.Id.Equals("tweak.network.tcpnodelay", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    using var ifRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", true);
                                    if (ifRoot != null)
                                    {
                                        foreach (var ifName in ifRoot.GetSubKeyNames())
                                        {
                                            using var ifKey = ifRoot.OpenSubKey(ifName, true);
                                            if (ifKey != null)
                                            {
                                                var oldIfDelay = ifKey.GetValue("TCPNoDelay");
                                                var oldIfAck = ifKey.GetValue("TcpAckFrequency");
                                                _backupManager.BackupRegistryValue($"AntiGravity.RegistryTweak.{def.Id}.{ifName}", $@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifName}", "TCPNoDelay", oldIfDelay?.ToString() ?? "0", "DWord", "1", "Backup of interface TCPNoDelay");
                                                _backupManager.BackupRegistryValue($"AntiGravity.RegistryTweak.{def.Id}.{ifName}", $@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifName}", "TcpAckFrequency", oldIfAck?.ToString() ?? "2", "DWord", "1", "Backup of interface TcpAckFrequency");
                                                ifKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                                                ifKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                                try { ifKey.Flush(); } catch { }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            // 6. IMMEDIATE REAL READ-BACK VERIFICATION
                            var afterVal = key.GetValue(def.ValueName);
                            var (verified, displayAfter) = EvaluateValue(def, afterVal);

                            var entry = new RegistryTransactionEntry
                            {
                                OptimizationId = def.Id,
                                Hive = def.RootKey,
                                Path = def.SubKey,
                                ValueName = def.ValueName,
                                ValueType = def.ValueKind.ToString(),
                                BeforeValue = oldStr,
                                TargetValue = def.TargetValueStr,
                                CurrentValue = displayAfter,
                                Applicability = "APPLICABLE",
                                RequiresReboot = def.RequiresRestart,
                                RegistryView = writeView.ToString(),
                                ErrorCode = verified ? "0x00000000" : "0x80004005",
                                Timestamp = DateTime.UtcNow
                            };

                            if (verified)
                            {
                                appliedCount++;
                                if (def.RequiresRestart)
                                {
                                    entry.ApplyState = "APPLIED_PENDING_REBOOT";
                                    entry.VerificationState = "PENDING_REBOOT";
                                    restartRequiredCount++;
                                }
                                else
                                {
                                    entry.ApplyState = "APPLIED";
                                    entry.VerificationState = "VERIFIED";
                                    verifiedCount++;
                                }
                            }
                            else
                            {
                                entry.ApplyState = "FAILED";
                                entry.VerificationState = "VERIFICATION_FAILED";
                                entry.ErrorMessage = $"Read-back mismatch: Expected '{def.TargetValueStr}', got '{displayAfter}'.";
                                failedCount++;
                            }

                            _journal.SaveEntry(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        var errEntry = new RegistryTransactionEntry
                        {
                            OptimizationId = def.Id,
                            Hive = def.RootKey,
                            Path = def.SubKey,
                            ValueName = def.ValueName,
                            TargetValue = def.TargetValueStr,
                            ApplyState = "FAILED",
                            VerificationState = "VERIFICATION_FAILED",
                            ErrorCode = "0x80004005",
                            ErrorMessage = ex.Message,
                            Timestamp = DateTime.UtcNow
                        };
                        _journal.SaveEntry(errEntry);
                    }
                }

                bool isSuccess = failedCount == 0;
                string status = failedCount > 0 ? (appliedCount > 0 ? "PARTIAL_SUCCESS" : "FAILED")
                                                : (restartRequiredCount > 0 ? "RESTART_REQUIRED" : "SUCCESS");

                string msg = isSuccess
                    ? (appliedCount == 0 && verifiedCount > 0 
                        ? $"{verifiedCount} ALREADY OPTIMAL • 0 CHANGES NEEDED" 
                        : (restartRequiredCount > 0 
                            ? $"{appliedCount} APPLIED & VERIFIED ({restartRequiredCount} RESTART REQUIRED)" 
                            : $"{appliedCount} APPLIED & VERIFIED"))
                    : $"{appliedCount} APPLIED • {failedCount} FAILED";

                return (isSuccess, msg, status, appliedCount, verifiedCount, restartRequiredCount, failedCount);
            });
        }

        public async Task<RegistryTweakExecutionResponse> ExecuteRegistryTweaksAsync(List<string> tweakIds)
        {
            return await Task.Run(() =>
            {
                var tweaksToApply = (tweakIds == null || tweakIds.Count == 0)
                    ? _tweakDatabase
                    : _tweakDatabase.Where(t => tweakIds.Contains(t.Id, StringComparer.OrdinalIgnoreCase)).ToList();

                string regTxId = $"reg-tx-{Guid.NewGuid():N}";
                try
                {
                    var snapshot = _backupManager.CreateApplicationSnapshot("Registry Intelligence Backup", $"Backup before applying {tweaksToApply.Count} registry tweaks");
                    regTxId = snapshot.TransactionId;
                }
                catch { }

                var response = new RegistryTweakExecutionResponse
                {
                    RequestedCount = tweaksToApply.Count,
                    TransactionId = regTxId
                };

                foreach (var def in tweaksToApply)
                {
                    var op = new RegistryTweakOperationResult
                    {
                        Id = def.Id,
                        Title = def.Name,
                        TargetValue = def.TargetValueStr,
                        Hive = def.RootKey,
                        KeyPath = def.SubKey,
                        ValueName = def.ValueName,
                        RequestedValue = def.TargetValueStr
                    };

                    // Pre-flight OS & Custom Applicability checks
                    if (!IsWindowsVersionSupported(def.SupportedOS))
                    {
                        op.Skipped = true;
                        op.Status = "skipped";
                        op.State = "UNSUPPORTED";
                        op.Reason = $"Requires {def.SupportedOS}.";
                        op.Detail = op.Reason;
                        op.VerificationResult = "SKIPPED";
                        response.SkippedCount++;
                        response.Operations.Add(op);
                        continue;
                    }

                    if (def.CustomApplicability != null)
                    {
                        var (appOk, appReason) = def.CustomApplicability();
                        if (!appOk)
                        {
                            op.Skipped = true;
                            op.Status = "skipped";
                            op.State = "NOT_APPLICABLE";
                            op.Reason = appReason;
                            op.Detail = appReason;
                            op.VerificationResult = "SKIPPED";
                            response.SkippedCount++;
                            response.Operations.Add(op);
                            continue;
                        }
                    }

                    // Authoritative Global Ownership & Live State Execution Gating
                    string canTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", $"{def.RootKey}\\{def.SubKey}\\{def.ValueName}");
                    if (!GlobalAppliedStateManager.Instance.CanExecute("RegistryIntelligence", canTarget, def.TargetValueStr, out string rejReason))
                    {
                        op.AlreadyDesired = true;
                        op.ReadbackVerified = true;
                        op.WriteAttempted = false;
                        op.WriteSucceeded = false;
                        op.Status = "succeeded";
                        op.State = "ALREADY_OPTIMAL";
                        op.VerificationResult = "ALREADY_OPTIMAL";
                        op.Verification = "Already configured to optimal target value";
                        op.Reason = rejReason;
                        op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Value: {def.ValueName} | State: ALREADY OPTIMIZED ({rejReason})";
                        response.AttemptedCount++;
                        response.AlreadyDesiredCount++;
                        response.VerifiedCount++;
                        response.Operations.Add(op);
                        continue;
                    }

                    try
                    {
                        var (readKey, resolvedView, _, _) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, false, def.PreferredView);
                        if (readKey == null && def.MissingBehavior != MissingValueBehavior.SafeToCreate)
                        {
                            op.Skipped = true;
                            op.Status = "skipped";
                            op.State = "SKIPPED";
                            op.Reason = "Subsystem key not present on this machine.";
                            op.WriteAttempted = false;
                            op.WriteSucceeded = false;
                            op.VerificationResult = "SKIPPED";
                            op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Key not present on this machine";
                            response.SkippedCount++;
                            response.Operations.Add(op);
                            continue;
                        }

                        if (readKey != null)
                        {
                            using (readKey)
                            {
                                var existingVal = readKey.GetValue(def.ValueName);
                                var (alreadyDone, currentDisplay) = EvaluateValue(def, existingVal);
                                op.CurrentValue = currentDisplay;
                                op.OriginalValue = currentDisplay;
                                op.ReadbackValue = currentDisplay;

                                if (alreadyDone)
                                {
                                    op.AlreadyDesired = true;
                                    op.ReadbackVerified = true;
                                    op.WriteAttempted = false;
                                    op.WriteSucceeded = false;
                                    op.Status = "succeeded";
                                    op.State = "ALREADY_OPTIMAL";
                                    op.VerificationResult = "ALREADY_OPTIMAL";
                                    op.Verification = $"Already configured to optimal target value ({currentDisplay})";
                                    op.Reason = $"Value already optimal: current value '{currentDisplay}' matches target '{def.TargetValueStr}'";
                                    op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Value: {def.ValueName} | Readback: {currentDisplay} | State: ALREADY OPTIMIZED";
                                    response.AttemptedCount++;
                                    response.AlreadyDesiredCount++;
                                    response.VerifiedCount++;

                                    var optEntry = new RegistryTransactionEntry
                                    {
                                        OptimizationId = def.Id,
                                        Hive = def.RootKey,
                                        Path = def.SubKey,
                                        ValueName = def.ValueName,
                                        ValueType = def.ValueKind.ToString(),
                                        BeforeValue = currentDisplay,
                                        TargetValue = def.TargetValueStr,
                                        CurrentValue = currentDisplay,
                                        Applicability = "ALREADY_OPTIMAL",
                                        ApplyState = "ALREADY_OPTIMIZED",
                                        VerificationState = "VERIFIED",
                                        RegistryView = resolvedView.ToString(),
                                        ErrorCode = "0x00000000",
                                        Timestamp = DateTime.UtcNow
                                    };
                                    _journal.SaveEntry(optEntry);
                                    response.Operations.Add(op);
                                    continue;
                                }
                            }
                        }

                        var (key, writeView, errCode, errDetail) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, true, def.PreferredView);
                        if (key == null)
                        {
                            response.AttemptedCount++;
                            response.FailedCount++;
                            op.Failed = true;
                            op.Status = "failed";
                            op.State = errCode == "0x80070005" ? "BLOCKED" : "FAILED";
                            op.FailureReason = errCode == "0x80070005" ? $"Access Denied: {errDetail}" : errDetail;
                            op.Reason = op.FailureReason;
                            op.WriteAttempted = true;
                            op.WriteSucceeded = false;
                            op.VerificationResult = "VERIFICATION_FAILED";
                            op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Value: {def.ValueName} | Failed to open writable key: {op.FailureReason}";

                            var failEntry = new RegistryTransactionEntry
                            {
                                OptimizationId = def.Id,
                                Hive = def.RootKey,
                                Path = def.SubKey,
                                ValueName = def.ValueName,
                                ValueType = def.ValueKind.ToString(),
                                TargetValue = def.TargetValueStr,
                                ApplyState = op.State,
                                VerificationState = "VERIFICATION_FAILED",
                                RegistryView = writeView.ToString(),
                                ErrorCode = errCode,
                                ErrorMessage = errDetail,
                                Timestamp = DateTime.UtcNow
                            };
                            _journal.SaveEntry(failEntry);
                            response.Operations.Add(op);
                            continue;
                        }

                        using (key)
                        {
                            var old = key.GetValue(def.ValueName);
                            var oldKind = old != null ? key.GetValueKind(def.ValueName) : def.ValueKind;
                            string oldStr = old == null ? "VALUE DID NOT EXIST" : BackupManager.FormatRegistryValue(old);
                            op.OriginalValue = oldStr;
                            string ownerTag = $"AntiGravity.RegistryTweak.{def.Id}";
                            _backupManager.BackupRegistryValue(ownerTag, $"{def.RootKey}\\{def.SubKey}", def.ValueName, oldStr, oldKind.ToString(), def.TargetValueStr, $"Backup of {def.Name}");

                            op.WriteAttempted = true;
                            object valObj = ParseTargetValue(def.TargetValueStr, def.ValueKind);
                            key.SetValue(def.ValueName, valObj, def.ValueKind);
                            try { key.Flush(); } catch { }

                            if (def.Id.Equals("tweak.network.tcpnodelay", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    using var ifRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", true);
                                    if (ifRoot != null)
                                    {
                                        foreach (var ifName in ifRoot.GetSubKeyNames())
                                        {
                                            using var ifKey = ifRoot.OpenSubKey(ifName, true);
                                            if (ifKey != null)
                                            {
                                                var oldIfDelay = ifKey.GetValue("TCPNoDelay");
                                                var oldIfAck = ifKey.GetValue("TcpAckFrequency");
                                                _backupManager.BackupRegistryValue($"AntiGravity.RegistryTweak.{def.Id}.{ifName}", $@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifName}", "TCPNoDelay", oldIfDelay?.ToString() ?? "0", "DWord", "1", "Backup of interface TCPNoDelay");
                                                _backupManager.BackupRegistryValue($"AntiGravity.RegistryTweak.{def.Id}.{ifName}", $@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifName}", "TcpAckFrequency", oldIfAck?.ToString() ?? "2", "DWord", "1", "Backup of interface TcpAckFrequency");
                                                ifKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                                                ifKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                                try { ifKey.Flush(); } catch { }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            var afterVal = key.GetValue(def.ValueName);
                            var (verified, displayAfter) = EvaluateValue(def, afterVal);
                            op.CurrentValue = displayAfter;
                            op.ReadbackValue = displayAfter;
                            op.WriteSucceeded = verified;

                            var entry = new RegistryTransactionEntry
                            {
                                OptimizationId = def.Id,
                                Hive = def.RootKey,
                                Path = def.SubKey,
                                ValueName = def.ValueName,
                                ValueType = def.ValueKind.ToString(),
                                BeforeValue = oldStr,
                                TargetValue = def.TargetValueStr,
                                CurrentValue = displayAfter,
                                Applicability = "APPLICABLE",
                                RequiresReboot = def.RequiresRestart,
                                RegistryView = writeView.ToString(),
                                ErrorCode = verified ? "0x00000000" : "0x80004005",
                                Timestamp = DateTime.UtcNow
                            };

                            response.AttemptedCount++;
                            if (verified)
                            {
                                response.AppliedCount++;
                                op.Applied = true;
                                op.ReadbackVerified = true;
                                op.RollbackAvailable = true;
                                op.Status = "succeeded";
                                op.VerificationResult = "VERIFIED";
                                op.Verification = $"Confirmed in registry via readback: {displayAfter}";
                                op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Value: {def.ValueName} | Original: {oldStr} | Applied: {def.TargetValueStr} | Readback: {displayAfter}";

                                if (def.RequiresRestart)
                                {
                                    entry.ApplyState = "APPLIED_PENDING_REBOOT";
                                    entry.VerificationState = "PENDING_REBOOT";
                                    response.RestartRequiredCount++;
                                    op.State = "APPLIED_PENDING_REBOOT";
                                    op.Reason = "Optimization applied; restart required to activate";
                                }
                                else
                                {
                                    entry.ApplyState = "APPLIED";
                                    entry.VerificationState = "VERIFIED";
                                    response.VerifiedCount++;
                                    op.State = "APPLIED";
                                }

                                GlobalAppliedStateManager.Instance.RecordApplied(
                                    canTarget,
                                    def.Id,
                                    "RegistryIntelligence",
                                    "RegistryIntelligence",
                                    oldStr,
                                    def.TargetValueStr,
                                    verified: true,
                                    rollbackAvailable: true,
                                    regTxId,
                                    "SetRegistryValue");
                            }
                            else
                            {
                                entry.ApplyState = "FAILED";
                                entry.VerificationState = "VERIFICATION_FAILED";
                                entry.ErrorMessage = $"Read-back mismatch: Expected '{def.TargetValueStr}', got '{displayAfter}'.";
                                response.FailedCount++;
                                op.Failed = true;
                                op.Status = "failed";
                                op.State = "FAILED";
                                op.VerificationResult = "VERIFICATION_FAILED";
                                op.FailureReason = entry.ErrorMessage;
                                op.Reason = entry.ErrorMessage;
                                op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Value: {def.ValueName} | Original: {oldStr} | Expected: {def.TargetValueStr} | Got: {displayAfter}";
                            }

                            _journal.SaveEntry(entry);
                            response.Operations.Add(op);
                        }
                    }
                    catch (Exception ex)
                    {
                        response.AttemptedCount++;
                        response.FailedCount++;
                        op.Failed = true;
                        op.Status = "failed";
                        op.State = "FAILED";
                        op.VerificationResult = "VERIFICATION_FAILED";
                        op.FailureReason = ex.Message;
                        op.Reason = ex.Message;
                        op.Detail = $"Hive: {def.RootKey} | Key: {def.SubKey} | Exception: {ex.Message}";
                        response.Operations.Add(op);
                    }
                }

                if (response.Operations.Count == 0 || (response.AppliedCount == 0 && response.AlreadyDesiredCount == 0))
                {
                    response.Success = false;
                    response.Status = "NO_ELIGIBLE_OPERATIONS";
                    response.Message = "NO ELIGIBLE REGISTRY OPTIMIZATIONS";
                }
                else
                {
                    response.Success = response.FailedCount == 0;
                    response.Status = response.FailedCount > 0
                        ? (response.AppliedCount > 0 ? "PARTIAL_SUCCESS" : "FAILED")
                        : (response.RestartRequiredCount > 0 ? "RESTART_REQUIRED" : "SUCCESS");

                    response.Message = response.Success
                        ? (response.AppliedCount == 0
                            ? $"{response.AlreadyDesiredCount} ALREADY OPTIMAL • 0 NEW WRITES REQUIRED"
                            : (response.RestartRequiredCount > 0
                                ? $"{response.AppliedCount} APPLIED & VERIFIED • {response.AlreadyDesiredCount} ALREADY OPTIMAL ({response.RestartRequiredCount} RESTART REQUIRED)"
                                : (response.AlreadyDesiredCount > 0
                                    ? $"{response.AppliedCount} APPLIED & VERIFIED • {response.AlreadyDesiredCount} ALREADY OPTIMAL"
                                    : $"{response.AppliedCount} APPLIED & VERIFIED")))
                        : $"{response.AppliedCount} APPLIED • {response.FailedCount} FAILED";
                }

                return response;
            });
        }

        public async Task<(bool Success, string Message, string Status, int RestoredCount, int FailedCount)> RestoreRegistryTweaksAsync(List<string> tweakIds)
        {
            return await Task.Run(() =>
            {
                var tweaksToRestore = (tweakIds == null || tweakIds.Count == 0)
                    ? _tweakDatabase
                    : _tweakDatabase.Where(t => tweakIds.Contains(t.Id, StringComparer.OrdinalIgnoreCase)).ToList();

                int restoredCount = 0;
                int failedCount = 0;
                var journalMap = _journal.LoadEntries().ToDictionary(x => x.OptimizationId, x => x, StringComparer.OrdinalIgnoreCase);

                foreach (var def in tweaksToRestore)
                {
                    try
                    {
                        string ownerTag = $"AntiGravity.RegistryTweak.{def.Id}";
                        bool backupRestored = _backupManager.RestoreByOwner(ownerTag);

                        if (!backupRestored && journalMap.TryGetValue(def.Id, out var entry))
                        {
                            var (key, _, _, _) = OpenTargetKeyWithPreflight(def.RootKey, def.SubKey, true, def.PreferredView);
                            if (key != null)
                            {
                                using (key)
                                {
                                    if (entry.BeforeValue == "VALUE DID NOT EXIST" || entry.BeforeValue == "NOT CONFIGURED" || string.IsNullOrEmpty(entry.BeforeValue))
                                    {
                                        try { key.DeleteValue(def.ValueName, false); } catch { }
                                    }
                                    else
                                    {
                                        if (def.ValueKind == RegistryValueKind.DWord && int.TryParse(entry.BeforeValue, out int iVal))
                                            key.SetValue(def.ValueName, iVal, RegistryValueKind.DWord);
                                        else
                                            key.SetValue(def.ValueName, entry.BeforeValue, def.ValueKind);
                                    }
                                    backupRestored = true;
                                }
                            }
                        }

                        if (backupRestored)
                        {
                            restoredCount++;
                            if (journalMap.TryGetValue(def.Id, out var existingEntry))
                            {
                                existingEntry.ApplyState = "ROLLED_BACK";
                                existingEntry.VerificationState = "UNVERIFIED";
                                _journal.SaveEntry(existingEntry);
                            }
                        }
                    }
                    catch
                    {
                        failedCount++;
                    }
                }

                return (true, $"Restored {restoredCount} registry tweaks to original state.", "SUCCESS", restoredCount, failedCount);
            });
        }
    }
}

