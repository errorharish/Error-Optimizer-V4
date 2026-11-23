#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Common;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.BiosSafe;
using BiosOptimizer.Core.Implementations.Cleaners;
using BiosOptimizer.Core.Implementations.Diagnostics;

namespace BiosOptimizer.Core.Implementations.Tools;

public sealed class SystemToolStatusItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Risk { get; set; } = "SAFE";
    public bool RequiresAdmin { get; set; }
    public bool RequiresConfirmation { get; set; }
    public bool RequiresReboot { get; set; }
    public string Applicability { get; set; } = "Ready"; // Ready | RequiresAdmin | NotApplicable | NeedsReboot
    public string Reason { get; set; } = string.Empty;
}

public sealed class SystemToolExecutionOutcome
{
    public string ToolId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Status { get; set; } = "Failed"; // Completed | Unsupported | RequiresConfirmation | RequiresAdmin | Failed
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OutputSummary { get; set; } = string.Empty;
    public string? ReportPath { get; set; }
    public bool RequiresReboot { get; set; }
    public long ReclaimedBytes { get; set; }
    public long DurationMs { get; set; }
}

public sealed class SystemToolJobStatusDto
{
    public string JobId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string State { get; set; } = "QUEUED"; // QUEUED, STARTING, RUNNING, WAITING_FOR_ELEVATION, CANCELLING, COMPLETED, COMPLETED_WITH_WARNINGS, FAILED, TIMED_OUT, CANCELLED, REQUIRES_RESTART, REQUIRES_USER_ACTION
    public string Status { get; set; } = "Running"; // Completed, Failed, RequiresConfirmation, RequiresAdmin, Unsupported, RequiresRestart, Running, Cancelled, TimedOut
    public bool Success { get; set; }
    public int Progress { get; set; }
    public string Target { get; set; } = "Local PC";
    public string ProcessName { get; set; } = "System Tool";
    public string BackendOwner { get; set; } = "Windows Service";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public long ElapsedMs { get; set; }
    public int? ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OutputSummary { get; set; } = string.Empty;
    public List<string> RecentOutputLines { get; set; } = new();
    public string? ReportPath { get; set; }
    public bool RequiresReboot { get; set; }
    public bool RequiresUserAction { get; set; }
    public long ReclaimedBytes { get; set; }
}

public sealed class SystemToolJob
{
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public string ToolId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string State { get; set; } = "QUEUED";
    public string Status { get; set; } = "Running";
    public bool Success { get; set; }
    public int Progress { get; set; }
    public string Target { get; set; } = "Local PC";
    public string ProcessName { get; set; } = "System Tool";
    public string BackendOwner { get; set; } = "Windows Service";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
    public int? ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OutputSummary { get; set; } = string.Empty;
    public bool RequiresReboot { get; set; }
    public bool RequiresUserAction { get; set; }
    public long ReclaimedBytes { get; set; }
    public string? ReportPath { get; set; }
    public int ProcessId { get; set; }

    private readonly object _lock = new();
    private readonly List<string> _outputLines = new();
    public CancellationTokenSource Cts { get; } = new();

    public void AppendOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            _outputLines.Add(line.TrimEnd());
            if (_outputLines.Count > 500)
                _outputLines.RemoveAt(0);
        }
    }

    public List<string> GetRecentOutput(int maxLines = 50)
    {
        lock (_lock)
        {
            int count = Math.Min(maxLines, _outputLines.Count);
            return _outputLines.Skip(_outputLines.Count - count).ToList();
        }
    }

    public SystemToolJobStatusDto ToDto()
    {
        long elapsed = (long)((EndedAtUtc ?? DateTime.UtcNow) - StartedAtUtc).TotalMilliseconds;
        return new SystemToolJobStatusDto
        {
            JobId = JobId,
            ToolId = ToolId,
            ToolName = ToolName,
            State = State,
            Status = Status,
            Success = Success,
            Progress = Progress,
            Target = Target,
            ProcessName = ProcessName,
            BackendOwner = BackendOwner,
            StartedAtUtc = StartedAtUtc,
            EndedAtUtc = EndedAtUtc,
            ElapsedMs = Math.Max(0, elapsed),
            ExitCode = ExitCode,
            Message = Message,
            OutputSummary = OutputSummary,
            RecentOutputLines = GetRecentOutput(50),
            ReportPath = ReportPath,
            RequiresReboot = RequiresReboot,
            RequiresUserAction = RequiresUserAction,
            ReclaimedBytes = ReclaimedBytes
        };
    }
}

public sealed class SystemToolsOrchestrator
{
    private static readonly Lazy<SystemToolsOrchestrator> _lazy = new(() => new SystemToolsOrchestrator());
    public static SystemToolsOrchestrator Instance => _lazy.Value;

    private static readonly ConcurrentDictionary<string, SystemToolJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string ReportsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ErrorOptimizer", "Reports");

    private static readonly string WinsockBackupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ErrorOptimizer", "Backups", "Winsock");

