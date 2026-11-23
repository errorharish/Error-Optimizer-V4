using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.IPC.Contracts;

namespace ErrorOptimizerPrototypeHost.Services;

public enum BackendConnectionState
{
    Connecting,
    Connected,
    Degraded,
    Offline
}

public enum ServiceScmState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Paused
}

public sealed record ServiceHealthSnapshot(
    bool Healthy,
    BackendConnectionState State,
    string ProtocolVersion,
    string ServiceVersion,
    string PipeName,
    string Message,
    double LatencyMs,
    IpcHandshakeResponse? Handshake,
    ServiceScmState ScmState = ServiceScmState.Unknown);

public sealed record IpcSendOutcome(
    IpcResponse Response,
    bool PipeReachable,
    bool MayHaveMutated = false);

/// <summary>
/// Named-pipe IPC client for <c>BiosOptimizer.Service</c> (<c>BiosOptimizer_IPC_v2_{session}</c> and fallback <c>default</c>).
/// Implements the v2 handshake, bounded retry/backoff, per-message timeouts, and clean streaming support.
/// </summary>
public sealed class ServiceIpcClient : IDisposable
{
    private const int ConnectTimeoutMs = 1500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _sessionId;
    private readonly string[] _candidatePipes;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private BackendConnectionState _state = BackendConnectionState.Connecting;
    private string? _activePipeName;
    private IpcHandshakeResponse? _lastHandshake;
    private string _lastMessage = "Service check not yet performed.";
    private bool _disposed;

