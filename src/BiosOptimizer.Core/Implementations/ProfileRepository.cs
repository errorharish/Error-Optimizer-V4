using System.Text.Json;
using System.Text.Json.Serialization;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.Implementations;

public class ProfileRepository : Interfaces.IProfileRepository
{
    private readonly string _profilesDirectory;

    public ProfileRepository(string profilesDirectory)
    {
        _profilesDirectory = profilesDirectory;
    }

    public static string NormalizeProfileId(string tierId)
    {
        if (string.IsNullOrWhiteSpace(tierId)) return "Normal";
        string trimmed = tierId.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("tierId", out var tProp) ||
                        doc.RootElement.TryGetProperty("TierId", out tProp) ||
                        doc.RootElement.TryGetProperty("tier", out tProp) ||
                        doc.RootElement.TryGetProperty("Tier", out tProp) ||
                        doc.RootElement.TryGetProperty("profile", out tProp) ||
                        doc.RootElement.TryGetProperty("Profile", out tProp) ||
                        doc.RootElement.TryGetProperty("profileId", out tProp) ||
                        doc.RootElement.TryGetProperty("ProfileId", out tProp) ||
                        doc.RootElement.TryGetProperty("id", out tProp) ||
                        doc.RootElement.TryGetProperty("Id", out tProp) ||
                        doc.RootElement.TryGetProperty("name", out tProp) ||
                        doc.RootElement.TryGetProperty("Name", out tProp))
                    {
                        var s = tProp.GetString();
                        if (!string.IsNullOrEmpty(s)) trimmed = s;
                    }
                }
            }
            catch { }
        }

        // If a file path was passed, isolate the file name without extension
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            try
            {
                trimmed = Path.GetFileNameWithoutExtension(trimmed);
            }
            catch { }
        }

        string compact = trimmed.Replace(" ", string.Empty, StringComparison.Ordinal)
                                .Replace("-", string.Empty, StringComparison.Ordinal)
                                .Replace("_", string.Empty, StringComparison.Ordinal)
                                .ToLowerInvariant();

        // Strip file extensions if still present
        if (compact.EndsWith(".json", StringComparison.Ordinal))
            compact = compact.Substring(0, compact.Length - 5);

        // Strip common prefixes
        if (compact.StartsWith("presets", StringComparison.Ordinal))
            compact = compact.Substring(7);
        else if (compact.StartsWith("preset", StringComparison.Ordinal))
            compact = compact.Substring(6);
        else if (compact.StartsWith("tier", StringComparison.Ordinal))
            compact = compact.Substring(4);

        // Strip common suffixes
        if (compact.EndsWith("optimization", StringComparison.Ordinal))
            compact = compact.Substring(0, compact.Length - 12);
        else if (compact.EndsWith("profile", StringComparison.Ordinal))
            compact = compact.Substring(0, compact.Length - 7);
        else if (compact.EndsWith("tier", StringComparison.Ordinal))
            compact = compact.Substring(0, compact.Length - 4);

        return compact switch
        {
            "normal" or "balanced" or "" => "Normal",
            "pro" => "Pro",
            "ultimate" => "Ultimate",
            "debloat" => "Debloat",
            "biossafe" or "bios" => "BiosSafe",
            "maxperformance" or "maximumperformance" or "maxperf" => "MaximumPerformance",
            "aaagaming" or "aaa" => "AaaGaming",
            "aftereffectsturbo" or "aftereffects" => "AfterEffectsTurbo",
            "emulatormax" or "emulator" => "EmulatorMax",
            "lowendrescue" or "lowend" => "LowEndRescue",
            "ramtest" => "RamTest",
            "topazai" or "topaz" => "TopazAI",
            "totalmax" or "total" => "TotalMax",
            _ => trimmed
        };
    }

    private static readonly HashSet<string> CanonicalTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Normal", "Pro", "Ultimate", "Debloat", "BiosSafe", "MaximumPerformance",
        "AaaGaming", "AfterEffectsTurbo", "EmulatorMax", "LowEndRescue", "RamTest", "TopazAI", "TotalMax"
    };

    public static ProfileDef GetCanonicalBuiltInProfile(string tierId)
    {
        string normalized = NormalizeProfileId(tierId);
        var profile = new ProfileDef
        {
            Id = normalized,
            DisplayName = $"{normalized} (Built-in Fallback)",
            Description = $"Explicit built-in baseline definition for {normalized} optimization profile.",
            TargetProcessReduction = new TargetProcessReduction { Min = 20, Max = 45 },
            ProfileOrigin = "BUILT_IN",
            IsBuiltInFallback = true,
            SourcePath = null,
            Entries = new List<OptimizationEntry>()
        };

        // Standard canonical baseline entries for recognized tiers
        switch (normalized)
        {
            case "Normal":
                profile.Entries.Add(new OptimizationEntry { Id = "normal.power.plan", DisplayName = "High Performance Power Plan", Category = "Power", Action = "PowerPlanOptimization", Target = "HighPerformance", Value = "Active", Risk = "Safe", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.power.throttling", DisplayName = "Disable Execution Power Throttling", Category = "Power", Action = "PowerPlanOptimization", Target = "PowerThrottling", Value = "Disabled", Risk = "Safe", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.input.mouse_accel", DisplayName = "Disable Mouse Acceleration", Category = "Input", Action = "SetRegistryValue", Target = "HKCU\\Control Panel\\Mouse\\MouseSpeed", Value = "0", Risk = "Safe", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.input.keyboard_rate", DisplayName = "Keyboard Repeat Rate Maximization", Category = "Input", Action = "SetRegistryValue", Target = "HKCU\\Control Panel\\Keyboard\\KeyboardSpeed", Value = "31", Risk = "Safe", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.ntfs.8dot3", DisplayName = "Disable NTFS 8.3 Short Name Creation", Category = "Storage", Action = "SetRegistryValue", Target = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\NtfsDisable8dot3NameCreation", Value = 1, Risk = "Safe", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.service.diagtrack", DisplayName = "Connected User Experiences and Telemetry", Category = "Service", Action = "SetServiceStartup", Target = "DiagTrack", Value = "Disabled", Risk = "Low", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.service.dmwappush", DisplayName = "WAP Push Routing Service", Category = "Service", Action = "SetServiceStartup", Target = "dmwappushservice", Value = "Disabled", Risk = "Low", SourceProfile = "Normal" });
                profile.Entries.Add(new OptimizationEntry { Id = "normal.privacy.advertising_id", DisplayName = "Disable Advertising ID", Category = "Privacy", Action = "SetRegistryValue", Target = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo\\Enabled", Value = 0, Risk = "Safe", SourceProfile = "Normal" });
                break;

            case "Pro":
                profile.Extends = new List<string> { "Normal" };
                profile.Entries.Add(new OptimizationEntry { Id = "pro.service.sysmain", DisplayName = "SysMain Service Tuning", Category = "Service", Action = "SetServiceStartup", Target = "SysMain", Value = "Disabled", Risk = "Medium", SourceProfile = "Pro" });
                profile.Entries.Add(new OptimizationEntry { Id = "pro.network.autotuning", DisplayName = "TCP Window Auto-Tuning Optimization", Category = "Network", Action = "SetRegistryValue", Target = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\GlobalMaxTcpWindowSize", Value = 65535, Risk = "Safe", SourceProfile = "Pro" });
                break;

            case "Ultimate":
                profile.Extends = new List<string> { "Pro" };
                profile.Entries.Add(new OptimizationEntry { Id = "ultimate.gpu.tdr", DisplayName = "GPU TDR Recovery Latency Extension", Category = "Gpu", Action = "SetRegistryValue", Target = "HKLM\\System\\CurrentControlSet\\Control\\GraphicsDrivers\\TdrDelay", Value = 8, Risk = "Safe", SourceProfile = "Ultimate" });
                break;

            case "Debloat":
                profile.Entries.Add(new OptimizationEntry { Id = "debloat.appx.consumer", DisplayName = "Consumer Appx Cleanup", Category = "Bloatware", Action = "RemoveAppxPackage", Target = "Microsoft.BingNews", Value = "Removed", Risk = "Low", SourceProfile = "Debloat" });
                profile.Entries.Add(new OptimizationEntry { Id = "debloat.privacy.telemetry_keys", DisplayName = "Windows Telemetry Policy Keys", Category = "Privacy", Action = "SetRegistryValue", Target = "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection\\AllowTelemetry", Value = 0, Risk = "Safe", SourceProfile = "Debloat" });
                break;

            case "BiosSafe":
                profile.Entries.Add(new OptimizationEntry { Id = "biossafe.sys.hpet", DisplayName = "Verify High Precision Event Timer", Category = "System", Action = "SetRegistryValue", Target = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Kernel\\GlobalTimerResolutionRequests", Value = 1, Risk = "Safe", SourceProfile = "BiosSafe" });
                break;

            case "MaximumPerformance":
                profile.Extends = new List<string> { "Ultimate" };
                profile.Entries.Add(new OptimizationEntry { Id = "maxperf.power.scheme", DisplayName = "Ultimate Performance Power Scheme", Category = "Power", Action = "PowerPlanOptimization", Target = "UltimatePerformance", Value = "Active", Risk = "Low", SourceProfile = "MaximumPerformance" });
                break;

            case "AaaGaming":
                profile.Extends = new List<string> { "Pro" };
                profile.Entries.Add(new OptimizationEntry { Id = "aaa.gpu.latency", DisplayName = "Gaming GPU Scheduler Low Latency Boost", Category = "Gpu", Action = "SetRegistryValue", Target = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\\Scheduler\\EnablePreemption", Value = 0, Risk = "Low", SourceProfile = "AaaGaming" });
                profile.Entries.Add(new OptimizationEntry { Id = "aaa.gamebar.fse", DisplayName = "Disable Fullscreen Optimizations Latency Gate", Category = "Gaming", Action = "SetRegistryValue", Target = "HKCU\\System\\GameConfigStore\\GameDVR_FSEBehaviorMode", Value = 2, Risk = "Safe", SourceProfile = "AaaGaming" });
                break;

            case "AfterEffectsTurbo":
                profile.Extends = new List<string> { "Pro" };
                profile.Entries.Add(new OptimizationEntry { Id = "ae.sys.paging_exec", DisplayName = "Kernel Paging Executive Working Set Locking", Category = "Memory", Action = "SetRegistryValue", Target = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management\\DisablePagingExecutive", Value = 1, Risk = "Safe", SourceProfile = "AfterEffectsTurbo" });
                profile.Entries.Add(new OptimizationEntry { Id = "ae.io.priority", DisplayName = "Foreground I/O Boost for High-Throughput Media", Category = "Storage", Action = "SetRegistryValue", Target = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\NetworkThrottlingIndex", Value = -1, Risk = "Safe", SourceProfile = "AfterEffectsTurbo" });
                break;

            case "EmulatorMax":
                profile.Extends = new List<string> { "Normal" };
                profile.Entries.Add(new OptimizationEntry { Id = "emu.timer.res", DisplayName = "Global High-Resolution Multimedia Timer (0.5ms)", Category = "System", Action = "SetRegistryValue", Target = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Kernel\\GlobalTimerResolutionRequests", Value = 1, Risk = "Safe", SourceProfile = "EmulatorMax" });
                profile.Entries.Add(new OptimizationEntry { Id = "emu.power.throttling", DisplayName = "Disable Execution Power Throttling for Virtualization Threads", Category = "Power", Action = "PowerPlanOptimization", Target = "PowerThrottling", Value = "Disabled", Risk = "Safe", SourceProfile = "EmulatorMax" });
                break;

            case "LowEndRescue":
                profile.Entries.Add(new OptimizationEntry { Id = "lowend.anim.disable", DisplayName = "Disable Windows Animation and Visual Burden", Category = "System", Action = "SetRegistryValue", Target = "HKCU\\Control Panel\\Desktop\\WindowMetrics\\MinAnimate", Value = "0", Risk = "Safe", SourceProfile = "LowEndRescue" });
                profile.Entries.Add(new OptimizationEntry { Id = "lowend.power.plan", DisplayName = "Lightweight High Performance Power Scheme", Category = "Power", Action = "PowerPlanOptimization", Target = "HighPerformance", Value = "Active", Risk = "Safe", SourceProfile = "LowEndRescue" });
                break;

            case "RamTest":
                profile.Entries.Add(new OptimizationEntry { Id = "ramtest.standby.purge", DisplayName = "Purge Standby Cache Working Set", Category = "Memory", Action = "PurgeStandbyList", Target = "MemoryCache", Value = "Purged", Risk = "Safe", SourceProfile = "RamTest" });
                break;

            case "TopazAI":
                profile.Extends = new List<string> { "Ultimate" };
                profile.Entries.Add(new OptimizationEntry { Id = "topaz.gpu.tdr", DisplayName = "Extended Compute TDR Latency for Neural Upscaling", Category = "Gpu", Action = "SetRegistryValue", Target = "HKLM\\System\\CurrentControlSet\\Control\\GraphicsDrivers\\TdrDelay", Value = 15, Risk = "Safe", SourceProfile = "TopazAI" });
                break;

            case "TotalMax":
                profile.Extends = new List<string> { "MaximumPerformance" };
                profile.Entries.Add(new OptimizationEntry { Id = "totalmax.core.unpark", DisplayName = "All CPU Core Unparking Minimum State (100%)", Category = "Cpu", Action = "PowerPlanOptimization", Target = "CoreParking", Value = "Unparked", Risk = "Low", SourceProfile = "TotalMax" });
                break;
        }

        return profile;
    }

    public ProfileDef? LoadProfile(string tierId)
    {
        string normalized = NormalizeProfileId(tierId);

        var candidateDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_profilesDirectory))
        {
            candidateDirs.Add(_profilesDirectory);
            candidateDirs.Add(Path.Combine(_profilesDirectory, "presets"));
        }

        string baseDir = AppContext.BaseDirectory;
        candidateDirs.Add(Path.Combine(baseDir, "profiles"));
        candidateDirs.Add(Path.Combine(baseDir, "profiles", "presets"));
        candidateDirs.Add(Path.Combine(baseDir, "..", "profiles"));
        candidateDirs.Add(Path.Combine(baseDir, "..", "profiles", "presets"));
        candidateDirs.Add(Path.Combine(baseDir, "core", "profiles"));
        candidateDirs.Add(Path.Combine(baseDir, "core", "profiles", "presets"));

        string appDomainDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.Equals(appDomainDir, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            candidateDirs.Add(Path.Combine(appDomainDir, "profiles"));
            candidateDirs.Add(Path.Combine(appDomainDir, "profiles", "presets"));
            candidateDirs.Add(Path.Combine(appDomainDir, "..", "profiles"));
            candidateDirs.Add(Path.Combine(appDomainDir, "core", "profiles"));
        }

        candidateDirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "profiles"));
        candidateDirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "profiles", "presets"));
        candidateDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer", "profiles"));
        candidateDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Error Optimizer", "profiles"));
        candidateDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Error Optimizer", "profiles"));
        candidateDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Error Optimizer", "core", "profiles"));

        string? resolvedPath = null;
        string[] fileVariants = { $"{normalized}.json", $"{normalized.ToLowerInvariant()}.json", $"{tierId}.json", $"{tierId.ToLowerInvariant()}.json" };

        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            foreach (var variant in fileVariants)
            {
                var testPath = Path.Combine(dir, variant);
                if (File.Exists(testPath))
                {
                    resolvedPath = testPath;
                    break;
                }
            }
            if (resolvedPath != null) break;
        }

        if (resolvedPath == null && File.Exists(tierId))
        {
            resolvedPath = tierId;
        }

        // If file exists on disk, load and validate it strictly (never silently fallback on corrupt JSON)
        if (resolvedPath != null)
        {
            try
            {
                var json = File.ReadAllText(resolvedPath);
                var profile = JsonSerializer.Deserialize<ProfileDef>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (profile == null)
                {
                    throw new InvalidOperationException($"Optimization profile file '{resolvedPath}' contains empty or invalid profile JSON.");
                }

                profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? normalized : profile.Id;
                profile.DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? $"{normalized} Optimization" : profile.DisplayName;
                profile.Entries ??= new List<OptimizationEntry>();
                profile.ProfileOrigin = "DISK";
                profile.SourcePath = resolvedPath;
                profile.IsBuiltInFallback = false;

                foreach (var entry in profile.Entries)
                {
                    entry.SourceProfile = normalized;
                }
                return profile;
            }
            catch (JsonException jEx)
            {
                throw new InvalidOperationException($"Optimization profile '{tierId}' at '{resolvedPath}' is corrupted: {jEx.Message}", jEx);
            }
        }

        // If file does NOT exist on disk, only recognized standard tiers can use the explicit built-in fallback
        if (CanonicalTiers.Contains(normalized))
        {
            return GetCanonicalBuiltInProfile(normalized);
        }

        // Unknown or custom profile that does not exist on disk
        throw new FileNotFoundException(
            $"Optimization profile '{tierId}' (normalized as '{normalized}') was not found on disk. Candidate locations probed: {string.Join("; ", candidateDirs.Where(Directory.Exists))}");
    }

    public ProfileDef? LoadResolvedProfile(string tierId)
    {
        string normalized = NormalizeProfileId(tierId);
        var profile = LoadProfile(normalized);
        if (profile == null) return null;

        var resolvedEntries = new Dictionary<string, OptimizationEntry>();
        var resolvedProcessReductionMin = profile.TargetProcessReduction.Min;
        var resolvedProcessReductionMax = profile.TargetProcessReduction.Max;

        // Recursively load inherited profiles
        LoadAndMergeInheritedProfiles(profile, resolvedEntries, new HashSet<string> { normalized });

        // Add current profile entries, resolving conflicts
        foreach (var entry in profile.Entries)
        {
            MergeEntry(resolvedEntries, entry);
        }

        profile.Entries = resolvedEntries.Values.ToList();
        return profile;
    }

    private void LoadAndMergeInheritedProfiles(ProfileDef profile, Dictionary<string, OptimizationEntry> resolvedEntries, HashSet<string> seen)
    {
        if (profile.Extends == null) return;

        foreach (var parentId in profile.Extends)
        {
            if (seen.Contains(parentId)) continue;
            seen.Add(parentId);

            var parent = LoadProfile(parentId);
            if (parent != null)
            {
                // Recursively load this parent's parents
                LoadAndMergeInheritedProfiles(parent, resolvedEntries, seen);

                foreach (var entry in parent.Entries)
                {
                    MergeEntry(resolvedEntries, entry);
                }
            }
        }
    }

    private void MergeEntry(Dictionary<string, OptimizationEntry> resolvedEntries, OptimizationEntry entry)
    {
        // Key uniquely identifies the target of the optimization
        var key = $"{entry.Action}:{entry.Target}".ToLowerInvariant();

        if (!resolvedEntries.TryGetValue(key, out var existing))
        {
            // Create a deep copy or just add it (we'll serialize/deserialize to copy later if needed, but simple reference is okay for now since we mutate SourceProfile earlier)
            var newEntry = CloneEntry(entry);
            resolvedEntries[key] = newEntry;
        }
        else
        {
            // Conflict resolution: Child entry overrides Target/Value/Conditions but retains highest risk
            var childEntry = CloneEntry(entry);
            childEntry.Risk = GetHighestRisk(existing.Risk, entry.Risk);
            resolvedEntries[key] = childEntry;
        }
    }

    private OptimizationEntry CloneEntry(OptimizationEntry source)
    {
        var json = JsonSerializer.Serialize(source);
        var clone = JsonSerializer.Deserialize<OptimizationEntry>(json)!;
        clone.SourceProfile = source.SourceProfile;
        return clone;
    }

    private string GetHighestRisk(string risk1, string risk2)
    {
        var risks = new[] { "Low", "Medium", "High", "Critical" };
        var idx1 = Array.FindIndex(risks, r => r.Equals(risk1, StringComparison.OrdinalIgnoreCase));
        var idx2 = Array.FindIndex(risks, r => r.Equals(risk2, StringComparison.OrdinalIgnoreCase));

        if (idx1 < 0) idx1 = 0;
        if (idx2 < 0) idx2 = 0;

        return risks[Math.Max(idx1, idx2)];
    }

    public bool ValidateProfile(ProfileDef profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) return false;
        if (profile.Entries == null || profile.Entries.Count == 0) return false;

        foreach (var entry in profile.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Action)) return false;
            if (string.IsNullOrWhiteSpace(entry.Target)) return false;
            
            var validRisks = new[] { "Low", "Medium", "High", "Critical" };
            if (!validRisks.Contains(entry.Risk, StringComparer.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
