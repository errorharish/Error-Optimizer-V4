#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BiosOptimizer.Core.Common;
using BiosOptimizer.Core.Implementations.Config;

namespace BiosOptimizer.Core.Implementations.Diagnostics;

public sealed record AuditLogEntry(
    DateTime TimestampUtc,
    string Category,
    string Action,
    string Status,
    string Message,
    bool IsDetailed = false);

/// <summary>
/// Thread-safe execution & policy logger under <c>%ProgramData%\ErrorOptimizer\logs</c>.
/// Retention sweeps operate strictly inside <c>logs\</c> and never touch safety backup snapshots.
/// </summary>
public static class AuditLogManager
{
    private static readonly object _sync = new();

    public static string LogsDirectory =>
        AppEnvironment.ResolveWritableDataDirectory("logs");

    private static string TodayLogPath =>
        Path.Combine(LogsDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");

    public static void Write(string category, string action, string status, string message, bool detailedOnly = false)
    {
        try
        {
            var settings = UnifiedSettingsStore.LoadSafe();
            if (detailedOnly && !settings.DetailedLogs)
                return;

            var entry = new AuditLogEntry(DateTime.UtcNow, category, action, status, message, detailedOnly);
            string line = JsonSerializer.Serialize(entry);

            lock (_sync)
            {
                Directory.CreateDirectory(LogsDirectory);
                File.AppendAllText(TodayLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    public static List<AuditLogEntry> ReadRecent(int maxEntries = 300)
    {
        var results = new List<AuditLogEntry>();
        int limit = Math.Clamp(maxEntries, 1, 2000);

        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(LogsDirectory))
                    return results;

                var files = Directory.GetFiles(LogsDirectory, "audit-*.jsonl")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    if (results.Count >= limit) break;
                    string[] lines = File.ReadAllLines(file);
                    for (int i = lines.Length - 1; i >= 0 && results.Count < limit; i--)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        try
                        {
                            var parsed = JsonSerializer.Deserialize<AuditLogEntry>(lines[i]);
                            if (parsed != null) results.Add(parsed);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        return results;
    }

    public static (string text, int entryCount, string? exportedFilePath) ExportLogs(int maxEntries = 300, string? targetFilePath = null)
    {
        var entries = ReadRecent(maxEntries);
        var sb = new StringBuilder();
        sb.AppendLine($"ERROR OPTIMIZER Audit Log Export — Generated {DateTime.UtcNow:O}");
        sb.AppendLine($"Entries: {entries.Count}");
        sb.AppendLine(new string('-', 72));

        foreach (var e in entries.OrderBy(x => x.TimestampUtc))
        {
            sb.AppendLine($"[{e.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC] [{e.Category}] [{e.Status}] {e.Action} — {e.Message}");
        }

        string text = sb.ToString();
        string? outPath = targetFilePath;

        if (string.IsNullOrWhiteSpace(outPath))
        {
            Directory.CreateDirectory(LogsDirectory);
            outPath = Path.Combine(LogsDirectory, $"export-{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
        }

        try
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, text, Encoding.UTF8);
        }
        catch
        {
            outPath = null;
        }

        return (text, entries.Count, outPath);
    }

    /// <summary>
    /// Purges expired log files inside LogsDirectory ONLY. Never touches BackupManager snapshots or Rollback stores.
    /// </summary>
    public static int PurgeExpiredLogs(int retentionDays)
    {
        int clampedDays = Math.Clamp(retentionDays, 1, 365);
        int deleted = 0;
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-clampedDays);

        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(LogsDirectory))
                    return 0;

                foreach (var file in Directory.GetFiles(LogsDirectory, "*.*"))
                {
                    string ext = Path.GetExtension(file);
                    if (!ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".log", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.LastWriteTimeUtc < cutoffUtc &&
                            !string.Equals(fi.FullName, TodayLogPath, StringComparison.OrdinalIgnoreCase))
                        {
                            fi.Delete();
                            deleted++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return deleted;
    }
}
