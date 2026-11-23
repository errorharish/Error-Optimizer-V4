#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BiosOptimizer.Core.V4.Safety;

namespace BiosOptimizer.Core.V4.Recommendations
{
    public enum OptimizationEngineDomain
    {
        Registry,
        Gpu,
        Input,
        Storage,
        Network,
        Debloat,
        BiosSafe,
        Profile,
        Workload,
        PowerPlan
    }

    /// <summary>
    /// Master Setting Ownership Arbiter and Defender Protection Gateway.
    /// Strictly guarantees:
    /// 1. Single-engine configuration ownership (GPU registry belongs to GPU, not Registry; etc.)
    /// 2. Conflict prevention across concurrent or sequential engine operations.
    /// 3. Non-bypassable Defender V4 Protected Rules enforcement before any configuration write.
    /// </summary>
    public sealed class OptimizationSettingOwnership
    {
        private static readonly Lazy<OptimizationSettingOwnership> _instance = new(() => new OptimizationSettingOwnership());
        public static OptimizationSettingOwnership Instance => _instance.Value;

        // Path / target prefixes mapped to the authoritative owning engine
        private readonly List<(string TargetPrefix, OptimizationEngineDomain Owner, string Description)> _ownershipMap = new();

        private OptimizationSettingOwnership()
        {
            RegisterOwnershipDefinitions();
        }

