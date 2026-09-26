#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// Strict policy engine protecting critical Windows OS subsystems, security software,
    /// active hardware drivers, input devices, audio pipelines, anti-cheat engines, and current workloads.
    /// </summary>
    public class ProtectedProcessPolicy
    {
        // 1. Windows Kernel & Essential Subsystems (Never terminate)
        private static readonly HashSet<string> KernelAndSystemSubsystems = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "System Idle Process", "Registry", "Memory Compression", "Secure System",
            "smss", "csrss", "wininit", "services", "lsass", "lsm", "winlogon", "fontdrvhost",
            "dwm", "sihost", "taskhostw", "ctfmon", "TextInputHost", "SearchIndexer",
            "ShellExperienceHost", "StartMenuExperienceHost", "ApplicationFrameHost",
            "RuntimeBroker", "explorer", "spoolsv", "audiodg", "dasHost", "conhost"
        };

        // 2. Windows Security & Core Infrastructure Services
        private static readonly HashSet<string> CoreSecurityAndInfrastructure = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "MsMpEng", "SecurityHealthService", "SecurityHealthSystray", "Sense", "SenseCncProxy",
            "wscsvc", "CryptSvc", "RpcSs", "RpcEptMapper", "DcomLaunch", "BrokerInfrastructure",
            "SystemEventsBroker", "TimeBrokerSvc", "StateRepository", "CoreMessagingRegistrar",
            "EventLog", "EventSystem", "Schedule", "ProfSvc", "gpsvc", "LanmanWorkstation",
            "LanmanServer", "Dhcp", "Dnscache", "netprofm", "nlasvc", "Power", "PlugPlay",
            "W32Time", "VaultSvc", "KeyIso", "SamSs", "LsaIso", "mpssvc", "BFE", "InstallService"
        };

        // 3. Hardware Drivers & Active Peripheral Infrastructure
        private static readonly HashSet<string> HardwareDriverComponents = new(StringComparer.OrdinalIgnoreCase)
        {
            // NVIDIA
            "nvcontainer", "NVDisplay.Container", "NVIDIA Web Helper", "nvspcaps64", "nvsphelper64",
            "NVIDIA RTX Voice", "NVIDIA Broadcast", "nvagent",
            // AMD
            "amdfendrsr", "amdow", "AMDRSServ", "RadeonSoftware", "cnext", "AMDRSSrcExt",
            // Intel
            "igfxCUIService", "igfxEM", "igfxHK", "IntelGraphicsAdminService", "igfxTray", "OneApp.IGCC.WinService",
            // Audio & DACs
            "AudioSrv", "AudioEndpointBuilder", "RAVCbg64", "RAVBg64", "RtkAudioService64", "RealtekService",
            "nahimicservice", "nahimicv3", "creative.sb", "audiodg", "WavesSysSvc64",
            // Input & Peripherals (Mice, Keyboards, Stream Decks, Tablets)
            "LogiRegistryService", "lghub_agent", "lghub", "iCUE", "CorsairService", "CorsairLLAService",
            "Razer Synapse Service", "RazerCentralService", "RzSDKServer", "RzActionEngine",
            "Wacom_Tablet", "Wacom_TabletUser", "StreamDeck"
        };

        // 4. Anti-Cheat Drivers & Processes (Crucial for online gaming integrity)
        private static readonly HashSet<string> AntiCheatEngines = new(StringComparer.OrdinalIgnoreCase)
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Sys", "EasyAntiCheat_Setup",
            "BEService", "BattlEye", "vgtray", "vgc", "ACE-Base", "ACE-Guard",
            "PunkBuster", "PnkBstrA", "PnkBstrB", "FACEITService", "faceit", "faceitclient",
            "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher", "Ricochet", "VALORANT-Win64-Shipping"
        };

        // 5. Game Launchers & Platforms (Protected during gaming workload)
        private static readonly HashSet<string> GamePlatforms = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamservice", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper",
            "Origin", "OriginWebHelperService", "EADesktop", "EABackgroundService",
            "Battle.net", "Battle.net.helper", "Agent", "UbisoftConnect", "upc", "Uplay",
            "GalaxyClient", "GalaxyClientService", "RiotClientServices", "RiotClientUx"
        };

        // 6. Professional Editing, Rendering & Audio DAW Suites
        private static readonly HashSet<string> CreativeWorkloadSuites = new(StringComparer.OrdinalIgnoreCase)
        {
            // Adobe
            "Adobe Premiere Pro", "AfterFX", "Photoshop", "Illustrator", "Audition", "Adobe Media Encoder",
            "AdobeIPCBroker", "CCLibrary", "CoreSync", "Adobe CEF Helper",
            // Blackmagic DaVinci Resolve
            "Resolve", "DaVinci Resolve", "fuscript", "dpdecoder",
            // 3D, VFX & Rendering
            "blender", "cinema 4d", "maya", "3dsmax", "houdini", "unrealengine", "unity",
            // Broadcasting & Transcoding
            "obs64", "obs32", "streamlabs obs", "HandBrake", "ffmpeg",
            // Topaz AI
            "Topaz Video AI", "Topaz Photo AI", "Topaz Gigapixel AI",
            // Pro Audio & DAWs
            "Ableton Live Suite", "FL64", "Cubase", "ProTools", "Studio One", "Reaper", "Bitwig Studio"
        };

        /// <summary>
        /// Evaluates whether a process is protected and should never be terminated automatically.
        /// </summary>
        public bool IsProcessProtected(ProcessIdentityDto process, bool isGamingActive, bool isEditingActive)
        {
            if (process == null) return true;

            string name = process.ProcessName.Trim();
            if (string.IsNullOrEmpty(name)) return true;

            // Self protection (Error Optimizer)
            if (name.Equals("ErrorOptimizer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ErrorOptimizerService", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BiosOptimizer.CLI", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BiosOptimizer.GUI", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BiosOptimizer.Service", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // System PID protection
            if (process.ProcessId <= 4) return true;

            // Kernel and OS Subsystem
            if (KernelAndSystemSubsystems.Contains(name)) return true;

            // Windows Core Infrastructure & Security
            if (CoreSecurityAndInfrastructure.Contains(name)) return true;

            // Hardware Drivers & Peripherals
            if (HardwareDriverComponents.Contains(name)) return true;

            // Anti-Cheat protection
            if (AntiCheatEngines.Contains(name)) return true;

            // Gaming workload protection
            if (isGamingActive && GamePlatforms.Contains(name))
            {
                return true;
            }

            // Editing workload protection
            if (isEditingActive && CreativeWorkloadSuites.Contains(name))
            {
                return true;
            }

            // Active foreground application protection
            if (process.IsForeground)
            {
                return true;
            }

            // Visible top-level desktop window (active user session)
            if (process.HasVisibleWindow && !IsKnownBackgroundOverlay(name))
            {
                return true;
            }

            // Executable path in Windows System32 / Drivers critical infrastructure
            if (!string.IsNullOrEmpty(process.ExecutablePath))
            {
                string pathLower = process.ExecutablePath.ToLowerInvariant();
                if (pathLower.EndsWith("\\drivers\\") || pathLower.EndsWith(".sys"))
                    return true;
                if (pathLower.Contains("\\system32\\drivers\\") || pathLower.Contains("\\system32\\wbem\\"))
                    return true;
            }

            return false;
        }

        private static bool IsKnownBackgroundOverlay(string name)
        {
            // Some updaters/overlays create hidden/dummy windows but are safe to terminate
            return name.Equals("GoogleUpdate", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Classifies the safety risk of an optimization candidate.
        /// </summary>
        public ProcessRisk EvaluateRisk(ProcessIdentityDto process, bool isGamingActive, bool isEditingActive)
        {
            if (process.ProcessId <= 4 || KernelAndSystemSubsystems.Contains(process.ProcessName))
                return ProcessRisk.CRITICAL_SYSTEM;

            if (CoreSecurityAndInfrastructure.Contains(process.ProcessName) ||
                HardwareDriverComponents.Contains(process.ProcessName) ||
                AntiCheatEngines.Contains(process.ProcessName))
                return ProcessRisk.ELEVATED_DRIVER;

            if ((isGamingActive && GamePlatforms.Contains(process.ProcessName)) ||
                (isEditingActive && CreativeWorkloadSuites.Contains(process.ProcessName)))
                return ProcessRisk.WORKLOAD_DEPENDENCY;

            if (process.HasVisibleWindow || process.IsForeground)
                return ProcessRisk.LOW_BACKGROUND;

            return ProcessRisk.SAFE_TO_TERMINATE;
        }
    }
}
