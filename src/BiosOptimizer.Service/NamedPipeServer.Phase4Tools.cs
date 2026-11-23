#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Tools;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Service;

public partial class NamedPipeServer
{
    private IpcResponse GetSystemToolsCatalogImpl()
    {
        try
        {
            return Ok(JsonSerializer.Serialize(SystemToolsOrchestrator.Instance.GetToolsCatalog()));
        }
        catch (Exception ex)
        {
            return Fail($"Tools catalog inspection failed: {ex.Message}");
        }
    }

    private IpcResponse ExecuteSystemTool(string? payload, CancellationToken ct)
    {
        try
        {
            string toolId = ReadStringPayload(payload, "toolId")
                ?? ReadStringPayload(payload, "id")
                ?? (payload?.Trim() ?? string.Empty);

            bool confirmed = ReadBoolPayload(payload, "confirmed");
            var jobStatus = SystemToolsOrchestrator.Instance.StartToolJob(toolId, confirmed, runInInteractiveSession: false);
            return Ok(JsonSerializer.Serialize(jobStatus));
        }
        catch (Exception ex)
        {
            return Fail($"Tool execution failed: {ex.Message}");
        }
    }

    private IpcResponse GetSystemToolJobStatusImpl(string? payload)
    {
        try
        {
            string jobId = ReadStringPayload(payload, "jobId")
                ?? ReadStringPayload(payload, "id")
                ?? (payload?.Trim() ?? string.Empty);

            var jobStatus = SystemToolsOrchestrator.Instance.GetJobStatus(jobId);
            if (jobStatus == null)
            {
                return Fail($"Job '{jobId}' not found.");
            }
            return Ok(JsonSerializer.Serialize(jobStatus));
        }
        catch (Exception ex)
        {
            return Fail($"Job status lookup failed: {ex.Message}");
        }
    }

    private IpcResponse CancelSystemToolJobImpl(string? payload)
    {
        try
        {
            string jobId = ReadStringPayload(payload, "jobId")
                ?? ReadStringPayload(payload, "id")
                ?? (payload?.Trim() ?? string.Empty);

            bool cancelled = SystemToolsOrchestrator.Instance.CancelJob(jobId);
            return Ok(JsonSerializer.Serialize(new { success = cancelled, message = cancelled ? "Cancellation requested." : "Job not found." }));
        }
        catch (Exception ex)
        {
            return Fail($"Cancel failed: {ex.Message}");
        }
    }

    private IpcResponse GetSystemToolReportImpl()
    {
        try
        {
            var latest = SystemToolsOrchestrator.Instance.GetLatestReportSummary();
            return Ok(JsonSerializer.Serialize(new { latestReport = latest }));
        }
        catch (Exception ex)
        {
            return Fail($"Report lookup failed: {ex.Message}");
        }
    }
}
