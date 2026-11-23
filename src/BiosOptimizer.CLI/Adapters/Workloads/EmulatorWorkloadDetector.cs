using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.CLI.Adapters.Workloads;

public class EmulatorWorkloadDetector : IWorkloadDetector
{
    public string WorkloadType => "Emulator";

    private static readonly string[] EmulatorProcessNames = new[]
    {
        "HD-Player",        // BlueStacks 5 & MSI App Player
        "dnplayer",         // LDPlayer
        "LdBoxHeadless",    // LDPlayer Box Headless
        "Nox",              // NoxPlayer
        "NoxVMHandle",      // NoxPlayer VM Handle
        "MEmu",             // MEmu Play
        "MEmuHeadless",     // MEmu Headless
        "MuMuPlayer",       // MuMu Player
        "MuMuNxDevice",     // MuMu NX Device
        "NemuPlayer"        // Netease MuMu
    };

    public InstalledWorkload? DetectInstallation()
    {
        // 1. Check for BlueStacks 5
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\BlueStacks_nxt");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "HD-Player.exe")))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = WorkloadType,
                        DisplayName = "BlueStacks 5 Android Emulator",
                        InstallationPath = installDir,
                        ExecutablePath = Path.Combine(installDir, "HD-Player.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 2. Check for MSI App Player
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MSI App Player");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "HD-Player.exe")))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = WorkloadType,
                        DisplayName = "MSI App Player Android Emulator",
                        InstallationPath = installDir,
                        ExecutablePath = Path.Combine(installDir, "HD-Player.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 3. Check for LDPlayer 9 / 4
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ChangZhi\ldplayer9") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\ChangZhi\ldplayer9") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ChangZhi\ldplayer") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\ChangZhi\ldplayer");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "dnplayer.exe")))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = WorkloadType,
                        DisplayName = "LDPlayer Android Emulator",
                        InstallationPath = installDir,
                        ExecutablePath = Path.Combine(installDir, "dnplayer.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 4. Check for NoxPlayer
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\BigNox\Nox") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\BigNox\Nox");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "Nox.exe")))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = WorkloadType,
                        DisplayName = "NoxPlayer Android Emulator",
                        InstallationPath = installDir,
                        ExecutablePath = Path.Combine(installDir, "Nox.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 5. Check for MEmu
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microvirt\MEmu");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "MEmu.exe")))
                {
                    return new InstalledWorkload
                    {
                        WorkloadType = WorkloadType,
                        DisplayName = "MEmu Play Android Emulator",
                        InstallationPath = installDir,
                        ExecutablePath = Path.Combine(installDir, "MEmu.exe"),
                        Detected = true
                    };
                }
            }
        }
        catch { }

        // 6. Check for MuMu Player
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Netease\MuMuPlayer") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Netease\MuMu");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installDir))
                {
                    string exePath = Path.Combine(installDir, "MuMuPlayer.exe");
                    if (!File.Exists(exePath)) exePath = Path.Combine(installDir, "NemuPlayer.exe");
                    if (File.Exists(exePath))
                    {
                        return new InstalledWorkload
                        {
                            WorkloadType = WorkloadType,
                            DisplayName = "MuMu Player Android Emulator",
                            InstallationPath = installDir,
                            ExecutablePath = exePath,
                            Detected = true
                        };
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public bool IsRunning()
    {
        foreach (var name in EmulatorProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    foreach (var p in procs) p.Dispose();
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    public bool IsFreeFireRunning()
    {
        foreach (var name in EmulatorProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    try
                    {
                        string title = p.MainWindowTitle;
                        if (!string.IsNullOrEmpty(title) &&
                            (title.Contains("Free Fire", StringComparison.OrdinalIgnoreCase) ||
                             title.Contains("FreeFire", StringComparison.OrdinalIgnoreCase) ||
                             title.Contains("Garena", StringComparison.OrdinalIgnoreCase)))
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
        }
        return false;
    }

    public string GetRunningProcessPath()
    {
        foreach (var name in EmulatorProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    try
                    {
                        string path = procs[0].MainModule?.FileName ?? string.Empty;
                        foreach (var p in procs) p.Dispose();
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                    catch
                    {
                        foreach (var p in procs) p.Dispose();
                    }
                }
            }
            catch { }
        }
        return string.Empty;
    }
}
