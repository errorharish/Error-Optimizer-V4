#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Automation;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Service;

public partial class NamedPipeServer
{
    private IpcResponse GetAutoOptimizeStateImpl()
    {
        try
        {
            return Ok(JsonSerializer.Serialize(AutoOptimizeEngine.Instance.GetStateSnapshot()));
        }
        catch (Exception ex)
        {
            return Fail($"Auto Optimize state read failed: {ex.Message}");
        }
    }

    private IpcResponse SaveAutoOptimizeConfigImpl(string? payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail("Auto Optimize config payload cannot be empty.");

            var cfg = JsonSerializer.Deserialize<AutoOptimizeConfig>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg == null)
                return Fail("Invalid Auto Optimize configuration JSON.");

            var saved = AutoOptimizeEngine.Instance.SaveConfig(cfg);
            if (saved.RunOnWindowsStartup && saved.Enabled)
            {
                var us = BiosOptimizer.Core.Implementations.Config.UnifiedSettingsStore.LoadSafe();
                BiosOptimizer.Core.Implementations.Startup.WindowsStartupRegistrar.Instance.ConfigureStartup(true, us.StartMinimized);
            }
            return Ok(JsonSerializer.Serialize(AutoOptimizeEngine.Instance.GetStateSnapshot()));
        }
        catch (Exception ex)
        {
            return Fail($"Auto Optimize save failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> TriggerAutoOptimizeNowAsync(string? payload, CancellationToken ct)
    {
        try
        {
            string trigger = ReadStringPayload(payload, "trigger") ?? "Manual";
            bool recycleConfirmed = ReadBoolPayload(payload, "recycleConfirmed");
            IReadOnlyList<string>? overrideTasks = null;
            var arr = ExtractStringArrayPayload(payload, "tasks");
            if (arr.Count > 0) overrideTasks = arr;

            var entry = await AutoOptimizeEngine.Instance.ExecuteAsync(trigger, overrideTasks, recycleConfirmed, ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(new { entry, state = AutoOptimizeEngine.Instance.GetStateSnapshot() }));
        }
        catch (Exception ex)
        {
            return Fail($"Auto Optimize execution failed: {ex.Message}");
        }
    }

    private IpcResponse CancelAutoOptimizeRunImpl()
    {
        bool cancelled = AutoOptimizeEngine.Instance.CancelActiveRun();
        return Ok(JsonSerializer.Serialize(new { cancelled, state = AutoOptimizeEngine.Instance.GetStateSnapshot() }));
    }

    private IpcResponse SetAutoOptimizePausedImpl(string? payload)
    {
        try
        {
            bool paused = ReadBoolPayload(payload, "paused");
            var cfg = AutoOptimizeEngine.Instance.LoadConfig();
            cfg.Paused = paused;
            AutoOptimizeEngine.Instance.SaveConfig(cfg);
            return Ok(JsonSerializer.Serialize(AutoOptimizeEngine.Instance.GetStateSnapshot()));
        }
        catch (Exception ex)
        {
            return Fail($"Pause update failed: {ex.Message}");
        }
    }

    private IpcResponse ClearAutoOptimizeHistoryImpl()
    {
        AutoOptimizeEngine.Instance.ClearHistory();
        return Ok(JsonSerializer.Serialize(AutoOptimizeEngine.Instance.GetStateSnapshot()));
    }

    private async Task<IpcResponse> ScanAutoOptimizeTasksAsync(CancellationToken ct)
    {
        try
        {
            var tasks = await AutoOptimizeEngine.Instance.ScanTasksAsync(ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(tasks));
        }
        catch (Exception ex)
        {
            return Fail($"Auto Optimize scan failed: {ex.Message}");
        }
    }
}
