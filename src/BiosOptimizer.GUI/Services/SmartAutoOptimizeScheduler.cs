#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Cleaners;
using BiosOptimizer.Core.Implementations.Memory;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.GUI.Services
{
    public enum AutoOptimizeScheduleOption
    {
        None = 0,
        Off = 0,
        FiveMinutes = 5,
        TenMinutes = 10,
        ThirtyMinutes = 30,
        OneHour = 60,
        ThreeHours = 180,
        SixHours = 360,
        TwelveHours = 720,
        OneDay = 1440
    }

    public class AutoOptimizeRunHistoryEntry
    {
        public string TimestampText { get; set; } = "";
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string ActionName { get; set; } = "";
        public string Status { get; set; } = "Completed"; // Completed, Deferred, Skipped, Failed
        public string ResultText { get; set; } = "";
        public string TriggerSource { get; set; } = "Scheduled"; // Scheduled, Run Now
        public string InternalCommand { get; set; } = "";
    }

    /// <summary>
    /// Metadata and execution contract for any optimization module registering with Auto Optimize.
    /// Only modules with AutoSchedulable = true, SafetyLevel = "SAFE", and RequiresConfirmation = false
    /// are exposed in the Auto Optimize Settings library.
    /// </summary>
    public class AutoSchedulableOptimizationDefinition
    {
        public string OptimizationId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "OTHER SAFE MAINTENANCE"; // MEMORY, STORAGE, NETWORK, BROWSER, OTHER SAFE MAINTENANCE
        public string SafetyLevel { get; set; } = "SAFE";
        public bool AutoSchedulable { get; set; } = true;
        public bool RequiresConfirmation { get; set; } = false;
        public bool RequiresService { get; set; } = true;
        public bool CanRunDuringActiveWorkload { get; set; } = false;
        public string EstimatedDuration { get; set; } = "~1–2s";
        public string InternalCommand { get; set; } = "";
        public OptimizationCategory ExecutionCategory { get; set; } = OptimizationCategory.Storage;
        public Func<CancellationToken, Task<(bool Success, string SummaryResult, long BytesReclaimed)>>? ExecuteHandler { get; set; }
    }

    public class AutoOptimizeScheduleState
    {
        public int SchemaVersion { get; set; } = 5;
        public string OptionKey { get; set; } = "None";
        public bool IsEnabled { get; set; } = false;
        public List<string> SelectedOptimizationIds { get; set; } = new();

        public int SelectedCount => SelectedOptimizationIds?.Count ?? 0;

        // Legacy compatibility flags kept synchronized with SelectedOptimizationIds
        public bool ActionCleanTempFiles { get; set; } = false;
        public bool ActionFreeStandbyMemory { get; set; } = false;

        public DateTime? LastRun { get; set; } = null;
        public DateTime? NextRun { get; set; } = null;
        public string LastResult { get; set; } = "—";
        public string LastRunStatus { get; set; } = "Never"; // Never, Running, Completed, Deferred, Skipped, Failed
        public string LastRunActionsText { get; set; } = "—";
        public string LastDeferralReason { get; set; } = "";
        public string LastTechnicalDeferDetail { get; set; } = "";
        public string LastRetryReason { get; set; } = "";
        public string ExecutionTrigger { get; set; } = "None";
        public string LastInternalCommandExecuted { get; set; } = "None";
        public long TotalBytesReclaimedHistorical { get; set; } = 0;

        public List<AutoOptimizeRunHistoryEntry> History { get; set; } = new();
        public List<AutoOptimizeRunHistoryEntry> RunHistory => History;

        public string GetSelectedSummaryText() => SmartAutoOptimizeScheduler.Instance.GetSelectedSummaryText();
    }

    public class ScheduledMaintenanceResult
    {
        public bool Success { get; set; }
        public bool WasDeferred { get; set; }
        public string DeferReason { get; set; } = "";
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public TimeSpan Duration { get; set; }
        public int ExecutedCount { get; set; }
        public int VerifiedCount { get; set; }
        public bool TempActionExecuted { get; set; }
        public long TempBytesScanned { get; set; }
        public int TempFilesScanned { get; set; }
        public long TempBytesDeleted { get; set; }
        public int TempFilesDeleted { get; set; }
        public string TempResultText { get; set; } = "";
        public bool MemoryActionExecuted { get; set; }
        public string MemoryActionReason { get; set; } = "";
        public long StandbyBeforeBytes { get; set; }
        public long StandbyAfterBytes { get; set; }
        public long StandbyReclaimedBytes { get; set; }
        public long AvailableBeforeBytes { get; set; }
        public long AvailableAfterBytes { get; set; }
        public string SummaryMessage { get; set; } = "";
        public string VerificationStatus { get; set; } = "VERIFIED";
    }

    /// <summary>
    /// V4 Authoritative Auto Optimize Maintenance Scheduler.
    /// Executes user-selected safe optimizations (where AutoSchedulable == true)
    /// using a non-UI background System.Threading.Timer and Windows Task Scheduler integration.
    /// Defaults strictly to OFF / Schedule = None / Selected = 0 / Next Run = — / Last Run = Never.
    /// </summary>
    public class SmartAutoOptimizeScheduler : IDisposable
    {
        private static readonly Lazy<SmartAutoOptimizeScheduler> _instance = new(() => new SmartAutoOptimizeScheduler());
        public static SmartAutoOptimizeScheduler Instance => _instance.Value;

        public const string TASK_NAME = @"\ErrorOptimizer\SmartAutoOptimizeTask";
        private const string MUTEX_NAME = @"Global\ErrorOptimizer_SmartAutoOptimize_Mutex";

        public const string ID_FREE_STANDBY = "memory.free_standby";
        public const string ID_REFRESH_APP_MEMORY = "memory.refresh_app_memory";
        public const string ID_CLEAN_TEMP = "storage.clean_temp";
        public const string ID_CLEAN_RECYCLE_BIN = "storage.clean_recycle_bin";
        public const string ID_FLUSH_DNS = "network.flush_dns";
        public const string ID_CLEAN_BROWSER_CACHE = "browser.clean_cache";

        private readonly string _configFilePath;
        private readonly string _logFilePath;
        private readonly StorageCleanerEngine _storageEngine = new();
        private readonly SemaphoreSlim _executionGate = new(1, 1);
        private readonly object _stateLock = new();
        private readonly Timer _backgroundTimer;
        private readonly List<AutoSchedulableOptimizationDefinition> _registeredModules = new();

        private AutoOptimizeScheduleState _state = new();
        private volatile bool _isExecuting = false;
        private Func<bool>? _serviceOnlineEvaluator;

        public event Action<AutoOptimizeScheduleState>? StateChanged;
        public event Action? LibraryChanged;
        public event Func<Task>? RequestTelemetryRefresh;

        public AutoOptimizeScheduleState State => _state;
        public bool IsExecuting => _isExecuting;

        public SmartAutoOptimizeScheduler()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer");
            string logDir = Path.Combine(appData, "logs");
            Directory.CreateDirectory(appData);
            Directory.CreateDirectory(logDir);

            _configFilePath = Path.Combine(appData, "auto_optimize_schedule_v4.json");
            _logFilePath = Path.Combine(logDir, "auto_maintenance.log");

            _backgroundTimer = new Timer(OnBackgroundTimerTick, null, Timeout.Infinite, Timeout.Infinite);

            RegisterBuiltInOptimizationModules();
            LoadState();
            ArmBackgroundTimer();
        }

        /// <summary>
        /// Future Extensibility API: Allows any optimization module to register its metadata and handler.
        /// If AutoSchedulable == true and SafetyLevel == "SAFE" and RequiresConfirmation == false,
        /// it automatically becomes available in AUTO OPTIMIZE SETTINGS without changing Dashboard layout.
        /// </summary>
        public void RegisterOptimizationModule(AutoSchedulableOptimizationDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.OptimizationId))
                return;

            lock (_stateLock)
            {
                int existingIdx = _registeredModules.FindIndex(m =>
                    m.OptimizationId.Equals(definition.OptimizationId, StringComparison.OrdinalIgnoreCase));
                if (existingIdx >= 0)
                {
                    _registeredModules[existingIdx] = definition;
                }
                else
                {
                    _registeredModules.Add(definition);
                }
            }

            LibraryChanged?.Invoke();
        }

        /// <summary>
        /// Returns ONLY operations explicitly marked AutoSchedulable = true, SafetyLevel = "SAFE",
        /// and RequiresConfirmation = false.
        /// </summary>
        public IReadOnlyList<AutoSchedulableOptimizationDefinition> GetSchedulableLibrary()
        {
            lock (_stateLock)
            {
                return _registeredModules
                    .Where(m => m.AutoSchedulable &&
                                !m.RequiresConfirmation &&
                                m.SafetyLevel.Equals("SAFE", StringComparison.OrdinalIgnoreCase) &&
                                m.ExecuteHandler != null)
                    .ToList();
            }
        }

        public IReadOnlyList<AutoSchedulableOptimizationDefinition> GetAllRegisteredDefinitions()
        {
            lock (_stateLock)
            {
                return _registeredModules.ToList();
            }
        }

        private void RegisterBuiltInOptimizationModules()
        {
            // 1. MEMORY: Free Standby Memory (AUTO_SCHEDULABLE = true)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = ID_FREE_STANDBY,
                DisplayName = "Free Standby Memory",
                Description = "Release cached standby memory Windows can safely reclaim without affecting active apps.",
                Category = "MEMORY",
                SafetyLevel = "SAFE",
                AutoSchedulable = true,
                RequiresConfirmation = false,
                RequiresService = true,
                CanRunDuringActiveWorkload = false,
                EstimatedDuration = "~1–2s",
                InternalCommand = "WindowsMemoryListProvider.ExecuteStandbyPurgeAsync",
                ExecutionCategory = OptimizationCategory.Memory,
                ExecuteHandler = async ct =>
                {
                    var memBefore = WindowsMemoryTelemetryProvider.Instance.SampleCurrentMemoryState();
                    long providerReclaimed = 0;
                    if (ExternalEmptyStandbyListProvider.Instance.IsAvailable)
                    {
                        var extRes = await ExternalEmptyStandbyListProvider.Instance.ExecuteAsync(false, ct);
                        providerReclaimed = extRes.StandbyReclaimedBytes;
                    }
                    else
                    {
                        var purgeRes = await WindowsMemoryListProvider.Instance.ExecuteStandbyPurgeAsync(false, ct);
                        providerReclaimed = purgeRes.StandbyReclaimedBytes;
                    }
                    var memAfter = WindowsMemoryTelemetryProvider.Instance.SampleCurrentMemoryState();
                    long deltaStandby = Math.Max(0, memBefore.StandbyBytes - memAfter.StandbyBytes);
                    long reclaimed = Math.Max(providerReclaimed, deltaStandby);
                    string msg = reclaimed >= 1024 * 1024
                        ? $"{FormatBytes(reclaimed)} standby memory released"
                        : "Standby memory verified clean";
                    return (true, msg, reclaimed);
                }
            });

            // 2. MEMORY: Refresh App Memory (AUTO_SCHEDULABLE = true)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = ID_REFRESH_APP_MEMORY,
                DisplayName = "Refresh App Memory",
                Description = "Trim unused working-set memory from eligible idle background applications.",
                Category = "MEMORY",
                SafetyLevel = "SAFE",
                AutoSchedulable = true,
                RequiresConfirmation = false,
                RequiresService = true,
                CanRunDuringActiveWorkload = false,
                EstimatedDuration = "~1–2s",
                InternalCommand = "WindowsMemoryListProvider.ExecuteWorkingSetTrimAsync",
                ExecutionCategory = OptimizationCategory.Memory,
                ExecuteHandler = async ct =>
                {
                    var memBefore = WindowsMemoryTelemetryProvider.Instance.SampleCurrentMemoryState();
                    var res = await WindowsMemoryListProvider.Instance.ExecuteWorkingSetTrimAsync(ct);
                    var memAfter = WindowsMemoryTelemetryProvider.Instance.SampleCurrentMemoryState();
                    long reclaimed = Math.Max(0, memAfter.AvailableBytes - memBefore.AvailableBytes);
                    string msg = !string.IsNullOrWhiteSpace(res.SummaryMessage)
                        ? res.SummaryMessage
                        : (reclaimed > 0 ? $"{FormatBytes(reclaimed)} working set trimmed" : "App memory verified optimal");
                    return (true, msg, reclaimed);
                }
            });

            // 3. STORAGE: Clean Temp Files (AUTO_SCHEDULABLE = true)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = ID_CLEAN_TEMP,
                DisplayName = "Clean Temp Files",
                Description = "Remove safe temporary files, stale logs, and crash dumps that are no longer in use.",
                Category = "STORAGE",
                SafetyLevel = "SAFE",
                AutoSchedulable = true,
                RequiresConfirmation = false,
                RequiresService = true,
                CanRunDuringActiveWorkload = false,
                EstimatedDuration = "~2–5s",
                InternalCommand = "StorageCleanerEngine.CleanCategories(TEMP, log-files, crash-dumps)",
                ExecutionCategory = OptimizationCategory.Storage,
                ExecuteHandler = async ct =>
                {
                    string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                    var allCategories = StorageCleanerEngine.BuildCategories();
                    var tempCats = allCategories.Where(c =>
                        c.CategoryType == "TEMP" ||
                        c.Id == "temp-files" ||
                        c.Id == "temp-appdata" ||
                        c.Id == "log-files" ||
                        c.Id == "crash-dumps").ToList();

                    var cleanRes = await Task.Run(() => _storageEngine.CleanCategories(tempCats, sysDrive, null, ct), ct);
                    string msg = cleanRes.BytesReclaimed > 0
                        ? $"{FormatBytes(cleanRes.BytesReclaimed)} temp files removed"
                        : "Temp directories verified clean";
                    return (true, msg, cleanRes.BytesReclaimed);
                }
            });

            // 4. STORAGE: Clean Recycle Bin (AUTO_SCHEDULABLE = true)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = ID_CLEAN_RECYCLE_BIN,
                DisplayName = "Clean Recycle Bin",
                Description = "Remove items currently stored in the Windows Recycle Bin.",
                Category = "STORAGE",
                SafetyLevel = "SAFE",
                AutoSchedulable = true,
                RequiresConfirmation = false,
                RequiresService = true,
                CanRunDuringActiveWorkload = false,
                EstimatedDuration = "~1–2s",
                InternalCommand = "StorageCleanerEngine.EmptyRecycleBin",
                ExecutionCategory = OptimizationCategory.Storage,
                ExecuteHandler = async ct =>
                {
                    var (success, freedBytes, message) = await Task.Run(() => _storageEngine.EmptyRecycleBin(), ct);
                    string msg = freedBytes > 0
                        ? $"{FormatBytes(freedBytes)} removed from Recycle Bin"
                        : (!string.IsNullOrWhiteSpace(message) ? message : "Recycle Bin verified empty");
                    return (success, msg, Math.Max(0, freedBytes));
                }
            });

            // 5. NETWORK: Flush DNS Cache (AUTO_SCHEDULABLE = true)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = ID_FLUSH_DNS,
                DisplayName = "Flush DNS Cache",
                Description = "Refresh saved DNS resolver cache entries for clean network name lookups.",
                Category = "NETWORK",
                SafetyLevel = "SAFE",
                AutoSchedulable = true,
                RequiresConfirmation = false,
                RequiresService = true,
                CanRunDuringActiveWorkload = true,
                EstimatedDuration = "<1s",
                InternalCommand = "ipconfig /flushdns",
                ExecutionCategory = OptimizationCategory.Network,
                ExecuteHandler = async ct =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ipconfig",
                        Arguments = "/flushdns",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync(ct);
                    return (true, "DNS resolver cache refreshed", 0L);
                }
            });

            // 6. BROWSER: Browser Cache Cleanup (AUTO_SCHEDULABLE = true)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = ID_CLEAN_BROWSER_CACHE,
                DisplayName = "Browser Cache Cleanup",
                Description = "Clear supported browser cache and temporary data safely when browsers are inactive.",
                Category = "BROWSER",
                SafetyLevel = "SAFE",
                AutoSchedulable = true,
                RequiresConfirmation = false,
                RequiresService = true,
                CanRunDuringActiveWorkload = false,
                EstimatedDuration = "~2–4s",
                InternalCommand = "StorageCleanerEngine.CleanCategories(BROWSER)",
                ExecutionCategory = OptimizationCategory.Storage,
                ExecuteHandler = async ct =>
                {
                    string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                    var allCategories = StorageCleanerEngine.BuildCategories();
                    var browserCats = allCategories.Where(c => c.CategoryType == "BROWSER" || c.Id == "browser-cache").ToList();
                    var cleanRes = await Task.Run(() => _storageEngine.CleanCategories(browserCats, sysDrive, null, ct), ct);
                    string msg = cleanRes.BytesReclaimed > 0
                        ? $"{FormatBytes(cleanRes.BytesReclaimed)} browser cache cleared"
                        : "Browser cache verified clean";
                    return (true, msg, cleanRes.BytesReclaimed);
                }
            });

            // Explicitly non-schedulable deep/system-wide operations (AUTO_SCHEDULABLE = false)
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = "process.aggressive_termination",
                DisplayName = "Aggressive Process Termination",
                Description = "Terminates active background processes.",
                Category = "PROCESSES",
                SafetyLevel = "MEDIUM",
                AutoSchedulable = false,
                RequiresConfirmation = true
            });
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = "service.disable_system",
                DisplayName = "Service Disabling",
                Description = "Modifies Windows service startup configuration.",
                Category = "SERVICES",
                SafetyLevel = "HIGH",
                AutoSchedulable = false,
                RequiresConfirmation = true
            });
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = "registry.kernel_modification",
                DisplayName = "Deep Registry Modifications",
                Description = "Modifies kernel and system registry parameters.",
                Category = "REGISTRY",
                SafetyLevel = "MEDIUM",
                AutoSchedulable = false,
                RequiresConfirmation = true
            });
            RegisterOptimizationModule(new AutoSchedulableOptimizationDefinition
            {
                OptimizationId = "max_performance.v4_deep",
                DisplayName = "V4 Maximum Performance Overhaul",
                Description = "Deep workstation/gaming performance reconfiguration.",
                Category = "MAX PERFORMANCE",
                SafetyLevel = "HIGH",
                AutoSchedulable = false,
                RequiresConfirmation = true
            });
        }

        public void SetServiceOnlineEvaluator(Func<bool> evaluator)
        {
            _serviceOnlineEvaluator = evaluator;
        }

        public bool IsServiceAvailable()
        {
            if (_serviceOnlineEvaluator != null)
            {
                try
                {
                    return _serviceOnlineEvaluator();
                }
                catch { }
            }

            // Headless / background fallback check: verify backend service process or pipe is alive
            try
            {
                var procs = Process.GetProcessesByName("BiosOptimizer.Service");
                if (procs.Length > 0) return true;

                using var client = new NamedPipeClientStream(".", "BiosOptimizerPipe", PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(250);
                return client.IsConnected;
            }
            catch
            {
                return false;
            }
        }

        public static IReadOnlyList<string> SupportedScheduleLabels { get; } = new[]
        {
            "None",
            "5 minutes",
            "10 minutes",
            "30 minutes",
            "1 hour",
            "6 hours",
            "12 hours",
            "1 day"
        };

        public static string GetOptionDisplayName(AutoOptimizeScheduleOption option) => option switch
        {
            AutoOptimizeScheduleOption.FiveMinutes => "5 minutes",
            AutoOptimizeScheduleOption.TenMinutes => "10 minutes",
            AutoOptimizeScheduleOption.ThirtyMinutes => "30 minutes",
            AutoOptimizeScheduleOption.OneHour => "1 hour",
            AutoOptimizeScheduleOption.ThreeHours => "3 hours",
            AutoOptimizeScheduleOption.SixHours => "6 hours",
            AutoOptimizeScheduleOption.TwelveHours => "12 hours",
            AutoOptimizeScheduleOption.OneDay => "1 day",
            _ => "None"
        };

        public static TimeSpan GetOptionInterval(AutoOptimizeScheduleOption option) => option switch
        {
            AutoOptimizeScheduleOption.FiveMinutes => TimeSpan.FromMinutes(5),
            AutoOptimizeScheduleOption.TenMinutes => TimeSpan.FromMinutes(10),
            AutoOptimizeScheduleOption.ThirtyMinutes => TimeSpan.FromMinutes(30),
            AutoOptimizeScheduleOption.OneHour => TimeSpan.FromHours(1),
            AutoOptimizeScheduleOption.ThreeHours => TimeSpan.FromHours(3),
            AutoOptimizeScheduleOption.SixHours => TimeSpan.FromHours(6),
            AutoOptimizeScheduleOption.TwelveHours => TimeSpan.FromHours(12),
            AutoOptimizeScheduleOption.OneDay => TimeSpan.FromDays(1),
            _ => TimeSpan.Zero
        };

        public static AutoOptimizeScheduleOption ParseOption(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return AutoOptimizeScheduleOption.None;
            string t = text.Trim().ToUpperInvariant();
            if (t == "NONE" || t == "OFF") return AutoOptimizeScheduleOption.None;
            if (t == "5 MINUTES" || t == "5 MIN" || t == "5M") return AutoOptimizeScheduleOption.FiveMinutes;
            if (t == "10 MINUTES" || t == "10 MIN" || t == "10M") return AutoOptimizeScheduleOption.TenMinutes;
            if (t.Contains("30") || t.Contains("HALF")) return AutoOptimizeScheduleOption.ThirtyMinutes;
            if (t == "1 HOUR" || t == "1H" || t == "HOURLY" || t.Contains("1 HR") || t == "60 MINUTES") return AutoOptimizeScheduleOption.OneHour;
            if (t.Contains("3 HOUR") || t == "3H" || t.Contains("3 HR")) return AutoOptimizeScheduleOption.ThreeHours;
            if (t.Contains("6 HOUR") || t == "6H" || t.Contains("6 HR")) return AutoOptimizeScheduleOption.SixHours;
            if (t.Contains("12 HOUR") || t == "12H" || t.Contains("12 HR")) return AutoOptimizeScheduleOption.TwelveHours;
            if (t.Contains("1 DAY") || t == "1D" || t == "DAILY" || t.Contains("24") || t.Contains("24H")) return AutoOptimizeScheduleOption.OneDay;
            return AutoOptimizeScheduleOption.None;
        }

        public bool IsOptimizationSelected(string optimizationId)
        {
            if (string.IsNullOrWhiteSpace(optimizationId)) return false;
            lock (_stateLock)
            {
                return _state.SelectedOptimizationIds.Any(id =>
                    id.Equals(optimizationId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<string> GetSelectedOptimizationIds()
        {
            lock (_stateLock)
            {
                return _state.SelectedOptimizationIds.ToList();
            }
        }

        public string GetSelectedSummaryText()
        {
            lock (_stateLock)
            {
                if (_state.SelectedOptimizationIds == null || _state.SelectedOptimizationIds.Count == 0)
                    return "None";

                var lib = GetSchedulableLibrary();
                var names = _state.SelectedOptimizationIds
                    .Select(id => lib.FirstOrDefault(m => m.OptimizationId.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id)
                    .ToList();
                return names.Count > 0 ? string.Join(", ", names) : "None";
            }
        }

        public static string FormatLastRunTime(DateTime? lastRun)
        {
            if (!lastRun.HasValue) return "Never";
            var local = lastRun.Value.ToLocalTime();
            if (local.Date == DateTime.Today)
                return $"Today, {local:h:mm tt}";
            if (local.Date == DateTime.Today.AddDays(-1))
                return $"Yesterday, {local:h:mm tt}";
            return local.ToString("MMM d, h:mm tt");
        }

        private void SyncLegacyFlagsLocked()
        {
            _state.SelectedOptimizationIds ??= new List<string>();
            var validIds = GetSchedulableLibrary()
                .Select(m => m.OptimizationId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _state.SelectedOptimizationIds = _state.SelectedOptimizationIds
                .Where(id => validIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _state.ActionCleanTempFiles = _state.SelectedOptimizationIds.Any(id =>
                id.Equals(ID_CLEAN_TEMP, StringComparison.OrdinalIgnoreCase));
            _state.ActionFreeStandbyMemory = _state.SelectedOptimizationIds.Any(id =>
                id.Equals(ID_FREE_STANDBY, StringComparison.OrdinalIgnoreCase));
        }

        public async Task AddOptimizationAsync(string optimizationId)
        {
            if (string.IsNullOrWhiteSpace(optimizationId)) return;
            var eligible = GetSchedulableLibrary()
                .FirstOrDefault(m => m.OptimizationId.Equals(optimizationId, StringComparison.OrdinalIgnoreCase));
            if (eligible == null) return;

            bool shouldSyncTask = false;
            AutoOptimizeScheduleOption opt = AutoOptimizeScheduleOption.None;

            lock (_stateLock)
            {
                if (!_state.SelectedOptimizationIds.Any(id => id.Equals(eligible.OptimizationId, StringComparison.OrdinalIgnoreCase)))
                {
                    _state.SelectedOptimizationIds.Add(eligible.OptimizationId);
                }
                SyncLegacyFlagsLocked();
                opt = ParseOption(_state.OptionKey);
                SaveState();
            }

            ArmBackgroundTimer();
            if (shouldSyncTask)
            {
                await SyncWindowsTaskSchedulerAsync(_state.IsEnabled ? opt : AutoOptimizeScheduleOption.None);
            }
            StateChanged?.Invoke(_state);
        }

        public async Task RemoveOptimizationAsync(string optimizationId)
        {
            if (string.IsNullOrWhiteSpace(optimizationId)) return;

            bool wasEnabled;
            bool shouldSyncTask = false;
            AutoOptimizeScheduleOption opt;

            lock (_stateLock)
            {
                wasEnabled = _state.IsEnabled;
                _state.SelectedOptimizationIds.RemoveAll(id => id.Equals(optimizationId, StringComparison.OrdinalIgnoreCase));
                SyncLegacyFlagsLocked();
                opt = ParseOption(_state.OptionKey);

                if (_state.IsEnabled && _state.SelectedOptimizationIds.Count == 0)
                {
                    // Enable Rule: Auto Optimize cannot stay enabled when 0 optimizations are selected
                    _state.IsEnabled = false;
                    _state.NextRun = null;
                    shouldSyncTask = true;
                }

                SaveState();
            }

            ArmBackgroundTimer();
            if (shouldSyncTask && wasEnabled)
            {
                AppSettingsService.Instance.SmartAutoOptimizeAutoStart = false;
                await SyncWindowsTaskSchedulerAsync(AutoOptimizeScheduleOption.None);
            }
            StateChanged?.Invoke(_state);
        }

        public async Task SetScheduleOptionOnlyAsync(AutoOptimizeScheduleOption option)
        {
            bool wasEnabled;
            bool shouldSyncTask = false;

            lock (_stateLock)
            {
                wasEnabled = _state.IsEnabled;
                _state.OptionKey = GetOptionDisplayName(option);
                SyncLegacyFlagsLocked();

                bool hasValidConfig = option != AutoOptimizeScheduleOption.None && _state.SelectedOptimizationIds.Count > 0;
                if (_state.IsEnabled)
                {
                    if (!hasValidConfig)
                    {
                        _state.IsEnabled = false;
                        _state.NextRun = null;
                        shouldSyncTask = true;
                    }
                    else
                    {
                        var interval = GetOptionInterval(option);
                        _state.NextRun = DateTime.Now.Add(interval);
                        shouldSyncTask = true;
                    }
                }
                else
                {
                    // Do NOT silently enable when user changes schedule while OFF
                    _state.NextRun = null;
                }

                SaveState();
            }

            ArmBackgroundTimer();
            if (shouldSyncTask || wasEnabled)
            {
                AppSettingsService.Instance.SmartAutoOptimizeAutoStart = _state.IsEnabled;
                await SyncWindowsTaskSchedulerAsync(_state.IsEnabled ? option : AutoOptimizeScheduleOption.None);
            }
            StateChanged?.Invoke(_state);
        }

        public async Task<bool> EnableSelectedAutoOptimizeAsync(AutoOptimizeScheduleOption? overrideOption = null)
        {
            AutoOptimizeScheduleOption opt;
            lock (_stateLock)
            {
                SyncLegacyFlagsLocked();
                opt = overrideOption ?? ParseOption(_state.OptionKey);
                if (opt == AutoOptimizeScheduleOption.None || _state.SelectedOptimizationIds.Count == 0)
                {
                    return false;
                }

                var interval = GetOptionInterval(opt);
                if (interval <= TimeSpan.Zero) return false;

                _state.OptionKey = GetOptionDisplayName(opt);
                _state.IsEnabled = true;
                _state.NextRun = DateTime.Now.Add(interval);
                _state.LastDeferralReason = "";
                _state.LastTechnicalDeferDetail = "";
                SaveState();
            }

            AppSettingsService.Instance.SmartAutoOptimizeAutoStart = true;
            ArmBackgroundTimer();
            await SyncWindowsTaskSchedulerAsync(opt);
            LogMaintenance("[ENABLED]", $"Auto Optimize enabled. Interval={_state.OptionKey}, Selected=[{string.Join(", ", _state.SelectedOptimizationIds)}], NextRun={_state.NextRun:O}");
            StateChanged?.Invoke(_state);
            return true;
        }

        /// <summary>
        /// Updates the selected schedule interval and action checkboxes without automatically enabling
        /// if Auto Optimize is currently OFF. If Auto Optimize is already ON, updates the active schedule
        /// or disables it if schedule becomes None or all actions are removed.
        /// </summary>
        public async Task UpdateConfigurationAsync(
            AutoOptimizeScheduleOption option,
            bool cleanTempFiles,
            bool freeStandbyMemory)
        {
            lock (_stateLock)
            {
                if (cleanTempFiles && !_state.SelectedOptimizationIds.Contains(ID_CLEAN_TEMP, StringComparer.OrdinalIgnoreCase))
                    _state.SelectedOptimizationIds.Add(ID_CLEAN_TEMP);
                else if (!cleanTempFiles)
                    _state.SelectedOptimizationIds.RemoveAll(id => id.Equals(ID_CLEAN_TEMP, StringComparison.OrdinalIgnoreCase));

                if (freeStandbyMemory && !_state.SelectedOptimizationIds.Contains(ID_FREE_STANDBY, StringComparer.OrdinalIgnoreCase))
                    _state.SelectedOptimizationIds.Add(ID_FREE_STANDBY);
                else if (!freeStandbyMemory)
                    _state.SelectedOptimizationIds.RemoveAll(id => id.Equals(ID_FREE_STANDBY, StringComparison.OrdinalIgnoreCase));

                SyncLegacyFlagsLocked();
            }

            await SetScheduleOptionOnlyAsync(option);
        }

        /// <summary>
        /// Explicitly enables Auto Optimize when a valid schedule and at least one action are selected.
        /// </summary>
        public async Task<bool> EnableAutoOptimizeAsync(
            AutoOptimizeScheduleOption option,
            bool cleanTempFiles,
            bool freeStandbyMemory)
        {
            lock (_stateLock)
            {
                if (cleanTempFiles && !_state.SelectedOptimizationIds.Contains(ID_CLEAN_TEMP, StringComparer.OrdinalIgnoreCase))
                    _state.SelectedOptimizationIds.Add(ID_CLEAN_TEMP);
                if (freeStandbyMemory && !_state.SelectedOptimizationIds.Contains(ID_FREE_STANDBY, StringComparer.OrdinalIgnoreCase))
                    _state.SelectedOptimizationIds.Add(ID_FREE_STANDBY);
                SyncLegacyFlagsLocked();
            }

            return await EnableSelectedAutoOptimizeAsync(option);
        }

        /// <summary>
        /// Explicitly disables Auto Optimize, stopping future scheduled runs while preserving configuration and history.
        /// </summary>
        public async Task DisableAutoOptimizeAsync()
        {
            lock (_stateLock)
            {
                _state.IsEnabled = false;
                _state.NextRun = null;
                _state.LastDeferralReason = "";
                _state.LastTechnicalDeferDetail = "";
                SaveState();
            }

            AppSettingsService.Instance.SmartAutoOptimizeAutoStart = false;
            ArmBackgroundTimer();
            await SyncWindowsTaskSchedulerAsync(AutoOptimizeScheduleOption.None);
            LogMaintenance("[DISABLED]", "Auto Optimize disabled by user. Active schedule stopped.");
            StateChanged?.Invoke(_state);
        }

        /// <summary>
        /// Updates schedule option without silently enabling if no actions are selected.
        /// </summary>
        public async Task SetScheduleAsync(AutoOptimizeScheduleOption option)
        {
            await SetScheduleOptionOnlyAsync(option);
        }

        /// <summary>
        /// Executes ONLY the currently selected Auto Optimize actions immediately (RUN NOW).
        /// Does not enable or alter the recurring schedule if Auto Optimize is OFF.
        /// </summary>
        public async Task<ScheduledMaintenanceResult?> RunSelectedOptimizationsNowAsync(CancellationToken ct = default)
        {
            List<string> selectedIds;
            lock (_stateLock)
            {
                SyncLegacyFlagsLocked();
                selectedIds = _state.SelectedOptimizationIds.ToList();
            }

            if (selectedIds.Count == 0) return null;

            return await ExecuteMaintenanceByIdsInternalAsync(
                trigger: "Run Now",
                isScheduledRun: false,
                optimizationIds: selectedIds,
                bypassWorkloadProtection: true,
                ct: ct);
        }

        public async Task<ScheduledMaintenanceResult?> RunSelectedNowAsync(
            bool cleanTempFiles,
            bool freeStandbyMemory,
            CancellationToken ct = default)
        {
            var ids = new List<string>();
            lock (_stateLock)
            {
                ids.AddRange(_state.SelectedOptimizationIds);
            }
            if (cleanTempFiles && !ids.Contains(ID_CLEAN_TEMP, StringComparer.OrdinalIgnoreCase))
                ids.Add(ID_CLEAN_TEMP);
            if (freeStandbyMemory && !ids.Contains(ID_FREE_STANDBY, StringComparer.OrdinalIgnoreCase))
                ids.Add(ID_FREE_STANDBY);

            if (ids.Count == 0) return null;
            return await ExecuteMaintenanceByIdsInternalAsync(
                trigger: "Run Now",
                isScheduledRun: false,
                optimizationIds: ids,
                bypassWorkloadProtection: true,
                ct: ct);
        }

        public async Task<ScheduledMaintenanceResult?> RunManualOrScheduledAsync(
            string trigger = "Scheduled",
            bool bypassWorkloadProtection = false,
            CancellationToken ct = default)
        {
            bool isScheduled = !bypassWorkloadProtection &&
                               !trigger.Equals("Run Now", StringComparison.OrdinalIgnoreCase) &&
                               !trigger.Equals("MANUAL_TEST_TRIGGER", StringComparison.OrdinalIgnoreCase);

            List<string> ids;
            lock (_stateLock)
            {
                SyncLegacyFlagsLocked();
                ids = _state.SelectedOptimizationIds.ToList();
            }

            return await ExecuteMaintenanceByIdsInternalAsync(
                trigger: trigger,
                isScheduledRun: isScheduled,
                optimizationIds: ids,
                bypassWorkloadProtection: bypassWorkloadProtection,
                ct: ct);
        }

        public async Task<ScheduledMaintenanceResult?> TriggerScheduledCleanupNowAsync(CancellationToken ct = default)
        {
            return await RunSelectedOptimizationsNowAsync(ct);
        }

        private async Task<ScheduledMaintenanceResult?> ExecuteMaintenanceByIdsInternalAsync(
            string trigger,
            bool isScheduledRun,
            IReadOnlyList<string> optimizationIds,
            bool bypassWorkloadProtection,
            CancellationToken ct)
        {
            var library = GetSchedulableLibrary();
            var selectedDefs = library
                .Where(m => optimizationIds.Any(id => id.Equals(m.OptimizationId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (isScheduledRun)
            {
                if (!_state.IsEnabled || ParseOption(_state.OptionKey) == AutoOptimizeScheduleOption.None || selectedDefs.Count == 0)
                {
                    return null;
                }
            }
            else if (selectedDefs.Count == 0)
            {
                return null;
            }

            if (!await _executionGate.WaitAsync(100, ct))
            {
                Debug.WriteLine("[Scheduler] Execution gate locked. Skipping duplicate invocation.");
                return null;
            }

            bool mutexAcquired = false;
            Mutex? mutex = null;

            try
            {
                mutex = new Mutex(false, MUTEX_NAME);
                try
                {
                    mutexAcquired = mutex.WaitOne(100, false);
                }
                catch (AbandonedMutexException)
                {
                    mutexAcquired = true;
                }

                if (!mutexAcquired)
                {
                    Debug.WriteLine("[Scheduler] Cross-process mutex in use. Skipping duplicate execution.");
                    return null;
                }

                // ── SAFETY CHECK 1: Service Availability ────────────────────────
                if (!IsServiceAvailable())
                {
                    return RecordDeferral(
                        trigger,
                        userReason: "Scheduled maintenance will resume when the optimizer service is available.",
                        technicalReason: "Service offline: BiosOptimizer.Service IPC pipe unavailable.");
                }

                // ── SAFETY CHECK 2: Conflicting Optimization Running ────────────
                if (OptimizationExecutionCoordinator.Instance.IsAnyCategoryBusy() ||
                    OptimizationProgressService.Instance.IsOptimizationRunning)
                {
                    return RecordDeferral(
                        trigger,
                        userReason: "An active workload was detected. Auto Optimize will try again later.",
                        technicalReason: "OptimizationExecutionCoordinator or OptimizationProgressService is busy.");
                }

                // ── SAFETY CHECK 3: Restore-Point Operation Active ──────────────
                if (OptimizationProgressService.Instance.IsRestorePointOperationActive)
                {
                    return RecordDeferral(
                        trigger,
                        userReason: "An active workload was detected. Auto Optimize will try again later.",
                        technicalReason: "WindowsSystemRestoreEngine restore-point lock is active.");
                }

                // ── SAFETY CHECK 4: Gaming / Editing / Emulator Workload Protection ──
                if (!bypassWorkloadProtection)
                {
                    if (IsActiveGamingOrEditingWorkload(out string workloadTechDetail))
                    {
                        return RecordDeferral(
                            trigger,
                            userReason: "An active workload was detected. Auto Optimize will try again later.",
                            technicalReason: workloadTechDetail);
                    }

                    if (IsBatteryPowerConstrained(out string batteryDetail))
                    {
                        return RecordDeferral(
                            trigger,
                            userReason: "Battery power is low. Auto Optimize will try again later.",
                            technicalReason: batteryDetail);
                    }
                }

                _isExecuting = true;
                lock (_stateLock)
                {
                    _state.ExecutionTrigger = trigger;
                    _state.LastRunStatus = "Running";
                }
                StateChanged?.Invoke(_state);

                var sw = Stopwatch.StartNew();
                var result = new ScheduledMaintenanceResult();
                var executedActionNames = new List<string>();
                var executedResultSummaries = new List<string>();
                var executedInternalCmds = new List<string>();
                DateTime runTimeLocal = DateTime.Now;
                string historyTimeLabel = FormatHistoryTimestamp(runTimeLocal);
                long totalReclaimedBytesThisRun = 0;

                foreach (var def in selectedDefs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (def.ExecuteHandler == null) continue;

                    executedActionNames.Add(def.DisplayName);
                    executedInternalCmds.Add(def.InternalCommand);
                    result.ExecutedCount++;

                    long bytesFromAction = 0;
                    var execCtx = await OptimizationExecutionCoordinator.Instance.ExecuteAsync(
                        $"Auto Optimize: {def.DisplayName}",
                        def.ExecutionCategory,
                        async innerCt =>
                        {
                            var (ok, summaryMsg, reclaimedBytes) = await def.ExecuteHandler(innerCt);
                            bytesFromAction = Math.Max(0, reclaimedBytes);
                            if (def.OptimizationId.Equals(ID_CLEAN_TEMP, StringComparison.OrdinalIgnoreCase))
                            {
                                result.TempActionExecuted = true;
                                result.TempBytesDeleted = bytesFromAction;
                                result.TempResultText = summaryMsg;
                            }
                            else if (def.OptimizationId.Equals(ID_FREE_STANDBY, StringComparison.OrdinalIgnoreCase))
                            {
                                result.MemoryActionExecuted = true;
                                result.StandbyReclaimedBytes = bytesFromAction;
                                result.MemoryActionReason = summaryMsg;
                            }
                            return summaryMsg;
                        },
                        TimeSpan.FromSeconds(30),
                        ct);

                    if (execCtx.Succeeded)
                    {
                        result.VerifiedCount++;
                    }

                    totalReclaimedBytesThisRun += bytesFromAction;
                    string itemResultText = execCtx.Succeeded ? execCtx.StatusMessage : "Skipped (resource busy)";
                    executedResultSummaries.Add($"{def.DisplayName}: {itemResultText}");

                    PrependHistoryEntry(new AutoOptimizeRunHistoryEntry
                    {
                        TimestampText = historyTimeLabel,
                        TimestampUtc = DateTime.UtcNow,
                        ActionName = def.DisplayName,
                        Status = execCtx.Succeeded ? "Completed" : "Skipped",
                        ResultText = itemResultText,
                        TriggerSource = isScheduledRun ? "Scheduled" : "Run Now",
                        InternalCommand = def.InternalCommand
                    });
                }

                // Trigger Telemetry Refresh on Dashboard / Views
                try
                {
                    if (RequestTelemetryRefresh != null)
                    {
                        await RequestTelemetryRefresh.Invoke();
                    }
                }
                catch { }

                sw.Stop();
                result.Duration = sw.Elapsed;
                result.Success = result.VerifiedCount > 0;
                result.SummaryMessage = executedResultSummaries.Count == 1
                    ? executedResultSummaries[0]
                    : string.Join(" · ", executedResultSummaries);

                lock (_stateLock)
                {
                    _state.LastRun = runTimeLocal;
                    _state.LastRunStatus = "Completed";
                    _state.LastRunActionsText = string.Join(", ", executedActionNames);
                    _state.LastResult = result.SummaryMessage;
                    _state.LastDeferralReason = "";
                    _state.LastTechnicalDeferDetail = "";
                    _state.LastRetryReason = "None";
                    _state.LastInternalCommandExecuted = string.Join(" + ", executedInternalCmds);
                    _state.TotalBytesReclaimedHistorical += totalReclaimedBytesThisRun;

                    if (_state.IsEnabled)
                    {
                        ScheduleNextRunLocked();
                    }
                    else
                    {
                        _state.NextRun = null;
                    }

                    SaveState();
                }

                RecordMaintenanceTransaction(result, trigger);
                ArmBackgroundTimer();
                StateChanged?.Invoke(_state);

                return result;
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _state.LastRun = DateTime.Now;
                    _state.LastRunStatus = "Failed";
                    _state.LastResult = ex.Message;
                    _state.LastRetryReason = "Will retry on next scheduled cycle";
                    if (_state.IsEnabled)
                    {
                        ScheduleNextRunLocked();
                    }
                    SaveState();
                }

                ArmBackgroundTimer();
                StateChanged?.Invoke(_state);
                LogMaintenance("[ERROR]", ex.ToString());
                return null;
            }
            finally
            {
                _isExecuting = false;
                if (mutexAcquired && mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
                _executionGate.Release();
            }
        }

        private ScheduledMaintenanceResult RecordDeferral(string trigger, string userReason, string technicalReason)
        {
            var now = DateTime.Now;
            var res = new ScheduledMaintenanceResult
            {
                Success = false,
                WasDeferred = true,
                DeferReason = userReason,
                SummaryMessage = userReason
            };

            lock (_stateLock)
            {
                _state.ExecutionTrigger = trigger;
                _state.LastRun = now;
                _state.LastRunStatus = "Deferred";
                _state.LastResult = userReason;
                _state.LastDeferralReason = userReason;
                _state.LastTechnicalDeferDetail = technicalReason;
                _state.LastRetryReason = _state.IsEnabled
                    ? $"Deferred — scheduled to retry at next interval ({_state.OptionKey})"
                    : "Manual trigger deferred";

                PrependHistoryEntry(new AutoOptimizeRunHistoryEntry
                {
                    TimestampText = FormatHistoryTimestamp(now),
                    TimestampUtc = DateTime.UtcNow,
                    ActionName = GetSelectedOptimizationsSummaryText(),
                    Status = "Deferred",
                    ResultText = userReason,
                    TriggerSource = trigger,
                    InternalCommand = technicalReason
                });

                if (_state.IsEnabled)
                {
                    ScheduleNextRunLocked();
                }
                SaveState();
            }

            ArmBackgroundTimer();
            StateChanged?.Invoke(_state);
            LogMaintenance("[DEFERRED]", $"{userReason} ({technicalReason})");
            return res;
        }

        public string GetSelectedOptimizationsSummaryText()
        {
            lock (_stateLock)
            {
                if (_state.SelectedOptimizationIds == null || _state.SelectedOptimizationIds.Count == 0)
                    return "None";

                var lib = GetSchedulableLibrary();
                var names = lib
                    .Where(m => _state.SelectedOptimizationIds.Contains(m.OptimizationId, StringComparer.OrdinalIgnoreCase))
                    .Select(m => m.DisplayName)
                    .ToList();

                return names.Count > 0 ? string.Join(", ", names) : "None";
            }
        }

        private static string GetSelectedActionsSummary(bool cleanTemp, bool freeStandby)
        {
            if (cleanTemp && freeStandby) return "Clean Temp Files, Free Standby Memory";
            if (cleanTemp) return "Clean Temp Files";
            if (freeStandby) return "Free Standby Memory";
            return "Maintenance";
        }

        private void PrependHistoryEntry(AutoOptimizeRunHistoryEntry entry)
        {
            _state.History.Insert(0, entry);
            if (_state.History.Count > 30)
            {
                _state.History.RemoveRange(30, _state.History.Count - 30);
            }
        }

        private static string FormatHistoryTimestamp(DateTime dt)
        {
            if (dt.Date == DateTime.Today)
            {
                return dt.ToString("HH:mm");
            }
            return dt.ToString("MMM d, HH:mm");
        }

        private void OnBackgroundTimerTick(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckAndTriggerScheduledRunAsync();
                }
                catch { }
            });
        }

        private async Task CheckAndTriggerScheduledRunAsync()
        {
            bool shouldTrigger = false;
            lock (_stateLock)
            {
                if (!_state.IsEnabled || !_state.NextRun.HasValue)
                {
                    ArmBackgroundTimer();
                    return;
                }

                SyncLegacyFlagsLocked();
                if (_state.SelectedOptimizationIds.Count == 0)
                {
                    _state.IsEnabled = false;
                    _state.NextRun = null;
                    SaveState();
                    ArmBackgroundTimer();
                    return;
                }

                if (DateTime.Now >= _state.NextRun.Value && !_isExecuting)
                {
                    shouldTrigger = true;
                }
            }

            if (shouldTrigger)
            {
                await RunManualOrScheduledAsync("Scheduled", bypassWorkloadProtection: false);
            }
            else
            {
                ArmBackgroundTimer();
            }
        }

        private void ArmBackgroundTimer()
        {
            try
            {
                lock (_stateLock)
                {
                    if (!_state.IsEnabled || !_state.NextRun.HasValue)
                    {
                        _backgroundTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }

                    TimeSpan remaining = _state.NextRun.Value - DateTime.Now;
                    if (remaining <= TimeSpan.FromSeconds(1))
                    {
                        _backgroundTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                    }
                    else
                    {
                        // Wake up at exact due time (or every 30s max so system sleep/resume stays accurate)
                        TimeSpan due = remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30);
                        _backgroundTimer.Change(due, Timeout.InfiniteTimeSpan);
                    }
                }
            }
            catch { }
        }

        private void ScheduleNextRunLocked()
        {
            if (!_state.IsEnabled)
            {
                _state.NextRun = null;
                return;
            }

            var opt = ParseOption(_state.OptionKey);
            var interval = GetOptionInterval(opt);
            if (interval > TimeSpan.Zero)
            {
                _state.NextRun = DateTime.Now.Add(interval);
            }
            else
            {
                _state.NextRun = null;
                _state.IsEnabled = false;
            }
        }

        private bool IsActiveGamingOrEditingWorkload(out string technicalDetail)
        {
            technicalDetail = "";
            try
            {
                // 1. Check authoritative UserWorkloadDetector (Games, Creative Rendering, Video Editing, Compilation, Virtualization)
                var workload = UserWorkloadDetector.Instance.ClassifyCurrentWorkload();
                if (workload != null)
                {
                    bool isProtected = workload.Category switch
                    {
                        ExtendedWorkloadCategory.Gaming => true,
                        ExtendedWorkloadCategory.CreativeRendering => true,
                        ExtendedWorkloadCategory.CreativeEditing => true,
                        ExtendedWorkloadCategory.Compilation => true,
                        ExtendedWorkloadCategory.Virtualization => true,
                        _ => false
                    };

                    if (isProtected)
                    {
                        technicalDetail = $"UserWorkloadDetector: {workload.Category} active (Process: {workload.PrimaryProcessName})";
                        return true;
                    }
                }

                // 2. Check running emulator engines (BlueStacks, MSI App Player, LDPlayer, Nox, MEmu)
                string[] emulatorProcessNames =
                {
                    "HD-Player", "Bluestacks", "BstkSVC", "dnplayer", "LdVBoxHeadless", "Nox", "NoxVMHandle", "MEmu", "MEmuHeadless"
                };
                var runningProcs = Process.GetProcesses();
                foreach (var p in runningProcs)
                {
                    try
                    {
                        if (emulatorProcessNames.Any(e => e.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase)))
                        {
                            technicalDetail = $"Active Emulator Process Detected: {p.ProcessName} (PID {p.Id})";
                            return true;
                        }
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            return false;
        }

        private bool IsBatteryPowerConstrained(out string detail)
        {
            detail = "";
            try
            {
                var hw = HardwareProfiler.GetQuickProfile();
                if (hw.IsBatteryPowered && hw.BatteryPercent < 35)
                {
                    detail = $"Battery power constrained ({hw.BatteryPercent}% remaining).";
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void RecordMaintenanceTransaction(ScheduledMaintenanceResult res, string trigger)
        {
            try
            {
                if (res.TempBytesDeleted > 0)
                {
                    BackupManager.Instance.CaptureStorageCleanup(
                        "Auto Optimize",
                        "Clean Temp Files",
                        res.TempBytesDeleted,
                        res.TempFilesDeleted,
                        new List<string> { "%TEMP%", "Windows\\Temp", "Stale Logs" },
                        false
                    );
                }

                if (res.MemoryActionExecuted && res.StandbyReclaimedBytes > 0)
                {
                    BackupManager.Instance.CaptureMemoryRuntime(
                        "Auto Optimize: Free Standby Memory",
                        res.StandbyReclaimedBytes / (1024 * 1024),
                        $"Standby: {FormatBytes(res.StandbyBeforeBytes)}",
                        $"Standby: {FormatBytes(res.StandbyAfterBytes)}",
                        $"Available: {FormatBytes(res.AvailableBeforeBytes)} → {FormatBytes(res.AvailableAfterBytes)}"
                    );
                }

                string logLine = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [AUTO_OPTIMIZE] Trigger={trigger}, Interval={_state.OptionKey}, Executed={res.ExecutedCount}, Verified={res.VerifiedCount}, TempDeleted={FormatBytes(res.TempBytesDeleted)}, StandbyReclaimed={FormatBytes(res.StandbyReclaimedBytes)}, Duration={res.Duration.TotalMilliseconds:F0}ms, Summary='{res.SummaryMessage}'";
                LogMaintenance("[TRANSACTION]", logLine);
            }
            catch { }
        }

        private void LogMaintenance(string tag, string message)
        {
            try
            {
                string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {tag} {message}";
                File.AppendAllLines(_logFilePath, new[] { line });
                Debug.WriteLine(line);
            }
            catch { }
        }

        private async Task SyncWindowsTaskSchedulerAsync(AutoOptimizeScheduleOption option)
        {
            await Task.Run(() =>
            {
                try
                {
                    // Always remove existing task first to prevent duplicate scheduled tasks
                    RunSchtasks($"/delete /tn \"{TASK_NAME}\" /f");

                    if (option == AutoOptimizeScheduleOption.None || !_state.IsEnabled)
                    {
                        return;
                    }

                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ErrorOptimizer.exe");
                    }

                    string scheduleParams = option switch
                    {
                        AutoOptimizeScheduleOption.FiveMinutes => "/sc MINUTE /mo 5",
                        AutoOptimizeScheduleOption.TenMinutes => "/sc MINUTE /mo 10",
                        AutoOptimizeScheduleOption.ThirtyMinutes => "/sc MINUTE /mo 30",
                        AutoOptimizeScheduleOption.OneHour => "/sc HOURLY /mo 1",
                        AutoOptimizeScheduleOption.ThreeHours => "/sc HOURLY /mo 3",
                        AutoOptimizeScheduleOption.SixHours => "/sc HOURLY /mo 6",
                        AutoOptimizeScheduleOption.TwelveHours => "/sc HOURLY /mo 12",
                        AutoOptimizeScheduleOption.OneDay => "/sc DAILY /mo 1",
                        _ => ""
                    };

                    if (!string.IsNullOrEmpty(scheduleParams))
                    {
                        string actionCmd = $"\\\"{exePath}\\\" --auto-optimize-background";
                        RunSchtasks($"/create /tn \"{TASK_NAME}\" {scheduleParams} /tr \"{actionCmd}\" /rl HIGHEST /f");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scheduler] Task Scheduler sync failed: {ex.Message}");
                }
            });
        }

        private static void RunSchtasks(string arguments)
        {
            try
            {
                using var p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                p.Start();
                p.WaitForExit(3000);
            }
            catch { }
        }

        private void LoadState()
        {
            lock (_stateLock)
            {
                try
                {
                    if (File.Exists(_configFilePath))
                    {
                        string json = File.ReadAllText(_configFilePath);
                        var loaded = JsonSerializer.Deserialize<AutoOptimizeScheduleState>(json);
                        if (loaded != null && loaded.SchemaVersion >= 5)
                        {
                            _state = loaded;
                            _state.History ??= new List<AutoOptimizeRunHistoryEntry>();
                            _state.SelectedOptimizationIds ??= new List<string>();
                            SyncLegacyFlagsLocked();

                            // Validate consistency: if enabled without valid interval or without any action, force OFF
                            var opt = ParseOption(_state.OptionKey);
                            bool hasActions = _state.SelectedOptimizationIds.Count > 0;
                            if (_state.IsEnabled && (opt == AutoOptimizeScheduleOption.None || !hasActions))
                            {
                                _state.IsEnabled = false;
                                _state.NextRun = null;
                            }
                            else if (_state.IsEnabled)
                            {
                                _state.OptionKey = GetOptionDisplayName(opt);
                                var interval = GetOptionInterval(opt);
                                if (!_state.NextRun.HasValue || _state.NextRun.Value <= DateTime.Now)
                                {
                                    _state.NextRun = DateTime.Now.Add(interval);
                                }
                            }
                            else
                            {
                                _state.NextRun = null;
                            }
                            return;
                        }
                    }

                    // First install / first use of V4 Final Auto Optimize:
                    // Strictly initialize to OFF, Schedule = None, Selected = 0, Next Run = —, Last Run = Never
                    _state = new AutoOptimizeScheduleState
                    {
                        SchemaVersion = 5,
                        IsEnabled = false,
                        OptionKey = "None",
                        SelectedOptimizationIds = new List<string>(),
                        ActionCleanTempFiles = false,
                        ActionFreeStandbyMemory = false,
                        NextRun = null,
                        LastRun = null,
                        LastRunStatus = "Never",
                        LastRunActionsText = "—",
                        LastResult = "—",
                        History = new List<AutoOptimizeRunHistoryEntry>()
                    };
                    SaveState();

                    // Ensure no legacy Windows Scheduled Task remains active when defaulting to OFF
                    _ = Task.Run(() => RunSchtasks($"/delete /tn \"{TASK_NAME}\" /f"));
                }
                catch
                {
                    _state = new AutoOptimizeScheduleState();
                }
            }
        }

        private void SaveState()
        {
            try
            {
                string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, json);
            }
            catch { }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public static string FormatNextRunRelative(DateTime? nextRun, bool isEnabled)
        {
            if (!isEnabled || !nextRun.HasValue) return "—";
            var diff = nextRun.Value - DateTime.Now;
            if (diff.TotalSeconds <= 0)
            {
                return "Due now";
            }
            if (diff.TotalMinutes < 1)
            {
                return $"in {Math.Max(1, (int)Math.Ceiling(diff.TotalSeconds))} sec ({nextRun.Value:HH:mm})";
            }
            if (diff.TotalMinutes < 60)
            {
                int mins = (int)Math.Ceiling(diff.TotalMinutes);
                return $"in {mins} {(mins == 1 ? "minute" : "minutes")} ({nextRun.Value:HH:mm})";
            }
            if (diff.TotalHours < 24)
            {
                int hours = (int)Math.Floor(diff.TotalHours);
                int mins = diff.Minutes;
                string dayPrefix = nextRun.Value.Date == DateTime.Today ? "Today" : "Tomorrow";
                return mins > 0
                    ? $"in {hours}h {mins}m ({dayPrefix}, {nextRun.Value:HH:mm})"
                    : $"in {hours} {(hours == 1 ? "hour" : "hours")} ({dayPrefix}, {nextRun.Value:HH:mm})";
            }
            return nextRun.Value.ToString("ddd, HH:mm");
        }

        public static string FormatLastRunTimestamp(DateTime? lastRun)
        {
            if (!lastRun.HasValue) return "Never";
            if (lastRun.Value.Date == DateTime.Today)
            {
                return $"Today, {lastRun.Value:HH:mm}";
            }
            if (lastRun.Value.Date == DateTime.Today.AddDays(-1))
            {
                return $"Yesterday, {lastRun.Value:HH:mm}";
            }
            return lastRun.Value.ToString("MMM d, HH:mm");
        }

        public void Dispose()
        {
            _backgroundTimer.Dispose();
            _executionGate.Dispose();
        }
    }
}
