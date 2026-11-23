using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.CLI.Adapters.Workloads;

public class GameWorkloadDetector : IWorkloadDetector
{
    public string WorkloadType => "AAA Games";

    private static readonly HashSet<string> KnownGameExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT", "VALORANT-Win64-Shipping", "cs2", "csgo", "Cyberpunk2077",
        "FortniteClient-Win64-Shipping", "FortniteLauncher", "GTA5", "Overwatch",
        "ApexLegends", "r5apex", "League of Legends", "RobloxPlayerBeta",
        "GenshinImpact", "StarRail", "Minecraft.Windows", "javaw", "RocketLeague",
        "Dota2", "Destiny2", "RainbowSix", "RainbowSix_Vulkan", "eldenring",
        "ModernWarfare", "cod", "Warframe", "PUBG", "TslGame", "RDR2",
        "witcher3", "MonsterHunterWorld", "MonsterHunterRise", "ForzaHorizon5",
        "F1_23", "F1_24", "FIFA23", "FC24", "FC25", "BaldursGate3", "bg3", "bg3_dx11",
        "deadbydaylight", "RustClient", "SeaOfThieves", "Palworld-Win64-Shipping"
    };

    public InstalledWorkload? DetectInstallation()
    {
        // 1. Steam Detection
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key != null)
            {
                var installPath = key.GetValue("InstallPath")?.ToString();
                if (!string.IsNullOrEmpty(installPath) && File.Exists(Path.Combine(installPath, "steam.exe")))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = "Steam",
                        DisplayName = "Steam Gaming Platform",
                        InstallationPath = installPath,
                        ExecutablePath = Path.Combine(installPath, "steam.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 2. Epic Games Launcher Detection
        try
        {
            using var epicKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher") ??
                                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
            if (epicKey != null)
            {
                var appData = epicKey.GetValue("AppDataPath")?.ToString();
                if (!string.IsNullOrEmpty(appData) && Directory.Exists(appData))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = "EpicGames",
                        DisplayName = "Epic Games Launcher",
                        InstallationPath = appData,
                        ExecutablePath = Path.Combine(appData, "EpicGamesLauncher.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 3. Riot Games Detection
        try
        {
            using var riotKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Riot Games\InstallationPaths");
            if (riotKey != null)
            {
                var riotPath = riotKey.GetValue("RiotClient")?.ToString();
                if (!string.IsNullOrEmpty(riotPath) && File.Exists(riotPath))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = "RiotGames",
                        DisplayName = "Riot Games Client",
                        InstallationPath = Path.GetDirectoryName(riotPath) ?? "",
                        ExecutablePath = riotPath,
                        Detected = true
                    };
                }
            }
        }
        catch { }

        return null;
    }

    public bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    if (KnownGameExecutables.Contains(p.ProcessName))
                    {
                        return true;
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return false;
    }

    public string GetRunningProcessPath()
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    if (KnownGameExecutables.Contains(p.ProcessName))
                    {
                        return p.MainModule?.FileName ?? string.Empty;
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return string.Empty;
    }
}
