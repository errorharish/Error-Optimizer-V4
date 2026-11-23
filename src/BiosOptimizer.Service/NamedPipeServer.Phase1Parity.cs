#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.BiosSafe;
using BiosOptimizer.Core.Implementations.Debloat;
using BiosOptimizer.Core.Implementations.RegistryValues;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Extensions.DependencyInjection;
namespace BiosOptimizer.Service;

public partial class NamedPipeServer
{
    private IBackupManager ResolveBackupManager() =>
        _services.GetService<IBackupManager>() ?? BackupManager.Instance;

    private IpcResponse GetCapabilitiesImpl()
    {
        try
        {
            var caps = new
            {
                serviceName = "BiosOptimizer.Service",
                protocolVersion = "v2",
                sessionId = _sessionId,
                isElevated = SystemPowerOperations.IsCurrentProcessElevated(),
                timestampUtc = DateTime.UtcNow,
                capabilities = new[]
                {
                    "OneClickOptimization",
                    "SystemHealth",
                    "TierEngine",
                    "InputOptimizer",
                    "RegistryTweaks",
                    "RamRegistryValues",
                    "GpuRegistryValues",
                    "StartupManager",
                    "ServiceManager",
                    "NetworkOptimizer",
                    "StorageCleaner",
                    "ProcessReduction",
                    "PowerPlans",
                    "DebloatBlocklist",
                    "BiosSafeUefi",
                    "BackupAndRestore",
                    "SystemRestorePoints",
                    "ServicingHealth",
                    "WorkloadOptimizer",
                    "V4MaxPerformance",
                }
            };
            return Ok(JsonSerializer.Serialize(caps));
        }
        catch (Exception ex)
        {
            return Fail($"GetCapabilities failed: {ex.Message}");
        }
    }

