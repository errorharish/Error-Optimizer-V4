#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Performance;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Service;

public partial class NamedPipeServer
{
    private async Task<IpcResponse> ProbePerformanceTuningAsync(CancellationToken ct)
    {
        try
        {
            var probe = await PerformanceTuningEngine.Instance.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(probe));
        }
        catch (Exception ex)
        {
            return Fail($"Performance tuning probe failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> ExecutePerformanceTuningAsync(string? payload, CancellationToken ct)
    {
        try
        {
            string target = ReadStringPayload(payload, "target") ?? "cpu";
            bool confirmed = ReadBoolPayload(payload, "confirmed", defaultVal: true);
            var res = await PerformanceTuningEngine.Instance.ExecuteTuningAsync(target, confirmed, ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(res));
        }
        catch (Exception ex)
        {
            return Fail($"Performance tuning execution failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> RollbackPerformanceTuningAsync(string? payload, CancellationToken ct)
    {
        try
        {
            string? txId = ReadStringPayload(payload, "transactionId");
            var (ok, msg) = await PerformanceTuningEngine.Instance.RollbackTuningAsync(txId, ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(new { success = ok, message = msg, transactionId = txId }));
        }
        catch (Exception ex)
        {
            return Fail($"Performance tuning rollback failed: {ex.Message}");
        }
    }
}
