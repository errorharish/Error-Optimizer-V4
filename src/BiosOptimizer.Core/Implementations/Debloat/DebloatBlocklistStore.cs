#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BiosOptimizer.Core.Common;

namespace BiosOptimizer.Core.Implementations.Debloat;

/// <summary>
/// Persists user-defined AppX/capability exclusion IDs that must never be removed by Debloat flows.
/// </summary>
public static class DebloatBlocklistStore
{
    private static readonly object _sync = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static string StoreFilePath =>
        Path.Combine(AppEnvironment.ResolveWritableDataDirectory(), "debloat-blocklist.json");

    public static List<string> LoadBlockedIds()
    {
        lock (_sync)
        {
            try
            {
                string readPath = AppEnvironment.ResolveDataReadPath("debloat-blocklist.json");
                if (!File.Exists(readPath))
                    return new List<string>();

                string json = File.ReadAllText(readPath);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<string>();

                var parsed = JsonSerializer.Deserialize<List<string>>(json);
                return (parsed ?? new List<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    public static IReadOnlyList<string> SaveBlockedIds(IEnumerable<string> ids)
    {
        var normalized = (ids ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_sync)
        {
            string? dir = Path.GetDirectoryName(StoreFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = StoreFilePath + ".tmp";
            string json = JsonSerializer.Serialize(normalized, _jsonOptions);
            File.WriteAllText(tmpPath, json);

            if (File.Exists(StoreFilePath))
                File.Replace(tmpPath, StoreFilePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tmpPath, StoreFilePath, overwrite: true);
        }

        return normalized;
    }

    public static bool IsBlocked(string packageIdentifier)
    {
        if (string.IsNullOrWhiteSpace(packageIdentifier))
            return false;

        var list = LoadBlockedIds();
        return list.Any(b =>
            packageIdentifier.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            packageIdentifier.Contains(b, StringComparison.OrdinalIgnoreCase));
    }
}
