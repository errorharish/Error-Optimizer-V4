#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Memory;
using BiosOptimizer.Core.Implementations.RegistryValues;
using BiosOptimizer.Core.Implementations.Startup;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.GUI.ViewModels.Base;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.GUI.Services
{
    /// <summary>
    /// Resolution lifecycle for every high-impact optimization opportunity:
    /// DETECTED -> ACTION STARTED -> ACTION APPLIED -> VERIFICATION -> RESCAN -> RESOLVED
    /// </summary>
    public enum OptimizationOpportunityLifecycleState
    {
        Detected = 0,
        ActionStarted = 1,
        ActionApplied = 2,
        Verification = 3,
        Rescan = 4,
        Resolved = 5
    }

    /// <summary>
    /// Unified, reusable model for a high-impact system performance opportunity displayed in
    /// the Dashboard "WHAT NEEDS TO BE OPTIMIZED" section.
    /// Strictly excludes routine Quick Maintenance actions (Temp cleanup, Recycle Bin, Browser Cache, DNS flush).
    /// </summary>
    public class OptimizationOpportunity : ViewModelBase
    {
        private string _title = string.Empty;
        private string _userDescription = string.Empty;
        private string _evidence = string.Empty;
        private string _impact = "HIGH";
        private string _actionText = "OPTIMIZE";
        private string _currentState = string.Empty;
        private OptimizationOpportunityLifecycleState _lifecycleState = OptimizationOpportunityLifecycleState.Detected;
        private bool _isExecuting;
        private DateTime? _resolvedAt;

        public string Id { get; set; } = string.Empty;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string UserDescription
        {
            get => _userDescription;
            set
            {
                _userDescription = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Explanation));
            }
        }

        /// <summary>
        /// Alias for XAML templates binding to Explanation.
        /// </summary>
        public string Explanation
        {
            get => _userDescription;
            set => UserDescription = value;
        }

        public string Icon { get; set; } = "\uE945";

        /// <summary>
        /// Optimization module category (ProcessReduction, StartupOptimization, PowerOptimization,
        /// MemoryOptimization, SchedulerOptimization, StorageOptimization, NetworkOptimization,
        /// GamingOptimization, EmulatorOptimization, etc.).
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Priority score (1-100) used for deterministic ordering alongside Impact, Confidence, and Workload relevance.
        /// </summary>
        public int Priority { get; set; } = 50;

        /// <summary>
        /// Impact level: "HIGH", "MEDIUM", or "LOW".
        /// </summary>
        public string Impact
        {
            get => _impact;
            set
            {
                _impact = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SeverityLabel));
                OnPropertyChanged(nameof(ImpactBadgeText));
                OnPropertyChanged(nameof(SeverityBrush));
                OnPropertyChanged(nameof(ImpactBadgeBackground));
            }
        }

        public string Risk { get; set; } = "SAFE";

        /// <summary>
        /// Detection confidence (0.0 to 1.0).
        /// </summary>
        public double Confidence { get; set; } = 0.95;

        /// <summary>
        /// True when real system diagnostics confirmed this opportunity is active on this PC.
        /// </summary>
        public bool Detected { get; set; } = true;

        /// <summary>
        /// The real diagnostic engine or telemetry provider that detected this opportunity.
        /// </summary>
        public string DetectionSource { get; set; } = string.Empty;

        public string ActionId { get; set; } = string.Empty;

        public string ActionText
        {
            get => _actionText;
            set { _actionText = value; OnPropertyChanged(); }
        }

        public string ActionRoute { get; set; } = "Dashboard";

        public string VerificationId { get; set; } = string.Empty;

        public string CurrentState
        {
            get => _currentState;
            set { _currentState = value; OnPropertyChanged(); }
        }

        public OptimizationOpportunityLifecycleState LifecycleState
        {
            get => _lifecycleState;
            set
            {
                _lifecycleState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LifecycleStateText));
            }
        }

        public string LifecycleStateText => _lifecycleState switch
        {
            OptimizationOpportunityLifecycleState.Detected => "DETECTED",
            OptimizationOpportunityLifecycleState.ActionStarted => "ACTION STARTED",
            OptimizationOpportunityLifecycleState.ActionApplied => "ACTION APPLIED",
            OptimizationOpportunityLifecycleState.Verification => "VERIFYING",
            OptimizationOpportunityLifecycleState.Rescan => "RESCANNING",
            OptimizationOpportunityLifecycleState.Resolved => "RESOLVED",
            _ => "DETECTED"
        };

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? ResolvedAt
        {
            get => _resolvedAt;
            set { _resolvedAt = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Concise real diagnostic evidence (e.g. "Potential workload: 242 processes • Protected: 19 • Safe candidates: 8").
        /// </summary>
        public string Evidence
        {
            get => _evidence;
            set
            {
                _evidence = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasEvidence));
            }
        }

        public bool HasEvidence => !string.IsNullOrWhiteSpace(_evidence);

        public int TotalWorkloadCount { get; set; }
        public int ProtectedCount { get; set; }
        public int SafeCandidateCount { get; set; }
        public bool HasWorkloadBreakdown => TotalWorkloadCount > 0 && SafeCandidateCount > 0;

        /// <summary>
        /// Indicates that V4 Protected Application Policy exclusions were enforced during detection.
        /// </summary>
        public bool Protected { get; set; } = true;

        public bool AutoSchedulable { get; set; } = false;

        public bool Available { get; set; } = true;

        public bool IsActionable { get; set; } = true;

        public bool IsExecuting
        {
            get => _isExecuting;
            set
            {
                _isExecuting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanExecuteAction));
            }
        }

        public bool CanExecuteAction => IsActionable && !IsExecuting;

        public ICommand? FixCommand { get; set; }

        public string ImpactBadgeText => Impact == "HIGH"
            ? "HIGH IMPACT"
            : Impact == "MEDIUM"
                ? "MEDIUM IMPACT"
                : "RECOMMENDED";

        public string SeverityLabel => ImpactBadgeText;

        public Brush SeverityBrush => Impact == "HIGH"
            ? BrushHelper.GetFrozenBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
            : Impact == "MEDIUM"
                ? BrushHelper.GetFrozenBrush(Color.FromRgb(0x68, 0x72, 0xD9))
                : BrushHelper.GetFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81));

        public Brush ImpactBadgeBackground => Impact == "HIGH"
            ? BrushHelper.GetFrozenBrush(Color.FromArgb(0x24, 0xF5, 0x9E, 0x0B))
            : Impact == "MEDIUM"
                ? BrushHelper.GetFrozenBrush(Color.FromArgb(0x24, 0x68, 0x72, 0xD9))
                : BrushHelper.GetFrozenBrush(Color.FromArgb(0x20, 0x10, 0xB9, 0x81));
    }

    /// <summary>
    /// Diagnostic snapshot context supplied to all registered opportunity rules.
    /// </summary>
    public sealed class OptimizationOpportunityContext
    {
        public int TotalProcesses { get; set; }
        public int ProtectedProcesses { get; set; }
        public int SafeReducibleProcesses { get; set; }
        public double SafeReducibleRamMb { get; set; }
        public List<int> SafeReduciblePids { get; set; } = new();

        public MemoryTelemetryState? MemorySnapshot { get; set; }

        public int TotalEnabledStartupApps { get; set; }
        public int ProtectedStartupApps { get; set; }
        public int UnnecessaryStartupApps { get; set; }
        public List<string> UnnecessaryStartupNames { get; set; } = new();

        public string ActivePowerPlan { get; set; } = "Balanced";
        public string DetectedWorkload { get; set; } = "General Everyday Use";
        public string RecommendedProfile { get; set; } = "Normal";

        public int PendingGpuOptimizationsCount { get; set; }
        public string GpuHardwareName { get; set; } = string.Empty;
        public int PendingEmulatorOptimizationsCount { get; set; }

        public OneClickPlan? OneClickPlan { get; set; }
        public FullOptimizationMasterPlan? MasterPlan { get; set; }
    }

    /// <summary>
    /// Contract for modular optimization opportunity evaluators.
    /// Future optimization engines can implement and register this interface with OptimizationOpportunityEngine
    /// without modifying Dashboard UI or ViewModel logic.
    /// </summary>
    public interface IOptimizationOpportunityRule
    {
        string RuleId { get; }
        string Category { get; }
        OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context);
    }

    /// <summary>
    /// Central engine that discovers, filters, and ranks high-impact performance optimization opportunities
    /// for the Dashboard "WHAT NEEDS TO BE OPTIMIZED" section.
    /// </summary>
    public sealed class OptimizationOpportunityEngine
    {
        private static readonly Lazy<OptimizationOpportunityEngine> _instance = new(() => new OptimizationOpportunityEngine());
        public static OptimizationOpportunityEngine Instance => _instance.Value;

        private readonly List<IOptimizationOpportunityRule> _rules = new();
        private readonly object _sync = new();

        private OptimizationOpportunityEngine()
        {
            // Register core high-impact performance opportunity evaluators
            RegisterRule(new ProcessReductionOpportunityRule());
            RegisterRule(new HighMemoryPressureOpportunityRule());
            RegisterRule(new PowerModeOpportunityRule());
            RegisterRule(new StartupWorkloadOpportunityRule());
            RegisterRule(new GpuSchedulingOpportunityRule());
            RegisterRule(new SystemResponsivenessOpportunityRule());
            RegisterRule(new NetworkLatencyOpportunityRule());
            RegisterRule(new StorageResponsivenessOpportunityRule());
            RegisterRule(new EmulatorPerformanceOpportunityRule());
            RegisterRule(new InputAimStabilityOpportunityRule());
        }

        /// <summary>
        /// Registers a new high-impact optimization opportunity rule dynamically.
        /// Replaces any existing rule with the same RuleId.
        /// </summary>
        public void RegisterRule(IOptimizationOpportunityRule rule)
        {
            if (rule == null) return;
            lock (_sync)
            {
                _rules.RemoveAll(r => r.RuleId.Equals(rule.RuleId, StringComparison.OrdinalIgnoreCase));
                _rules.Add(rule);
            }
        }

        public IReadOnlyList<IOptimizationOpportunityRule> GetRegisteredRules()
        {
            lock (_sync)
            {
                return _rules.ToList();
            }
        }

        /// <summary>
        /// Captures real diagnostic evidence across Process Reduction (respecting V4 ProtectedApplicationPolicy),
        /// Startup Manager (respecting V4 ProtectedApplicationPolicy), Memory Telemetry, Power Plan, GPU Registry,
        /// and Master/One-Click plans.
        /// </summary>
        public async Task<OptimizationOpportunityContext> CaptureDiagnosticContextAsync(
            IIpcClient? ipc,
            string? activePowerPlan,
            string? detectedWorkload,
            string? recommendedProfile,
            OneClickPlan? oneClickPlan,
            FullOptimizationMasterPlan? masterPlan,
            CancellationToken ct = default)
        {
            var ctx = new OptimizationOpportunityContext
            {
                ActivePowerPlan = !string.IsNullOrWhiteSpace(activePowerPlan) ? activePowerPlan : "Balanced",
                DetectedWorkload = !string.IsNullOrWhiteSpace(detectedWorkload) ? detectedWorkload : "General Everyday Use",
                RecommendedProfile = !string.IsNullOrWhiteSpace(recommendedProfile) ? recommendedProfile : "Normal",
                OneClickPlan = oneClickPlan,
                MasterPlan = masterPlan
            };

            await Task.Run(async () =>
            {
                // 1. Real Memory Telemetry
                try
                {
                    ctx.MemorySnapshot = WindowsMemoryTelemetryProvider.Instance.SampleCurrentMemoryState();
                }
                catch { }

                // 2. Real Process Reduction Scan (with V4 Protected Application Policy enforcement)
                try
                {
                    bool populatedFromIpc = false;
                    if (ipc != null && ipc.IsOnline)
                    {
                        var resp = await ipc.SendRequestAsync(IpcMessageType.GetProcessCandidates, null, ct);
                        if (resp.Success && !string.IsNullOrEmpty(resp.Data))
                        {
                            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                            var candidates = JsonSerializer.Deserialize<List<ProcessCandidateModel>>(resp.Data, options);
                            if (candidates != null && candidates.Count > 0)
                            {
                                int total = candidates.Count;
                                int protectedCount = 0;
                                int safeCount = 0;
                                double safeRam = 0;
                                var safePids = new List<int>();

                                foreach (var c in candidates)
                                {
                                    // Double-verify against V4 Protected Application Policy
                                    var policyMatch = ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(
                                        c.ProcessName,
                                        c.ProcessPath,
                                        c.Publisher,
                                        !string.IsNullOrWhiteSpace(c.Publisher));

                                    if (policyMatch.IsProtected || c.IsProtected)
                                    {
                                        protectedCount++;
                                    }
                                    else if (c.IsSafeToKill)
                                    {
                                        safeCount++;
                                        safeRam += c.RamUsageMb;
                                        safePids.Add(c.ProcessId);
                                    }
                                }

                                ctx.TotalProcesses = total;
                                ctx.ProtectedProcesses = protectedCount;
                                ctx.SafeReducibleProcesses = safeCount;
                                ctx.SafeReducibleRamMb = Math.Round(safeRam, 1);
                                ctx.SafeReduciblePids = safePids;
                                populatedFromIpc = true;
                            }
                        }
                    }

                    if (!populatedFromIpc)
                    {
                        // Local fallback using V4 ProcessIntelligenceEngine + ProtectedApplicationPolicy
                        var engine = new ProcessIntelligenceEngine();
                        var snap = await engine.CaptureSnapshotAsync();
                        ctx.TotalProcesses = snap.TotalProcessCount;
                        ctx.ProtectedProcesses = snap.ProtectedProcessCount + snap.RequiredProcessCount + snap.WorkloadProcessCount;
                        var reducibleList = snap.Processes
                            .Where(p => (p.Classification == ProcessClassification.REDUCIBLE ||
                                         p.Classification == ProcessClassification.IMMEDIATE_REDUCIBLE) &&
                                        !ProtectedApplicationPolicy.Instance.IsProcessProtected(p))
                            .ToList();
                        ctx.SafeReducibleProcesses = reducibleList.Count;
                        ctx.SafeReducibleRamMb = Math.Round(reducibleList.Sum(p => p.RamWorkingSetMb), 1);
                        ctx.SafeReduciblePids = reducibleList.Select(p => p.ProcessId).ToList();
                    }
                }
                catch { }

                // 3. Real Startup Workload Scan (excluding V4 Protected Applications & core OS/drivers)
                try
                {
                    var mgr = new WindowsStartupManager();
                    var entries = mgr.GetStartupEntries();
                    var enabledEntries = entries.Where(e => e.State == StartupState.Enabled).ToList();

                    int protectedStartups = 0;
                    var unnecessaryStartups = new List<string>();

                    foreach (var entry in enabledEntries)
                    {
                        string entryName = !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName : entry.ValueName;
                        var match = ProtectedApplicationPolicy.Instance.EvaluateStartupEntry(
                            entry.ValueName,
                            entry.CommandLine,
                            entry.ExePath);

                        if (!match.IsProtected && !string.IsNullOrWhiteSpace(entry.DisplayName))
                        {
                            match = ProtectedApplicationPolicy.Instance.EvaluateStartupEntry(
                                entry.DisplayName,
                                entry.CommandLine,
                                entry.ExePath);
                        }

                        bool isCoreWindowsOrDriver =
                            entry.IsSystemItem ||
                            entryName.Contains("Windows Security", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("SecurityHealth", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("ErrorOptimizer", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("BiosOptimizer", StringComparison.OrdinalIgnoreCase);

                        if (match.IsProtected || isCoreWindowsOrDriver)
                        {
                            protectedStartups++;
                        }
                        else
                        {
                            unnecessaryStartups.Add(entryName);
                        }
                    }

                    ctx.TotalEnabledStartupApps = enabledEntries.Count;
                    ctx.ProtectedStartupApps = protectedStartups;
                    ctx.UnnecessaryStartupApps = unnecessaryStartups.Count;
                    ctx.UnnecessaryStartupNames = unnecessaryStartups;
                }
                catch { }

                // 4. Real GPU Registry & Multimedia Scheduling Scan
                try
                {
                    var hw = GpuRegistryValueEngine.Instance.DetectGpuHardware(false);
                    var gpuOpts = GpuRegistryValueEngine.Instance.ScanAllGpuOptimizations("Normal", false);
                    var pendingGpu = gpuOpts
                        .Where(g => g.Category != GpuRegistryCategory.Diagnostic &&
                                    (g.Status == RegistryValueStatus.Recommended || g.Status == RegistryValueStatus.RequiresRestart) &&
                                    g.CanApply)
                        .ToList();
                    ctx.GpuHardwareName = hw.Name;
                    ctx.PendingGpuOptimizationsCount = pendingGpu.Count;
                }
                catch { }
            }, ct);

            return ctx;
        }

        /// <summary>
        /// Evaluates all registered rules against the real diagnostic context,
        /// filters to active detected opportunities, and ranks the top 3-5 items by:
        /// 1. Actual impact
        /// 2. Severity / priority
        /// 3. Confidence
        /// 4. Relevance to current workload
        /// </summary>
        public List<OptimizationOpportunity> EvaluateOpportunities(
            OptimizationOpportunityContext context,
            int maxItems = 0)
        {
            List<IOptimizationOpportunityRule> rulesSnapshot;
            lock (_sync)
            {
                rulesSnapshot = _rules.ToList();
            }

            var detectedList = new List<OptimizationOpportunity>();
            foreach (var rule in rulesSnapshot)
            {
                try
                {
                    var opp = rule.Evaluate(context);
                    if (opp != null && opp.Detected && opp.Available)
                    {
                        detectedList.Add(opp);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OptimizationOpportunityEngine] Rule {rule.RuleId} error: {ex.Message}");
                }
            }

            bool hasActiveCreativeOrGamingWorkload =
                !string.IsNullOrWhiteSpace(context.DetectedWorkload) &&
                !context.DetectedWorkload.Equals("General Everyday Use", StringComparison.OrdinalIgnoreCase);

            var ranked = detectedList
                .OrderByDescending(o => GetImpactWeight(o.Impact))
                .ThenByDescending(o => GetWorkloadRelevanceBonus(o, context.DetectedWorkload, hasActiveCreativeOrGamingWorkload))
                .ThenByDescending(o => o.Priority)
                .ThenByDescending(o => o.Confidence);

            return (maxItems > 0 ? ranked.Take(maxItems) : ranked).ToList();
        }

        private static int GetImpactWeight(string impact) => impact?.ToUpperInvariant() switch
        {
            "HIGH" => 3,
            "MEDIUM" => 2,
            _ => 1
        };

        private static int GetWorkloadRelevanceBonus(OptimizationOpportunity opp, string workload, bool hasActiveWorkload)
        {
            if (!hasActiveWorkload) return 0;
            if (workload.Contains("Emulator", StringComparison.OrdinalIgnoreCase) &&
                opp.Category.Equals("EmulatorOptimization", StringComparison.OrdinalIgnoreCase))
                return 25;
            if ((workload.Contains("After Effects", StringComparison.OrdinalIgnoreCase) ||
                 workload.Contains("Topaz", StringComparison.OrdinalIgnoreCase) ||
                 workload.Contains("Video Editing", StringComparison.OrdinalIgnoreCase)) &&
                (opp.Category.Equals("MemoryOptimization", StringComparison.OrdinalIgnoreCase) ||
                 opp.Category.Equals("ProcessReduction", StringComparison.OrdinalIgnoreCase) ||
                 opp.Category.Equals("PowerOptimization", StringComparison.OrdinalIgnoreCase)))
                return 15;
            return 0;
        }
    }

    // =========================================================================
    // BUILT-IN HIGH-IMPACT PERFORMANCE OPPORTUNITY RULES
    // =========================================================================

    /// <summary>
    /// 1. PROCESS REDUCTION / BACKGROUND WORKLOAD (Mandatory)
    /// Only triggers when safe reducible background processes > 0 after enforcing V4 ProtectedApplicationPolicy.
    /// Never shows raw process count as a problem when SafeReducibleProcesses == 0.
    /// </summary>
    public sealed class ProcessReductionOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.process.reduction";
        public string Category => "ProcessReduction";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            if (context.SafeReducibleProcesses <= 0)
                return null;

            bool isHighImpact = context.SafeReducibleProcesses >= 5 || context.SafeReducibleRamMb >= 250.0;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "REDUCE UNNECESSARY BACKGROUND WORK",
                UserDescription = "Safe background processes were identified that may be reduced.",
                Evidence = $"Potential workload: {context.TotalProcesses} processes • Protected: {context.ProtectedProcesses} • Safe candidates: {context.SafeReducibleProcesses}",
                Icon = "\uE8D0",
                Category = Category,
                Priority = isHighImpact ? 95 : 85,
                Impact = isHighImpact ? "HIGH" : "MEDIUM",
                Confidence = 0.96,
                Detected = true,
                DetectionSource = "V4 ProcessIntelligenceEngine + ProtectedApplicationPolicy",
                ActionId = "ProcessReduction.ReviewOrReduce",
                ActionText = "REVIEW",
                ActionRoute = "Dashboard",
                VerificationId = "Verify.SafeProcessCandidatesCount",
                CurrentState = $"{context.SafeReducibleProcesses} Safe Candidates",
                TotalWorkloadCount = context.TotalProcesses,
                ProtectedCount = context.ProtectedProcesses,
                SafeCandidateCount = context.SafeReducibleProcesses,
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 2. HIGH MEMORY PRESSURE
    /// Only triggers when real physical memory usage is genuinely elevated (>= 75% RAM used),
    /// NOT as a routine standby cache button when memory is healthy.
    /// </summary>
    public sealed class HighMemoryPressureOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.memory.pressure";
        public string Category => "MemoryOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            var snap = context.MemorySnapshot;
            if (snap == null || snap.MemoryPressurePercent < 75.0)
                return null;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "HIGH MEMORY PRESSURE",
                UserDescription = "High memory usage may affect active applications.",
                Evidence = $"{snap.MemoryPressurePercent:F0}% RAM used ({snap.AvailableFormatted} available • {snap.StandbyFormatted} reclaimable cache)",
                Icon = "\uE950",
                Category = Category,
                Priority = snap.MemoryPressurePercent >= 85.0 ? 98 : 90,
                Impact = "HIGH",
                Confidence = 0.98,
                Detected = true,
                DetectionSource = "WindowsMemoryTelemetryProvider",
                ActionId = "MemoryOptimization.ReclaimPressure",
                ActionText = "OPTIMIZE",
                ActionRoute = "AiRamLimiter",
                VerificationId = "Verify.MemoryPressureReduced",
                CurrentState = $"{snap.MemoryPressurePercent:F0}% RAM Used",
                Protected = true,
                AutoSchedulable = true,
                Available = true
            };
        }
    }

    /// <summary>
    /// 3. PERFORMANCE POWER MODE
    /// Triggers when Windows is using Balanced or Power Saver instead of High/Ultimate/Max Performance.
    /// </summary>
    public sealed class PowerModeOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.power.performance_mode";
        public string Category => "PowerOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            string plan = context.ActivePowerPlan ?? "Balanced";
            bool isSubOptimal = plan.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                                plan.Contains("Power Saver", StringComparison.OrdinalIgnoreCase) ||
                                plan.Contains("Eco", StringComparison.OrdinalIgnoreCase);

            if (!isSubOptimal)
                return null;

            bool isPowerSaver = plan.Contains("Power Saver", StringComparison.OrdinalIgnoreCase);

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "PERFORMANCE POWER MODE",
                UserDescription = "Balanced power mode may be limiting sustained performance.",
                Evidence = $"Active plan: {plan} • Switch to High / Ultimate Performance",
                Icon = "\uE7E8",
                Category = Category,
                Priority = isPowerSaver ? 92 : 82,
                Impact = isPowerSaver ? "HIGH" : "MEDIUM",
                Confidence = 0.99,
                Detected = true,
                DetectionSource = "PowerPlanEngine",
                ActionId = "PowerOptimization.ActivateHighPerformance",
                ActionText = "OPTIMIZE",
                ActionRoute = "Normal",
                VerificationId = "Verify.ActivePowerPlanGuid",
                CurrentState = plan,
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 4. STARTUP WORKLOAD
    /// Triggers when non-protected, non-essential startup apps launch automatically with Windows.
    /// Strictly excludes protected creative/gaming/VPN/remote/OEM apps (Adobe, Maxon, Red Giant, Topaz,
    /// DaVinci Resolve, Steam, Lenovo Vantage, UltraViewer, Spotify, Urban VPN).
    /// </summary>
    public sealed class StartupWorkloadOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.startup.workload";
        public string Category => "StartupOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            if (context.UnnecessaryStartupApps < 2)
                return null;

            bool isHigh = context.UnnecessaryStartupApps >= 6;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "STARTUP WORKLOAD",
                UserDescription = "Several unnecessary apps are loading with Windows.",
                Evidence = $"Startup items: {context.TotalEnabledStartupApps} enabled • Protected: {context.ProtectedStartupApps} • Non-essential: {context.UnnecessaryStartupApps}",
                Icon = "\uE7F4",
                Category = Category,
                Priority = isHigh ? 86 : 76,
                Impact = isHigh ? "HIGH" : "MEDIUM",
                Confidence = 0.92,
                Detected = true,
                DetectionSource = "WindowsStartupManager + ProtectedApplicationPolicy",
                ActionId = "StartupOptimization.ReviewEntries",
                ActionText = "REVIEW",
                ActionRoute = "StartupManager",
                VerificationId = "Verify.EnabledStartupEntries",
                CurrentState = $"{context.UnnecessaryStartupApps} Non-Essential Apps",
                TotalWorkloadCount = context.TotalEnabledStartupApps,
                ProtectedCount = context.ProtectedStartupApps,
                SafeCandidateCount = context.UnnecessaryStartupApps,
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 5. GPU & MULTIMEDIA SCHEDULING (Gaming / Editing Performance)
    /// Triggers when real hardware-aware GPU latency & MMCSS scheduling optimizations are pending.
    /// </summary>
    public sealed class GpuSchedulingOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.gaming.gpu_scheduling";
        public string Category => "GamingOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            if (context.PendingGpuOptimizationsCount <= 0)
                return null;

            string gpuLabel = !string.IsNullOrWhiteSpace(context.GpuHardwareName)
                ? context.GpuHardwareName
                : "Graphics Adapter";

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "GPU & MULTIMEDIA SCHEDULING",
                UserDescription = "Graphics driver latency and multimedia scheduling can be optimized.",
                Evidence = $"{context.PendingGpuOptimizationsCount} hardware-verified GPU parameters ready for {gpuLabel}",
                Icon = "\uE7F8",
                Category = Category,
                Priority = 88,
                Impact = "HIGH",
                Confidence = 0.95,
                Detected = true,
                DetectionSource = "GpuRegistryValueEngine",
                ActionId = "GamingOptimization.ApplyGpuScheduling",
                ActionText = "OPTIMIZE",
                ActionRoute = "GpuRegistryValues",
                VerificationId = "Verify.GpuRegistryReadback",
                CurrentState = $"{context.PendingGpuOptimizationsCount} Pending",
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 6. SYSTEM RESPONSIVENESS & CPU SCHEDULING
    /// Triggers when real One-Click or Core Scheduler responsiveness settings are at unoptimized Windows defaults.
    /// </summary>
    public sealed class SystemResponsivenessOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.scheduler.system_responsiveness";
        public string Category => "SchedulerOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            int oneClickReady = context.OneClickPlan?.RecommendedCount ?? 0;
            int schedulerPending = context.MasterPlan?.AllActions.Count(a =>
                (a.CategoryKey.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
                 a.CategoryKey.Equals("Pro", StringComparison.OrdinalIgnoreCase) ||
                 a.CategoryKey.Equals("BiosSafe", StringComparison.OrdinalIgnoreCase)) &&
                a.IsPending) ?? 0;

            int totalPending = Math.Max(oneClickReady, schedulerPending);
            if (totalPending <= 0)
                return null;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "SYSTEM RESPONSIVENESS & SCHEDULING",
                UserDescription = "Foreground CPU priority and desktop responsiveness settings can be tuned.",
                Evidence = $"{totalPending} safe scheduler & responsiveness improvements detected",
                Icon = "\uE945",
                Category = Category,
                Priority = totalPending >= 4 ? 87 : 78,
                Impact = totalPending >= 4 ? "HIGH" : "MEDIUM",
                Confidence = 0.94,
                Detected = true,
                DetectionSource = "OneClickOptimizationEngine + FullOptimizationCoordinator",
                ActionId = "SchedulerOptimization.ApplyBaseline",
                ActionText = "OPTIMIZE",
                ActionRoute = "OneClick",
                VerificationId = "Verify.SchedulerBaselineReadback",
                CurrentState = $"{totalPending} Ready",
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 7. NETWORK LATENCY & THROTTLING CONFIGURATION
    /// Triggers when real TCP/IP auto-tuning or network throttling index settings are unoptimized.
    /// Never triggers for routine DNS cache flushing.
    /// </summary>
    public sealed class NetworkLatencyOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.network.latency_config";
        public string Category => "NetworkOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            var pendingNet = context.MasterPlan?.AllActions
                .Where(a => a.CategoryKey.Equals("Network", StringComparison.OrdinalIgnoreCase) &&
                            a.IsPending &&
                            !a.ItemId.Contains("dns", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pendingNet == null || pendingNet.Count == 0)
                return null;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "NETWORK LATENCY CONFIGURATION",
                UserDescription = "Network packet throttling and TCP latency settings can be improved.",
                Evidence = $"{pendingNet.Count} low-latency network stack configurations available",
                Icon = "\uE774",
                Category = Category,
                Priority = 72,
                Impact = "MEDIUM",
                Confidence = 0.91,
                Detected = true,
                DetectionSource = "FullOptimizationCoordinator (NetworkOptimizerEngine)",
                ActionId = "NetworkOptimization.ApplyLowLatency",
                ActionText = "OPTIMIZE",
                ActionRoute = "Network",
                VerificationId = "Verify.NetworkLatencyConfig",
                CurrentState = $"{pendingNet.Count} Sub-optimal",
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 8. STORAGE I/O RESPONSIVENESS
    /// Triggers only when real NVMe/SSD filesystem responsiveness settings are pending.
    /// Strictly excludes Temp file cleanup, Recycle Bin, and Browser Cache cleanup.
    /// </summary>
    public sealed class StorageResponsivenessOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.storage.io_responsiveness";
        public string Category => "StorageOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            var pendingStorage = context.MasterPlan?.AllActions
                .Where(a => a.CategoryKey.Equals("Storage", StringComparison.OrdinalIgnoreCase) &&
                            a.IsPending &&
                            !a.ItemId.Contains("temp", StringComparison.OrdinalIgnoreCase) &&
                            !a.ItemId.Contains("cache", StringComparison.OrdinalIgnoreCase) &&
                            !a.ItemId.Contains("recycle", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pendingStorage == null || pendingStorage.Count == 0)
                return null;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "STORAGE I/O RESPONSIVENESS",
                UserDescription = "System drive I/O and filesystem responsiveness settings can be optimized.",
                Evidence = $"{pendingStorage.Count} storage responsiveness parameters pending verification",
                Icon = "\uEDA2",
                Category = Category,
                Priority = 70,
                Impact = "MEDIUM",
                Confidence = 0.90,
                Detected = true,
                DetectionSource = "FullOptimizationCoordinator (StorageEngine)",
                ActionId = "StorageOptimization.ApplyIoResponsiveness",
                ActionText = "OPTIMIZE",
                ActionRoute = "Storage",
                VerificationId = "Verify.StorageIoResponsiveness",
                CurrentState = $"{pendingStorage.Count} Pending",
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 9. EMULATOR PERFORMANCE CONFIGURATION
    /// Triggers when an Android emulator workload is active on the system.
    /// </summary>
    public sealed class EmulatorPerformanceOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.emulator.performance";
        public string Category => "EmulatorOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            if (string.IsNullOrWhiteSpace(context.DetectedWorkload) ||
                !context.DetectedWorkload.Contains("Emulator", StringComparison.OrdinalIgnoreCase))
                return null;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "EMULATOR PERFORMANCE TUNING",
                UserDescription = "Active Android emulator detected — high-priority CPU & graphics allocation available.",
                Evidence = $"Detected workload: {context.DetectedWorkload}",
                Icon = "\uE7FC",
                Category = Category,
                Priority = 89,
                Impact = "HIGH",
                Confidence = 0.95,
                Detected = true,
                DetectionSource = "UserWorkloadDetector (EmulatorWorkloadDetector)",
                ActionId = "EmulatorOptimization.Review",
                ActionText = "REVIEW",
                ActionRoute = "Emulator",
                VerificationId = "Verify.EmulatorProfileState",
                CurrentState = "Emulator Active",
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

    /// <summary>
    /// 10. INPUT PRECISION & 1:1 AIM STABILITY (New Engine Opportunity)
    /// Triggers when Windows cursor acceleration curves or non-optimal keyboard repeat rates are detected.
    /// </summary>
    public sealed class InputAimStabilityOpportunityRule : IOptimizationOpportunityRule
    {
        public string RuleId => "opp.input.aim_stability";
        public string Category => "InputOptimization";

        public OptimizationOpportunity? Evaluate(OptimizationOpportunityContext context)
        {
            var profile = InputOptimizerEngine.Instance.GetComprehensiveInputProfile();
            if (profile.IsOptimalForGaming)
                return null;

            return new OptimizationOpportunity
            {
                Id = RuleId,
                Title = "INPUT RESPONSIVENESS & AIM STABILITY",
                UserDescription = "Non-linear cursor acceleration or default keyboard repeat delay is active.",
                Evidence = $"MarkC 1:1 Curves: {(profile.IsMarkCCurveActive ? "ACTIVE" : "INACTIVE")} • Repeat: {profile.KeyboardSpeed} (Delay: {profile.KeyboardDelay})",
                Icon = "\uE962",
                Category = Category,
                Priority = 86,
                Impact = "HIGH",
                Confidence = 0.95,
                Detected = true,
                DetectionSource = "InputOptimizerEngine (1:1 MarkC Curve Evaluator)",
                ActionId = "InputOptimization.ApplyPrecision",
                ActionText = "OPTIMIZE",
                ActionRoute = "InputOptimizer",
                VerificationId = "Verify.InputCurvesReadback",
                CurrentState = profile.IsMarkCCurveActive ? "Sub-optimal Repeat Rate" : "Acceleration Enabled",
                Protected = true,
                AutoSchedulable = false,
                Available = true
            };
        }
    }

}