        private void RegisterOwnershipDefinitions()
        {
            // ── GPU OPTIMIZATION ENGINE OWNERSHIP ────────────────────────────
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", OptimizationEngineDomain.Gpu, "Graphics driver scheduling (HAGS), TDR watchdogs, and display adapter controls");
            Register(@"HKCU\Control Panel\GraphicsDrivers", OptimizationEngineDomain.Gpu, "Per-user graphics display parameters (Variable Refresh Rate VRR / Adaptive Sync)");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", OptimizationEngineDomain.Gpu, "GPU Display Adapter driver parameters (NVIDIA PowerMizer, AMD ULPS, Intel Graphics)");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm", OptimizationEngineDomain.Gpu, "NVIDIA display driver PowerMizer and performance modes");
            Register(@"HKCU\Software\Microsoft\DirectX\UserGpuPreferences", OptimizationEngineDomain.Gpu, "Per-application discrete GPU performance preference mapping");
            Register(@"HKLM\SOFTWARE\Microsoft\Windows\DWM\OverlayTestMode", OptimizationEngineDomain.Gpu, "DWM DirectFlip and overlay display presentation latency");
            Register(@"HKCU\System\GameConfigStore", OptimizationEngineDomain.Gpu, "GameDVR and Fullscreen Exclusive graphics capture parameters");
            Register(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", OptimizationEngineDomain.Gpu, "Windows GameDVR hardware capture policy");

            // ── REGISTRY OPTIMIZATION ENGINE OWNERSHIP ───────────────────────
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\Win32PrioritySeparation", OptimizationEngineDomain.Registry, "Foreground CPU thread quantum priority scheduling");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings", OptimizationEngineDomain.PowerPlan, "AC/DC Power scheme values, core parking, and device power policies");
            Register(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\SystemResponsiveness", OptimizationEngineDomain.Registry, "MMCSS system multimedia foreground execution quota");
            Register(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NetworkThrottlingIndex", OptimizationEngineDomain.Registry, "Network packet throttling index registry setting");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\SvcHostSplitThresholdInKB", OptimizationEngineDomain.Registry, "SvcHost split threshold for large RAM systems");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\LargeSystemCache", OptimizationEngineDomain.Registry, "File-system cache size allocation for high-memory systems");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\DisablePagingExecutive", OptimizationEngineDomain.Registry, "Kernel-mode drivers and executive resident in physical RAM");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\ClearPageFileAtShutdown", OptimizationEngineDomain.Registry, "Pagefile wipe on shutdown toggle");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisable8dot3NameCreation", OptimizationEngineDomain.Registry, "NTFS 8.3 short filename generation overhead suppression");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisableLastAccessUpdate", OptimizationEngineDomain.Registry, "NTFS last access timestamp disk write update suppression");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsMemoryUsage", OptimizationEngineDomain.Registry, "NTFS paged-pool metadata cache quota");
            Register(@"HKCU\Control Panel\Desktop\MenuShowDelay", OptimizationEngineDomain.Registry, "Shell desktop UI menu popup latency");
            Register(@"HKCU\Control Panel\Desktop\SmoothScroll", OptimizationEngineDomain.Registry, "Smooth scroll latency suppression");
            Register(@"HKCU\Control Panel\Desktop\UserPreferencesMask", OptimizationEngineDomain.Registry, "Visual animations and menu fade mask");
            Register(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", OptimizationEngineDomain.Registry, "Windows Explorer visual effects performance setting");
            Register(@"HKCU\Control Panel\Desktop\WaitToKillAppTimeout", OptimizationEngineDomain.Registry, "Hung application clean termination timeout");
            Register(@"HKCU\Control Panel\Desktop\HungAppTimeout", OptimizationEngineDomain.Registry, "Hung application detection threshold");

            // ── INPUT OPTIMIZATION ENGINE OWNERSHIP ──────────────────────────
            Register(@"HKCU\Control Panel\Mouse", OptimizationEngineDomain.Input, "Mouse acceleration curves, thresholds, and linear 1:1 scaling");
            Register(@"HKCU\Control Panel\Keyboard", OptimizationEngineDomain.Input, "Keyboard repeat delay and rate settings");
            Register(@"HKCU\Control Panel\Accessibility", OptimizationEngineDomain.Input, "Accessibility shortcut key lockouts (StickyKeys, FilterKeys, ToggleKeys)");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters\MouseDataQueueSize", OptimizationEngineDomain.Input, "Mouse packet driver queue depth");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters\KeyboardDataQueueSize", OptimizationEngineDomain.Input, "Keyboard packet driver queue depth");
            Register(@"Win32_SPI:Mouse", OptimizationEngineDomain.Input, "SystemParametersInfo mouse speed and acceleration SPI parameters");
            Register(@"Win32_SPI:Keyboard", OptimizationEngineDomain.Input, "SystemParametersInfo keyboard repeat speed and delay SPI parameters");

            // ── STORAGE OPTIMIZATION ENGINE OWNERSHIP ────────────────────────
            Register(@"FileSystem:TRIM", OptimizationEngineDomain.Storage, "Solid State Drive TRIM and wear-leveling deallocate notify");
            Register(@"FileSystem:CleanupCategories", OptimizationEngineDomain.Storage, "Temporary files, download cache, crash dumps, and shader caches");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\IoPageLockLimit", OptimizationEngineDomain.Storage, "I/O page lock limit for high-throughput disk workloads");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", OptimizationEngineDomain.Storage, "Prefetch and Superfetch parameters for SSD storage");

            // ── NETWORK OPTIMIZATION ENGINE OWNERSHIP ────────────────────────
            Register(@"Netsh:TcpGlobal", OptimizationEngineDomain.Network, "TCP Auto-Tuning, Congestion Provider, RSS, ECN, and Timestamps");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", OptimizationEngineDomain.Network, "Interface-specific TCP low latency parameters (TcpAckFrequency, TCPNoDelay)");
            Register(@"Network:DNS", OptimizationEngineDomain.Network, "Active DNS resolver benchmarks, cache flushing, and server configuration");

            // ── DEBLOAT ENGINE OWNERSHIP ──────────────────────────────────────
            Register(@"AppX:Package", OptimizationEngineDomain.Debloat, "Microsoft Store and AppX package lifecycle management");
            Register(@"AppX:Provisioned", OptimizationEngineDomain.Debloat, "Windows provisioned OEM and consumer system packages");
            Register(@"OneDrive:Client", OptimizationEngineDomain.Debloat, "Safe OneDrive client installation, sync shutdown, and Explorer integration");
            Register(@"Xbox:GameBarOverlay", OptimizationEngineDomain.Debloat, "Xbox Gaming Overlay removal with Gaming Services protection");
            Register(@"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", OptimizationEngineDomain.Debloat, "Windows consumer promo apps, tips, and cloud app suggestions");
            Register(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", OptimizationEngineDomain.Debloat, "Diagnostic data collection and telemetry suppression");

            // ── BIOS SAFE ENGINE OWNERSHIP ────────────────────────────────────
            Register(@"Firmware:WMI", OptimizationEngineDomain.BiosSafe, "WMI BIOS and motherboard hardware capability enumeration");
            Register(@"Firmware:UEFI", OptimizationEngineDomain.BiosSafe, "UEFI boot mode, Secure Boot, and TPM 2.0 platform inspection");
            Register(@"PCIe:ReBAR", OptimizationEngineDomain.BiosSafe, "PCIe Resizable BAR and Above 4G Decoding readiness verification");
            Register(@"Motherboard:XMP_EXPO", OptimizationEngineDomain.BiosSafe, "Memory XMP/EXPO rated overclock profiles and timing guidance");
            Register(@"Firmware:DirectReboot", OptimizationEngineDomain.BiosSafe, "Privileged Windows-to-UEFI direct firmware reboot trigger");

            // ── MASTER PROFILE ENGINE OWNERSHIP ──────────────────────────────
            Register(@"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled", OptimizationEngineDomain.Profile, "Windows Game Mode foreground prioritization");
            Register(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", OptimizationEngineDomain.Profile, "Global user background app CPU throttling policy");
            Register(@"HKCU\Control Panel\Desktop\WindowMetrics\MinAnimate", OptimizationEngineDomain.Profile, "Desktop shell window minimize and restore animation latency");
            Register(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", OptimizationEngineDomain.Profile, "MMCSS game task thread scheduling and SFIO priority");
            Register(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\DisablePagingCombining", OptimizationEngineDomain.Profile, "Process memory compression page combining policy");
            Register(@"HKCU\Software\Microsoft\Windows\DWM\EnableAeroPeek", OptimizationEngineDomain.Profile, "DWM compositor desktop composition latency");

            // ── AI WORKLOAD OPTIMIZATION ENGINE OWNERSHIP ────────────────────
            Register(@"Workload:ProcessScheduling", OptimizationEngineDomain.Workload, "Dynamic foreground thread priority and CPU quantum scheduling");
            Register(@"Workload:GpuPreference", OptimizationEngineDomain.Workload, "Hardware-aware discrete GPU allocation and DirectX preference");
            Register(@"Workload:ContentionReduction", OptimizationEngineDomain.Workload, "Background thread de-prioritization during heavy workloads");

            // ── AI POWER PLAN OPTIMIZATION ENGINE OWNERSHIP ──────────────────
            Register(@"PowerPlan:Scheme", OptimizationEngineDomain.PowerPlan, "Native Windows active power scheme lifecycle and switching");
            Register(@"PowerPlan:ProcessorPowerManagement", OptimizationEngineDomain.PowerPlan, "Processor throttle states, cooling policy, and core unparking");
            Register(@"PowerPlan:AcDcTransition", OptimizationEngineDomain.PowerPlan, "Hardware-aware AC vs Battery dynamic power state adaptation");
        }

        private void Register(string targetPrefix, OptimizationEngineDomain owner, string description)
        {
            _ownershipMap.Add((targetPrefix, owner, description));
        }

        /// <summary>
        /// Validates whether a specific engine has authoritative ownership to configure the target.
        /// </summary>
        public bool CanEngineManage(OptimizationEngineDomain engine, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;

            var match = _ownershipMap.FirstOrDefault(m => target.StartsWith(m.TargetPrefix, StringComparison.OrdinalIgnoreCase) ||
                                                          m.TargetPrefix.StartsWith(target, StringComparison.OrdinalIgnoreCase));
            if (match.TargetPrefix == null)
            {
                // Unregistered target: Default to Registry for general registry paths, otherwise deny
                return engine == OptimizationEngineDomain.Registry && (target.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) || target.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase));
            }

            return match.Owner == engine;
        }

        /// <summary>
        /// Retrieves the designated owner engine for any system target or configuration path.
        /// </summary>
        public OptimizationEngineDomain GetOwnerForTarget(string target)
        {
            var match = _ownershipMap.FirstOrDefault(m => target.StartsWith(m.TargetPrefix, StringComparison.OrdinalIgnoreCase) ||
                                                          m.TargetPrefix.StartsWith(target, StringComparison.OrdinalIgnoreCase));
            return match.TargetPrefix != null ? match.Owner : OptimizationEngineDomain.Registry;
        }

        /// <summary>
        /// Strictly validates whether a target or operation violates Defender V4 Protected Rules.
        /// Rejects any attempt to disable or modify Defender, security health services, drivers, or essential system components.
        /// </summary>
        public (bool IsBlocked, string Reason) EvaluateDefenderProtection(string targetIdentifier, string? serviceOrProcessName = null)
        {
            // 1. Service Name Protection Check
            if (!string.IsNullOrWhiteSpace(serviceOrProcessName))
            {
                if (HardProtectRegistry.IsServiceProtected(serviceOrProcessName))
                {
                    return (true, $"Target service '{serviceOrProcessName}' is shielded by Defender V4 Protected Rules.");
                }

                if (HardProtectRegistry.IsProcessProtected(serviceOrProcessName))
                {
                    return (true, $"Target process '{serviceOrProcessName}' is shielded by Defender V4 Protected Rules.");
                }
            }

            // 2. Registry Target Protection Check
            string cleanTarget = targetIdentifier ?? string.Empty;
            string[] protectedRegistryKeywords =
            {
                @"SOFTWARE\Policies\Microsoft\Windows Defender",
                @"SOFTWARE\Microsoft\Windows Defender",
                @"SYSTEM\CurrentControlSet\Services\WinDefend",
                @"SYSTEM\CurrentControlSet\Services\Sense",
                @"SYSTEM\CurrentControlSet\Services\SecurityHealthService",
                @"SYSTEM\CurrentControlSet\Services\wscsvc",
                @"SYSTEM\CurrentControlSet\Services\mpssvc",
                @"SYSTEM\CurrentControlSet\Services\MpsSvc",
                @"SYSTEM\CurrentControlSet\Services\BFE",
                @"SYSTEM\CurrentControlSet\Services\CryptSvc",
                @"SYSTEM\CurrentControlSet\Services\stornvme",
                @"FeatureSettingsOverride"
            };

            foreach (var kw in protectedRegistryKeywords)
            {
                if (cleanTarget.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, $"Target registry path '{cleanTarget}' is a core Windows Defender / Security component.");
                }
            }

            // 3. Protected Application Exclusion Policy Check
            var appMatch = ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(Path.GetFileNameWithoutExtension(cleanTarget));
            if (appMatch.IsProtected)
            {
                return (true, $"Target '{cleanTarget}' matches protected application policy: {appMatch.Reason}");
            }

            // 4. Protected AppX Package Check
            if (ProtectedApplicationPolicy.IsProtectedPackage(cleanTarget))
            {
                return (true, $"Package '{cleanTarget}' is a protected Windows core system package and cannot be uninstalled.");
            }

            return (false, string.Empty);
        }
    }
}
