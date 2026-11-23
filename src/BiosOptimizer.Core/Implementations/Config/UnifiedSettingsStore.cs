#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiosOptimizer.Core.Common;

namespace BiosOptimizer.Core.Implementations.Config;

public sealed class AiPolicySettings
{
    [JsonPropertyName("powerPlan")]
    public bool PowerPlan { get; set; }

    [JsonPropertyName("workload")]
    public bool Workload { get; set; }

    [JsonPropertyName("autoOptimize")]
    public bool AutoOptimize { get; set; }

    public AiPolicySettings Clone() => new()
    {
        PowerPlan = PowerPlan,
        Workload = Workload,
        AutoOptimize = AutoOptimize
    };
}

/// <summary>
/// Authoritative V4 persistent configuration contract aligned 1:1 with frontend <c>AppSettings</c>
/// and persisted atomically under <c>%ProgramData%\ErrorOptimizer\settings.json</c>.
/// </summary>
public sealed class UnifiedAppSettings
{
    // Concurrency / Versioning
    [JsonPropertyName("configVersion")]
    public int ConfigVersion { get; set; } = 1;

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    // Master Auto-Optimization & AI Toggles
    [JsonPropertyName("autoOptimizationEnabled")]
    public bool AutoOptimizationEnabled { get; set; }

    [JsonPropertyName("aiOptimizationEnabled")]
    public bool AiOptimizationEnabled { get; set; } = true;

    // Startup & Tray Runtime
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("runInSystemTray")]
    public bool RunInSystemTray { get; set; } = true;

    [JsonPropertyName("backgroundServiceEnabled")]
    public bool BackgroundServiceEnabled { get; set; } = true;

    // Automation & Hardware
    [JsonPropertyName("adaptiveHardwareDetectionEnabled")]
    public bool AdaptiveHardwareDetectionEnabled { get; set; } = true;

    [JsonPropertyName("automaticScanEnabled")]
    public bool AutomaticScanEnabled { get; set; } = true;

    [JsonPropertyName("automaticExecutionEnabled")]
    public bool AutomaticExecutionEnabled { get; set; }

    [JsonPropertyName("monitoringInterval")]
    public int MonitoringInterval { get; set; } = 30; // Minutes for AutoOpt cycle

    [JsonPropertyName("monitorIntervalSec")]
    public int MonitorIntervalSec { get; set; } = 3;  // Seconds for live telemetry

    [JsonPropertyName("selectedOptimizationProfile")]
    public string SelectedOptimizationProfile { get; set; } = "MaximumPerformance";

    [JsonPropertyName("approvedOptimizationCategories")]
    public List<string> ApprovedOptimizationCategories { get; set; } = new()
    {
        "MEMORY", "TEMP", "SYSTEM", "BROWSER"
    };

    [JsonPropertyName("userConsentVersion")]
    public int UserConsentVersion { get; set; } = 1;

    [JsonPropertyName("lastSuccessfulScan")]
    public string? LastSuccessfulScan { get; set; }

    [JsonPropertyName("lastExecutionStatus")]
    public string LastExecutionStatus { get; set; } = "IDLE";

    // Legacy / Sub-policy mappings for backward compatibility
    [JsonPropertyName("policies")]
    public AiPolicySettings Policies { get; set; } = new();

    [JsonPropertyName("aiEnabled")]
    public bool AiEnabled
    {
        get => AiOptimizationEnabled;
        set => AiOptimizationEnabled = value;
    }

    [JsonPropertyName("performanceMode")]
    public string PerformanceMode { get; set; } = "auto"; // auto | low | balanced | high

    [JsonPropertyName("confirmMedium")]
    public bool ConfirmMedium { get; set; } = true;

    [JsonPropertyName("confirmHigh")]
    public bool ConfirmHigh { get; set; } = true;

    [JsonPropertyName("autoBackup")]
    public bool AutoBackup { get; set; } = true;

    [JsonPropertyName("detailedLogs")]
    public bool DetailedLogs { get; set; }

    [JsonPropertyName("logRetentionDays")]
    public int LogRetentionDays { get; set; } = 7; // 3 | 7 | 14 | 30

    [JsonPropertyName("uiAnimations")]
    public bool UiAnimations { get; set; } = true;

    [JsonPropertyName("pageTransitions")]
    public bool PageTransitions { get; set; } = true;

    [JsonPropertyName("reducedMotion")]
    public bool ReducedMotion { get; set; }

    [JsonPropertyName("lightweight")]
    public bool Lightweight { get; set; }

    [JsonPropertyName("depth")]
    public string Depth { get; set; } = "MEDIUM (3D)";

    [JsonPropertyName("lastSuite")]
    public string LastSuite { get; set; } = "V4";

    [JsonPropertyName("hostExecutablePath")]
    public string? HostExecutablePath { get; set; }

    public static UnifiedAppSettings CreateDefaults(string? preserveLastSuite = null, string? preserveHostExe = null) => new()
    {
        ConfigVersion = 1,
        LastUpdatedUtc = DateTime.UtcNow,
        AutoOptimizationEnabled = false,
        AiOptimizationEnabled = true,
        StartWithWindows = false,
        StartMinimized = false,
        RunInSystemTray = true,
        BackgroundServiceEnabled = true,
        AdaptiveHardwareDetectionEnabled = true,
        AutomaticScanEnabled = true,
        AutomaticExecutionEnabled = false,
        MonitoringInterval = 30,
        MonitorIntervalSec = 3,
        SelectedOptimizationProfile = "MaximumPerformance",
        ApprovedOptimizationCategories = new List<string> { "MEMORY", "TEMP", "SYSTEM", "BROWSER" },
        UserConsentVersion = 1,
        LastSuccessfulScan = null,
        LastExecutionStatus = "IDLE",
        Policies = new AiPolicySettings(),
        PerformanceMode = "auto",
        ConfirmMedium = true,
        ConfirmHigh = true,
        AutoBackup = true,
        DetailedLogs = false,
        LogRetentionDays = 7,
        UiAnimations = true,
        PageTransitions = true,
        ReducedMotion = false,
        Lightweight = false,
        Depth = "MEDIUM (3D)",
        LastSuite = preserveLastSuite is "V3" ? "V3" : "V4",
        HostExecutablePath = preserveHostExe
    };
}

