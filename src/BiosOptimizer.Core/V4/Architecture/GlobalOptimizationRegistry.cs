#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BiosOptimizer.Core.V4.Architecture
{
    public sealed class CanonicalOptimizationMetadata
    {
        public string CanonicalId { get; set; } = string.Empty;
        public string OwningCategory { get; set; } = string.Empty;
        public string CanonicalTarget { get; set; } = string.Empty;
        public string Action { get; set; } = "SetRegistryValue";
        public string OptimalValue { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Risk { get; set; } = "Low";
    }

    /// <summary>
    /// Master Authority for Optimization Catalog Ownership in ERROR OPTIMIZER V4.
    /// Strictly guarantees that every system target has exactly ONE authoritative owning category,
    /// eliminating cross-page duplicate exposure, conflicting modifications, and renamed/disguised duplicates.
    /// Backed by GlobalSystemSettingRegistry for unified optimal configuration authority.
    /// </summary>
    public sealed class GlobalOptimizationRegistry
    {
        private static readonly Lazy<GlobalOptimizationRegistry> _instance = new(() => new GlobalOptimizationRegistry());
        public static GlobalOptimizationRegistry Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, CanonicalOptimizationMetadata> _canonicalTargets = new(StringComparer.OrdinalIgnoreCase);

        public GlobalOptimizationRegistry()
        {
            InitializeAuthoritativeAssignments();
        }

        public static string NormalizeTarget(string action, string rawTarget)
        {
            return GlobalSystemSettingRegistry.NormalizeTarget(action, rawTarget);
        }

        public string GetAuthoritativeOwner(string canonicalTarget)
        {
            string owner = GlobalSystemSettingRegistry.Instance.GetAuthoritativeOwner(canonicalTarget);
            if (!string.IsNullOrEmpty(owner)) return owner;

            if (_canonicalTargets.TryGetValue(canonicalTarget, out var meta))
                return meta.OwningCategory;

            return string.Empty;
        }

        public bool TryGetOwner(string canonicalTarget, out string authoritativeOwner)
        {
            authoritativeOwner = GetAuthoritativeOwner(canonicalTarget);
            return !string.IsNullOrEmpty(authoritativeOwner);
        }

        public bool IsAuthoritativeForCategory(string canonicalTarget, string category)
        {
            string owner = GetAuthoritativeOwner(canonicalTarget);
            if (string.IsNullOrEmpty(owner)) return true; // Unique unassigned target

            return CategoryMatches(owner, category);
        }

        private static bool CategoryMatches(string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return true;
            if ((expected.Equals("InputOptimizer", StringComparison.OrdinalIgnoreCase) || expected.Equals("InputLatency", StringComparison.OrdinalIgnoreCase)) &&
                (actual.Equals("InputOptimizer", StringComparison.OrdinalIgnoreCase) || actual.Equals("InputLatency", StringComparison.OrdinalIgnoreCase)))
                return true;
            if ((expected.Equals("Debloat", StringComparison.OrdinalIgnoreCase) || expected.Equals("Debloater", StringComparison.OrdinalIgnoreCase)) &&
                (actual.Equals("Debloat", StringComparison.OrdinalIgnoreCase) || actual.Equals("Debloater", StringComparison.OrdinalIgnoreCase)))
                return true;
            if ((expected.Equals("MaximumPerformance", StringComparison.OrdinalIgnoreCase) || expected.Equals("MaxPerformance", StringComparison.OrdinalIgnoreCase)) &&
                (actual.Equals("MaximumPerformance", StringComparison.OrdinalIgnoreCase) || actual.Equals("MaxPerformance", StringComparison.OrdinalIgnoreCase)))
                return true;
            if ((expected.Equals("AIOptimization", StringComparison.OrdinalIgnoreCase) || expected.Equals("AI", StringComparison.OrdinalIgnoreCase)) &&
                (actual.Equals("AIOptimization", StringComparison.OrdinalIgnoreCase) || actual.Equals("AI", StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        public bool RegisterTarget(
            string canonicalTarget,
            string authoritativeOwner,
            string canonicalId,
            string optimalValue,
            string displayName,
            string action = "SetRegistryValue")
        {
            if (string.IsNullOrWhiteSpace(canonicalTarget)) return false;

            _canonicalTargets[canonicalTarget] = new CanonicalOptimizationMetadata
            {
                CanonicalTarget = canonicalTarget,
                OwningCategory = authoritativeOwner,
                CanonicalId = canonicalId,
                OptimalValue = optimalValue,
                DisplayName = displayName,
                Action = action
            };
            return true;
        }

        private void InitializeAuthoritativeAssignments()
        {
            // =========================================================================
            // 1. INPUT OPTIMIZER (Exclusive Authority over Pointer & Keyboard Latency)
            // =========================================================================
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\MOUCLASS\\PARAMETERS\\MOUSEDATAQUEUESIZE", "InputOptimizer", "opt.input.mouclass_size", "20", "Mouse Class Driver Buffer Size Optimization");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\KBDCLASS\\PARAMETERS\\KEYBOARDDATAQUEUESIZE", "InputOptimizer", "opt.input.kbdclass_size", "20", "Keyboard Class Driver Buffer Size Calibration");
            Register("REG:HKCU\\CONTROL PANEL\\KEYBOARD\\KEYBOARDDELAY", "InputOptimizer", "opt.input.keyboard_delay", "0", "Keyboard Repeat Delay");
            Register("REG:HKCU\\CONTROL PANEL\\KEYBOARD\\KEYBOARDSPEED", "InputOptimizer", "opt.input.keyboard_speed", "31", "Keyboard Repeat Rate");
            Register("REG:HKCU\\CONTROL PANEL\\MOUSE\\MOUSESENSITIVITY", "InputOptimizer", "opt.input.mouse_sensitivity", "10", "Pointer Base Sensitivity (6/11 Default 1:1)");
            Register("REG:HKCU\\CONTROL PANEL\\MOUSE\\MOUSESPEED", "InputOptimizer", "opt.input.mouse_speed", "0", "Mouse Pointer Acceleration Curve");
            Register("REG:HKCU\\CONTROL PANEL\\MOUSE\\MOUSETHRESHOLD1", "InputOptimizer", "opt.input.mouse_threshold1", "0", "Mouse Pointer Threshold 1");
            Register("REG:HKCU\\CONTROL PANEL\\MOUSE\\MOUSETHRESHOLD2", "InputOptimizer", "opt.input.mouse_threshold2", "0", "Mouse Pointer Threshold 2");
            Register("REG:HKCU\\CONTROL PANEL\\MOUSE\\MOUSEHOVERTIME", "InputOptimizer", "opt.input.mouse_hover_time", "10", "Mouse Hover Response Time");
            Register("REG:HKCU\\CONTROL PANEL\\MOUSE\\ACTIVEWINDOWTRACKING", "InputOptimizer", "opt.input.active_window_tracking", "0", "Active Window Tracking Delay");
            Register("REG:HKCU\\CONTROL PANEL\\ACCESSIBILITY\\STICKYKEYS\\FLAGS", "InputOptimizer", "opt.input.sticky_keys", "506", "Disable StickyKeys Shortcut Bypass");
            Register("REG:HKCU\\CONTROL PANEL\\ACCESSIBILITY\\KEYBOARD RESPONSE\\FLAGS", "InputOptimizer", "opt.input.filter_keys", "0", "Disable FilterKeys & Debounce Latency");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\USB\\DISABLESELECTIVESUSPEND", "InputOptimizer", "opt.input.usb_selective_suspend", "1", "Per-Device USB Selective Suspend Exemption");

            // =========================================================================
            // 2. DEBLOATER (Exclusive Authority over Appx, Bloatware & Consumer Telemetry)
            // =========================================================================
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\CONTENTDELIVERYMANAGER\\SILENTINSTALLEDAPPSENABLED", "Debloat", "opt.debloat.silent_apps", "0", "Disable Silent Consumer App Installs");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\CONTENTDELIVERYMANAGER\\SUBSCRIBEDCONTENT-338388ENABLED", "Debloat", "opt.debloat.promoted_suggestions", "0", "Disable Suggested Apps in Windows Start");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\WINDOWS SEARCH\\ALLOWCORTANA", "Debloat", "opt.debloat.allow_cortana", "0", "Disable Cortana Voice Assistant");
            Register("REG:HKCU\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\EXPLORER\\DISABLESEARCHBOXSUGGESTIONS", "Debloat", "opt.debloat.bing_search_box", "1", "Disable Bing Web Search in Explorer");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\DSH\\ALLOWNEWSANDINTERESTS", "Debloat", "opt.debloat.widgets", "0", "Disable Windows 11 Widgets Feed");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\SIPROFILES\\MSAL_DISCOVERY", "Debloat", "opt.debloat.msal_discovery", "0", "Disable Background Account Discovery Sync");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\CONTENTDELIVERYMANAGER\\SYSTEMPANEFEEDENABLED", "Debloat", "opt.debloat.system_pane_feed", "0", "Disable System Pane Promotional Feed");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\EXPLORER\\ADVANCED\\SHOWSYNCPROVIDERNOTIFICATIONS", "Debloat", "opt.debloat.sync_notifications", "0", "Disable OneDrive Ads in File Explorer");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\PRIVACY\\TAILOREDEXPERIENCESWITHDIAGNOSTICDATAENABLEDENUM", "Debloat", "opt.debloat.tailored_experiences", "0", "Disable Tailored Experiences Data Mining");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\FEEDBACK\\SYSTEMVALUE", "Debloat", "opt.debloat.feedback_frequency", "0", "Disable Windows Feedback Requests Frequency");

            // =========================================================================
            // 3. PRO OPTIMIZATION (Exclusive Authority over MMCSS, Audio, CPU Scheduling)
            // =========================================================================
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\PRIORITYCONTROL\\WIN32PRIORITYSEPARATION", "Pro", "opt.pro.win32_priority_separation", "38", "CPU Scheduling Quantum Priority Separation");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\NETWORKTHROTTLINGINDEX", "Pro", "opt.pro.network_throttling", "ffffffff", "Disable MMCSS Network Throttling Index");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\SYSTEMRESPONSIVENESS", "Pro", "opt.pro.system_responsiveness", "10", "MMCSS Background CPU Reservation (10% Documented Minimum)");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\TASKS\\GAMES\\GPU PRIORITY", "Pro", "opt.pro.games_gpu_priority", "8", "MMCSS High-Priority Game GPU Scheduling");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\TASKS\\GAMES\\PRIORITY", "Pro", "opt.pro.games_priority", "6", "MMCSS High-Priority Game Task Scheduling");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\TASKS\\PRO AUDIO\\SCHEDULING CATEGORY", "Pro", "opt.pro.proaudio_scheduling", "High", "MMCSS Pro Audio Real-Time Category");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\TCPIP\\PARAMETERS\\TCPACKFREQUENCY", "Pro", "opt.pro.tcp_ack_frequency", "1", "Immediate TCP ACK Pacing (TcpAckFrequency = 1)");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\TCPIP\\PARAMETERS\\INTERFACES\\DEFAULT\\TCPACKFREQUENCY", "Pro", "opt.pro.tcp_ack_frequency_default", "1", "Immediate Interface TCP ACK Pacing");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\TCPIP\\PARAMETERS\\TCPDELACKTICKS", "Pro", "opt.pro.tcp_del_ack_ticks", "0", "Zero TCP Delayed ACK Timer (TcpDelAckTicks = 0)");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\TCPIP\\PARAMETERS\\TCP1323OPTS", "Pro", "opt.pro.tcp1323_opts", "1", "RFC 1323 High-Performance TCP Timestamps");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\KERNEL\\GLOBALTIMERRESOLUTIONREQUESTS", "Pro", "opt.pro.global_timer_resolution", "1", "Global High-Resolution Kernel Timer Precision (0.5ms)");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SVCHOSTSPLITTHRESHOLDINKB", "Pro", "opt.pro.svchost_split", "16777216", "SvcHost Process Isolation Dynamic Split Threshold");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\GRAPHICSDRIVERS\\TDRDELAY", "Pro", "opt.pro.tdr_delay", "10", "Increase GPU TDR Recovery Delay Timeout");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\FILESYSTEM\\NTFSDISABLELASTACCESSUPDATE", "Pro", "opt.pro.ntfs_last_access", "1", "Disable NTFS Last Access Time Updates");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\FILESYSTEM\\NTFSMEMORYUSAGE", "Pro", "opt.pro.ntfs_memory_usage", "2", "Increase NTFS Paged Pool Working Cache");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\IOPAGELOCKLIMIT", "Pro", "opt.pro.io_page_lock_limit", "16777216", "Expand Direct I/O Memory Lock Limit (16MB)");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\POOLUSAGEMAXIMUM", "Pro", "opt.pro.pool_usage_max", "60", "Optimize Dynamic Memory Pool Allocation Limit");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\PREFETCHPARAMETERS\\ENABLESUPERFETCH", "Pro", "opt.pro.superfetch_ssd", "0", "Disable SysMain Superfetch on SSD Drives");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\POWERSETTINGS\\54533251-82BE-4824-96C1-47B60B740D00\\89E37796-A416-4A73-9EF1-77886F795CB9\\ATTRIBUTES", "Pro", "opt.pro.power_epp_attr", "2", "Expose Energy Performance Preference in Power GUI");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\POWERSETTINGS\\54533251-82BE-4824-96C1-47B60B740D00\\94D3A615-A899-4AC5-AE2B-E4D8F634367F\\ATTRIBUTES", "Pro", "opt.pro.power_boost_attr", "2", "Expose Processor Performance Boost Mode in Power GUI");
            Register("SVC:WSEARCH", "Pro", "opt.pro.svc_wsearch", "Manual", "Windows Search Indexing Service", "SetServiceStartup");

            // =========================================================================
            // 4. ULTIMATE OPTIMIZATION (Exclusive Authority over Workstation & GPU)
            // =========================================================================
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\GRAPHICSDRIVERS\\HWSCHMODE", "Ultimate", "opt.ultimate.hags", "2", "Enable Hardware-Accelerated GPU Scheduling (HAGS)");
            Register("POWER:SUB_PROCESSOR\\CPMINCORES", "Ultimate", "opt.ultimate.core_unparking", "0", "CPU Core Parking Suppression (AC Power)");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\FILESYSTEM\\NTFSDISABLE8DOT3NAMECREATION", "Ultimate", "opt.ultimate.ntfs_8dot3", "1", "Disable NTFS 8.3 Short File Name Generation");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\FILESYSTEM\\NTFSDISABLECOMPRESSION", "Ultimate", "opt.ultimate.ntfs_compression", "1", "Disable Real-Time File System Compression");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\FILESYSTEM\\BYPASSIOFLAGS", "Ultimate", "opt.ultimate.bypassio", "1", "Enable DirectStorage BypassIO Storage Path");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\DISABLEPAGINGEXECUTIVE", "Ultimate", "opt.ultimate.disable_paging_exec", "1", "Keep Windows Kernel & Drivers in Physical RAM");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\LARGESYSTEMCACHE", "Ultimate", "opt.ultimate.large_system_cache", "1", "Enable Large System File Cache for High-Throughput I/O");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\CLEARPAGEFILEATSHUTDOWN", "Ultimate", "opt.ultimate.clear_pagefile", "0", "Do Not Clear Page File on Shutdown for Instant Power Down");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\DIRECT3D\\MAXFRAMELATENCY", "Ultimate", "opt.ultimate.d3d_latency", "1", "Direct3D Render Ahead Limit (1 Pre-Rendered Frame)");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\DIRECTX\\USERGPUPREFERENCES\\DIRECTXUSERGLOBALSETTINGS\\SWAPEFFECTUPGRADE", "Ultimate", "opt.ultimate.swap_effect", "1", "DXGI Flip Model Swap Effect Upgrade");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\TASKS\\AUDIO\\AFFINITY", "Ultimate", "opt.ultimate.audio_affinity", "0", "Unrestricted Audio Thread CPU Core Affinity");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\POWERSETTINGS\\501A4D13-42AF-4429-9FD5-6921FB825D05\\EE12F906-D277-404B-B6DA-F55A15196015\\ATTRIBUTES", "Ultimate", "opt.ultimate.nvme_idle_attr", "2", "Expose NVMe Idle Power State in Power Options");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\POWERSETTINGS\\54533251-82BE-4824-96C1-47B60B740D00\\7F2F4C9F-3E54-4A2B-9E0C-8D3A5A7B1E2F\\ATTRIBUTES", "Ultimate", "opt.ultimate.perf_autonomous_attr", "2", "Expose Processor Autonomous Mode in Power Options");

            // =========================================================================
            // 5. MAX PERFORMANCE (Exclusive Authority over Extreme Gaming & Core Unparking)
            // =========================================================================
            Register("REG:HKCU\\SYSTEM\\GAMECONFIGSTORE\\GAMEDVR_ENABLED", "MaximumPerformance", "opt.maxperf.gamedvr_enabled", "0", "Complete GameDVR Background Video Encoding Suppression");
            Register("REG:HKCU\\SYSTEM\\GAMECONFIGSTORE\\GAMEDVR_FSEBEHAVIORMODE", "MaximumPerformance", "opt.maxperf.fse_behavior", "2", "Enforce Fullscreen Exclusive True Hardware Latency Mode");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\GAMEMODE\\ALLOWAUTOGAMEMODE", "MaximumPerformance", "opt.maxperf.auto_game_mode", "1", "Enforce Dedicated Windows Gaming Mode Priority");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\POWERSETTINGS\\54533251-82BE-4824-96C1-47B60B740D00\\0CC5B647-C1DF-4637-891A-DEC35C318583\\VALUEMAX", "MaximumPerformance", "opt.maxperf.core_unparking", "0", "Unpark All CPU Cores for 100% Performance Headroom");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\POWERTHROTTLING\\POWERTHROTTLINGOFF", "MaximumPerformance", "opt.maxperf.power_throttling_off", "1", "Disable Background EcoQoS Power Throttling for Virtualization");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\CLASS\\{4D36E968-E325-11CE-BFC1-08002BE10318}\\0000\\POWERMIZERENABLE", "MaximumPerformance", "opt.maxperf.powermizer", "1", "NVIDIA PowerMizer Performance Mode");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\NVLDDMKM\\GLOBAL\\NVCPLHORIZONTALPAGEFILES\\POWERMIZERENABLE", "MaximumPerformance", "opt.maxperf.powermizer_global", "1", "NVIDIA Driver Global PowerMizer Mode");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\PREFETCHPARAMETERS\\ENABLEPREFETCHER", "MaximumPerformance", "opt.maxperf.enable_prefetcher", "0", "Disable Prefetcher on High-End NVMe Systems");

            // =========================================================================
            // 6. NORMAL OPTIMIZATION (Exclusive Authority over Safe Everyday Performance)
            // =========================================================================
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\MENUSHOWDELAY", "Normal", "opt.normal.menu_show_delay", "0", "Remove Menu Show Delay");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\WINDOWMETRICS\\MINANIMATE", "Normal", "opt.normal.min_animate", "0", "Eliminate Window Minimize & Restore Animation Delay");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\DRAGFULLWINDOWS", "Normal", "opt.normal.drag_full_windows", "0", "Disable Full Window Content While Dragging");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\SMOOTHSCROLL", "Normal", "opt.normal.smooth_scroll", "0", "Disable Smooth Scrolling Lag");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\HUNGAPPTIMEOUT", "Normal", "opt.normal.hung_app_timeout", "2000", "Reduce Hung Application Detection Timeout");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\WAITTOKILLAPPTIMEOUT", "Normal", "opt.normal.wait_to_kill_app", "2000", "Reduce Wait-to-Kill Application Timeout");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\DWM\\ENABLEAEROPEEK", "Normal", "opt.normal.enable_aero_peek", "0", "Disable Aero Peek Delay");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\THEMES\\PERSONALIZE\\ENABLETRANSPARENCY", "Normal", "opt.normal.transparency", "0", "Disable Taskbar & Window Transparency");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\EXPLORER\\ADVANCED\\TASKBARANIMATIONS", "Normal", "opt.normal.taskbar_animations", "0", "Disable Taskbar Pop-up Animations");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\POWER\\HIBERNATEENABLED", "Normal", "opt.normal.hibernate_enabled", "0", "Disable Desktop Hibernation File (Reclaim Disk Space)");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\DATACOLLECTION\\ALLOWTELEMETRY", "Normal", "opt.normal.allow_telemetry", "0", "Set Windows Diagnostic Data to Minimal Telemetry");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\WINDOWS ERROR REPORTING\\DISABLED", "Normal", "opt.normal.wer_disabled", "1", "Disable Windows Error Reporting Service Daemon");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\CRASHCONTROL\\CRASHDUMPENABLED", "Normal", "opt.normal.crash_dump_enabled", "0", "Disable System Memory Dump Logging");
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\BACKGROUNDACCESSAPPLICATIONS\\GLOBALUSERDISABLED", "Normal", "opt.normal.bg_access_apps", "1", "Throttle Background Store App CPU Activity");
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\WINLOGON\\AUTORESTARTSHELL", "Normal", "opt.normal.autorestart_shell", "1", "Automatically Restart Windows Explorer Shell on Crash");
            Register("SVC:SYSMAIN", "Normal", "opt.normal.svc_sysmain", "Disabled", "SuperFetch / SysMain Service Configuration");
            Register("SVC:DIAGTRACK", "Normal", "opt.normal.svc_diagtrack", "Disabled", "Connected User Experiences and Telemetry");
            Register("SVC:DMWAPPUSHSERVICE", "Normal", "opt.normal.svc_dmwappushservice", "Disabled", "WAP Push Message Routing Service");
            Register("SVC:REMOTEREGISTRY", "Normal", "opt.normal.svc_remoteregistry", "Disabled", "Remote Registry Service");
            Register("SVC:DEFRAGSVC", "Normal", "opt.normal.svc_defragsvc", "Manual", "Optimize Drives Service Startup");
            Register("SVC:WERSVC", "Normal", "opt.normal.svc_wersvc", "Disabled", "Windows Error Reporting Service");
            Register("SVC:RETAILDEMO", "Normal", "opt.normal.svc_retaildemo", "Disabled", "Retail Demo Service");

            // =========================================================================
            // 7. LOW-END PC OPTIMIZATION (Exclusive Authority over Aggressive Lightweight GUI)
            // =========================================================================
            Register("REG:HKCU\\SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\EXPLORER\\VISUALEFFECTS\\VISUALFXSETTING", "LowEndPC", "opt.lowend.visualfx", "2", "Adjust for Best Performance (Visual Effects)");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\FONTSMOOTHING", "LowEndPC", "opt.lowend.fontsmoothing", "2", "Standard ClearType Font Smoothing Calibration");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\USERPREFERENCEMASK", "LowEndPC", "opt.lowend.userprefmask", "90,12,03,80,10,00,00,00", "Performance Window Manager Metrics Mask");
            Register("REG:HKCU\\CONTROL PANEL\\DESKTOP\\WINDOWMETRICS\\APPHORIZONTALSPACING", "LowEndPC", "opt.lowend.app_horizontal_spacing", "0", "Reduce Window Metrics Padding");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\DWM\\DISALLOWANIMATIONS", "LowEndPC", "opt.lowend.disallow_animations", "1", "Force Disable Desktop Window Animations");

            // =========================================================================
            // 8. CORE OPTIMIZATION (Exclusive Authority over Background Services & Tasks)
            // =========================================================================
            Register("SVC:MAPSBROKER", "CoreOptimization", "opt.core.svc_mapsbroker", "Disabled", "Downloaded Maps Manager Background Service");
            Register("SVC:SENSORDATASERVICE", "CoreOptimization", "opt.core.svc_sensordatasvc", "Disabled", "Sensor Data Service Background Polling");
            Register("SVC:SENSORSVC", "CoreOptimization", "opt.core.svc_sensorsvc", "Disabled", "Sensor Service Background Polling");
            Register("SVC:SENSRSVC", "CoreOptimization", "opt.core.svc_sensrsvc", "Disabled", "Sensor Monitoring Service");
            Register("SVC:TABLETINPUTSERVICE", "CoreOptimization", "opt.core.svc_tabletinput", "Disabled", "Touch Keyboard and Handwriting Panel Service");
            Register("SVC:WPCMONSVC", "CoreOptimization", "opt.core.svc_wpcmon", "Disabled", "Parental Controls Monitoring Daemon");
            Register("SVC:WBIOSRVC", "CoreOptimization", "opt.core.svc_wbiosrvc", "Manual", "Windows Biometric Service Manual Startup");
            Register("SVC:XBLAUTHMANAGER", "CoreOptimization", "opt.core.svc_xblauth", "Manual", "Xbox Live Auth Manager On-Demand Startup");
            Register("SVC:XBLGAMESAVE", "CoreOptimization", "opt.core.svc_xblsave", "Manual", "Xbox Live Game Save On-Demand Startup");
            Register("SVC:XBOXNETAPISVC", "CoreOptimization", "opt.core.svc_xboxnet", "Manual", "Xbox Live Networking Service On-Demand Startup");
            Register("TASK:\\MICROSOFT\\WINDOWS\\CUSTOMER EXPERIENCE IMPROVEMENT PROGRAM\\CONSOLIDATOR", "CoreOptimization", "opt.core.task_ceip_consolidator", "Disabled", "CEIP Telemetry Consolidator Scheduled Task");
            Register("TASK:\\MICROSOFT\\WINDOWS\\CUSTOMER EXPERIENCE IMPROVEMENT PROGRAM\\USBCEIP", "CoreOptimization", "opt.core.task_ceip_usb", "Disabled", "CEIP USB Telemetry Scheduled Task");
            Register("TASK:\\MICROSOFT\\WINDOWS\\APPLICATION EXPERIENCE\\MICROSOFT COMPATIBILITY APPRAISER", "CoreOptimization", "opt.core.task_appraiser", "Disabled", "Microsoft Compatibility Appraiser Telemetry Task");
            Register("TASK:\\MICROSOFT\\WINDOWS\\APPLICATION EXPERIENCE\\PROGRAMDATAUPDATER", "CoreOptimization", "opt.core.task_programdataupdater", "Disabled", "Application Experience Program Data Updater");

            // =========================================================================
            // 9. AI OPTIMIZATION (Exclusive Authority over Workload Scheduling & Dynamic Priority)
            // =========================================================================
            Register("REG:HKLM\\SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION\\MULTIMEDIA\\SYSTEMPROFILE\\TASKS\\GAMES\\AFFINITY", "AIOptimization", "opt.ai.games_affinity", "0", "Adaptive Dynamic CPU Core Affinity Scheduling");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\EXECUTIVE\\ADDITIONALCRITICALWORKERTHREADS", "AIOptimization", "opt.ai.critical_workers", "2", "Additional Kernel Critical Worker Threads Pool");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\EXECUTIVE\\ADDITIONALDELAYEDWORKERTHREADS", "AIOptimization", "opt.ai.delayed_workers", "2", "Additional Kernel Delayed Worker Threads Pool");

            // =========================================================================
            // 10. REGISTRY INTELLIGENCE (Exclusive Authority over Specialized System Tweaks)
            // =========================================================================
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\LANMANWORKSTATION\\PARAMETERS\\DISABLEBANDWIDTHTHROTTLING", "RegistryIntelligence", "tweak.network.lanman_bw", "1", "Disable SMB Network Bandwidth Throttling");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\SERVICES\\LANMANWORKSTATION\\PARAMETERS\\DISABLELARGEMTU", "RegistryIntelligence", "tweak.network.lanman_mtu", "0", "Allow Large MTU Windows File Sharing Transfers");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\SYSTEM\\PUBLISHUSERACTIVITIES", "RegistryIntelligence", "tweak.privacy.publish_activities", "0", "Disable Publishing User Timeline Activities");
            Register("REG:HKLM\\SOFTWARE\\POLICIES\\MICROSOFT\\WINDOWS\\SYSTEM\\UPLOADUSERACTIVITIES", "RegistryIntelligence", "tweak.privacy.upload_activities", "0", "Disable Uploading User Timeline Activities");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SESSION MANAGER\\MEMORY MANAGEMENT\\SECONDLEVELDATACACHE", "RegistryIntelligence", "tweak.cpu.l2_cache", "1024", "Explicit CPU L2 Cache Size Hardware Hint");

            // =========================================================================
            // 11. BIOS SAFE (Exclusive Authority over Firmware Guidance & Safe PCIe/Power)
            // =========================================================================
            Register("BIOS:REBOOTTOUEFI", "BiosSafe", "bios.action.reboot_to_uefi_firmware", "RebootToUefi", "Reboot to UEFI Firmware Setup", "SetBiosSetting");
            Register("REG:HKLM\\SYSTEM\\CURRENTCONTROLSET\\CONTROL\\DEVICEGUARD\\SCENARIOS\\HYPERVISORENFORCEDCODEINTEGRITY\\ENABLED", "BiosSafe", "biossafe.cpu.virtualization_guidance", "0", "Virtualization-Based Security (VBS/HVCI) Performance Guidance");
        }

        private void Register(string target, string owner, string canonicalId, string optimalValue, string displayName, string action = "SetRegistryValue")
        {
            RegisterTarget(target, owner, canonicalId, optimalValue, displayName, action);
        }
    }
}
