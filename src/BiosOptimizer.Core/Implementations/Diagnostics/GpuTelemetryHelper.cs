#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BiosOptimizer.Core.Common;

namespace BiosOptimizer.Core.Implementations.Diagnostics;

/// <summary>
/// Canonical WDDM GPU Engine counter aggregator matching Windows Task Manager semantics:
/// sums process-level samples per physical adapter &amp; engine queue (clamped [0, 100]),
/// then takes the maximum across active engine queues per physical adapter.
/// </summary>
public static class GpuTelemetryHelper
{
    private static readonly Regex LuidRegex = new(@"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhysRegex = new(@"phys_(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EngRegex = new(@"eng_(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EngTypeRegex = new(@"engtype_([A-Za-z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Resolves a WDDM GPU Engine counter instance name to an adapter index in <paramref name="adapters"/>
    /// (matching LUID first, then physical adapter index fallback).
    /// </summary>
    public static int ResolveAdapterIndex(
        string? instanceName,
        IReadOnlyList<DxgiAdapterInfo>? adapters,
        int fallbackAdapterCount)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return -1;

        int maxCount = adapters != null && adapters.Count > 0 ? adapters.Count : fallbackAdapterCount;
        if (maxCount <= 0)
            return -1;

        if (adapters != null && adapters.Count > 0)
        {
            var luidMatch = LuidRegex.Match(instanceName);
            if (luidMatch.Success)
            {
                string luid = luidMatch.Groups[1].Value;
                for (int i = 0; i < adapters.Count; i++)
                {
                    if (string.Equals(adapters[i].AdapterLuidHex, luid, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
        }

        var physMatch = PhysRegex.Match(instanceName);
        if (physMatch.Success && int.TryParse(physMatch.Groups[1].Value, out int physIdx))
        {
            return physIdx < maxCount ? physIdx : 0;
        }

        return maxCount == 1 ? 0 : -1;
    }

    /// <summary>
    /// Extracts a canonical engine queue key (<c>eng_{id}_{type}</c>) from a WDDM GPU Engine instance name
    /// so multiple processes sharing the same hardware queue are summed before taking max across queues.
    /// </summary>
    public static string ExtractEngineQueueKey(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return "eng:unknown";

        var engMatch = EngRegex.Match(instanceName);
        var typeMatch = EngTypeRegex.Match(instanceName);
        string eng = engMatch.Success ? engMatch.Groups[1].Value : "?";
        string engType = typeMatch.Success ? typeMatch.Groups[1].Value.ToUpperInvariant() : "OTHER";
        return $"eng:{eng}:{engType}";
    }

    /// <summary>
    /// Aggregates per-adapter GPU utilization (%) from PDH <c>GPU Engine</c> counter samples.
    /// </summary>
    public static Dictionary<int, double> AggregateByAdapterIndex(
        IEnumerable<(int AdapterIndex, string InstanceName, double Utilization)> samples)
    {
        var queueTotals = new Dictionary<(int AdapterIndex, string QueueKey), double>();

        foreach (var (adapterIdx, instanceName, rawVal) in samples)
        {
            if (adapterIdx < 0 || !double.IsFinite(rawVal) || rawVal < 0)
                continue;

            var key = (adapterIdx, ExtractEngineQueueKey(instanceName));
            queueTotals.TryGetValue(key, out double cur);
            queueTotals[key] = cur + rawVal;
        }

        var adapterMax = new Dictionary<int, double>();
        foreach (var kv in queueTotals)
        {
            int idx = kv.Key.AdapterIndex;
            double clampedQueue = Math.Clamp(kv.Value, 0.0, 100.0);
            if (!adapterMax.TryGetValue(idx, out double existingMax) || clampedQueue > existingMax)
                adapterMax[idx] = Math.Round(clampedQueue, 1);
        }

        return adapterMax;
    }

    /// <summary>
    /// Aggregates WMI <c>Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine</c> rows into a single
    /// bounded [0, 100] overall utilization value for the preferred LUID (or highest-loaded physical adapter).
    /// </summary>
    public static double? AggregateWmiEngineUtilization(
        IEnumerable<(string? InstanceName, double Utilization)> samples,
        string? preferredLuidHex = null)
    {
        var queueTotals = new Dictionary<(string AdapterKey, string QueueKey), double>();

        foreach (var (instanceName, rawVal) in samples)
        {
            if (!double.IsFinite(rawVal) || rawVal < 0)
                continue;

            string adapterKey = ExtractAdapterKey(instanceName);
            string queueKey = ExtractEngineQueueKey(instanceName);
            var key = (adapterKey, queueKey);
            queueTotals.TryGetValue(key, out double cur);
            queueTotals[key] = cur + rawVal;
        }

        if (queueTotals.Count == 0)
            return null;

        var perAdapterMax = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in queueTotals)
        {
            double clampedQueue = Math.Clamp(kv.Value, 0.0, 100.0);
            if (!perAdapterMax.TryGetValue(kv.Key.AdapterKey, out double cur) || clampedQueue > cur)
                perAdapterMax[kv.Key.AdapterKey] = clampedQueue;
        }

        if (!string.IsNullOrWhiteSpace(preferredLuidHex) &&
            perAdapterMax.TryGetValue($"luid:{preferredLuidHex}", out double preferredLoad))
        {
            return Math.Round(preferredLoad, 1);
        }

        double maxLoad = perAdapterMax.Values.Max();
        return Math.Round(Math.Clamp(maxLoad, 0.0, 100.0), 1);
    }

    private static string ExtractAdapterKey(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return "default";

        var luidMatch = LuidRegex.Match(instanceName);
        if (luidMatch.Success)
            return $"luid:{luidMatch.Groups[1].Value}";

        var physMatch = PhysRegex.Match(instanceName);
        if (physMatch.Success)
            return $"phys:{physMatch.Groups[1].Value}";

        return "default";
    }
}
