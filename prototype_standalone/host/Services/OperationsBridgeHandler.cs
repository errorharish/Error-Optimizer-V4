using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Automation;
using BiosOptimizer.Core.Implementations.BiosSafe;
using BiosOptimizer.Core.Implementations.Cleaners;
using BiosOptimizer.Core.Implementations.Config;
using BiosOptimizer.Core.Implementations.Debloat;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Performance;
using BiosOptimizer.Core.Implementations.Power;
using BiosOptimizer.Core.Implementations.RegistryValues;
using BiosOptimizer.Core.Implementations.Services;
using BiosOptimizer.Core.Implementations.Storage;
using BiosOptimizer.Core.Implementations.Tools;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.V4.Coordinator;
using BiosOptimizer.Core.V4.Recommendations;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Detection;
using BiosOptimizer.IPC.Contracts;
using BiosOptimizer.Safety;
using BiosOptimizer.CLI.Adapters.Bios;
using BiosOptimizer.CLI.Adapters.Workloads;
using Microsoft.Extensions.DependencyInjection;
using StartupMgr = BiosOptimizer.Core.Implementations.Startup.WindowsStartupManager;
using StartupRegistrar = BiosOptimizer.Core.Implementations.Startup.WindowsStartupRegistrar;

namespace ErrorOptimizerPrototypeHost.Services;