public static class UnifiedSettingsStore
{
    private static readonly object _sync = new();
    private static readonly HashSet<string> AllowedPerfModes = new(StringComparer.OrdinalIgnoreCase) { "auto", "low", "balanced", "high" };
    private static readonly HashSet<int> AllowedIntervals = new() { 1, 2, 3, 5, 10 };
    private static readonly HashSet<int> AllowedRetentions = new() { 3, 7, 14, 30 };
    private static readonly HashSet<string> AllowedDepths = new(StringComparer.OrdinalIgnoreCase) { "LOW (2D)", "MEDIUM (3D)", "HIGH (DEEP)" };
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string RootDirectory => AppEnvironment.ResolveWritableDataDirectory();

    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public static UnifiedAppSettings LoadSafe()
    {
        lock (_sync)
        {
            try
            {
                string readPath = AppEnvironment.ResolveDataReadPath("settings.json");
                if (!File.Exists(readPath))
                    return UnifiedAppSettings.CreateDefaults();

                string json = File.ReadAllText(readPath);
                if (string.IsNullOrWhiteSpace(json))
                    return UnifiedAppSettings.CreateDefaults();

                var parsed = JsonSerializer.Deserialize<UnifiedAppSettings>(json, _jsonOptions);
                return Sanitize(parsed);
            }
            catch
            {
                QuarantineCorruptFile();
                return UnifiedAppSettings.CreateDefaults();
            }
        }
    }

    public static UnifiedAppSettings Save(UnifiedAppSettings incoming)
    {
        UnifiedAppSettings sanitized = Sanitize(incoming);
        sanitized.ConfigVersion++;
        sanitized.LastUpdatedUtc = DateTime.UtcNow;

        lock (_sync)
        {
            Directory.CreateDirectory(RootDirectory);
            string tmp = SettingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(sanitized, _jsonOptions);
            File.WriteAllText(tmp, json);

            if (File.Exists(SettingsFilePath))
                File.Replace(tmp, SettingsFilePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, SettingsFilePath, overwrite: true);
        }

        // Keep AutoOptimizeEngine configuration in sync
        try
        {
            Automation.AutoOptimizeEngine.Instance.SyncWithGlobalConfig(
                sanitized.AutoOptimizationEnabled,
                sanitized.MonitoringInterval
            );
        }
        catch { }

        return sanitized;
    }

