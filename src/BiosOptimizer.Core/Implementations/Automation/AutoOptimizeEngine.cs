#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using BiosOptimizer.Core.Implementations.Cleaners;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Storage;

namespace BiosOptimizer.Core.Implementations.Automation;

/// <summary>
/// Headless recurring maintenance engine implementing all 10 Auto Optimize tasks, running-browser lock guards,
/// 24-hour file age protection, newest crash-dump preservation, overlap synchronization, and atomic config/history stores.
/// </summary>
public sealed class AutoOptimizeEngine
{
    private static readonly Lazy<AutoOptimizeEngine> _instance = new(() => new AutoOptimizeEngine());
    public static AutoOptimizeEngine Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ValidTaskIds = new(AutoOptimizeTaskIds.All, StringComparer.OrdinalIgnoreCase);

    private readonly object _fileLock = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private bool _isRunning;
    private bool _hasQueuedRun;
    private DateTime? _nextRunUtc;
    private CancellationTokenSource? _activeRunCts;

    public static string RootDirectory =>
        BiosOptimizer.Core.Common.AppEnvironment.ResolveWritableDataDirectory();

    public static string ConfigPath => Path.Combine(RootDirectory, "auto-optimize.config.json");
    public static string HistoryPath => Path.Combine(RootDirectory, "auto-optimize-history.json");

    public AutoOptimizeConfig LoadConfig()
    {
        lock (_fileLock)
        {
            try
            {
                string readPath = BiosOptimizer.Core.Common.AppEnvironment.ResolveDataReadPath("auto-optimize.config.json");
                if (!File.Exists(readPath))
                    return SanitizeConfig(new AutoOptimizeConfig());

                string json = File.ReadAllText(readPath);
                if (string.IsNullOrWhiteSpace(json))
                    return SanitizeConfig(new AutoOptimizeConfig());

                return SanitizeConfig(JsonSerializer.Deserialize<AutoOptimizeConfig>(json, _jsonOptions));
            }
            catch
            {
                return SanitizeConfig(new AutoOptimizeConfig());
            }
        }
    }

    public AutoOptimizeConfig SaveConfig(AutoOptimizeConfig incoming)
    {
        var sanitized = SanitizeConfig(incoming);
        WriteAtomicJson(ConfigPath, sanitized);

        if (sanitized.Enabled && !sanitized.Paused)
            _nextRunUtc = DateTime.UtcNow.AddMinutes(sanitized.IntervalMin);
        else
            _nextRunUtc = null;

        return sanitized;
    }

    public void SyncWithGlobalConfig(Config.UnifiedAppSettings settings)
    {
        if (settings == null) return;
        SyncWithGlobalConfig(settings.AutoOptimizationEnabled, settings.MonitoringInterval);
    }

    public void SyncWithGlobalConfig(bool enabled, int intervalMin)
    {
        var cfg = LoadConfig();
        bool changed = false;
        if (cfg.Enabled != enabled)
        {
            cfg.Enabled = enabled;
            changed = true;
        }
        if (intervalMin > 0 && cfg.IntervalMin != intervalMin)
        {
            cfg.IntervalMin = intervalMin;
            changed = true;
        }
        if (changed)
        {
            SaveConfig(cfg);
        }
    }


