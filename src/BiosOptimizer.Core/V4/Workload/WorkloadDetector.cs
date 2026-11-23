#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BiosOptimizer.Core.V4.Workload
{
    public enum ActiveWorkload
    {
        None,
        Game,
        VideoEditing,
        AudioEditing,
        ThreeDEditing,
        Rendering,
        Encoding,
        Emulator,
        Streaming,
        VirtualMachine,
        Benchmark
    }

    public sealed class WorkloadDetectionReport
    {
        public List<ActiveWorkload> ActiveWorkloads { get; set; } = new();
        public Dictionary<ActiveWorkload, List<string>> MatchedProcesses { get; set; } = new();
        public int TotalActiveCount => ActiveWorkloads.Count(w => w != ActiveWorkload.None);
    }

    /// <summary>
    /// Real-time detection and isolation of critical user workloads (Games, Creative Suites,
    /// Streaming, Encoders, Emulators, VMs). Any process in an active workload tree or vendor family
    /// is hard-shielded against termination.
    /// </summary>
    public class WorkloadDetector
    {
        private static readonly Lazy<WorkloadDetector> _instance = new(() => new WorkloadDetector());
        public static WorkloadDetector Instance => _instance.Value;

        private readonly Dictionary<ActiveWorkload, List<string>> _signatures = new();

        public WorkloadDetector(string? signaturesPath = null)
        {
            LoadSignatures(signaturesPath);
        }

        private void LoadSignatures(string? signaturesPath)
        {
            // Seed defaults first
            SeedDefaults();

            try
            {
                string path = signaturesPath ?? FindSignaturesPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (Enum.TryParse<ActiveWorkload>(prop.Name, true, out var wl))
                        {
                            if (prop.Value.TryGetProperty("processKeywords", out var kwArr))
                            {
                                var list = new List<string>();
                                foreach (var kw in kwArr.EnumerateArray())
                                {
                                    list.Add(kw.GetString()!);
                                }
                                _signatures[wl] = list;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void SeedDefaults()
        {
            _signatures[ActiveWorkload.Game] = new() { "steam", "epicgames", "battle.net", "riotclient", "gog", "upc", "gamingservices" };
            _signatures[ActiveWorkload.VideoEditing] = new() { "premiere", "resolve", "vegas", "filmora", "capcut", "avid", "hitfilm", "afterfx" };
            _signatures[ActiveWorkload.AudioEditing] = new() { "audacity", "reaper", "ableton", "fl64", "fl", "cubase", "nuendo", "protools", "studioapp", "bitwig", "reason" };
            _signatures[ActiveWorkload.ThreeDEditing] = new() { "blender", "maya", "3dsmax", "cinema4d", "houdini", "zbrush", "modo", "sketchup", "unrealeditor", "unity" };
            _signatures[ActiveWorkload.Rendering] = new() { "render", "arnold", "vray", "redshift", "corona", "cycles", "octane" };
            _signatures[ActiveWorkload.Encoding] = new() { "handbrake", "ffmpeg", "x264", "x265", "amecommand", "staxrip", "shanaencoder" };
            _signatures[ActiveWorkload.Streaming] = new() { "obs64", "obs32", "obs-browser-page", "streamlabs", "xsplit", "twitchstudio", "restream", "meld", "nvbroadcast", "nvidia share", "relive", "discord" };
            _signatures[ActiveWorkload.Emulator] = new() { "hd-player", "bstksvc", "bluestacksservices", "dnplayer", "nox", "memu", "gameloop", "mumu", "genymotion" };
            _signatures[ActiveWorkload.VirtualMachine] = new() { "vmware-vmx", "vboxheadless", "virtualbox", "vmwp", "parallels", "qemu" };
            _signatures[ActiveWorkload.Benchmark] = new() { "3dmark", "cinebench", "geekbench", "passmark", "unigine", "furmark", "occt" };
        }

        private static string FindSignaturesPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(baseDir, "data", "workload_signatures.json");
            if (File.Exists(p1)) return p1;

            string cwd = Directory.GetCurrentDirectory();
            string p2 = Path.Combine(cwd, "data", "workload_signatures.json");
            if (File.Exists(p2)) return p2;

            return p1;
        }

        public WorkloadDetectionReport DetectWorkloads(IEnumerable<string>? runningProcessNames = null)
        {
            var report = new WorkloadDetectionReport();
            var processes = runningProcessNames != null
                ? runningProcessNames.ToList()
                : Process.GetProcesses().Select(p => p.ProcessName).ToList();

            foreach (var kvp in _signatures)
            {
                var matched = new List<string>();
                foreach (var proc in processes)
                {
                    string pLower = proc.ToLowerInvariant();
                    if (kvp.Value.Any(kw => pLower.Contains(kw.ToLowerInvariant())))
                    {
                        matched.Add(proc);
                    }
                }

                if (matched.Count > 0)
                {
                    report.ActiveWorkloads.Add(kvp.Key);
                    report.MatchedProcesses[kvp.Key] = matched;
                }
            }

            if (report.ActiveWorkloads.Count == 0)
            {
                report.ActiveWorkloads.Add(ActiveWorkload.None);
            }

            return report;
        }

        public bool IsWorkloadActive(ActiveWorkload workload, IEnumerable<string>? runningProcessNames = null)
        {
            if (workload == ActiveWorkload.None) return false;
            var report = DetectWorkloads(runningProcessNames);
            return report.ActiveWorkloads.Contains(workload);
        }

        public bool IsProcessInWorkloadFamily(string processName, ActiveWorkload activeWorkload)
        {
            if (string.IsNullOrWhiteSpace(processName) || activeWorkload == ActiveWorkload.None) return false;

            if (_signatures.TryGetValue(activeWorkload, out var keywords))
            {
                string pLower = processName.ToLowerInvariant();
                return keywords.Any(kw => pLower.Contains(kw.ToLowerInvariant()));
            }

            return false;
        }
    }
}