    private static bool IsPipeListed(string pipeName)
    {
        try
        {
            foreach (string _ in Directory.EnumerateFiles(@"\\.\pipe\", pipeName))
                return true;
            return false;
        }
        catch
        {
            // Fall back to direct ConnectAsync probe if kernel pipe enumeration is blocked.
            return true;
        }
    }

    public ServiceIpcClient()
    {
        _sessionId = ResolveSessionIdFromArgs(Environment.GetCommandLineArgs());
        _candidatePipes = !string.IsNullOrWhiteSpace(_sessionId) &&
                          !_sessionId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? new[] { $"BiosOptimizer_IPC_v2_{_sessionId}", "BiosOptimizer_IPC_v2_default" }
            : new[] { "BiosOptimizer_IPC_v2_default" };
    }

    public BackendConnectionState State => _state;
    public string ActivePipeName => _activePipeName ?? _candidatePipes[0];
    public IpcHandshakeResponse? LastHandshake => _lastHandshake;
    public string LastMessage => _lastMessage;

    private static string ResolveSessionIdFromArgs(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
            {
                string val = arg["--session=".Length..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }
        return "default";
    }

    public static int GetDefaultTimeoutMs(IpcMessageType type) => type switch
    {
        IpcMessageType.GetVersion or
        IpcMessageType.GetDashboard or
        IpcMessageType.GetSystemToolsCatalog or
        IpcMessageType.GetSystemToolJobStatus or
        IpcMessageType.CancelSystemToolJob or
        IpcMessageType.GetSystemToolReport or
        IpcMessageType.V4GetLiveMetrics => 5000,

        IpcMessageType.GetSystemInfo or
        IpcMessageType.GetHardwareInfo or
        IpcMessageType.GetMachineProfile or
        IpcMessageType.GetStartupItems or
        IpcMessageType.GetServices or
        IpcMessageType.GetNetworkStatus or
        IpcMessageType.GetStorageStatus or
        IpcMessageType.ScanCleaner or
        IpcMessageType.GetDebloatItems or
        IpcMessageType.GetBiosCapabilities or
        IpcMessageType.GetProcessCandidates or
        IpcMessageType.PlanInputOptimization or
        IpcMessageType.PlanRegistryTweak or
        IpcMessageType.PlanNetworkOptimization or
        IpcMessageType.ScanStorageCategories or
        IpcMessageType.GetPowerPlans or
        IpcMessageType.GetRestorePoints or
        IpcMessageType.CheckSystemRestoreStatus or
        IpcMessageType.GetSystemProtectionStatus or
        IpcMessageType.CreateApplicationBackup or
        IpcMessageType.ValidateBackupIntegrity or
        IpcMessageType.DeleteBackup or
        IpcMessageType.V4ScanProcessIntelligence or
        IpcMessageType.V4GetMaximumPerformancePlan => 30000,

        IpcMessageType.EnableSystemProtection or
        IpcMessageType.DisableSystemProtection => 60000,

        IpcMessageType.CreateRestorePoint or
        IpcMessageType.ApplyTier or
        IpcMessageType.RestoreTier or
        IpcMessageType.RestoreBackup or
        IpcMessageType.ExecuteOneClick or
        IpcMessageType.ApplyDebloatItems or
        IpcMessageType.ApplyStorageCleanup or
        IpcMessageType.ScanLargeFiles or
        IpcMessageType.ScanDuplicateFiles or
        IpcMessageType.ExecuteServicingTool or
        IpcMessageType.ExecuteSystemTool or
        IpcMessageType.V4ExecuteMaximumPerformancePass or
        IpcMessageType.V4ExecuteFullOptimization or
        IpcMessageType.V4RollbackMaximumPerformance or
        IpcMessageType.TriggerAutoOptimizeNow => 120000,

        _ => 15000
    };

    public async Task<ServiceHealthSnapshot> CheckHealthAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == BackendConnectionState.Offline)
                _state = BackendConnectionState.Connecting;
        }
        finally
        {
            _stateLock.Release();
        }

        IpcResponse resp = await SendAsync(IpcMessageType.GetVersion, null, 5000, ct).ConfigureAwait(false);
        sw.Stop();

        if (!resp.Success || string.IsNullOrWhiteSpace(resp.Data))
        {
            var scmState = InspectScmState();
            string err = !string.IsNullOrWhiteSpace(resp.ErrorMessage)
                ? resp.ErrorMessage
                : scmState switch
                {
                    ServiceScmState.StartPending => "BiosOptimizer.Service is starting (StartPending); waiting for IPC pipe.",
                    ServiceScmState.StopPending => "BiosOptimizer.Service is stopping (StopPending).",
                    ServiceScmState.Stopped => "BiosOptimizer.Service is installed but currently stopped.",
                    ServiceScmState.NotInstalled => "BiosOptimizer.Service is not installed in SCM.",
                    _ => "BiosOptimizer.Service named pipe did not respond."
                };

            BackendConnectionState connState = scmState == ServiceScmState.StartPending
                ? BackendConnectionState.Connecting
                : BackendConnectionState.Offline;

            UpdateConnectionState(connState, null, err, null);
            return new ServiceHealthSnapshot(
                Healthy: false,
                State: connState,
                ProtocolVersion: "v2",
                ServiceVersion: string.Empty,
                PipeName: ActivePipeName,
                Message: err,
                LatencyMs: sw.Elapsed.TotalMilliseconds,
                Handshake: _lastHandshake,
                ScmState: scmState);
        }

        IpcHandshakeResponse? handshake = null;
        string serviceVersion = resp.Data.Trim();
        string protocolVersion = "v2";
        bool protocolValid = true;
        string statusMsg;

        try
        {
            if (resp.Data.TrimStart().StartsWith('{'))
            {
                handshake = JsonSerializer.Deserialize<IpcHandshakeResponse>(resp.Data, JsonOptions);
                if (handshake != null)
                {
                    if (!string.IsNullOrWhiteSpace(handshake.ServiceVersion))
                        serviceVersion = handshake.ServiceVersion;
                    if (!string.IsNullOrWhiteSpace(handshake.ProtocolVersion))
                    {
                        protocolVersion = handshake.ProtocolVersion;
                        protocolValid = string.Equals(protocolVersion, "v2", StringComparison.OrdinalIgnoreCase) ||
                                        protocolVersion.StartsWith("2.", StringComparison.OrdinalIgnoreCase);
                    }
                    statusMsg = protocolValid
                        ? $"Connected to {handshake.ServiceName} v{serviceVersion} via {ActivePipeName} ({handshake.Status})"
                        : $"Protocol mismatch on {ActivePipeName}: expected v2, got '{protocolVersion}'";
                }
                else
                {
                    statusMsg = $"Connected to BiosOptimizer.Service via {ActivePipeName}";
                }
            }
            else
            {
                statusMsg = $"Connected to BiosOptimizer.Service ({serviceVersion}) via {ActivePipeName}";
            }
        }
        catch (JsonException)
        {
            statusMsg = $"Connected to BiosOptimizer.Service ({serviceVersion}) via {ActivePipeName}";
        }

        var nextState = protocolValid ? BackendConnectionState.Connected : BackendConnectionState.Degraded;
        UpdateConnectionState(nextState, _activePipeName, statusMsg, handshake);

        return new ServiceHealthSnapshot(
            Healthy: protocolValid,
            State: nextState,
            ProtocolVersion: protocolVersion,
            ServiceVersion: serviceVersion,
            PipeName: ActivePipeName,
            Message: statusMsg,
            LatencyMs: sw.Elapsed.TotalMilliseconds,
            Handshake: handshake);
    }

