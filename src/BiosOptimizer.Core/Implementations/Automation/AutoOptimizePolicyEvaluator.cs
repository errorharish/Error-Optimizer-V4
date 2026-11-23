#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BiosOptimizer.Core.Implementations.Config;

namespace BiosOptimizer.Core.Implementations.Automation;

/// <summary>
/// Evaluated decision produced identically by both the Windows service worker and standalone Host supervisor.
/// </summary>
public sealed record AutoOptimizePolicyEvaluation(
    bool AiEnabled,
    bool PolicyAutoOptimizeEnabled,
    bool ConfigEnabled,
    bool ConfigPaused,
    bool RunOnWindowsStartup,
    bool RunOnLaunch,
    bool IsScheduleEligible,
    bool IsStartupEligible,
    bool DelayStartupByInterval,
    bool ShouldRunOnAppLaunch,
    bool ShouldRunOnAppExit,
    bool ShouldRunScheduledTick,
    IReadOnlyList<string> SelectedTasks,
    string Reason);

/// <summary>
/// Canonical evaluator for Auto Optimize startup, launch, exit, and scheduled interval eligibility.
/// Eliminates policy drift between <c>AutoOptimizeWorker</c> and <c>HostBackgroundSupervisor</c>.
/// </summary>
public static class AutoOptimizePolicyEvaluator
{
    private static readonly HashSet<string> ValidTaskSet = new(AutoOptimizeTaskIds.All, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FilterValidTasks(IEnumerable<string>? rawTasks)
    {
        if (rawTasks == null)
            return Array.Empty<string>();

        return rawTasks
            .Where(t => !string.IsNullOrWhiteSpace(t) && ValidTaskSet.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AutoOptimizePolicyEvaluation Evaluate(
        UnifiedAppSettings? settings,
        AutoOptimizeConfig? cfg,
        DateTime? nextRunUtc = null,
        DateTime? nowUtc = null)
    {
        var s = settings ?? UnifiedSettingsStore.LoadSafe();
        var c = AutoOptimizeEngine.SanitizeConfig(cfg ?? AutoOptimizeEngine.Instance.LoadConfig());
        DateTime now = nowUtc ?? DateTime.UtcNow;

        IReadOnlyList<string> selectedTasks = FilterValidTasks(c.Tasks);
        bool hasSelectedTasks = selectedTasks.Count > 0;
        bool isScheduleEligible = c.Enabled && !c.Paused && hasSelectedTasks;

        // AI Policy autoOptimize is gated behind the master AiEnabled switch, while explicit
        // RunOnWindowsStartup in AutoOptimizeConfig represents explicit user Auto Optimize config.
        bool effectiveAiAutoPolicy = s.AiEnabled && s.Policies.AutoOptimize;
        bool isStartupEligible = isScheduleEligible && (effectiveAiAutoPolicy || c.RunOnWindowsStartup);
        bool delayStartupByInterval = string.Equals(c.StartupPolicy, "next-interval", StringComparison.OrdinalIgnoreCase);

        bool shouldRunLaunch = isScheduleEligible && c.RunOnLaunch;
        bool shouldRunExit = isScheduleEligible &&
            string.Equals(c.CloseBehavior, "exit-run", StringComparison.OrdinalIgnoreCase);
        bool shouldRunTick = isScheduleEligible && nextRunUtc.HasValue && now >= nextRunUtc.Value;

        string reason = !hasSelectedTasks
            ? "No Auto Optimize tasks selected."
            : !c.Enabled
                ? "Auto Optimize scheduler is disabled."
                : c.Paused
                    ? "Auto Optimize scheduler is paused."
                    : "Auto Optimize scheduler is eligible.";

        return new AutoOptimizePolicyEvaluation(
            AiEnabled: s.AiEnabled,
            PolicyAutoOptimizeEnabled: s.Policies.AutoOptimize,
            ConfigEnabled: c.Enabled,
            ConfigPaused: c.Paused,
            RunOnWindowsStartup: c.RunOnWindowsStartup,
            RunOnLaunch: c.RunOnLaunch,
            IsScheduleEligible: isScheduleEligible,
            IsStartupEligible: isStartupEligible,
            DelayStartupByInterval: delayStartupByInterval,
            ShouldRunOnAppLaunch: shouldRunLaunch,
            ShouldRunOnAppExit: shouldRunExit,
            ShouldRunScheduledTick: shouldRunTick,
            SelectedTasks: selectedTasks,
            Reason: reason);
    }
}