    private IpcResponse GetBackupsReal(string? payload = null)
    {
        try
        {
            int offset = 0;
            int limit = 150;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("offset", out var oProp) && oProp.TryGetInt32(out int oVal)) offset = Math.Max(0, oVal);
                        if (doc.RootElement.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out int lVal)) limit = Math.Clamp(lVal, 1, 1000);
                    }
                }
                catch { }
            }

            var bm = ResolveBackupManager();
            var transactions = bm.GetTransactionsPaginated(offset, limit, out int totalCount);
            var profileGroups = bm.GetProfileBackupGroups(includeTransactions: false);
            var stats = bm.GetSummaryStats();
            var v4Backups = BackupStore.List();
            var v4Transactions = new V4RollbackEngine().GetTransactions();
            bool hasMore = (offset + transactions.Count) < totalCount;
            return Ok(JsonSerializer.Serialize(new { transactions, totalCount, offset, limit, hasMore, profileGroups, stats, v4Backups, v4Transactions }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to list backups: {ex.Message}");
        }
    }

    private IpcResponse CreateApplicationBackupImpl(string? payload)
    {
        try
        {
            string name = string.Empty;
            string? notes = null;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (payload.TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                        name = nEl.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("notes", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
                        notes = noteEl.GetString();
                }
                else
                {
                    name = payload.Trim();
                }
            }

            var bm = ResolveBackupManager();
            var created = bm.CreateApplicationSnapshot(name, notes);
            bool savedOk = !string.Equals(created.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase);
            return savedOk
                ? Ok(JsonSerializer.Serialize(new { success = true, transaction = created, message = $"Application backup '{created.OptimizationName}' created and hashed." }))
                : Fail("Failed to persist application backup snapshot.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to create application backup: {ex.Message}");
        }
    }

    private IpcResponse ValidateBackupIntegrityImpl(string? payload)
    {
        try
        {
            string id = string.Empty;
            bool isSnapshot = false;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (payload.TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("backupId", out var bEl) && bEl.ValueKind == JsonValueKind.String)
                    {
                        id = bEl.GetString() ?? string.Empty;
                        isSnapshot = true;
                    }
                    else if (doc.RootElement.TryGetProperty("transactionId", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    {
                        id = tEl.GetString() ?? string.Empty;
                    }
                    else if (doc.RootElement.TryGetProperty("id", out var iEl) && iEl.ValueKind == JsonValueKind.String)
                    {
                        id = iEl.GetString() ?? string.Empty;
                    }
                }
                else
                {
                    id = payload.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(id))
                return Fail("transactionId or backupId is required.");

            if (isSnapshot)
            {
                var (validSnap, snapReason, entry) = BackupStore.Validate(id);
                return Ok(JsonSerializer.Serialize(new { valid = validSnap, reason = snapReason, entry }));
            }

            var bm = ResolveBackupManager();
            var (validTx, txReason) = bm.ValidateTransactionIntegrity(id);
            if (!validTx)
            {
                var (validSnap, snapReason, entry) = BackupStore.Validate(id);
                if (entry != null)
                    return Ok(JsonSerializer.Serialize(new { valid = validSnap, reason = snapReason, entry }));
            }
            return Ok(JsonSerializer.Serialize(new { valid = validTx, reason = txReason }));
        }
        catch (Exception ex)
        {
            return Fail($"Backup validation failed: {ex.Message}");
        }
    }

    private IpcResponse DeleteBackupImpl(string? payload)
    {
        try
        {
            string id = string.Empty;
            bool isSnapshot = false;
            bool deleteAll = false;
            List<string>? bulkIds = null;

            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (payload.TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("deleteAll", out var daEl) && daEl.ValueKind == JsonValueKind.True)
                    {
                        deleteAll = true;
                    }
                    else if (root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                    {
                        bulkIds = new List<string>();
                        foreach (var item in idsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                string val = item.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(val)) bulkIds.Add(val);
                            }
                        }
                    }
                    else if (root.TryGetProperty("backupId", out var bEl) && bEl.ValueKind == JsonValueKind.String)
                    {
                        id = bEl.GetString() ?? string.Empty;
                        isSnapshot = true;
                    }
                    else if (root.TryGetProperty("transactionId", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    {
                        id = tEl.GetString() ?? string.Empty;
                    }
                    else if (root.TryGetProperty("id", out var iEl) && iEl.ValueKind == JsonValueKind.String)
                    {
                        id = iEl.GetString() ?? string.Empty;
                    }
                }
                else
                {
                    id = payload.Trim();
                }
            }

            var bm = ResolveBackupManager();

            if (deleteAll)
            {
                var (bmDeleted, bmSkipped, bmFailed, bmFreed) = bm.DeleteAllApplicationBackups();
                var (storeDeleted, storeFailed, storeFreed) = BackupStore.DeleteAll();
                var (v4Deleted, v4Failed) = new V4RollbackEngine().DeleteAllTransactions();

                int totalDeleted = bmDeleted + storeDeleted + v4Deleted;
                int totalSkipped = bmSkipped;
                int totalFailed = bmFailed + storeFailed + v4Failed;
                long totalFreed = bmFreed + storeFreed;

                return Ok(JsonSerializer.Serialize(new
                {
                    success = true,
                    deleted = true,
                    deletedCount = totalDeleted,
                    skippedCount = totalSkipped,
                    failedCount = totalFailed,
                    freedBytes = totalFreed,
                    message = $"{totalDeleted} application backup(s) purged. Windows System Restore points preserved."
                }));
            }

            if (bulkIds != null)
            {
                var (bmDeleted, bmSkipped, bmFailed, bmFreed) = bm.DeleteTransactions(bulkIds);
                var (storeDeleted, storeFailed, storeFreed) = BackupStore.DeleteMultiple(bulkIds);
                var (v4Deleted, v4Failed) = new V4RollbackEngine().DeleteTransactions(bulkIds);

                int totalDeleted = bmDeleted + storeDeleted + v4Deleted;
                int totalSkipped = bmSkipped;
                int totalFailed = bmFailed + storeFailed + v4Failed;
                long totalFreed = bmFreed + storeFreed;

                return Ok(JsonSerializer.Serialize(new
                {
                    success = true,
                    deleted = totalDeleted > 0,
                    deletedCount = totalDeleted,
                    skippedCount = totalSkipped,
                    failedCount = totalFailed,
                    freedBytes = totalFreed,
                    message = $"{totalDeleted} backup(s) deleted cleanly."
                }));
            }

            if (string.IsNullOrWhiteSpace(id))
                return Fail("transactionId, backupId, ids, or deleteAll is required.");

            if (isSnapshot)
            {
                var (deletedSnap, reason) = BackupStore.Delete(id);
                return deletedSnap
                    ? Ok(JsonSerializer.Serialize(new { deleted = true, id, message = reason }))
                    : Fail(reason);
            }

            if (bm.DeleteTransaction(id))
                return Ok(JsonSerializer.Serialize(new { deleted = true, id, message = $"Transaction '{id}' deleted safely." }));

            var (deletedFallback, snapMsg) = BackupStore.Delete(id);
            if (deletedFallback)
                return Ok(JsonSerializer.Serialize(new { deleted = true, id, message = snapMsg }));

            if (new V4RollbackEngine().DeleteTransaction(id))
                return Ok(JsonSerializer.Serialize(new { deleted = true, id, message = $"V4 Transaction '{id}' deleted safely." }));

            return Fail($"Backup '{id}' was not found.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to delete backup: {ex.Message}");
        }
    }

    private async Task<IpcResponse> RestoreTierRealAsync(string? payload, CancellationToken ct)
    {
        try
        {
            string tierId = string.IsNullOrWhiteSpace(payload) ? "Normal" : payload.Trim();
            if (tierId.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(tierId);
                if (doc.RootElement.TryGetProperty("profileName", out var p) && p.ValueKind == JsonValueKind.String)
                    tierId = p.GetString() ?? "Normal";
                else if (doc.RootElement.TryGetProperty("tierId", out var t) && t.ValueKind == JsonValueKind.String)
                    tierId = t.GetString() ?? "Normal";
            }

            var bm = ResolveBackupManager();
            var result = await bm.RestoreProfileAsync(tierId, ct).ConfigureAwait(false);
            return result.Success
                ? Ok(JsonSerializer.Serialize(result))
                : Fail(string.IsNullOrWhiteSpace(result.Message) ? "Tier restore failed." : result.Message);
        }
        catch (Exception ex)
        {
            return Fail($"Tier restore failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> RestoreBackupRealAsync(string? payload, CancellationToken ct)
    {
        try
        {
            var bm = ResolveBackupManager();
            string raw = payload?.Trim() ?? string.Empty;

            if (raw.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("fullSnapshot", out var fullEl) && fullEl.ValueKind == JsonValueKind.True)
                {
                    var fullRes = await bm.RestoreFullSystemSnapshotAsync(ct).ConfigureAwait(false);
                    return fullRes.Success
                        ? Ok(JsonSerializer.Serialize(fullRes))
                        : Fail(string.IsNullOrWhiteSpace(fullRes.Message) ? "Full system restore failed." : fullRes.Message);
                }

                if (root.TryGetProperty("transactionId", out var txEl) && txEl.ValueKind == JsonValueKind.String)
                {
                    string txId = txEl.GetString() ?? string.Empty;
                    var txRes = await bm.RestoreSingleTransactionAsync(txId, ct).ConfigureAwait(false);
                    return txRes.Success
                        ? Ok(JsonSerializer.Serialize(txRes))
                        : Fail(string.IsNullOrWhiteSpace(txRes.Message) ? $"Restore for transaction '{txId}' failed." : txRes.Message);
                }

                if (root.TryGetProperty("backupId", out var bEl) && bEl.ValueKind == JsonValueKind.String)
                {
                    string backupId = bEl.GetString() ?? string.Empty;
                    var (validSnap, reason, _) = BackupStore.Validate(backupId);
                    if (!validSnap)
                        return Fail(reason);
                    BackupStore.Restore(backupId);
                    return Ok(JsonSerializer.Serialize(new { success = true, backupId, message = $"V4 snapshot '{backupId}' restored." }));
                }

                if (root.TryGetProperty("batchId", out var batchEl) && batchEl.ValueKind == JsonValueKind.String)
                {
                    string batchId = batchEl.GetString() ?? string.Empty;
                    bool ok = new V4RollbackEngine().RollbackBatch(batchId, out int restoredCount, out int failedCount, out string summaryMsg);
                    return ok
                        ? Ok(JsonSerializer.Serialize(new { success = true, batchId, restoredCount, message = $"V4 batch '{batchId}' rolled back." }))
                        : Fail(string.IsNullOrWhiteSpace(summaryMsg) ? $"V4 rollback failed ({failedCount} failed)." : summaryMsg);
                }

                if (root.TryGetProperty("profileName", out var profEl) && profEl.ValueKind == JsonValueKind.String)
                {
                    string prof = profEl.GetString() ?? string.Empty;
                    var profRes = await bm.RestoreProfileAsync(prof, ct).ConfigureAwait(false);
                    return profRes.Success
                        ? Ok(JsonSerializer.Serialize(profRes))
                        : Fail(string.IsNullOrWhiteSpace(profRes.Message) ? $"Restore for profile '{prof}' failed." : profRes.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (bm.GetTransactionById(raw) != null)
                {
                    var txRes = await bm.RestoreSingleTransactionAsync(raw, ct).ConfigureAwait(false);
                    return txRes.Success
                        ? Ok(JsonSerializer.Serialize(txRes))
                        : Fail(string.IsNullOrWhiteSpace(txRes.Message) ? $"Restore for transaction '{raw}' failed." : txRes.Message);
                }

                var byProfile = await bm.RestoreProfileAsync(raw, ct).ConfigureAwait(false);
                return byProfile.Success
                    ? Ok(JsonSerializer.Serialize(byProfile))
                    : Fail(string.IsNullOrWhiteSpace(byProfile.Message) ? $"Restore for '{raw}' failed." : byProfile.Message);
            }

            var latest = bm.GetAllTransactions()
                .Where(t => t.CanRollback && t.RollbackStatus != "RESTORED")
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            if (latest == null)
                return Ok(JsonSerializer.Serialize(new RestoreResult
                {
                    Success = true,
                    VerificationResult = "Already in Baseline State",
                    Message = "No restorable backup transactions remain."
                }));

            var latestRes = await bm.RestoreSingleTransactionAsync(latest.TransactionId, ct).ConfigureAwait(false);
            return latestRes.Success
                ? Ok(JsonSerializer.Serialize(latestRes))
                : Fail(string.IsNullOrWhiteSpace(latestRes.Message) ? $"Restore for '{latest.TransactionId}' failed." : latestRes.Message);
        }
        catch (Exception ex)
        {
            return Fail($"Backup restore failed: {ex.Message}");
        }
    }

    private IpcResponse GetDebloatBlocklistImpl()
    {
        try
        {
            return Ok(JsonSerializer.Serialize(new { blockedIds = DebloatBlocklistStore.LoadBlockedIds() }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to read debloat blocklist: {ex.Message}");
        }
    }

    private IpcResponse SaveDebloatBlocklistImpl(string? payload)
    {
        try
        {
            var ids = ExtractStringArrayPayload(payload, "blockedIds", "ids");
            var saved = DebloatBlocklistStore.SaveBlockedIds(ids);
            return Ok(JsonSerializer.Serialize(new { success = true, blockedIds = saved, count = saved.Count }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to save debloat blocklist: {ex.Message}");
        }
    }

    private IpcResponse RequestUefiRebootImpl(string? payload)
    {
        bool confirmed = ReadBoolPayload(payload, "confirmed");
        if (!confirmed)
            return Fail("Explicit confirmation (confirmed=true) is required before rebooting to UEFI firmware.");

        bool ok = BiosSafeEngine.Instance.RequestDirectUefiReboot(out string message);
        return ok
            ? Ok(JsonSerializer.Serialize(new { success = true, message }))
            : Fail(string.IsNullOrWhiteSpace(message) ? "UEFI firmware reboot unavailable." : message);
    }

    private IpcResponse ScheduleSystemRestartImpl(string? payload)
    {
        bool confirmed = ReadBoolPayload(payload, "confirmed");
        int delay = ReadIntPayload(payload, "delaySeconds", 60);
        string? reason = ReadStringPayload(payload, "reason");
        var outcome = SystemPowerOperations.ScheduleRestart(delay, confirmed, reason);
        return outcome.Success
            ? Ok(JsonSerializer.Serialize(new { outcome.Success, outcome.Message, outcome.Data }))
            : Fail(outcome.Message);
    }

    private IpcResponse CancelScheduledRestartImpl()
    {
        var outcome = SystemPowerOperations.CancelScheduledRestart();
        return outcome.Success
            ? Ok(JsonSerializer.Serialize(new { outcome.Success, outcome.Message }))
            : Fail(outcome.Message);
    }

    private IpcResponse EvaluateAboutCatalogImpl()
    {
        try
        {
            var data = SystemPowerOperations.EvaluateAboutCatalogAudit(
                isServiceConnected: true,
                serviceVersion: typeof(NamedPipeServer).Assembly.GetName().Version?.ToString());
            return Ok(JsonSerializer.Serialize(data));
        }
        catch (Exception ex)
        {
            return Fail($"About catalog evaluation failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> DiscoverOneClickAsync(CancellationToken ct)
    {
        try
        {
            var plan = await new OneClickOptimizationEngine().DiscoverPlanAsync(ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(plan, CamelCaseJsonOptions));
        }
        catch (Exception ex)
        {
            return Fail($"One-Click discovery failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> ExecuteOneClickAsync(string? payload, CancellationToken ct)
    {
        try
        {
            var selectedIds = ExtractStringArrayPayload(payload, "selectedIds");
            var engine = new OneClickOptimizationEngine();
            var plan = await engine.DiscoverPlanAsync(ct).ConfigureAwait(false);
            bool hasExplicitSelection = !string.IsNullOrWhiteSpace(payload) && payload.Contains("\"selectedIds\"", StringComparison.OrdinalIgnoreCase);
            if (hasExplicitSelection || selectedIds.Count > 0)
            {
                var set = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
                foreach (var action in plan.Actions)
                    action.IsSelected = set.Contains(action.Id);
            }
            var res = await engine.ExecutePlanAsync(plan, progress: null, ct: ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(res, CamelCaseJsonOptions));
        }
        catch (Exception ex)
        {
            return Fail($"One-Click execution failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> EvaluateSystemHealthAsync(CancellationToken ct)
    {
        try
        {
            var snap = await new SystemHealthEngine().EvaluateSystemHealthAsync(ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(snap));
        }
        catch (Exception ex)
        {
            return Fail($"System Health evaluation failed: {ex.Message}");
        }
    }

    private IpcResponse GetRamRegistryValuesImpl()
    {
        try
        {
            return Ok(JsonSerializer.Serialize(MachineRegistryValueEngine.Instance.ScanAllValues()));
        }
        catch (Exception ex)
        {
            return Fail($"RAM registry scan failed: {ex.Message}");
        }
    }

    private IpcResponse MutateRamRegistryValueImpl(string? payload, bool apply)
    {
        try
        {
            string? id = ReadStringPayload(payload, "id");
            if (string.IsNullOrWhiteSpace(id)) return Fail("Entry 'id' is required.");

            if (apply)
            {
                var targetItem = MachineRegistryValueEngine.Instance.ScanAllValues()
                    .FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (targetItem == null) return Fail($"RAM registry entry '{id}' not found.");
                if (targetItem.Status == RegistryValueStatus.NotApplicable)
                    return Fail("RAM registry setting is not applicable on this Windows build.");
                if (targetItem.Status == RegistryValueStatus.AlreadyConfigured)
                {
                    string canTarget = BiosOptimizer.Core.V4.Architecture.GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", $@"{targetItem.RegistryPath}\{targetItem.ValueName}");
                    BiosOptimizer.Core.V4.Architecture.GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                        canTarget, targetItem.Id, "RegistryValuesRam", "RegistryValuesRam",
                        targetItem.CurrentValueDisplay ?? string.Empty, targetItem.CandidateValueDisplay ?? string.Empty, "SetRegistryValue");
                    return Ok(JsonSerializer.Serialize(new { item = targetItem, message = "Setting is already configured to optimal threshold.", status = "ALREADY_OPTIMAL" }));
                }

                long? kb = null;
                double? ramGb = null;
                if (!string.IsNullOrWhiteSpace(payload) && payload.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.TryGetProperty("kb", out var kbEl) && kbEl.TryGetInt64(out long kbVal))
                            kb = kbVal;
                        if (doc.RootElement.TryGetProperty("ramGb", out var rEl) && rEl.TryGetDouble(out double rVal))
                            ramGb = rVal;
                    }
                    catch { }
                }

                bool okApply = MachineRegistryValueEngine.Instance.ApplyRegistryValueWithSelection(id, kb, ramGb, out var appliedItem, out string applyMsg);
                return okApply && appliedItem != null
                    ? Ok(JsonSerializer.Serialize(new { item = appliedItem, message = applyMsg }))
                    : Fail(applyMsg);
            }

            var item = MachineRegistryValueEngine.Instance.ScanAllValues()
                .FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item == null) return Fail($"RAM registry entry '{id}' not found.");

            bool ok = MachineRegistryValueEngine.Instance.RollbackRegistryValue(item, out string msg);
            return ok ? Ok(JsonSerializer.Serialize(new { item, message = msg })) : Fail(msg);
        }
        catch (Exception ex)
        {
            return Fail($"RAM registry operation failed: {ex.Message}");
        }
    }

    private IpcResponse GetGpuRegistryValuesImpl(string? payload)
    {
        try
        {
            string profile = ReadStringPayload(payload, "profile") ?? "Normal";
            var hw = GpuRegistryValueEngine.Instance.DetectGpuHardware();
            var items = GpuRegistryValueEngine.Instance.ScanAllGpuOptimizations(profile, forceRescan: true);
            var eligibleItems = items.Where(i => i.IsApplicable).ToList();
            bool hasNvidia = hw.VendorType == GpuVendorType.Nvidia || hw.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasAmd = hw.VendorType == GpuVendorType.Amd || hw.Name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || hw.Name.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0;
            return Ok(JsonSerializer.Serialize(new { hardware = hw, items = eligibleItems, hasNvidia, hasAmd }));
        }
        catch (Exception ex)
        {
            return Fail($"GPU registry scan failed: {ex.Message}");
        }
    }

    private IpcResponse MutateGpuRegistryValueImpl(string? payload, bool apply)
    {
        try
        {
            string? id = ReadStringPayload(payload, "id");
            if (string.IsNullOrWhiteSpace(id)) return Fail("Entry 'id' is required.");
            string profile = ReadStringPayload(payload, "profile") ?? "Normal";
            var item = GpuRegistryValueEngine.Instance.FindOptimization(id, profile);
            if (item == null) return Fail($"GPU registry entry '{id}' not found.");
            if (apply && !item.IsApplicable) return Fail(item.WhyApplicable ?? "Not applicable on this hardware.");
            if (apply && item.Status == RegistryValueStatus.AlreadyConfigured)
            {
                string canTarget = BiosOptimizer.Core.V4.Architecture.GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", $@"{item.RegistryPath}\{item.ValueName}");
                BiosOptimizer.Core.V4.Architecture.GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                    canTarget, item.Id, "GpuRegistryValues", "GpuRegistryValues",
                    item.CurrentValueDisplay ?? string.Empty, item.TargetValueDisplay ?? string.Empty, "SetRegistryValue");
                return Ok(JsonSerializer.Serialize(new { success = true, item, message = "Setting is already in its optimal configuration.", status = "ALREADY_OPTIMAL" }));
            }

            var (ok, msg) = apply
                ? GpuRegistryValueEngine.Instance.ApplyOptimization(item)
                : GpuRegistryValueEngine.Instance.RestoreOptimization(item);

            return ok ? Ok(JsonSerializer.Serialize(new { success = true, item, message = msg, status = "SUCCESS" })) : Fail(msg);
        }
        catch (Exception ex)
        {
            return Fail($"GPU registry operation failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> WorkloadDiscoverAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var engine = WorkloadOptimizationEngine.Instance;
            engine.LoadConfiguration();
            engine.BuildInstalledInventory();
            engine.DetectAndProcessWorkload();
            return Ok(JsonSerializer.Serialize(new { status = "discovered" }));
        }, ct).ConfigureAwait(false);
    }

    private async Task<IpcResponse> WorkloadApplyAsync(CancellationToken ct)
    {
        try
        {
            var res = await WorkloadOptimizationEngine.Instance.OptimizeActiveForegroundWorkloadAsync(ct).ConfigureAwait(false);
            return Ok(JsonSerializer.Serialize(res));
        }
        catch (Exception ex)
        {
            return Fail($"Workload apply failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> WorkloadRevertAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            WorkloadOptimizationEngine.Instance.RollbackAllWorkloadSessions();
            return Ok(JsonSerializer.Serialize(new { reverted = true }));
        }, ct).ConfigureAwait(false);
    }

    // ── Shared payload JSON parsers ──
    internal static string? ReadStringPayload(string? payload, string propName)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        string trimmed = payload.Trim();
        if (!trimmed.StartsWith("{")) return trimmed;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }

    internal static bool ReadBoolPayload(string? payload, string propName, bool defaultVal = false)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith("{")) return defaultVal;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty(propName, out var el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
            }
        }
        catch { }
        return defaultVal;
    }

    internal static int ReadIntPayload(string? payload, string propName, int defaultVal = 0)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith("{")) return defaultVal;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty(propName, out var el) && el.TryGetInt32(out int v))
                return v;
        }
        catch { }
        return defaultVal;
    }

    internal static List<string> ExtractStringArrayPayload(string? payload, params string[] candidateProps)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(payload)) return list;
        string trimmed = payload.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            JsonElement target = default;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                target = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in candidateProps)
                {
                    if (doc.RootElement.TryGetProperty(prop, out var found) && found.ValueKind == JsonValueKind.Array)
                    {
                        target = found;
                        break;
                    }
                }
            }

            if (target.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in target.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                        list.Add(el.GetString()!);
                }
            }
        }
        catch { }
        return list;
    }

    private IpcResponse ReportInteractiveSessionStateImpl(string? payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail("Payload required for ReportInteractiveSessionState.");

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            int sessionId = root.TryGetProperty("sessionId", out var sEl) && sEl.TryGetInt32(out int sVal) ? sVal : 1;
            uint pid = root.TryGetProperty("foregroundPid", out var pEl) && pEl.TryGetUInt32(out uint pVal) ? pVal : 0;
            string procName = root.TryGetProperty("processName", out var pnEl) ? pnEl.GetString() ?? "" : "";
            string exePath = root.TryGetProperty("executablePath", out var epEl) ? epEl.GetString() ?? "" : "";
            string title = root.TryGetProperty("windowTitle", out var wtEl) ? wtEl.GetString() ?? "" : "";
            bool isFs = root.TryGetProperty("isFullscreen", out var fsEl) && fsEl.GetBoolean();

            InteractiveSessionBridge.UpdateSessionForeground(sessionId, pid, procName, exePath, title, isFs);
            return Ok(JsonSerializer.Serialize(new { success = true, sessionId, foregroundPid = pid }));
        }
        catch (Exception ex)
        {
            return Fail($"Failed to update interactive session state: {ex.Message}");
        }
    }
}