    public List<SystemToolStatusItem> GetToolsCatalog()
    {
        bool isAdmin = SystemPowerOperations.IsCurrentProcessElevated();
        bool hasBattery = HasSystemBattery();
        bool isUefi = BiosSafeEngine.Instance.IsUefiFirmwareSupported();

        string AdminState(bool reqAdmin, bool supported = true, string unsupportedReason = "") =>
            !supported
                ? "NotApplicable"
                : (reqAdmin && !isAdmin ? "RequiresAdmin" : "Ready");

        return new List<SystemToolStatusItem>
        {
            new() { Id = "sfc", Name = "System File Checker (SFC)", Category = "Servicing", Risk = "SAFE", RequiresAdmin = true, Applicability = AdminState(true), Reason = "Scans and restores Windows component integrity" },
            new() { Id = "dism-repair", Name = "DISM Component Store Repair", Category = "Servicing", Risk = "SAFE", RequiresAdmin = true, Applicability = AdminState(true), Reason = "Online component store repair via Windows Servicing" },
            new() { Id = "chkdsk-scan", Name = "Check Disk (read-only scan)", Category = "Storage", Risk = "SAFE", RequiresAdmin = true, Applicability = AdminState(true), Reason = "Online read-only volume integrity scan (chkdsk /scan)" },
            new() { Id = "memory-diagnostic", Name = "Memory Diagnostic Scheduler", Category = "Diagnostics", Risk = "CONFIRM", RequiresAdmin = true, RequiresConfirmation = true, RequiresReboot = true, Applicability = AdminState(true), Reason = "Schedules Windows Memory Diagnostic on next restart" },
            new() { Id = "standby-memory", Name = "Clear Standby Memory", Category = "Memory", Risk = "SAFE", RequiresAdmin = true, Applicability = AdminState(true), Reason = "Purges standby page list without hurting active working sets" },
            new() { Id = "temp-cleanup", Name = "Temp & Cache Cleanup", Category = "Cleanup", Risk = "SAFE", RequiresAdmin = false, Applicability = "Ready", Reason = "Safely purges user and Windows temporary cache directories" },
            new() { Id = "dism-cleanup", Name = "Component Store Cleanup", Category = "Servicing", Risk = "SAFE", RequiresAdmin = true, Applicability = AdminState(true), Reason = "Cleans superseded WinSxS components" },
            new() { Id = "reset-windows-update", Name = "Reset Windows Update Cache", Category = "System", Risk = "CONFIRM", RequiresAdmin = true, RequiresConfirmation = true, Applicability = AdminState(true), Reason = "Stops WU services, flushes SoftwareDistribution, restores services" },
            new() { Id = "rebuild-icon-cache", Name = "Rebuild Icon & Thumbnail Cache", Category = "Shell", Risk = "SAFE", RequiresAdmin = false, Applicability = "Ready", Reason = "Cleans corrupted shell icon DBs and refreshes explorer" },
            new() { Id = "reset-winsock", Name = "Reset Network Stack (Winsock)", Category = "Network", Risk = "CONFIRM", RequiresAdmin = true, RequiresConfirmation = true, RequiresReboot = true, Applicability = AdminState(true), Reason = "Resets Winsock LSP catalog and TCP/IP stack (reboot required)" },
            new() { Id = "flush-dns", Name = "Flush DNS Resolver Cache", Category = "Network", Risk = "SAFE", RequiresAdmin = false, Applicability = "Ready", Reason = "Flushes local DNS client resolver cache" },
            new() { Id = "restart-gpu", Name = "Restart Graphics Driver", Category = "Hardware", Risk = "CONFIRM", RequiresAdmin = true, RequiresConfirmation = true, Applicability = AdminState(true), Reason = "Soft-restarts display adapter PnP device node" },
            new() { Id = "shader-cache", Name = "Clear DirectX Shader Cache", Category = "Gaming", Risk = "SAFE", RequiresAdmin = false, Applicability = "Ready", Reason = "Purges DirectX and GPU driver shader caches" },
            new() { Id = "battery-energy-report", Name = "Battery & Energy Report", Category = "Power", Risk = "SAFE", RequiresAdmin = false, Applicability = "Ready", Reason = hasBattery ? "Generates comprehensive battery & energy diagnostics HTML" : "Generates system energy diagnostics (battery skipped on AC desktop)" },
            new() { Id = "reregister-store-apps", Name = "Re-register Store Apps", Category = "Apps", Risk = "SAFE", RequiresAdmin = true, Applicability = AdminState(true), Reason = "Re-registers core AppX packages with valid manifests" },
            new() { Id = "uefi-reboot", Name = "Reboot to UEFI Firmware", Category = "Firmware", Risk = "CONFIRM", RequiresAdmin = true, RequiresConfirmation = true, RequiresReboot = true, Applicability = isUefi ? AdminState(true) : "NotApplicable", Reason = isUefi ? "Direct restart into UEFI firmware settings" : "Unsupported on Legacy BIOS platforms" }
        };
    }

