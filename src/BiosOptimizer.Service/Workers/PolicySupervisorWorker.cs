#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Config;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Power;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BiosOptimizer.Service.Workers;

/// <summary>
/// Executes enabled AI boot policies (<c>powerPlan</c>, <c>workload</c>, <c>autoOptimize</c> arming)
/// on Windows service startup and periodically purges expired audit logs in <c>%ProgramData%\ErrorOptimizer\logs</c>.
/// </summary>
public sealed class PolicySupervisorWorker : BackgroundService
{
    private readonly ILogger<PolicySupervisorWorker> _logger;

    public PolicySupervisorWorker(ILogger<PolicySupervisorWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            await ApplyStartupPoliciesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PolicySupervisor startup pass failed");
            AuditLogManager.Write("PolicySupervisor", "StartupPolicies", "Warning", ex.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = UnifiedSettingsStore.LoadSafe();
                int purged = AuditLogManager.PurgeExpiredLogs(settings.LogRetentionDays);
                if (purged > 0)
                {
                    AuditLogManager.Write("AuditLog", "PurgeExpiredLogs", "OK",
                        $"Purged {purged} expired log file(s) older than {settings.LogRetentionDays} days.", detailedOnly: true);
                }

                await Task.Delay(TimeSpan.FromHours(6), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PolicySupervisor periodic sweep failed");
                try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task ApplyStartupPoliciesAsync(CancellationToken ct)
    {
        var settings = UnifiedSettingsStore.LoadSafe();
        WorkloadOptimizationEngine.Instance.LoadConfiguration();
        WorkloadOptimizationEngine.Instance.BuildInstalledInventory();

        var res = await AiPolicyReconciler
            .ReconcileAsync(settings, managePowerPlan: true, ct: ct)
            .ConfigureAwait(false);

        if (!res.AllSucceeded)
        {
            _logger.LogWarning("PolicySupervisor startup reconciliation finished with warnings: {Errors}", string.Join("; ", res.Errors));
        }
        else if (!settings.AiEnabled)
        {
            AuditLogManager.Write("PolicySupervisor", "MasterAI", "Paused", "Master AI Optimization is disabled; active AI policies ensured stopped.");
        }
    }
}
