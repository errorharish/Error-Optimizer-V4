#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BiosOptimizer.Core.Implementations.Automation;

public static class AutoOptimizeTaskIds
{
    public const string TempWindows = "temp-windows";
    public const string TempUser = "temp-user";
    public const string TempCrash = "temp-crash";
    public const string MemStandby = "mem-standby";
    public const string BrowserChrome = "browser-chrome";
    public const string BrowserEdge = "browser-edge";
    public const string BrowserFirefox = "browser-firefox";
    public const string RecycleBin = "recycle-bin";
    public const string SysShader = "sys-shader";
    public const string SysDns = "sys-dns";

    public static readonly IReadOnlyList<string> All = new[]
    {
        TempWindows, TempUser, TempCrash, MemStandby,
        BrowserChrome, BrowserEdge, BrowserFirefox,
        RecycleBin, SysShader, SysDns
    };

    public static readonly IReadOnlyList<string> Recommended = new[]
    {
        TempWindows, TempUser, TempCrash, MemStandby,
        BrowserChrome, BrowserEdge, BrowserFirefox,
        SysShader, SysDns
    };
}

public sealed class AutoOptimizeConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("tasks")]
    public List<string> Tasks { get; set; } = new(AutoOptimizeTaskIds.Recommended);

    [JsonPropertyName("intervalMin")]
    public int IntervalMin { get; set; } = 30;

    [JsonPropertyName("runOnLaunch")]
    public bool RunOnLaunch { get; set; }

    [JsonPropertyName("closeBehavior")]
    public string CloseBehavior { get; set; } = "stop"; // stop | exit-run | background

    [JsonPropertyName("runOnWindowsStartup")]
    public bool RunOnWindowsStartup { get; set; }

    [JsonPropertyName("startupPolicy")]
    public string StartupPolicy { get; set; } = "after-delay"; // after-delay | next-interval

    [JsonPropertyName("overlap")]
    public string Overlap { get; set; } = "skip"; // skip | queue

    [JsonPropertyName("safeOnly")]
    public bool SafeOnly { get; set; } = true;

    [JsonPropertyName("skipRecentTemp")]
    public bool SkipRecentTemp { get; set; } = true;

    [JsonPropertyName("recycleConfirm")]
    public bool RecycleConfirm { get; set; } = true;

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "Balanced"; // Balanced | Light | Thorough
}

public sealed class AutoTaskScanInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("estimatedBytes")]
    public long EstimatedBytes { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("browserRunning")]
    public bool BrowserRunning { get; set; }

    [JsonPropertyName("installed")]
    public bool Installed { get; set; } = true;

    [JsonPropertyName("statusNote")]
    public string StatusNote { get; set; } = string.Empty;
}

public sealed class AutoTaskExecutionDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "Completed"; // Completed | Skipped | Cancelled | Failed

    [JsonPropertyName("status")]
    public string Status { get; set; } = "VERIFIED"; // VERIFIED | ALREADY_OPTIMIZED | NOT_INSTALLED | SKIPPED_BUSY | FAILED_ATTEMPTED | UNVERIFIED

    [JsonPropertyName("reclaimedBytes")]
    public long ReclaimedBytes { get; set; }

    [JsonPropertyName("filesDeleted")]
    public int FilesDeleted { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("verified")]
    public bool Verified { get; set; } = true;

    [JsonPropertyName("alreadyOptimized")]
    public bool AlreadyOptimized { get; set; }
}

public sealed class AutoOptimizeHistoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = "Manual"; // Schedule | Manual | App launch | App exit | Windows startup

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Completed"; // Completed | Partial | Skipped | Cancelled

    [JsonPropertyName("completed")]
    public int Completed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("cancelled")]
    public int Cancelled { get; set; }

    [JsonPropertyName("totalReclaimedBytes")]
    public long TotalReclaimedBytes { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("tasks")]
    public List<AutoTaskExecutionDetail> Tasks { get; set; } = new();
}

public sealed class AutoOptimizeStateSnapshot
{
    [JsonPropertyName("config")]
    public AutoOptimizeConfig Config { get; set; } = new();

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Disabled"; // Running | Enabled | Paused | Disabled

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }

    [JsonPropertyName("queued")]
    public bool Queued { get; set; }

    [JsonPropertyName("nextRunUtc")]
    public DateTime? NextRunUtc { get; set; }

    [JsonPropertyName("lastRunUtc")]
    public DateTime? LastRunUtc { get; set; }

    [JsonPropertyName("history")]
    public List<AutoOptimizeHistoryEntry> History { get; set; } = new();
}
