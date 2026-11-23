using System;







using System.IO;







using System.IO.Pipes;







using System.Text;







using System.Text.Json;







using System.Threading;







using System.Threading.Tasks;







using System.Threading.Channels;







using BiosOptimizer.Core.Interfaces;







using BiosOptimizer.Core.Services;







using BiosOptimizer.IPC.Contracts;







using BiosOptimizer.Core.Implementations;







using BiosOptimizer.Core.Implementations.Storage;







using BiosOptimizer.Core.Implementations.Diagnostics;







using BiosOptimizer.Core.Models;







using BiosOptimizer.Core.Implementations.Cleaners;







using Microsoft.Extensions.Hosting;







using Microsoft.Extensions.Logging;







using Microsoft.Extensions.DependencyInjection;







namespace BiosOptimizer.Service







{







    public partial class NamedPipeServer : BackgroundService







    {







        private readonly string PipeName;







        private readonly string _sessionId;







        private readonly ILogger<NamedPipeServer> _logger;







        private readonly IServiceProvider _services;

        private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };







        public NamedPipeServer(







            ILogger<NamedPipeServer> logger, 







            IServiceProvider services,







            Microsoft.Extensions.Configuration.IConfiguration config)







        {







            _logger = logger;







            _services = services;







            _sessionId = config["session"] ?? "default";







            PipeName = $"BiosOptimizer_IPC_v2_{_sessionId}";







        }







        private readonly System.Collections.Concurrent.ConcurrentBag<NamedPipeServerStream> _activePipes = new();







        protected override async Task ExecuteAsync(CancellationToken stoppingToken)







        {







            _logger.LogInformation("PIPE_INIT_STARTED");







            







            if (await PerformSelfTestAsync(stoppingToken))







            {







                _logger.LogInformation("IPC_READY");







            }







            else







            {







                _logger.LogWarning("SERVICE_ERROR: Self-test failed, but continuing listener...");







            }







            _logger.LogInformation("PIPE_LISTENING on '{Pipe}'", PipeName);







            







            int consecutiveUacFailures = 0;







            var pipeSecurity = new System.IO.Pipes.PipeSecurity();







            // Local System







            pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(







                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),







                System.IO.Pipes.PipeAccessRights.FullControl,







                System.Security.AccessControl.AccessControlType.Allow));







                







            // Builtin Administrators







            pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(







                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),







                System.IO.Pipes.PipeAccessRights.FullControl,







                System.Security.AccessControl.AccessControlType.Allow));







                







            // Everyone (World) - allows GUI running as any user to connect







                        // Explicit Security ACL: Restrict to Authenticated Users and Local Administrators
            // WorldSid (Everyone), AnonymousSid, and NetworkSid access are strictly denied/excluded.
            pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                System.IO.Pipes.PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.IO.Pipes.PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AnonymousSid, null),
                System.IO.Pipes.PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Deny));

            pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null),
                System.IO.Pipes.PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Deny));







            try







            {







                while (!stoppingToken.IsCancellationRequested)







                {







                    try







                    {







                        var pipe = System.IO.Pipes.NamedPipeServerStreamAcl.Create(







                            PipeName, PipeDirection.InOut,







                            NamedPipeServerStream.MaxAllowedServerInstances,







                            // BYTE mode: required for ReadLineAsync-based streaming in the client







                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,







                            0, 0, pipeSecurity);







                            







                        consecutiveUacFailures = 0; // Reset on successful pipe creation







                        _activePipes.Add(pipe);







                        _logger.LogInformation("PIPE_CREATED");







                        







                        await pipe.WaitForConnectionAsync(stoppingToken);







                        _logger.LogInformation("PIPE_CLIENT_CONNECTED");







                        _ = HandleClientAsync(pipe, stoppingToken);







                    }







                    catch (UnauthorizedAccessException ex)







                    {







                        consecutiveUacFailures++;







                        _logger.LogWarning("PIPE_WARNING: UnauthorizedAccessException. Stale handle or duplicate instance. Retrying...");







                        







                        // Wait up to 100 seconds for the previous long-running client to disconnect







                        if (consecutiveUacFailures >= 100)







                        {







                            _logger.LogCritical(ex, "PIPE_FATAL: Maximum UAC failures reached. Stopping pipe listener.");







                            break;







                        }







                        







                        await Task.Delay(1000, stoppingToken);







                    }







                    catch (System.IO.IOException)







                    {







                        // Likely MaxAllowedServerInstances reached







                        _logger.LogWarning("PIPE_WARNING: IOException. Max instances reached or pipe in use. Retrying in 2s...");







                        await Task.Delay(2000, stoppingToken);







                    }







                    catch (OperationCanceledException) { break; }







                    catch (Exception ex)







                    {







                        _logger.LogError(ex, "PIPE_ERROR: Unexpected error in listener.");







                        await Task.Delay(2000, stoppingToken);







                    }







                }







            } catch (Exception ex) { _logger.LogError(ex, "PIPE_FATAL_ERROR"); } finally {







                _logger.LogInformation("PIPE_SHUTDOWN");







                foreach (var active in _activePipes)







                {







                    try { active.Dispose(); } catch { }







                }







            }







        }







        







        // Use a unique test pipe name distinct from the main listener to avoid kernel handle conflicts







        private const string SelfTestPipeName = "BiosOptimizer_IPC_v2_selftest";







        private async Task<bool> PerformSelfTestAsync(CancellationToken ct)







        {







            try







            {







                _logger.LogInformation("IPC_STARTING (Self-test on '{TestPipe}')", SelfTestPipeName);







                







                var pipeSecurity = new System.IO.Pipes.PipeSecurity();







                pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(







                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),







                    System.IO.Pipes.PipeAccessRights.FullControl,







                    System.Security.AccessControl.AccessControlType.Allow));







                pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(







                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),







                    System.IO.Pipes.PipeAccessRights.FullControl,







                    System.Security.AccessControl.AccessControlType.Allow));







                pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(







                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),







                    System.IO.Pipes.PipeAccessRights.ReadWrite,







                    System.Security.AccessControl.AccessControlType.Allow));







                // Use a SEPARATE test pipe name -Â never the same as PipeName -Â to avoid







                // creating a zombie handle that blocks the main listener's first Create call.







                using var server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(







                    SelfTestPipeName, PipeDirection.InOut,







                    1,







                    PipeTransmissionMode.Message, PipeOptions.Asynchronous,







                    0, 0, pipeSecurity);







                using var client = new System.IO.Pipes.NamedPipeClientStream(".", SelfTestPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);







                







                using var cts = new CancellationTokenSource(3000);







                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct);







                var connectTask = client.ConnectAsync(linked.Token);







                var waitTask = server.WaitForConnectionAsync(linked.Token);







                







                await Task.WhenAll(connectTask, waitTask);







                _logger.LogInformation("IPC_SELF_TEST_PASSED");







                return true;







            }







            catch (Exception ex)







            {







                _logger.LogWarning(ex, "PIPE_WARNING: Self-test failed (non-fatal -Â main listener will start anyway).");







                return false;







            }







        }







        private static bool IsPrivilegedOperation(IpcMessageType type) => type switch
        {
            IpcMessageType.SetServiceStartupType => true,
            IpcMessageType.ChangeServiceState => true,
            IpcMessageType.DisableStartupItem => true,
            IpcMessageType.EnableStartupItem => true,
            IpcMessageType.DisableSystemProtection => true,
            IpcMessageType.EnableSystemProtection => true,
            IpcMessageType.CreateRestorePoint => true,
            IpcMessageType.RestoreBackup => true,
            IpcMessageType.DeleteBackup => true,
            IpcMessageType.DeleteStorageFile => true,
            IpcMessageType.EmptyRecycleBin => true,
            IpcMessageType.ApplyStorageCleanup => true,
            IpcMessageType.TerminateProcesses => true,
            IpcMessageType.ApplyBiosSetting => true,
            IpcMessageType.ApplyTier => true,
            IpcMessageType.RestoreTier => true,
            IpcMessageType.ApplyCleaner => true,
            IpcMessageType.ApplyDebloatItems => true,
            IpcMessageType.RestoreAllSystem => true,
            IpcMessageType.ApplyInputOptimization => true,
            IpcMessageType.RestoreInputOptimization => true,
            IpcMessageType.ApplyRegistryTweak => true,
            IpcMessageType.RestoreRegistryTweak => true,
            IpcMessageType.ApplyNetworkOptimization => true,
            IpcMessageType.RestoreNetworkOptimization => true,
            IpcMessageType.ExecuteServicingTool => true,
            IpcMessageType.ApplyPowerPlan => true,
            IpcMessageType.RestorePowerPlan => true,
            IpcMessageType.EnableUltimatePerformance => true,
            IpcMessageType.CreateCustomAiPlan => true,
            IpcMessageType.DeleteCustomAiPlan => true,
            IpcMessageType.V4ExecuteMaximumPerformancePass => true,
            IpcMessageType.V4ExecuteFullOptimization => true,
            IpcMessageType.V4RollbackMaximumPerformance => true,
            IpcMessageType.V4SuppressWorkloadDrift => true,
            IpcMessageType.ExecuteOneClick => true,
            IpcMessageType.ExecuteSystemTool => true,
            IpcMessageType.CancelSystemToolJob => true,
            IpcMessageType.ExecutePerformanceTuning => true,
            IpcMessageType.RollbackPerformanceTuning => true,
            IpcMessageType.RequestUefiReboot => true,
            IpcMessageType.ScheduleSystemRestart => true,
            IpcMessageType.CancelScheduledRestart => true,
            _ => false
        };

