#nullable enable
using System;
using System.Text.Json;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Service;

public partial class NamedPipeServer
{
    private IpcResponse GetKernelTelemetryImpl()
    {
        try
        {
            var snap = HardwareTelemetryCollector.Instance.CollectSnapshot();
            return Ok(JsonSerializer.Serialize(snap));
        }
        catch (Exception ex)
        {
            return Fail($"Kernel telemetry query failed: {ex.Message}");
        }
    }

    private IpcResponse SetTelemetrySubscriptionActiveImpl(string? payload)
    {
        try
        {
            bool active = ReadBoolPayload(payload, "active", false);
            if (active)
                HardwareTelemetryCollector.Instance.AcquireEtwSubscription();
            else
                HardwareTelemetryCollector.Instance.ReleaseEtwSubscription();

            return Ok(JsonSerializer.Serialize(new { active }));
        }
        catch (Exception ex)
        {
            return Fail($"Telemetry subscription toggle failed: {ex.Message}");
        }
    }
}
