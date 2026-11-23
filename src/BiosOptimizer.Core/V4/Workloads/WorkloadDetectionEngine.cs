#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BiosOptimizer.Core.V4.Workloads
{
    public class WorkloadStatus
    {
        public bool IsGamingDetected { get; set; }
        public bool IsEditingDetected { get; set; }
        public bool IsStreamingDetected { get; set; }
        public bool IsEmulatorDetected { get; set; }
        public BiosOptimizer.IPC.Contracts.WorkloadProfileType ActiveProfile { get; set; } = BiosOptimizer.IPC.Contracts.WorkloadProfileType.GeneralPerformance;
        public bool IsGeneralPerformance => !IsGamingDetected && !IsEditingDetected && !IsStreamingDetected && !IsEmulatorDetected;
        public List<string> ActiveWorkloadNames { get; set; } = new();
        public List<string> ActiveWorkloadProcesses { get; set; } = new();
        public List<string> ActiveShieldDescriptions { get; set; } = new();
    }

    /// <summary>
    /// Unified Workload Detection Engine for Gaming, Creative Editing/Rendering, and Android Emulators.
    /// Dynamically monitors running processes and system state to shield workload-critical components.
    /// </summary>
    public class WorkloadDetectionEngine
    {
        private static readonly Dictionary<string, string> KnownGameLaunchers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["steam"] = "Steam Gaming Platform",
            ["epicgameslauncher"] = "Epic Games Launcher",
            ["battle.net"] = "Battle.net Client",
            ["eadesktop"] = "EA Desktop App",
            ["origin"] = "EA Origin",
            ["ubisoftconnect"] = "Ubisoft Connect",
            ["upc"] = "Ubisoft Connect Helper",
            ["galaxyclient"] = "GOG Galaxy Client",
            ["riotclientservices"] = "Riot Games Client",
            // Gaming Communication & Overlays
            ["discord"] = "Discord Voice & Overlay Client",
            ["teamspeak"] = "TeamSpeak Voice Client",
            ["ts3client_win64"] = "TeamSpeak 3 Client",
            ["guilded"] = "Guilded Voice & Community Client",
            ["mumble"] = "Mumble Voice Client",
            ["overwolf"] = "Overwolf Overlay Framework",
            ["crosshairx"] = "Crosshair X Overlay",
            ["rtss"] = "RivaTuner Statistics Server",
            ["msiafterburner"] = "MSI Afterburner Hardware Monitor"
        };

        private static readonly Dictionary<string, string> KnownAntiCheats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["easyanticheat"] = "Easy Anti-Cheat",
            ["easyanticheat_eos"] = "Easy Anti-Cheat EOS",
            ["beservice"] = "BattlEye Anti-Cheat Service",
            ["vgc"] = "Riot Vanguard Anti-Cheat Service",
            ["vgtray"] = "Riot Vanguard System Tray",
            ["faceitservice"] = "FACEIT Anti-Cheat Service",
            ["eaanticheat.gameservice"] = "EA Anti-Cheat Service"
        };

        private static readonly Dictionary<string, string> KnownEmulators = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HD-Player"] = "BlueStacks / MSI App Player 5 (Android Virtualization Engine)",
            ["HD-Adb"] = "BlueStacks ADB Bridge",
            ["BstkSVC"] = "BlueStacks Service Host",
            ["BlueStacks"] = "BlueStacks Host Application",
            ["MSIAppPlayer"] = "MSI App Player Host",
            ["BlueStacksWeb"] = "BlueStacks Web Helper",
            ["dnplayer"] = "LDPlayer Android Emulator",
            ["LdBoxHeadless"] = "LDPlayer Hypervisor Engine",
            ["Nox"] = "NoxPlayer Android Emulator",
            ["NoxVMHandle"] = "NoxPlayer Virtualization Handler",
            ["MEmu"] = "MEmu Play Android Emulator",
            ["MEmuHeadless"] = "MEmu Hypervisor Engine",
            ["mumuplayer"] = "MuMu Player Android Emulator",
            ["mumunxdevice"] = "MuMu Player Virtualization Device",
            ["ldpboxheadless"] = "LDPlayer Box Headless Engine",
            ["crosvm"] = "Google Play Games on PC Hypervisor Engine",
            ["ServiceVM"] = "Google Play Games on PC Virtualization Service",
            ["Bootstrapper"] = "Google Play Games on PC Bootstrapper",
            ["FreeFire"] = "Garena Free Fire Workload",
            ["FreeFireMAX"] = "Garena Free Fire MAX Workload",
            ["com.dts.freefireth"] = "Garena Free Fire (Android Execution)",
            ["com.dts.freefiremax"] = "Garena Free Fire MAX (Android Execution)"
        };

        private static readonly Dictionary<string, string> KnownCreativeSuites = new(StringComparer.OrdinalIgnoreCase)
        {
            // Adobe
            ["adobe premiere pro"] = "Adobe Premiere Pro (Video Editing)",
            ["premiere"] = "Adobe Premiere Pro",
            ["afterfx"] = "Adobe After Effects (Motion Graphics & VFX)",
            ["photoshop"] = "Adobe Photoshop (Image Editing)",
            ["illustrator"] = "Adobe Illustrator (Vector Graphics)",
            ["audition"] = "Adobe Audition (Audio Production)",
            ["adobe media encoder"] = "Adobe Media Encoder (Video Rendering)",
            ["ame"] = "Adobe Media Encoder Worker",
            ["dynamiclinkmediaserver"] = "Adobe Dynamic Link Media Server",
            ["adobe cef helper"] = "Adobe Creative Cloud CEF Helper",
            ["adobelive"] = "Adobe Live",
            // Blackmagic DaVinci Resolve
            ["resolve"] = "DaVinci Resolve (Color Grading & NLE)",
            ["fuscript"] = "DaVinci Resolve Fusion Scripting",
            ["dpdecoder"] = "DaVinci Resolve Video Decoder Engine",
            // 3D & Animation
            ["blender"] = "Blender 3D Suite",
            ["cinema 4d"] = "Maxon Cinema 4D",
            ["3dsmax"] = "Autodesk 3ds Max",
            ["maya"] = "Autodesk Maya",
            ["houdini"] = "SideFX Houdini",
            ["unrealengine"] = "Unreal Engine Editor",
            ["unity"] = "Unity Editor",
            // Streaming & Recording
            ["obs64"] = "OBS Studio (Broadcasting / Recording)",
            ["obs32"] = "OBS Studio (32-bit)",
            ["streamlabs obs"] = "Streamlabs Desktop",
            ["handbrake"] = "HandBrake Video Transcoder",
            // Topaz AI
            ["topaz video ai"] = "Topaz Video AI Upscaler",
            ["topaz photo ai"] = "Topaz Photo AI",
            // Streaming & Broadcasting Extensions
            ["twitchstudio"] = "Twitch Studio Broadcaster",
            ["xsplit"] = "XSplit Broadcaster",
            ["vmix64"] = "vMix Live Production",
            ["elgato capture"] = "Elgato 4K Capture Utility",
            ["camtasia"] = "TechSmith Camtasia Studio",
            ["audacity"] = "Audacity Audio Editor",
            ["voicemeeter"] = "VB-Audio VoiceMeeter Mixer",
            ["voicemeeter8"] = "VoiceMeeter Banana / Potato",
            ["voicemeeterpro"] = "VoiceMeeter Pro Mixer",
            // Additional Editors & Transcoders
            ["vegas"] = "MAGIX VEGAS Pro Video Editor",
            ["vegas200"] = "MAGIX VEGAS Pro 20",
            ["shotcut"] = "Shotcut Open Source Video Editor",
            ["capcut"] = "CapCut Video Editor Desktop",
            ["kdenlive"] = "Kdenlive Video Editor",
            // Audio Production / DAWs
            ["ableton live suite"] = "Ableton Live DAW",
            ["ableton live 11 suite"] = "Ableton Live DAW",
            ["ableton live 12 suite"] = "Ableton Live DAW",
            ["fl64"] = "FL Studio (Digital Audio Workstation)",
            ["cubase"] = "Steinberg Cubase Pro",
            ["protools"] = "Avid Pro Tools",
            ["studio one"] = "PreSonus Studio One",
            ["reaper"] = "REAPER DAW"
        };

        /// <summary>
        /// Analyzes the running processes and returns a comprehensive workload status report.
        /// </summary>
        public WorkloadStatus DetectActiveWorkloads(IEnumerable<Process> runningProcesses)
        {
            var names = new List<string>();
            string? detectedSubGame = null;
            foreach (var proc in runningProcesses)
            {
                try
                {
                    names.Add(proc.ProcessName);
                    if (!string.IsNullOrEmpty(proc.MainWindowTitle))
                    {
                        if (proc.MainWindowTitle.Contains("Free Fire", StringComparison.OrdinalIgnoreCase) ||
                            proc.MainWindowTitle.Contains("Garena", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedSubGame = "Garena Free Fire (Running on Android Hypervisor)";
                        }
                    }
                }
                catch { }
            }
            var status = DetectActiveWorkloads(names);
            if (detectedSubGame != null)
            {
                status.IsGamingDetected = true;
                status.IsEmulatorDetected = true;
                if (!status.ActiveWorkloadNames.Contains(detectedSubGame))
                    status.ActiveWorkloadNames.Insert(0, detectedSubGame);
            }
            return status;
        }

        /// <summary>
        /// Analyzes a list of process names and returns a comprehensive workload status report.
        /// </summary>
        public WorkloadStatus DetectActiveWorkloads(IEnumerable<string> processNames)
        {
            var status = new WorkloadStatus();
            var activeProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pName in processNames)
            {
                if (string.IsNullOrWhiteSpace(pName)) continue;
                activeProcNames.Add(pName);

                // Check Gaming Launchers
                if (KnownGameLaunchers.TryGetValue(pName, out var launcherName))
                {
                    status.IsGamingDetected = true;
                    if (!status.ActiveWorkloadNames.Contains(launcherName))
                        status.ActiveWorkloadNames.Add(launcherName);
                    status.ActiveWorkloadProcesses.Add(pName);
                }

                // Check Anti-Cheat Engines
                if (KnownAntiCheats.TryGetValue(pName, out var antiCheatName))
                {
                    status.IsGamingDetected = true;
                    if (!status.ActiveWorkloadNames.Contains(antiCheatName))
                        status.ActiveWorkloadNames.Add(antiCheatName);
                    status.ActiveWorkloadProcesses.Add(pName);
                }

                // Check Android Emulators
                if (KnownEmulators.TryGetValue(pName, out var emulatorName))
                {
                    status.IsEmulatorDetected = true;
                    if (!status.ActiveWorkloadNames.Contains(emulatorName))
                        status.ActiveWorkloadNames.Add(emulatorName);
                    status.ActiveWorkloadProcesses.Add(pName);
                }

                // Check Creative / Editing / Streaming suites
                if (KnownCreativeSuites.TryGetValue(pName, out var creativeName))
                {
                    status.IsEditingDetected = true;
                    if (creativeName.Contains("OBS Studio", StringComparison.OrdinalIgnoreCase) ||
                        creativeName.Contains("Broadcasting", StringComparison.OrdinalIgnoreCase) ||
                        creativeName.Contains("Streamlabs", StringComparison.OrdinalIgnoreCase))
                    {
                        status.IsStreamingDetected = true;
                    }
                    if (!status.ActiveWorkloadNames.Contains(creativeName))
                        status.ActiveWorkloadNames.Add(creativeName);
                    status.ActiveWorkloadProcesses.Add(pName);
                }
            }

            // Build active shield descriptions
            if (status.IsGamingDetected)
            {
                status.ActiveShieldDescriptions.Add("Gaming Workload Shield Active (Protecting Launchers, Anti-Cheat, Audio Stack, Low-Latency Input)");
            }

            if (status.IsEmulatorDetected)
            {
                status.ActiveShieldDescriptions.Add("Android Emulator Workload Shield Active (Protecting Virtualization Hypervisor, Guest vCPUs, ADB Bridge, Long Quantum Scheduling)");
            }

            if (status.IsStreamingDetected)
            {
                status.ActiveShieldDescriptions.Add("Streaming & Broadcast Shield Active (Protecting OBS Studio, Audio Cables, Capture Card Pipelines)");
            }

            if (status.IsEditingDetected)
            {
                status.ActiveShieldDescriptions.Add("Creative/Editing Workload Shield Active (Protecting Render Pipelines, GPU Acceleration, Media Codecs, Licensing)");
            }

            // Resolve formal 5-Profile Workload Policy
            if (status.IsEmulatorDetected)
            {
                status.ActiveProfile = BiosOptimizer.IPC.Contracts.WorkloadProfileType.Emulator;
            }
            else if (status.IsStreamingDetected)
            {
                status.ActiveProfile = BiosOptimizer.IPC.Contracts.WorkloadProfileType.Streaming;
            }
            else if (status.IsEditingDetected)
            {
                status.ActiveProfile = BiosOptimizer.IPC.Contracts.WorkloadProfileType.VideoEditing;
            }
            else if (status.IsGamingDetected)
            {
                status.ActiveProfile = BiosOptimizer.IPC.Contracts.WorkloadProfileType.Gaming;
            }
            else
            {
                status.ActiveProfile = BiosOptimizer.IPC.Contracts.WorkloadProfileType.GeneralPerformance;
                status.ActiveShieldDescriptions.Add("General Performance Shield Active (Conservative Adaptive Prioritization, Preserving All Interactive Applications)");
            }

            return status;
        }

        /// <summary>
        /// Returns true if a specific process is a critical workload dependency.
        /// </summary>
        public bool IsWorkloadCritical(string processName, bool isGamingActive, bool isEditingActive, bool isEmulatorActive = false)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            if (isGamingActive && (KnownGameLaunchers.ContainsKey(processName) || KnownAntiCheats.ContainsKey(processName)))
                return true;

            if (isEmulatorActive && KnownEmulators.ContainsKey(processName))
                return true;

            if (isEditingActive && KnownCreativeSuites.ContainsKey(processName))
                return true;

            // Licensing / IPC brokers protected if editing session is running
            if (isEditingActive && (processName.Equals("AdobeIPCBroker", StringComparison.OrdinalIgnoreCase) ||
                                   processName.Equals("CCXProcess", StringComparison.OrdinalIgnoreCase) ||
                                   processName.Equals("CoreSync", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }
    }
}
