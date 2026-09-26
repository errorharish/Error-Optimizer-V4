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
        public List<string> ActiveWorkloadNames { get; set; } = new();
        public List<string> ActiveWorkloadProcesses { get; set; } = new();
        public List<string> ActiveShieldDescriptions { get; set; } = new();
    }

    /// <summary>
    /// Unified Workload Detection Engine for Gaming and Editing/Rendering.
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
            ["riotclientservices"] = "Riot Games Client"
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
            var status = new WorkloadStatus();
            var activeProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in runningProcesses)
            {
                try
                {
                    string pName = proc.ProcessName;
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

                    // Check Creative / Editing suites
                    if (KnownCreativeSuites.TryGetValue(pName, out var creativeName))
                    {
                        status.IsEditingDetected = true;
                        if (!status.ActiveWorkloadNames.Contains(creativeName))
                            status.ActiveWorkloadNames.Add(creativeName);
                        status.ActiveWorkloadProcesses.Add(pName);
                    }
                }
                catch { }
            }

            // Build active shield descriptions
            if (status.IsGamingDetected)
            {
                status.ActiveShieldDescriptions.Add("Gaming Workload Shield Active (Protecting Launchers, Anti-Cheat, Audio Stack, Low-Latency Input)");
            }

            if (status.IsEditingDetected)
            {
                status.ActiveShieldDescriptions.Add("Creative/Editing Workload Shield Active (Protecting Render Pipelines, GPU Acceleration, Media Codecs, Licensing)");
            }

            return status;
        }

        /// <summary>
        /// Returns true if a specific process is a critical workload dependency.
        /// </summary>
        public bool IsWorkloadCritical(string processName, bool isGamingActive, bool isEditingActive)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            if (isGamingActive && (KnownGameLaunchers.ContainsKey(processName) || KnownAntiCheats.ContainsKey(processName)))
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