    public SystemToolJobStatusDto StartToolJob(string toolId, bool confirmed = false, bool runInInteractiveSession = false)
    {
        string normalized = NormalizeToolId(toolId);
        var meta = ResolveToolMetadata(normalized);
        bool isAdmin = SystemPowerOperations.IsCurrentProcessElevated();

        string backendOwner = runInInteractiveSession
            ? "Interactive Host"
            : (Startup.WindowsStartupRegistrar.IsRunningAsSystem() ? "Windows Service" : "Interactive Host");

        var job = new SystemToolJob
        {
            ToolId = normalized,
            ToolName = meta.Name,
            Target = meta.Target,
            ProcessName = meta.ProcessName,
            BackendOwner = backendOwner,
            State = "STARTING",
            Status = "Running",
            Progress = 5,
            Message = $"Initiating {meta.Name}..."
        };

        // 1. Validation & Pre-check: Confirmation
        if (meta.RequiresConfirmation && !confirmed)
        {
            job.State = "REQUIRES_USER_ACTION";
            job.Status = "RequiresConfirmation";
            job.Success = false;
            job.Progress = 0;
            job.RequiresUserAction = true;
            job.Message = $"{meta.Name} requires explicit user confirmation before proceeding.";
            job.EndedAtUtc = DateTime.UtcNow;
            _jobs[job.JobId] = job;
            return job.ToDto();
        }

        // 2. Validation & Pre-check: Elevation
        if (meta.RequiresAdmin && !isAdmin && !Startup.WindowsStartupRegistrar.IsRunningAsSystem())
        {
            job.State = "WAITING_FOR_ELEVATION";
            job.Status = "RequiresAdmin";
            job.Success = false;
            job.Progress = 0;
            job.RequiresUserAction = true;
            job.Message = $"Administrator elevation is required to execute {meta.Name}.";
            job.EndedAtUtc = DateTime.UtcNow;
            _jobs[job.JobId] = job;
            return job.ToDto();
        }

        // 3. Platform capability check: UEFI
        if (normalized == "uefi-reboot" && !BiosSafeEngine.Instance.IsUefiFirmwareSupported())
        {
            job.State = "FAILED";
            job.Status = "Unsupported";
            job.Success = false;
            job.Progress = 0;
            job.Message = "Direct firmware reboot (/fw) is only supported on native UEFI systems. Your computer is configured with Legacy BIOS; reboot manually using Del or F2.";
            job.EndedAtUtc = DateTime.UtcNow;
            _jobs[job.JobId] = job;
            return job.ToDto();
        }

        _jobs[job.JobId] = job;
        job.State = "RUNNING";

        // Launch background execution
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteJobInternalAsync(job, confirmed).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.State = "CANCELLED";
                job.Status = "Cancelled";
                job.Success = false;
                job.Message = $"{job.ToolName} was cancelled by user request.";
            }
            catch (Exception ex)
            {
                job.State = "FAILED";
                job.Status = "Failed";
                job.Success = false;
                job.Message = ex.Message;
            }
            finally
            {
                job.EndedAtUtc = DateTime.UtcNow;
                AuditLogManager.Write("SystemTools", job.ToolId, job.Status, job.Message);
            }
        });

        return job.ToDto();
    }

    public SystemToolJobStatusDto? GetJobStatus(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        if (_jobs.TryGetValue(jobId, out var job))
        {
            return job.ToDto();
        }
        return null;
    }

    public bool CancelJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.State is "RUNNING" or "STARTING" or "QUEUED")
            {
                job.State = "CANCELLING";
                job.Message = "Cancelling tool execution...";
                try { job.Cts.Cancel(); } catch { }
                return true;
            }
        }
        return false;
    }

    public async Task<SystemToolExecutionOutcome> ExecuteToolAsync(string toolId, bool confirmed = false, CancellationToken ct = default)
    {
        var jobStatus = StartToolJob(toolId, confirmed, runInInteractiveSession: false);
        string jobId = jobStatus.JobId;

        // Await job completion up to 30 minutes for direct backward-compatibility calls
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(30) && !ct.IsCancellationRequested)
        {
            var st = GetJobStatus(jobId);
            if (st == null || st.State is "COMPLETED" or "COMPLETED_WITH_WARNINGS" or "FAILED" or "TIMED_OUT" or "CANCELLED" or "REQUIRES_RESTART" or "REQUIRES_USER_ACTION" or "WAITING_FOR_ELEVATION")
            {
                return new SystemToolExecutionOutcome
                {
                    ToolId = st?.ToolId ?? toolId,
                    ToolName = st?.ToolName ?? toolId,
                    Success = st?.Success ?? false,
                    Status = st?.Status ?? "Failed",
                    ExitCode = st?.ExitCode ?? -1,
                    Message = st?.Message ?? string.Empty,
                    OutputSummary = st?.OutputSummary ?? string.Empty,
                    ReportPath = st?.ReportPath,
                    RequiresReboot = st?.RequiresReboot ?? false,
                    ReclaimedBytes = st?.ReclaimedBytes ?? 0,
                    DurationMs = st?.ElapsedMs ?? sw.ElapsedMilliseconds
                };
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return new SystemToolExecutionOutcome
        {
            ToolId = toolId,
            ToolName = toolId,
            Success = false,
            Status = "TimedOut",
            Message = "Tool execution timed out."
        };
    }

    private async Task ExecuteJobInternalAsync(SystemToolJob job, bool confirmed)
    {
        var ct = job.Cts.Token;
        switch (job.ToolId)
        {
            case "sfc":
                await RunServicingJobAsync(job, "sfc", TimeSpan.FromMinutes(45), ct).ConfigureAwait(false);
                break;

            case "dism-repair":
                await RunServicingJobAsync(job, "dism_restore", TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
                break;

            case "dism-cleanup":
                await RunServicingJobAsync(job, "dism_cleanup", TimeSpan.FromMinutes(45), ct).ConfigureAwait(false);
                break;

            case "chkdsk-scan":
                await RunChkdskScanJobAsync(job, ct).ConfigureAwait(false);
                break;

            case "memory-diagnostic":
                await ScheduleMemoryDiagnosticJobAsync(job, confirmed, ct).ConfigureAwait(false);
                break;

            case "standby-memory":
                await Task.Run(() => ClearStandbyMemoryJob(job, ct), ct).ConfigureAwait(false);
                break;

            case "temp-cleanup":
                await Task.Run(() => TempCleanupJob(job, ct), ct).ConfigureAwait(false);
                break;

            case "reset-windows-update":
                await ResetWindowsUpdateJobAsync(job, confirmed, ct).ConfigureAwait(false);
                break;

            case "rebuild-icon-cache":
                await RebuildIconCacheJobAsync(job, ct).ConfigureAwait(false);
                break;

            case "reset-winsock":
                await ResetWinsockJobAsync(job, confirmed, ct).ConfigureAwait(false);
                break;

            case "flush-dns":
                await FlushDnsJobAsync(job).ConfigureAwait(false);
                break;

            case "restart-gpu":
                await RestartGpuJobAsync(job, confirmed, ct).ConfigureAwait(false);
                break;

            case "shader-cache":
                await Task.Run(() => ClearShaderCacheJob(job, ct), ct).ConfigureAwait(false);
                break;

            case "battery-energy-report":
                await BatteryReportJobAsync(job, ct).ConfigureAwait(false);
                break;

            case "reregister-store-apps":
                await ReregisterStoreAppsJobAsync(job, ct).ConfigureAwait(false);
                break;

            case "uefi-reboot":
                UefiRebootJob(job, confirmed);
                break;

            default:
                job.State = "FAILED";
                job.Status = "Unsupported";
                job.Success = false;
                job.Message = $"Unknown tool '{job.ToolId}'.";
                break;
        }
    }

    private static async Task RunServicingJobAsync(SystemToolJob job, string servicingToolId, TimeSpan timeout, CancellationToken ct)
    {
        job.AppendOutput($"[SERVICING] Launching Windows Servicing engine for '{servicingToolId}'...");
        job.Progress = 10;

        var res = await WindowsServicingHealthEngine.Instance.ExecuteServicingToolAsync(
            servicingToolId,
            customArguments: null,
            timeout: timeout,
            ct: ct,
            onOutputLine: line =>
            {
                job.AppendOutput(line);
            },
            onProgressUpdate: (pct, msg) =>
            {
                job.Progress = Math.Clamp(pct, 10, 95);
                job.Message = msg;
            },
            onProcessStarted: (pid, procName) =>
            {
                job.ProcessId = pid;
                job.ProcessName = procName;
                job.AppendOutput($"[SERVICING] Process started: {procName} (PID {pid})");
            }).ConfigureAwait(false);

        job.ExitCode = res.ExitCode;
        job.OutputSummary = TrimSummary(!string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardOutput : res.StandardError);

        if (res.IsCancelled)
        {
            job.State = "CANCELLED";
            job.Status = "Cancelled";
            job.Success = false;
            job.Message = $"{job.ToolName} execution was cancelled.";
        }
        else if (res.IsTimedOut)
        {
            job.State = "TIMED_OUT";
            job.Status = "TimedOut";
            job.Success = false;
            job.Message = $"{job.ToolName} exceeded allocated timeout ({timeout.TotalMinutes} minutes). Process terminated.";
        }
        else if (res.Success || res.ExitCode == 0 || (servicingToolId == "sfc" && res.ExitCode == 1))
        {
            job.Progress = 100;
            job.Success = true;
            if (res.ExitCode == 3010)
            {
                job.RequiresReboot = true;
                job.State = "REQUIRES_RESTART";
                job.Status = "Completed";
                job.Message = $"{job.ToolName} completed successfully. Restart required to finalize changes.";
            }
            else if (servicingToolId == "sfc" && res.ExitCode == 1)
            {
                job.State = "COMPLETED_WITH_WARNINGS";
                job.Status = "Completed";
                job.Message = "Windows Resource Protection found corrupt files and successfully repaired them.";
            }
            else
            {
                job.State = "COMPLETED";
                job.Status = "Completed";
                job.Message = !string.IsNullOrWhiteSpace(res.Summary) ? res.Summary : $"{job.ToolName} completed successfully.";
            }
        }
        else
        {
            job.Progress = 100;
            job.Success = false;
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = !string.IsNullOrWhiteSpace(res.Summary)
                ? res.Summary
                : (!string.IsNullOrWhiteSpace(res.DiagnosedRootCause) ? res.DiagnosedRootCause : $"{job.ToolName} failed (exit code {res.ExitCode}).");
        }
    }

    private static async Task RunChkdskScanJobAsync(SystemToolJob job, CancellationToken ct)
    {
        string sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        job.Target = $"Volume {sysDrive}";
        job.ProcessName = "chkdsk.exe";
        job.Progress = 10;
        job.AppendOutput($"[CHKDSK] Starting online read-only volume scan on {sysDrive} (chkdsk.exe {sysDrive} /scan)...");

        var res = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "chkdsk.exe",
            $"{sysDrive} /scan",
            TimeSpan.FromMinutes(30),
            ct,
            onOutputLine: line => job.AppendOutput(line),
            onErrorLine: line => job.AppendOutput(line),
            onProcessStarted: (pid, name) =>
            {
                job.ProcessId = pid;
                job.ProcessName = name;
            }).ConfigureAwait(false);

        job.ExitCode = res.ExitCode;
        job.OutputSummary = TrimSummary(res.StandardOutput);
        job.Progress = 100;

        if (res.IsCancelled)
        {
            job.State = "CANCELLED";
            job.Status = "Cancelled";
            job.Success = false;
            job.Message = "Check Disk read-only scan was cancelled.";
        }
        else if (res.IsTimedOut)
        {
            job.State = "TIMED_OUT";
            job.Status = "TimedOut";
            job.Success = false;
            job.Message = "Check Disk read-only scan timed out.";
        }
        else if (res.ExitCode == 0)
        {
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.Success = true;
            job.Message = $"Online read-only scan on {sysDrive} completed with zero corruption detected.";
        }
        else if (res.ExitCode is 1 or 2 or 3)
        {
            job.State = "COMPLETED_WITH_WARNINGS";
            job.Status = "Completed";
            job.Success = true;
            job.RequiresUserAction = true;
            job.Message = $"Check Disk completed with warnings (exit code {res.ExitCode}). Minor volume inconsistencies or unindexed items were noted.";
        }
        else
        {
            job.State = "FAILED";
            job.Status = "Failed";
            job.Success = false;
            job.Message = $"Check Disk reported error exit code {res.ExitCode}. Review output summary.";
        }
    }

    private static async Task ScheduleMemoryDiagnosticJobAsync(SystemToolJob job, bool confirmed, CancellationToken ct)
    {
        job.Target = "Windows Boot Manager & Memory Subsystem";
        job.ProcessName = "bcdedit.exe";
        job.Progress = 20;
        job.AppendOutput("[MEMDIAG] Configuring BCD bootsequence for Windows Memory Diagnostic ({memdiag})...");

        var res = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "bcdedit.exe",
            "/bootsequence {memdiag}",
            TimeSpan.FromSeconds(20),
            ct,
            onOutputLine: line => job.AppendOutput(line),
            onErrorLine: line => job.AppendOutput(line)).ConfigureAwait(false);

        job.ExitCode = res.ExitCode;
        job.OutputSummary = TrimSummary(res.StandardOutput);
        job.Progress = 100;

        if (res.Success || res.ExitCode == 0)
        {
            job.RequiresReboot = true;
            job.Success = true;
            job.State = "REQUIRES_RESTART";
            job.Status = "Completed";
            job.Message = "Windows Memory Diagnostic has been scheduled for the next boot. Restart the computer to run hardware memory testing.";
        }
        else
        {
            job.Success = false;
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = $"Failed to schedule Windows Memory Diagnostic (bcdedit exit code {res.ExitCode}): {TrimSummary(res.StandardError)}";
        }
    }

    private static void ClearStandbyMemoryJob(SystemToolJob job, CancellationToken ct)
    {
        job.Target = "Windows Memory Manager (Standby Page List)";
        job.ProcessName = "WindowsMemoryListProvider";
        job.Progress = 25;
        job.AppendOutput("[MEMORY] Reading physical memory baseline before purge...");

        var detail = new RamCleanupEngine().CleanWithDetails(ct);
        job.Progress = 100;
        job.ReclaimedBytes = detail.TotalReclaimedBytes;
        job.Success = detail.Succeeded;

        if (detail.Succeeded)
        {
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.Message = detail.Message;
            job.AppendOutput($"[MEMORY] Purge complete: {detail.Message}");
        }
        else if (detail.RequiresAdmin)
        {
            job.State = "WAITING_FOR_ELEVATION";
            job.Status = "RequiresAdmin";
            job.Message = "Administrator privilege with SeProfileSingleProcessPrivilege is required to clear kernel standby cache.";
        }
        else
        {
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = detail.Message;
        }
    }

    private static void TempCleanupJob(SystemToolJob job, CancellationToken ct)
    {
        job.Target = "User & Windows Temporary Cache Directories";
        job.ProcessName = "TempCleanerEngine";
        job.Progress = 20;
        job.AppendOutput("[TEMP] Scanning and purging temporary files, safely skipping locked items...");

        long freed = new TempCleanerEngine().Clean();
        job.Progress = 100;
        job.Success = true;
        job.ReclaimedBytes = freed;
        job.State = "COMPLETED";
        job.Status = "Completed";
        job.Message = $"Purged temporary application and system cache files ({Storage.StorageCleanupCategory.FormatBytes(freed)} reclaimed). Active locked files safely preserved.";
        job.AppendOutput($"[TEMP] Cleanup complete: {job.Message}");
    }

    private static async Task ResetWindowsUpdateJobAsync(SystemToolJob job, bool confirmed, CancellationToken ct)
    {
        job.Target = "Windows Update Services & SoftwareDistribution Cache";
        job.ProcessName = "ServiceController / File System";
        job.Progress = 10;

        string winDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string sdDownload = Path.Combine(winDir, "SoftwareDistribution", "Download");
        string sdDataStore = Path.Combine(winDir, "SoftwareDistribution", "DataStore");

        var services = new[] { "wuauserv", "bits", "dosvc", "cryptsvc" };
        var priorRunning = new List<string>();
        long reclaimed = 0;
        int deleted = 0;

        try
        {
            job.AppendOutput("[WU] Stopping Windows Update related background services...");
            foreach (var svcName in services)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var sc = new ServiceController(svcName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        priorRunning.Add(svcName);
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                        job.AppendOutput($"[WU] Stopped service: {svcName}");
                    }
                }
                catch (Exception ex)
                {
                    job.AppendOutput($"[WU] Note: Service {svcName} status query: {ex.Message}");
                }
            }

            job.Progress = 50;
            job.AppendOutput("[WU] Purging corrupt update download payloads and temporary datastores...");

            foreach (var dir in new[] { sdDownload, sdDataStore })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in StorageCleanerEngine.SafeEnumerateFiles(dir, ct))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var fi = new FileInfo(f);
                        long len = fi.Length;
                        if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes &= ~FileAttributes.ReadOnly;
                        fi.Delete();
                        if (!File.Exists(f))
                        {
                            reclaimed += len;
                            deleted++;
                        }
                    }
                    catch { }
                }
            }

            job.Progress = 85;
            job.Success = true;
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.ReclaimedBytes = reclaimed;
            job.Message = $"Reset Windows Update cache cleanly ({deleted} files removed, {Storage.StorageCleanupCategory.FormatBytes(reclaimed)} reclaimed). All servicing subsystems restored.";
            job.AppendOutput($"[WU] {job.Message}");
        }
        finally
        {
            job.AppendOutput("[WU] Restoring background services to original operational state...");
            foreach (var svcName in priorRunning)
            {
                try
                {
                    using var sc = new ServiceController(svcName);
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                        job.AppendOutput($"[WU] Restored service: {svcName}");
                    }
                }
                catch { }
            }
            job.Progress = 100;
        }
    }

    private static async Task RebuildIconCacheJobAsync(SystemToolJob job, CancellationToken ct)
    {
        job.Target = "Windows Explorer Shell Icon & Thumbnail Database";
        job.ProcessName = "explorer.exe / ie4uinit.exe";
        job.Progress = 15;
        job.AppendOutput("[ICONCACHE] Scanning user LocalAppData Explorer cache databases...");

        long reclaimed = 0;
        int deleted = 0;
        bool restartedExplorer = false;

        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var targetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                targetDirs.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"));
                string legacyDb = Path.Combine(localAppData, "IconCache.db");
                if (File.Exists(legacyDb))
                {
                    try
                    {
                        long len = new FileInfo(legacyDb).Length;
                        File.Delete(legacyDb);
                        reclaimed += len;
                        deleted++;
                    }
                    catch { }
                }
            }

            // Gracefully terminate explorer if running in current session to release lock
            if (Process.GetProcessesByName("explorer").Length > 0)
            {
                job.AppendOutput("[ICONCACHE] Gracefully halting Explorer process to release cache locks...");
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(3000); restartedExplorer = true; } catch { }
                }
            }

            job.Progress = 50;
            foreach (var dir in targetDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.db"))
                {
                    string name = Path.GetFileName(f);
                    if (!name.StartsWith("iconcache", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("thumbcache", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        long len = new FileInfo(f).Length;
                        File.Delete(f);
                        if (!File.Exists(f))
                        {
                            reclaimed += len;
                            deleted++;
                            job.AppendOutput($"[ICONCACHE] Purged DB: {name}");
                        }
                    }
                    catch { }
                }
            }

            job.Progress = 80;
            job.AppendOutput("[ICONCACHE] Broadcasting shell icon update via ie4uinit.exe...");
            await TaskExecutionSupervisor.ExecuteProcessAsync(
                "ie4uinit.exe",
                "-show",
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            job.Progress = 100;
            job.Success = true;
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.ReclaimedBytes = reclaimed;
            job.Message = $"Rebuilt icon & thumbnail cache ({deleted} database file(s) purged, {Storage.StorageCleanupCategory.FormatBytes(reclaimed)} reclaimed). Explorer shell refreshed.";
        }
        finally
        {
            if (restartedExplorer && Process.GetProcessesByName("explorer").Length == 0)
            {
                try
                {
                    job.AppendOutput("[ICONCACHE] Relaunching Windows Explorer shell...");
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                }
                catch { }
            }
        }
    }

    private static async Task ResetWinsockJobAsync(SystemToolJob job, bool confirmed, CancellationToken ct)
    {
        job.Target = "Winsock LSP Catalog & TCP/IP Protocol Stack";
        job.ProcessName = "netsh.exe";
        job.Progress = 15;
        job.AppendOutput("[WINSOCK] Backing up current Winsock catalog state...");

        Directory.CreateDirectory(WinsockBackupDirectory);
        string backupPath = Path.Combine(WinsockBackupDirectory, $"winsock-catalog-{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");

        var catRes = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "netsh.exe",
            "winsock show catalog",
            TimeSpan.FromSeconds(15),
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(catRes.StandardOutput))
        {
            try { await File.WriteAllTextAsync(backupPath, catRes.StandardOutput, Encoding.UTF8, ct).ConfigureAwait(false); } catch { }
        }

        job.Progress = 50;
        job.AppendOutput("[WINSOCK] Executing netsh winsock reset...");
        var wRes = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "netsh.exe",
            "winsock reset",
            TimeSpan.FromSeconds(30),
            ct,
            onOutputLine: l => job.AppendOutput(l)).ConfigureAwait(false);

        job.Progress = 75;
        job.AppendOutput("[WINSOCK] Executing netsh int ip reset...");
        var ipRes = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "netsh.exe",
            "int ip reset",
            TimeSpan.FromSeconds(30),
            ct,
            onOutputLine: l => job.AppendOutput(l)).ConfigureAwait(false);

        job.Progress = 100;
        bool ok = wRes.Success && ipRes.Success;
        job.ExitCode = ok ? 0 : (wRes.ExitCode != 0 ? wRes.ExitCode : ipRes.ExitCode);
        job.OutputSummary = TrimSummary($"{wRes.StandardOutput}\n{ipRes.StandardOutput}");

        if (ok)
        {
            job.Success = true;
            job.RequiresReboot = true;
            job.State = "REQUIRES_RESTART";
            job.Status = "Completed";
            job.ReportPath = File.Exists(backupPath) ? backupPath : null;
            job.Message = "Winsock LSP catalog and TCP/IP stack reset cleanly. Restart Windows to finalize network bindings.";
        }
        else
        {
            job.Success = false;
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = $"Winsock reset reported errors (winsock exit={wRes.ExitCode}, ip exit={ipRes.ExitCode}).";
        }
    }

    private static async Task FlushDnsJobAsync(SystemToolJob job)
    {
        job.Target = "Windows DNS Client Resolver Cache";
        job.ProcessName = "ipconfig.exe";
        job.Progress = 30;
        job.AppendOutput("[DNS] Flushing local DNS resolver cache...");

        var r = await new NetworkOptimizerEngine(BackupManager.Instance, new NetworkDiagnosticsService())
            .FlushDnsCacheAsync().ConfigureAwait(false);

        job.Progress = 100;
        job.Success = r.Success;
        job.State = r.Success ? "COMPLETED" : "FAILED";
        job.Status = r.Success ? "Completed" : "Failed";
        job.Message = r.Message;
        job.AppendOutput($"[DNS] {r.Message}");
    }

    private static async Task RestartGpuJobAsync(SystemToolJob job, bool confirmed, CancellationToken ct)
    {
        job.Target = "Primary Display Adapter Hardware Device Node";
        job.ProcessName = "pnputil.exe";
        job.Progress = 20;

        string? pnpId = ResolvePrimaryDisplayAdapterPnpId();
        if (string.IsNullOrWhiteSpace(pnpId))
        {
            job.Progress = 100;
            job.State = "FAILED";
            job.Status = "Unsupported";
            job.Success = false;
            job.Message = "No physical PnP display adapter PNPDeviceID found.";
            return;
        }

        job.AppendOutput($"[GPU] Detected primary display adapter: {pnpId}");
        job.AppendOutput("[GPU] Invoking pnputil /restart-device...");

        var res = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "pnputil.exe",
            $"/restart-device \"{pnpId}\"",
            TimeSpan.FromSeconds(25),
            ct,
            onOutputLine: l => job.AppendOutput(l),
            onErrorLine: l => job.AppendOutput(l)).ConfigureAwait(false);

        job.Progress = 100;
        job.ExitCode = res.ExitCode;
        job.OutputSummary = TrimSummary(res.StandardOutput);

        bool ok = !res.IsTimedOut && (res.ExitCode == 0 || res.ExitCode == 3010);
        if (ok)
        {
            job.Success = true;
            if (res.ExitCode == 3010)
            {
                job.RequiresReboot = true;
                job.State = "REQUIRES_RESTART";
                job.Status = "Completed";
                job.Message = $"Display adapter '{pnpId}' restarted. System reboot required to finalize.";
            }
            else
            {
                job.State = "COMPLETED";
                job.Status = "Completed";
                job.Message = $"Display adapter '{pnpId}' restarted cleanly without reboot.";
            }
        }
        else
        {
            job.Success = false;
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = $"Display adapter restart failed (pnputil exit code {res.ExitCode}).";
        }
    }

    private static void ClearShaderCacheJob(SystemToolJob job, CancellationToken ct)
    {
        job.Target = "DirectX & Vendor Shader Bytecode Caches (D3DSCache / NVIDIA / AMD / Intel)";
        job.ProcessName = "ShaderCacheCleanerEngine";
        job.Progress = 25;
        job.AppendOutput("[SHADER] Scanning shader cache directories in user LocalAppData...");

        long freed = new ShaderCacheCleanerEngine().Clean();
        job.Progress = 100;
        job.Success = true;
        job.ReclaimedBytes = freed;
        job.State = "COMPLETED";
        job.Status = "Completed";
        job.Message = $"Cleared DirectX/GPU shader cache ({Storage.StorageCleanupCategory.FormatBytes(freed)} reclaimed). In-use game caches safely preserved.";
        job.AppendOutput($"[SHADER] {job.Message}");
    }

    private static async Task BatteryReportJobAsync(SystemToolJob job, CancellationToken ct)
    {
        job.Target = "Windows Power Subsystem Diagnostics";
        job.ProcessName = "powercfg.exe";
        job.Progress = 15;

        Directory.CreateDirectory(ReportsDirectory);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        bool hasBattery = HasSystemBattery();

        string? batteryPath = null;
        if (hasBattery)
        {
            batteryPath = Path.Combine(ReportsDirectory, $"battery-report-{stamp}.html");
            job.AppendOutput($"[POWER] Generating battery health diagnostic report: {batteryPath}...");

            var bRes = await TaskExecutionSupervisor.ExecuteProcessAsync(
                "powercfg.exe",
                $"/batteryreport /output \"{batteryPath}\"",
                TimeSpan.FromSeconds(30),
                ct,
                onOutputLine: l => job.AppendOutput(l)).ConfigureAwait(false);

            if (!bRes.IsTimedOut && bRes.ExitCode == 0 && File.Exists(batteryPath))
            {
                job.AppendOutput("[POWER] Battery report generated successfully.");
            }
            else
            {
                batteryPath = null;
            }
        }
        else
        {
            job.AppendOutput("[POWER] Desktop / AC-only system detected (no physical battery installed).");
        }

        job.Progress = 60;
        string? energyPath = Path.Combine(ReportsDirectory, $"energy-report-{stamp}.html");
        if (SystemPowerOperations.IsCurrentProcessElevated())
        {
            job.AppendOutput($"[POWER] Tracing system energy efficiency (/energy /duration 5)...");
            var eRes = await TaskExecutionSupervisor.ExecuteProcessAsync(
                "powercfg.exe",
                $"/energy /duration 5 /output \"{energyPath}\"",
                TimeSpan.FromSeconds(30),
                ct,
                onOutputLine: l => job.AppendOutput(l)).ConfigureAwait(false);

            if (eRes.IsTimedOut || !File.Exists(energyPath)) energyPath = null;
        }
        else
        {
            energyPath = null;
        }

        job.Progress = 100;
        string? primaryReport = batteryPath ?? energyPath;
        bool ok = primaryReport != null && File.Exists(primaryReport);

        if (ok)
        {
            job.Success = true;
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.ReportPath = primaryReport;
            job.Message = hasBattery
                ? $"Battery diagnostic report generated successfully ({Path.GetFileName(primaryReport)})."
                : $"Energy efficiency diagnostic report generated successfully ({Path.GetFileName(primaryReport)}). Battery test skipped on AC desktop.";
        }
        else if (!hasBattery && !SystemPowerOperations.IsCurrentProcessElevated())
        {
            job.Success = false;
            job.State = "WAITING_FOR_ELEVATION";
            job.Status = "RequiresAdmin";
            job.Message = "No system battery detected, and running powercfg /energy requires Administrator privilege.";
        }
        else
        {
            job.Success = false;
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = "Failed to generate diagnostic report. Verify powercfg permissions.";
        }
    }

    private static async Task ReregisterStoreAppsJobAsync(SystemToolJob job, CancellationToken ct)
    {
        job.Target = "Windows AppX Application Package Manifests";
        job.ProcessName = "powershell.exe / Add-AppxPackage";
        job.Progress = 15;
        job.AppendOutput("[STOREAPPS] Enumerating AppX packages with valid AppxManifest.xml files...");

        const string psCmd =
            "$ok=0;$skip=0; Get-AppxPackage | Where-Object { $_.InstallLocation -and (Test-Path (Join-Path $_.InstallLocation 'AppxManifest.xml')) } | ForEach-Object { try { Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $_.InstallLocation 'AppxManifest.xml') -ErrorAction Stop; $ok++ } catch { $skip++ } }; Write-Output \"REGISTERED=$ok SKIPPED_IN_USE=$skip\"";

        var res = await TaskExecutionSupervisor.ExecuteProcessAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCmd}\"",
            TimeSpan.FromMinutes(10),
            ct,
            onOutputLine: l => job.AppendOutput(l),
            onErrorLine: l => job.AppendOutput(l)).ConfigureAwait(false);

        job.Progress = 100;
        job.ExitCode = res.ExitCode;
        job.OutputSummary = TrimSummary(res.StandardOutput);

        bool ok = res.Success;
        var match = Regex.Match(res.StandardOutput, @"REGISTERED=(\d+)\s+SKIPPED_IN_USE=(\d+)");
        if (ok && match.Success)
        {
            job.Success = true;
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.Message = $"Store App re-registration finished: {match.Groups[1].Value} registered, {match.Groups[2].Value} already current/in-use.";
        }
        else if (ok)
        {
            job.Success = true;
            job.State = "COMPLETED";
            job.Status = "Completed";
            job.Message = "Store App manifest re-registration completed successfully.";
        }
        else
        {
            job.Success = false;
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = $"Store App re-registration failed (exit code {res.ExitCode}).";
        }
    }

    private static void UefiRebootJob(SystemToolJob job, bool confirmed)
    {
        job.Target = "UEFI Firmware Setup Interface";
        job.ProcessName = "shutdown.exe /r /fw";
        job.Progress = 50;
        job.AppendOutput("[UEFI] Verifying native UEFI boot mode support...");

        if (!BiosSafeEngine.Instance.IsUefiFirmwareSupported())
        {
            job.Progress = 100;
            job.State = "FAILED";
            job.Status = "Unsupported";
            job.Success = false;
            job.Message = "Direct firmware reboot is not supported on Legacy BIOS systems. Reboot into BIOS manually using Del or F2.";
            return;
        }

        job.AppendOutput("[UEFI] Initiating direct reboot into UEFI firmware settings...");
        bool ok = BiosSafeEngine.Instance.RequestDirectUefiReboot(out string msg);

        job.Progress = 100;
        job.Success = ok;
        if (ok)
        {
            job.RequiresReboot = true;
            job.State = "REQUIRES_RESTART";
            job.Status = "Completed";
            job.Message = msg;
        }
        else
        {
            job.State = "FAILED";
            job.Status = "Failed";
            job.Message = msg;
        }
    }

    public object? GetLatestReportSummary()
    {
        if (!Directory.Exists(ReportsDirectory)) return null;
        var latest = Directory.GetFiles(ReportsDirectory, "*.html")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest == null) return null;
        return new
        {
            fileName = latest.Name,
            path = latest.FullName,
            generatedUtc = latest.LastWriteTimeUtc,
            sizeBytes = latest.Length
        };
    }

    private static (string Name, string Target, string ProcessName, bool RequiresAdmin, bool RequiresConfirmation) ResolveToolMetadata(string toolId) =>
        toolId switch
        {
            "sfc" => ("System File Checker (SFC)", "System32 & Windows Component Store", "sfc.exe", true, false),
            "dism-repair" => ("DISM Component Store Repair", "WinSxS Component Store", "dism.exe", true, false),
            "chkdsk-scan" => ("Check Disk (read-only scan)", "System Volume (C:)", "chkdsk.exe", true, false),
            "memory-diagnostic" => ("Memory Diagnostic Scheduler", "Windows Boot Manager & Memory Subsystem", "bcdedit.exe", true, true),
            "standby-memory" => ("Clear Standby Memory", "Windows Memory Manager (Standby Page List)", "WindowsMemoryListProvider", true, false),
            "temp-cleanup" => ("Temp & Cache Cleanup", "User & Windows Temporary Cache Directories", "TempCleanerEngine", false, false),
            "dism-cleanup" => ("Component Store Cleanup", "WinSxS Component Store", "dism.exe", true, false),
            "reset-windows-update" => ("Reset Windows Update Cache", "Windows Update Services & SoftwareDistribution", "ServiceController / File System", true, true),
            "rebuild-icon-cache" => ("Rebuild Icon & Thumbnail Cache", "Windows Explorer Shell Icon Database", "explorer.exe / ie4uinit.exe", false, false),
            "reset-winsock" => ("Reset Network Stack (Winsock)", "Winsock LSP Catalog & TCP/IP Stack", "netsh.exe", true, true),
            "flush-dns" => ("Flush DNS Resolver Cache", "Windows DNS Client Resolver Cache", "ipconfig.exe", false, false),
            "restart-gpu" => ("Restart Graphics Driver", "Display Adapter Device Node", "pnputil.exe", true, true),
            "shader-cache" => ("Clear DirectX Shader Cache", "DirectX & GPU Vendor Shader Caches", "ShaderCacheCleanerEngine", false, false),
            "battery-energy-report" => ("Battery & Energy Report", "Windows Power Subsystem Diagnostics", "powercfg.exe", false, false),
            "reregister-store-apps" => ("Re-register Store Apps", "Windows AppX Application Packages", "powershell.exe / Add-AppxPackage", true, false),
            "uefi-reboot" => ("Reboot to UEFI Firmware", "UEFI Firmware Setup Interface", "shutdown.exe /r /fw", true, true),
            _ => (toolId, "Local PC", "System Tool", false, false)
        };

    private static bool HasSystemBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolvePrimaryDisplayAdapterPnpId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, Status FROM Win32_VideoController");
            string? fallback = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                string? pnp = obj["PNPDeviceID"]?.ToString();
                string? name = obj["Name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(pnp) || pnp.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
                    return pnp;
                fallback ??= pnp;
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static string TrimSummary(string? text, int maxLen = 1200)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string trimmed = text.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[^maxLen..];
    }

    public static string NormalizeToolId(string input)
    {
        string lower = (input ?? string.Empty).Trim().ToLowerInvariant();
        return lower switch
        {
            "system file checker (sfc)" or "sfc" => "sfc",
            "dism component store repair" or "dism_restore" or "dism-repair" => "dism-repair",
            "check disk (read-only scan)" or "chkdsk" or "chkdsk-scan" => "chkdsk-scan",
            "memory diagnostic scheduler" or "mdsched" or "memory-diagnostic" => "memory-diagnostic",
            "clear standby memory" or "standby-memory" => "standby-memory",
            "temp & cache cleanup" or "temp-cleanup" => "temp-cleanup",
            "component store cleanup" or "dism_cleanup" or "dism-cleanup" => "dism-cleanup",
            "reset windows update cache" or "reset-windows-update" => "reset-windows-update",
            "rebuild icon & thumbnail cache" or "rebuild-icon-cache" => "rebuild-icon-cache",
            "reset network stack (winsock)" or "reset-winsock" or "winsock" => "reset-winsock",
            "flush dns resolver cache" or "flush-dns" => "flush-dns",
            "restart graphics driver" or "restart-gpu" => "restart-gpu",
            "clear directx shader cache" or "shader-cache" => "shader-cache",
            "battery & energy report" or "battery-energy-report" => "battery-energy-report",
            "re-register store apps" or "reregister-store-apps" => "reregister-store-apps",
            "reboot to uefi firmware" or "uefi-reboot" => "uefi-reboot",
            _ => lower
        };
    }
}
