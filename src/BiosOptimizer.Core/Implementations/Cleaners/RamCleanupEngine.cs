#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using BiosOptimizer.Core.Implementations.Memory;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.Implementations.Cleaners;

public sealed class StandbyCleanupDetailedResult
{
    public bool Succeeded { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool StandbyPurged { get; set; }
    public bool WorkingSetsTrimmed { get; set; }
    public long AvailableBeforeBytes { get; set; }
    public long AvailableAfterBytes { get; set; }
    public long StandbyBeforeBytes { get; set; }
    public long StandbyAfterBytes { get; set; }
    public long StandbyReclaimedBytes { get; set; }
    public long WorkingSetReclaimedBytes { get; set; }
    public long TotalReclaimedBytes { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class RamCleanupEngine : ICleanerEngine
{
    public string EngineId => "ram-cleanup";
    public string DisplayName => "RAM & Standby Memory Cleanup";

    public CleanupScanDto Scan()
    {
        var provider = WindowsMemoryListProvider.Instance;
        var snap = provider.GetCurrentSnapshot();
        long standbyOrTrim = snap.IsNativeKernelQueried && snap.StandbyCacheBytes > 0
            ? snap.StandbyCacheBytes
            : 0;

        return new CleanupScanDto
        {
            Category = "RAM",
            IsSafeToClean = provider.IsSupported,
            TotalSizeInBytes = standbyOrTrim,
            FileCount = 0,
            ScannedPaths = new List<string>
            {
                snap.IsNativeKernelQueried
                    ? $"Standby Cache: {snap.StandbyFormatted} · Available: {snap.AvailableFormatted}"
                    : $"Available Physical RAM: {snap.AvailableFormatted} (Kernel Standby List requires SeProfileSingleProcessPrivilege)"
            }
        };
    }

    public StandbyCleanupDetailedResult CleanWithDetails(CancellationToken ct = default)
    {
        var provider = WindowsMemoryListProvider.Instance;
        var before = provider.GetCurrentSnapshot();

        var detail = new StandbyCleanupDetailedResult
        {
            AvailableBeforeBytes = before.AvailableBytes,
            StandbyBeforeBytes = before.StandbyCacheBytes
        };

        MemoryCleanupResult? standbyRes = null;
        if (provider.IsSupported)
        {
            try
            {
                standbyRes = provider.ExecuteStandbyPurgeAsync(lowPriorityOnly: false, ct).GetAwaiter().GetResult();
                detail.StandbyPurged = standbyRes.Succeeded;
            }
            catch (Exception ex)
            {
                standbyRes = new MemoryCleanupResult { Succeeded = false, IsSupported = true, SummaryMessage = ex.Message };
            }
        }
        else
        {
            detail.RequiresAdmin = true;
        }

        MemoryCleanupResult? wsRes = null;
        try
        {
            wsRes = provider.ExecuteWorkingSetTrimAsync(ct).GetAwaiter().GetResult();
            detail.WorkingSetsTrimmed = wsRes.Succeeded;
        }
        catch { }

        var after = provider.GetCurrentSnapshot();
        detail.AvailableAfterBytes = after.AvailableBytes;
        detail.StandbyAfterBytes = after.StandbyCacheBytes;

        long standbyDelta = (before.IsNativeKernelQueried && after.IsNativeKernelQueried && before.StandbyCacheBytes >= 0 && after.StandbyCacheBytes >= 0)
            ? Math.Max(0, before.StandbyCacheBytes - after.StandbyCacheBytes)
            : (standbyRes?.StandbyReclaimedBytes ?? 0);
        long availDelta = Math.Max(0, after.AvailableBytes - before.AvailableBytes);
        long wsDelta = wsRes?.ReclaimedBytes ?? 0;

        detail.StandbyReclaimedBytes = standbyDelta;
        detail.WorkingSetReclaimedBytes = Math.Max(wsDelta, 0);
        detail.TotalReclaimedBytes = Math.Max(standbyDelta, Math.Max(availDelta, wsDelta));

        if (detail.StandbyPurged)
        {
            detail.Succeeded = true;
            detail.RequiresAdmin = false;
            detail.Message = detail.TotalReclaimedBytes > 0
                ? $"Purged standby list & trimmed idle working sets (+{MemorySnapshot.FormatBytes(availDelta)} available RAM, -{MemorySnapshot.FormatBytes(standbyDelta)} standby cache)."
                : $"Standby list flushed & idle working sets trimmed (Available RAM: {after.AvailableFormatted}).";
        }
        else
        {
            detail.Succeeded = false;
            detail.RequiresAdmin = true;
            detail.Message = detail.WorkingSetsTrimmed
                ? $"Trimmed non-elevated background working sets ({MemorySnapshot.FormatBytes(detail.WorkingSetReclaimedBytes)}), but Administrator privilege (SeProfileSingleProcessPrivilege) is required to purge the Windows Standby List."
                : "Administrator privilege (SeProfileSingleProcessPrivilege) or connected background service is required to purge Windows Standby List.";
        }

        return detail;
    }

    public long Clean()
    {
        var details = CleanWithDetails();
        return details.TotalReclaimedBytes;
    }
}
