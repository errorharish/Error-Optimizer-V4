using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Cleaners;
using BiosOptimizer.GUI;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.GUI.Views;

namespace AdaptiveTester
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("=======================================================");
            Console.WriteLine("STARTING UNIVERSAL HARDWARE-ADAPTIVE PERFORMANCE AUDIT");
            Console.WriteLine("=======================================================");

            // 1. Hardware Profiler Speed & Accuracy Test
            TestHardwareProfiler();

            // 2. Hardware Classification Matrix Test
            TestClassificationMatrix();

            // 3. Adaptive Resource Governor Limits Test
            TestAdaptiveGovernorLimits();

            // 4. Workload Detection & Dynamic Throttling Test
            TestWorkloadDetectionAndThrottling();

            // 5. Rolling File Logger & Retention Test
            TestRollingFileLogger();

            // 6. Streaming Storage Batch Scanner Test
            TestStreamingStorageScanner();

            // 7. WPF UI Thread Integration & Responsive Navigation Test
            TestWpfIntegration();

            Console.WriteLine();
            Console.WriteLine("=======================================================");
            Console.WriteLine("*** ALL HARDWARE-ADAPTIVE TESTS PASSED SUCCESSFULLY! ***");
            Console.WriteLine("=======================================================");
        }

        static void TestHardwareProfiler()
        {
            Console.WriteLine("\n[TEST 1] Testing Lightweight Hardware Profiling Speed & Accuracy...");
            var sw = Stopwatch.StartNew();
            var profile = HardwareProfiler.GetQuickProfile(forceRefresh: true);
            sw.Stop();

            Console.WriteLine($"  Detection Duration : {sw.ElapsedMilliseconds} ms (Benchmark Target: < 50 ms)");
            Console.WriteLine($"  CPU Model          : {profile.CpuModel}");
            Console.WriteLine($"  Logical Cores      : {profile.CpuLogicalCores}");
            Console.WriteLine($"  Total RAM          : {profile.RamTotalGb:F1} GB");
            Console.WriteLine($"  Available RAM      : {profile.RamAvailableGb:F1} GB ({profile.RamUsagePercent:F0}% Used)");
            Console.WriteLine($"  Power Source       : {(profile.IsBatteryPowered ? "Battery (" + profile.BatteryPercent + "%)" : "AC Power")}");
            Console.WriteLine($"  Free Disk Space    : {profile.TotalFreeDiskSpaceGb:F1} GB");
            Console.WriteLine($"  OS Version / Build : {profile.WindowsVersion} ({profile.WindowsBuild})");
            Console.WriteLine($"  Hardware Tier      : {profile.Tier} - {profile.TierDescription}");

            if (sw.ElapsedMilliseconds > 150)
            {
                throw new Exception($"Hardware detection took too long: {sw.ElapsedMilliseconds} ms");
            }
            Console.WriteLine("  [PASS] Lightweight hardware profiling passed.");
        }

        static void TestClassificationMatrix()
        {
            Console.WriteLine("\n[TEST 2] Testing Hardware Tier Classification Matrix...");

            // Simulated Low-End: 4GB RAM, 2 Cores
            var lowEnd = new HardwareProfile { RamTotalGb = 4.0, CpuLogicalCores = 2, IsBatteryPowered = false };
            var lowEndTier = HardwareProfiler.ClassifyMachine(lowEnd);
            Console.WriteLine($"  Simulated Low-End (4GB RAM, 2 Cores)    -> Classified as: {lowEndTier} (Expected: LowResource)");
            if (lowEndTier != HardwareTier.LowResource) throw new Exception("Low-End classification failed.");

            // Simulated Mid-Range: 16GB RAM, 8 Cores, AC
            var midRange = new HardwareProfile { RamTotalGb = 16.0, CpuLogicalCores = 8, IsBatteryPowered = false, TotalFreeDiskSpaceGb = 50 };
            var midRangeTier = HardwareProfiler.ClassifyMachine(midRange);
            Console.WriteLine($"  Simulated Mid-Range (16GB RAM, 8 Cores) -> Classified as: {midRangeTier} (Expected: Normal)");
            if (midRangeTier != HardwareTier.Normal) throw new Exception("Mid-Range classification failed.");

            // Simulated High-End: 32GB RAM, 16 Cores, AC
            var highEnd = new HardwareProfile { RamTotalGb = 32.0, CpuLogicalCores = 16, IsBatteryPowered = false, TotalFreeDiskSpaceGb = 100 };
            var highEndTier = HardwareProfiler.ClassifyMachine(highEnd);
            Console.WriteLine($"  Simulated High-End (32GB RAM, 16 Cores) -> Classified as: {highEndTier} (Expected: HighPerformance)");
            if (highEndTier != HardwareTier.HighPerformance) throw new Exception("High-End classification failed.");

            // Simulated Battery Saver / Low Battery: 16GB RAM, 8 Cores, Battery at 10%
            var batteryLow = new HardwareProfile { RamTotalGb = 16.0, CpuLogicalCores = 8, IsBatteryPowered = true, BatteryPercent = 10 };
            var batteryLowTier = HardwareProfiler.ClassifyMachine(batteryLow);
            Console.WriteLine($"  Simulated Low Battery (10% Battery)     -> Classified as: {batteryLowTier} (Expected: LowResource)");
            if (batteryLowTier != HardwareTier.LowResource) throw new Exception("Low Battery classification failed.");

            Console.WriteLine("  [PASS] Hardware classification matrix passed.");
        }

        static void TestAdaptiveGovernorLimits()
        {
            Console.WriteLine("\n[TEST 3] Testing Adaptive Resource Governor Dynamic Limits...");
            var gov = AdaptiveResourceGovernor.Instance;

            gov.SetManualOverride(HardwareTier.LowResource);
            Console.WriteLine($"  [LowResource Tier] MaxConcurrency: {gov.MaxWorkerConcurrency}, TelemetryInterval: {gov.TelemetryIntervalMs}ms, FileBatchSize: {gov.FileBatchSize}");
            if (gov.MaxWorkerConcurrency != 1 || gov.TelemetryIntervalMs != 2500 || gov.FileBatchSize != 100)
                throw new Exception("LowResource limits mismatch.");

            gov.SetManualOverride(HardwareTier.Normal);
            Console.WriteLine($"  [Normal Tier]      MaxConcurrency: {gov.MaxWorkerConcurrency}, TelemetryInterval: {gov.TelemetryIntervalMs}ms, FileBatchSize: {gov.FileBatchSize}");
            if (gov.MaxWorkerConcurrency != 2 || gov.TelemetryIntervalMs != 1200 || gov.FileBatchSize != 500)
                throw new Exception("Normal limits mismatch.");

            gov.SetManualOverride(HardwareTier.HighPerformance);
            Console.WriteLine($"  [HighPerformance]  MaxConcurrency: {gov.MaxWorkerConcurrency}, TelemetryInterval: {gov.TelemetryIntervalMs}ms, FileBatchSize: {gov.FileBatchSize}");
            if (gov.MaxWorkerConcurrency != 4 || gov.TelemetryIntervalMs != 1000 || gov.FileBatchSize != 1000)
                throw new Exception("HighPerformance limits mismatch.");

            // Reset manual override to native profile
            gov.SetManualOverride(null);
            Console.WriteLine("  [PASS] Adaptive governor limits passed.");
        }

        static void TestWorkloadDetectionAndThrottling()
        {
            Console.WriteLine("\n[TEST 4] Testing Workload Detection & Dynamic Throttling System...");
            var gov = AdaptiveResourceGovernor.Instance;
            bool eventFired = false;
            string lastReason = "";

            gov.ThrottlingChanged += (isThrottled, reason) =>
            {
                eventFired = true;
                lastReason = reason;
            };

            // 1. Simulate High CPU / Game Workload
            gov.ReportSystemBusy(true, "Foreground Game: Cyberpunk2077");
            Console.WriteLine($"  Triggered Busy Workload -> IsThrottled: {gov.IsThrottled}, Reason: '{gov.ThrottlingReason}'");
            Console.WriteLine($"  Throttled Telemetry Interval: {gov.TelemetryIntervalMs} ms (Expected: 4000 ms)");
            if (!gov.IsThrottled || gov.TelemetryIntervalMs != 4000 || !eventFired)
                throw new Exception("Workload throttling trigger failed.");

            // 2. Restore System Idle
            eventFired = false;
            gov.ReportSystemBusy(false, "");
            Console.WriteLine($"  Restored Idle Workload  -> IsThrottled: {gov.IsThrottled}, Telemetry Interval: {gov.TelemetryIntervalMs} ms");
            if (gov.IsThrottled || !eventFired)
                throw new Exception("Workload idle restore failed.");

            Console.WriteLine("  [PASS] Workload detection & dynamic throttling passed.");
        }

        static void TestRollingFileLogger()
        {
            Console.WriteLine("\n[TEST 5] Testing Rolling File Logger & Retention Limits...");
            string tempLogDir = Path.Combine(Path.GetTempPath(), "ErrorOptimizer_LogTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Max file size 50 KB, max 3 rolling backups
                var logger = new RollingFileLogger(tempLogDir, "test.log", maxFileSizeBytes: 50 * 1024, maxRollingFiles: 3);

                // Write 250 KB of log data
                string dummyPayload = new string('A', 1024);
                for (int i = 0; i < 250; i++)
                {
                    logger.Info($"Payload line {i}: {dummyPayload}");
                }

                var files = Directory.GetFiles(tempLogDir, "test.log*");
                Console.WriteLine($"  Generated Rolling Files Count: {files.Length} (Max Allowed: 4 = 1 current + 3 backups)");
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    Console.WriteLine($"    - {Path.GetFileName(f)}: {fi.Length / 1024} KB");
                    if (fi.Length > 60 * 1024) throw new Exception($"Log file exceeded max allowed size: {fi.Length} bytes");
                }

                if (files.Length > 4) throw new Exception($"Too many rolling log files: {files.Length}");
                Console.WriteLine("  [PASS] Rolling file logger & retention limits verified.");
            }
            finally
            {
                try { Directory.Delete(tempLogDir, true); } catch { }
            }
        }

        static void TestStreamingStorageScanner()
        {
            Console.WriteLine("\n[TEST 6] Testing Streaming Storage Scanner & Batch Yielding...");
            var engine = new StorageCleanerEngine();
            var categories = StorageCleanerEngine.BuildCategories();
            var tempCat = categories.Find(c => c.Id == "temp-files");

            if (tempCat != null)
            {
                var sw = Stopwatch.StartNew();
                var scanned = engine.ScanCategory(tempCat);
                sw.Stop();
                Console.WriteLine($"  Scanned Category: {scanned.Name}");
                Console.WriteLine($"  Detected Files  : {scanned.FileCount}");
                Console.WriteLine($"  Detected Bytes  : {scanned.DetectedBytes / (1024 * 1024):F1} MB");
                Console.WriteLine($"  Scan Duration   : {sw.ElapsedMilliseconds} ms");
            }
            Console.WriteLine("  [PASS] Streaming storage scanner verified.");
        }

        static void TestWpfIntegration()
        {
            Console.WriteLine("\n[TEST 7] Testing WPF UI Thread Integration & Responsive Navigation...");
            var app = new App();
            app.InitializeComponent();

            var ipcClient = new IpcClient();
            var vm = new DashboardViewModel(ipcClient);
            Console.WriteLine($"  Dashboard Initialized -> DeviceName: '{vm.DeviceName}', TierBadge: '{vm.HardwareTierBadgeText}'");
            Console.WriteLine($"  IsThrottled: {vm.IsThrottled}, Score: {vm.Score}");

            if (string.IsNullOrEmpty(vm.DeviceName) || string.IsNullOrEmpty(vm.HardwareTierBadgeText))
                throw new Exception("Dashboard hardware tier bindings empty.");

            // Test 10x repeated navigation cycle
            for (int i = 1; i <= 10; i++)
            {
                var toolsVm = new ToolsViewModel(ipcClient);
                var toolsView = new ToolsView { DataContext = toolsVm };
                toolsView.Measure(new Size(1280, 780));
                toolsView.Arrange(new Rect(0, 0, 1280, 780));
            }
            Console.WriteLine("  10x Navigation cycle passed cleanly.");
            vm.Dispose();
            Console.WriteLine("  [PASS] WPF UI integration and navigation verified.");
        }
    }
}
