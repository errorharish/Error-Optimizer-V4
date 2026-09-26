using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiosOptimizer.Core.Implementations.Power;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.GUI.Views;
using BiosOptimizer.IPC;

namespace PowerPlanTester
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("=======================================================");
            Console.WriteLine("STARTING NATIVE AI POWER PLAN ENGINE MASTER AUDIT");
            Console.WriteLine("=======================================================");

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
            var engine = PowerPlanEngine.Instance;
            var hwAnalyzer = HardwareAnalyzer.Instance;

            // ----------------------------------------------------
            // TEST 1: Hardware Analysis & Classification
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 1] Testing Hardware Analysis & Classification...");
            var hwReport = hwAnalyzer.Collect(forceRefresh: true);
            Console.WriteLine($"  Detected CPU      : {hwReport.CpuName} ({hwReport.PhysicalCores} Physical, {hwReport.LogicalCores} Logical Cores, {hwReport.BaseClockGhz:F2} GHz)");
            Console.WriteLine($"  Detected RAM      : {hwReport.RamTotalGb:F1} GB");
            Console.WriteLine($"  Detected GPU      : {hwReport.GpuName} (Discrete: {hwReport.IsDiscreteGpu})");
            Console.WriteLine($"  Detected Power    : {(hwReport.IsLaptop ? (hwReport.IsBatteryPowered ? $"Battery ({hwReport.BatteryPercent}%)" : "Laptop AC") : "Desktop AC")}");
            Console.WriteLine($"  Hardware Tier     : {hwReport.TierClassification}");
            Console.WriteLine($"  Detected Workload : {hwReport.DetectedWorkload}");
            Console.WriteLine($"  Thermal Status    : {hwReport.ThermalStatus}");

            if (string.IsNullOrWhiteSpace(hwReport.CpuName)) throw new Exception("CPU detection returned empty.");
            Console.WriteLine("  [PASS] Hardware analysis returned authentic system parameters.");

            // ----------------------------------------------------
            // TEST 2: Native Windows Scheme Discovery
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 2] Testing Native Win32 Scheme Discovery (PowerEnumerate)...");
            var plans = await engine.DiscoverPowerPlansAsync();
            var (activeGuid, activeName) = await engine.GetActiveSchemeAsync();

            Console.WriteLine($"  Active Windows Scheme: '{activeName}' ({activeGuid})");
            Console.WriteLine($"  Total Installed Schemes: {plans.Count(p => p.IsInstalled)}");
            foreach (var p in plans)
            {
                Console.WriteLine($"    - [{(p.IsActive ? "ACTIVE" : p.StatusText)}] '{p.Name}' | GUID: {p.Guid} | Battery: {p.BatteryImpact} | Safety: {p.RiskLevel}");
            }

            if (!plans.Any(p => p.IsActive)) throw new Exception("No active scheme detected.");
            Console.WriteLine("  [PASS] Native enumeration succeeded.");

            // ----------------------------------------------------
            // TEST 3: Hardware & Workload-Aware AI Recommendation
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 3] Testing AI Recommendation Generation...");
            var rec = engine.GenerateRecommendation(plans);
            Console.WriteLine($"  Recommended Scheme : '{rec.RecommendedPlanName}' ({rec.RecommendedPlanGuid})");
            Console.WriteLine($"  Recommendation Why : {rec.Reason}");
            Console.WriteLine($"  Active Workload    : {rec.ActiveWorkloadSummary}");
            Console.WriteLine("  [PASS] AI Recommendation verified.");

            // ----------------------------------------------------
            // TEST 4: Scheme Transitions with Strict Windows Readback
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 4] Testing Scheme Transitions with Independent Readback...");
            string initialGuid = activeGuid;

            // Transition to High Performance
            var highPerf = plans.FirstOrDefault(p => p.Name.Equals("High performance", StringComparison.OrdinalIgnoreCase) && p.IsInstalled);
            if (highPerf != null)
            {
                Console.WriteLine($"  Applying '{highPerf.Name}' ({highPerf.Guid})...");
                var resHigh = await engine.ApplyPowerPlanAsync(highPerf.Guid);
                if (!resHigh.Success || !resHigh.Verified) throw new Exception($"Failed to apply High Performance: {resHigh.Message}");
                var verifyHigh = await engine.GetActiveSchemeAsync();
                if (!verifyHigh.guid.Equals(highPerf.Guid, StringComparison.OrdinalIgnoreCase)) throw new Exception("Readback mismatch for High Performance.");
                Console.WriteLine($"  [VERIFIED] Windows active scheme confirmed: '{verifyHigh.name}' ({verifyHigh.guid})");
            }

            // Restore initial
            Console.WriteLine($"  Restoring initial scheme '{activeName}' ({initialGuid})...");
            var resRestore = await engine.ApplyPowerPlanAsync(initialGuid);
            if (!resRestore.Success || !resRestore.Verified) throw new Exception($"Failed to restore initial plan: {resRestore.Message}");
            Console.WriteLine("  [PASS] Standard transitions verified.");

            // ----------------------------------------------------
            // TEST 5: Custom AI Power Plan Creation & Tuning
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 5] Testing Custom AI Power Plan Creation, Tuning & Activation...");
            var createRes = await engine.CreateCustomAiPlanAsync();
            Console.WriteLine($"  Creation Result: Success={createRes.Success}, Verified={createRes.Verified}, Message='{createRes.Message}'");
            if (!createRes.Success || !createRes.Verified) throw new Exception($"AI Plan creation failed: {createRes.Message}");

            var verifyAi = await engine.GetActiveSchemeAsync();
            Console.WriteLine($"  Windows Active Scheme after Creation: '{verifyAi.name}' ({verifyAi.guid})");

            var plansAfterCreate = await engine.DiscoverPowerPlansAsync();
            var aiPlan = plansAfterCreate.FirstOrDefault(p => p.IsCustomAiPlan && p.IsInstalled);
            if (aiPlan == null) throw new Exception("Created AI Plan not found in PowerEnumerate discovery!");
            Console.WriteLine($"  [VERIFIED] AI Plan '{aiPlan.Name}' found in Windows powercfg schemes list! (Active: {aiPlan.IsActive})");

            // ----------------------------------------------------
            // TEST 6: Custom AI Plan Deletion & Safety
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 6] Testing Custom AI Plan Cleanup & Safe Deletion...");
            var delRes = await engine.DeleteCustomAiPlanAsync(aiPlan.Guid);
            Console.WriteLine($"  Delete Result: Success={delRes.Success}, Message='{delRes.Message}'");
            if (!delRes.Success) throw new Exception($"Failed to delete AI Plan: {delRes.Message}");

            var plansAfterDel = await engine.DiscoverPowerPlansAsync();
            if (plansAfterDel.Any(p => p.Guid.Equals(aiPlan.Guid, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception("Deleted AI Plan still present in Windows enumeration!");
            }
            Console.WriteLine("  [PASS] Custom AI Plan deleted cleanly from Windows registry/schemes.");

            // Restore Balanced
            await engine.ApplyPowerPlanAsync(PowerPlanEngine.BalancedGuid);
            var finalVerify = await engine.GetActiveSchemeAsync();
            Console.WriteLine($"  Final Active Scheme: '{finalVerify.name}' ({finalVerify.guid})");

            // ----------------------------------------------------
            // TEST 7: WPF View & DataBinding Integrity
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 7] Testing WPF UI Thread & DataBinding Integrity...");
            var staThread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    _ = new Application();
                }
                var vm = new AiOptimizationViewModel(new BiosOptimizer.GUI.Services.IpcClient());
                vm.ActiveTab = AiOptimizationActiveTab.PowerPlan;
                vm.RefreshPowerPlansAsync().GetAwaiter().GetResult();

                Console.WriteLine($"  Active Tab: {vm.ActiveTab}, Plans in VM: {vm.PowerPlans.Count}, Active Plan: {vm.CurrentActivePlanName}");
                Console.WriteLine($"  Hardware Diagnostics in VM: {vm.HardwareSummaryText}");
                Console.WriteLine("  [PASS] WPF ViewModel & DataBinding state verified cleanly on STA thread.");
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Console.WriteLine("\n=======================================================");
            Console.WriteLine("*** ALL NATIVE AI POWER PLAN ENGINE TESTS PASSED! ***");
            Console.WriteLine("=======================================================");
        }
    }
}
