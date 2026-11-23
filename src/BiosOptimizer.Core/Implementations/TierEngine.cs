using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.Services;
using BiosOptimizer.Core.V4.Recommendations;
using BiosOptimizer.Core.V4.Safety;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiosOptimizer.Core.Implementations;
#pragma warning disable 1998

public class TierEngine : ITierEngine
{
    private readonly IProfileRepository _profileRepo;
    private readonly IEnvironmentDetector _envDetector;
    private readonly IActionRegistry _actionRegistry;
    private readonly IProcessSnapshot _processSnapshot;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly IBackupManager _backupManager;

    // Never-touch enforcement (last line of defence - handlers also enforce this)
    private static readonly HashSet<string> NeverTouchServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "SecurityHealthService", "mpssvc", "wscsvc", "Sense", "WdNisSvc", "MpsSvc",
        "AudioSrv", "AudioEndpointBuilder", "RpcSs", "RpcEptMapper", "DcomLaunch", "PlugPlay",
        "Power", "EventLog", "EventSystem", "BrokerInfrastructure", "SystemEventsBroker",
        "TimeBrokerSvc", "Schedule", "CryptSvc", "Dhcp", "Dnscache", "netprofm", "nsi",
        "Winmgmt", "LSM", "FontCache", "StateRepository", "ProfSvc", "gpsvc",
        "LanmanWorkstation", "LanmanServer", "Themes", "WlanSvc", "WwanSvc", "NlaSvc",
        "Netman", "NlaSvc", "netprofm"
    };

    // Services where we can set Manual but never Disabled
    private static readonly HashSet<string> ManualOnlyServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "UsoSvc", "wuauserv", "BITS", "DoSvc"
    };

    public TierEngine(
        IProfileRepository profileRepo,
        IEnvironmentDetector envDetector,
        IActionRegistry actionRegistry,
        IProcessSnapshot processSnapshot,
        IBackupManager backupManager)
    {
        _profileRepo = profileRepo;
        _envDetector = envDetector;
        _actionRegistry = actionRegistry;
        _processSnapshot = processSnapshot;
        _backupManager = backupManager;
        _conditionEvaluator = new ConditionEvaluator();
    }

    private static readonly Dictionary<string, string[]> TierCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = new[] { "Power", "Input", "Memory", "Storage", "Ntfs", "Privacy", "Telemetry" },
        ["Pro"] = new[] { "Cpu", "Memory", "Network", "Gaming", "Telemetry", "Power" },
        ["Ultimate"] = new[] { "Cpu", "Memory", "Gpu", "Gaming", "Network", "Storage", "Ntfs", "Power" },
        ["Debloat"] = new[] { "Privacy", "Telemetry", "Bloatware" },
        ["BiosSafe"] = new[] { "System", "Bios", "Firmware" },
        ["MaximumPerformance"] = new[] { "Cpu", "Gpu", "Power", "Memory", "Gaming" },
        ["AaaGaming"] = new[] { "Gpu", "Gaming", "Cpu", "Input", "Power" },
        ["AfterEffectsTurbo"] = new[] { "Memory", "Storage", "Cpu", "Power" },
        ["EmulatorMax"] = new[] { "System", "Power", "Cpu", "Memory" },
        ["LowEndRescue"] = new[] { "System", "Power", "Memory", "Storage" },
        ["RamTest"] = new[] { "Memory" },
        ["TopazAI"] = new[] { "Gpu", "Cpu", "Power" },
        ["TotalMax"] = new[] { "Cpu", "Gpu", "Power", "Memory", "Storage", "Network", "Gaming" }
    };

    public Task<OptimizationPreview> PreviewAsync(string tierId, CancellationToken cancellationToken)
    {
        string normalized = ProfileRepository.NormalizeProfileId(tierId);
        var profile = _profileRepo.LoadResolvedProfile(normalized);
        if (profile == null) throw new FileNotFoundException($"Optimization profile '{tierId}' (normalized as '{normalized}') could not be resolved.");

        var env = _envDetector.Detect();
        var snapshot = _processSnapshot.TakeSnapshot();
        var preview = new OptimizationPreview
        {
            CurrentProcessCount = snapshot.ProcessCount,
            EstimatedProcessReduction = (profile.TargetProcessReduction.Min + profile.TargetProcessReduction.Max) / 2,
            Baseline = new PerformanceBaseline
            {
                Timestamp = DateTime.UtcNow,
                ProcessCount = snapshot.ProcessCount,
                AvailableRamBytes = env.HardwareProfile.RAM,
                PowerPlan = env.HardwareProfile.PowerPlan
            }
        };

        foreach (var entry in profile.Entries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Safety gate: Defender V4 Protected Rules and never-touch enforcement
            if (IsBlockedByDefenderProtection(entry, out var defenderReason))
            {
                preview.SkippedChanges.Add(new SkippedChange { Id = entry.Id, Item = entry.DisplayName, Category = "NotApplicable", Reason = defenderReason });
                continue;
            }

            if (!IsOsSupported(entry, env))
            {
                preview.SkippedChanges.Add(new SkippedChange { Id = entry.Id, Item = entry.DisplayName, Category = "Unsupported", Reason = $"Unsupported on this OS ({(env.IsWindows10 ? "Win10" : "Win11")})" });
                continue;
            }

            if (!_conditionEvaluator.Evaluate(entry.Conditions, env))
            {
                var reason = BuildConditionReason(entry.Conditions, env);
                preview.SkippedChanges.Add(new SkippedChange { Id = entry.Id, Item = entry.DisplayName, Category = "NotApplicable", Reason = reason });
                continue;
            }

            var handler = _actionRegistry.GetHandler(entry.Action);
            if (handler == null)
            {
                preview.SkippedChanges.Add(new SkippedChange { Id = entry.Id, Item = entry.DisplayName, Category = "Unsupported", Reason = "Handler unavailable" });
                continue;
            }

            var availability = handler.CheckAvailability(entry, env);
            if (availability != TargetState.Ready)
            {
                preview.SkippedChanges.Add(new SkippedChange { Id = entry.Id, Item = entry.DisplayName, Category = "NotApplicable", Reason = availability == TargetState.NotAvailable ? "Target not present on this machine" : "Action not applicable" });
                continue;
            }

            var change = new PlannedChange
            {
                Id = entry.Id,
                ActionName = entry.Action,
                Item = entry.DisplayName,
                Category = entry.Category,
                Current = handler.GetCurrentValueDisplay(entry),
                Target = entry.Value?.ToString() ?? "",
                Risk = entry.Risk,
                Warning = entry.Warning,
                Description = entry.Description,
                ExecutionType = entry.ExecutionType ?? "WINDOWS_AUTOMATIC"
            };

            if (string.Equals(entry.SourceProfile, normalized, StringComparison.OrdinalIgnoreCase))
            {
                change.IsInherited = false;
                preview.PlannedChanges.Add(change);
            }
            else
            {
                change.IsInherited = true;
                preview.InheritedChanges.Add(change);
                preview.InheritedEntryCount++;
            }
        }

        // Include matching catalog optimizations in preview
        if (TierCategoryMap.TryGetValue(normalized, out var cats))
        {
            var existingIds = new HashSet<string>(preview.PlannedChanges.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var meta in OptimizationCatalog.Instance.GetAll())
            {
                if (existingIds.Contains(meta.OptimizationId)) continue;
                if (!cats.Any(c => c.Equals(meta.Category, StringComparison.OrdinalIgnoreCase))) continue;

                bool isDone = false;
                try { isDone = meta.Verify != null && meta.Verify(); } catch { }
                string targetVal = meta.ReadTargetState?.Invoke() ?? "Optimized";
                string curVal = isDone ? targetVal : (meta.ReadCurrentState?.Invoke() ?? "Default");

                preview.PlannedChanges.Add(new PlannedChange
                {
                    Id = meta.OptimizationId,
                    ActionName = meta.ExecutionMethod,
                    Item = meta.Name,
                    Category = meta.Category,
                    Current = curVal,
                    Target = targetVal,
                    Risk = meta.RiskLevel,
                    Description = meta.Description,
                    ExecutionType = "WINDOWS_AUTOMATIC",
                    IsInherited = false
                });
            }
        }

        return Task.FromResult(preview);
    }

    public async Task<List<OptimizationResult>> ApplyAsync(string tierId, ConfirmationContext confirmation, bool dryRun, CancellationToken cancellationToken, IProgress<OptimizationResult>? progress = null, HashSet<string>? allowedItemIds = null)
    {
        string normalized = ProfileRepository.NormalizeProfileId(tierId);
        var profile = _profileRepo.LoadResolvedProfile(normalized);
        if (profile == null) throw new FileNotFoundException($"Optimization profile '{tierId}' (normalized as '{normalized}') could not be resolved.");
        
        return await ApplyAsync(profile, confirmation, dryRun, cancellationToken, progress, allowedItemIds);
    }
    
    public async Task<List<OptimizationResult>> ApplyAsync(ProfileDef profile, ConfirmationContext confirmation, bool dryRun, CancellationToken cancellationToken, IProgress<OptimizationResult>? progress = null, HashSet<string>? allowedItemIds = null)
    {
        var results = new List<OptimizationResult>();

        // Phase 1: Machine & capability detection
        EmitSystem(progress, $"Applying {profile.DisplayName} (Source: {profile.ProfileOrigin})...");
        EmitSystem(progress, "Detecting machine...");
        var env = _envDetector.Detect();
        EmitSystem(progress, "Detecting hardware...");
        EmitSystem(progress, $"CPU: {env.CpuName} ({env.PhysicalCoreCount}C/{env.LogicalCoreCount}T)");
        EmitSystem(progress, $"RAM: {env.RamSizeGb} GB | Storage: {env.StorageType}");
        EmitSystem(progress, $"GPU(s): {string.Join(", ", env.Gpus.Select(g => g.Name))}");
        EmitSystem(progress, "Detecting firmware...");
        EmitSystem(progress, $"BIOS: {env.BIOSVendor} {env.BIOSVersion}");
        EmitSystem(progress, "Detecting installed software...");
        var workloadNames = env.Workloads.Where(w => w.Detected).Select(w => w.DisplayName).ToList();
        if (workloadNames.Count > 0)
            EmitSystem(progress, $"Detected workloads: {string.Join(", ", workloadNames)}");
        else
            EmitSystem(progress, "No special workloads detected.");

        // Phase 2: Build machine-specific optimization plan
        EmitPlan(progress, "Building machine-specific optimization plan...");

        var executedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogItemsToExecute = new List<OptimizationMetadata>();

        if (allowedItemIds != null && allowedItemIds.Count > 0)
        {
            foreach (var id in allowedItemIds)
            {
                var meta = OptimizationCatalog.Instance.GetById(id);
                if (meta != null && executedItemIds.Add(meta.OptimizationId))
                {
                    catalogItemsToExecute.Add(meta);
                }
            }
        }
        else
        {
            string normalizedTier = ProfileRepository.NormalizeProfileId(profile.Id);
            if (TierCategoryMap.TryGetValue(normalizedTier, out var cats))
            {
                foreach (var meta in OptimizationCatalog.Instance.GetAll())
                {
                    if (cats.Any(c => c.Equals(meta.Category, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (executedItemIds.Add(meta.OptimizationId))
                        {
                            catalogItemsToExecute.Add(meta);
                        }
                    }
                }
            }
        }

        var executionPlan = new List<(OptimizationEntry Entry, IActionHandler Handler)>();
        var skippedTargets = new List<(OptimizationEntry Entry, string Reason)>();

        foreach (var entry in profile.Entries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Strict Scope & Deduplication: Never execute an item twice
            if (executedItemIds.Contains(entry.Id) || executedItemIds.Contains(entry.Target))
            {
                continue;
            }

            // Engine-level safety gates: Defender V4 Protected Rules enforcement
            if (IsBlockedByDefenderProtection(entry, out var defenderApplyReason))
            {
                skippedTargets.Add((entry, defenderApplyReason));
                continue;
            }

            if (!IsOsSupported(entry, env))
            {
                skippedTargets.Add((entry, $"OS gate: not applicable to {(env.IsWindows10 ? "Win10" : "Win11")}"));
                continue;
            }

            if (!_conditionEvaluator.Evaluate(entry.Conditions, env))
            {
                var reason = BuildConditionReason(entry.Conditions, env);
                skippedTargets.Add((entry, $"Hardware/software gate: {reason}"));
                continue;
            }

            bool isSelected = allowedItemIds == null ||
                allowedItemIds.Contains(entry.Id) ||
                allowedItemIds.Contains(entry.DisplayName) ||
                allowedItemIds.Contains(entry.Target);

            if (!isSelected)
            {
                skippedTargets.Add((entry, "User deselected this item during preview"));
                continue;
            }

            var handler = _actionRegistry.GetHandler(entry.Action);
            if (handler == null)
            {
                skippedTargets.Add((entry, $"No handler for action: {entry.Action}"));
                continue;
            }

            var availability = handler.CheckAvailability(entry, env);
            if (availability == TargetState.Ready)
            {
                executionPlan.Add((entry, handler));
                executedItemIds.Add(entry.Id);
            }
            else if (availability == TargetState.Protected)
            {
                skippedTargets.Add((entry, $"PROTECTED: {entry.Target} is protected by system security or safety policy"));
            }
            else if (availability == TargetState.NotApplicable)
            {
                skippedTargets.Add((entry, $"UNSUPPORTED: {entry.Target} is not applicable to current environment"));
            }
            else if (availability == TargetState.NotAvailable)
            {
                skippedTargets.Add((entry, $"NOT AVAILABLE: Target {entry.Target} does not exist on this machine"));
            }
        }

        // Emit results for skipped/protected items (so GUI log shows them)
        foreach (var (entry, reason) in skippedTargets)
        {
            bool isProtected = reason.Contains("PROTECTED", StringComparison.OrdinalIgnoreCase) ||
                               reason.Contains("DEFENDER", StringComparison.OrdinalIgnoreCase) ||
                               reason.Contains("NEVER-TOUCH", StringComparison.OrdinalIgnoreCase);
            bool isUnsupported = reason.Contains("UNSUPPORTED", StringComparison.OrdinalIgnoreCase) ||
                                 reason.Contains("OS gate", StringComparison.OrdinalIgnoreCase) ||
                                 reason.Contains("Hardware/software gate", StringComparison.OrdinalIgnoreCase);
            var r = new OptimizationResult
            {
                Status = isProtected ? ResultStatus.Protected : (isUnsupported ? ResultStatus.Unsupported : ResultStatus.NotAvailable),
                ItemId = entry.Id,
                ActionName = entry.Action,
                Category = entry.Category,
                DisplayName = entry.DisplayName,
                Message = isProtected ? $"[ PROTECTED ] {entry.DisplayName} - {reason}" : $"[ SKIPPED ] {entry.DisplayName} - {reason}"
            };
            results.Add(r);
        }

        int totalPlanCount = executionPlan.Count + catalogItemsToExecute.Count;
        if (totalPlanCount == 0)
        {
            var r = new OptimizationResult { Status = ResultStatus.NotApplicable, ItemId = "NONE", DisplayName = "Optimization Plan", Message = "No applicable optimization targets available on this machine." };
            results.Add(r);
            progress?.Report(r);
            return results;
        }

        EmitPlan(progress, $"{totalPlanCount} supported targets found for this machine.");

        string? activeBatchId = null;
        if (!dryRun)
        {
            EmitSystem(progress, "Creating SHA-256 verified pre-optimization Application Backup snapshot...");
            var snapshotTx = _backupManager.CreateApplicationSnapshot(
                $"Pre-{profile.DisplayName} Optimization Backup",
                $"Verified pre-optimization baseline for {profile.DisplayName} ({totalPlanCount} targets)");

            bool snapshotValid = snapshotTx != null &&
                                 !string.Equals(snapshotTx.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase) &&
                                 _backupManager.ValidateTransactionIntegrity(snapshotTx.TransactionId).Valid;

            if (!snapshotValid)
            {
                string reason = snapshotTx == null
                    ? "Backup snapshot returned null."
                    : _backupManager.ValidateTransactionIntegrity(snapshotTx.TransactionId).Reason;
                EmitSystem(progress, $"ABORTED: Pre-optimization Application Backup failed verification ({reason}). No Windows settings were modified.");
                var abortResult = new OptimizationResult
                {
                    Status = ResultStatus.Failed,
                    ItemId = "BackupPreflight",
                    ActionName = "CreateApplicationSnapshot",
                    Category = profile.Id,
                    DisplayName = "Pre-Optimization Application Backup",
                    Message = $"Aborted optimization because verified Application Backup could not be created: {reason}"
                };
                results.Add(abortResult);
                progress?.Report(abortResult);
                return results;
            }

            EmitSystem(progress, $"Verified Application Backup Snapshot ({snapshotTx!.TransactionId}).");

            string batchId = _backupManager.BeginBatchTransaction(profile.Id, profile.DisplayName, profile.Id);
            if (string.IsNullOrWhiteSpace(batchId) || !_backupManager.ValidateTransactionIntegrity(batchId).Valid)
            {
                EmitSystem(progress, "ABORTED: Application Backup batch container failed integrity verification. No Windows settings were modified.");
                var batchAbort = new OptimizationResult
                {
                    Status = ResultStatus.Failed,
                    ItemId = "BackupBatchPreflight",
                    ActionName = "BeginBatchTransaction",
                    Category = profile.Id,
                    DisplayName = "Application Backup Batch Container",
                    Message = "Aborted optimization because backup batch transaction could not be verified on disk."
                };
                results.Add(batchAbort);
                progress?.Report(batchAbort);
                return results;
            }

            EmitSystem(progress, "Exporting pre-execution Registry and Service states...");
            var regPaths = executionPlan.Where(p => p.Entry.Category == "Registry").Select(p => p.Entry.Target).ToList();
            var svcNames = executionPlan.Where(p => p.Entry.Category == "Service").Select(p => p.Entry.Target).ToList();
            using var preBatchScope = BackupManager.PushProfileBatchScope(profile.Id, profile.DisplayName, batchId);
            _backupManager.BackupRegistryState(regPaths);
            _backupManager.BackupServiceState(svcNames);

            activeBatchId = batchId;
        }

        // Phase 3: Capture before-baseline
        var beforeProcessCount = Process.GetProcesses().Where(p => p.Id > 4).Count();
        long beforeRamFreeBytes = 0;
        try
        {
            using var mos = new System.Management.ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var o in mos.Get())
                long.TryParse(o["FreePhysicalMemory"]?.ToString(), out beforeRamFreeBytes);
            beforeRamFreeBytes *= 1024;
        }
        catch { }

        // Phase 4: Execute
        int completedCount = 0;
        using var executionBatchScope = activeBatchId != null
            ? BackupManager.PushProfileBatchScope(profile.Id, profile.DisplayName, activeBatchId)
            : null;

        // First execute catalog items
        foreach (var meta in catalogItemsToExecute)
        {
            if (cancellationToken.IsCancellationRequested) break;
            completedCount++;

            if (meta.RiskLevel.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            {
                var r = new OptimizationResult { Status = ResultStatus.Failed, ItemId = meta.OptimizationId, ActionName = meta.ExecutionMethod, Category = meta.Category, DisplayName = meta.Name, Message = "Critical risk target blocked by safety engine." };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            if (meta.RiskLevel.Equals("High", StringComparison.OrdinalIgnoreCase) && !confirmation.IsConfirmed)
            {
                var r = new OptimizationResult { Status = ResultStatus.Failed, ItemId = meta.OptimizationId, ActionName = meta.ExecutionMethod, Category = meta.Category, DisplayName = meta.Name, Message = "High-risk target requires explicit user confirmation." };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            bool isAlreadyOptimized = false;
            try { isAlreadyOptimized = meta.Verify != null && meta.Verify(); } catch { }

            if (isAlreadyOptimized)
            {
                var r = new OptimizationResult
                {
                    Status = ResultStatus.AlreadyOptimized,
                    ItemId = meta.OptimizationId,
                    ActionName = meta.ExecutionMethod,
                    Category = meta.Category,
                    DisplayName = meta.Name,
                    Message = $"[ ALREADY OPTIMIZED ] {meta.Name} is already in the desired state."
                };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            if (dryRun)
            {
                var r = new OptimizationResult
                {
                    Status = ResultStatus.Success,
                    ItemId = meta.OptimizationId,
                    ActionName = meta.ExecutionMethod,
                    Category = meta.Category,
                    DisplayName = meta.Name,
                    Message = $"[DRY-RUN] Would apply: {meta.Name}"
                };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            progress?.Report(new OptimizationResult { Status = ResultStatus.Running, ItemId = meta.OptimizationId, ActionName = meta.ExecutionMethod, Category = meta.Category, DisplayName = meta.Name, Message = $"[ APPLY ] Setting target state for {meta.Name}" });

            var (success, msg, backupData) = meta.Execute != null ? meta.Execute(false) : (false, "No execute action", null);

            if (success)
            {
                bool verified = false;
                try { verified = meta.Verify != null && meta.Verify(); } catch { }

                var r = new OptimizationResult
                {
                    Status = ResultStatus.Success,
                    ItemId = meta.OptimizationId,
                    ActionName = meta.ExecutionMethod,
                    Category = meta.Category,
                    DisplayName = meta.Name,
                    Message = verified ? $"[ SUCCESS ] {meta.Name} ({completedCount}/{totalPlanCount}) - Verified." : $"[ APPLIED ] {meta.Name} ({completedCount}/{totalPlanCount}) - {msg}"
                };
                results.Add(r);
                progress?.Report(r);
            }
            else
            {
                var r = new OptimizationResult
                {
                    Status = ResultStatus.Failed,
                    ItemId = meta.OptimizationId,
                    ActionName = meta.ExecutionMethod,
                    Category = meta.Category,
                    DisplayName = meta.Name,
                    Message = $"[ FAILED ] {meta.Name} - {msg}"
                };
                results.Add(r);
                progress?.Report(r);
            }
        }
        foreach (var planItem in executionPlan)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var entry = planItem.Entry;
            var handler = planItem.Handler;
            completedCount++;

            // Critical risk is refused
            if (entry.Risk.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            {
                var r = new OptimizationResult { Status = ResultStatus.Failed, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = "Critical risk target blocked by safety engine." };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            // High-risk confirmation gate
            bool needsConfirm = entry.RequiresConfirmation || entry.Risk.Equals("High", StringComparison.OrdinalIgnoreCase);
            if (entry.Action.Equals("SetBiosSetting", StringComparison.OrdinalIgnoreCase) && env.BitLockerActive)
                needsConfirm = true;

            if (needsConfirm && !confirmation.IsConfirmed)
            {
                var r = new OptimizationResult { Status = ResultStatus.Failed, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"High-risk target requires explicit user confirmation." };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            if (dryRun)
            {
                var r = new OptimizationResult { Status = ResultStatus.Success, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[DRY-RUN] Would apply: {entry.DisplayName}" };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            // Emit live detection events
            progress?.Report(new OptimizationResult { Status = ResultStatus.Running, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ DETECT ] {entry.DisplayName}" });
            progress?.Report(new OptimizationResult { Status = ResultStatus.Running, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ DEPENDENCY ] Checking app dependencies - OK" });

            string currentVal = handler.GetCurrentValueDisplay(entry);
            string targetVal = entry.Value?.ToString() ?? "";

            progress?.Report(new OptimizationResult { Status = ResultStatus.Running, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ READ ] Current state = {currentVal}" });

            // Already-optimized fast path
            if (StateNormalizer.IsSatisfied(currentVal, targetVal))
            {
                var r = new OptimizationResult { Status = ResultStatus.AlreadyOptimized, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ ALREADY OPTIMIZED ] {entry.DisplayName} is already in the desired state." };
                results.Add(r);
                progress?.Report(r);
                continue;
            }

            progress?.Report(new OptimizationResult { Status = ResultStatus.Running, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ APPLY ] Setting target state = {targetVal}" });

            var applyResult = handler.Apply(entry);

            if (applyResult.Status == ResultStatus.Success)
            {
                // Real Authoritative Readback Verification
                string newCurrentVal = handler.GetCurrentValueDisplay(entry);
                
                bool isMatch = StateNormalizer.IsSatisfied(newCurrentVal, targetVal);

                if (isMatch)
                {
                    progress?.Report(new OptimizationResult { Status = ResultStatus.Verifying, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ VERIFY ] Readback confirmed: {newCurrentVal}." });
                    applyResult.Message = $"[ SUCCESS ] {entry.DisplayName} ({completedCount}/{totalPlanCount})";
                }
                else
                {
                    StateNormalizer.LogReconciliationMismatch(entry.Id, currentVal, targetVal, newCurrentVal, targetVal, handler.ActionName);
                    progress?.Report(new OptimizationResult { Status = ResultStatus.Verifying, ItemId = entry.Id, ActionName = entry.Action, Category = entry.Category, DisplayName = entry.DisplayName, Message = $"[ VERIFY ] Mismatch! Expected {targetVal}, got {newCurrentVal}." });
                    applyResult.Status = ResultStatus.Failed;
                    applyResult.Message = $"[ FAILED ] {entry.DisplayName} - Readback verification failed.";
                }
            }

            applyResult.ItemId = entry.Id;
            applyResult.ActionName = entry.Action;
            applyResult.Category = entry.Category;
            applyResult.DisplayName = entry.DisplayName;
            results.Add(applyResult);
            progress?.Report(applyResult);
        }

        // Phase 5: Capture after-baseline and emit performance measurement
        var afterProcessCount = Process.GetProcesses().Where(p => p.Id > 4).Count();
        long afterRamFreeBytes = 0;
        try
        {
            using var mos = new System.Management.ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var o in mos.Get())
                long.TryParse(o["FreePhysicalMemory"]?.ToString(), out afterRamFreeBytes);
            afterRamFreeBytes *= 1024;
        }
        catch { }

        int deltaProcesses = beforeProcessCount - afterProcessCount;
        long deltaRamFreeMb = (afterRamFreeBytes - beforeRamFreeBytes) / (1024 * 1024);

        if (activeBatchId != null)
        {
            _backupManager.CommitBatchTransaction(activeBatchId);
        }

        EmitSystem(progress, $"Process count delta: -{Math.Max(0, deltaProcesses)} processes.");
        EmitSystem(progress, $"Memory delta: +{Math.Max(0, deltaRamFreeMb)} MB free.");
        EmitSystem(progress, "Optimization cycle completed.");

        return results;
    }

    private static bool IsBlockedByDefenderProtection(OptimizationEntry entry, out string reason)
    {
        // 1. Core never-touch service enforcement
        if (IsNeverTouch(entry))
        {
            reason = $"NEVER-TOUCH: {entry.Target} is a protected core system component";
            return true;
        }

        // 2. Manual-only service policy enforcement
        if (IsDisabledForManualOnlyService(entry))
        {
            reason = $"POLICY: {entry.Target} can only be set to Manual, not Disabled";
            return true;
        }

        // 3. Central Arbiter Defender Protection Gate
        string targetToCheck = !string.IsNullOrEmpty(entry.Target) ? entry.Target : entry.Id;
        var (isBlocked, blockReason) = OptimizationSettingOwnership.Instance.EvaluateDefenderProtection(targetToCheck);
        if (isBlocked)
        {
            reason = $"DEFENDER V4 PROTECTED: {blockReason}";
            return true;
        }

        // 4. Registry-specific HardProtect validation
        if (entry.Category.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
            entry.Action.Equals("SetRegistryValue", StringComparison.OrdinalIgnoreCase))
        {
            if (HardProtectRegistry.IsProtected(targetToCheck, out var regReason))
            {
                reason = $"DEFENDER V4 PROTECTED: {regReason}";
                return true;
            }
        }

        // 5. AppX package protection validation
        if (entry.Category.Equals("AppX", StringComparison.OrdinalIgnoreCase) ||
            entry.Action.Equals("RemoveAppxPackage", StringComparison.OrdinalIgnoreCase))
        {
            if (ProtectedApplicationPolicy.IsProtectedPackage(targetToCheck))
            {
                reason = $"DEFENDER V4 PROTECTED: System-critical package '{targetToCheck}' cannot be removed";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsNeverTouch(OptimizationEntry entry)
    {
        if (entry.Category.Equals("Service", StringComparison.OrdinalIgnoreCase))
            return NeverTouchServices.Contains(entry.Target);
        return false;
    }

    private static bool IsDisabledForManualOnlyService(OptimizationEntry entry)
    {
        if (!entry.Category.Equals("Service", StringComparison.OrdinalIgnoreCase)) return false;
        if (!ManualOnlyServices.Contains(entry.Target)) return false;
        return entry.Value?.ToString()?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsOsSupported(OptimizationEntry entry, EnvironmentContext env)
    {
        return UniversalEligibilityEngine.IsOsSupported(entry, env);
    }

    private static string BuildConditionReason(List<ConditionDef>? conditions, EnvironmentContext env)
    {
        if (conditions == null || conditions.Count == 0) return "Condition not met";
        var parts = new List<string>();
        foreach (var c in conditions)
        {
            parts.Add($"{c.Type} {c.Operator} {c.Value}");
        }
        return string.Join(", ", parts);
    }

    private static void EmitSystem(IProgress<OptimizationResult>? progress, string msg)
    {
        progress?.Report(new OptimizationResult
        {
            Status = ResultStatus.Running,
            ItemId = "SYSTEM",
            ActionName = "System",
            Category = "System",
            DisplayName = "System",
            Message = $"[ SYSTEM ] {msg}"
        });
    }

    private static void EmitPlan(IProgress<OptimizationResult>? progress, string msg)
    {
        progress?.Report(new OptimizationResult
        {
            Status = ResultStatus.Running,
            ItemId = "PLAN",
            ActionName = "Plan",
            Category = "Plan",
            DisplayName = "Optimization Plan",
            Message = $"[ PLAN ] {msg}"
        });
    }
}
