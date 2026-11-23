#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// Strict policy engine protecting critical Windows OS subsystems, security software,
    /// active hardware drivers, input devices, audio pipelines, anti-cheat engines, and current workloads.
    /// </summary>
    public class ProtectedProcessPolicy
    {
        private static readonly int _currentProcessId = Environment.ProcessId;
        private static readonly string _currentExecutablePath = ResolveCurrentExecutablePath();
        private static readonly string _currentProcessName = ResolveCurrentProcessName();

        // Explicit Optimizer Self-Protection Signatures (exact normalized process names without .exe)
        private static readonly HashSet<string> OptimizerSelfProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ERROR OPTIMIZER PROTOTYPE",
            "ERROR OPTIMIZER",
            "ErrorOptimizer",
            "ErrorOptimizerService",
            "ErrorOptimizer.Host",
            "BiosOptimizer.CLI",
            "BiosOptimizer.GUI",
            "BiosOptimizer.Service",
            "PrototypeHost"
        };

        private static string ResolveCurrentExecutablePath()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path))
                    return ProcessIdentitySafety.NormalizeExecutablePath(path);
            }
            catch { }

            try
            {
                using var current = Process.GetCurrentProcess();
                if (ProcessIdentitySafety.TryResolveExecutablePath(current.Id, current, out string resolved, out _, out _))
                    return resolved;
            }
            catch { }

            return string.Empty;
        }

        private static string ResolveCurrentProcessName()
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                return NormalizeProcessName(current.ProcessName);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeProcessName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            string trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^4]
                : trimmed;
        }

        /// <summary>
        /// Task F: Authoritative self-protection check for the running optimizer process,
        /// ERROR OPTIMIZER PROTOTYPE.exe, and optimizer host/service binaries.
        /// Fails closed when an optimizer name signature is present even if executable path is unresolved.
        /// </summary>
        public static bool IsSelfOrOptimizerHost(ProcessIdentityDto? process, out string reason)
        {
            reason = string.Empty;
            if (process == null)
            {
                reason = "Self-protection fail-closed: null process identity.";
                return true;
            }

            if (process.ProcessId > 0 && process.ProcessId == _currentProcessId)
            {
                reason = $"Active optimizer process (current PID {_currentProcessId}).";
                return true;
            }

            string cleanName = NormalizeProcessName(process.ProcessName);
            string normalizedPath = ProcessIdentitySafety.NormalizeExecutablePath(process.ExecutablePath);
            string pathFileName = string.IsNullOrEmpty(normalizedPath)
                ? string.Empty
                : NormalizeProcessName(Path.GetFileName(normalizedPath));

            if (!string.IsNullOrEmpty(_currentExecutablePath) &&
                !string.IsNullOrEmpty(normalizedPath) &&
                normalizedPath.Equals(_currentExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Matches running optimizer executable image ({normalizedPath}).";
                return true;
            }

            if (!string.IsNullOrEmpty(cleanName) && OptimizerSelfProcessNames.Contains(cleanName))
            {
                reason = string.IsNullOrEmpty(normalizedPath)
                    ? $"Optimizer host binary '{cleanName}' protected (fail-closed: executable path unresolved)."
                    : $"Optimizer host/service binary '{cleanName}' ({normalizedPath}).";
                return true;
            }

            if (!string.IsNullOrEmpty(pathFileName) && OptimizerSelfProcessNames.Contains(pathFileName))
            {
                reason = $"Optimizer host executable image '{pathFileName}' ({normalizedPath}).";
                return true;
            }

            // Fail-closed protection for versioned/renamed optimizer host executables (e.g. "ERROR OPTIMIZER V4")
            if ((!string.IsNullOrEmpty(cleanName) &&
                 (cleanName.StartsWith("ERROR OPTIMIZER", StringComparison.OrdinalIgnoreCase) ||
                  cleanName.StartsWith("BiosOptimizer", StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrEmpty(pathFileName) &&
                 (pathFileName.StartsWith("ERROR OPTIMIZER", StringComparison.OrdinalIgnoreCase) ||
                  pathFileName.StartsWith("BiosOptimizer", StringComparison.OrdinalIgnoreCase))))
            {
                reason = string.IsNullOrEmpty(normalizedPath)
                    ? $"Optimizer binary family '{cleanName}' protected (fail-closed: incomplete path identity)."
                    : $"Optimizer binary family '{cleanName}' ({normalizedPath}).";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Task F: Evaluates whether a process is the optimizer itself, an optimizer host/service binary,
        /// or a verified descendant (e.g. child msedgewebview2 renderer) of an optimizer host.
        /// Validates parent.StartTime <= child.StartTime + 2s on every tree hop to block PID reuse spoofing.
        /// </summary>
        public static bool IsSelfOrVerifiedDescendant(
            ProcessIdentityDto? process,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree,
            out string reason)
        {
            reason = string.Empty;
            if (process == null)
            {
                reason = "Self-protection fail-closed: null process identity.";
                return true;
            }

            if (IsSelfOrOptimizerHost(process, out reason))
            {
                return true;
            }

            if (process.IsVerifiedSelfOrDescendant)
            {
                reason = $"Verified descendant of optimizer host ({process.ParentProcessName}, PID {process.ParentProcessId}).";
                return true;
            }

            if (processTree != null && process.ParentProcessId > 4)
            {
                var currentChild = process;
                int depth = 0;
                var visited = new HashSet<int> { process.ProcessId };

                while (currentChild.ParentProcessId > 4 && depth < 12 && visited.Add(currentChild.ParentProcessId))
                {
                    if (!processTree.TryGetValue(currentChild.ParentProcessId, out var parentProc))
                        break;

                    if (!ProcessIdentitySafety.IsVerifiedParentChild(parentProc, currentChild))
                        break;

                    if (IsSelfOrOptimizerHost(parentProc, out string parentReason))
                    {
                        reason = $"Verified descendant of optimizer host '{parentProc.ProcessName}' (PID {parentProc.ProcessId}): {parentReason}";
                        return true;
                    }

                    currentChild = parentProc;
                    depth++;
                }
            }

            return false;
        }

        public static bool IsShieldedUserApplication(string? processName)
        {
            string cleanName = NormalizeProcessName(processName);
            return !string.IsNullOrEmpty(cleanName) && ShieldedUserApplications.Contains(cleanName);
        }

        /// <summary>
        /// Task E: Walks verified parent-child links (validating parent.StartTime <= child.StartTime + 2s)
        /// to determine whether a process is a verified descendant of a ShieldedUserApplication.
        /// </summary>
        public static bool TryFindVerifiedShieldedAncestor(
            ProcessIdentityDto? process,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree,
            out ProcessIdentityDto? shieldedAncestor)
        {
            shieldedAncestor = null;
            if (process == null || processTree == null || process.ParentProcessId <= 4)
                return false;

            var currentChild = process;
            int depth = 0;
            var visited = new HashSet<int> { process.ProcessId };

            while (currentChild.ParentProcessId > 4 && depth < 12 && visited.Add(currentChild.ParentProcessId))
            {
                if (!processTree.TryGetValue(currentChild.ParentProcessId, out var parentProc))
                    break;

                if (!ProcessIdentitySafety.IsVerifiedParentChild(parentProc, currentChild))
                    break;

                if (IsShieldedUserApplication(parentProc.ProcessName))
                {
                    shieldedAncestor = parentProc;
                    return true;
                }

                currentChild = parentProc;
                depth++;
            }

            return false;
        }
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
            // Input & Peripherals (Logitech, Razer, Corsair, Mice, Keyboards, Stream Decks, Tablets)
            "LogiRegistryService", "lghub_agent", "lghub", "lghub_system_tray", "lghub_updater", "LCore", "LogitechGHub",
            "logioptionsplus", "logioptionsplus_agent", "logioptionsplus_updater", "LogiBolt", "LogiOptions", "LogiOptionsMgr",
            "logi_lamparray_service", "LGHUBConfigService", "LogiBoltService", "LogiOptionsPlusService",
            "Razer Synapse Service", "Razer Synapse 3", "Razer Synapse", "RazerCentralService", "RazerCentral",
            "RzActionEngine", "RzSDKServer", "RzChromaStreamServer", "RzChromaSDKHost", "RzDeviceEngine", "RazerCortex",
            "iCUE", "CorsairService", "CorsairLLAService", "Wacom_Tablet", "Wacom_TabletUser", "StreamDeck",
            "obs64", "obs32", "streamlabs obs", "Streamlabs Desktop", "WaveLink", "Voicemeeter"
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

        // 7. Shielded User Applications (Existing-App Rule: Never kill/block/uninstall main applications)
        private static readonly HashSet<string> ShieldedUserApplications = new(StringComparer.OrdinalIgnoreCase)
        {
            // Web Browsers
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "waterfox", "librewolf", "tor",
            // Communication & Chat
            "discord", "slack", "teams", "msteams", "telegram", "whatsapp", "signal", "zoom", "skype",
            // Development & IDE Tooling
            "antigravity", "code", "devenv", "rider64",
            // Creative Suites & Video/Audio
            "resolve", "premiere", "afterfx", "photoshop", "illustrator", "indesign", "audition",
            "adobe media encoder", "blender", "cinema 4d", "maya", "3dsmax", "houdini", "unrealengine",
            "unity", "obs64", "obs32", "streamlabs obs", "handbrake", "ffmpeg",
            // Android Emulators & VMs
            "bluestacks", "hd-player", "msi app player", "nox", "ldplayer", "memu", "vboxheadless", "virtualbox",
            // Game Platforms & Clients
            "steam", "epicgameslauncher", "origin", "eadesktop", "battle.net", "galaxyclient", "riotclientservices"
        };

        private static readonly HashSet<string> StrictlyProtectedServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "SecurityHealthService", "mpssvc", "AudioSrv", "AudioEndpointBuilder",
            "netprofm", "Dhcp", "Dnscache", "EventLog", "EventSystem", "Power", "PlugPlay",
            "RpcSs", "RpcEptMapper", "DcomLaunch", "BrokerInfrastructure", "SystemEventsBroker",
            "TimeBrokerSvc", "Themes", "Winmgmt", "Schedule", "CryptSvc", "LanmanWorkstation",
            "LanmanServer", "ProfSvc", "gpsvc", "LSM", "FontCache", "StateRepository", "BFE",
            "CoreMessagingRegistrar", "InstallService", "KeyIso", "SamSs", "VaultSvc", "W32Time",
            "nvcontainer", "NVDisplay.Container", "AMDRSServ", "RtkAudioService64", "EasyAntiCheat",
            "BEService", "vgc"
        };

        public static bool IsStrictlyProtectedService(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName)) return false;
            if (StrictlyProtectedServices.Contains(serviceName) ||
                CoreSecurityAndInfrastructure.Contains(serviceName) ||
                HardwareDriverComponents.Contains(serviceName) ||
                AntiCheatEngines.Contains(serviceName))
            {
                return true;
            }

            if (serviceName.EndsWith("UpdaterService", StringComparison.OrdinalIgnoreCase) ||
                serviceName.EndsWith("Update", StringComparison.OrdinalIgnoreCase) ||
                serviceName.EndsWith("Updater", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ProtectedApplicationPolicy.Instance.IsServiceProtected(serviceName);
        }

        /// <summary>
        /// Evaluates whether a process is protected and should never be terminated automatically.
        /// </summary>
        public bool IsProcessProtected(
            ProcessIdentityDto process,
            bool isGamingActive,
            bool isEditingActive,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree = null)
        {
            if (process == null) return true;

            string name = process.ProcessName.Trim();
            if (string.IsNullOrEmpty(name)) return true;

            // Self protection (Error Optimizer, ERROR OPTIMIZER PROTOTYPE.exe, current PID, and verified descendants)
            if (IsSelfOrVerifiedDescendant(process, processTree, out _))
            {
                return true;
            }

            if (name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Verified descendant of a Shielded User Application (e.g., Antigravity, VS Code, Chrome, IDE helpers)
            if (process.IsShieldedDescendant || TryFindVerifiedShieldedAncestor(process, processTree, out _))
            {
                return true;
            }

            // System PID protection
            if (process.ProcessId <= 4) return true;

            // V4 Protected Application / Service Exclusion Policy (Creative/Editing, Remote Access, Media, VPN, Gaming/Steam, Lenovo Vantage)
            if (ProtectedApplicationPolicy.Instance.IsProcessProtected(process, processTree))
            {
                return true;
            }

            // Check name-based protection (Kernel, Infrastructure, Drivers, Anti-Cheat, Workload)
            if (IsProtectedProcess(name, isGamingActive, isEditingActive)) return true;

            // Existing-App Protection: Main user applications are protected
            if (ShieldedUserApplications.Contains(name))
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

        /// <summary>
        /// Evaluates whether a process by name is protected under current or default policy.
        /// </summary>
        public bool IsProtectedProcess(string processName, bool isGamingActive = false, bool isEditingActive = false)
        {
            if (string.IsNullOrEmpty(processName)) return true;
            string name = processName.Trim();

            string cleanName = NormalizeProcessName(name);
            if (OptimizerSelfProcessNames.Contains(cleanName) ||
                cleanName.StartsWith("ERROR OPTIMIZER", StringComparison.OrdinalIgnoreCase) ||
                cleanName.StartsWith("BiosOptimizer", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (KernelAndSystemSubsystems.Contains(name)) return true;
            if (CoreSecurityAndInfrastructure.Contains(name)) return true;
            if (HardwareDriverComponents.Contains(name)) return true;
            if (AntiCheatEngines.Contains(name)) return true;
            if (ShieldedUserApplications.Contains(name)) return true;
            if (ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(name).IsProtected) return true;
            if (isGamingActive && GamePlatforms.Contains(name)) return true;
            if (isEditingActive && CreativeWorkloadSuites.Contains(name)) return true;

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
        public ProcessRisk EvaluateRisk(
            ProcessIdentityDto process,
            bool isGamingActive,
            bool isEditingActive,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree = null)
        {
            if (process.ProcessId <= 4 || KernelAndSystemSubsystems.Contains(process.ProcessName))
                return ProcessRisk.CRITICAL_SYSTEM;

            if (CoreSecurityAndInfrastructure.Contains(process.ProcessName) ||
                HardwareDriverComponents.Contains(process.ProcessName) ||
                AntiCheatEngines.Contains(process.ProcessName))
                return ProcessRisk.ELEVATED_DRIVER;

            var protectedMatch = ProtectedApplicationPolicy.Instance.EvaluateProcess(process, processTree);
            if (protectedMatch.IsProtected)
                return ProcessRisk.WORKLOAD_DEPENDENCY;

            if (ShieldedUserApplications.Contains(process.ProcessName))
                return ProcessRisk.LOW_BACKGROUND;

            if ((isGamingActive && GamePlatforms.Contains(process.ProcessName)) ||
                (isEditingActive && CreativeWorkloadSuites.Contains(process.ProcessName)))
                return ProcessRisk.WORKLOAD_DEPENDENCY;

            if (process.HasVisibleWindow || process.IsForeground)
                return ProcessRisk.LOW_BACKGROUND;

            return ProcessRisk.SAFE_TO_TERMINATE;
        }
    }
}