    public static UnifiedAppSettings MergeAndSave(JsonElement patchOrFull, string? currentHostExePath = null)
    {
        var current = LoadSafe();
        if (!string.IsNullOrWhiteSpace(currentHostExePath) && File.Exists(currentHostExePath))
            current.HostExecutablePath = currentHostExePath;

        if (patchOrFull.ValueKind == JsonValueKind.Object)
        {
            // Master toggles
            if (patchOrFull.TryGetProperty("autoOptimizationEnabled", out var aoEn) && (aoEn.ValueKind == JsonValueKind.True || aoEn.ValueKind == JsonValueKind.False))
                current.AutoOptimizationEnabled = aoEn.GetBoolean();

            if (patchOrFull.TryGetProperty("aiOptimizationEnabled", out var aiEn) && (aiEn.ValueKind == JsonValueKind.True || aiEn.ValueKind == JsonValueKind.False))
                current.AiOptimizationEnabled = aiEn.GetBoolean();

            // Startup & Tray
            if (patchOrFull.TryGetProperty("startWithWindows", out var sw) && (sw.ValueKind == JsonValueKind.True || sw.ValueKind == JsonValueKind.False))
                current.StartWithWindows = sw.GetBoolean();
            if (patchOrFull.TryGetProperty("startMinimized", out var sm) && (sm.ValueKind == JsonValueKind.True || sm.ValueKind == JsonValueKind.False))
                current.StartMinimized = sm.GetBoolean();
            if (patchOrFull.TryGetProperty("runInSystemTray", out var rist) && (rist.ValueKind == JsonValueKind.True || rist.ValueKind == JsonValueKind.False))
                current.RunInSystemTray = rist.GetBoolean();
            if (patchOrFull.TryGetProperty("backgroundServiceEnabled", out var bse) && (bse.ValueKind == JsonValueKind.True || bse.ValueKind == JsonValueKind.False))
                current.BackgroundServiceEnabled = bse.GetBoolean();

            // Automation & Hardware
            if (patchOrFull.TryGetProperty("adaptiveHardwareDetectionEnabled", out var ahde) && (ahde.ValueKind == JsonValueKind.True || ahde.ValueKind == JsonValueKind.False))
                current.AdaptiveHardwareDetectionEnabled = ahde.GetBoolean();
            if (patchOrFull.TryGetProperty("automaticScanEnabled", out var ase) && (ase.ValueKind == JsonValueKind.True || ase.ValueKind == JsonValueKind.False))
                current.AutomaticScanEnabled = ase.GetBoolean();
            if (patchOrFull.TryGetProperty("automaticExecutionEnabled", out var aee) && (aee.ValueKind == JsonValueKind.True || aee.ValueKind == JsonValueKind.False))
                current.AutomaticExecutionEnabled = aee.GetBoolean();
            if (patchOrFull.TryGetProperty("monitoringInterval", out var mi) && mi.TryGetInt32(out int mInt))
                current.MonitoringInterval = mInt > 0 ? mInt : current.MonitoringInterval;

            if (patchOrFull.TryGetProperty("selectedOptimizationProfile", out var sop) && sop.ValueKind == JsonValueKind.String)
                current.SelectedOptimizationProfile = sop.GetString() ?? current.SelectedOptimizationProfile;

            if (patchOrFull.TryGetProperty("approvedOptimizationCategories", out var aoc) && aoc.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in aoc.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                }
                if (list.Count > 0) current.ApprovedOptimizationCategories = list;
            }

            if (patchOrFull.TryGetProperty("lastSuccessfulScan", out var lss) && lss.ValueKind == JsonValueKind.String)
                current.LastSuccessfulScan = lss.GetString();
            if (patchOrFull.TryGetProperty("lastExecutionStatus", out var les) && les.ValueKind == JsonValueKind.String)
                current.LastExecutionStatus = les.GetString() ?? current.LastExecutionStatus;

            // Sub-policies
            if (patchOrFull.TryGetProperty("policies", out var pol) && pol.ValueKind == JsonValueKind.Object)
            {
                if (pol.TryGetProperty("powerPlan", out var pp) && (pp.ValueKind == JsonValueKind.True || pp.ValueKind == JsonValueKind.False))
                    current.Policies.PowerPlan = pp.GetBoolean();
                if (pol.TryGetProperty("workload", out var wl) && (wl.ValueKind == JsonValueKind.True || wl.ValueKind == JsonValueKind.False))
                    current.Policies.Workload = wl.GetBoolean();
                if (pol.TryGetProperty("autoOptimize", out var ao) && (ao.ValueKind == JsonValueKind.True || ao.ValueKind == JsonValueKind.False))
                {
                    current.Policies.AutoOptimize = ao.GetBoolean();
                    current.AutoOptimizationEnabled = current.Policies.AutoOptimize;
                }
            }

            if (patchOrFull.TryGetProperty("performanceMode", out var pm) && pm.ValueKind == JsonValueKind.String)
                current.PerformanceMode = pm.GetString() ?? current.PerformanceMode;
            if (patchOrFull.TryGetProperty("aiEnabled", out var aiEl) && (aiEl.ValueKind == JsonValueKind.True || aiEl.ValueKind == JsonValueKind.False))
            {
                current.AiOptimizationEnabled = aiEl.GetBoolean();
            }

            if (patchOrFull.TryGetProperty("monitorIntervalSec", out var miSec) && miSec.TryGetInt32(out int mSecVal))
                current.MonitorIntervalSec = mSecVal;

            if (patchOrFull.TryGetProperty("confirmMedium", out var cm) && (cm.ValueKind == JsonValueKind.True || cm.ValueKind == JsonValueKind.False))
                current.ConfirmMedium = cm.GetBoolean();
            if (patchOrFull.TryGetProperty("confirmHigh", out var ch) && (ch.ValueKind == JsonValueKind.True || ch.ValueKind == JsonValueKind.False))
                current.ConfirmHigh = ch.GetBoolean();
            if (patchOrFull.TryGetProperty("autoBackup", out var ab) && (ab.ValueKind == JsonValueKind.True || ab.ValueKind == JsonValueKind.False))
                current.AutoBackup = ab.GetBoolean();
            if (patchOrFull.TryGetProperty("detailedLogs", out var dl) && (dl.ValueKind == JsonValueKind.True || dl.ValueKind == JsonValueKind.False))
                current.DetailedLogs = dl.GetBoolean();
            if (patchOrFull.TryGetProperty("logRetentionDays", out var lr) && lr.TryGetInt32(out int lrVal))
                current.LogRetentionDays = lrVal;

            if (patchOrFull.TryGetProperty("uiAnimations", out var ua) && (ua.ValueKind == JsonValueKind.True || ua.ValueKind == JsonValueKind.False))
                current.UiAnimations = ua.GetBoolean();
            if (patchOrFull.TryGetProperty("pageTransitions", out var pt) && (pt.ValueKind == JsonValueKind.True || pt.ValueKind == JsonValueKind.False))
                current.PageTransitions = pt.GetBoolean();
            if (patchOrFull.TryGetProperty("reducedMotion", out var rm) && (rm.ValueKind == JsonValueKind.True || rm.ValueKind == JsonValueKind.False))
                current.ReducedMotion = rm.GetBoolean();
            if (patchOrFull.TryGetProperty("lightweight", out var lw) && (lw.ValueKind == JsonValueKind.True || lw.ValueKind == JsonValueKind.False))
                current.Lightweight = lw.GetBoolean();

            if (patchOrFull.TryGetProperty("depth", out var dp) && dp.ValueKind == JsonValueKind.String)
                current.Depth = dp.GetString() ?? current.Depth;
            if (patchOrFull.TryGetProperty("lastSuite", out var ls) && ls.ValueKind == JsonValueKind.String)
                current.LastSuite = ls.GetString() ?? current.LastSuite;
        }

