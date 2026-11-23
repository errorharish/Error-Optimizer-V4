using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.ActionHandlers;

public class DisableScheduledTaskHandler : IActionHandler
{
    private readonly IScheduledTaskManager _taskManager;

    public DisableScheduledTaskHandler(IScheduledTaskManager taskManager)
    {
        _taskManager = taskManager;
    }

    public string ActionName => "DisableScheduledTask";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        var task = _taskManager.FindTask(entry.Target);
        return task != null ? TargetState.Ready : TargetState.NotAvailable;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var result = new OptimizationResult { ItemId = entry.Id, DisplayName = entry.DisplayName };
        var state = _taskManager.GetState(entry.Target);

        if (state == TaskState.Disabled)
        {
            result.Status = ResultStatus.AlreadyOptimized;
            result.Message = "Task is already disabled.";
            return result;
        }

        // Capture before state into Universal Backup Manager and verify SHA-256 integrity before mutating
        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureTargetedGenericTweak(
            entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual",
            $"task.{entry.Target}".ToLowerInvariant().Replace('\\', '.'),
            $"Scheduled Task: {entry.Target}",
            "Services",
            "ScheduledTask",
            state.ToString(),
            "Disabled",
            "ScheduledTaskRestore",
            true,
            "SAFE",
            targetKey: entry.Target
        );
        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
        {
            result.Status = ResultStatus.Failed;
            result.Message = bkErr;
            return result;
        }

        bool success = _taskManager.Disable(entry.Target);
        if (!success)
        {
            string err = !string.IsNullOrWhiteSpace(_taskManager.LastError)
                ? _taskManager.LastError
                : "Failed to disable task.";
            result.Status = (err.Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
                             err.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
                             err.Contains("privilege", StringComparison.OrdinalIgnoreCase))
                ? ResultStatus.Blocked
                : ResultStatus.Failed;
            result.Message = err;
            return result;
        }

        if (_taskManager.GetState(entry.Target) != TaskState.Disabled)
        {
            result.Status = ResultStatus.Failed;
            result.Message = "Verification failed: Task state did not transition to Disabled after execution.";
            return result;
        }

        result.Status = ResultStatus.Success;
        result.Message = "Task disabled successfully.";
        return result;
    }

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        return _taskManager.GetState(entry.Target).ToString();
    }
}