    public async Task<IpcResponse> SendAsync(
        IpcMessageType type,
        string? payload = null,
        int? timeoutMsOverride = null,
        CancellationToken ct = default)
    {
        var outcome = await SendDetailedAsync(type, payload, timeoutMsOverride, ct).ConfigureAwait(false);
        return outcome.Response;
    }

    public async Task<IpcSendOutcome> SendDetailedAsync(
        IpcMessageType type,
        string? payload = null,
        int? timeoutMsOverride = null,
        CancellationToken ct = default)
    {
        if (_disposed)
            return new IpcSendOutcome(Fail("IPC client is disposed."), PipeReachable: false);

        int timeoutMs = timeoutMsOverride ?? GetDefaultTimeoutMs(type);
        var request = new IpcMessage { Type = type, Payload = payload };
        string serialized = JsonSerializer.Serialize(request) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(serialized);

        string lastError = "BiosOptimizer.Service is not running.";
        bool anyPipeListed = false;
        ServiceScmState scmState = InspectScmState();

        // When SCM reports StartPending and pipe hasn't opened yet, wait briefly before giving up to fallback.
        if (scmState == ServiceScmState.StartPending && !_candidatePipes.Any(IsPipeListed))
        {
            UpdateConnectionState(BackendConnectionState.Connecting, null, "Waiting for BiosOptimizer.Service startup...", _lastHandshake);
            for (int waitIdx = 0; waitIdx < 14 && !ct.IsCancellationRequested; waitIdx++)
            {
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { break; }
                if (_candidatePipes.Any(IsPipeListed))
                    break;
            }
            scmState = InspectScmState();
        }

        if (scmState == ServiceScmState.StopPending)
        {
            lastError = "BiosOptimizer.Service is currently stopping (StopPending).";
            UpdateConnectionState(BackendConnectionState.Offline, null, lastError, _lastHandshake);
            return new IpcSendOutcome(Fail(lastError), PipeReachable: false, MayHaveMutated: false);
        }

        if (scmState is ServiceScmState.Stopped or ServiceScmState.NotInstalled)
        {
            lastError = scmState == ServiceScmState.Stopped
                ? "BiosOptimizer.Service is installed but currently stopped."
                : "BiosOptimizer.Service is not installed in SCM.";
            UpdateConnectionState(BackendConnectionState.Offline, null, lastError, _lastHandshake);
            return new IpcSendOutcome(Fail(lastError), PipeReachable: false, MayHaveMutated: false);
        }

        foreach (string pipeName in _candidatePipes)
        {
            anyPipeListed = true;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                bool bytesSent = false;

                try
                {
                    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(ConnectTimeoutMs, timeoutCts.Token).ConfigureAwait(false);

                    await pipe.WriteAsync(bytes, 0, bytes.Length, timeoutCts.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                    bytesSent = true;

                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, leaveOpen: true);
                    string? responseLine = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(responseLine))
                    {
                        lastError = $"Pipe '{pipeName}' closed without returning a response.";
                        // Never replay after bytesSent on empty/broken response to prevent duplicate execution.
                        UpdateConnectionState(BackendConnectionState.Degraded, pipeName, lastError, _lastHandshake);
                        return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: true);
                    }

                    var parsed = JsonSerializer.Deserialize<IpcResponse>(responseLine, JsonOptions);
                    if (parsed == null)
                    {
                        lastError = $"Pipe '{pipeName}' returned malformed JSON.";
                        UpdateConnectionState(BackendConnectionState.Degraded, pipeName, lastError, _lastHandshake);
                        return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: true);
                    }