private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)







        {







            // A bounded channel serialises ALL writes to the pipe on a single loop.







            // This prevents concurrent pipe.Write from the Progress<T> callback thread







            // clashing with the final response write -Â which was the root cause of







            // streaming silently failing.







            var writeChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });







            // Dedicated write pump -Â the only place that touches pipe.WriteAsync







            async Task WritePumpAsync()







            {







                await foreach (var chunk in writeChannel.Reader.ReadAllAsync(ct))







                {







                    if (!pipe.IsConnected) break;







                    try







                    {







                        await pipe.WriteAsync(chunk, 0, chunk.Length, ct);







                        await pipe.FlushAsync(ct);







                    }







                    catch { break; }







                }







            }







            var writePump = WritePumpAsync();







            try







            {







                using var reader = new System.IO.StreamReader(pipe, Encoding.UTF8, false, 65536, leaveOpen: true);

                string clientName = "InteractiveUser";
                bool isClientAdmin = true;
                try
                {
                    pipe.RunAsClient(() =>
                    {
                        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                        if (id != null)
                        {
                            clientName = id.Name;
                            var principal = new System.Security.Principal.WindowsPrincipal(id);
                            isClientAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                        }
                    });
                }
                catch { }







                while (pipe.IsConnected && !ct.IsCancellationRequested)







                {







                    var line = await reader.ReadLineAsync();







                    if (line == null) break;







                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Maximum message size security limit (1MB) to protect against DoS
                    if (line.Length > 1048576)
                    {
                        var tooLargeResp = Fail("Message rejected: payload exceeds 1MB limit.");
                        writeChannel.Writer.TryWrite(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tooLargeResp) + "\n"));
                        break;
                    }







                    IpcResponse response;







                    try







                    {







                        var msg = JsonSerializer.Deserialize<IpcMessage>(line);







                        System.Console.WriteLine($"[SERVICE] REQUEST_RECEIVED type={msg?.Type}");







                        // Progress callback: enqueue bytes onto the channel (non-blocking, thread-safe)







                        Action<IpcResponse> progressCallback = (resp) =>







                        {







                            var respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp) + "\n");







                            writeChannel.Writer.TryWrite(respBytes);







                        };







                        if (msg != null && IsPrivilegedOperation(msg.Type) && !isClientAdmin)
                        {
                            _logger.LogWarning("Access Denied: Unprivileged caller '{Client}' attempted privileged action '{Type}'", clientName, msg.Type);
                            response = Fail($"Access Denied: Operation '{msg.Type}' requires elevated administrative privileges.");
                        }
                        else
                        {
                            response = await RouteAsync(msg!, progressCallback, ct);
                        }







                    }







                    catch (Exception ex)







                    {







                        response = Fail("Internal error: " + ex.Message);







                        System.Console.WriteLine($"[SERVICE] ROUTE_ERROR: {ex.Message}");







                    }







                    // Enqueue final response through the same channel







                    var finalBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n");







                    writeChannel.Writer.TryWrite(finalBytes);







                }







            }







            catch (Exception ex) when (ex is not OperationCanceledException)







            {







                _logger.LogWarning(ex, "Client handler exception.");







            }







            finally







            {







                writeChannel.Writer.TryComplete();







                try { await writePump; } catch { }







                pipe.Dispose();







            }







        }







        private async Task<IpcResponse> RouteAsync(IpcMessage msg, Action<IpcResponse> progress, CancellationToken ct)







        {







            if (msg == null) return Fail("Null message");







            return msg.Type switch







            {







                IpcMessageType.GetVersion             => GetAssemblyVersion(),







                IpcMessageType.GetOptimizationScore   => GetScore(),







                IpcMessageType.GetOptimizationSummary => GetScore(),







                IpcMessageType.GetDashboard           => GetLiveMetrics(),







                IpcMessageType.GetSystemInfo          => GetSystemInfo(),







                IpcMessageType.GetHardwareInfo        => GetSystemInfo(),







                IpcMessageType.GetStartupItems        => GetStartupItems(),







                IpcMessageType.EnableStartupItem      => EnableStartupItem(msg.Payload),







                IpcMessageType.DisableStartupItem     => DisableStartupItem(msg.Payload),







                IpcMessageType.GetServices            => GetServices(),







                IpcMessageType.SetServiceStartupType  => SetServiceStartupType(msg.Payload),







                IpcMessageType.ChangeServiceState     => ChangeServiceState(msg.Payload),







                IpcMessageType.GetNetworkStatus       => GetNetworkStatus(),







                IpcMessageType.GetStorageStatus       => GetStorage(),







                IpcMessageType.PreviewTier            => await PreviewTierAsync(msg.Payload ?? "Normal", ct),







                IpcMessageType.ApplyTier              => await ApplyTierAsync(msg.Payload, progress, ct),







                IpcMessageType.RestoreTier            => await RestoreTierRealAsync(msg.Payload, ct),







                IpcMessageType.ScanCleaner            => ScanCleaner(),







                IpcMessageType.ApplyCleaner           => ApplyCleaner(),







                IpcMessageType.GetDebloatItems        => GetDebloatItems(),







                IpcMessageType.ApplyDebloatItems      => await ApplyDebloatItemsAsync(msg.Payload!, progress, ct),







                IpcMessageType.GetBiosCapabilities    => await GetBiosCapabilitiesAsync(ct),







                IpcMessageType.ApplyBiosSetting       => await ApplyTierAsync("BiosSafe", progress, ct),







                IpcMessageType.GetBackups             => GetBackupsReal(msg.Payload),







                IpcMessageType.RestoreBackup          => await RestoreBackupRealAsync(msg.Payload, ct),







                IpcMessageType.GetMachineProfile      => GetMachineProfile(),







                IpcMessageType.GetProcessCandidates   => await GetProcessCandidatesAsync(),







                IpcMessageType.TerminateProcesses     => TerminateProcesses(msg.Payload),







                IpcMessageType.RestoreAllSystem       => RestoreAllSystem(),







                IpcMessageType.PlanInputOptimization  => await PlanInputOptimizationAsync(),







                IpcMessageType.ApplyInputOptimization => await ApplyInputOptimizationAsync(msg.Payload),







                IpcMessageType.RestoreInputOptimization => await RestoreInputOptimizationAsync(),







                IpcMessageType.PlanRegistryTweak        => await PlanRegistryTweakAsync(),







                IpcMessageType.ApplyRegistryTweak       => await ApplyRegistryTweakAsync(msg.Payload),







                IpcMessageType.RestoreRegistryTweak     => await RestoreRegistryTweakAsync(msg.Payload),







                IpcMessageType.PlanNetworkOptimization  => await PlanNetworkOptimizationAsync(msg.Payload),







                IpcMessageType.ApplyNetworkOptimization => await ApplyNetworkOptimizationAsync(msg.Payload),







                IpcMessageType.RestoreNetworkOptimization => await RestoreNetworkOptimizationAsync(),







                IpcMessageType.FlushDnsCache            => await FlushDnsCacheAsync(),







                IpcMessageType.RunNetworkDiagnostics    => await RunNetworkDiagnosticsAsync(),







                // Storage Optimizer — new authoritative storage engine
                IpcMessageType.ScanStorageCategories    => await ScanStorageCategoriesAsync(msg.Payload, ct),
                IpcMessageType.ApplyStorageCleanup      => await ApplyStorageCleanupAsync(msg.Payload, progress, ct),
                IpcMessageType.GetRecycleBinInfo        => GetRecycleBinInfoImpl(),
                IpcMessageType.EmptyRecycleBin          => EmptyRecycleBinImpl(),
                IpcMessageType.ScanLargeFiles           => await ScanLargeFilesAsync(msg.Payload, ct),
                IpcMessageType.ScanDuplicateFiles       => await ScanDuplicateFilesAsync(msg.Payload, ct),
                IpcMessageType.DeleteStorageFile        => DeleteStorageFileImpl(msg.Payload),

                // Power Plan Optimization
                IpcMessageType.GetPowerPlans            => await GetPowerPlansAsync(ct),
                IpcMessageType.ApplyPowerPlan           => await ApplyPowerPlanAsync(msg.Payload, ct),
                IpcMessageType.RestorePowerPlan         => await RestorePowerPlanAsync(ct),
                IpcMessageType.EnableUltimatePerformance => await EnableUltimatePerformanceAsync(ct),
                IpcMessageType.CreateCustomAiPlan       => await CreateCustomAiPlanAsync(msg.Payload, ct),
                IpcMessageType.DeleteCustomAiPlan       => await DeleteCustomAiPlanAsync(msg.Payload, ct),
                IpcMessageType.CreateRestorePoint       => await CreateRestorePointAsync(msg.Payload),
                IpcMessageType.GetRestorePoints         => await GetRestorePointsAsync(),
                IpcMessageType.CheckSystemRestoreStatus => await CheckSystemRestoreStatusAsync(),
                IpcMessageType.EnableSystemProtection   => await EnableSystemProtectionAsync(msg.Payload),
                IpcMessageType.GetSystemProtectionStatus => await GetSystemProtectionStatusAsync(msg.Payload),
                IpcMessageType.DisableSystemProtection  => await DisableSystemProtectionAsync(msg.Payload),
                IpcMessageType.ExecuteServicingTool     => await ExecuteServicingToolAsync(msg.Payload, ct),
                IpcMessageType.GetServicingEnvironment  => await GetServicingEnvironmentAsync(),
                IpcMessageType.EnsureServicingPrerequisites => await EnsureServicingPrerequisitesAsync(msg.Payload),

                // V4 Maximum Performance
                IpcMessageType.V4ScanProcessIntelligence => await V4ScanProcessIntelligenceAsync(),
                IpcMessageType.V4GetMaximumPerformancePlan => await V4GetMaximumPerformancePlanAsync(msg.Payload),
                IpcMessageType.V4ExecuteMaximumPerformancePass => await V4ExecuteMaximumPerformancePassAsync(msg.Payload, ct),
                IpcMessageType.V4ExecuteFullOptimization => await V4ExecuteFullOptimizationAsync(msg.Payload, ct),
                IpcMessageType.V4RollbackMaximumPerformance => await V4RollbackMaximumPerformanceAsync(msg.Payload),
                IpcMessageType.V4GetLiveMetrics          => V4GetLiveMetricsImpl(),
                IpcMessageType.V4GetOptimizationHistory  => V4GetOptimizationHistoryImpl(),
                IpcMessageType.V4GetProcessTargetBand    => V4GetProcessTargetBandImpl(msg.Payload),
                IpcMessageType.V4GetWorkloadGuardTelemetry => await V4GetWorkloadGuardTelemetryAsync(),
                IpcMessageType.V4SuppressWorkloadDrift   => await V4SuppressWorkloadDriftAsync(),

                // Phase 1 (NEW-BE-006): Capabilities, Blocklist, Missing Actions & Core/V4 IPC Parity
                IpcMessageType.GetCapabilities          => GetCapabilitiesImpl(),
                IpcMessageType.GetDebloatBlocklist      => GetDebloatBlocklistImpl(),
                IpcMessageType.SaveDebloatBlocklist     => SaveDebloatBlocklistImpl(msg.Payload),
                IpcMessageType.RequestUefiReboot        => RequestUefiRebootImpl(msg.Payload),
                IpcMessageType.ScheduleSystemRestart    => ScheduleSystemRestartImpl(msg.Payload),
                IpcMessageType.CancelScheduledRestart   => CancelScheduledRestartImpl(),
                IpcMessageType.EvaluateAboutCatalog     => EvaluateAboutCatalogImpl(),
                IpcMessageType.DiscoverOneClick         => await DiscoverOneClickAsync(ct),
                IpcMessageType.ExecuteOneClick          => await ExecuteOneClickAsync(msg.Payload, ct),
                IpcMessageType.EvaluateSystemHealth     => await EvaluateSystemHealthAsync(ct),
                IpcMessageType.GetRamRegistryValues     => GetRamRegistryValuesImpl(),
                IpcMessageType.ApplyRamRegistryValue    => MutateRamRegistryValueImpl(msg.Payload, apply: true),
                IpcMessageType.RollbackRamRegistryValue => MutateRamRegistryValueImpl(msg.Payload, apply: false),
                IpcMessageType.GetGpuRegistryValues     => GetGpuRegistryValuesImpl(msg.Payload),
                IpcMessageType.ApplyGpuRegistryValue    => MutateGpuRegistryValueImpl(msg.Payload, apply: true),
                IpcMessageType.RollbackGpuRegistryValue => MutateGpuRegistryValueImpl(msg.Payload, apply: false),
                IpcMessageType.WorkloadDiscover         => await WorkloadDiscoverAsync(ct),
                IpcMessageType.WorkloadApply            => await WorkloadApplyAsync(ct),
                IpcMessageType.WorkloadRevert           => await WorkloadRevertAsync(ct),

                // Phase 2 (NEW-BE-007): Unified settings, startup registration & audit logs
                IpcMessageType.GetUnifiedSettings       => GetUnifiedSettingsImpl(),
                IpcMessageType.SaveUnifiedSettings      => await SaveUnifiedSettingsAsync(msg.Payload, ct),
                IpcMessageType.ResetUnifiedSettings     => await ResetUnifiedSettingsAsync(ct),
                IpcMessageType.GetStartupRegistrationStatus => GetStartupRegistrationStatusImpl(),
                IpcMessageType.SetStartupRegistration   => SetStartupRegistrationImpl(msg.Payload),
                IpcMessageType.ExportAuditLogs          => ExportAuditLogsImpl(msg.Payload),
                IpcMessageType.PurgeExpiredLogs         => PurgeExpiredLogsImpl(msg.Payload),

                // Phase 3 (NEW-BE-002): Auto Optimize Persistent Scheduler
                IpcMessageType.GetAutoOptimizeState     => GetAutoOptimizeStateImpl(),
                IpcMessageType.SaveAutoOptimizeConfig   => SaveAutoOptimizeConfigImpl(msg.Payload),
                IpcMessageType.TriggerAutoOptimizeNow   => await TriggerAutoOptimizeNowAsync(msg.Payload, ct),
                IpcMessageType.CancelAutoOptimizeRun    => CancelAutoOptimizeRunImpl(),
                IpcMessageType.SetAutoOptimizePaused    => SetAutoOptimizePausedImpl(msg.Payload),
                IpcMessageType.ClearAutoOptimizeHistory => ClearAutoOptimizeHistoryImpl(),
                IpcMessageType.ScanAutoOptimizeTasks    => await ScanAutoOptimizeTasksAsync(ct),

                // Phase 4 (NEW-BE-003): Unified System Tools Orchestrator
                IpcMessageType.GetSystemToolsCatalog    => GetSystemToolsCatalogImpl(),
                IpcMessageType.ExecuteSystemTool        => ExecuteSystemTool(msg.Payload, ct),
                IpcMessageType.GetSystemToolJobStatus   => GetSystemToolJobStatusImpl(msg.Payload),
                IpcMessageType.CancelSystemToolJob      => CancelSystemToolJobImpl(msg.Payload),
                IpcMessageType.GetSystemToolReport      => GetSystemToolReportImpl(),

                // Phase 5 (NEW-BE-001): Hardware-Safe CPU/GPU Performance Tuning
                IpcMessageType.ProbePerformanceTuning   => await ProbePerformanceTuningAsync(ct),
                IpcMessageType.ExecutePerformanceTuning => await ExecutePerformanceTuningAsync(msg.Payload, ct),
                IpcMessageType.RollbackPerformanceTuning => await RollbackPerformanceTuningAsync(msg.Payload, ct),

                // Phase 7 (NEW-BE-005): Kernel DPC & NVMe/Vendor Thermal Telemetry
                IpcMessageType.GetKernelTelemetry       => GetKernelTelemetryImpl(),
                IpcMessageType.SetTelemetrySubscriptionActive => SetTelemetrySubscriptionActiveImpl(msg.Payload),

                // Application Backup Snapshot Lifecycle
                IpcMessageType.CreateApplicationBackup  => CreateApplicationBackupImpl(msg.Payload),
                IpcMessageType.ValidateBackupIntegrity  => ValidateBackupIntegrityImpl(msg.Payload),
                IpcMessageType.DeleteBackup             => DeleteBackupImpl(msg.Payload),

                // Interactive Session Agent Bridge (Decoupled from Session 0)
                IpcMessageType.ReportInteractiveSessionState => ReportInteractiveSessionStateImpl(msg.Payload),

                _                                     => Fail("Unknown message type: " + msg.Type)







            };







        }







        // --- Implementations ---







        private IpcResponse GetAssemblyVersion()







        {







            var attr = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);







            var buildId = attr != null && attr.Length > 0 







                ? ((System.Reflection.AssemblyInformationalVersionAttribute)attr[0]).InformationalVersion 







                : (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown");







            var process = System.Diagnostics.Process.GetCurrentProcess();







            var handshake = new IpcHandshakeResponse







            {







                ServiceName = "BiosOptimizerService",







                ServiceVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0",







                BuildId = buildId,







                ProcessId = process.Id,







                ExecutablePath = process.MainModule?.FileName ?? "Unknown",







                ProtocolVersion = "v2",







                SessionId = _sessionId,







                Status = "Ready",







                Timestamp = DateTime.UtcNow.ToString("o"),







                Capabilities = new System.Collections.Generic.List<string> { 







                    "Optimization", "Storage", "Network", "Diagnostics", "Debloat", "BIOS", "StartupManager", "ServiceManager" 







                }







            };







            return Ok(JsonSerializer.Serialize(handshake));







        }







        private IpcResponse GetLiveMetrics()







        {







            try







            {







                // This is instant, 0ms latency







                var metrics = BiosOptimizer.Service.Metrics.LiveSystemMetricsService.CurrentMetrics;







                return Ok(JsonSerializer.Serialize(metrics));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse GetScore()







        {







            try







            {







                var envDetector = _services.GetRequiredService<IEnvironmentDetector>();







                var scoreEngine = _services.GetRequiredService<IOptimizationScoreEngine>();







                var profileRepo = _services.GetRequiredService<IProfileRepository>();







                var actionRegistry = _services.GetRequiredService<IActionRegistry>();







                var env = envDetector.Detect();







                var score = scoreEngine.CalculateScore(env, profileRepo, actionRegistry);







                var label = score.OverallScore switch







                {







                    >= 90 => "Excellent",







                    >= 70 => "Good",







                    >= 50 => "Fair",







                    >= 30 => "Needs Optimization",







                    _     => "Critical"







                };







                var result = new







                {







                    Score          = (double)score.OverallScore,







                    Label          = label,







                    TotalActions   = score.TotalAvailable,







                    AppliedActions = score.Applied,







                    AvailableActions = score.Remaining







                };







                return Ok(JsonSerializer.Serialize(result));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse GetSystemInfo()







        {







            try







            {







                var envDetector = _services.GetRequiredService<IEnvironmentDetector>();







                var diagEngine  = _services.GetRequiredService<DiagnosticsEngine>();







                var env = envDetector.Detect();







                var diag = diagEngine.GatherDiagnostics(env);







                var gpus = string.Join("\nGPU: ", diag.AllGpuNames);







                var info = $"CPU: {diag.CpuName}\n" +







                           $"Physical Cores: {diag.PhysicalCores} / Logical: {diag.LogicalProcessors}\n" +







                           $"RAM: {diag.RamTotalBytes / (1024 * 1024 * 1024L)} GB\n" +







                           $"GPU: {gpus}\n" +







                           $"Motherboard: {diag.Motherboard}\n" +







                           $"BIOS Version: {diag.BiosVersion}\n" +







                           $"Windows: {diag.WindowsVersion}\n" +







                           $"HAGS: {diag.HagsStatus}\n" +







                           $"Secure Boot: {diag.SecureBootStatus}\n" +







                           $"TPM: {diag.TpmStatus}\n" +







                           $"BitLocker: {diag.BitLockerStatus}\n" +







                           $"Manufacturer: {env.Manufacturer}  Model: {env.Model}\n" +







                           $"RAM (GB): {env.RamSizeGb}  BitLocker Active: {env.BitLockerActive}";







                return Ok(info);







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse GetMachineProfile()







        {







            try







            {







                var envDetector = _services.GetRequiredService<IEnvironmentDetector>();







                var env = envDetector.Detect();







                







                // Ensure supported targets count is populated by calling preview on all tiers







                return Ok(JsonSerializer.Serialize(env.MachineProfile));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse GetStartupItems()







        {







            try







            {







                var mgr = _services.GetRequiredService<IStartupManager>();







                var items = mgr.GetStartupEntries();







                return Ok(JsonSerializer.Serialize(items));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private class StartupPayload
        {
            public string Id { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string ValueName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string? TargetSid { get; set; }
        }

        private IpcResponse EnableStartupItem(string? payload)
        {
            try
            {
                if (string.IsNullOrEmpty(payload)) return Fail("Empty payload");
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var dto = JsonSerializer.Deserialize<StartupPayload>(payload, opts);
                if (dto == null) return Fail("Invalid payload");
                var mgr = _services.GetRequiredService<IStartupManager>();
                string valName = !string.IsNullOrEmpty(dto.ValueName) ? dto.ValueName : dto.Name;
                string effectivePath = !string.IsNullOrEmpty(dto.TargetSid) && !dto.Path.Contains(dto.TargetSid) ? $@"{dto.TargetSid}\{dto.Path}" : dto.Path;
                string effectiveId = !string.IsNullOrEmpty(dto.TargetSid) && !dto.Id.Contains(dto.TargetSid) ? $"{dto.Id}|{dto.TargetSid}" : dto.Id;
                var success = mgr.EnableEntry(dto.Source, effectivePath, valName, effectiveId);
                return success
                    ? Ok(JsonSerializer.Serialize(new { ok = true, success = true, id = dto.Id, state = "Enabled", isEnabled = true, name = valName, message = $"Successfully enabled {valName}." }))
                    : Fail($"Failed to enable startup item '{valName}'.");
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private IpcResponse DisableStartupItem(string? payload)
        {
            try
            {
                if (string.IsNullOrEmpty(payload)) return Fail("Empty payload");
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var dto = JsonSerializer.Deserialize<StartupPayload>(payload, opts);
                if (dto == null) return Fail("Invalid payload");
                var mgr = _services.GetRequiredService<IStartupManager>();
                string valName = !string.IsNullOrEmpty(dto.ValueName) ? dto.ValueName : dto.Name;
                string effectivePath = !string.IsNullOrEmpty(dto.TargetSid) && !dto.Path.Contains(dto.TargetSid) ? $@"{dto.TargetSid}\{dto.Path}" : dto.Path;
                string effectiveId = !string.IsNullOrEmpty(dto.TargetSid) && !dto.Id.Contains(dto.TargetSid) ? $"{dto.Id}|{dto.TargetSid}" : dto.Id;
                var success = mgr.DisableEntry(dto.Source, effectivePath, valName, effectiveId);
                return success
                    ? Ok(JsonSerializer.Serialize(new { ok = true, success = true, id = dto.Id, state = "Disabled", isEnabled = false, name = valName, message = $"Successfully disabled {valName}." }))
                    : Fail($"Failed to disable startup item '{valName}'.");
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private IpcResponse GetServices()







        {







            try







            {







                var mgr = _services.GetRequiredService<IServiceManager>();







                var allServices = mgr.GetAllServices();







                var dtos = allServices.Select(s => new ServiceInfoDto







                {







                    ServiceName = s.ServiceName,







                    DisplayName = s.DisplayName,







                    Description = s.Description,







                    ExePath = s.ExePath,







                    StartupType = s.StartupType.ToString(),







                    RunningState = s.RunningState.ToString(),







                    Risk = s.Risk,







                    ActionSafety = s.ActionSafety,







                    ActionSafetyReason = s.ActionSafetyReason,







                    Publisher = s.Publisher,







                    Version = s.Version,







                    Category = s.Category,







                    ServiceType = s.ServiceType,







                    Account = s.Account,







                    IsDelayedStart = s.IsDelayedStart,







                    IsCritical = s.IsCritical,







                    FileExists = s.FileExists,







                    IsMicrosoft = s.IsMicrosoft,







                    DependsOn = s.DependsOn,







                    DependentServices = s.DependentServices







                }).ToList();







                return Ok(JsonSerializer.Serialize(dtos));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private class ServiceControlPayload







        {







            public string ServiceName { get; set; } = string.Empty;







            public string State { get; set; } = string.Empty;







        }







        private IpcResponse ChangeServiceState(string? payload)







        {







            try







            {







                if (string.IsNullOrEmpty(payload)) return Fail("Empty payload");







                var dto = JsonSerializer.Deserialize<ServiceControlPayload>(payload);







                if (dto == null) return Fail("Invalid payload");







                var mgr = _services.GetRequiredService<IServiceManager>();







                bool success = false;

                if (dto.State.Equals("Start", StringComparison.OrdinalIgnoreCase))

                    success = mgr.Start(dto.ServiceName);

                else if (dto.State.Equals("Stop", StringComparison.OrdinalIgnoreCase))

                    success = mgr.Stop(dto.ServiceName);

                else if (dto.State.Equals("Restart", StringComparison.OrdinalIgnoreCase))

                    success = mgr.Restart(dto.ServiceName);







                return success ? Ok(dto.State) : Fail($"Failed to {dto.State.ToLower()} service");







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







                private static readonly HashSet<string> ProtectedServiceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "WdNisSvc", "Sense", "wscsvc", "mpssvc", "BFE", "BDESVC",
            "RpcSs", "DcomLaunch", "EventLog", "PlugPlay", "CryptSvc", "SamSs", "LSM",
            "Schedule", "Winmgmt", "Themes", "AudioSrv", "AudioEndpointBuilder", "Dhcp",
            "Dnscache", "LanmanWorkstation", "LanmanServer", "WlanSvc", "Disk", "volume",
            "partmgr", "mountmgr", "fltmgr", "BrokerInfrastructure", "StorSvc"
        };

        private static bool IsProtectedService(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return true;
            return ProtectedServiceNames.Contains(serviceName.Trim());
        }

private IpcResponse SetServiceStartupType(string? payload)







        {







            try







            {







                if (string.IsNullOrEmpty(payload)) return Fail("Empty payload");







                var dto = JsonSerializer.Deserialize<ServiceControlPayload>(payload);







                if (dto == null) return Fail("Invalid payload");







                var mgr = _services.GetRequiredService<IServiceManager>();







                







                ServiceStartupType st = ServiceStartupType.Unknown;







                if (dto.State.Equals("AutomaticDelayedStart", StringComparison.OrdinalIgnoreCase) ||

                    dto.State.Equals("Automatic (Delayed)", StringComparison.OrdinalIgnoreCase))

                {

                    st = ServiceStartupType.AutomaticDelayedStart;

                }

                else if (dto.State.Equals("Automatic", StringComparison.OrdinalIgnoreCase)) 

                {

                    st = ServiceStartupType.Automatic;

                }

                else if (dto.State.Equals("Manual", StringComparison.OrdinalIgnoreCase)) 

                {

                    st = ServiceStartupType.Manual;

                }

                else if (dto.State.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) 

                {

                    st = ServiceStartupType.Disabled;

                }







                if (st == ServiceStartupType.Unknown) return Fail("Invalid startup type");

                // Security Policy: Protect critical Windows OS, security, and hardware services from being disabled
                if (st == ServiceStartupType.Disabled && IsProtectedService(dto.ServiceName))
                {
                    _logger.LogWarning("Blocked attempt to disable protected service: {Service}", dto.ServiceName);
                    return Fail($"Blocked by security policy: Service '{dto.ServiceName}' is a critical Windows system or security service and cannot be disabled.");
                }

                bool success = mgr.SetStartupType(dto.ServiceName, st);







                return success ? Ok("Changed") : Fail("Failed to change startup type");







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private string GetServiceRisk(string serviceName)







        {







            var lower = serviceName.ToLowerInvariant();







            if (lower.Contains("windows") || lower.Contains("microsoft") || lower.StartsWith("wuauserv") || lower.StartsWith("vss")) 







                return "High";







            if (lower.Contains("intel") || lower.Contains("amd") || lower.Contains("nvidia")) 







                return "Medium";







            return "Low";







        }







        private IpcResponse GetNetworkStatus()







        {







            try







            {







                var adapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();







                var sb = new System.Text.StringBuilder();







                int idx = 1;







                foreach (var adapter in adapters)







                {







                    // Only show operational, non-loopback adapters







                    if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)







                        continue;







                    if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)







                        continue;







                    var props = adapter.GetIPProperties();







                    var ipv4 = props.UnicastAddresses







                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);







                    var dns = props.DnsAddresses.Count > 0 ? props.DnsAddresses[0].ToString() : "N/A";







                    var speedMbps = adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000} Mbps" : "Unknown";







                    var adapterType = adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211







                        ? "Wi-Fi" : "Ethernet";







                    sb.AppendLine($"Adapter {idx}: {adapter.Name}");







                    sb.AppendLine($"Type: {adapterType}");







                    sb.AppendLine($"Description: {adapter.Description}");







                    sb.AppendLine($"Speed: {speedMbps}");







                    sb.AppendLine($"IP: {ipv4?.Address?.ToString() ?? "N/A"}");







                    sb.AppendLine($"DNS: {dns}");







                    sb.AppendLine($"MAC: {adapter.GetPhysicalAddress()}");







                    sb.AppendLine();







                    idx++;







                }







                if (sb.Length == 0)







                    return Ok("No active network adapters detected.");







                return Ok(sb.ToString().TrimEnd());







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse GetStorage()







        {







            try







            {







                var eng = _services.GetRequiredService<StorageEngine>();







                var drives = eng.GetDrives();







                return Ok(JsonSerializer.Serialize(drives));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> PreviewTierAsync(string tierId, CancellationToken ct)



        {



            try



            {



                string targetTier = tierId ?? "Normal";



                if (!string.IsNullOrWhiteSpace(targetTier) && targetTier.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(targetTier);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("tierId", out var tProp) ||
                                doc.RootElement.TryGetProperty("TierId", out tProp) ||
                                doc.RootElement.TryGetProperty("tier", out tProp) ||
                                doc.RootElement.TryGetProperty("Tier", out tProp) ||
                                doc.RootElement.TryGetProperty("profile", out tProp) ||
                                doc.RootElement.TryGetProperty("profileId", out tProp))
                            {
                                var s = tProp.GetString();
                                targetTier = !string.IsNullOrWhiteSpace(s) ? s : "Normal";
                            }
                            else
                            {
                                targetTier = "Normal";
                            }
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.String)
                        {
                            var s = doc.RootElement.GetString();
                            targetTier = !string.IsNullOrWhiteSpace(s) ? s : "Normal";
                        }
                        else
                        {
                            targetTier = "Normal";
                        }
                    }
                    catch
                    {
                        targetTier = "Normal";
                    }
                }
                else if (string.IsNullOrWhiteSpace(targetTier) || targetTier.Contains('{') || targetTier.Contains('"'))
                {
                    targetTier = "Normal";
                }
                targetTier = ProfileRepository.NormalizeProfileId(targetTier);
                var uEngine = _services.GetService<IUniversalEligibilityEngine>();
                if (uEngine != null)
                {
                    var uPlan = uEngine.EvaluateTierProfile(targetTier);
                    return Ok(JsonSerializer.Serialize(uPlan, CamelCaseJsonOptions));
                }
                var engine = _services.GetRequiredService<ITierEngine>();
                var preview = await engine.PreviewAsync(targetTier, ct);



                



                var dto = new TierPreviewDto



                {



                    TierId = targetTier,



                    TierName = targetTier,



                    ActionCount = preview.PlannedChanges?.Count ?? 0,



                    CanApply = true,



                    CanRestore = false,



                    Actions = new List<OptimizationActionDto>()



                };







                                                var allChanges = new List<BiosOptimizer.Core.Models.PlannedChange>();
                if (preview.PlannedChanges != null) allChanges.AddRange(preview.PlannedChanges);
                if (preview.InheritedChanges != null) allChanges.AddRange(preview.InheritedChanges);

                dto.ActionCount = allChanges.Count;

                foreach (var p in allChanges)
                {
                    bool isAlreadyOptimized = StateNormalizer.IsSatisfied(p.Current, p.Target);
                    string status = isAlreadyOptimized ? "AlreadyOptimized" : "Recommended";
                    bool isSelected = !isAlreadyOptimized;

                    dto.Actions.Add(new OptimizationActionDto
                    {
                        ItemId = p.Id ?? "",
                        ActionName = p.ActionName ?? "",
                        DisplayName = p.Item ?? "",
                        Description = p.Description ?? "",
                        Category = p.Category ?? "General",
                        IsInherited = p.IsInherited,
                        CurrentState = p.Current ?? "",
                        TargetState = p.Target ?? "",
                        Risk = p.Risk ?? "",
                        Warning = p.Warning ?? "",
                        ExecutionType = p.ExecutionType ?? "WINDOWS_AUTOMATIC",
                        Status = status,
                        IsSelected = isSelected
                    });
                }

                foreach (var s in preview.SkippedChanges ?? new List<BiosOptimizer.Core.Models.SkippedChange>())
                {
                    string status = string.Equals(s.Category, "Unsupported", StringComparison.OrdinalIgnoreCase) ? "Unsupported" : "NotApplicable";
                    dto.Actions.Add(new OptimizationActionDto
                    {
                        ItemId = s.Id ?? "",
                        DisplayName = s.Item ?? "",
                        Category = s.Category ?? "General",
                        Reason = s.Reason ?? "",
                        Status = status,
                        ExecutionType = s.ExecutionType ?? "WINDOWS_AUTOMATIC",
                        Applicable = false,
                        Supported = (status != "Unsupported"),
                        IsSelected = false
                    });
                }







                return Ok(JsonSerializer.Serialize(dto));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> ApplyTierAsync(string? payload, Action<IpcResponse> progressCallback, CancellationToken ct)







        {







            try







            {







                if (string.IsNullOrEmpty(payload)) return Fail("Empty payload");







                







                string tierId = "Normal";







                System.Collections.Generic.HashSet<string>? allowedItemIds = null;







                







                if (!string.IsNullOrWhiteSpace(payload))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("tierId", out var tProp) ||
                                doc.RootElement.TryGetProperty("TierId", out tProp) ||
                                doc.RootElement.TryGetProperty("tier", out tProp) ||
                                doc.RootElement.TryGetProperty("Tier", out tProp) ||
                                doc.RootElement.TryGetProperty("profile", out tProp) ||
                                doc.RootElement.TryGetProperty("profileId", out tProp))
                            {
                                var s = tProp.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) tierId = s;
                            }
                            if (doc.RootElement.TryGetProperty("selectedItemIds", out var sProp) ||
                                doc.RootElement.TryGetProperty("SelectedItemIds", out sProp) ||
                                doc.RootElement.TryGetProperty("allowedItemIds", out sProp) ||
                                doc.RootElement.TryGetProperty("AllowedItemIds", out sProp) ||
                                doc.RootElement.TryGetProperty("selectedIds", out sProp))
                            {
                                if (sProp.ValueKind == JsonValueKind.Array)
                                {
                                    allowedItemIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var item in sProp.EnumerateArray())
                                    {
                                        var s = item.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) allowedItemIds.Add(s);
                                    }
                                }
                            }
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.String)
                        {
                            var s = doc.RootElement.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) tierId = s;
                        }
                        else
                        {
                            tierId = "Normal";
                        }
                    }
                    catch
                    {
                        if (!payload.Contains('{') && !payload.Contains('[') && !payload.Contains('"') && !payload.Contains(':'))
                            tierId = payload.Trim();
                        else
                            tierId = "Normal";
                    }
                }
                else
                {
                    tierId = "Normal";
                }
                tierId = ProfileRepository.NormalizeProfileId(tierId);
                var uEngine = _services.GetService<IUniversalEligibilityEngine>();
                if (uEngine != null)
                {
                    var uRes = await uEngine.ExecutePlanAsync(tierId, allowedItemIds, dryRun: false, ct).ConfigureAwait(false);
                    return Ok(JsonSerializer.Serialize(uRes, CamelCaseJsonOptions));
                }
                var engine = _services.GetRequiredService<ITierEngine>();
                var preview = await engine.PreviewAsync(tierId, ct);







                







                int total = preview.PlannedChanges?.Count ?? 0;







                int current = 0;







                var confirmation = new ConfirmationContext { IsConfirmed = true };







                var progress = new SynchronousProgress<OptimizationResult>(r =>
                {
                    if (r.Status != BiosOptimizer.Core.Models.ResultStatus.Running && r.Status != BiosOptimizer.Core.Models.ResultStatus.Verifying)
                    {
                        current++;
                    }

                    var dto = new OptimizationActionDto
                    {
                        ItemId = r.ItemId,
                        ActionName = r.ActionName,
                        Category = r.Category,
                        DisplayName = r.DisplayName,
                        Reason = r.Message,
                        Status = r.Status.ToString()
                    };

                    progressCallback(Ok(JsonSerializer.Serialize(new { ProgressUpdate = true, Action = dto, Current = current, Total = total })));
                });







                var results = await engine.ApplyAsync(tierId, confirmation, false, ct, progress, allowedItemIds);







                int success = 0, failed = 0, blocked = 0, notAvailable = 0, alreadyOptimized = 0, notApplicable = 0;







                var failedReasons = new System.Collections.Generic.List<string>();







                var blockedReasons = new System.Collections.Generic.List<string>();







                







                foreach (var r in results)







                {







                    if (r.Status == ResultStatus.Success || r.Status == ResultStatus.Verified) success++;







                    else if (r.Status == ResultStatus.Failed) 







                    {







                        failed++;







                        failedReasons.Add(r.Message);







                    }







                    else if (r.Status == ResultStatus.Blocked)







                    {







                        blocked++;







                        blockedReasons.Add(r.Message);







                    }







                    else if (r.Status == ResultStatus.AlreadyOptimized) alreadyOptimized++;







                    else if (r.Status == ResultStatus.NotApplicable) notApplicable++;







                    else if (r.Status == ResultStatus.NotAvailable) notAvailable++;







                }







                







                var summary = new







                {







                    Success = success,







                    Failed = failed,







                    Blocked = blocked,







                    NotAvailable = notAvailable,







                    AlreadyOptimized = alreadyOptimized,







                    NotApplicable = notApplicable,







                    FailedReasons = failedReasons,







                    BlockedReasons = blockedReasons







                };







                try
                {
                    string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer", "logs");
                    Directory.CreateDirectory(debugDir);
                    System.IO.File.WriteAllText(Path.Combine(debugDir, "ApplyTierDebug.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }







                return Ok(JsonSerializer.Serialize(new { FinalResult = true, Summary = summary, Results = results, Operations = results }));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private Task<IpcResponse> RestoreTierAsync(string tierId, Action<IpcResponse> progressCallback, CancellationToken ct)







        {







            return RestoreTierRealAsync(tierId, ct);







        }







        private Task<IpcResponse> RestoreLastAsync(Action<IpcResponse> progressCallback, CancellationToken ct)







        {







            // Restore the "Normal" tier as the safest fallback if no specific snapshot ID exists







            return RestoreTierAsync("Normal", progressCallback, ct);







        }







        private IpcResponse ScanCleaner()







        {







            try







            {







                var ram  = _services.GetRequiredService<RamCleanupEngine>();







                var temp = _services.GetRequiredService<TempCleanerEngine>();







                var ramScan  = ram.Scan();







                var tempScan = temp.Scan();







                var ramMb  = ramScan.TotalSizeInBytes / (1024 * 1024);







                var tempMb = tempScan.TotalSizeInBytes / (1024 * 1024);







                return Ok($"Reclaimable RAM: ~{ramMb} MB\nTemp Files: ~{tempMb} MB ({tempScan.FileCount} files)");







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse ApplyCleaner()







        {







            try







            {







                var ram  = _services.GetRequiredService<RamCleanupEngine>();







                var temp = _services.GetRequiredService<TempCleanerEngine>();







                var ramFreed  = ram.Clean();







                var tempFreed = temp.Clean();







                var totalMb = (ramFreed + tempFreed) / (1024 * 1024);







                return Ok($"Cleanup complete. Freed approximately {totalMb} MB.");







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse GetDebloatItems()







        {







            try







            {







                var packageManager = _services.GetRequiredService<BiosOptimizer.Core.Interfaces.IPackageManager>();







                var packages = packageManager.GetInstalledPackages();







                







                var actions = new List<OptimizationActionDto>();







                foreach (var pkg in packages)







                {







                    string status = "SAFE TO REMOVE";







                    if (pkg.IsSystemComponent) status = "PROTECTED";







                    else if (pkg.IsFramework) status = "FRAMEWORK";







                    else if (pkg.IsResource) status = "RESOURCE";
                    else if (BiosOptimizer.Core.Implementations.Debloat.DebloatBlocklistStore.IsBlocked(pkg.FullName) ||
                             BiosOptimizer.Core.Implementations.Debloat.DebloatBlocklistStore.IsBlocked(pkg.Name))
                    {
                        status = "BLOCKLISTED";
                    }







                    else







                    {







                        var lowerName = pkg.Name.ToLowerInvariant();







                        if (lowerName.Contains("cortana") || lowerName.Contains("edge") || lowerName.Contains("store") || lowerName.Contains("windows.client"))







                            status = "HIGH RISK";







                        else if (pkg.PublisherDisplayName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))







                            status = "MEDIUM";







                        else if (lowerName.Contains("xbox") || lowerName.Contains("zune") || lowerName.Contains("bing") || lowerName.Contains("solitaire"))







                            status = "SAFE TO REMOVE";







                        else







                            status = "OPTIONAL";







                    }







                    actions.Add(new OptimizationActionDto







                    {







                        ItemId = pkg.FullName,







                        DisplayName = pkg.DisplayName,







                        Category = pkg.PublisherDisplayName,







                        Status = status







                    });







                }







                var dto = new TierPreviewDto { Actions = actions };







                return Ok(JsonSerializer.Serialize(dto));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> ApplyDebloatItemsAsync(string payload, Action<IpcResponse> progressCallback, CancellationToken ct)







        {







            try







            {







                var request = JsonSerializer.Deserialize<DebloatRequestDto>(payload);







                if (request == null || request.SelectedIds.Count == 0) return Ok("No items selected.");







                







                var profile = new ProfileDef { Id = "Debloat", DisplayName = "Debloat" };







                foreach (var id in request.SelectedIds)
                {
                    if (BiosOptimizer.Core.V4.Safety.ProtectedApplicationPolicy.IsProtectedPackage(id) ||
                        BiosOptimizer.Core.Implementations.Debloat.DebloatBlocklistStore.IsBlocked(id))
                    {
                        continue;
                    }







                    profile.Entries.Add(new OptimizationEntry 







                    { 







                        Id = "debloat.appx." + id.GetHashCode(), 







                        Action = "RemoveAppxPackage", 







                        Target = id, 







                        Category = "AppX",







                        DisplayName = "Remove AppX Package",







                        Risk = "Low"







                    });







                }







                







                var engine = _services.GetRequiredService<ITierEngine>();







                var confirmation = new ConfirmationContext { IsConfirmed = true };







                







                int total = profile.Entries.Count;







                int current = 0;







                var progress = new Progress<OptimizationResult>(r =>







                {







                    if (r.Status != BiosOptimizer.Core.Models.ResultStatus.Running)







                    {







                        current++;







                    }







                    var dto = new OptimizationActionDto







                    {







                        ItemId = r.ItemId,







                        DisplayName = r.DisplayName,







                        Reason = r.Message,







                        Status = r.Status.ToString()







                    };







                    progressCallback(Ok(JsonSerializer.Serialize(new { ProgressUpdate = true, Action = dto, Current = current, Total = total })));







                });







                







                var results = await engine.ApplyAsync(profile, confirmation, false, ct, progress);







                







                int removed = results.Count(r => r.Status == BiosOptimizer.Core.Models.ResultStatus.Success);







                int failed = results.Count(r => r.Status == BiosOptimizer.Core.Models.ResultStatus.Failed);







                







                var summary = new







                {







                    FinalResult = true,







                    Success = removed,







                    Failed = failed,







                    Message = $"Debloat complete. Applied: {removed}, Failed/Skipped: {failed}"







                };







                







                try { progressCallback(Ok(JsonSerializer.Serialize(summary))); } catch { }
                return Ok(JsonSerializer.Serialize(summary));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> GetBiosCapabilitiesAsync(CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                return await Task.Run(() =>
                {
                    try
                    {
                        var provider = _services.GetRequiredService<IProviderManager>();
                        var active = provider.GetActiveProvider();
                        if (active == null) return Ok("No BIOS provider detected for this hardware.");
                        var info = active.GetProviderInfo();
                        return Ok($"Provider: {info.Name}  Status: {info.Status}  Reason: {info.Reason}");
                    }
                    catch (Exception ex)
                    {
                        return Ok($"Generic UEFI / ACPI provider detected ({ex.Message}).");
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                return Ok("Generic UEFI / ACPI BIOS Provider active (read-only telemetry).");
            }
        }







        private async Task<IpcResponse> GetProcessCandidatesAsync()







        {







            Console.WriteLine("[PROCESS REDUCTION] Handler received GetProcessCandidates");







            try







            {







                var engine = _services.GetRequiredService<IProcessReductionEngine>();







                var list = await engine.ScanProcessesAsync();







                var options = new JsonSerializerOptions();







                options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());







                return Ok(JsonSerializer.Serialize(list, options));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse TerminateProcesses(string? payload)







        {







            if (string.IsNullOrWhiteSpace(payload)) return Fail("No processes specified.");







            try







            {







                var pids = JsonSerializer.Deserialize<List<int>>(payload) ?? new List<int>();







                var engine = _services.GetRequiredService<IProcessReductionEngine>();







                int killed = engine.TerminateProcesses(pids);







                return Ok(JsonSerializer.Serialize(new { Killed = killed }));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private IpcResponse RestoreAllSystem()







        {







            try







            {







                var backup = _services.GetRequiredService<IBackupManager>();







                bool restored = backup.RestoreAll();







                return Ok(JsonSerializer.Serialize(new { Success = restored }));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> PlanInputOptimizationAsync()







        {







            try







            {







                var engine = _services.GetRequiredService<IInputOptimizerEngine>();







                var plan = await engine.PlanInputOptimizationAsync();







                return Ok(JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> ApplyInputOptimizationAsync(string? riskLevel)







        {







            try







            {







                var engine = _services.GetRequiredService<IInputOptimizerEngine>();
                var planBefore = await engine.PlanInputOptimizationAsync();
                var res = await engine.ApplyInputOptimizationAsync(riskLevel ?? "CORE");
                var planAfter = await engine.PlanInputOptimizationAsync();

                var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(riskLevel) && riskLevel.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(riskLevel);
                        if (doc.RootElement.TryGetProperty("selectedIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in arr.EnumerateArray())
                            {
                                if (item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                                    selectedIds.Add(s);
                            }
                        }
                    }
                    catch { }
                }

                var expandedIds = BiosOptimizer.Core.Implementations.InputOptimizerEngine.ExpandSelectedIds(selectedIds);
                var targetActions = (selectedIds.Count > 0)
                    ? planAfter.Actions.Where(a => expandedIds.Contains(a.Id)).ToList()
                    : planAfter.Actions;

                var ops = targetActions.Select(a =>
                {
                    var b = planBefore.Actions.FirstOrDefault(x => x.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase));
                    bool wasAlready = b != null && (b.Status == "OPTIMIZED" || b.Status == "ALREADY_OPTIMIZED");
                    bool isDone = a.Status == "OPTIMIZED" || a.Status == "VERIFIED" || a.Status == "RESTART_REQUIRED";
                    string requestedId = selectedIds.FirstOrDefault(sid => sid.Equals(a.Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(BiosOptimizer.Core.Implementations.InputOptimizerEngine.ResolveFrontendAlias(a.Id), sid, StringComparison.OrdinalIgnoreCase)) ?? a.Id;

                    return new
                    {
                        id = requestedId,
                        canonicalId = a.Id,
                        title = a.Name,
                        status = wasAlready ? "succeeded" : (isDone ? "succeeded" : (a.Status == "NOT_SUPPORTED" || a.Status == "NOT_AVAILABLE" ? "skipped" : "failed")),
                        isAlreadyOptimized = wasAlready,
                        alreadyOptimized = wasAlready,
                        canonicalStatus = wasAlready ? "ALREADY_OPTIMIZED" : (isDone ? "VERIFIED" : (a.Status == "NOT_SUPPORTED" || a.Status == "NOT_AVAILABLE" ? "INELIGIBLE" : "FAILED")),
                        currentValue = a.CurrentValue,
                        targetValue = a.TargetValue,
                        applied = !wasAlready && isDone,
                        detail = wasAlready ? "ALREADY_OPTIMIZED" : (isDone ? "VERIFIED" : (a.Reason ?? "Operation failed")),
                        verification = wasAlready
                            ? $"Already configured to optimal target value ({a.CurrentValue})"
                            : $"Confirmed via Windows SPI/Registry readback: {a.CurrentValue}",
                        reason = wasAlready
                            ? $"Value already optimal ({a.CurrentValue}); no write required"
                            : (isDone ? "Optimization applied and verified" : (a.Reason ?? "Operation failed"))
                    };
                }).ToList();

                var payloadOut = JsonSerializer.Serialize(new
                {
                    success = res.Success,
                    message = res.Message,
                    operations = ops
                });

                return Ok(payloadOut);







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> RestoreInputOptimizationAsync()







        {







            try







            {







                var engine = _services.GetRequiredService<IInputOptimizerEngine>();







                var res = await engine.RestoreInputOptimizationAsync();







                return res.Success ? Ok(res.Message) : Fail(res.Message);







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> PlanRegistryTweakAsync()
        {
            try
            {
                var uEngine = _services.GetService<IUniversalEligibilityEngine>();
                if (uEngine != null)
                {
                    var uPlan = uEngine.EvaluateRegistryIntelligence();
                    return Ok(JsonSerializer.Serialize(uPlan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                }
                var engine = _services.GetRequiredService<IRegistryTweakEngine>();







                var plan = await engine.PlanRegistryTweakAsync();







                return Ok(JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> ApplyRegistryTweakAsync(string? payload)







        {







            try







            {







                var tweakIds = new List<string>();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in doc.RootElement.EnumerateArray())
                            {
                                var s = elem.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) tweakIds.Add(s);
                            }
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("ids", out var idProp) ||
                                doc.RootElement.TryGetProperty("Ids", out idProp) ||
                                doc.RootElement.TryGetProperty("selectedIds", out idProp))
                            {
                                if (idProp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var elem in idProp.EnumerateArray())
                                    {
                                        var s = elem.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) tweakIds.Add(s);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        if (!payload.Contains('{') && !payload.Contains('[') && !payload.Contains('"'))
                            tweakIds.Add(payload.Trim());
                    }
                }







                var uEngine = _services.GetService<IUniversalEligibilityEngine>();
                if (uEngine != null)
                {
                    var uRes = await uEngine.ExecutePlanAsync("RegistryIntelligence", new HashSet<string>(tweakIds, StringComparer.OrdinalIgnoreCase));
                    return Ok(JsonSerializer.Serialize(uRes, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                }

                var engine = _services.GetRequiredService<IRegistryTweakEngine>();
                var res = await engine.ExecuteRegistryTweaksAsync(tweakIds);
                return Ok(JsonSerializer.Serialize(res, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> RestoreRegistryTweakAsync(string? payload)







        {







            try







            {







                var tweakIds = new List<string>();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in doc.RootElement.EnumerateArray())
                            {
                                var s = elem.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) tweakIds.Add(s);
                            }
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("ids", out var idProp) ||
                                doc.RootElement.TryGetProperty("Ids", out idProp) ||
                                doc.RootElement.TryGetProperty("selectedIds", out idProp))
                            {
                                if (idProp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var elem in idProp.EnumerateArray())
                                    {
                                        var s = elem.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) tweakIds.Add(s);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        if (!payload.Contains('{') && !payload.Contains('[') && !payload.Contains('"'))
                            tweakIds.Add(payload.Trim());
                    }
                }







                var engine = _services.GetRequiredService<IRegistryTweakEngine>();







                var res = await engine.RestoreRegistryTweaksAsync(tweakIds);







                return res.Success ? Ok(JsonSerializer.Serialize(new { Message = res.Message, Status = res.Status, RestoredCount = res.RestoredCount, FailedCount = res.FailedCount })) : Fail(res.Message);







            }







            catch (Exception ex) { return Fail(ex.Message); }







        }







        private async Task<IpcResponse> PlanNetworkOptimizationAsync(string? payload)
        {
            try
            {
                var engine = _services.GetRequiredService<INetworkOptimizerEngine>();
                bool includeDiag = bool.TryParse(payload, out bool b) && b;
                var plan = await engine.PlanNetworkOptimizationAsync(includeDiag);
                return Ok(JsonSerializer.Serialize(plan));
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private async Task<IpcResponse> ApplyNetworkOptimizationAsync(string? payload)
        {
            try
            {
                if (string.IsNullOrEmpty(payload)) return Fail("Missing actionId payload");
                var engine = _services.GetRequiredService<INetworkOptimizerEngine>();
                var (success, message, verifiedState) = await engine.ApplyNetworkActionAsync(payload);
                return success 
                    ? Ok(JsonSerializer.Serialize(new { Success = true, Message = message, VerifiedState = verifiedState }))
                    : Fail(message);
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private async Task<IpcResponse> RestoreNetworkOptimizationAsync()
        {
            try
            {
                var engine = _services.GetRequiredService<INetworkOptimizerEngine>();
                var (success, message) = await engine.RestoreNetworkOptimizationAsync();
                return success ? Ok(message) : Fail(message);
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private async Task<IpcResponse> FlushDnsCacheAsync()
        {
            try
            {
                var engine = _services.GetRequiredService<INetworkOptimizerEngine>();
                var (success, message, timestamp) = await engine.FlushDnsCacheAsync();
                return Ok(JsonSerializer.Serialize(new { Success = success, Message = message, Timestamp = timestamp }));
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private async Task<IpcResponse> RunNetworkDiagnosticsAsync()
        {
            try
            {
                var engine = _services.GetRequiredService<INetworkOptimizerEngine>();
                var diag = await engine.RunDiagnosticsAsync();
                return Ok(JsonSerializer.Serialize(diag));
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private static IpcResponse Ok(string data)   => new() { Success = true,  Data = data };







        private static IpcResponse Fail(string error, string? data = null) => new() { Success = false, ErrorMessage = error, Data = data };

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // STORAGE OPTIMIZER â€” Authoritative Handlers
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private Task<IpcResponse> ScanStorageCategoriesAsync(string? payload, CancellationToken ct)
        {
            return Task.Run<IpcResponse>(() =>
            {
                try
                {
                    var engine = new StorageCleanerEngine();
                    var categories = StorageCleanerEngine.BuildCategories();

                    // Scan each category for real file sizes
                    foreach (var cat in categories)
                    {
                        if (ct.IsCancellationRequested) break;
                        engine.ScanCategory(cat);
                    }

                    // Also include drive info
                    var storageEngine = _services.GetRequiredService<StorageEngine>();
                    var drives = storageEngine.GetDrives();

                    var result = new
                    {
                        Drives = drives.Select(d => new
                        {
                            d.Name,
                            d.Letter,
                            d.VolumeLabel,
                            d.DriveFormat,
                            d.DriveType,
                            d.MediaType,
                            d.Model,
                            d.InterfaceType,
                            d.TotalGb,
                            d.UsedGb,
                            d.FreeGb,
                            d.UsagePercentage,
                            d.HealthStatus,
                            d.SmartStatus,
                            d.TemperatureText,
                            d.StoragePressure,
                            d.IsSystemDrive
                        }).ToList(),
                        Categories = categories.Select(c => new
                        {
                            c.Id,
                            c.Name,
                            c.Description,
                            c.Icon,
                            c.CategoryType,
                            c.RiskLevel,
                            c.DetectedBytes,
                            c.FileCount,
                            c.IsSelected,
                            c.IsScanned,
                            c.FormattedSize
                        }).ToList(),
                        TotalReclaimableBytes = categories.Sum(c => c.DetectedBytes),
                        TotalFilesFound = categories.Sum(c => c.FileCount)
                    };

                    return Ok(JsonSerializer.Serialize(result));
                }
                catch (Exception ex) { return Fail(ex.Message); }
            }, ct);
        }

        private Task<IpcResponse> ApplyStorageCleanupAsync(string? payload, Action<IpcResponse> progressCallback, CancellationToken ct)
        {
            return Task.Run<IpcResponse>(() =>
            {
                try
                {
                    // Deserialize selected category IDs and drive letter
                    var req = string.IsNullOrEmpty(payload)
                        ? null
                        : JsonSerializer.Deserialize<StorageCleanupRequestDto>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    var selectedIds  = req?.SelectedCategoryIds ?? new List<string>();
                    string driveLetter = req?.DriveLetter ?? "C";

                    var engine = new StorageCleanerEngine();
                    var allCategories = StorageCleanerEngine.BuildCategories();

                    // Filter to selected categories only
                    var targetCategories = allCategories
                        .Where(c => selectedIds.Count == 0 || selectedIds.Contains(c.Id))
                        .ToList();

                    // Re-scan to get current sizes
                    foreach (var cat in targetCategories)
                        engine.ScanCategory(cat);

                    // Progress callback adapter
                    void OnProgress(StorageCleanupProgressReport report)
                    {
                        progressCallback(Ok(JsonSerializer.Serialize(new
                        {
                            ProgressUpdate = true,
                            report.CurrentStage,
                            report.ProgressPercent,
                            report.CurrentItemName,
                            report.FilesProcessed,
                            report.FilesRemoved,
                            report.FilesSkipped,
                            report.BytesReclaimed,
                            report.StatusMessage
                        })));
                    }

                    var result = engine.CleanCategories(targetCategories, driveLetter, OnProgress, ct);

                    // Send final result
                    progressCallback(Ok(JsonSerializer.Serialize(new
                    {
                        FinalResult = true,
                        result.Success,
                        result.BytesReclaimed,
                        FormattedReclaimed = result.FormattedReclaimed,
                        result.FilesRemoved,
                        result.FilesSkipped,
                        result.FilesFailed,
                        InitialFreeGb = Math.Round(result.InitialFreeBytes / (1024.0 * 1024 * 1024), 2),
                        FinalFreeGb   = Math.Round(result.FinalFreeBytes   / (1024.0 * 1024 * 1024), 2),
                        result.SummaryMessage
                    })));

                    return Ok(JsonSerializer.Serialize(result));
                }
                catch (Exception ex) { return Fail(ex.Message); }
            }, ct);
        }

        private IpcResponse GetRecycleBinInfoImpl()
        {
            try
            {
                var engine = new StorageCleanerEngine();
                var (sizeBytes, count) = engine.GetRecycleBinInfo();
                return Ok(JsonSerializer.Serialize(new
                {
                    SizeBytes = sizeBytes,
                    FormattedSize = StorageCleanupCategory.FormatBytes(sizeBytes),
                    ItemCount = count
                }));
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private IpcResponse EmptyRecycleBinImpl()
        {
            try
            {
                var engine = new StorageCleanerEngine();
                var (success, freedBytes, message) = engine.EmptyRecycleBin();
                return Ok(JsonSerializer.Serialize(new
                {
                    Success = success,
                    FreedBytes = freedBytes,
                    FormattedFreed = StorageCleanupCategory.FormatBytes(freedBytes),
                    Message = message
                }));
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private Task<IpcResponse> ScanLargeFilesAsync(string? payload, CancellationToken ct)
        {
            return Task.Run<IpcResponse>(() =>
            {
                try
                {
                    string driveLetter = "C:\\";
                    long minSizeBytes  = 500L * 1024 * 1024; // 500 MB default

                    if (!string.IsNullOrEmpty(payload))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("DriveLetter", out var dl)) driveLetter = (dl.GetString() ?? "C") + ":\\";
                            if (doc.RootElement.TryGetProperty("MinSizeMb", out var ms)) minSizeBytes = (long)ms.GetDouble() * 1024 * 1024;
                        }
                        catch { }
                    }

                    var engine = new StorageCleanerEngine();
                    var files = engine.FindLargeFiles(driveLetter, minSizeBytes, ct);

                    var dtos = files.Select(f => new
                    {
                        f.FileName,
                        f.FullPath,
                        f.Extension,
                        f.SizeBytes,
                        f.SizeMb,
                        f.FormattedSize,
                        LastModified = f.LastModified.ToString("yyyy-MM-dd"),
                        f.DriveLetter
                    }).ToList();

                    return Ok(JsonSerializer.Serialize(dtos));
                }
                catch (Exception ex) { return Fail(ex.Message); }
            }, ct);
        }

        private Task<IpcResponse> ScanDuplicateFilesAsync(string? payload, CancellationToken ct)
        {
            return Task.Run<IpcResponse>(() =>
            {
                try
                {
                    string rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    long minSizeBytes = 1L * 1024 * 1024; // 1 MB minimum for duplicates

                    if (!string.IsNullOrEmpty(payload))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("RootPath", out var rp)) rootPath = rp.GetString() ?? rootPath;
                            if (doc.RootElement.TryGetProperty("MinSizeMb", out var ms)) minSizeBytes = (long)ms.GetDouble() * 1024 * 1024;
                        }
                        catch { }
                    }

                    var engine = new StorageCleanerEngine();
                    var groups = engine.FindDuplicates(rootPath, minSizeBytes, ct);

                    var dtos = groups.Select(g => new
                    {
                        g.GroupId,
                        g.FileSizeBytes,
                        g.FormattedFileSize,
                        g.Hash,
                        g.FilePaths,
                        g.ReclaimableBytes,
                        g.FormattedReclaimable
                    }).ToList();

                    return Ok(JsonSerializer.Serialize(new
                    {
                        Groups = dtos,
                        TotalGroups = dtos.Count,
                        TotalFiles = dtos.Sum(g => g.FilePaths.Count),
                        TotalReclaimableBytes = dtos.Sum(g => g.ReclaimableBytes),
                        FormattedTotalReclaimable = StorageCleanupCategory.FormatBytes(dtos.Sum(g => (long)g.ReclaimableBytes))
                    }));
                }
                catch (Exception ex) { return Fail(ex.Message); }
            }, ct);
        }

        private IpcResponse DeleteStorageFileImpl(string? payload)
        {
            try
            {
                if (string.IsNullOrEmpty(payload)) return Fail("Empty payload");

                string rawPath = payload.Trim('"', ' ');
                if (!BiosOptimizer.Core.Implementations.Cleaners.SecureStoragePathValidator.ValidateForDeletion(rawPath, out string canonicalPath, out string failureReason))
                {
                    return Fail($"File deletion blocked by security policy: {failureReason}");
                }

                long size = new FileInfo(canonicalPath).Length;
                File.Delete(canonicalPath);

                if (File.Exists(canonicalPath))
                {
                    return Fail($"File deletion could not be verified: {Path.GetFileName(canonicalPath)} is locked or in use.");
                }

                return Ok(JsonSerializer.Serialize(new
                {
                    Success = true,
                    DeletedBytes = size,
                    FormattedSize = StorageCleanupCategory.FormatBytes(size),
                    Message = $"Deleted: {Path.GetFileName(canonicalPath)}"
                }));
            }
            catch (UnauthorizedAccessException ex) { return Fail($"Access denied: {ex.Message}"); }
            catch (IOException ex) { return Fail($"File in use: {ex.Message}"); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private class StorageCleanupRequestDto
        {
            public List<string> SelectedCategoryIds { get; set; } = new();
            public string DriveLetter { get; set; } = "C";
        }

        // --- Power Plan Optimization Backend Handlers ---
        private async Task<IpcResponse> GetPowerPlansAsync(CancellationToken ct)
        {
            try
            {
                var plans = await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.DiscoverPowerPlansAsync(ct);
                var active = await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.GetActiveSchemeAsync(ct);
                var hw = BiosOptimizer.Core.Implementations.Power.HardwareAnalyzer.Instance.Collect();
                var rec = BiosOptimizer.Core.Implementations.Power.HardwareAnalyzer.Instance.Recommend(hw, plans);
                var payload = new
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
                return Ok(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to discover power plans: {ex.Message}");
            }
        }

        private async Task<IpcResponse> ApplyPowerPlanAsync(string? guid, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(guid)) return Fail("Target GUID cannot be empty.");
                var res = await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.ApplyPowerPlanAsync(guid, BiosOptimizer.Core.Implementations.Power.PowerPlanChangeSource.USER_REQUEST, ct);
                if (!res.Success || !res.Verified)
                    return Fail(string.IsNullOrWhiteSpace(res.Message) ? "Failed to apply power plan." : res.Message, JsonSerializer.Serialize(res));
                return Ok(JsonSerializer.Serialize(res));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to apply power plan: {ex.Message}");
            }
        }

        private async Task<IpcResponse> RestorePowerPlanAsync(CancellationToken ct)
        {
            try
            {
                var res = await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.RestorePreviousPowerPlanAsync(ct);
                if (!res.Success || !res.Verified)
                    return Fail(string.IsNullOrWhiteSpace(res.Message) ? "Failed to restore power plan." : res.Message, JsonSerializer.Serialize(res));
                return Ok(JsonSerializer.Serialize(res));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to restore power plan: {ex.Message}");
            }
        }

        private async Task<IpcResponse> EnableUltimatePerformanceAsync(CancellationToken ct)
        {
            try
            {
                var res = await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.EnableUltimatePerformanceAsync(ct);
                if (!res.Success || !res.Verified)
                    return Fail(string.IsNullOrWhiteSpace(res.Message) ? "Failed to enable Ultimate Performance: " + res.Message : res.Message, JsonSerializer.Serialize(res));
                return Ok(JsonSerializer.Serialize(res));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to enable Ultimate Performance: {ex.Message}");
            }
        }

        private async Task<IpcResponse> CreateCustomAiPlanAsync(string? payload, CancellationToken ct)
        {
            try
            {
                bool isBattery = !string.IsNullOrWhiteSpace(payload) && payload.Contains("battery", StringComparison.OrdinalIgnoreCase);
                var res = isBattery
                    ? await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.CreateOrGetAiBatteryEfficiencyPlanAsync(ct)
                    : await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.CreateOrGetAiMaxPerformancePlanAsync(ct);
                if (!res.Success || !res.Verified)
                    return Fail(string.IsNullOrWhiteSpace(res.Message) ? "Failed to activate custom AI power plan." : res.Message, JsonSerializer.Serialize(res));
                return Ok(JsonSerializer.Serialize(res));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to create custom AI power plan: {ex.Message}");
            }
        }

        private async Task<IpcResponse> DeleteCustomAiPlanAsync(string? guid, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(guid)) return Fail("GUID cannot be empty.");
                var res = await BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.DeleteCustomAiPlanAsync(guid, ct);
                return Ok(JsonSerializer.Serialize(res));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to delete custom AI power plan: {ex.Message}");
            }
        }

        private static string? _cachedRestorePointsJson;
        private static DateTime _cachedRestorePointsUtc = DateTime.MinValue;
        private static string? _cachedRestoreStatusJson;
        private static DateTime _cachedRestoreStatusUtc = DateTime.MinValue;
        private static readonly TimeSpan SystemRestoreCacheTtl = TimeSpan.FromSeconds(60);
        private static readonly object _systemRestoreCacheLock = new();

        public static void InvalidateSystemRestoreCache()
        {
            lock (_systemRestoreCacheLock)
            {
                _cachedRestorePointsJson = null;
                _cachedRestorePointsUtc = DateTime.MinValue;
                _cachedRestoreStatusJson = null;
                _cachedRestoreStatusUtc = DateTime.MinValue;
            }
        }

        private async Task<IpcResponse> CreateRestorePointAsync(string? payload)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string? description = null;
                    string? origin = null;

                    if (!string.IsNullOrWhiteSpace(payload) && payload.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.String)
                                origin = o.GetString();
                            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                                description = d.GetString();
                        }
                        catch
                        {
                            return Fail("Invalid restore point request payload.");
                        }
                    }

                    if (!string.Equals(origin, "dashboard", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("Windows System Restore Point creation is restricted to the V3 Dashboard CREATE RESTORE POINT action.");
                    }

                    var res = BiosOptimizer.Core.Implementations.WindowsSystemRestoreEngine.Instance.CreateRestorePoint(description);
                    InvalidateSystemRestoreCache();
                    string json = JsonSerializer.Serialize(res);
                    return new IpcResponse
                    {
                        Success = res.Success,
                        Data = json,
                        ErrorMessage = res.Success ? null : (res.ErrorMessage ?? res.StatusMessage)
                    };
                }
                catch (Exception ex)
                {
                    return Fail($"System Restore execution exception: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> GetRestorePointsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (_systemRestoreCacheLock)
                    {
                        if (_cachedRestorePointsJson != null && (DateTime.UtcNow - _cachedRestorePointsUtc) < SystemRestoreCacheTtl)
                        {
                            return Ok(_cachedRestorePointsJson);
                        }
                    }

                    var list = BiosOptimizer.Core.Implementations.WindowsSystemRestoreEngine.Instance.EnumerateRestorePoints();
                    string json = JsonSerializer.Serialize(list);
                    lock (_systemRestoreCacheLock)
                    {
                        _cachedRestorePointsJson = json;
                        _cachedRestorePointsUtc = DateTime.UtcNow;
                    }
                    return Ok(json);
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to enumerate restore points: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> CheckSystemRestoreStatusAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (_systemRestoreCacheLock)
                    {
                        if (_cachedRestoreStatusJson != null && (DateTime.UtcNow - _cachedRestoreStatusUtc) < SystemRestoreCacheTtl)
                        {
                            return Ok(_cachedRestoreStatusJson);
                        }
                    }

                    var engine = BiosOptimizer.Core.Implementations.WindowsSystemRestoreEngine.Instance;
                    var (isAvail, availReason) = engine.CheckSystemRestoreAvailability();
                    var (isEnabled, drive, protReason) = engine.CheckSystemProtectionStatus();
                    string json = JsonSerializer.Serialize(new
                    {
                        isAvailable = isAvail,
                        isEnabled,
                        enabled = isEnabled,
                        protectionEnabled = isEnabled,
                        drive,
                        reason = isEnabled ? protReason : (isAvail ? protReason : availReason),
                        IsAvailable = isAvail,
                        IsEnabled = isEnabled,
                        Drive = drive,
                        Reason = isEnabled ? protReason : (isAvail ? protReason : availReason)
                    });
                    lock (_systemRestoreCacheLock)
                    {
                        _cachedRestoreStatusJson = json;
                        _cachedRestoreStatusUtc = DateTime.UtcNow;
                    }
                    return Ok(json);
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to check system restore status: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> GetSystemProtectionStatusAsync(string? payload)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var (isEnabled, drive, reason) = BiosOptimizer.Core.Implementations.WindowsSystemRestoreEngine.Instance.CheckSystemProtectionStatus(payload);
                    return Ok(JsonSerializer.Serialize(new { IsEnabled = isEnabled, Drive = drive, Reason = reason }));
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to check system protection status: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> EnableSystemProtectionAsync(string? payload)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var (success, message) = BiosOptimizer.Core.Implementations.WindowsSystemRestoreEngine.Instance.EnableSystemProtection(payload);
                    InvalidateSystemRestoreCache();
                    return new IpcResponse
                    {
                        Success = success,
                        Data = JsonSerializer.Serialize(new { Success = success, Message = message }),
                        ErrorMessage = success ? null : message
                    };
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to enable system protection: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> DisableSystemProtectionAsync(string? payload)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(payload)) return Fail("Target drive letter required.");
                    string drive = payload.Trim().TrimEnd('\\', '/');
                    if (drive.Length != 2 || drive[1] != ':')
                    {
                        return Fail("Invalid drive format. Expected standard drive letter such as 'C:'.");
                    }

                    _logger.LogWarning("System Protection disable requested for drive {Drive}", drive);
                    var (success, message) = BiosOptimizer.Core.Implementations.WindowsSystemRestoreEngine.Instance.DisableSystemProtection(drive);
                    InvalidateSystemRestoreCache();
                    return new IpcResponse
                    {
                        Success = success,
                        Data = JsonSerializer.Serialize(new { Success = success, Message = message }),
                        ErrorMessage = success ? null : message
                    };
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to disable system protection: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> ExecuteServicingToolAsync(string? payload, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload)) return Fail("Tool ID payload is required.");

                var req = JsonSerializer.Deserialize<ServicingToolRequestDto>(payload) 
                    ?? new ServicingToolRequestDto { ToolId = payload };

                var timeout = req.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(req.TimeoutSeconds) : TimeSpan.FromHours(2);

                var res = await WindowsServicingHealthEngine.Instance.ExecuteServicingToolAsync(
                    req.ToolId,
                    req.Arguments,
                    timeout,
                    ct
                );

                var dto = new ServicingToolResponseDto
                {
                    ToolId = res.ToolId,
                    ToolName = res.ToolName,
                    Command = res.Command,
                    ProcessStarted = res.ProcessStarted,
                    ProcessId = res.ProcessId,
                    ExitCode = res.ExitCode,
                    Success = res.Success,
                    Summary = res.Summary,
                    DiagnosedRootCause = res.DiagnosedRootCause,
                    RecommendedRemediation = res.RecommendedRemediation,
                    DurationSeconds = res.Duration.TotalSeconds,
                    IsCancelled = res.IsCancelled,
                    IsTimedOut = res.IsTimedOut,
                    IsNotApplicable = res.IsNotApplicable,
                    StandardOutput = res.StandardOutput,
                    StandardError = res.StandardError,
                    RelevantLogExcerpts = res.RelevantLogExcerpts
                };

                return Ok(JsonSerializer.Serialize(dto));
            }
            catch (Exception ex)
            {
                return Fail($"Failed to execute servicing tool: {ex.Message}");
            }
        }

        private async Task<IpcResponse> GetServicingEnvironmentAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var snap = WindowsServicingHealthEngine.Instance.DetectServicingEnvironment();
                    return Ok(JsonSerializer.Serialize(snap));
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to query servicing environment: {ex.Message}");
                }
            });
        }

        private async Task<IpcResponse> EnsureServicingPrerequisitesAsync(string? payload)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string toolId = payload ?? "sfc";
                    var (repaired, msg) = WindowsServicingHealthEngine.Instance.EnsureServicingPrerequisites(toolId);
                    return Ok(JsonSerializer.Serialize(new { Repaired = repaired, Message = msg }));
                }
                catch (Exception ex)
                {
                    return Fail($"Failed to remediate servicing prerequisites: {ex.Message}");
                }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // V4 MAXIMUM PERFORMANCE IPC HANDLERS
        // ══════════════════════════════════════════════════════════════════

        private async Task<IpcResponse> V4ScanProcessIntelligenceAsync()
        {
            try
            {
                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var snapshot = await coordinator.ScanProcessesAsync();
                return Ok(JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Process Scan failed: {ex.Message}");
            }
        }

        private async Task<IpcResponse> V4GetMaximumPerformancePlanAsync(string? payload)
        {
            try
            {
                int pass = 1;
                string workloadProfile = "Unified";

                if (!string.IsNullOrEmpty(payload))
                {
                    if (int.TryParse(payload, out int p))
                    {
                        pass = p;
                    }
                    else if (payload.Equals("Unified", StringComparison.OrdinalIgnoreCase) ||
                             payload.Equals("Gaming", StringComparison.OrdinalIgnoreCase) ||
                             payload.Equals("VideoEditing", StringComparison.OrdinalIgnoreCase) ||
                             payload.Equals("Streaming", StringComparison.OrdinalIgnoreCase))
                    {
                        workloadProfile = payload;
                    }
                    else
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("pass", out var passEl) || doc.RootElement.TryGetProperty("Pass", out passEl))
                            {
                                pass = passEl.GetInt32();
                            }
                            if (doc.RootElement.TryGetProperty("workloadProfile", out var wpEl) || doc.RootElement.TryGetProperty("WorkloadProfile", out wpEl))
                            {
                                workloadProfile = wpEl.GetString() ?? "Unified";
                            }
                        }
                        catch { }
                    }
                }

                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var plan = await coordinator.GeneratePlanAsync(pass, workloadProfile);
                return Ok(JsonSerializer.Serialize(plan));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Plan generation failed: {ex.Message}");
            }
        }

        private async Task<IpcResponse> V4ExecuteMaximumPerformancePassAsync(string? payload, CancellationToken ct)
        {
            try
            {
                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var result = await coordinator.ExecuteFullOptimizationAsync(maxPasses: 1, "Unified", false, null, ct);
                return Ok(JsonSerializer.Serialize(result));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Pass execution failed: {ex.Message}");
            }
        }

        private async Task<IpcResponse> V4ExecuteFullOptimizationAsync(string? payload, CancellationToken ct)
        {
            try
            {
                int maxPasses = 4;
                string workloadProfile = "Unified";
                bool isCustom = false;
                System.Collections.Generic.List<string>? selectedIds = null;

                if (!string.IsNullOrEmpty(payload))
                {
                    if (int.TryParse(payload, out int p))
                    {
                        maxPasses = p;
                    }
                    else
                    {
                        try
                        {
                            var req = JsonSerializer.Deserialize<V4OptimizationExecutionRequestDto>(payload);
                            if (req != null)
                            {
                                maxPasses = req.MaxPasses > 0 ? req.MaxPasses : 4;
                                workloadProfile = !string.IsNullOrEmpty(req.WorkloadProfile) ? req.WorkloadProfile : "Unified";
                                isCustom = req.IsCustomSelection;
                                selectedIds = req.SelectedCandidateIds;
                            }
                        }
                        catch { }
                    }
                }

                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var result = await coordinator.ExecuteFullOptimizationAsync(maxPasses, workloadProfile, isCustom, selectedIds, ct);
                return Ok(JsonSerializer.Serialize(result));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Full Optimization failed: {ex.Message}");
            }
        }

        private async Task<IpcResponse> V4RollbackMaximumPerformanceAsync(string? payload)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string batchId = payload ?? string.Empty;
                    var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                    bool ok = coordinator.RollbackOptimization(batchId, out int restored, out int failed, out string msg);
                    return Ok(JsonSerializer.Serialize(new { Success = ok, RestoredCount = restored, FailedCount = failed, Message = msg }));
                }
                catch (Exception ex)
                {
                    return Fail($"V4 Rollback failed: {ex.Message}");
                }
            });
        }

        private IpcResponse V4GetLiveMetricsImpl()
        {
            try
            {
                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var metrics = coordinator.GetLiveMetrics();
                return Ok(JsonSerializer.Serialize(metrics));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Live Metrics failed: {ex.Message}");
            }
        }

        private IpcResponse V4GetOptimizationHistoryImpl()
        {
            try
            {
                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var records = coordinator.RollbackEngine.GetTransactions();
                return Ok(JsonSerializer.Serialize(records));
            }
            catch (Exception ex)
            {
                return Fail($"V4 History query failed: {ex.Message}");
            }
        }

        private IpcResponse V4GetProcessTargetBandImpl(string? payload)
        {
            try
            {
                int startingCount = 200;
                if (!string.IsNullOrEmpty(payload) && int.TryParse(payload, out int count))
                {
                    startingCount = count;
                }

                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var band = coordinator.TargetBandEngine.CalculateTargetBand(startingCount);
                return Ok(JsonSerializer.Serialize(band));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Process Target Band calculation failed: {ex.Message}");
            }
        }

        private async Task<IpcResponse> V4GetWorkloadGuardTelemetryAsync()
        {
            try
            {
                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                var telemetry = await coordinator.GetWorkloadGuardTelemetryAsync();
                return Ok(JsonSerializer.Serialize(telemetry));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Workload Guard telemetry failed: {ex.Message}");
            }
        }

        private async Task<IpcResponse> V4SuppressWorkloadDriftAsync()
        {
            try
            {
                var coordinator = BiosOptimizer.Core.V4.Coordinator.V4MaximumPerformanceCoordinator.Instance;
                int suppressedCount = await coordinator.SuppressWorkloadDriftAsync();
                return Ok(JsonSerializer.Serialize(new { SuppressedCount = suppressedCount, Success = true }));
            }
            catch (Exception ex)
            {
                return Fail($"V4 Workload Drift suppression failed: {ex.Message}");
            }
        }
    }
}
