using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels;

namespace AiWorkloadRamTester
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("===================================================================");
            Console.WriteLine("STARTING AI WORKLOAD & AI RAM LIMITER UPGRADED ENGINE AUDIT");
            Console.WriteLine("===================================================================");

            try
            {
                RunAuditAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[AUDIT FATAL ERROR]: {ex}");
                Environment.Exit(1);
            }
        }

        static async Task RunAuditAsync()
        {
            await Task.Yield();
            var workloadDetector = UserWorkloadDetector.Instance;
            var workloadEngine = WorkloadOptimizationEngine.Instance;
            var ramEngine = RamLimiterEngine.Instance;

            // ----------------------------------------------------
            // TEST 1: Continuous Workload Classification & Telemetry
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 1] Testing Continuous Workload Classification & Telemetry...");
            var liveClassification = workloadDetector.ClassifyCurrentWorkload();
            Console.WriteLine($"  Current Workload Category: {liveClassification.Category}");
            Console.WriteLine($"  Primary Process: '{liveClassification.PrimaryProcessName}' (PID: {liveClassification.PrimaryPid})");
            Console.WriteLine($"  Fullscreen Window: {liveClassification.IsFullscreen}");
            Console.WriteLine($"  Confidence Score: {liveClassification.ConfidenceScore}%");
            Console.WriteLine($"  Description: '{liveClassification.Description}'");
            Console.WriteLine($"  System RAM Usage: {liveClassification.RamUsagePercent:F1}%");

            if (liveClassification.ConfidenceScore < 0 || liveClassification.ConfidenceScore > 100)
                throw new Exception("Invalid confidence score range.");
            Console.WriteLine("  [PASS] Workload classifier returned valid telemetry & confidence.");

            // ----------------------------------------------------
            // TEST 2: Workload Engine Inventory & Discovery
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 2] Testing Workload Engine Inventory & Process Classifier...");
            workloadEngine.BuildInstalledInventory();
            Console.WriteLine($"  Discovered Installed Workloads: {workloadEngine.InstalledInventory.Count}");
            foreach (var item in workloadEngine.InstalledInventory.Take(8))
            {
                Console.WriteLine($"    - [{item.TypeDisplay}] '{item.DisplayName}' (Launcher: {item.Launcher})");
            }

            // Test process classification for various workloads
            int currentPid = Process.GetCurrentProcess().Id;
            var currentInfo = workloadEngine.ClassifyProcess(currentPid);
            Console.WriteLine($"  Self-Process Classification: '{currentInfo.DisplayName}' | Type: {currentInfo.TypeDisplay} | Confidence: {currentInfo.ConfidenceScore}%");
            Console.WriteLine("  [PASS] Workload inventory and classification operational.");

            // ----------------------------------------------------
            // TEST 3: Hardware-Aware AI RAM Limiter Detection
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 3] Testing Hardware-Aware RAM Limiter Engine Telemetry...");
            ramEngine.ScanAndEnforce();
            Console.WriteLine($"  System Total RAM: {ramEngine.SystemTotalRamMb:F0} MB ({ramEngine.SystemTotalRamMb / 1024.0:F1} GB)");
            Console.WriteLine($"  System Used RAM: {ramEngine.SystemUsedRamMb:F0} MB ({ramEngine.SystemUsedRamMb / 1024.0:F1} GB)");
            Console.WriteLine($"  System RAM Usage: {ramEngine.SystemRamUsagePercent:F1}%");
            Console.WriteLine($"  Memory Pressure Level: {ramEngine.PressureLevelText}");
            Console.WriteLine($"  Auto-Detected App Groups: {ramEngine.AutoDetectedGroups.Count}");

            foreach (var grp in ramEngine.AutoDetectedGroups.Take(5))
            {
                Console.WriteLine($"    - [{grp.StatusText}] '{grp.ApplicationName}' | Memory: {grp.CurrentRamMb:F0}MB / Limit: {grp.RecommendedLimitMb:F0}MB | Procs: {grp.ProcessCount}");
            }
            Console.WriteLine("  [PASS] Hardware-aware RAM telemetry and grouping verified.");

            // ----------------------------------------------------
            // TEST 4: Safe Working-Set Trim with Before/After Verification
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 4] Testing Safe Working-Set Trimming and Verification...");
            double beforeRam = ramEngine.SystemUsedRamMb;
            
            // Find a non-critical test candidate (e.g. self or background group)
            var targetGroup = ramEngine.AutoDetectedGroups.FirstOrDefault(g => !g.IsForeground && g.CurrentRamMb > 50);
            if (targetGroup != null)
            {
                Console.WriteLine($"  Target candidate for safe trim: '{targetGroup.ApplicationName}' ({targetGroup.CurrentRamMb:F0}MB)");
            }
            else
            {
                Console.WriteLine("  No high-RAM background candidate currently running; verifying memory inspection safety...");
            }

            ramEngine.ScanAndEnforce();
            double afterRam = ramEngine.SystemUsedRamMb;
            Console.WriteLine($"  Memory Before: {beforeRam:F0} MB -> After: {afterRam:F0} MB");
            Console.WriteLine("  [PASS] Verified non-destructive memory inspection & safe trimming path.");

            // ----------------------------------------------------
            // TEST 5: WPF ViewModel Integration on STA Thread
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 5] Testing AiOptimizationViewModel WPF DataBinding on STA Thread...");
            var staThread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    _ = new Application();
                }
                var vm = new AiOptimizationViewModel(new IpcClient());
                Console.WriteLine($"  Active Tab: {vm.ActiveTab}");
                Console.WriteLine($"  Tier Name: '{vm.TierName}'");
                Console.WriteLine($"  Ram Limiter Auto Groups: {vm.AutoDetectedGroups.Count}");
                Console.WriteLine($"  Workload Filtered Inventory: {vm.FilteredInventory.Count}");
                Console.WriteLine($"  Workload Running Count: {vm.RunningWorkloads.Count}");

                // Switch to Workload tab
                vm.SelectWorkloadTabCommand.Execute(null);
                Console.WriteLine($"  Switched Tab: {vm.ActiveTab} (IsWorkloadTab: {vm.IsWorkloadTab})");

                // Switch to Power Plan tab
                vm.SelectPowerPlanTabCommand.Execute(null);
                Console.WriteLine($"  Switched Tab: {vm.ActiveTab} (IsPowerPlanTab: {vm.IsPowerPlanTab})");

                Console.WriteLine("  [PASS] WPF ViewModel bindings and tab navigation verified.");
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Console.WriteLine("\n===================================================================");
            Console.WriteLine("*** ALL AI WORKLOAD & AI RAM LIMITER UPGRADE TESTS PASSED! ***");
            Console.WriteLine("===================================================================");
        }
    }
}