                    _activePipeName = pipeName;
                    if (_state != BackendConnectionState.Connected && type != IpcMessageType.GetVersion)
                        UpdateConnectionState(BackendConnectionState.Connected, pipeName, "Service pipe connected.", _lastHandshake);

                    return new IpcSendOutcome(parsed, PipeReachable: true, MayHaveMutated: false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = $"Timed out waiting for '{type}' on pipe '{pipeName}' ({timeoutMs} ms).";
                    UpdateConnectionState(BackendConnectionState.Degraded, pipeName, lastError, _lastHandshake);
                    return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: bytesSent);
                }
                catch (System.TimeoutException)
                {
                    lastError = $"Pipe '{pipeName}' connection timed out.";
                    if (bytesSent)
                        return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: true);
                }
                catch (IOException ex)
                {
                    lastError = $"Pipe I/O error on '{pipeName}': {ex.Message}";
                    if (bytesSent)
                    {
                        UpdateConnectionState(BackendConnectionState.Degraded, pipeName, lastError, _lastHandshake);
                        return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: true);
                    }
                    if (attempt == 0 && !ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(120, ct).ConfigureAwait(false); } catch { }
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = $"Access denied on pipe '{pipeName}': {ex.Message}";
                    return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: false);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    if (bytesSent)
                        return new IpcSendOutcome(Fail(lastError), PipeReachable: true, MayHaveMutated: true);
                }
            }
        }

        if (!anyPipeListed)
        {
            lastError = scmState switch
            {
                ServiceScmState.StartPending => $"BiosOptimizer.Service is still starting; pipe ({ActivePipeName}) is pending.",
                ServiceScmState.StopPending => $"BiosOptimizer.Service is stopping; pipe ({ActivePipeName}) is unavailable.",
                ServiceScmState.Stopped => $"BiosOptimizer.Service is stopped; pipe ({ActivePipeName}) is not active.",
                ServiceScmState.NotInstalled => $"BiosOptimizer.Service is not installed; pipe ({ActivePipeName}) is not active.",
                _ => $"BiosOptimizer.Service named pipe ({ActivePipeName}) is not active."
            };
        }

        UpdateConnectionState(
            scmState == ServiceScmState.StartPending ? BackendConnectionState.Connecting : BackendConnectionState.Offline,
            null,
            lastError,
            _lastHandshake);
        return new IpcSendOutcome(Fail(lastError), PipeReachable: false, MayHaveMutated: false);
    }

    public static ServiceScmState InspectScmState(out string resolvedServiceName)
    {
        resolvedServiceName = "BiosOptimizer.Service";
        string[] candidates = { "BiosOptimizer.Service", "BiosOptimizerService" };

        foreach (var name in candidates)
        {
            try
            {
                using var sc = new ServiceController(name);
                var status = sc.Status;
                resolvedServiceName = name;
                return status switch
                {
                    ServiceControllerStatus.Running => ServiceScmState.Running,
                    ServiceControllerStatus.StartPending => ServiceScmState.StartPending,
                    ServiceControllerStatus.StopPending => ServiceScmState.StopPending,
                    ServiceControllerStatus.Stopped => ServiceScmState.Stopped,
                    ServiceControllerStatus.Paused => ServiceScmState.Paused,
                    _ => ServiceScmState.Unknown
                };
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SCM QUERY ERROR] {name}: {ex.Message}");
                return ServiceScmState.Unknown;
            }
        }

        return ServiceScmState.NotInstalled;
    }

    public static ServiceScmState InspectScmState()
    {
        return InspectScmState(out _);
    }

    public static bool TryStartService(out string diagnostic)
    {
        var scm = InspectScmState(out string serviceName);
        if (scm == ServiceScmState.NotInstalled)
        {
            diagnostic = "BiosOptimizer.Service is not registered in Windows Service Control Manager.";
            return false;
        }

        if (scm == ServiceScmState.Running)
        {
            diagnostic = $"Windows service '{serviceName}' is already running.";
            return true;
        }

        if (scm == ServiceScmState.StartPending)
        {
            diagnostic = $"Windows service '{serviceName}' is already starting (StartPending).";
            return true;
        }

        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Start();
            diagnostic = $"Started service '{serviceName}' via ServiceController.";
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"start \"{serviceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(3000);
                    if (proc.ExitCode == 0)
                    {
                        diagnostic = $"Launched sc.exe start for '{serviceName}'.";
                        return true;
                    }
                    string err = proc.StandardError.ReadToEnd().Trim();
                    diagnostic = $"sc.exe start '{serviceName}' exited with code {proc.ExitCode}: {err}";
                    return false;
                }
            }
            catch { }

            diagnostic = $"Failed to start '{serviceName}': {ex.Message}";
            return false;
        }
    }

    public async Task<bool> WaitForServiceInitializationAsync(int timeoutMs = 8000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            if (_candidatePipes.Any(IsPipeListed))
                return true;

            var scm = InspectScmState();
            if (scm is ServiceScmState.Stopped or ServiceScmState.NotInstalled)
                return false;

            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { break; }
        }

        return _candidatePipes.Any(IsPipeListed);
    }

    public async IAsyncEnumerable<IpcResponse> StreamAsync(
        IpcMessageType type,
        string? payload = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed)
        {
            yield return Fail("IPC client is disposed.");
            yield break;
        }

        NamedPipeClientStream? connectedPipe = null;
        string connectedName = _candidatePipes[0];
        string lastError = "BiosOptimizer.Service is unreachable.";

        foreach (string pipeName in _candidatePipes)
        {
            ct.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeoutMs);
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new IpcMessage { Type = type, Payload = payload }) + "\n");
                await pipe.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);

                connectedPipe = pipe;
                connectedName = pipeName;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                pipe.Dispose();
            }
        }

        if (connectedPipe == null)
        {
            UpdateConnectionState(BackendConnectionState.Offline, null, lastError, _lastHandshake);
            yield return Fail(lastError);
            yield break;
        }

        _activePipeName = connectedName;
        using (connectedPipe)
        using (var reader = new StreamReader(connectedPipe, Encoding.UTF8, false, 65536, leaveOpen: true))
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                string? readError = null;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    line = null;
                    readError = $"Pipe stream interrupted: {ex.Message}";
                }

                if (readError != null)
                {
                    yield return Fail(readError);
                    yield break;
                }

                if (line == null)
                    yield break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                IpcResponse? response = null;
                try
                {
                    response = JsonSerializer.Deserialize<IpcResponse>(line, JsonOptions);
                }
                catch { }

                if (response != null)
                    yield return response;
            }
        }
    }

    private void UpdateConnectionState(
        BackendConnectionState nextState,
        string? pipeName,
        string message,
        IpcHandshakeResponse? handshake)
    {
        _state = nextState;
        if (!string.IsNullOrWhiteSpace(pipeName))
            _activePipeName = pipeName;
        _lastMessage = message;
        if (handshake != null)
            _lastHandshake = handshake;
    }

    private static IpcResponse Fail(string error) => new() { Success = false, ErrorMessage = error };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateLock.Dispose();
    }
}