/// <summary>
/// Central WebView2 host-to-backend dispatcher bridging <c>BiosOptimizer.Service</c> (Named Pipe v2 IPC),
/// <c>BiosOptimizer.Core</c>, <c>BiosOptimizer.Detection</c>, <c>BiosOptimizer.Safety</c>, and
/// <c>V4.EmulatorOptimization</c>.
/// </summary>
public sealed class OperationsBridgeHandler : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly Window _ownerWindow;
    private readonly Action<string> _postJson;
    private readonly ServiceIpcClient _ipc;
    private readonly SystemTelemetryService _telemetry;
    private readonly WorkloadMonitorBridge _workload;
    private readonly InputLatencySampler _inputSampler = new();
    private readonly BackupManager _backupManager = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _disposed;

    public static OperationsBridgeHandler? ActiveInstance { get; private set; }

    public void BroadcastGlobalConfig(UnifiedAppSettings? settings = null, object? autoOptState = null)
    {
        try
        {
            settings ??= UnifiedSettingsStore.LoadSafe();
            autoOptState ??= AutoOptimizeEngine.Instance.GetStateSnapshot();

            var payload = JsonSerializer.Serialize(new
            {
                type = "event",
                @event = "global-config:changed",
                config = settings,
                autoOptState = autoOptState
            }, JsonOptions);

            _postJson(payload);

            var settingsPayload = JsonSerializer.Serialize(new
            {
                type = "event",
                @event = "settings:changed",
                settings = settings
            }, JsonOptions);
            _postJson(settingsPayload);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BROADCAST CONFIG ERROR] {ex.Message}");
        }
    }

    public void NavigateToPage(string pageSlug)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "event",
                @event = "navigate:to",
                page = pageSlug
            }, JsonOptions);
            _postJson(payload);
        }
        catch { }
    }

    public OperationsBridgeHandler(Window ownerWindow, Action<string> postJson)
    {
        _ownerWindow = ownerWindow;
        _postJson = postJson;
        _ipc = new ServiceIpcClient();
        _telemetry = new SystemTelemetryService(_ipc, postJson);
        _workload = new WorkloadMonitorBridge(ownerWindow, postJson);
        ActiveInstance = this;
    }

    public bool CanHandleWindowlessAction(string action) =>
        action.Equals("window:open-external", StringComparison.OrdinalIgnoreCase) ||
        !action.StartsWith("window:", StringComparison.OrdinalIgnoreCase);

    public async Task HandleMessageAsync(string rawJson)
    {
        if (_disposed || string.IsNullOrWhiteSpace(rawJson))
            return;

        string? requestId = null;
        string? action = null;
        JsonElement root;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();

            if (!root.TryGetProperty("action", out var actionProp) || actionProp.ValueKind != JsonValueKind.String)
                return;

            action = actionProp.GetString();
            if (string.IsNullOrWhiteSpace(action))
                return;

            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                requestId = idProp.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        CancellationToken ct = _lifetimeCts.Token;

        try
        {
            switch (action)
            {
                // ── Fire-and-forget subscriptions ──
                case "telemetry:subscribe":
                {
                    int? interval = root.TryGetProperty("intervalMs", out var intEl) && intEl.TryGetInt32(out int ms)
                        ? ms
                        : null;
                    bool setInterval = root.TryGetProperty("setInterval", out var siEl) &&
                                       (siEl.ValueKind == JsonValueKind.True || siEl.ValueKind == JsonValueKind.False) &&
                                       siEl.GetBoolean();
                    _telemetry.Subscribe(interval, setInterval);
                    return;
                }
                case "telemetry:unsubscribe":
                    _telemetry.Unsubscribe();
                    return;

                // ── Core WebView2 adapter contracts (exact schemas) ──
                case "host:handshake":
                    await ReplyServiceHealthAsync(requestId ?? string.Empty, ct).ConfigureAwait(false);
                    return;

                case "service:health":
                    await ReplyServiceHealthAsync(requestId ?? string.Empty, ct).ConfigureAwait(false);
                    return;

                case "service:status":
                {
                    var statusPayload = await BuildHostHandshakePayloadAsync(ct).ConfigureAwait(false);
                    ReplyOk(requestId, statusPayload);
                    return;
                }

                case "service:capabilities":
                {
                    var outcome = await _ipc.SendDetailedAsync(IpcMessageType.GetCapabilities, null, 3000, ct).ConfigureAwait(false);
                    var hostPayload = await BuildHostHandshakePayloadAsync(ct).ConfigureAwait(false);
                    if (outcome.PipeReachable && outcome.Response.Success && !string.IsNullOrWhiteSpace(outcome.Response.Data))
                    {
                        ReplyOk(requestId, new
                        {
                            host = hostPayload,
                            serviceCapabilities = TryParseRawJsonOrString(outcome.Response.Data)
                        });
                    }
                    else
                    {
                        ReplyOk(requestId, hostPayload);
                    }
                    return;
                }

                case "system:scan":
                {
                    string scanId = requestId ?? $"scan-{Guid.NewGuid():N}";
                    var reply = await _telemetry.BuildScanReplyAsync(scanId, ct).ConfigureAwait(false);
                    _postJson(JsonSerializer.Serialize(reply, JsonOptions));
                    return;
                }

                // ── Workload Optimization live bridge ──
                case "workload:monitor-start":
                    _workload.StartMonitor();
                    ReplyOk(requestId, new { started = true });
                    return;

                case "workload:discover":
                {
                    var items = await _workload.DiscoverInstalledAsync(ct).ConfigureAwait(false);
                    ReplyOk(requestId, items);
                    return;
                }

                case "workload:pick-exe":
                {
                    string? path = await _workload.PickExecutableAsync().ConfigureAwait(false);
                    ReplyOk(requestId, path);
                    return;
                }

                case "workload:launch":
                {
                    string? path = GetPayloadString(root, "path");
                    var (ok, pid, error) = await _workload.LaunchAsync(path).ConfigureAwait(false);
                    if (ok) ReplyOk(requestId, new { pid });
                    else ReplyError(requestId, error ?? "Failed to launch workload.");
                    return;
                }

                case "workload:apply":
                {
                    if (!TryGetPayload(root, out var payload) ||
                        !payload.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid) ||
                        !payload.TryGetProperty("plan", out var planEl))
                    {
                        ReplyError(requestId, "workload:apply requires payload { pid, plan }.");
                        return;
                    }

                    var (ok, applied, error) = await _workload.ApplyPlanAsync(pid, planEl).ConfigureAwait(false);
                    if (ok) ReplyOk(requestId, new { applied });
                    else ReplyError(requestId, error ?? "Failed to apply workload optimization.");
                    return;
                }

                case "workload:focus":
                {
                    if (!TryGetPayload(root, out var payload) ||
                        !payload.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid))
                    {
                        ReplyError(requestId, "workload:focus requires payload { pid }.");
                        return;
                    }

                    var (ok, error) = await _workload.FocusProcessAsync(pid).ConfigureAwait(false);
                    if (ok) ReplyOk(requestId, new { focused = true });
                    else ReplyError(requestId, error ?? "Unable to focus process window.");
                    return;
                }

                case "workload:revert":
                {
                    if (!TryGetPayload(root, out var payload) ||
                        !payload.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid))
                    {
                        ReplyError(requestId, "workload:revert requires payload { pid }.");
                        return;
                    }

                    var (ok, error) = await _workload.RevertProcessAsync(pid).ConfigureAwait(false);
                    if (ok) ReplyOk(requestId, new { reverted = true });
                    else ReplyError(requestId, error ?? "Unable to revert workload optimization.");
                    return;
                }

                // ── Typed IPC-first + Direct Core/V4 routes for all V3 & V4 modules ──
                case "system:info":
                {
                    var sysInfo = await Task.Run(() => BuildStructuredSystemInfoPayload(), ct).ConfigureAwait(false);
                    ReplyOk(requestId, sysInfo);
                    return;
                }

                case "system:health":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.EvaluateSystemHealth,
                        null,
                        async () => (object)await new SystemHealthEngine().EvaluateSystemHealthAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "oneclick:discover":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.DiscoverOneClick,
                        null,
                        async () => (object)await new OneClickOptimizationEngine().DiscoverPlanAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "oneclick:execute":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"selectedIds\":[]}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ExecuteOneClick,
                        rawPayload,
                        async () =>
                        {
                            var selectedIds = ReadStringArray(root, "selectedIds");
                            bool hasExplicitSelection = !string.IsNullOrWhiteSpace(rawPayload) && rawPayload.Contains("\"selectedIds\"", StringComparison.OrdinalIgnoreCase);
                            var engine = new OneClickOptimizationEngine();
                            var plan = await engine.DiscoverPlanAsync(ct).ConfigureAwait(false);
                            if (hasExplicitSelection || selectedIds.Count > 0)
                            {
                                var selectedSet = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
                                foreach (var a in plan.Actions)
                                    a.IsSelected = selectedSet.Contains(a.Id);
                            }
                            return (object)await engine.ExecutePlanAsync(plan, progress: null, ct: ct).ConfigureAwait(false);
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tier:scan":
                {
                    string tierId = ProfileRepository.NormalizeProfileId(GetPayloadString(root, "tier") ?? GetPayloadString(root, "tierId") ?? "Normal");
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.PreviewTier,
                        tierId,
                        () =>
                        {
                            var uEngine = CreateUniversalEligibilityEngine();
                            return Task.FromResult<object>(uEngine.EvaluateTierProfile(tierId));
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tier:execute":
                {
                    string tierId = ProfileRepository.NormalizeProfileId(GetPayloadString(root, "tierId") ?? GetPayloadString(root, "tier") ?? "Normal");
                    var selected = ReadStringArray(root, "selectedItemIds");
                    string rawPayload = JsonSerializer.Serialize(new { tierId, selectedItemIds = selected });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyTier,
                        rawPayload,
                        async () =>
                        {
                            HashSet<string>? allowed = selected.Count > 0
                                ? new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase)
                                : null;
                            var uEngine = CreateUniversalEligibilityEngine();
                            var results = await uEngine.ExecutePlanAsync(tierId, allowed, dryRun: false, ct).ConfigureAwait(false);
                            return (object)results;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tier:debloat-scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetDebloatItems,
                        null,
                        async () => (object)await new DebloatEngine(_backupManager).DiscoverInstalledPackagesAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "tier:debloat-execute":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"selectedIds\":[]}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyDebloatItems,
                        rawPayload,
                        async () =>
                        {
                            var ids = ReadStringArray(root, "selectedIds");
                            return (object)await new DebloatEngine(_backupManager).RemoveSelectedPackagesAsync(ids, ct).ConfigureAwait(false);
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tier:debloat-get-blocklist":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetDebloatBlocklist,
                        null,
                        () => Task.FromResult<object>(new { blockedIds = DebloatBlocklistStore.LoadBlockedIds() }),
                        ct).ConfigureAwait(false);
                    return;

                case "tier:debloat-save-blocklist":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"blockedIds\":[]}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SaveDebloatBlocklist,
                        rawPayload,
                        () =>
                        {
                            var ids = ReadStringArray(root, "blockedIds");
                            if (ids.Count == 0) ids = ReadStringArray(root, "ids");
                            var saved = DebloatBlocklistStore.SaveBlockedIds(ids);
                            return Task.FromResult<object>(new { success = true, blockedIds = saved, count = saved.Count });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "bios:reboot-uefi":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"confirmed\":false}";
                    bool confirmed = TryGetPayload(root, out var p) &&
                                     p.TryGetProperty("confirmed", out var cEl) &&
                                     cEl.ValueKind == JsonValueKind.True;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RequestUefiReboot,
                        rawPayload,
                        () =>
                        {
                            if (!confirmed)
                                throw new InvalidOperationException("Explicit confirmation (confirmed=true) is required before rebooting to UEFI firmware.");
                            if (!BiosSafeEngine.Instance.RequestDirectUefiReboot(out string msg))
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? "UEFI firmware reboot unavailable." : msg);
                            return Task.FromResult<object>(new { success = true, message = msg });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tier:biossafe-report":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetBiosCapabilities,
                        null,
                        async () => (object)await BiosSafeEngine.Instance.GenerateFirmwareGuidanceCardsAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "input:scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.PlanInputOptimization,
                        null,
                        async () => (object)await new InputOptimizerEngine().PlanInputOptimizationAsync().ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "input:apply":
                {
                    string risk = GetPayloadString(root, "riskLevel") ?? "Safe";
                    var selectedIds = ReadStringArray(root, "selectedIds");
                    string payloadJson = JsonSerializer.Serialize(new { riskLevel = risk, selectedIds });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyInputOptimization,
                        payloadJson,
                        async () =>
                        {
                            var engine = new InputOptimizerEngine();
                            var planBefore = await engine.PlanInputOptimizationAsync().ConfigureAwait(false);
                            var res = await engine.ApplyInputOptimizationAsync(payloadJson).ConfigureAwait(false);
                            var planAfter = await engine.PlanInputOptimizationAsync().ConfigureAwait(false);

                            var allActionsBefore = (planBefore.Actions ?? Enumerable.Empty<InputOptimizationAction>())
                                .Concat(planBefore.ExcludedActions ?? Enumerable.Empty<InputOptimizationAction>())
                                .ToList();

                            var allActionsAfter = (planAfter.Actions ?? Enumerable.Empty<InputOptimizationAction>())
                                .Concat(planAfter.ExcludedActions ?? Enumerable.Empty<InputOptimizationAction>())
                                .ToList();

                            var expandedIds = BiosOptimizer.Core.Implementations.InputOptimizerEngine.ExpandSelectedIds(selectedIds);
                            var targetList = (selectedIds.Count > 0)
                                ? allActionsAfter.Where(a => expandedIds.Contains(a.Id) || selectedIds.Any(sid => sid.Equals(a.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(BiosOptimizer.Core.Implementations.InputOptimizerEngine.ResolveFrontendAlias(a.Id), sid, StringComparison.OrdinalIgnoreCase))).ToList()
                                : allActionsAfter;

                            var ops = targetList.Select(a =>
                            {
                                var beforeAct = allActionsBefore.FirstOrDefault(b => b.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase));
                                bool wasAlready = beforeAct != null && (beforeAct.AlreadyOptimized || beforeAct.Status == "OPTIMIZED" || beforeAct.Status == "ALREADY_OPTIMIZED" || beforeAct.SupportState == "ALREADY_OPTIMAL" || beforeAct.ExecutionState == "ALREADY_OPTIMAL");
                                bool isDone = a.AlreadyOptimized || a.Status == "OPTIMIZED" || a.Status == "VERIFIED" || a.Status == "RESTART_REQUIRED" || a.SupportState == "ALREADY_OPTIMAL" || a.ExecutionState == "ALREADY_OPTIMAL" || a.ExecutionState == "SUCCESS";
                                bool isIneligible = a.Status == "NOT_SUPPORTED" || a.Status == "NOT_AVAILABLE" || a.SupportState == "NOT_SUPPORTED" || a.SupportState == "NOT_AVAILABLE" || a.Applicable == false;
                                string requestedId = selectedIds.FirstOrDefault(sid => sid.Equals(a.Id, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(BiosOptimizer.Core.Implementations.InputOptimizerEngine.ResolveFrontendAlias(a.Id), sid, StringComparison.OrdinalIgnoreCase)) ?? a.Id;

                                string status = wasAlready ? "succeeded" : (isDone ? "succeeded" : (isIneligible ? "skipped" : "failed"));
                                string canonicalStatus = wasAlready ? "ALREADY_OPTIMAL" : (isDone ? "APPLIED" : (isIneligible ? "INELIGIBLE" : "FAILED"));

                                return new
                                {
                                    id = requestedId,
                                    canonicalId = a.Id,
                                    title = a.Name,
                                    status,
                                    isAlreadyOptimized = wasAlready,
                                    alreadyOptimized = wasAlready,
                                    canonicalStatus,
                                    currentValue = a.CurrentValue,
                                    targetValue = a.TargetValue,
                                    applied = !wasAlready && isDone,
                                    detail = wasAlready ? "ALREADY_OPTIMAL" : (isDone ? "VERIFIED" : (a.Reason ?? (isIneligible ? "Not supported" : "Operation failed"))),
                                    verification = wasAlready
                                        ? $"Already configured to optimal target value ({a.CurrentValue})"
                                        : (isDone ? $"Confirmed via Windows SPI/Registry readback: {a.CurrentValue}" : (a.Reason ?? "Not verified")),
                                    reason = wasAlready
                                        ? $"Value already optimal: current value '{a.CurrentValue}' matches target '{a.TargetValue}'"
                                        : (isDone ? "Optimization applied and verified" : (a.Reason ?? "Operation failed"))
                                };
                            }).ToList();

                            bool overallSuccess = res.Success || (ops.Count > 0 && ops.All(o => o.status == "succeeded" || o.status == "skipped"));
                            string overallMessage = overallSuccess
                                ? (ops.All(o => o.isAlreadyOptimized)
                                    ? "All input optimizations are already optimal."
                                    : "Input optimizations applied and verified successfully.")
                                : (res.Message ?? "Input optimization completed.");

                            return (object)new
                            {
                                success = overallSuccess,
                                message = overallMessage,
                                operations = ops
                            };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "input:restore":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreInputOptimization,
                        null,
                        async () =>
                        {
                            var res = await new InputOptimizerEngine().RestoreInputOptimizationAsync().ConfigureAwait(false);
                            return (object)new { success = res.Success, message = res.Message };
                        },
                        ct).ConfigureAwait(false);
                    return;

                // ── Phase 6 (NEW-BE-004): Interactive Desktop Win32 Raw Input Sampler ──
                case "input:latency-test":
                {
                    int duration = TryGetPayload(root, out var p) && p.TryGetProperty("durationSeconds", out var dEl) && dEl.TryGetInt32(out int s)
                        ? s
                        : 5;
                    var result = await _inputSampler.RunSamplingAsync(duration, ct).ConfigureAwait(false);
                    ReplyOk(requestId, result);
                    return;
                }

                case "input:latency-cancel":
                    ReplyOk(requestId, new { cancelled = _inputSampler.CancelCurrentTest() });
                    return;

                case "registry:tweaks-scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.PlanRegistryTweak,
                        null,
                        () =>
                        {
                            var uEngine = CreateUniversalEligibilityEngine();
                            return Task.FromResult<object>(uEngine.EvaluateRegistryIntelligence());
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "registry:tweaks-apply":
                {
                    var ids = ReadStringArray(root, "ids");
                    if (ids.Count == 0 && root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in root.EnumerateArray())
                        {
                            var s = elem.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                        }
                    }
                    string rawPayload = JsonSerializer.Serialize(ids);
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyRegistryTweak,
                        rawPayload,
                        async () =>
                        {
                            var uEngine = CreateUniversalEligibilityEngine();
                            var r = await uEngine.ExecutePlanAsync("RegistryIntelligence", new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase), dryRun: false, ct).ConfigureAwait(false);
                            return (object)r;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "registry:tweaks-restore":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "[]";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreRegistryTweak,
                        rawPayload,
                        async () =>
                        {
                            var ids = ReadStringArray(root, "ids");
                            var r = await new RegistryTweakEngine(_backupManager).RestoreRegistryTweaksAsync(ids).ConfigureAwait(false);
                            return (object)new { r.Success, r.Message, r.Status, r.RestoredCount, r.FailedCount };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "registry:ram-scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetRamRegistryValues,
                        null,
                        () => Task.FromResult<object>(MachineRegistryValueEngine.Instance.ScanAllValues()),
                        ct).ConfigureAwait(false);
                    return;

                case "registry:ram-apply":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyRamRegistryValue,
                        rawPayload,
                        () =>
                        {
                            string id = GetPayloadString(root, "id") ?? string.Empty;
                            long? kb = TryGetPayload(root, out var p) && p.TryGetProperty("kb", out var kbEl) && kbEl.TryGetInt64(out long kbVal)
                                ? kbVal
                                : (long?)null;
                            double? ramGb = TryGetPayload(root, out var p2) && p2.TryGetProperty("ramGb", out var ramEl) && ramEl.TryGetDouble(out double rVal)
                                ? rVal
                                : (double?)null;

                            if (!MachineRegistryValueEngine.Instance.ApplyRegistryValueWithSelection(id, kb, ramGb, out var item, out string msg) || item == null)
                                throw new InvalidOperationException(msg);
                            return Task.FromResult<object>(new { item, message = msg });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "registry:ram-revert":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RollbackRamRegistryValue,
                        rawPayload,
                        () =>
                        {
                            string id = GetPayloadString(root, "id") ?? string.Empty;
                            var item = MachineRegistryValueEngine.Instance.ScanAllValues()
                                .FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                            if (item == null) throw new InvalidOperationException($"RAM registry entry '{id}' not found.");
                            if (!MachineRegistryValueEngine.Instance.RollbackRegistryValue(item, out string msg))
                                throw new InvalidOperationException(msg);
                            return Task.FromResult<object>(new { item, message = msg });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "registry:gpu-scan":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"profile\":\"Normal\"}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetGpuRegistryValues,
                        rawPayload,
                        () =>
                        {
                            string profile = GetPayloadString(root, "profile") ?? "Normal";
                            var hw = GpuRegistryValueEngine.Instance.DetectGpuHardware();
                            var items = GpuRegistryValueEngine.Instance.ScanAllGpuOptimizations(profile, forceRescan: true);
                            return Task.FromResult<object>(new { hardware = hw, items });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "registry:gpu-apply":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyGpuRegistryValue,
                        rawPayload,
                        () =>
                        {
                            string id = GetPayloadString(root, "id") ?? string.Empty;
                            string profile = GetPayloadString(root, "profile") ?? "Normal";
                            var item = GpuRegistryValueEngine.Instance.FindOptimization(id, profile);
                            if (item == null)
                                return Task.FromResult<object>(new { success = false, message = $"GPU registry entry '{id}' not found.", status = "NOT_AVAILABLE" });
                            if (!item.IsApplicable)
                                return Task.FromResult<object>(new { success = false, message = item.WhyApplicable ?? "Not applicable on this hardware.", status = "NOT_APPLICABLE" });
                            var (ok, msg) = GpuRegistryValueEngine.Instance.ApplyOptimization(item);
                            return Task.FromResult<object>(new { success = ok, item, message = msg, status = ok ? "SUCCESS" : "FAILED" });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "registry:gpu-revert":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RollbackGpuRegistryValue,
                        rawPayload,
                        () =>
                        {
                            string id = GetPayloadString(root, "id") ?? string.Empty;
                            string profile = GetPayloadString(root, "profile") ?? "Normal";
                            var item = GpuRegistryValueEngine.Instance.FindOptimization(id, profile);
                            if (item == null)
                                return Task.FromResult<object>(new { success = false, message = $"GPU registry entry '{id}' not found.", status = "NOT_AVAILABLE" });
                            var (ok, msg) = GpuRegistryValueEngine.Instance.RestoreOptimization(item);
                            return Task.FromResult<object>(new { success = ok, item, message = msg, status = ok ? "SUCCESS" : "FAILED" });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "startup:scan":
                {
                    var hostItems = new StartupMgr().GetStartupEntries();
                    var outcome = await _ipc.SendDetailedAsync(IpcMessageType.GetStartupItems, null, 5000, ct).ConfigureAwait(false);
                    if (outcome.PipeReachable && outcome.Response.Success && !string.IsNullOrWhiteSpace(outcome.Response.Data))
                    {
                        try
                        {
                            var svcItems = JsonSerializer.Deserialize<List<StartupEntryState>>(outcome.Response.Data, JsonOptions);
                            if (svcItems != null && svcItems.Count > 0)
                            {
                                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var merged = new List<StartupEntryState>();
                                foreach (var item in hostItems)
                                {
                                    string key = !string.IsNullOrEmpty(item.Id) ? item.Id : $"{item.Source}|{item.ValueName}";
                                    if (seen.Add(key))
                                        merged.Add(item);
                                }
                                foreach (var item in svcItems)
                                {
                                    string key = !string.IsNullOrEmpty(item.Id) ? item.Id : $"{item.Source}|{item.ValueName}";
                                    if (seen.Add(key))
                                        merged.Add(item);
                                }
                                ReplyOk(requestId, merged);
                                return;
                            }
                        }
                        catch { }
                    }
                    ReplyOk(requestId, hostItems);
                    return;
                }

                case "startup:toggle":
                {
                    string? rawPayload = ExtractPayloadJson(root);
                    bool enable = root.TryGetProperty("payload", out var pEl) &&
                                  pEl.TryGetProperty("enable", out var enEl) && enEl.ValueKind == JsonValueKind.True;

                    string currentUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(currentUserSid) && !string.IsNullOrEmpty(rawPayload) && !rawPayload.Contains("targetSid", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(rawPayload);
                            if (dict != null)
                            {
                                dict["targetSid"] = currentUserSid;
                                rawPayload = JsonSerializer.Serialize(dict);
                            }
                        }
                        catch { }
                    }

                    await RouteIpcWithFallbackAsync(
                        requestId,
                        enable ? IpcMessageType.EnableStartupItem : IpcMessageType.DisableStartupItem,
                        rawPayload,
                        () =>
                        {
                            string id = GetPayloadString(root, "id") ?? string.Empty;
                            string src = GetPayloadString(root, "source") ?? string.Empty;
                            string path = GetPayloadString(root, "path") ?? string.Empty;
                            string name = GetPayloadString(root, "name") ?? string.Empty;
                            string valueName = GetPayloadString(root, "valueName") ?? name;
                            var mgr = new StartupMgr();
                            bool ok = enable ? mgr.EnableEntry(src, path, valueName, id) : mgr.DisableEntry(src, path, valueName, id);
                            if (!ok)
                                throw new InvalidOperationException($"Failed to {(enable ? "enable" : "disable")} startup item '{(string.IsNullOrEmpty(name) ? id : name)}' (administrator permission may be required).");
                            return Task.FromResult<object>(new {
                                ok = true,
                                success = true,
                                id,
                                state = enable ? "Enabled" : "Disabled",
                                isEnabled = enable,
                                name = string.IsNullOrEmpty(name) ? valueName : name,
                                message = $"Successfully {(enable ? "enabled" : "disabled")} startup item."
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "services:scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetServices,
                        null,
                        () => Task.FromResult<object>(new WindowsServiceManager().GetAllServices()),
                        ct).ConfigureAwait(false);
                    return;

                case "services:set-startup":
                {
                    string serviceName = GetPayloadString(root, "ServiceName") ?? GetPayloadString(root, "serviceName") ?? string.Empty;
                    string stateText = GetPayloadString(root, "State") ?? GetPayloadString(root, "startupType") ?? GetPayloadString(root, "state") ?? string.Empty;
                    string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { ServiceName = serviceName, State = stateText });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SetServiceStartupType,
                        rawPayload,
                        () =>
                        {
                            if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(stateText))
                                throw new ArgumentException("ServiceName and startup State are required.");
                            if (!Enum.TryParse<ServiceStartupType>(stateText, true, out var parsedStartup))
                                throw new ArgumentException($"Unsupported service startup type '{stateText}'.");
                            bool ok = new WindowsServiceManager().SetStartupType(serviceName, parsedStartup);
                            if (!ok)
                                throw new InvalidOperationException($"Failed to set startup type '{stateText}' for service '{serviceName}' (check permissions or protected service policy).");
                            return Task.FromResult<object>(new { success = true, serviceName, startupType = parsedStartup.ToString() });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "services:change-state":
                {
                    string serviceName = GetPayloadString(root, "ServiceName") ?? GetPayloadString(root, "serviceName") ?? string.Empty;
                    string actionState = GetPayloadString(root, "State") ?? GetPayloadString(root, "action") ?? GetPayloadString(root, "state") ?? string.Empty;
                    string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { ServiceName = serviceName, State = actionState });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ChangeServiceState,
                        rawPayload,
                        () =>
                        {
                            if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(actionState))
                                throw new ArgumentException("ServiceName and State are required.");
                            var mgr = new WindowsServiceManager();
                            bool ok = actionState.ToUpperInvariant() switch
                            {
                                "START" => mgr.Start(serviceName),
                                "STOP" => mgr.Stop(serviceName),
                                "RESTART" => mgr.Restart(serviceName),
                                _ => throw new ArgumentException($"Unsupported service action '{actionState}'.")
                            };
                            if (!ok)
                                throw new InvalidOperationException($"Failed to {actionState.ToLowerInvariant()} service '{serviceName}' (administrator privilege or dependency constraint may apply).");
                            return Task.FromResult<object>(new { success = true, serviceName, state = actionState });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "network:scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.PlanNetworkOptimization,
                        ExtractPayloadJson(root),
                        async () => (object)await new NetworkOptimizerEngine(_backupManager, new NetworkDiagnosticsService()).PlanNetworkOptimizationAsync().ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "network:apply":
                {
                    string actionId = GetPayloadString(root, "id") ?? ExtractPayloadJson(root) ?? string.Empty;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyNetworkOptimization,
                        actionId,
                        async () =>
                        {
                            var r = await new NetworkOptimizerEngine(_backupManager, new NetworkDiagnosticsService()).ApplyNetworkActionAsync(actionId).ConfigureAwait(false);
                            return (object)new { success = r.Success, message = r.Message, verifiedState = r.VerifiedState, id = actionId, name = actionId };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "network:apply-quick":
                {
                    var netEngine = new NetworkOptimizerEngine(_backupManager, new NetworkDiagnosticsService());
                    var tasksList = new List<object>();

                    // 1. Throttling
                    var t1 = await netEngine.ApplyNetworkActionAsync("net.mmcss.networkthrottling").ConfigureAwait(false);
                    tasksList.Add(new
                    {
                        id = "network-0",
                        taskId = "net.mmcss.networkthrottling",
                        name = "Disable Multimedia Network Throttling Index",
                        outcome = t1.Success ? "Completed" : "Failed",
                        verified = t1.Success,
                        reason = t1.Message
                    });

                    // 2. Auto-tuning
                    var t2 = await netEngine.ApplyNetworkActionAsync("net.tcp.autotuning").ConfigureAwait(false);
                    tasksList.Add(new
                    {
                        id = "network-1",
                        taskId = "net.tcp.autotuning",
                        name = "TCP Window Auto-Tuning Calibration (Normal)",
                        outcome = t2.Success ? "Completed" : "Failed",
                        verified = t2.Success,
                        reason = t2.Message
                    });

                    // 3. DNS Cache flush
                    var t3 = await netEngine.FlushDnsCacheAsync().ConfigureAwait(false);
                    tasksList.Add(new
                    {
                        id = "network-2",
                        taskId = "sys-dns",
                        name = "Flush & Register System DNS Resolver Cache",
                        outcome = t3.Success ? "Completed" : "Failed",
                        verified = t3.Success,
                        reason = t3.Message
                    });

                    bool overallOk = t1.Success && t2.Success && t3.Success;
                    ReplyOk(requestId, new
                    {
                        success = overallOk,
                        message = overallOk ? "Network Boost applied and verified." : "Some network optimizations could not be applied.",
                        tasks = tasksList
                    });
                    return;
                }

                case "network:flush-dns":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.FlushDnsCache,
                        null,
                        async () =>
                        {
                            var r = await new NetworkOptimizerEngine(_backupManager, new NetworkDiagnosticsService()).FlushDnsCacheAsync().ConfigureAwait(false);
                            return (object)new { r.Success, r.Message, r.Timestamp };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "network:restore":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreNetworkOptimization,
                        null,
                        async () =>
                        {
                            var r = await new NetworkOptimizerEngine(_backupManager, new NetworkDiagnosticsService()).RestoreNetworkOptimizationAsync().ConfigureAwait(false);
                            return (object)new { r.Success, r.Message };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "network:diagnostics":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RunNetworkDiagnostics,
                        null,
                        async () => (object)await new NetworkOptimizerEngine(_backupManager, new NetworkDiagnosticsService()).RunDiagnosticsAsync(null, ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "storage:drives":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetStorageStatus,
                        null,
                        () => Task.FromResult<object>(new StorageEngine().GetDrives()),
                        ct).ConfigureAwait(false);
                    return;

                case "storage:scan-cleanup":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ScanStorageCategories,
                        ExtractPayloadJson(root),
                        async () => (object)await new StorageEngine().DiscoverStorageCleanupOpportunitiesAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "storage:run-cleanup":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "[]";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyStorageCleanup,
                        rawPayload,
                        async () =>
                        {
                            var ids = ReadStringArray(root, "categoryIds");
                            var r = await new StorageEngine().ExecuteSafeStorageCleanupAsync(ids, ct).ConfigureAwait(false);
                            return (object)new { r.Success, r.ReclaimedBytes, r.Message };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "storage:trim":
                {
                    string drive = GetPayloadString(root, "drive") ?? "C:";
                    var r = await new StorageEngine().InvokeDriveReTrimAsync(drive).ConfigureAwait(false);
                    ReplyOk(requestId, new { r.Success, r.Message });
                    return;
                }

                case "storage:enable-trim":
                {
                    var r = await new StorageEngine().EnableTrimAsync().ConfigureAwait(false);
                    ReplyOk(requestId, new { r.Success, r.Message });
                    return;
                }

                case "storage:recycle-info":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetRecycleBinInfo,
                        null,
                        () =>
                        {
                            var (sizeBytes, count) = new StorageCleanerEngine().GetRecycleBinInfo();
                            return Task.FromResult<object>(new
                            {
                                sizeBytes,
                                formattedSize = StorageCleanupCategory.FormatBytes(sizeBytes),
                                itemCount = count
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "storage:empty-recycle":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.EmptyRecycleBin,
                        null,
                        () =>
                        {
                            var (success, freedBytes, message) = new StorageCleanerEngine().EmptyRecycleBin();
                            if (!success) throw new InvalidOperationException(message);
                            return Task.FromResult<object>(new
                            {
                                success,
                                freedBytes,
                                formattedFreed = StorageCleanupCategory.FormatBytes(freedBytes),
                                message
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "storage:scan-large":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ScanLargeFiles,
                        ExtractPayloadJson(root),
                        () =>
                        {
                            string drive = GetPayloadString(root, "drive") ?? "C:\\";
                            long minBytes = 50L * 1024L * 1024L;
                            if (TryGetPayload(root, out var p) && p.TryGetProperty("minSizeMb", out var mbEl) && mbEl.TryGetInt64(out long mb))
                                minBytes = Math.Max(1, mb) * 1024L * 1024L;
                            return Task.FromResult<object>(new StorageCleanerEngine().FindLargeFiles(drive, minBytes, ct));
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "storage:scan-duplicates":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ScanDuplicateFiles,
                        ExtractPayloadJson(root),
                        () =>
                        {
                            string path = GetPayloadString(root, "path") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                            long minBytes = 1024L * 1024L;
                            return Task.FromResult<object>(new StorageCleanerEngine().FindDuplicates(path, minBytes, ct));
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "storage:delete-files":
                {
                    var paths = ReadStringArray(root, "paths");
                    bool toRecycle = !TryGetPayload(root, out var p) ||
                                     !p.TryGetProperty("permanent", out var perm) ||
                                     perm.ValueKind != JsonValueKind.True;
                    var r = new StorageCleanerEngine().DeleteStorageFiles(paths, toRecycle);
                    ReplyOk(requestId, r);
                    return;
                }

                case "storage:reveal-file":
                {
                    string? path = GetPayloadString(root, "path");
                    var outcome = SystemPowerOperations.RevealInExplorer(path);
                    if (outcome.Success) ReplyOk(requestId, outcome.Data);
                    else ReplyError(requestId, outcome.Message);
                    return;
                }

                case "system:schedule-restart":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"confirmed\":false}";
                    bool confirmed = TryGetPayload(root, out var p) &&
                                     p.TryGetProperty("confirmed", out var cEl) &&
                                     cEl.ValueKind == JsonValueKind.True;
                    int delay = TryGetPayload(root, out var p2) &&
                                p2.TryGetProperty("delaySeconds", out var dEl) &&
                                dEl.TryGetInt32(out int sec) ? sec : 60;
                    string? reason = GetPayloadString(root, "reason");
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ScheduleSystemRestart,
                        rawPayload,
                        () =>
                        {
                            var outcome = SystemPowerOperations.ScheduleRestart(delay, confirmed, reason);
                            if (!outcome.Success) throw new InvalidOperationException(outcome.Message);
                            return Task.FromResult<object>(new { outcome.Success, outcome.Message, outcome.Data });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "system:cancel-restart":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CancelScheduledRestart,
                        null,
                        () =>
                        {
                            var outcome = SystemPowerOperations.CancelScheduledRestart();
                            if (!outcome.Success) throw new InvalidOperationException(outcome.Message);
                            return Task.FromResult<object>(new { outcome.Success, outcome.Message });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "about:audit":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.EvaluateAboutCatalog,
                        null,
                        async () =>
                        {
                            var snap = await _ipc.CheckHealthAsync(ct).ConfigureAwait(false);
                            return SystemPowerOperations.EvaluateAboutCatalogAudit(snap.Healthy, snap.ServiceVersion);
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "window:open-external":
                {
                    string? target = GetPayloadString(root, "channel") ?? GetPayloadString(root, "url");
                    var outcome = SystemPowerOperations.OpenApprovedChannelUrl(target);
                    if (outcome.Success) ReplyOk(requestId, outcome.Data);
                    else ReplyError(requestId, outcome.Message);
                    return;
                }

                // ── Phase 2 (NEW-BE-007): Unified Settings, Startup Status & Audit Logs ──
                // ── Global Config & Synchronization ──
                case "global-config:get":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetUnifiedSettings,
                        null,
                        () =>
                        {
                            var config = UnifiedSettingsStore.LoadSafe();
                            var autoOptState = AutoOptimizeEngine.Instance.GetStateSnapshot();
                            var startupInfo = StartupRegistrar.Instance.GetStartupInfo();
                            return Task.FromResult<object>(new
                            {
                                config,
                                autoOptState,
                                startupInfo = new
                                {
                                    entryName = startupInfo.EntryName,
                                    isEnabled = startupInfo.IsEnabled,
                                    status = startupInfo.Status.ToString(),
                                    displayStatus = startupInfo.DisplayStatus,
                                    detailMessage = startupInfo.DetailMessage,
                                    exePath = startupInfo.ExePath,
                                    arguments = startupInfo.Arguments
                                }
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "global-config:save":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    var prior = UnifiedSettingsStore.LoadSafe();
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SaveUnifiedSettings,
                        rawPayload,
                        async () =>
                        {
                            using var doc = JsonDocument.Parse(rawPayload);
                            var saved = UnifiedSettingsStore.MergeAndSave(doc.RootElement, Environment.ProcessPath);
                            if (saved.StartWithWindows != prior.StartWithWindows ||
                                saved.StartMinimized != prior.StartMinimized ||
                                saved.StartWithWindows)
                            {
                                var tx = StartupRegistrar.Instance.ConfigureStartupTransactional(
                                    saved.StartWithWindows,
                                    saved.StartMinimized,
                                    Environment.ProcessPath);
                                if (!tx.Success)
                                    saved = UnifiedSettingsStore.LoadSafe();
                            }

                            AutoOptimizeEngine.Instance.SyncWithGlobalConfig(saved);

                            var reconcile = await AiPolicyReconciler.ReconcileAsync(
                                saved,
                                managePowerPlan: true,
                                hostRevertWorkloadAsync: () => _workload.StopAndRevertAllAsync(),
                                ct: ct).ConfigureAwait(false);

                            AuditLogManager.Write("Settings", "SaveGlobalConfig",
                                reconcile.AllSucceeded ? "OK" : "Warning",
                                $"Saved global config (autoOpt={saved.AutoOptimizationEnabled}, ai={saved.AiOptimizationEnabled}, startup={saved.StartWithWindows}, tray={saved.RunInSystemTray}).",
                                detailedOnly: reconcile.AllSucceeded);

                            BroadcastGlobalConfig(saved);
                            HostBackgroundSupervisor.Instance.RefreshTrayStatus();
                            return (object)new { success = true, config = saved, autoOptState = AutoOptimizeEngine.Instance.GetStateSnapshot() };
                        },
                        ct).ConfigureAwait(false);
                    await SyncHostBridgesToSavedPoliciesAsync().ConfigureAwait(false);
                    return;
                }

                case "global-config:toggle-auto-opt":
                {
                    var s = UnifiedSettingsStore.LoadSafe();
                    s.AutoOptimizationEnabled = !s.AutoOptimizationEnabled;
                    s = UnifiedSettingsStore.Save(s);
                    AutoOptimizeEngine.Instance.SyncWithGlobalConfig(s);
                    BroadcastGlobalConfig(s);
                    HostBackgroundSupervisor.Instance.RefreshTrayStatus();
                    ReplyOk(requestId, new
                    {
                        autoOptimizationEnabled = s.AutoOptimizationEnabled,
                        config = s,
                        autoOptState = AutoOptimizeEngine.Instance.GetStateSnapshot()
                    });
                    return;
                }

                case "global-config:toggle-ai":
                {
                    var s = UnifiedSettingsStore.LoadSafe();
                    s.AiOptimizationEnabled = !s.AiOptimizationEnabled;
                    s.AiEnabled = s.AiOptimizationEnabled;
                    s = UnifiedSettingsStore.Save(s);
                    AutoOptimizeEngine.Instance.SyncWithGlobalConfig(s);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AiPolicyReconciler.ReconcileAsync(s, managePowerPlan: true, hostRevertWorkloadAsync: () => _workload.StopAndRevertAllAsync(), ct: ct).ConfigureAwait(false);
                        }
                        catch { }
                    });
                    BroadcastGlobalConfig(s);
                    HostBackgroundSupervisor.Instance.RefreshTrayStatus();
                    ReplyOk(requestId, new
                    {
                        aiOptimizationEnabled = s.AiOptimizationEnabled,
                        config = s,
                        autoOptState = AutoOptimizeEngine.Instance.GetStateSnapshot()
                    });
                    return;
                }

                case "settings:get":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetUnifiedSettings,
                        null,
                        () => Task.FromResult<object>(UnifiedSettingsStore.LoadSafe()),
                        ct).ConfigureAwait(false);
                    return;

                case "settings:save":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    var prior = UnifiedSettingsStore.LoadSafe();
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SaveUnifiedSettings,
                        rawPayload,
                        async () =>
                        {
                            using var doc = JsonDocument.Parse(rawPayload);
                            var saved = UnifiedSettingsStore.MergeAndSave(doc.RootElement, Environment.ProcessPath);
                            if (saved.StartWithWindows != prior.StartWithWindows ||
                                saved.StartMinimized != prior.StartMinimized ||
                                saved.StartWithWindows)
                            {
                                var tx = StartupRegistrar.Instance.ConfigureStartupTransactional(
                                    saved.StartWithWindows,
                                    saved.StartMinimized,
                                    Environment.ProcessPath);
                                if (!tx.Success)
                                    saved = UnifiedSettingsStore.LoadSafe();
                            }

                            var reconcile = await AiPolicyReconciler.ReconcileAsync(
                                saved,
                                managePowerPlan: true,
                                hostRevertWorkloadAsync: () => _workload.StopAndRevertAllAsync(),
                                ct: ct).ConfigureAwait(false);

                            AuditLogManager.Write("Settings", "SaveUnifiedSettings",
                                reconcile.AllSucceeded ? "OK" : "Warning",
                                $"Saved settings (ai={saved.AiEnabled}, power={saved.Policies.PowerPlan}, workload={saved.Policies.Workload}, autoOpt={saved.Policies.AutoOptimize}, startup={saved.StartWithWindows}).",
                                detailedOnly: reconcile.AllSucceeded);
                            return (object)saved;
                        },
                        ct).ConfigureAwait(false);

                    // Synchronize in-process UI monitor bridges when Service processed the settings update
                    await SyncHostBridgesToSavedPoliciesAsync().ConfigureAwait(false);
                    return;
                }

                case "settings:reset-defaults":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ResetUnifiedSettings,
                        null,
                        async () =>
                        {
                            var reset = UnifiedSettingsStore.ResetDefaults();
                            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                            {
                                reset.HostExecutablePath = Environment.ProcessPath;
                                reset = UnifiedSettingsStore.Save(reset);
                            }
                            StartupRegistrar.Instance.ConfigureStartupTransactional(
                                reset.StartWithWindows,
                                reset.StartMinimized,
                                Environment.ProcessPath);
                            await AiPolicyReconciler.ReconcileAsync(
                                reset,
                                managePowerPlan: true,
                                hostRevertWorkloadAsync: () => _workload.StopAndRevertAllAsync(),
                                ct: ct).ConfigureAwait(false);
                            var confirmed = UnifiedSettingsStore.LoadSafe();
                            AuditLogManager.Write("Settings", "ResetUnifiedSettings", "OK", "Restored default settings and reconciled AI policies.");
                            return (object)confirmed;
                        },
                        ct).ConfigureAwait(false);
                    await SyncHostBridgesToSavedPoliciesAsync().ConfigureAwait(false);
                    return;

                case "settings:startup-status":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetStartupRegistrationStatus,
                        null,
                        () =>
                        {
                            var info = StartupRegistrar.Instance.GetStartupInfo();
                            string resolved = StartupRegistrar.ResolveInstalledOrLocalExePath(Environment.ProcessPath);
                            return Task.FromResult<object>(new
                            {
                                entryName = info.EntryName,
                                isEnabled = info.IsEnabled,
                                status = info.Status.ToString(),
                                displayStatus = info.DisplayStatus,
                                detailMessage = info.DetailMessage,
                                sourceKey = info.SourceKey,
                                exePath = string.IsNullOrWhiteSpace(info.ExePath) ? resolved : info.ExePath,
                                arguments = info.Arguments,
                                executableExists = !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved)
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "settings:set-startup":
                {
                    bool enable = TryGetPayload(root, out var p) && p.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.True;
                    bool minimized = TryGetPayload(root, out var p2) && p2.TryGetProperty("startMinimized", out var sm) && sm.ValueKind == JsonValueKind.True;
                    string? exeHint = Environment.ProcessPath;
                    string ipcPayload = JsonSerializer.Serialize(new
                    {
                        enable,
                        startMinimized = minimized,
                        exePath = exeHint
                    });

                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SetStartupRegistration,
                        ipcPayload,
                        () =>
                        {
                            var tx = StartupRegistrar.Instance.ConfigureStartupTransactional(enable, minimized, exeHint);
                            if (!tx.Success)
                            {
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(tx.ErrorMessage)
                                    ? "Failed to update Windows startup registration."
                                    : tx.ErrorMessage);
                            }

                            return Task.FromResult<object>(new
                            {
                                success = tx.Success,
                                isEnabled = tx.IsEnabled,
                                status = tx.Info.Status.ToString(),
                                displayStatus = tx.Info.DisplayStatus,
                                detailMessage = tx.Info.DetailMessage,
                                exePath = tx.ExePath
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "settings:logs-export":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"maxEntries\":300}";
                    int max = TryGetPayload(root, out var p) && p.TryGetProperty("maxEntries", out var mx) && mx.TryGetInt32(out int v) ? v : 300;
                    string? targetPath = GetPayloadString(root, "targetPath");
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ExportAuditLogs,
                        rawPayload,
                        () =>
                        {
                            var (text, count, exportedPath) = AuditLogManager.ExportLogs(max, targetPath);
                            return Task.FromResult<object>(new { entryCount = count, exportedPath, text });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "settings:logs-copy":
                {
                    var (text, count, exportedPath) = AuditLogManager.ExportLogs(300);
                    bool copied = false;
                    string? copyError = null;
                    await _ownerWindow.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            Clipboard.SetText(text);
                            copied = true;
                        }
                        catch (Exception ex)
                        {
                            copyError = ex.Message;
                        }
                    });
                    ReplyOk(requestId, new { copied, error = copyError, entryCount = count, exportedPath, text });
                    return;
                }

                case "settings:purge-logs":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    var current = UnifiedSettingsStore.LoadSafe();
                    int days = TryGetPayload(root, out var p) && p.TryGetProperty("retentionDays", out var dEl) && dEl.TryGetInt32(out int d) ? d : current.LogRetentionDays;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.PurgeExpiredLogs,
                        rawPayload,
                        () => Task.FromResult<object>(new
                        {
                            retentionDays = days,
                            deletedFiles = AuditLogManager.PurgeExpiredLogs(days),
                            backupsPreserved = true
                        }),
                        ct).ConfigureAwait(false);
                    return;
                }

                case "servicing:env":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetServicingEnvironment,
                        null,
                        () => Task.FromResult<object>(WindowsServicingHealthEngine.Instance.DetectServicingEnvironment()),
                        ct).ConfigureAwait(false);
                    return;

                case "servicing:run":
                {
                    string toolId = GetPayloadString(root, "toolId") ?? GetPayloadString(root, "id") ?? string.Empty;
                    string? args = GetPayloadString(root, "arguments");
                    int timeoutSec = TryGetPayload(root, out var sp) && sp.TryGetProperty("timeoutSeconds", out var toEl) && toEl.TryGetInt32(out int sec) && sec > 0
                        ? sec
                        : 7200;
                    string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { toolId, arguments = args, timeoutSeconds = timeoutSec });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ExecuteServicingTool,
                        rawPayload,
                        async () =>
                        {
                            if (string.IsNullOrWhiteSpace(toolId))
                                throw new ArgumentException("toolId is required for servicing execution.");
                            var res = await WindowsServicingHealthEngine.Instance.ExecuteServicingToolAsync(
                                toolId,
                                args,
                                TimeSpan.FromSeconds(timeoutSec),
                                ct).ConfigureAwait(false);
                            return (object)new
                            {
                                toolId = res.ToolId,
                                toolName = res.ToolName,
                                command = res.Command,
                                processStarted = res.ProcessStarted,
                                processId = res.ProcessId,
                                exitCode = res.ExitCode,
                                success = res.Success,
                                summary = res.Summary,
                                diagnosedRootCause = res.DiagnosedRootCause,
                                recommendedRemediation = res.RecommendedRemediation,
                                durationSeconds = res.Duration.TotalSeconds,
                                isCancelled = res.IsCancelled,
                                isTimedOut = res.IsTimedOut,
                                isNotApplicable = res.IsNotApplicable,
                                standardOutput = res.StandardOutput,
                                standardError = res.StandardError
                            };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                // ── Phase 4 (NEW-BE-003): Unified System Tools Orchestrator ──
                case "tools:catalog":
                case "tools:status":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetSystemToolsCatalog,
                        null,
                        () => Task.FromResult<object>(SystemToolsOrchestrator.Instance.GetToolsCatalog()),
                        ct).ConfigureAwait(false);
                    return;

                case "tools:execute":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    string toolId = GetPayloadString(root, "toolId")
                        ?? GetPayloadString(root, "id")
                        ?? GetPayloadString(root, "name")
                        ?? string.Empty;
                    bool confirmed = TryGetPayload(root, out var p) &&
                                     p.TryGetProperty("confirmed", out var cEl) &&
                                     cEl.ValueKind == JsonValueKind.True;

                    string norm = SystemToolsOrchestrator.NormalizeToolId(toolId);
                    bool isUserSessionTool = norm is "rebuild-icon-cache" or "shader-cache" or "reregister-store-apps";

                    if (isUserSessionTool)
                    {
                        var hostJob = SystemToolsOrchestrator.Instance.StartToolJob(toolId, confirmed, runInInteractiveSession: true);
                        ReplyOk(requestId, hostJob);
                        return;
                    }

                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ExecuteSystemTool,
                        rawPayload,
                        () => Task.FromResult<object>(SystemToolsOrchestrator.Instance.StartToolJob(toolId, confirmed, runInInteractiveSession: false)),
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tools:job-status":
                case "tools:job":
                {
                    string jobId = GetPayloadString(root, "jobId")
                        ?? GetPayloadString(root, "id")
                        ?? string.Empty;

                    var localJob = SystemToolsOrchestrator.Instance.GetJobStatus(jobId);
                    if (localJob != null)
                    {
                        ReplyOk(requestId, localJob);
                        return;
                    }

                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetSystemToolJobStatus,
                        JsonSerializer.Serialize(new { jobId }),
                        () => Task.FromResult<object>(localJob ?? new SystemToolJobStatusDto
                        {
                            JobId = jobId,
                            State = "FAILED",
                            Status = "Failed",
                            Message = "Job not found on host or service."
                        }),
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tools:cancel":
                {
                    string jobId = GetPayloadString(root, "jobId")
                        ?? GetPayloadString(root, "id")
                        ?? string.Empty;

                    bool cancelledLocal = SystemToolsOrchestrator.Instance.CancelJob(jobId);
                    if (cancelledLocal)
                    {
                        ReplyOk(requestId, new { success = true, message = "Cancellation requested on host." });
                        return;
                    }

                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CancelSystemToolJob,
                        JsonSerializer.Serialize(new { jobId }),
                        () => Task.FromResult<object>(new { success = false, message = "Job not found on service to cancel." }),
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tools:report":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetSystemToolReport,
                        null,
                        () => Task.FromResult<object>(new { latestReport = SystemToolsOrchestrator.Instance.GetLatestReportSummary() }),
                        ct).ConfigureAwait(false);
                    return;

                // ── Phase 5 (NEW-BE-001): CPU/GPU Performance Tuning ──
                case "tuning:probe":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ProbePerformanceTuning,
                        null,
                        async () => (object)await PerformanceTuningEngine.Instance.ProbeCapabilitiesAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "tuning:execute":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"target\":\"cpu\",\"confirmed\":true}";
                    string target = GetPayloadString(root, "target") ?? "cpu";
                    bool confirmed = !TryGetPayload(root, out var p) ||
                                     !p.TryGetProperty("confirmed", out var cEl) ||
                                     cEl.ValueKind != JsonValueKind.False;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ExecutePerformanceTuning,
                        rawPayload,
                        async () => (object)await PerformanceTuningEngine.Instance.ExecuteTuningAsync(target, confirmed, ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;
                }

                case "tuning:rollback":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    string? txId = GetPayloadString(root, "transactionId");
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RollbackPerformanceTuning,
                        rawPayload,
                        async () =>
                        {
                            var (ok, msg) = await PerformanceTuningEngine.Instance.RollbackTuningAsync(txId, ct).ConfigureAwait(false);
                            return (object)new { success = ok, message = msg, transactionId = txId };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "processes:scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetProcessCandidates,
                        null,
                        async () => (object)await new ProcessReductionEngine(_backupManager).ScanProcessesAsync().ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "processes:kill":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "[]";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.TerminateProcesses,
                        rawPayload,
                        () =>
                        {
                            var pids = new List<int>();
                            if (TryGetPayload(root, out var p) && p.TryGetProperty("pids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var el in arr.EnumerateArray())
                                    if (el.TryGetInt32(out int pid)) pids.Add(pid);
                            }
                            var killRes = new ProcessReductionEngine(_backupManager).TerminateProcessesDetailed(pids);
                            return Task.FromResult<object>(new
                            {
                                terminated = killRes.Terminated,
                                skippedProtected = killRes.SkippedProtected,
                                accessDenied = killRes.AccessDenied,
                                errors = killRes.Errors
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "power:discover":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetPowerPlans,
                        null,
                        async () =>
                        {
                            var plans = await PowerPlanEngine.Instance.DiscoverPowerPlansAsync(ct).ConfigureAwait(false);
                            var active = await PowerPlanEngine.Instance.GetActiveSchemeAsync(ct).ConfigureAwait(false);
                            var hw = HardwareAnalyzer.Instance.Collect();
                            var rec = HardwareAnalyzer.Instance.Recommend(hw, plans);
                            return (object)new
                            {
                                activeGuid = active.guid,
                                activeName = active.name,
                                plans,
                                hardware = hw.SummaryText,
                                recommendedPlan = rec.RecommendedPlanName,
                                recommendedGuid = rec.RecommendedPlanGuid,
                                recommendationReason = rec.Reason,
                                isOptimal = rec.IsCurrentActiveOptimal
                            };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "power:apply":
                {
                    string guid = GetPayloadString(root, "guid") ?? PowerPlanEngine.HighPerformanceGuid;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyPowerPlan,
                        guid,
                        async () =>
                        {
                            var res = await PowerPlanEngine.Instance.ApplyPowerPlanAsync(guid, PowerPlanChangeSource.USER_REQUEST, ct).ConfigureAwait(false);
                            if (!res.Success || !res.Verified)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Failed to apply power plan." : res.Message);
                            return (object)new {
                                success = res.Success,
                                name = res.ActiveSchemeName,
                                guid = res.ActiveSchemeGuid,
                                message = res.Message,
                                verified = res.Verified,
                                activeSchemeVerified = res.ActiveSchemeVerified,
                                settingsTotal = res.SettingsTotal,
                                settingsVerified = res.SettingsVerified,
                                settingsUnsupported = res.SettingsUnsupported,
                                settingsFailed = res.SettingsFailed,
                                baseScheme = res.BaseSchemeUsed,
                                modernStandby = res.ModernStandbyDetected,
                                verifiedSettings = res.VerifiedSettings
                            };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "power:create-ai-max":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CreateCustomAiPlan,
                        ExtractPayloadJson(root) ?? "ai-max",
                        async () =>
                        {
                            var res = await PowerPlanEngine.Instance.CreateOrGetAiMaxPerformancePlanAsync(ct).ConfigureAwait(false);
                            if (!res.Success || !res.Verified)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Failed to activate AI Max Performance power plan." : res.Message);
                            return (object)new {
                                success = res.Success,
                                name = res.ActiveSchemeName,
                                guid = res.ActiveSchemeGuid,
                                message = res.Message,
                                verified = res.Verified,
                                activeSchemeVerified = res.ActiveSchemeVerified,
                                settingsTotal = res.SettingsTotal,
                                settingsVerified = res.SettingsVerified,
                                settingsUnsupported = res.SettingsUnsupported,
                                settingsFailed = res.SettingsFailed,
                                baseScheme = res.BaseSchemeUsed,
                                modernStandby = res.ModernStandbyDetected,
                                verifiedSettings = res.VerifiedSettings
                            };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "power:create-ai-battery":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CreateCustomAiPlan,
                        "ai-battery",
                        async () =>
                        {
                            var res = await PowerPlanEngine.Instance.CreateOrGetAiBatteryEfficiencyPlanAsync(ct).ConfigureAwait(false);
                            if (!res.Success || !res.Verified)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Failed to activate AI Battery Efficiency power plan." : res.Message);
                            return (object)new {
                                success = res.Success,
                                name = res.ActiveSchemeName,
                                guid = res.ActiveSchemeGuid,
                                message = res.Message,
                                verified = res.Verified,
                                activeSchemeVerified = res.ActiveSchemeVerified,
                                settingsTotal = res.SettingsTotal,
                                settingsVerified = res.SettingsVerified,
                                settingsUnsupported = res.SettingsUnsupported,
                                settingsFailed = res.SettingsFailed,
                                baseScheme = res.BaseSchemeUsed,
                                modernStandby = res.ModernStandbyDetected,
                                verifiedSettings = res.VerifiedSettings
                            };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "power:enable-ultimate":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.EnableUltimatePerformance,
                        null,
                        async () =>
                        {
                            var res = await PowerPlanEngine.Instance.EnableUltimatePerformanceAsync(ct).ConfigureAwait(false);
                            if (!res.Success || !res.Verified)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Failed to enable Ultimate Performance power plan." : res.Message);
                            return (object)new { success = res.Success, name = res.ActiveSchemeName, guid = res.ActiveSchemeGuid, message = res.Message, verified = res.Verified };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "power:restore":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestorePowerPlan,
                        null,
                        async () =>
                        {
                            var res = await PowerPlanEngine.Instance.RestorePreviousPowerPlanAsync(ct).ConfigureAwait(false);
                            if (!res.Success || !res.Verified)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Failed to restore previous power plan." : res.Message);
                            return (object)new { success = res.Success, name = res.ActiveSchemeName, guid = res.ActiveSchemeGuid, message = res.Message, verified = res.Verified };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "power:restore-balanced":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ApplyPowerPlan,
                        PowerPlanEngine.BalancedGuid,
                        async () =>
                        {
                            var res = await PowerPlanEngine.Instance.RestoreBalancedPlanAsync(ct).ConfigureAwait(false);
                            if (!res.Success || !res.Verified)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Failed to restore Balanced power plan." : res.Message);
                            return (object)new { success = res.Success, name = res.ActiveSchemeName, guid = res.ActiveSchemeGuid, message = res.Message, verified = res.Verified };
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "backup:capabilities":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetCapabilities,
                        null,
                        () =>
                        {
                            var stats = _backupManager.GetSummaryStats();
                            var v4Count = BackupStore.List().Count;
                            return Task.FromResult<object>(new
                            {
                                isElevated = SystemPowerOperations.IsCurrentProcessElevated(),
                                stats,
                                v4SnapshotCount = v4Count,
                                integrityVerification = true,
                                supportsCreate = true,
                                supportsDelete = true,
                                separatedFromSystemRestore = true
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "backup:list":
                {
                    int offset = 0;
                    int limit = 150;
                    if (root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object)
                    {
                        if (pl.TryGetProperty("offset", out var oProp) && oProp.TryGetInt32(out int oVal)) offset = Math.Max(0, oVal);
                        if (pl.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out int lVal)) limit = Math.Clamp(lVal, 1, 1000);
                    }
                    else if (root.TryGetProperty("offset", out var oProp) && oProp.TryGetInt32(out int oVal))
                    {
                        offset = Math.Max(0, oVal);
                        if (root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out int lVal)) limit = Math.Clamp(lVal, 1, 1000);
                    }

                    string rawPayload = JsonSerializer.Serialize(new { offset, limit });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetBackups,
                        rawPayload,
                        () =>
                        {
                            var tx = _backupManager.GetTransactionsPaginated(offset, limit, out int totalCount);
                            var groups = _backupManager.GetProfileBackupGroups(includeTransactions: false);
                            var stats = _backupManager.GetSummaryStats();
                            var v4Backups = BackupStore.List();
                            var v4Transactions = new V4RollbackEngine().GetTransactions();
                            return Task.FromResult<object>(new
                            {
                                transactions = tx,
                                totalCount,
                                offset,
                                limit,
                                hasMore = (offset + tx.Count) < totalCount,
                                profileGroups = groups,
                                stats,
                                v4Backups,
                                v4Transactions
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "backup:create":
                {
                    string name = GetPayloadString(root, "name") ?? string.Empty;
                    string? notes = GetPayloadString(root, "notes");
                    string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { name, notes });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CreateApplicationBackup,
                        rawPayload,
                        () =>
                        {
                            var created = _backupManager.CreateApplicationSnapshot(name, notes);
                            if (string.Equals(created.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Failed to persist application backup snapshot.");
                            return Task.FromResult<object>(new
                            {
                                success = true,
                                transaction = created,
                                message = $"Application backup '{created.OptimizationName}' created and hashed."
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "backup:validate":
                {
                    string id = GetPayloadString(root, "transactionId")
                                ?? GetPayloadString(root, "backupId")
                                ?? GetPayloadString(root, "id")
                                ?? string.Empty;
                    string? backupId = GetPayloadString(root, "backupId");
                    string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { transactionId = id, backupId });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ValidateBackupIntegrity,
                        rawPayload,
                        () =>
                        {
                            if (!string.IsNullOrWhiteSpace(backupId))
                            {
                                var (validSnap, reason, entry) = BackupStore.Validate(backupId);
                                return Task.FromResult<object>(new { valid = validSnap, reason, entry });
                            }
                            var (validTx, txReason) = _backupManager.ValidateTransactionIntegrity(id);
                            if (!validTx)
                            {
                                var (validSnap, snapReason, entry) = BackupStore.Validate(id);
                                if (entry != null)
                                    return Task.FromResult<object>(new { valid = validSnap, reason = snapReason, entry });
                            }
                            return Task.FromResult<object>(new { valid = validTx, reason = txReason });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "backup:delete-bulk":
                case "backup:delete-all":
                case "backup:delete":
                {
                    bool deleteAll = string.Equals(action, "backup:delete-all", StringComparison.OrdinalIgnoreCase);
                    List<string>? ids = null;

                    if (root.TryGetProperty("payload", out var plEl) && plEl.ValueKind == JsonValueKind.Object)
                    {
                        if (plEl.TryGetProperty("deleteAll", out var daEl) && daEl.ValueKind == JsonValueKind.True) deleteAll = true;
                        if (plEl.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                        {
                            ids = new List<string>();
                            foreach (var item in idsEl.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    string s = item.GetString() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (root.TryGetProperty("deleteAll", out var daEl) && daEl.ValueKind == JsonValueKind.True) deleteAll = true;
                        if (root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                        {
                            ids = new List<string>();
                            foreach (var item in idsEl.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    string s = item.GetString() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                                }
                            }
                        }
                    }

                    if (deleteAll)
                    {
                        string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { deleteAll = true });
                        await RouteIpcWithFallbackAsync(
                            requestId,
                            IpcMessageType.DeleteBackup,
                            rawPayload,
                            () =>
                            {
                                var (bmDeleted, bmSkipped, bmFailed, bmFreed) = _backupManager.DeleteAllApplicationBackups();
                                var (storeDeleted, storeFailed, storeFreed) = BackupStore.DeleteAll();
                                var (v4Deleted, v4Failed) = new V4RollbackEngine().DeleteAllTransactions();

                                int totalDeleted = bmDeleted + storeDeleted + v4Deleted;
                                int totalSkipped = bmSkipped;
                                int totalFailed = bmFailed + storeFailed + v4Failed;
                                long totalFreed = bmFreed + storeFreed;

                                return Task.FromResult<object>(new
                                {
                                    success = true,
                                    deleted = true,
                                    deletedCount = totalDeleted,
                                    skippedCount = totalSkipped,
                                    failedCount = totalFailed,
                                    freedBytes = totalFreed,
                                    message = $"{totalDeleted} application backup(s) purged. Windows System Restore points preserved."
                                });
                            },
                            ct).ConfigureAwait(false);
                        return;
                    }

                    if (ids != null && ids.Count > 0)
                    {
                        string rawPayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { ids });
                        await RouteIpcWithFallbackAsync(
                            requestId,
                            IpcMessageType.DeleteBackup,
                            rawPayload,
                            () =>
                            {
                                var (bmDeleted, bmSkipped, bmFailed, bmFreed) = _backupManager.DeleteTransactions(ids);
                                var (storeDeleted, storeFailed, storeFreed) = BackupStore.DeleteMultiple(ids);
                                var (v4Deleted, v4Failed) = new V4RollbackEngine().DeleteTransactions(ids);

                                int totalDeleted = bmDeleted + storeDeleted + v4Deleted;
                                int totalSkipped = bmSkipped;
                                int totalFailed = bmFailed + storeFailed + v4Failed;
                                long totalFreed = bmFreed + storeFreed;

                                return Task.FromResult<object>(new
                                {
                                    success = true,
                                    deleted = totalDeleted > 0,
                                    deletedCount = totalDeleted,
                                    skippedCount = totalSkipped,
                                    failedCount = totalFailed,
                                    freedBytes = totalFreed,
                                    message = $"{totalDeleted} backup(s) deleted cleanly."
                                });
                            },
                            ct).ConfigureAwait(false);
                        return;
                    }

                    string id = GetPayloadString(root, "transactionId")
                                ?? GetPayloadString(root, "backupId")
                                ?? GetPayloadString(root, "id")
                                ?? string.Empty;
                    string? backupId = GetPayloadString(root, "backupId");
                    string singlePayload = ExtractPayloadJson(root) ?? JsonSerializer.Serialize(new { transactionId = id, backupId });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.DeleteBackup,
                        singlePayload,
                        () =>
                        {
                            if (string.IsNullOrWhiteSpace(id))
                                throw new ArgumentException("transactionId, backupId, ids, or deleteAll is required.");

                            if (!string.IsNullOrWhiteSpace(backupId))
                            {
                                var (deletedSnap, reason) = BackupStore.Delete(backupId);
                                if (!deletedSnap) throw new InvalidOperationException(reason);
                                return Task.FromResult<object>(new { deleted = true, id = backupId, message = reason });
                            }

                            if (_backupManager.DeleteTransaction(id))
                                return Task.FromResult<object>(new { deleted = true, id, message = $"Transaction '{id}' deleted safely." });

                            var (deletedFallback, snapReason) = BackupStore.Delete(id);
                            if (deletedFallback)
                                return Task.FromResult<object>(new { deleted = true, id, message = snapReason });

                            if (new V4RollbackEngine().DeleteTransaction(id))
                                return Task.FromResult<object>(new { deleted = true, id, message = $"V4 Transaction '{id}' deleted safely." });

                            throw new InvalidOperationException($"Backup '{id}' was not found.");
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "backup:restore-transaction":
                {
                    bool fullSnapshot = TryGetPayload(root, out var fp) &&
                                        fp.TryGetProperty("fullSnapshot", out var fsEl) &&
                                        fsEl.ValueKind == JsonValueKind.True;
                    string id = GetPayloadString(root, "transactionId") ?? string.Empty;
                    string profileName = GetPayloadString(root, "profileName") ?? string.Empty;
                    string rawPayload = ExtractPayloadJson(root) ?? (fullSnapshot
                        ? "{\"fullSnapshot\":true}"
                        : JsonSerializer.Serialize(new { transactionId = id }));
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreBackup,
                        rawPayload,
                        async () =>
                        {
                            RestoreResult res;
                            if (fullSnapshot)
                            {
                                res = await _backupManager.RestoreFullSystemSnapshotAsync(ct).ConfigureAwait(false);
                            }
                            else if (!string.IsNullOrWhiteSpace(id))
                            {
                                res = await _backupManager.RestoreSingleTransactionAsync(id, ct).ConfigureAwait(false);
                            }
                            else if (!string.IsNullOrWhiteSpace(profileName))
                            {
                                res = await _backupManager.RestoreProfileAsync(ProfileRepository.NormalizeProfileId(profileName), ct).ConfigureAwait(false);
                            }
                            else
                            {
                                throw new ArgumentException("transactionId, profileName, or fullSnapshot=true is required.");
                            }
                            if (!res.Success)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Backup restore did not succeed." : res.Message);
                            return (object)res;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "backup:restore-full":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreBackup,
                        "{\"fullSnapshot\":true}",
                        async () =>
                        {
                            var res = await _backupManager.RestoreFullSystemSnapshotAsync(ct).ConfigureAwait(false);
                            if (!res.Success)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Full system restore failed." : res.Message);
                            return (object)res;
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "backup:restore-profile":
                {
                    string name = ProfileRepository.NormalizeProfileId(GetPayloadString(root, "profileName") ?? string.Empty);
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreTier,
                        name,
                        async () =>
                        {
                            if (string.IsNullOrWhiteSpace(name))
                                throw new ArgumentException("profileName is required.");
                            var res = await _backupManager.RestoreProfileAsync(name, ct).ConfigureAwait(false);
                            if (!res.Success)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? $"Restore for profile '{name}' failed." : res.Message);
                            return (object)res;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "backup:restore-snapshot":
                {
                    string? backupId = GetPayloadString(root, "backupId") ?? GetPayloadString(root, "id");
                    string? batchId = GetPayloadString(root, "batchId");
                    if (!string.IsNullOrWhiteSpace(backupId))
                    {
                        await RouteIpcWithFallbackAsync(
                            requestId,
                            IpcMessageType.RestoreBackup,
                            JsonSerializer.Serialize(new { backupId }),
                            async () =>
                            {
                                var (validSnap, reason, _) = BackupStore.Validate(backupId);
                                if (!validSnap)
                                    throw new InvalidOperationException(reason);
                                await Task.Run(() => BackupStore.Restore(backupId), ct).ConfigureAwait(false);
                                return (object)new { success = true, backupId, message = $"V4 snapshot '{backupId}' restored." };
                            },
                            ct).ConfigureAwait(false);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(batchId))
                    {
                        await RouteIpcWithFallbackAsync(
                            requestId,
                            IpcMessageType.RestoreBackup,
                            JsonSerializer.Serialize(new { batchId }),
                            async () =>
                            {
                                return await Task.Run(() =>
                                {
                                    bool r = new V4RollbackEngine().RollbackBatch(batchId, out int restoredCount, out int failedCount, out string summaryMsg);
                                    if (!r) throw new InvalidOperationException(string.IsNullOrWhiteSpace(summaryMsg) ? $"V4 rollback failed ({failedCount} failed)." : summaryMsg);
                                    return (object)new { success = true, batchId, restoredCount, message = $"V4 batch '{batchId}' rolled back." };
                                }, ct).ConfigureAwait(false);
                            },
                            ct).ConfigureAwait(false);
                        return;
                    }
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.RestoreBackup,
                        "{\"fullSnapshot\":true}",
                        async () =>
                        {
                            var res = await _backupManager.RestoreFullSystemSnapshotAsync(ct).ConfigureAwait(false);
                            if (!res.Success)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Message) ? "Full system restore failed." : res.Message);
                            return (object)res;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "restorepoint:list":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetRestorePoints,
                        null,
                        () => Task.FromResult<object>(GetCachedRestorePoints()),
                        ct).ConfigureAwait(false);
                    return;

                case "restorepoint:create":
                {
                    string? origin = GetPayloadString(root, "origin");
                    if (!string.Equals(origin, "dashboard", StringComparison.OrdinalIgnoreCase))
                    {
                        ReplyError(requestId, "Windows System Restore Point creation is restricted to the Dashboard CREATE RESTORE POINT action.");
                        return;
                    }

                    string name = GetPayloadString(root, "description") ?? $"ErrorOptimizer_{DateTime.Now:yyyyMMdd_HHmm}";
                    string ipcPayload = JsonSerializer.Serialize(new { description = name, origin = "dashboard" });
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CreateRestorePoint,
                        ipcPayload,
                        async () =>
                        {
                            InvalidateSystemRestoreCache();
                            var res = await Task.Run(() => WindowsSystemRestoreEngine.Instance.CreateRestorePoint(name), ct).ConfigureAwait(false);
                            InvalidateSystemRestoreCache();
                            if (!res.Success)
                            {
                                string prefix = res.Status == RestorePointStatus.SkippedByPolicy ? "SKIPPED BY POLICY: " : string.Empty;
                                throw new InvalidOperationException(prefix + (res.ErrorMessage ?? res.StatusMessage ?? "Failed to create Windows restore point."));
                            }
                            return (object)res;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "restorepoint:status":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CheckSystemRestoreStatus,
                        null,
                        () =>
                        {
                            var srState = GetCachedSystemRestoreSnapshot();
                            return Task.FromResult<object>(new
                            {
                                isAvailable = srState.IsAvailable,
                                isEnabled = srState.IsEnabled,
                                enabled = srState.IsEnabled,
                                protectionEnabled = srState.IsEnabled,
                                drive = srState.Drive,
                                reason = srState.IsEnabled ? srState.ProtectionReason : (srState.IsAvailable ? srState.ProtectionReason : srState.AvailabilityReason)
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "restorepoint:enable":
                {
                    string? drive = GetPayloadString(root, "drive");
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.EnableSystemProtection,
                        drive,
                        () =>
                        {
                            InvalidateSystemRestoreCache();
                            var (success, message) = RunOnBoundedMtaThread(
                                () => WindowsSystemRestoreEngine.Instance.EnableSystemProtection(drive),
                                TimeSpan.FromSeconds(50),
                                (false, "Timed out waiting for Windows System Protection to enable."));
                            InvalidateSystemRestoreCache();
                            if (!success)
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Failed to enable System Protection." : message);
                            var srState = GetCachedSystemRestoreSnapshot(forceRefresh: true);
                            return Task.FromResult<object>(new
                            {
                                success,
                                isEnabled = srState.IsEnabled,
                                enabled = srState.IsEnabled,
                                protectionEnabled = srState.IsEnabled,
                                drive = srState.Drive,
                                message = srState.IsEnabled ? srState.ProtectionReason : message
                            });
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "auto:get-state":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.GetAutoOptimizeState,
                        null,
                        () => Task.FromResult<object>(AutoOptimizeEngine.Instance.GetStateSnapshot()),
                        ct).ConfigureAwait(false);
                    return;

                case "auto:save-config":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{}";
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SaveAutoOptimizeConfig,
                        rawPayload,
                        () =>
                        {
                            var cfg = JsonSerializer.Deserialize<AutoOptimizeConfig>(rawPayload, JsonOptions) ?? new AutoOptimizeConfig();
                            var saved = AutoOptimizeEngine.Instance.SaveConfig(cfg);
                            var us = UnifiedSettingsStore.LoadSafe();
                            us.AutoOptimizationEnabled = saved.Enabled;
                            us.MonitoringInterval = saved.IntervalMin;
                            us = UnifiedSettingsStore.Save(us);
                            if (saved.RunOnWindowsStartup && saved.Enabled) { StartupRegistrar.Instance.ConfigureStartup(true, us.StartMinimized); }
                            BroadcastGlobalConfig(us);
                            HostBackgroundSupervisor.Instance.RefreshTrayStatus();
                            return Task.FromResult<object>(AutoOptimizeEngine.Instance.GetStateSnapshot());
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "auto:run-now":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"trigger\":\"Manual\"}";
                    string trigger = GetPayloadString(root, "trigger") ?? "Manual";
                    bool recycleConfirmed = TryGetPayload(root, out var p) &&
                                            p.TryGetProperty("recycleConfirmed", out var rc) &&
                                            rc.ValueKind == JsonValueKind.True;
                    var taskList = ReadStringArray(root, "tasks");
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.TriggerAutoOptimizeNow,
                        rawPayload,
                        async () =>
                        {
                            var entry = await AutoOptimizeEngine.Instance
                                .ExecuteAsync(trigger, taskList.Count > 0 ? taskList : null, recycleConfirmed, ct)
                                .ConfigureAwait(false);
                            return (object)new { entry, state = AutoOptimizeEngine.Instance.GetStateSnapshot() };
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "auto:cancel":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.CancelAutoOptimizeRun,
                        null,
                        () => Task.FromResult<object>(new
                        {
                            cancelled = AutoOptimizeEngine.Instance.CancelActiveRun(),
                            state = AutoOptimizeEngine.Instance.GetStateSnapshot()
                        }),
                        ct).ConfigureAwait(false);
                    return;

                case "auto:pause":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"paused\":true}";
                    bool paused = !TryGetPayload(root, out var p) ||
                                  !p.TryGetProperty("paused", out var pe) ||
                                  pe.ValueKind != JsonValueKind.False;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.SetAutoOptimizePaused,
                        rawPayload,
                        () =>
                        {
                            var cfg = AutoOptimizeEngine.Instance.LoadConfig();
                            cfg.Paused = paused;
                            AutoOptimizeEngine.Instance.SaveConfig(cfg);
                            BroadcastGlobalConfig();
                            HostBackgroundSupervisor.Instance.RefreshTrayStatus();
                            return Task.FromResult<object>(AutoOptimizeEngine.Instance.GetStateSnapshot());
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "auto:clear-history":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ClearAutoOptimizeHistory,
                        null,
                        () =>
                        {
                            AutoOptimizeEngine.Instance.ClearHistory();
                            return Task.FromResult<object>(AutoOptimizeEngine.Instance.GetStateSnapshot());
                        },
                        ct).ConfigureAwait(false);
                    return;

                case "auto:scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.ScanAutoOptimizeTasks,
                        null,
                        async () => (object)await AutoOptimizeEngine.Instance.ScanTasksAsync(ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "auto:execute":
                {
                    string rawPayload = ExtractPayloadJson(root) ?? "{\"trigger\":\"Manual\"}";
                    var tasks = ReadStringArray(root, "tasks");
                    bool recycleConfirmed = TryGetPayload(root, out var p) &&
                                            p.TryGetProperty("recycleConfirmed", out var rc) &&
                                            rc.ValueKind == JsonValueKind.True;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.TriggerAutoOptimizeNow,
                        rawPayload,
                        async () => (object)await AutoOptimizeEngine.Instance
                            .ExecuteAsync("Manual", tasks.Count > 0 ? tasks : null, recycleConfirmed, ct)
                            .ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;
                }

                // ── Core Optimization & Legacy Maximum Performance ──
                case "core-optimization:scan":
                case "v4:maxperf-scan":
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.V4ScanProcessIntelligence,
                        null,
                        async () => (object)await V4MaximumPerformanceCoordinator.Instance.ScanProcessesAsync().ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;

                case "core-optimization:plan":
                case "v4:maxperf-plan":
                {
                    string profile = GetPayloadString(root, "workloadProfile") ?? "Gaming";
                    string ipcPayload = JsonSerializer.Serialize(new { pass = 1, workloadProfile = profile }, JsonOptions);
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.V4GetMaximumPerformancePlan,
                        ipcPayload,
                        async () => (object)await V4MaximumPerformanceCoordinator.Instance.GeneratePlanAsync(1, profile).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                    return;
                }

                case "core-optimization:execute":
                case "v4:maxperf-execute":
                {
                    string profile = GetPayloadString(root, "workloadProfile") ?? "Gaming";
                    var selected = ReadStringArray(root, "selectedIds");
                    bool hasCoreConvergence = selected.Count == 0 || selected.Contains("core", StringComparer.OrdinalIgnoreCase);
                    int passes = hasCoreConvergence ? 4 : 1;
                    string ipcPayload = JsonSerializer.Serialize(new
                    {
                        MaxPasses = passes,
                        WorkloadProfile = profile,
                        IsCustomSelection = selected.Count > 0,
                        SelectedCandidateIds = selected
                    }, JsonOptions);
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.V4ExecuteFullOptimization,
                        ipcPayload,
                        async () =>
                        {
                            var res = await V4MaximumPerformanceCoordinator.Instance.ExecuteFullOptimizationAsync(
                                maxPasses: passes,
                                workloadProfile: profile,
                                isCustomSelection: selected.Count > 0,
                                selectedCandidateIds: selected.Count > 0 ? selected : null,
                                ct: ct).ConfigureAwait(false);
                            if (!res.Success && string.Equals(res.ResultState, "ABORTED", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.StopReason) ? "Core Optimization execution aborted by safety invariant." : res.StopReason);
                            return (object)res;
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "core-optimization:rollback":
                case "v4:maxperf-rollback":
                {
                    string batchId = GetPayloadString(root, "batchId") ?? string.Empty;
                    await RouteIpcWithFallbackAsync(
                        requestId,
                        IpcMessageType.V4RollbackMaximumPerformance,
                        batchId,
                        async () =>
                        {
                            return await Task.Run(() =>
                            {
                                var coordinator = V4MaximumPerformanceCoordinator.Instance;
                                bool ok = coordinator.RollbackOptimization(batchId, out int restored, out int failed, out string msg);
                                if (!ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? $"Core Optimization rollback failed ({failed} failed)." : msg);
                                return (object)new { success = true, batchId, restoredCount = restored, failedCount = failed, message = msg };
                            }, ct).ConfigureAwait(false);
                        },
                        ct).ConfigureAwait(false);
                    return;
                }

                case "v4:recommendations-scan":
                    ReplyOk(requestId, await MasterRecommendationEngine.Instance.GenerateRecommendationsAsync(forceRescan: true, ct: ct).ConfigureAwait(false));
                    return;

                case "v4:recommendations-execute":
                {
                    string recId = GetPayloadString(root, "id") ?? string.Empty;
                    ReplyOk(requestId, await MasterExecutionCoordinator.Instance.ExecuteRecommendationAsync(recId, ct).ConfigureAwait(false));
                    return;
                }

                default:
                    if (requestId != null)
                        ReplyError(requestId, $"Unsupported host bridge action '{action}'.");
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            if (requestId != null)
                ReplyError(requestId, $"Operation '{action}' cancelled.");
        }
        catch (Exception ex)
        {
            if (requestId != null)
                ReplyError(requestId, ex.Message);
        }
    }

    private static (bool IsAvailable, string StatusReason, DateTime CheckedAt) _cachedSysRestore = (false, "", DateTime.MinValue);
    private static readonly object _srLock = new();

    private static (bool IsAvailable, string StatusReason) GetCachedSystemRestoreAvailability()
    {
        lock (_srLock)
        {
            if (DateTime.UtcNow - _cachedSysRestore.CheckedAt < TimeSpan.FromSeconds(60))
            {
                return (_cachedSysRestore.IsAvailable, _cachedSysRestore.StatusReason);
            }
        }

        try
        {
            // Fast registry preflight check first: if DisableSR is set in Group Policy or CurrentVersion, we know it's disabled in <1ms without WMI
            using (var polKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"))
            {
                if (polKey != null && polKey.GetValue("DisableSR") is int d && d == 1)
                {
                    lock (_srLock)
                    {
                        _cachedSysRestore = (false, "System Protection is disabled by Windows Group Policy (DisableSR = 1).", DateTime.UtcNow);
                        return (_cachedSysRestore.IsAvailable, _cachedSysRestore.StatusReason);
                    }
                }
            }

            var sr = WindowsSystemRestoreEngine.Instance.CheckSystemRestoreAvailability();
            lock (_srLock)
            {
                _cachedSysRestore = (sr.IsAvailable, sr.StatusReason, DateTime.UtcNow);
                return (sr.IsAvailable, sr.StatusReason);
            }
        }
        catch (Exception ex)
        {
            return (false, $"System Restore check failed: {ex.Message}");
        }
    }

    private async Task<object> BuildHostHandshakePayloadAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var snap = await _ipc.CheckHealthAsync(ct).ConfigureAwait(false);
        bool isElevated = SystemPowerOperations.IsCurrentProcessElevated();

        bool coreReady = false;
        bool backupReady = false;
        bool settingsReady = false;
        bool autoReady = false;
        bool sysRestoreAvail = false;
        string sysRestoreReason = string.Empty;
        string? probeError = null;

        try
        {
            coreReady = Directory.Exists(ResolveProfilesDirectory()) || typeof(OneClickOptimizationEngine) != null;
            _ = _backupManager.GetSummaryStats();
            backupReady = true;
            _ = UnifiedSettingsStore.LoadSafe();
            settingsReady = true;
            _ = AutoOptimizeEngine.Instance.GetStateSnapshot();
            autoReady = true;
            var sr = GetCachedSystemRestoreAvailability();
            sysRestoreAvail = sr.IsAvailable;
            sysRestoreReason = sr.StatusReason;
        }
        catch (Exception ex)
        {
            probeError = ex.Message;
        }

        sw.Stop();
        bool hostEngineReady = coreReady && backupReady && settingsReady && autoReady;
        bool overallHealthy = snap.Healthy || hostEngineReady;
        string hostVersion = typeof(OperationsBridgeHandler).Assembly.GetName().Version?.ToString() ?? "4.0.1";
        string reportedVersion = !string.IsNullOrWhiteSpace(snap.ServiceVersion) ? snap.ServiceVersion : hostVersion;
        string mode = snap.Healthy ? "windows-service" : "desktop-host-engine";
        string message = snap.Healthy
            ? snap.Message
            : overallHealthy
                ? (isElevated
                    ? "Desktop Host Backend online (elevated in-process Core Optimization engines active; background service pipe idle)"
                    : "Desktop Host Backend online (in-process Core Optimization engines active; service pipe offline — admin-only actions require elevation)")
                : $"Backend subsystem initialization failed: {probeError ?? "unknown error"}";

        bool interactiveDesktop = Environment.UserInteractive;

        var features = new Dictionary<string, object>
        {
            ["windowsServicePipe"] = new { available = snap.Healthy, mode = snap.Healthy ? "connected" : "offline", reason = snap.Message },
            ["backupAndRestore"] = new { available = backupReady, mode = snap.Healthy ? "service" : "host" },
            ["systemRestore"] = new { available = sysRestoreAvail, mode = isElevated ? "enabled" : "elevation-required", reason = sysRestoreReason },
            ["rawInputLatency"] = new { available = interactiveDesktop, mode = interactiveDesktop ? "win32-wm-input" : "session0-blocked" },
            ["etwDpcTelemetry"] = new { available = isElevated, mode = isElevated ? "kernel-etw" : "partial-non-elevated" },
            ["presentMonBenchmark"] = new { available = true, mode = "etw-dxgi-orchestrator" },
            ["uefiReboot"] = new { available = isElevated, mode = isElevated ? "bcdedit-firmware" : "elevation-required" },
            ["powerPlans"] = new { available = true, mode = "powercfg-engine" },
            ["autoOptimizeScheduler"] = new { available = autoReady, mode = "persistent-engine" },
            ["unifiedSettings"] = new { available = settingsReady, mode = "programdata-store" },
            ["systemTools"] = new { available = true, mode = "orchestrator" },
            ["serviceManager"] = new { available = true, mode = isElevated ? "read-write" : "read-only" }
        };

        return new
        {
            healthy = overallHealthy,
            hostConnected = true,
            hostEngineReady,
            serviceConnected = snap.Healthy,
            backendMode = mode,
            state = overallHealthy ? (snap.Healthy ? "connected" : "host_connected") : "disconnected",
            protocolVersion = "v2",
            version = reportedVersion,
            serviceVersion = snap.ServiceVersion,
            hostVersion,
            pipeName = snap.PipeName,
            isElevated,
            message,
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            capabilities = snap.Handshake?.Capabilities ?? new List<string>(features.Keys),
            features
        };
    }

    private async Task ReplyServiceHealthAsync(string requestId, CancellationToken ct)
    {
        var payload = await BuildHostHandshakePayloadAsync(ct).ConfigureAwait(false);
        _postJson(JsonSerializer.Serialize(new
        {
            id = requestId,
            ok = true,
            result = payload
        }, JsonOptions));
    }

    private async Task RouteIpcWithFallbackAsync(
        string? requestId,
        IpcMessageType ipcType,
        string? ipcPayload,
        Func<Task<object>> directFallback,
        CancellationToken ct)
    {
        var outcome = await _ipc.SendDetailedAsync(ipcType, ipcPayload, null, ct).ConfigureAwait(false);
        if (outcome.PipeReachable || outcome.MayHaveMutated)
        {
            if (outcome.Response.Success)
            {
                object? parsed = string.IsNullOrWhiteSpace(outcome.Response.Data)
                    ? new { ok = true, success = true }
                    : TryParseRawJsonOrString(outcome.Response.Data);
                ReplyOk(requestId, parsed);
            }
            else if (!string.IsNullOrWhiteSpace(outcome.Response.Data) &&
                     (outcome.Response.Data.TrimStart().StartsWith("{") || outcome.Response.Data.TrimStart().StartsWith("[")))
            {
                // Service returned structured payload with individual operation accounting
                object? parsed = TryParseRawJsonOrString(outcome.Response.Data);
                ReplyOk(requestId, parsed);
            }
            else if (!outcome.MayHaveMutated)
            {
                // Service failed cleanly without mutation (e.g. Session 0 isolation, unprivileged pipe client, or missing user hive):
                // fall back to in-process elevated desktop host engine execution for 100% parity with standalone build!
                try
                {
                    object directResult = await directFallback().ConfigureAwait(false);
                    ReplyOk(requestId, directResult);
                }
                catch (Exception ex)
                {
                    ReplyError(requestId, ex.Message);
                }
            }
            else
            {
                ReplyError(requestId, outcome.Response.ErrorMessage ?? $"Service operation '{ipcType}' failed or timed out without confirmed completion.");
            }
            return;
        }

        try
        {
            object directResult = await directFallback().ConfigureAwait(false);
            ReplyOk(requestId, directResult);
        }
        catch (Exception ex)
        {
            ReplyError(requestId, ex.Message);
        }
    }

    private async Task SyncHostBridgesToSavedPoliciesAsync()
    {
        try
        {
            var s = UnifiedSettingsStore.LoadSafe();
            if (!s.AiEnabled || !s.Policies.Workload)
                await _workload.StopAndRevertAllAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private TierEngine CreateTierEngine()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IWorkloadDetector, EmulatorWorkloadDetector>();
        services.AddSingleton<IWorkloadDetector, AdobeWorkloadDetector>();
        services.AddSingleton<IWorkloadDetector, TopazWorkloadDetector>();
        services.AddSingleton<IWorkloadDetector, GameWorkloadDetector>();

        services.AddSingleton<IProfileRepository>(_ => new ProfileRepository(ResolveProfilesDirectory()));
        services.AddSingleton<IEnvironmentDetector>(sp => new EnvironmentDetector(sp.GetServices<IWorkloadDetector>()));
        services.AddSingleton<IProcessSnapshot, ProcessSnapshot>();
        services.AddSingleton<ISafetyPolicy, ProtectedTargetsPolicy>();
        services.AddSingleton<IVerificationEngine, VerificationEngine>();

        services.AddSingleton<IServiceManager, BiosOptimizer.CLI.Adapters.WindowsServiceManager>();
        services.AddSingleton<IRegistryManager, BiosOptimizer.CLI.Adapters.WindowsRegistryManager>();
        services.AddSingleton<IScheduledTaskManager, BiosOptimizer.CLI.Adapters.WindowsScheduledTaskManager>();
        services.AddSingleton<IStartupManager, BiosOptimizer.CLI.Adapters.WindowsStartupManager>();
        services.AddSingleton<IPackageManager, BiosOptimizer.CLI.Adapters.WindowsPackageManager>();
        services.AddSingleton<IAcpiEvaluator, NullAcpiEvaluator>();
        services.AddSingleton<IProviderManager, ProviderManager>();
        services.AddSingleton<IBackupManager>(_backupManager);

        List<Type> handlerTypes = typeof(IActionHandler).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IActionHandler).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (Type handlerType in handlerTypes)
        {
            services.AddSingleton(typeof(IActionHandler), handlerType);
        }

        services.AddSingleton<IActionRegistry>(sp =>
        {
            var registry = new ActionRegistry();
            foreach (var handler in sp.GetServices<IActionHandler>())
            {
                registry.RegisterHandler(handler);
            }
            return registry;
        });

        services.AddSingleton<TierEngine>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var actionRegistry = provider.GetRequiredService<IActionRegistry>();
        int registeredCount = provider.GetServices<IActionHandler>().Count();
        if (registeredCount != handlerTypes.Count)
        {
            throw new InvalidOperationException(
                $"Host fallback DI registration incomplete: expected {handlerTypes.Count} IActionHandler implementations, registered {registeredCount}.");
        }

        return provider.GetRequiredService<TierEngine>();
    }

    private UniversalEligibilityEngine CreateUniversalEligibilityEngine()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IWorkloadDetector, EmulatorWorkloadDetector>();
        services.AddSingleton<IWorkloadDetector, AdobeWorkloadDetector>();
        services.AddSingleton<IWorkloadDetector, TopazWorkloadDetector>();
        services.AddSingleton<IWorkloadDetector, GameWorkloadDetector>();

        services.AddSingleton<IProfileRepository>(_ => new ProfileRepository(ResolveProfilesDirectory()));
        services.AddSingleton<IEnvironmentDetector>(sp => new EnvironmentDetector(sp.GetServices<IWorkloadDetector>()));
        services.AddSingleton<IProcessSnapshot, ProcessSnapshot>();
        services.AddSingleton<ISafetyPolicy, ProtectedTargetsPolicy>();
        services.AddSingleton<IVerificationEngine, VerificationEngine>();

        services.AddSingleton<IServiceManager, BiosOptimizer.CLI.Adapters.WindowsServiceManager>();
        services.AddSingleton<IRegistryManager, BiosOptimizer.CLI.Adapters.WindowsRegistryManager>();
        services.AddSingleton<IScheduledTaskManager, BiosOptimizer.CLI.Adapters.WindowsScheduledTaskManager>();
        services.AddSingleton<IStartupManager, BiosOptimizer.CLI.Adapters.WindowsStartupManager>();
        services.AddSingleton<IPackageManager, BiosOptimizer.CLI.Adapters.WindowsPackageManager>();
        services.AddSingleton<IAcpiEvaluator, NullAcpiEvaluator>();
        services.AddSingleton<IProviderManager, ProviderManager>();
        services.AddSingleton<IBackupManager>(_backupManager);

        List<Type> handlerTypes = typeof(IActionHandler).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IActionHandler).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (Type handlerType in handlerTypes)
        {
            services.AddSingleton(typeof(IActionHandler), handlerType);
        }

        services.AddSingleton<IActionRegistry>(sp =>
        {
            var registry = new ActionRegistry();
            foreach (var handler in sp.GetServices<IActionHandler>())
            {
                registry.RegisterHandler(handler);
            }
            return registry;
        });

        services.AddSingleton<TierEngine>();
        services.AddSingleton<UniversalEligibilityEngine>();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<UniversalEligibilityEngine>();
    }

    private static object BuildStructuredSystemInfoPayload()
    {
        var hw = HardwareDetectionService.Instance.GetSnapshot(forceRefresh: false);
        var quick = HardwareProfiler.GetQuickProfile(forceRefresh: false);
        var gpus = DxgiGpuReader.EnumerateAdapters();
        var diag = new SystemDiagnosticsReader().ReadCompleteDiagnostics();
        var kernel = HardwareTelemetryCollector.Instance.CollectSnapshot();

        var primaryGpu = hw.PrimaryGpu;
        var primaryDxgi = gpus.Find(g => g.IsDedicatedGpu) ?? gpus.FirstOrDefault();

        bool? virtEnabled = null;
        if (!string.IsNullOrWhiteSpace(diag.Cpu.Virtualization) &&
            !diag.Cpu.Virtualization.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            virtEnabled = diag.Cpu.Virtualization.Contains("Enabled", StringComparison.OrdinalIgnoreCase);
        }

        bool? secureBoot = diag.Security.SecureBootStatus switch
        {
            "GOOD" => true,
            "WARNING" => false,
            _ => (bool?)null
        };
        bool? tpmEnabled = diag.Security.TpmStatus switch
        {
            "GOOD" => true,
            "WARNING" => false,
            _ => (bool?)null
        };
        bool? defenderEnabled = diag.Security.DefenderStatus switch
        {
            "GOOD" => true,
            "WARNING" => false,
            _ => (bool?)null
        };
        bool? hvciEnabled = !string.IsNullOrWhiteSpace(diag.Security.CoreIsolationDetail) &&
                            !diag.Security.CoreIsolationDetail.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
            ? diag.Security.CoreIsolationDetail.Contains("Active", StringComparison.OrdinalIgnoreCase)
            : (bool?)null;

        string ramSpec = !string.IsNullOrWhiteSpace(hw.Memory.SpecFormatted) &&
                         !hw.Memory.SpecFormatted.Equals("System Memory", StringComparison.OrdinalIgnoreCase)
            ? hw.Memory.SpecFormatted
            : string.Join(" ", new[] { diag.Memory.MemoryType, diag.Memory.SpeedText }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "Unknown"));
        if (string.IsNullOrWhiteSpace(ramSpec)) ramSpec = "Unavailable";

        string powerSource = diag.Power.HasBattery
            ? (diag.Power.AcConnected ? "AC Power (Charging / Plugged)" : $"Battery ({diag.Power.BatteryPercentage}%)")
            : (diag.Power.AcConnected ? "AC Power (Desktop)" : diag.Power.BatteryStatus);

        string cpuFreqText = !string.IsNullOrWhiteSpace(diag.Cpu.BaseClock) && diag.Cpu.BaseClock != "Standard Frequency"
            ? $"{diag.Cpu.BaseClock} / {diag.Cpu.MaxClock}"
            : (kernel.CpuClockMhz.HasValue ? $"{kernel.CpuClockMhz.Value:F0} MHz / {(kernel.CpuMaxClockMhz ?? kernel.CpuClockMhz.Value):F0} MHz" : "Unavailable");

        var flatHardware = new
        {
            cpuName = hw.Cpu.Name,
            cpuFrequency = cpuFreqText,
            cpuMaxClock = diag.Cpu.MaxClock,
            cpuL3Cache = diag.Cpu.L3Cache,
            virtualizationEnabled = virtEnabled,
            ramInstalledGb = hw.Memory.InstalledPhysicalGb,
            ramUsableGb = hw.Memory.UsablePhysicalGb,
            ramReservedGb = hw.Memory.HardwareReservedGb,
            ramSpec,
            ramChannels = !string.IsNullOrWhiteSpace(hw.Memory.ChannelMode) && hw.Memory.ChannelMode != "Unknown"
                ? hw.Memory.ChannelMode
                : diag.Memory.ChannelConfig,
            virtualMemoryAvailable = diag.Memory.VirtualMemoryText,
            gpuName = primaryDxgi?.Description ?? primaryGpu.Name,
            vendorName = primaryGpu.VendorName,
            gpuDriverVersion = primaryGpu.DriverVersion,
            driverVersion = primaryGpu.DriverVersion,
            driverDate = primaryGpu.DriverDate,
            wddmVersion = diag.Graphics.WddmVersion,
            osName = hw.Windows.FullDisplayString,
            osVersion = hw.Windows.ProductName,
            osBuild = string.IsNullOrWhiteSpace(diag.Windows.OsBuild) ? hw.Windows.BuildNumber : diag.Windows.OsBuild,
            windowsVersion = hw.Windows.ProductName,
            motherboard = string.IsNullOrWhiteSpace(hw.Motherboard)
                ? $"{diag.Motherboard.Manufacturer} {diag.Motherboard.Model}".Trim()
                : hw.Motherboard,
            baseBoard = $"{diag.Motherboard.Manufacturer} {diag.Motherboard.Model}".Trim(),
            biosVersion = string.IsNullOrWhiteSpace(diag.Motherboard.BiosVendor)
                ? hw.BiosVersion
                : $"{diag.Motherboard.BiosVersion} ({diag.Motherboard.BiosVendor})".Trim(),
            firmwareType = diag.Motherboard.FirmwareType,
            tpmStatus = diag.Motherboard.TpmStatus,
            tpmEnabled,
            secureBoot,
            defenderEnabled,
            hvciEnabled,
            powerSource,
            batteryHealth = diag.Power.HasBattery ? diag.Power.BatteryHealth : "Desktop / No Battery",
            powerPlan = diag.Windows.PowerPlan,
            networkAdapter = diag.Network.ActiveAdapterName,
            networkSpeed = !string.IsNullOrWhiteSpace(diag.Network.LinkSpeed) && diag.Network.LinkSpeed != "Unavailable"
                ? $"{diag.Network.ConnectionType} · {diag.Network.LinkSpeed}"
                : diag.Network.ConnectionType,
            ipv4Address = diag.Network.Ipv4Address,
            storageModel = diag.Storage.PrimaryStorageModel,
            windows = hw.Windows,
            memory = hw.Memory,
            cpu = hw.Cpu
        };

        return new
        {
            hardware = flatHardware,
            snapshot = hw,
            quick,
            gpus,
            diagnostics = diag
        };
    }

    private static string ResolveProfilesDirectory()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "profiles"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "profiles")),
            Path.Combine(Directory.GetCurrentDirectory(), "profiles"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer", "profiles")
        };

        foreach (string dir in candidates)
        {
            if (Directory.Exists(dir))
                return dir;
        }
        return candidates[0];
    }

    private static object? TryParseRawJsonOrString(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return raw;
        }
    }

    private static bool TryGetPayload(JsonElement root, out JsonElement payload)
    {
        if (root.TryGetProperty("payload", out payload) && payload.ValueKind == JsonValueKind.Object)
            return true;
        payload = default;
        return false;
    }

    private static string? GetPayloadString(JsonElement root, string propName)
    {
        if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(propName, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }
        return root.TryGetProperty(propName, out var top) && top.ValueKind == JsonValueKind.String
            ? top.GetString()
            : null;
    }

    private static string? ExtractPayloadJson(JsonElement root) =>
        root.TryGetProperty("payload", out var p) ? p.GetRawText() : null;

    private static List<string> ReadStringArray(JsonElement root, string propName)
    {
        var list = new List<string>();
        JsonElement target = default;
        if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(propName, out var arr))
            target = arr;
        else if (root.TryGetProperty(propName, out var topArr))
            target = topArr;

        if (target.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in target.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
        }
        return list;
    }

    private void ReplyOk(string? requestId, object? result)
    {
        if (requestId == null || _disposed) return;
        _postJson(JsonSerializer.Serialize(new { id = requestId, ok = true, result }, JsonOptions));
    }

    private void ReplyError(string? requestId, string error)
    {
        if (requestId == null || _disposed) return;
        _postJson(JsonSerializer.Serialize(new { id = requestId, ok = false, error }, JsonOptions));
    }

    private sealed record CachedSystemRestoreState(
        DateTime CapturedUtc,
        bool IsAvailable,
        string AvailabilityReason,
        bool IsEnabled,
        string Drive,
        string ProtectionReason);

    private readonly object _srCacheLock = new();
    private CachedSystemRestoreState? _srStatusCache;
    private (DateTime CapturedUtc, List<SystemRestorePointItem> Items)? _srPointsCache;

    private void InvalidateSystemRestoreCache()
    {
        lock (_srCacheLock)
        {
            _srStatusCache = null;
            _srPointsCache = null;
        }
    }

    private CachedSystemRestoreState GetCachedSystemRestoreSnapshot(bool forceRefresh = false)
    {
        lock (_srCacheLock)
        {
            if (!forceRefresh && _srStatusCache != null && (DateTime.UtcNow - _srStatusCache.CapturedUtc) < TimeSpan.FromSeconds(60))
                return _srStatusCache;

            var probed = RunOnBoundedMtaThread(
                () =>
                {
                    var (avail, reason) = WindowsSystemRestoreEngine.Instance.CheckSystemRestoreAvailability();
                    var (enabled, drive, protReason) = WindowsSystemRestoreEngine.Instance.CheckSystemProtectionStatus();
                    return new CachedSystemRestoreState(DateTime.UtcNow, avail, reason, enabled, drive, protReason);
                },
                TimeSpan.FromSeconds(10),
                new CachedSystemRestoreState(
                    DateTime.UtcNow,
                    false,
                    "Timed out querying Windows System Restore availability.",
                    false,
                    Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:",
                    "Timed out querying System Protection status."));

            _srStatusCache = probed;
            return probed;
        }
    }

    private List<SystemRestorePointItem> GetCachedRestorePoints(bool forceRefresh = false)
    {
        lock (_srCacheLock)
        {
            if (!forceRefresh && _srPointsCache.HasValue && (DateTime.UtcNow - _srPointsCache.Value.CapturedUtc) < TimeSpan.FromSeconds(60))
                return _srPointsCache.Value.Items;

            var list = RunOnBoundedMtaThread(
                () => WindowsSystemRestoreEngine.Instance.EnumerateRestorePoints(),
                TimeSpan.FromSeconds(12),
                new List<SystemRestorePointItem>());

            _srPointsCache = (DateTime.UtcNow, list);
            return list;
        }
    }

    private static T RunOnBoundedMtaThread<T>(Func<T> work, TimeSpan timeout, T fallbackOnTimeout)
    {
        T result = fallbackOnTimeout;
        Exception? captured = null;
        var done = new ManualResetEventSlim(false);
        int remainingOwners = 2;

        void ReleaseOwner()
        {
            if (Interlocked.Decrement(ref remainingOwners) == 0)
            {
                try { done.Dispose(); } catch { }
            }
        }

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                try { done.Set(); } catch (ObjectDisposedException) { }
                ReleaseOwner();
            }
        })
        {
            IsBackground = true,
            Name = "EO-MtaWorker"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        bool signaled = false;
        try
        {
            signaled = done.Wait(timeout);
        }
        finally
        {
            ReleaseOwner();
        }

        if (!signaled)
        {
            return fallbackOnTimeout;
        }

        if (captured != null)
            throw new InvalidOperationException(captured.Message, captured);
        return result;
    }

    public void PauseBackgroundUiStreams()
    {
        if (_disposed) return;
        _telemetry.ResetSubscriptions();
    }

    public void Dispose()
    {
        if (ActiveInstance == this) ActiveInstance = null;
        if (_disposed) return;
        _disposed = true;
        try { _lifetimeCts.Cancel(); } catch { }
        _telemetry.Dispose();
        _workload.Dispose();
        _inputSampler.Dispose();
        _ipc.Dispose();
        _lifetimeCts.Dispose();
    }
}
