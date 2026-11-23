#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Power;

namespace BiosOptimizer.Core.Implementations.Config;

public sealed class AiPolicyReconcileResult
{
    public bool PowerPlanOk { get; set; } = true;
    public bool WorkloadOk { get; set; } = true;
    public bool AllSucceeded => PowerPlanOk && WorkloadOk;
    public List<string> Actions { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Live, single-flight reconciler that synchronizes Windows power plan and workload optimization monitor
/// whenever <see cref="UnifiedAppSettings"/> change or services initialize.
/// Disabling any policy stops its active worker and reverts managed runtime state; failures are logged
/// without silently disabling saved policy toggles.
/// </summary>
public static class AiPolicyReconciler
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public static async Task<AiPolicyReconcileResult> ReconcileAsync(
        UnifiedAppSettings? settings,
        bool managePowerPlan = true,
        Func<Task>? hostRevertWorkloadAsync = null,
        CancellationToken ct = default)
    {
        var result = new AiPolicyReconcileResult();
        var s = settings ?? UnifiedSettingsStore.LoadSafe();

        bool wantPowerPlan = s.AiEnabled && s.Policies.PowerPlan;
        bool wantWorkload = s.AiEnabled && s.Policies.Workload;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Power Plan Policy
            if (managePowerPlan)
            {
                try
                {
                    if (wantPowerPlan)
                    {
                        var planRes = await PowerPlanEngine.Instance
                            .CreateOrGetAiMaxPerformancePlanAsync(ct)
                            .ConfigureAwait(false);
                        if (!planRes.Success)
                        {
                            result.PowerPlanOk = false;
                            string msg = string.IsNullOrWhiteSpace(planRes.Message)
                                ? "Failed to activate AI Max Performance power plan."
                                : planRes.Message;
                            result.Errors.Add($"PowerPlan: {msg}");
                            AuditLogManager.Write("AiPolicy", "AI.PowerPlan", "Failed", msg);
                        }
                        else
                        {
                            result.Actions.Add($"PowerPlan activated ({planRes.ActiveSchemeName})");
                            AuditLogManager.Write("AiPolicy", "AI.PowerPlan", "Applied",
                                $"Activated AI Power Plan ({planRes.ActiveSchemeName}).");
                        }
                    }
                    else
                    {
                        PowerPlanEngine.Instance.StopBackgroundEnforcement();
                        if (await PowerPlanEngine.Instance.IsCurrentSchemeAiManagedAsync(ct).ConfigureAwait(false))
                        {
                            var restoreRes = await PowerPlanEngine.Instance
                                .RestorePreviousPowerPlanAsync(ct)
                                .ConfigureAwait(false);
                            if (!restoreRes.Success)
                            {
                                result.PowerPlanOk = false;
                                string msg = string.IsNullOrWhiteSpace(restoreRes.Message)
                                    ? "Failed to restore baseline Windows power plan."
                                    : restoreRes.Message;
                                result.Errors.Add($"PowerPlan: {msg}");
                                AuditLogManager.Write("AiPolicy", "AI.PowerPlan.Restore", "Failed", msg);
                            }
                            else
                            {
                                result.Actions.Add($"Restored baseline power plan ({restoreRes.ActiveSchemeName})");
                                AuditLogManager.Write("AiPolicy", "AI.PowerPlan.Restore", "Restored",
                                    $"Restored baseline Windows power plan ({restoreRes.ActiveSchemeName}).");
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.PowerPlanOk = false;
                    result.Errors.Add($"PowerPlan: {ex.Message}");
                    AuditLogManager.Write("AiPolicy", "AI.PowerPlan", "Failed", ex.Message);
                }
            }

            // 2. Workload Optimizer Policy
            try
            {
                if (wantWorkload)
                {
                    await Task.Run(() => WorkloadOptimizationEngine.Instance.Start(), ct).ConfigureAwait(false);
                    result.Actions.Add("Workload monitor active");
                    AuditLogManager.Write("AiPolicy", "AI.Workload", "Active", "Workload monitor active.");
                }
                else
                {
                    await Task.Run(() =>
                    {
                        WorkloadOptimizationEngine.Instance.Stop();
                        WorkloadOptimizationEngine.Instance.RollbackAllWorkloadSessions();
                    }, ct).ConfigureAwait(false);

                    if (hostRevertWorkloadAsync != null)
                        await hostRevertWorkloadAsync().ConfigureAwait(false);

                    result.Actions.Add("Workload monitor stopped & process states restored");
                    AuditLogManager.Write("AiPolicy", "AI.Workload", "Stopped",
                        "Stopped workload monitor and restored process states.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.WorkloadOk = false;
                result.Errors.Add($"Workload: {ex.Message}");
                AuditLogManager.Write("AiPolicy", "AI.Workload", "Failed", ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }
}
