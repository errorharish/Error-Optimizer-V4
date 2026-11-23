#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Automation;
using BiosOptimizer.Core.Implementations.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BiosOptimizer.Service.Workers;

/// <summary>
/// Headless background maintenance scheduler executing Auto Optimize jobs independently of UI lifetime.
/// Honors <c>startupPolicy</c> (<c>after-delay</c> = 2 min), custom 1–1440 minute intervals, pause/resume,
/// and <c>AI_POLICIES.autoOptimize</c> resume-after-boot setting.
/// </summary>
public sealed class AutoOptimizeWorker : BackgroundService
{
    private readonly ILogger<AutoOptimizeWorker> _logger;

    public AutoOptimizeWorker(ILogger<AutoOptimizeWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var initialCfg = AutoOptimizeEngine.Instance.LoadConfig();
            var settings = UnifiedSettingsStore.LoadSafe();
            var startupEval = AutoOptimizePolicyEvaluator.Evaluate(settings, initialCfg);

            if (startupEval.IsStartupEligible)
            {
                if (!startupEval.DelayStartupByInterval)
                {
                    AutoOptimizeEngine.Instance.SetNextRunUtc(DateTime.UtcNow.AddMinutes(2));
                    await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

                    var postCfg = AutoOptimizeEngine.Instance.LoadConfig();
                    var postSettings = UnifiedSettingsStore.LoadSafe();
                    var postEval = AutoOptimizePolicyEvaluator.Evaluate(postSettings, postCfg);
                    if (postEval.IsStartupEligible)
                    {
                        await AutoOptimizeEngine.Instance
                            .ExecuteAsync("Windows startup", postEval.SelectedTasks, externalCt: stoppingToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    AutoOptimizeEngine.Instance.SetNextRunUtc(DateTime.UtcNow.AddMinutes(initialCfg.IntervalMin));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoOptimizeWorker startup check failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);

                var state = AutoOptimizeEngine.Instance.GetStateSnapshot();
                var settings = UnifiedSettingsStore.LoadSafe();
                var eval = AutoOptimizePolicyEvaluator.Evaluate(settings, state.Config, state.NextRunUtc);

                if (!eval.ShouldRunScheduledTick)
                    continue;

                await AutoOptimizeEngine.Instance
                    .ExecuteAsync("Schedule", eval.SelectedTasks, externalCt: stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AutoOptimizeWorker tick failed");
            }
        }
    }
}
