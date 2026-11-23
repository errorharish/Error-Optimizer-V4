using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.ActionHandlers;
using BiosOptimizer.Detection;
using BiosOptimizer.Safety;
using BiosOptimizer.Core.Models;
using BiosOptimizer.CLI.Adapters.Workloads;
using BiosOptimizer.Core.Common;
using BiosOptimizer.Core.Implementations.Cleaners;
using BiosOptimizer.Core.Implementations.Diagnostics;
using BiosOptimizer.Core.Implementations.Storage;

namespace BiosOptimizer.Service
{
    public class ErrorOptimizerService : ServiceBase
    {
        private static string LogPath => Path.Combine(AppEnvironment.ResolveWritableDataDirectory("logs"), "service.log");
        private IHost? _host;
        private CancellationTokenSource? _cts;
        private Task? _backgroundTask;

        public ErrorOptimizerService()
        {
            ServiceName = "BiosOptimizer.Service";
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            Log("service-start");
            _cts = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => StartHostAsync(args, _cts.Token));
        }

        protected override void OnStop()
        {
            Log("service-stop");
            try
            {
                try { BiosOptimizer.Core.Implementations.Diagnostics.HardwareTelemetryCollector.Instance.StopAll(); } catch { }
                _cts?.Cancel();
                if (_host != null)
                {
                    _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    _host.Dispose();
                    _host = null;
                }
            }
            catch (Exception ex)
            {
                Log($"service-stop-error {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        public void TestStartupAndStop(string[] args)
        {
            Console.WriteLine("Starting BiosOptimizer.Service in console mode. Press Ctrl+C or Enter to stop...");
            OnStart(args);

            var stopEvent = new ManualResetEventSlim(false);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => stopEvent.Set();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stopEvent.Set();
            };

            if (Environment.UserInteractive)
            {
                Task.Run(() =>
                {
                    try
                    {
                        var line = Console.ReadLine();
                        if (line != null)
                        {
                            stopEvent.Set();
                        }
                    }
                    catch { }
                });
            }

            stopEvent.Wait();
            Console.WriteLine("Stopping BiosOptimizer.Service...");
            OnStop();
            Console.WriteLine("Stopped.");
        }

        private async Task StartHostAsync(string[] args, CancellationToken token)
        {
            try
            {
                Log("host-builder-init");
                var host = Host.CreateDefaultBuilder(args)
                    .ConfigureServices((ctx, services) =>
                    {
                        RegisterBackendServices(services);
                        services.AddHostedService<NamedPipeServer>();
                        services.AddHostedService<BiosOptimizer.Service.Metrics.LiveSystemMetricsService>();
                        services.AddHostedService<BiosOptimizer.Service.Workers.PolicySupervisorWorker>();
                        services.AddHostedService<BiosOptimizer.Service.Workers.AutoOptimizeWorker>();
                    })
                    .Build();

                _host = host;
                Log("host-starting");
                await host.RunAsync(token);
                Log("host-stopped");
            }
            catch (OperationCanceledException)
            {
                Log("host-cancelled");
            }
            catch (Exception ex)
            {
                Log($"host-fatal-error {ex}");
            }
        }

        private static void RegisterBackendServices(IServiceCollection services)
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            var baseDir = !string.IsNullOrEmpty(exePath) ? Path.GetDirectoryName(exePath) : AppContext.BaseDirectory;
            var profilesDir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "profiles");
            
            if (!Directory.Exists(profilesDir))
                profilesDir = Path.Combine(baseDir ?? "", "..", "profiles");
            if (!Directory.Exists(profilesDir))
                profilesDir = Path.Combine(Directory.GetCurrentDirectory(), "profiles");
            if (!Directory.Exists(profilesDir))
                profilesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
            if (!Directory.Exists(profilesDir))
                profilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer", "profiles");

            var workloadDetectors = new System.Collections.Generic.List<IWorkloadDetector>
            {
                new EmulatorWorkloadDetector(),
                new AdobeWorkloadDetector(),
                new TopazWorkloadDetector(),
                new GameWorkloadDetector()
            };

            IProfileRepository profileRepo = new ProfileRepository(profilesDir);
            IEnvironmentDetector envDetector = new EnvironmentDetector(workloadDetectors);
            IProcessSnapshot processSnapshot = new ProcessSnapshot();
            ISafetyPolicy safetyPolicy = new ProtectedTargetsPolicy();
            IVerificationEngine verificationEngine = new VerificationEngine();

            IServiceManager serviceManager = new BiosOptimizer.CLI.Adapters.WindowsServiceManager();
            IRegistryManager registryManager = new BiosOptimizer.CLI.Adapters.WindowsRegistryManager();
            IScheduledTaskManager taskManager = new BiosOptimizer.CLI.Adapters.WindowsScheduledTaskManager();
            IStartupManager startupManager = new BiosOptimizer.CLI.Adapters.WindowsStartupManager();
            IPackageManager packageManager = new BiosOptimizer.CLI.Adapters.WindowsPackageManager();
            BiosOptimizer.CLI.Adapters.Bios.IAcpiEvaluator acpiEvaluator = new BiosOptimizer.CLI.Adapters.Bios.NullAcpiEvaluator();
            IProviderManager providerManager = new BiosOptimizer.CLI.Adapters.Bios.ProviderManager(envDetector, acpiEvaluator);

            IActionRegistry actionRegistry = new ActionRegistry();
            actionRegistry.RegisterHandler(new SetServiceStartupHandler(serviceManager, safetyPolicy, verificationEngine));
            actionRegistry.RegisterHandler(new SetRegistryValueHandler(registryManager));
            actionRegistry.RegisterHandler(new DisableScheduledTaskHandler(taskManager));
            actionRegistry.RegisterHandler(new DisableStartupEntryHandler(startupManager));
            actionRegistry.RegisterHandler(new RemoveAppxPackageHandler(packageManager, safetyPolicy, verificationEngine));
            actionRegistry.RegisterHandler(new SetBiosSettingHandler(providerManager, verificationEngine));
            actionRegistry.RegisterHandler(new PowerPlanOptimizationHandler());
            actionRegistry.RegisterHandler(new ProcessPriorityHandler());
            actionRegistry.RegisterHandler(new CpuAffinityHandler());
            actionRegistry.RegisterHandler(new ForegroundPriorityHandler());
            actionRegistry.RegisterHandler(new HagsConfigurationHandler());
            actionRegistry.RegisterHandler(new PagefileOptimizationHandler());
            actionRegistry.RegisterHandler(new DnsFlushHandler());
            actionRegistry.RegisterHandler(new TcpAutoTuningHandler());
            actionRegistry.RegisterHandler(new CongestionControlHandler());
            actionRegistry.RegisterHandler(new NetworkThrottlingHandler());
            actionRegistry.RegisterHandler(new VisualFxHandler());
            actionRegistry.RegisterHandler(new GpuPerformanceHandler());
            actionRegistry.RegisterHandler(new CpuParkingHandler());
            actionRegistry.RegisterHandler(new MouseOptimizationHandler());
            actionRegistry.RegisterHandler(new KeyboardOptimizationHandler());
            actionRegistry.RegisterHandler(new ManagedRegistryHealthHandler());
            actionRegistry.RegisterHandler(new SvchostSplitThresholdHandler());

            IOptimizationScoreEngine scoreEngine = new OptimizationScoreEngine();
            IBackupManager backupManager = new BackupManager(registryManager, serviceManager);
            ITierEngine tierEngine = new TierEngine(profileRepo, envDetector, actionRegistry, processSnapshot, backupManager);
            IUniversalEligibilityEngine universalEligibilityEngine = new UniversalEligibilityEngine(profileRepo, envDetector, actionRegistry, backupManager, packageManager, processSnapshot);
            var diagEngine = new DiagnosticsEngine();
            var storageEngine = new StorageEngine();
            var ramCleaner = new RamCleanupEngine();
            var tempCleaner = new TempCleanerEngine();

            // Singleton registrations
            services.AddSingleton<IEnvironmentDetector>(envDetector);
            services.AddSingleton<IProfileRepository>(profileRepo);
            services.AddSingleton<IActionRegistry>(actionRegistry);
            services.AddSingleton<IBackupManager>(backupManager);
            services.AddSingleton<IProcessReductionEngine, ProcessReductionEngine>();
            services.AddSingleton<IInputOptimizerEngine, InputOptimizerEngine>();
            services.AddSingleton<IRegistryTweakEngine, RegistryTweakEngine>();
            services.AddSingleton<INetworkOptimizerEngine, NetworkOptimizerEngine>();
            services.AddSingleton<ITierEngine>(tierEngine);
            services.AddSingleton<IUniversalEligibilityEngine>(universalEligibilityEngine);
            services.AddSingleton<UniversalEligibilityEngine>((UniversalEligibilityEngine)universalEligibilityEngine);
            services.AddSingleton<IProcessSnapshot>(processSnapshot);
            services.AddSingleton<IOptimizationScoreEngine>(scoreEngine);
            services.AddSingleton<IStartupManager>(startupManager);
            services.AddSingleton<IServiceManager>(serviceManager);
            services.AddSingleton<IProviderManager>(providerManager);
            services.AddSingleton(diagEngine);
            services.AddSingleton(storageEngine);
            services.AddSingleton(ramCleaner);
            services.AddSingleton(tempCleaner);
            services.AddSingleton<IPackageManager>(packageManager);
        }

        private static void Log(string message)
        {
            try
            {
                string path = LogPath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {message}\n");
            }
            catch { }
        }
    }
}
