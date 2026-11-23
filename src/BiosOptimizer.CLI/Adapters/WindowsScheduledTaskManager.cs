using System.Runtime.Versioning;
using BiosOptimizer.Core.Interfaces;

namespace BiosOptimizer.CLI.Adapters;

[SupportedOSPlatform("windows")]
public class WindowsScheduledTaskManager : IScheduledTaskManager
{
    private dynamic? GetTask(string taskPath)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;

            dynamic scheduler = Activator.CreateInstance(type)!;
            scheduler.Connect();

            // Split into folder and name. Task path typically starts with "\"
            int lastSlash = taskPath.LastIndexOf('\\');
            if (lastSlash == -1) return null;

            string folderPath = taskPath.Substring(0, lastSlash);
            if (string.IsNullOrEmpty(folderPath)) folderPath = "\\";
            
            string taskName = taskPath.Substring(lastSlash + 1);

            dynamic folder = scheduler.GetFolder(folderPath);
            return folder.GetTask(taskName);
        }
        catch
        {
            return null;
        }
    }

    public ScheduledTaskState? FindTask(string taskPath)
    {
        var task = GetTask(taskPath);
        if (task == null) return null;

        return new ScheduledTaskState
        {
            TaskPath = taskPath,
            TaskName = task.Name,
            State = GetStateFromTask(task)
        };
    }

    public TaskState GetState(string taskPath)
    {
        var task = GetTask(taskPath);
        if (task == null) return TaskState.Unknown;
        return GetStateFromTask(task);
    }

    private TaskState GetStateFromTask(dynamic task)
    {
        // 1 = Disabled, 2 = Queued, 3 = Ready, 4 = Running
        if (task.State == 1) return TaskState.Disabled;
        if (task.State == 2 || task.State == 3 || task.State == 4) return TaskState.Enabled;
        return TaskState.Unknown;
    }

    public string? LastError { get; private set; }

    public bool Enable(string taskPath)
    {
        LastError = null;
        try
        {
            var task = GetTask(taskPath);
            if (task == null)
            {
                LastError = $"Scheduled task '{taskPath}' not found.";
                return false;
            }
            task.Enabled = true;
            return true;
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            uint hr = (uint)comEx.ErrorCode;
            if (hr == 0x80070005)
            {
                LastError = $"Access Denied: Administrator privileges required to enable scheduled task '{taskPath}'.";
            }
            else
            {
                LastError = $"COM error (0x{hr:X8}) enabling scheduled task '{taskPath}': {comEx.Message}";
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            LastError = $"Access Denied: Administrator privileges required to enable scheduled task '{taskPath}'.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Failed to enable scheduled task '{taskPath}': {ex.Message}";
            return false;
        }
    }

    public bool Disable(string taskPath)
    {
        LastError = null;
        try
        {
            var task = GetTask(taskPath);
            if (task == null)
            {
                LastError = $"Scheduled task '{taskPath}' not found.";
                return false;
            }
            task.Enabled = false;
            return true;
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            uint hr = (uint)comEx.ErrorCode;
            if (hr == 0x80070005)
            {
                LastError = $"Access Denied: Administrator privileges required to disable scheduled task '{taskPath}'.";
            }
            else
            {
                LastError = $"COM error (0x{hr:X8}) disabling scheduled task '{taskPath}': {comEx.Message}";
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            LastError = $"Access Denied: Administrator privileges required to disable scheduled task '{taskPath}'.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Failed to disable scheduled task '{taskPath}': {ex.Message}";
            return false;
        }
    }

    public bool Restore(string taskPath, TaskState originalState)
    {
        if (originalState == TaskState.Enabled) return Enable(taskPath);
        if (originalState == TaskState.Disabled) return Disable(taskPath);
        return false;
    }
}
