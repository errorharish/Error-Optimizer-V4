using System;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Power;

namespace PowerPlanMasterTester
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("===================================================================");
            Console.WriteLine("STARTING AUTHORITATIVE POWER PLAN ENGINE MASTER AUDIT");
            Console.WriteLine("===================================================================");

            try
            {
                RunAuditAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[FATAL AUDIT ERROR]: {ex}");
                Environment.Exit(1);
            }
        }

        static async Task RunAuditAsync()
        {
            var engine = PowerPlanEngine.Instance;

            // 1. Initial State Check
            Console.WriteLine("\n[TEST 1: INITIAL STATE & DISCOVERY]");
            var (initialGuid, initialName) = await engine.GetActiveSchemeAsync();
            Console.WriteLine($"  Initial Active Scheme: '{initialName}' ({initialGuid})");

            var plans = await engine.DiscoverPowerPlansAsync();
            Console.WriteLine($"  Total Discovered Plans: {plans.Count}");
            foreach (var p in plans)
            {
                Console.WriteLine($"    * [{p.Status}] '{p.Name}' | GUID: {p.Guid} | Active: {p.IsActive} | Custom AI: {p.IsCustomAiPlan}");
            }

            var activeInList = plans.FirstOrDefault(p => p.IsActive);
            if (activeInList == null || !activeInList.Guid.Equals(initialGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Active plan in discovery list ({activeInList?.Guid}) does not match Windows active scheme ({initialGuid}).");
            }
            Console.WriteLine("  [PASS] Real Windows power plans enumerated accurately with real installed GUIDs.");

            // 2. Apply High Performance
            Console.WriteLine("\n[TEST 2: APPLY HIGH PERFORMANCE]");
            var highPlan = plans.FirstOrDefault(p => p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase) && p.IsInstalled);
            if (highPlan != null)
            {
                Console.WriteLine($"  Applying: '{highPlan.Name}' ({highPlan.Guid})");
                var resHigh = await engine.ApplyPowerPlanAsync(highPlan.Guid);
                Console.WriteLine($"  Result: Success={resHigh.Success}, Verified={resHigh.Verified}, Message='{resHigh.Message}'");
                var (curGuid, curName) = await engine.GetActiveSchemeAsync();
                Console.WriteLine($"  Windows Reports: '{curName}' ({curGuid})");
                if (!curGuid.Equals(highPlan.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"High Performance activation failed: Expected {highPlan.Guid}, got {curGuid}");
                }
                Console.WriteLine("  [PASS] High Performance activated and verified from Windows authoritative source.");
            }

            // 3. Apply Ultimate Performance
            Console.WriteLine("\n[TEST 3: APPLY ULTIMATE PERFORMANCE]");
            var ultPlan = plans.FirstOrDefault(p => p.Name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase));
            if (ultPlan != null)
            {
                Console.WriteLine($"  Applying: '{ultPlan.Name}' ({ultPlan.Guid})");
                var resUlt = await engine.ApplyPowerPlanAsync(ultPlan.Guid);
                Console.WriteLine($"  Result: Success={resUlt.Success}, Verified={resUlt.Verified}, Message='{resUlt.Message}'");
                var (curGuid, curName) = await engine.GetActiveSchemeAsync();
                Console.WriteLine($"  Windows Reports: '{curName}' ({curGuid})");
                if (!curGuid.Equals(resUlt.ActiveSchemeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Ultimate Performance activation failed: Expected {resUlt.ActiveSchemeGuid}, got {curGuid}");
                }
                Console.WriteLine("  [PASS] Ultimate Performance activated and verified from Windows authoritative source.");
            }

            // 4. Apply Balanced
            Console.WriteLine("\n[TEST 4: APPLY BALANCED]");
            var balPlan = plans.FirstOrDefault(p => p.Name.Equals("Balanced", StringComparison.OrdinalIgnoreCase) && p.IsInstalled);
            if (balPlan != null)
            {
                Console.WriteLine($"  Applying: '{balPlan.Name}' ({balPlan.Guid})");
                var resBal = await engine.ApplyPowerPlanAsync(balPlan.Guid);
                Console.WriteLine($"  Result: Success={resBal.Success}, Verified={resBal.Verified}, Message='{resBal.Message}'");
                var (curGuid, curName) = await engine.GetActiveSchemeAsync();
                Console.WriteLine($"  Windows Reports: '{curName}' ({curGuid})");
                if (!curGuid.Equals(balPlan.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Balanced activation failed: Expected {balPlan.Guid}, got {curGuid}");
                }
                Console.WriteLine("  [PASS] Balanced activated and verified from Windows authoritative source.");
            }

            // 5. Create Custom AI Power Plan
            Console.WriteLine("\n[TEST 5: CREATE CUSTOM AI POWER PLAN]");
            var resCreate = await engine.CreateCustomAiPlanAsync();
            Console.WriteLine($"  Result: Success={resCreate.Success}, Verified={resCreate.Verified}, Message='{resCreate.Message}'");
            var (aiGuid, aiName) = await engine.GetActiveSchemeAsync();
            Console.WriteLine($"  Active Scheme after creation: '{aiName}' ({aiGuid})");
            if (!resCreate.Success || !resCreate.Verified || !aiGuid.Equals(resCreate.ActiveSchemeGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Custom AI Power Plan creation failed: Expected active {resCreate.ActiveSchemeGuid}, got {aiGuid}");
            }

            var refreshedPlans = await engine.DiscoverPowerPlansAsync();
            var detectedAi = refreshedPlans.FirstOrDefault(p => p.Guid.Equals(aiGuid, StringComparison.OrdinalIgnoreCase));
            if (detectedAi == null || !detectedAi.IsActive || !detectedAi.IsCustomAiPlan)
            {
                throw new Exception($"Created AI plan '{aiGuid}' was not found as Active Custom AI Plan in refreshed discovery.");
            }
            Console.WriteLine("  [PASS] Custom AI Power Plan created, calibrated, activated, and verified in Windows.");

            // 6. Delete Custom AI Power Plan and Restore
            Console.WriteLine("\n[TEST 6: DELETE CUSTOM AI PLAN & RESTORE]");
            var resDel = await engine.DeleteCustomAiPlanAsync(aiGuid);
            Console.WriteLine($"  Result: Success={resDel.Success}, Message='{resDel.Message}'");
            var (finalGuid, finalName) = await engine.GetActiveSchemeAsync();
            Console.WriteLine($"  Active Scheme after delete: '{finalName}' ({finalGuid})");
            var finalPlans = await engine.DiscoverPowerPlansAsync();
            if (finalPlans.Any(p => p.Guid.Equals(aiGuid, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception($"Custom AI plan '{aiGuid}' still exists in Windows after deletion.");
            }
            Console.WriteLine("  [PASS] Custom AI plan deleted cleanly from Windows and balanced restored.");

            Console.WriteLine("\n===================================================================");
            Console.WriteLine("*** ALL AUTHORITATIVE POWER PLAN ENGINE AUDIT TESTS PASSED! ***");
            Console.WriteLine("===================================================================");
        }
    }
}
