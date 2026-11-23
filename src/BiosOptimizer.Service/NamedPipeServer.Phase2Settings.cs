#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Config;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Startup;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Service;

public partial class NamedPipeServer
{
    private IpcResponse GetUnifiedSettingsImpl()
    {
        try
        {
            var settings = UnifiedSettingsStore.LoadSafe();
            return Ok(JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to load settings: {ex.Message}");
        }
    }

    private async Task<IpcResponse> SaveUnifiedSettingsAsync(string? payload, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail("Settings payload cannot be empty.");

            var prior = UnifiedSettingsStore.LoadSafe();
            using var doc = JsonDocument.Parse(payload);
            var saved = UnifiedSettingsStore.MergeAndSave(doc.RootElement);

            // Transactionally sync Windows logon startup only if startup flags changed or startup is enabled
            if (saved.StartWithWindows != prior.StartWithWindows ||
                saved.StartMinimized != prior.StartMinimized ||
                saved.StartWithWindows)
            {
                var tx = WindowsStartupRegistrar.Instance.ConfigureStartupTransactional(
                    saved.StartWithWindows,
                    saved.StartMinimized,
                    saved.HostExecutablePath);
                if (!tx.Success)
                {
                    saved = UnifiedSettingsStore.LoadSafe();
                }
            }

            // Reconcile AI policies live immediately (disabling stops active workers & restores baseline state;
            // failures are logged cleanly without mutating saved toggle flags)
            var reconcile = await AiPolicyReconciler
                .ReconcileAsync(saved, managePowerPlan: true, ct: ct)
                .ConfigureAwait(false);

            AuditLogManager.Write("Settings", "SaveUnifiedSettings",
                reconcile.AllSucceeded ? "OK" : "Warning",
                $"Saved unified settings (ai={saved.AiEnabled}, power={saved.Policies.PowerPlan}, workload={saved.Policies.Workload}, autoOpt={saved.Policies.AutoOptimize}, startup={saved.StartWithWindows}, retention={saved.LogRetentionDays}d).",
                detailedOnly: reconcile.AllSucceeded);

            return Ok(JsonSerializer.Serialize(saved));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to save settings: {ex.Message}");
        }
    }

    private async Task<IpcResponse> ResetUnifiedSettingsAsync(CancellationToken ct)
    {
        try
        {
            var reset = UnifiedSettingsStore.ResetDefaults();
            WindowsStartupRegistrar.Instance.ConfigureStartupTransactional(
                reset.StartWithWindows,
                reset.StartMinimized,
                reset.HostExecutablePath);

            await AiPolicyReconciler
                .ReconcileAsync(reset, managePowerPlan: true, ct: ct)
                .ConfigureAwait(false);

            var confirmed = UnifiedSettingsStore.LoadSafe();
            AuditLogManager.Write("Settings", "ResetUnifiedSettings", "OK", "Restored unified defaults and reconciled AI policies.");
            return Ok(JsonSerializer.Serialize(confirmed));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to reset settings: {ex.Message}");
        }
    }

    private IpcResponse GetStartupRegistrationStatusImpl()
    {
        try
        {
            var info = WindowsStartupRegistrar.Instance.GetStartupInfo();
            string resolvedExe = WindowsStartupRegistrar.ResolveInstalledOrLocalExePath();
            return Ok(JsonSerializer.Serialize(new
            {
                entryName = info.EntryName,
                isEnabled = info.IsEnabled,
                status = info.Status.ToString(),
                displayStatus = info.DisplayStatus,
                detailMessage = info.DetailMessage,
                sourceKey = info.SourceKey,
                exePath = string.IsNullOrWhiteSpace(info.ExePath) ? resolvedExe : info.ExePath,
                arguments = info.Arguments,
                executableExists = !string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe)
            }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to inspect Windows startup status: {ex.Message}");
        }
    }

    private IpcResponse SetStartupRegistrationImpl(string? payload)
    {
        try
        {
            bool enable = ReadBoolPayload(payload, "enable", false);
            bool minimized = ReadBoolPayload(payload, "startMinimized", false);
            string? preferredExe = ReadStringPayload(payload, "exePath");

            var tx = WindowsStartupRegistrar.Instance.ConfigureStartupTransactional(enable, minimized, preferredExe);
            if (!tx.Success)
            {
                return Fail(string.IsNullOrWhiteSpace(tx.ErrorMessage)
                    ? "Failed to verify Windows startup registration change."
                    : tx.ErrorMessage);
            }

            return Ok(JsonSerializer.Serialize(new
            {
                success = tx.Success,
                isEnabled = tx.IsEnabled,
                status = tx.Info.Status.ToString(),
                displayStatus = tx.Info.DisplayStatus,
                detailMessage = tx.Info.DetailMessage,
                exePath = tx.ExePath
            }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to configure Windows startup: {ex.Message}");
        }
    }

    private IpcResponse ExportAuditLogsImpl(string? payload)
    {
        try
        {
            int maxEntries = ReadIntPayload(payload, "maxEntries", 300);
            string? targetPath = ReadStringPayload(payload, "targetPath");
            var (text, count, exportedPath) = AuditLogManager.ExportLogs(maxEntries, targetPath);
            return Ok(JsonSerializer.Serialize(new
            {
                entryCount = count,
                exportedPath,
                text
            }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to export audit logs: {ex.Message}");
        }
    }

    private IpcResponse PurgeExpiredLogsImpl(string? payload)
    {
        try
        {
            var settings = UnifiedSettingsStore.LoadSafe();
            int days = ReadIntPayload(payload, "retentionDays", settings.LogRetentionDays);
            int deleted = AuditLogManager.PurgeExpiredLogs(days);
            return Ok(JsonSerializer.Serialize(new
            {
                retentionDays = days,
                deletedFiles = deleted,
                backupsPreserved = true
            }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to purge expired logs: {ex.Message}");
        }
    }
}