        return Save(current);
    }

    public static UnifiedAppSettings ResetDefaults()
    {
        var current = LoadSafe();
        return Save(UnifiedAppSettings.CreateDefaults(current.LastSuite, current.HostExecutablePath));
    }

    public static UnifiedAppSettings Sanitize(UnifiedAppSettings? raw)
    {
        if (raw == null) return UnifiedAppSettings.CreateDefaults();

        var def = UnifiedAppSettings.CreateDefaults();
        var policies = raw.Policies?.Clone() ?? new AiPolicySettings();

        // Enforce bidirectional policy sync
        if (raw.AutoOptimizationEnabled)
            policies.AutoOptimize = true;
        else if (policies.AutoOptimize)
            raw.AutoOptimizationEnabled = true;

        return new UnifiedAppSettings
        {
            ConfigVersion = raw.ConfigVersion > 0 ? raw.ConfigVersion : 1,
            LastUpdatedUtc = raw.LastUpdatedUtc != default ? raw.LastUpdatedUtc : DateTime.UtcNow,
            AutoOptimizationEnabled = raw.AutoOptimizationEnabled,
            AiOptimizationEnabled = raw.AiOptimizationEnabled,
            StartWithWindows = raw.StartWithWindows,
            StartMinimized = raw.StartMinimized,
            RunInSystemTray = raw.RunInSystemTray,
            BackgroundServiceEnabled = raw.BackgroundServiceEnabled,
            AdaptiveHardwareDetectionEnabled = raw.AdaptiveHardwareDetectionEnabled,
            AutomaticScanEnabled = raw.AutomaticScanEnabled,
            AutomaticExecutionEnabled = raw.AutomaticExecutionEnabled,
            MonitoringInterval = raw.MonitoringInterval > 0 ? raw.MonitoringInterval : def.MonitoringInterval,
            MonitorIntervalSec = AllowedIntervals.Contains(raw.MonitorIntervalSec) ? raw.MonitorIntervalSec : def.MonitorIntervalSec,
            SelectedOptimizationProfile = !string.IsNullOrWhiteSpace(raw.SelectedOptimizationProfile) ? raw.SelectedOptimizationProfile : def.SelectedOptimizationProfile,
            ApprovedOptimizationCategories = raw.ApprovedOptimizationCategories != null && raw.ApprovedOptimizationCategories.Count > 0 ? raw.ApprovedOptimizationCategories : def.ApprovedOptimizationCategories,
            UserConsentVersion = raw.UserConsentVersion > 0 ? raw.UserConsentVersion : 1,
            LastSuccessfulScan = raw.LastSuccessfulScan,
            LastExecutionStatus = !string.IsNullOrWhiteSpace(raw.LastExecutionStatus) ? raw.LastExecutionStatus : "IDLE",
            Policies = policies,
            PerformanceMode = AllowedPerfModes.Contains(raw.PerformanceMode) ? raw.PerformanceMode.ToLowerInvariant() : def.PerformanceMode,
            ConfirmMedium = raw.ConfirmMedium,
            ConfirmHigh = raw.ConfirmHigh,
            AutoBackup = raw.AutoBackup,
            DetailedLogs = raw.DetailedLogs,
            LogRetentionDays = AllowedRetentions.Contains(raw.LogRetentionDays) ? raw.LogRetentionDays : def.LogRetentionDays,
            UiAnimations = raw.UiAnimations,
            PageTransitions = raw.PageTransitions,
            ReducedMotion = raw.ReducedMotion,
            Lightweight = raw.Lightweight,
            Depth = AllowedDepths.Contains(raw.Depth) ? raw.Depth.ToUpperInvariant() : def.Depth,
            LastSuite = raw.LastSuite.Equals("V4", StringComparison.OrdinalIgnoreCase) ? "V4" : "V3",
            HostExecutablePath = raw.HostExecutablePath
        };
    }

    private static void QuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string corruptPath = Path.Combine(RootDirectory, $"settings.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.bak");
                File.Move(SettingsFilePath, corruptPath, overwrite: true);
            }
        }
        catch { }
    }
}