    public List<AutoOptimizeHistoryEntry> LoadHistory()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(HistoryPath))
                    return new List<AutoOptimizeHistoryEntry>();

                string json = File.ReadAllText(HistoryPath);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<AutoOptimizeHistoryEntry>();

                return JsonSerializer.Deserialize<List<AutoOptimizeHistoryEntry>>(json, _jsonOptions)
                    ?? new List<AutoOptimizeHistoryEntry>();
            }
            catch
            {
                return new List<AutoOptimizeHistoryEntry>();
            }
        }
    }

    public void ClearHistory()
    {
        WriteAtomicJson(HistoryPath, new List<AutoOptimizeHistoryEntry>());
    }

    public void SetNextRunUtc(DateTime? nextUtc) => _nextRunUtc = nextUtc;

    public AutoOptimizeStateSnapshot GetStateSnapshot()
    {
        var cfg = LoadConfig();
        var history = LoadHistory();
        string status = _isRunning
            ? "Running"
            : !cfg.Enabled
                ? "Disabled"
                : cfg.Paused
                    ? "Paused"
                    : "Enabled";

        if (cfg.Enabled && !cfg.Paused && !_nextRunUtc.HasValue)
            _nextRunUtc = DateTime.UtcNow.AddMinutes(cfg.IntervalMin);

        return new AutoOptimizeStateSnapshot
        {
            Config = cfg,
            Status = status,
            IsRunning = _isRunning,
            Queued = _hasQueuedRun,
            NextRunUtc = cfg.Enabled && !cfg.Paused ? _nextRunUtc : null,
            LastRunUtc = history.FirstOrDefault()?.TimestampUtc,
            History = history
        };
    }

    public bool CancelActiveRun()
    {
        try
        {
            _hasQueuedRun = false;
            _activeRunCts?.Cancel();
            return _isRunning;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<AutoTaskScanInfo>> ScanTasksAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var cfg = LoadConfig();
            var cutoffUtc = cfg.SkipRecentTemp ? DateTime.UtcNow.AddHours(-24) : DateTime.MaxValue;
            var list = new List<AutoTaskScanInfo>();

            foreach (var id in AutoOptimizeTaskIds.All)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(ScanSingleTask(id, cutoffUtc));
            }

            return list;
        }, ct).ConfigureAwait(false);
    }

    public async Task<AutoOptimizeHistoryEntry> ExecuteAsync(
        string trigger,
        IReadOnlyList<string>? overrideTasks = null,
        bool manualRecycleConfirmed = false,
        CancellationToken externalCt = default)
    {
        var cfg = LoadConfig();

        if (!_runGate.Wait(0))
        {
            if (cfg.Overlap.Equals("queue", StringComparison.OrdinalIgnoreCase) && !_hasQueuedRun)
            {
                _hasQueuedRun = true;
                await _runGate.WaitAsync(externalCt).ConfigureAwait(false);
                _hasQueuedRun = false;
            }
            else
            {
                return new AutoOptimizeHistoryEntry
                {
                    Trigger = trigger,
                    Status = "Skipped",
                    Skipped = 1,
                    Tasks = new List<AutoTaskExecutionDetail>
                    {
                        new() { Id = "scheduler", Outcome = "Skipped", Reason = "Skipped overlapping run because another Auto Optimize pass is already running." }
                    }
                };
            }
        }

        _isRunning = true;
        _activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _activeRunCts.Token;
        var sw = Stopwatch.StartNew();

        try
        {
            var selectedTasks = (overrideTasks ?? (IReadOnlyList<string>)cfg.Tasks)
                .Where(t => ValidTaskIds.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedTasks.Count == 0)
            {
                sw.Stop();
                return new AutoOptimizeHistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    Trigger = trigger,
                    Status = "Skipped",
                    Skipped = 0,
                    DurationMs = sw.ElapsedMilliseconds,
                    Tasks = new List<AutoTaskExecutionDetail>
                    {
                        new() { Id = "scheduler", Outcome = "Skipped", Reason = "No Auto Optimize tasks are selected." }
                    }
                };
            }

            bool unattended = !trigger.Equals("Manual", StringComparison.OrdinalIgnoreCase);
            var details = new List<AutoTaskExecutionDetail>();
            DateTime tempCutoffUtc = cfg.SkipRecentTemp ? DateTime.UtcNow.AddHours(-24) : DateTime.MaxValue;

            foreach (var taskId in selectedTasks)
            {
                if (ct.IsCancellationRequested)
                {
                    details.Add(new AutoTaskExecutionDetail { Id = taskId, Outcome = "Cancelled", Reason = "Cancelled before task execution." });
                    continue;
                }

                // Task-level guardrails matching autoOptimize.ts planRun()
                if (taskId.Equals(AutoOptimizeTaskIds.SysShader, StringComparison.OrdinalIgnoreCase) && cfg.SafeOnly && unattended)
                {
                    details.Add(new AutoTaskExecutionDetail { Id = taskId, Outcome = "Skipped", Reason = "Safe-only mode — LOW-risk shader task skipped during unattended run." });
                    continue;
                }

                if (taskId.Equals(AutoOptimizeTaskIds.RecycleBin, StringComparison.OrdinalIgnoreCase))
                {
                    if (unattended && cfg.RecycleConfirm)
                    {
                        details.Add(new AutoTaskExecutionDetail { Id = taskId, Outcome = "Skipped", Reason = "Needs confirmation — skipped during unattended run." });
                        continue;
                    }
                    if (!unattended && cfg.RecycleConfirm && !manualRecycleConfirmed)
                    {
                        details.Add(new AutoTaskExecutionDetail { Id = taskId, Outcome = "Skipped", Reason = "Recycle Bin emptying not explicitly confirmed." });
                        continue;
                    }
                }

                var outcome = await ExecuteTaskAsync(taskId, tempCutoffUtc, ct).ConfigureAwait(false);
                details.Add(outcome);
            }

            sw.Stop();

            int completed = details.Count(d => d.Outcome == "Completed");
            int skipped = details.Count(d => d.Outcome == "Skipped");
            int cancelled = details.Count(d => d.Outcome == "Cancelled");
            string status = cancelled > 0
                ? "Cancelled"
                : completed == 0
                    ? "Skipped"
                    : skipped > 0
                        ? "Partial"
                        : "Completed";

            var entry = new AutoOptimizeHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Trigger = trigger,
                Status = status,
                Completed = completed,
                Skipped = skipped,
                Cancelled = cancelled,
                TotalReclaimedBytes = details.Sum(d => d.ReclaimedBytes),
                DurationMs = sw.ElapsedMilliseconds,
                Tasks = details
            };

            AppendHistory(entry);
            AuditLogManager.Write("AutoOptimize", trigger, status,
                $"Completed={completed}, Skipped={skipped}, Reclaimed={StorageCleanupCategory.FormatBytes(entry.TotalReclaimedBytes)}, Elapsed={entry.DurationMs}ms.");

            if (cfg.Enabled && !cfg.Paused)
                _nextRunUtc = DateTime.UtcNow.AddMinutes(cfg.IntervalMin);

            return entry;
        }
        finally
        {
            _isRunning = false;
            _activeRunCts?.Dispose();
            _activeRunCts = null;
            _runGate.Release();
        }
    }

    private AutoTaskScanInfo ScanSingleTask(string id, DateTime tempCutoffUtc)
    {
        switch (id)
        {
            case AutoOptimizeTaskIds.TempWindows:
            {
                string winTemp = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Temp");
                var (bytes, count) = MeasureDirectory(winTemp, tempCutoffUtc);
                return new AutoTaskScanInfo { Id = id, EstimatedBytes = bytes, FileCount = count, StatusNote = $"{count} eligible files" };
            }
            case AutoOptimizeTaskIds.TempUser:
            {
                long total = 0; int count = 0;
                foreach (var dir in ResolveUserTempDirectories())
                {
                    var (b, c) = MeasureDirectory(dir, tempCutoffUtc);
                    total += b; count += c;
                }
                return new AutoTaskScanInfo { Id = id, EstimatedBytes = total, FileCount = count, StatusNote = $"{count} eligible files" };
            }
            case AutoOptimizeTaskIds.TempCrash:
            {
                var files = EnumerateCrashDumpsOrderedNewestFirst();
                var candidates = files.Skip(1).ToList(); // Preserve newest 1 dump!
                return new AutoTaskScanInfo
                {
                    Id = id,
                    EstimatedBytes = candidates.Sum(f => f.Length),
                    FileCount = candidates.Count,
                    StatusNote = files.Count > 0 ? $"Preserving newest dump; {candidates.Count} older dump(s)" : "No crash dumps"
                };
            }
            case AutoOptimizeTaskIds.MemStandby:
            {
                var scan = new RamCleanupEngine().Scan();
                return new AutoTaskScanInfo { Id = id, EstimatedBytes = scan.TotalSizeInBytes, FileCount = scan.FileCount, StatusNote = "Standby list purge ready" };
            }
            case AutoOptimizeTaskIds.BrowserChrome:
            {
                if (!IsBrowserInstalled("chrome"))
                {
                    return new AutoTaskScanInfo
                    {
                        Id = id,
                        EstimatedBytes = 0,
                        FileCount = 0,
                        BrowserRunning = false,
                        Installed = false,
                        StatusNote = "Google Chrome is not installed on this PC"
                    };
                }
                bool running = IsProcessRunning("chrome");
                var (b, c) = MeasurePaths(ResolveChromiumCacheDirectories(@"Google\Chrome\User Data"));
                return new AutoTaskScanInfo
                {
                    Id = id,
                    EstimatedBytes = b,
                    FileCount = c,
                    BrowserRunning = running,
                    Installed = true,
                    StatusNote = running ? "Chrome is running — will skip" : $"{c} cache files"
                };
            }
            case AutoOptimizeTaskIds.BrowserEdge:
            {
                if (!IsBrowserInstalled("msedge"))
                {
                    return new AutoTaskScanInfo
                    {
                        Id = id,
                        EstimatedBytes = 0,
                        FileCount = 0,
                        BrowserRunning = false,
                        Installed = false,
                        StatusNote = "Microsoft Edge is not installed on this PC"
                    };
                }
                bool running = IsProcessRunning("msedge");
                var (b, c) = MeasurePaths(ResolveChromiumCacheDirectories(@"Microsoft\Edge\User Data"));
                return new AutoTaskScanInfo
                {
                    Id = id,
                    EstimatedBytes = b,
                    FileCount = c,
                    BrowserRunning = running,
                    Installed = true,
                    StatusNote = running ? "Edge is running — will skip" : $"{c} cache files"
                };
            }
            case AutoOptimizeTaskIds.BrowserFirefox:
            {
                if (!IsBrowserInstalled("firefox"))
                {
                    return new AutoTaskScanInfo
                    {
                        Id = id,
                        EstimatedBytes = 0,
                        FileCount = 0,
                        BrowserRunning = false,
                        Installed = false,
                        StatusNote = "Mozilla Firefox is not installed on this PC"
                    };
                }
                bool running = IsProcessRunning("firefox");
                var (b, c) = MeasurePaths(ResolveFirefoxCacheDirectories());
                return new AutoTaskScanInfo
                {
                    Id = id,
                    EstimatedBytes = b,
                    FileCount = c,
                    BrowserRunning = running,
                    Installed = true,
                    StatusNote = running ? "Firefox is running — will skip" : $"{c} cache files"
                };
            }
            case AutoOptimizeTaskIds.RecycleBin:
            {
                var (b, c) = new StorageCleanerEngine().GetRecycleBinInfo();
                return new AutoTaskScanInfo { Id = id, EstimatedBytes = b, FileCount = c, StatusNote = $"{c} item(s) in Recycle Bin" };
            }
            case AutoOptimizeTaskIds.SysShader:
            {
                var scan = new ShaderCacheCleanerEngine().Scan();
                return new AutoTaskScanInfo { Id = id, EstimatedBytes = scan.TotalSizeInBytes, FileCount = scan.FileCount, StatusNote = $"{scan.FileCount} shader cache files" };
            }
            case AutoOptimizeTaskIds.SysDns:
                return new AutoTaskScanInfo { Id = id, EstimatedBytes = 0, FileCount = 0, StatusNote = "DNS resolver flush ready" };
            default:
                return new AutoTaskScanInfo { Id = id };
        }
    }

    private async Task<AutoTaskExecutionDetail> ExecuteTaskAsync(string taskId, DateTime tempCutoffUtc, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            switch (taskId)
            {
                case AutoOptimizeTaskIds.TempWindows:
                {
                    string winTemp = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Temp");
                    var (bytes, deleted, skipped) = CleanDirectoryWithAgeFilter(winTemp, tempCutoffUtc, ct);
                    bool alreadyClean = deleted == 0 && skipped == 0;
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = "Completed",
                        Status = alreadyClean ? "ALREADY_OPTIMIZED" : "VERIFIED",
                        Verified = true,
                        AlreadyOptimized = alreadyClean,
                        ReclaimedBytes = bytes,
                        FilesDeleted = deleted,
                        Reason = alreadyClean
                            ? "Windows temporary directory already clean (0 temporary files present)."
                            : (deleted > 0
                                ? $"Removed {deleted} file(s) ({StorageCleanupCategory.FormatBytes(bytes)} reclaimed){(skipped > 0 ? $", safely preserved {skipped} in-use/recent files" : "")}."
                                : $"Temporary directory maintained ({skipped} in-use files safely preserved).")
                    };
                }
                case AutoOptimizeTaskIds.TempUser:
                {
                    long totalBytes = 0; int totalDeleted = 0; int totalSkipped = 0;
                    foreach (var dir in ResolveUserTempDirectories())
                    {
                        var (b, d, s) = CleanDirectoryWithAgeFilter(dir, tempCutoffUtc, ct);
                        totalBytes += b; totalDeleted += d; totalSkipped += s;
                    }
                    bool alreadyClean = totalDeleted == 0 && totalSkipped == 0;
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = "Completed",
                        Status = alreadyClean ? "ALREADY_OPTIMIZED" : "VERIFIED",
                        Verified = true,
                        AlreadyOptimized = alreadyClean,
                        ReclaimedBytes = totalBytes,
                        FilesDeleted = totalDeleted,
                        Reason = alreadyClean
                            ? "User temporary directories already clean (0 temporary files present)."
                            : (totalDeleted > 0
                                ? $"Removed {totalDeleted} user temp file(s) ({StorageCleanupCategory.FormatBytes(totalBytes)} reclaimed){(totalSkipped > 0 ? $", safely preserved {totalSkipped} in-use/recent files" : "")}."
                                : $"User temporary directories maintained ({totalSkipped} in-use files safely preserved).")
                    };
                }
                case AutoOptimizeTaskIds.TempCrash:
                {
                    var ordered = EnumerateCrashDumpsOrderedNewestFirst();
                    if (ordered.Count <= 1)
                    {
                        return new AutoTaskExecutionDetail
                        {
                            Id = taskId,
                            Outcome = "Completed",
                            Status = "ALREADY_OPTIMIZED",
                            Verified = true,
                            AlreadyOptimized = true,
                            Reason = ordered.Count == 1
                                ? "Kept newest crash dump for diagnostics (0 older dumps to delete; directory optimal)."
                                : "Crash dump directories already clean (0 crash dumps present)."
                        };
                    }

                    long reclaimed = 0; int deleted = 0;
                    foreach (var fi in ordered.Skip(1))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            long len = fi.Length;
                            if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes &= ~FileAttributes.ReadOnly;
                            fi.Delete();
                            reclaimed += len;
                            deleted++;
                        }
                        catch { }
                    }
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = "Completed",
                        Status = "VERIFIED",
                        Verified = true,
                        AlreadyOptimized = false,
                        ReclaimedBytes = reclaimed,
                        FilesDeleted = deleted,
                        Reason = $"Removed {deleted} older crash dump(s) ({StorageCleanupCategory.FormatBytes(reclaimed)} reclaimed); preserved newest dump ({ordered[0].Name})."
                    };
                }
                case AutoOptimizeTaskIds.MemStandby:
                {
                    var detail = new RamCleanupEngine().CleanWithDetails(ct);
                    bool alreadyClean = detail.Succeeded && detail.StandbyReclaimedBytes == 0 && detail.TotalReclaimedBytes == 0;
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = detail.Succeeded ? "Completed" : (detail.RequiresAdmin ? "Failed" : "Skipped"),
                        Status = detail.Succeeded ? (alreadyClean ? "ALREADY_OPTIMIZED" : "VERIFIED") : (detail.RequiresAdmin ? "FAILED_ATTEMPTED" : "UNVERIFIED"),
                        Verified = detail.Succeeded,
                        AlreadyOptimized = alreadyClean,
                        ReclaimedBytes = detail.TotalReclaimedBytes,
                        Reason = alreadyClean ? "RAM standby cache already clean (0 bytes in standby list)." : detail.Message
                    };
                }
                case AutoOptimizeTaskIds.BrowserChrome:
                    return CleanBrowserTask(taskId, "chrome", "Google Chrome", "chrome", ResolveChromiumCacheDirectories(@"Google\Chrome\User Data"), ct);
                case AutoOptimizeTaskIds.BrowserEdge:
                    return CleanBrowserTask(taskId, "msedge", "Microsoft Edge", "msedge", ResolveChromiumCacheDirectories(@"Microsoft\Edge\User Data"), ct);
                case AutoOptimizeTaskIds.BrowserFirefox:
                    return CleanBrowserTask(taskId, "firefox", "Mozilla Firefox", "firefox", ResolveFirefoxCacheDirectories(), ct);
                case AutoOptimizeTaskIds.RecycleBin:
                {
                    var (ok, freed, msg) = new StorageCleanerEngine().EmptyRecycleBin();
                    bool alreadyClean = ok && freed == 0;
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = ok ? "Completed" : "Skipped",
                        Status = ok ? (alreadyClean ? "ALREADY_OPTIMIZED" : "VERIFIED") : "FAILED_ATTEMPTED",
                        Verified = ok,
                        AlreadyOptimized = alreadyClean,
                        ReclaimedBytes = freed,
                        Reason = alreadyClean ? "Recycle Bin already empty." : msg
                    };
                }
                case AutoOptimizeTaskIds.SysShader:
                {
                    long freed = new ShaderCacheCleanerEngine().Clean();
                    bool alreadyClean = freed == 0;
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = "Completed",
                        Status = alreadyClean ? "ALREADY_OPTIMIZED" : "VERIFIED",
                        Verified = true,
                        AlreadyOptimized = alreadyClean,
                        ReclaimedBytes = freed,
                        Reason = alreadyClean
                            ? "DirectX / GPU shader caches already clean (0 bytes present)."
                            : $"Cleared {StorageCleanupCategory.FormatBytes(freed)} shader cache."
                    };
                }
                case AutoOptimizeTaskIds.SysDns:
                {
                    var res = await new NetworkOptimizerEngine(BackupManager.Instance, new NetworkDiagnosticsService())
                        .FlushDnsCacheAsync().ConfigureAwait(false);
                    return new AutoTaskExecutionDetail
                    {
                        Id = taskId,
                        Outcome = res.Success ? "Completed" : "Failed",
                        Status = res.Success ? "VERIFIED" : "FAILED_ATTEMPTED",
                        Verified = res.Success,
                        AlreadyOptimized = false,
                        Reason = res.Message
                    };
                }
                default:
                    return new AutoTaskExecutionDetail { Id = taskId, Outcome = "Skipped", Status = "SKIPPED_BUSY", Reason = "Unknown task ID." };
            }
        }, ct).ConfigureAwait(false);
    }

    private static AutoTaskExecutionDetail CleanBrowserTask(
        string taskId, string browserKey, string browserDisplay, string processName, IEnumerable<string> cacheDirs, CancellationToken ct)
    {
        // 1. Defect 2: Check genuine installation evidence first!
        if (!IsBrowserInstalled(browserKey))
        {
            return new AutoTaskExecutionDetail
            {
                Id = taskId,
                Outcome = "Skipped",
                Status = "NOT_INSTALLED",
                Verified = false,
                AlreadyOptimized = false,
                ReclaimedBytes = 0,
                FilesDeleted = 0,
                Reason = $"{browserDisplay} is not installed on this PC."
            };
        }

        // 2. Running browser lock guard -> SKIPPED_BUSY
        if (IsProcessRunning(processName))
        {
            return new AutoTaskExecutionDetail
            {
                Id = taskId,
                Outcome = "Skipped",
                Status = "SKIPPED_BUSY",
                Verified = false,
                AlreadyOptimized = false,
                ReclaimedBytes = 0,
                FilesDeleted = 0,
                Reason = $"{browserDisplay} is currently running — skipped to prevent file access contention."
            };
        }

        // 3. Installed and not running: clean cache
        long bytes = 0; int deleted = 0; int skipped = 0;
        foreach (var dir in cacheDirs)
        {
            var (b, d, s) = CleanDirectoryWithAgeFilter(dir, DateTime.MaxValue, ct);
            bytes += b; deleted += d; skipped += s;
        }

        bool alreadyClean = deleted == 0 && skipped == 0;
        return new AutoTaskExecutionDetail
        {
            Id = taskId,
            Outcome = "Completed",
            Status = alreadyClean ? "ALREADY_OPTIMIZED" : "VERIFIED",
            Verified = true,
            AlreadyOptimized = alreadyClean,
            ReclaimedBytes = bytes,
            FilesDeleted = deleted,
            Reason = alreadyClean
                ? $"{browserDisplay} cache already clean (0 files present)."
                : (deleted > 0
                    ? $"Removed {deleted} {browserDisplay} cache file(s) ({StorageCleanupCategory.FormatBytes(bytes)} reclaimed)."
                    : $"{browserDisplay} cache maintained ({skipped} locked files preserved).")
        };
    }

    public static bool IsBrowserInstalled(string browserKey)
    {
        try
        {
            string key = browserKey.ToLowerInvariant().Trim();
            switch (key)
            {
                case "firefox":
                case "browser-firefox":
                case "mozilla":
                    return CheckFirefoxInstalled();

                case "chrome":
                case "browser-chrome":
                case "googlechrome":
                    return CheckChromeInstalled();

                case "edge":
                case "msedge":
                case "browser-edge":
                    return CheckEdgeInstalled();

                case "brave":
                case "browser-brave":
                    return CheckBraveInstalled();

                case "opera":
                case "browser-opera":
                    return CheckOperaInstalled();

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckFirefoxInstalled()
    {
        string[] regKeys = new[]
        {
            @"SOFTWARE\Mozilla\Mozilla Firefox",
            @"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe"
        };
        foreach (var rk in regKeys)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(rk);
                if (k != null) return true;
            }
            catch { }
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(rk);
                if (k != null) return true;
            }
            catch { }
        }

        string prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (File.Exists(Path.Combine(prog, "Mozilla Firefox", "firefox.exe"))) return true;
        if (File.Exists(Path.Combine(progX86, "Mozilla Firefox", "firefox.exe"))) return true;
        if (File.Exists(Path.Combine(local, "Mozilla Firefox", "firefox.exe"))) return true;

        return false;
    }

    private static bool CheckChromeInstalled()
    {
        string[] regKeys = new[]
        {
            @"SOFTWARE\Google\Chrome",
            @"SOFTWARE\Clients\StartMenuInternet\Google Chrome",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"
        };
        foreach (var rk in regKeys)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(rk);
                if (k != null) return true;
            }
            catch { }
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(rk);
                if (k != null) return true;
            }
            catch { }
        }

        string prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (File.Exists(Path.Combine(prog, "Google", "Chrome", "Application", "chrome.exe"))) return true;
        if (File.Exists(Path.Combine(progX86, "Google", "Chrome", "Application", "chrome.exe"))) return true;
        if (File.Exists(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"))) return true;

        return false;
    }

    private static bool CheckEdgeInstalled()
    {
        string[] regKeys = new[]
        {
            @"SOFTWARE\Microsoft\Edge",
            @"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"
        };
        foreach (var rk in regKeys)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(rk);
                if (k != null) return true;
            }
            catch { }
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(rk);
                if (k != null) return true;
            }
            catch { }
        }

        string prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (File.Exists(Path.Combine(progX86, "Microsoft", "Edge", "Application", "msedge.exe"))) return true;
        if (File.Exists(Path.Combine(prog, "Microsoft", "Edge", "Application", "msedge.exe"))) return true;

        return false;
    }

    private static bool CheckBraveInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\BraveSoftware\Brave");
            if (k != null) return true;
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe");
            if (k != null) return true;
        }
        catch { }

        string prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (File.Exists(Path.Combine(prog, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"))) return true;
        if (File.Exists(Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"))) return true;

        return false;
    }

    private static bool CheckOperaInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\opera.exe");
            if (k != null) return true;
        }
        catch { }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (File.Exists(Path.Combine(local, "Programs", "Opera", "opera.exe"))) return true;
        if (File.Exists(Path.Combine(prog, "Opera", "opera.exe"))) return true;

        return false;
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ResolveUserProfileDirs()
    {
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) list.Add(current);

        try
        {
            string usersRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
            if (Directory.Exists(usersRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(usersRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add(dir);
                }
            }
        }
        catch { }

        return list.ToList();
    }

    private static IEnumerable<string> ResolveUserTempDirectories()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string currentTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(currentTemp)) dirs.Add(currentTemp);

        foreach (var profile in ResolveUserProfileDirs())
        {
            string t = Path.Combine(profile, "AppData", "Local", "Temp");
            if (Directory.Exists(t)) dirs.Add(t);
        }
        return dirs;
    }

    private static IEnumerable<string> ResolveChromiumCacheDirectories(string relativeUserDataPath)
    {
        var dirs = new List<string>();
        foreach (var user in ResolveUserProfileDirs())
        {
            string userData = Path.Combine(user, "AppData", "Local", relativeUserDataPath);
            if (!Directory.Exists(userData)) continue;

            try
            {
                foreach (var profileDir in Directory.EnumerateDirectories(userData))
                {
                    string folderName = Path.GetFileName(profileDir);
                    if (!folderName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                        !folderName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                    {
                        string candidate = Path.Combine(profileDir, sub);
                        if (Directory.Exists(candidate)) dirs.Add(candidate);
                    }
                }
            }
            catch { }
        }
        return dirs;
    }

    private static IEnumerable<string> ResolveFirefoxCacheDirectories()
    {
        var dirs = new List<string>();
        foreach (var user in ResolveUserProfileDirs())
        {
            string profiles = Path.Combine(user, "AppData", "Local", "Mozilla", "Firefox", "Profiles");
            if (!Directory.Exists(profiles)) continue;

            try
            {
                foreach (var p in Directory.EnumerateDirectories(profiles))
                {
                    foreach (var sub in new[] { "cache2", "startupCache", "shader-cache" })
                    {
                        string candidate = Path.Combine(p, sub);
                        if (Directory.Exists(candidate)) dirs.Add(candidate);
                    }
                }
            }
            catch { }
        }
        return dirs;
    }

    private static List<FileInfo> EnumerateCrashDumpsOrderedNewestFirst()
    {
        var list = new List<FileInfo>();
        string winDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        string memDmp = Path.Combine(winDir, "MEMORY.DMP");
        if (File.Exists(memDmp))
        {
            try { list.Add(new FileInfo(memDmp)); } catch { }
        }

        var roots = new List<string> { Path.Combine(winDir, "Minidump") };
        foreach (var profile in ResolveUserProfileDirs())
        {
            roots.Add(Path.Combine(profile, "AppData", "Local", "CrashDumps"));
            roots.Add(Path.Combine(profile, "AppData", "Local", "Temp", "WER"));
        }

        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            foreach (var f in StorageCleanerEngine.SafeEnumerateFiles(r))
            {
                try { list.Add(new FileInfo(f)); } catch { }
            }
        }

        return list.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    private static (long bytes, int count) MeasurePaths(IEnumerable<string> dirs)
    {
        long total = 0; int count = 0;
        foreach (var d in dirs)
        {
            var (b, c) = MeasureDirectory(d, DateTime.MaxValue);
            total += b; count += c;
        }
        return (total, count);
    }

    private static (long bytes, int count) MeasureDirectory(string dir, DateTime lastWriteCutoffUtc)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        long bytes = 0; int count = 0;
        foreach (var f in StorageCleanerEngine.SafeEnumerateFiles(dir))
        {
            try
            {
                var fi = new FileInfo(f);
                if (lastWriteCutoffUtc != DateTime.MaxValue && fi.LastWriteTimeUtc > lastWriteCutoffUtc)
                    continue;
                bytes += fi.Length;
                count++;
            }
            catch { }
        }
        return (bytes, count);
    }

    private static (long bytes, int deleted, int skipped) CleanDirectoryWithAgeFilter(string dir, DateTime lastWriteCutoffUtc, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return (0, 0, 0);
        long reclaimed = 0; int deleted = 0; int skipped = 0;
        foreach (var f in StorageCleanerEngine.SafeEnumerateFiles(dir, ct))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var fi = new FileInfo(f);
                if (lastWriteCutoffUtc != DateTime.MaxValue && fi.LastWriteTimeUtc > lastWriteCutoffUtc)
                {
                    skipped++;
                    continue;
                }

                long len = fi.Length;
                if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes &= ~FileAttributes.ReadOnly;
                fi.Delete();

                if (!File.Exists(f))
                {
                    reclaimed += len;
                    deleted++;
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                skipped++;
            }
        }
        return (reclaimed, deleted, skipped);
    }

    private void AppendHistory(AutoOptimizeHistoryEntry entry)
    {
        var list = LoadHistory();
        list.Insert(0, entry);
        if (list.Count > 100) list.RemoveRange(100, list.Count - 100);
        WriteAtomicJson(HistoryPath, list);
    }

    private void WriteAtomicJson<T>(string path, T value)
    {
        lock (_fileLock)
        {
            Directory.CreateDirectory(RootDirectory);
            string tmp = path + $".{Environment.ProcessId}.tmp";
            string json = JsonSerializer.Serialize(value, _jsonOptions);
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    public static AutoOptimizeConfig SanitizeConfig(AutoOptimizeConfig? raw)
    {
        var c = raw ?? new AutoOptimizeConfig();
        return new AutoOptimizeConfig
        {
            Enabled = c.Enabled,
            Paused = c.Paused,
            Tasks = (c.Tasks ?? new List<string>())
                .Where(t => ValidTaskIds.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IntervalMin = Math.Clamp(c.IntervalMin <= 0 ? 30 : c.IntervalMin, 1, 1440),
            RunOnLaunch = c.RunOnLaunch,
            CloseBehavior = c.CloseBehavior is "exit-run" or "background" ? c.CloseBehavior : "stop",
            RunOnWindowsStartup = c.RunOnWindowsStartup,
            StartupPolicy = c.StartupPolicy == "next-interval" ? "next-interval" : "after-delay",
            Overlap = c.Overlap == "queue" ? "queue" : "skip",
            SafeOnly = c.SafeOnly,
            SkipRecentTemp = c.SkipRecentTemp,
            RecycleConfirm = c.RecycleConfirm,
            Profile = c.Profile is "Light" or "Thorough" ? c.Profile : "Balanced"
        };
    }
}
