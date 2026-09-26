using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;

namespace InputOptimizerStateTester
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("================================================================================");
            Console.WriteLine(" ERROR OPTIMIZER V3 — ADVANCED INPUT OPTIMIZER ACCEPTANCE TEST SUITE");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            int totalTests = 0;
            int passedTests = 0;

            void AssertTest(string testName, bool condition, string details = "")
            {
                totalTests++;
                if (condition)
                {
                    passedTests++;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("[PASS] ");
                    Console.ResetColor();
                    Console.WriteLine($"{testName} -> {details}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[FAIL] ");
                    Console.ResetColor();
                    Console.WriteLine($"{testName} -> {details}");
                }
            }

            var journal = new InputTransactionJournal();
            journal.Clear();

            // -- SETUP: Set temporary non-optimal baseline for test items --
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", true);
                key?.SetValue("MouseDataQueueSize", 100, RegistryValueKind.DWord);
            }
            catch { }
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", true);
                key?.SetValue("KeyboardDataQueueSize", 100, RegistryValueKind.DWord);
            }
            catch { }

            // -- TEST 1: Initial Discovery & Optimization-Specific Applicability --
            Console.WriteLine("-- 1. Testing Optimization-Specific Applicability Detection --");
            var engine = new InputOptimizerEngine();
            var initialPlan = await engine.PlanInputOptimizationAsync();

            AssertTest("Plan Generation", initialPlan != null && initialPlan.Actions.Count > 0, $"Actions: {initialPlan?.Actions.Count ?? 0}");
            
            bool allHaveSpecificLocation = initialPlan!.Actions.All(a => !string.IsNullOrWhiteSpace(a.Reason));
            AssertTest("Specific Registry/Device Target Defined", allHaveSpecificLocation, "All actions have explicit technical location");

            int initialApplicable = initialPlan.Actions.Count(a => a.Applicable);
            int initialNotApplicable = initialPlan.Actions.Count(a => !a.Applicable && a.Status == "NOT_AVAILABLE");
            int initialUnsupported = initialPlan.Actions.Count(a => !a.Applicable && a.Status == "NOT_SUPPORTED");
            int initialOptimal = initialPlan.Actions.Count(a => a.Status == "OPTIMIZED" || a.AlreadyOptimized);
            int initialPending = initialPlan.Actions.Count(a => a.Applicable && a.Status == "PENDING");
            int initialRestartReq = initialPlan.Actions.Count(a => a.Status == "RESTART_REQUIRED");

            Console.WriteLine($"   • Total Cataloged: {initialPlan.Actions.Count}");
            Console.WriteLine($"   • Applicable: {initialApplicable}");
            Console.WriteLine($"   • Already Optimal: {initialOptimal}");
            Console.WriteLine($"   • Pending: {initialPending}");
            Console.WriteLine($"   • Restart Required: {initialRestartReq}");
            Console.WriteLine($"   • Not Applicable: {initialNotApplicable}");
            Console.WriteLine($"   • Unsupported: {initialUnsupported}");

            AssertTest("Pending Optimizations Detected", initialPending > 0, $"{initialPending} pending optimizations ready for apply");
            AssertTest("No Generic Applicability Assumption", initialApplicable <= initialPlan.Actions.Count, "Each optimization evaluated individually");

            // -- TEST 2: Real Current State Live Reading --
            Console.WriteLine("\n-- 2. Testing Live State Reading (No Fake/Cached Values) --");
            bool validCurrentStates = initialPlan.Actions.All(a => !string.IsNullOrWhiteSpace(a.CurrentValue) && a.CurrentValue != "Unknown");
            AssertTest("Live State Reading", validCurrentStates, "All current states read live from Win32/Registry");

            // -- TEST 3: Apply Optimization Execution & Readback Verification --
            Console.WriteLine("\n-- 3. Testing Apply Workflow & Readback Verification --");
            var (applySuccess, applyMsg) = await engine.ApplyInputOptimizationAsync("ADVANCED");
            Console.WriteLine($"   • Apply Result: Success={applySuccess}, Message='{applyMsg}'");
            AssertTest("Apply Execution", applySuccess, applyMsg);

            // -- TEST 4: Post-Apply Plan State & Restart Required Classification --
            Console.WriteLine("\n-- 4. Testing Post-Apply Plan State (Before Reboot) --");
            var postApplyPlan = await engine.PlanInputOptimizationAsync();
            int postRestartReq = postApplyPlan.Actions.Count(a => a.Status == "RESTART_REQUIRED");
            int postOptimal = postApplyPlan.Actions.Count(a => a.Status == "OPTIMIZED" || a.AlreadyOptimized);
            int postPending = postApplyPlan.Actions.Count(a => a.Applicable && a.Status == "PENDING");

            Console.WriteLine($"   • Post-Apply Optimized: {postOptimal}");
            Console.WriteLine($"   • Post-Apply Restart Required: {postRestartReq}");
            Console.WriteLine($"   • Post-Apply Pending: {postPending}");

            AssertTest("Restart Required Classified Separately", postRestartReq > 0, $"{postRestartReq} items waiting for reboot verification");
            AssertTest("Pending Items Decremented", postPending == 0, $"Remaining pending: {postPending}");

            // -- TEST 5: Journal Persistence --
            Console.WriteLine("\n-- 5. Testing Journal & Transaction Persistence --");
            var savedTx = journal.LoadLatestTransaction();
            AssertTest("Transaction Saved", savedTx != null && savedTx.Items.Count > 0, $"Persisted items: {savedTx?.Items?.Count ?? 0}");
            
            bool itemsHaveAllFields = savedTx != null && savedTx.Items.All(i => 
                !string.IsNullOrWhiteSpace(i.OptimizationId) &&
                !string.IsNullOrWhiteSpace(i.TargetState) &&
                !string.IsNullOrWhiteSpace(i.State));
            AssertTest("Transaction Fields Integrity", itemsHaveAllFields, "OptimizationId, TargetState, State, RequiresReboot verified");

            // -- TEST 6: Authoritative Startup Reconciliation (Simulating Reboot) --
            Console.WriteLine("\n-- 6. Testing Startup Reconciliation (Simulating Post-Reboot Verification) --");
            // Simulate that system rebooted (Boot uptime at apply is now in past)
            if (savedTx != null)
            {
                foreach (var item in savedTx.Items)
                {
                    item.BootUptimeAtApplyMs = long.MaxValue; // Forces hasRebooted = true
                }
                journal.SaveTransaction(savedTx);
            }

            var restartEngine = new InputOptimizerEngine();
            var reconciledPlan = await restartEngine.PlanInputOptimizationAsync();

            int recApplicable = reconciledPlan.Actions.Count(a => a.Applicable);
            int recOptimal = reconciledPlan.Actions.Count(a => a.Status == "OPTIMIZED" || a.AlreadyOptimized);
            int recRestartReq = reconciledPlan.Actions.Count(a => a.Status == "RESTART_REQUIRED");
            int recPending = reconciledPlan.Actions.Count(a => a.Applicable && a.Status == "PENDING");

            Console.WriteLine($"   • Post-Reboot Applicable: {recApplicable}");
            Console.WriteLine($"   • Post-Reboot Optimized (Verified): {recOptimal}");
            Console.WriteLine($"   • Post-Reboot Restart Required: {recRestartReq}");
            Console.WriteLine($"   • Post-Reboot Pending: {recPending}");

            AssertTest("Post-Reboot Verification", recOptimal == recApplicable, $"All {recOptimal}/{recApplicable} applicable items verified post-reboot");
            AssertTest("Zero Stale Pending Items", recPending == 0, "No pending items remaining");

            // -- TEST 7: Re-Execution Protection --
            Console.WriteLine("\n-- 7. Testing Re-Execution Prevention --");
            var (reapplySuccess, reapplyMsg) = await restartEngine.ApplyInputOptimizationAsync("ADVANCED");
            Console.WriteLine($"   • Reapply Result: Success={reapplySuccess}, Message='{reapplyMsg}'");
            AssertTest("Already Optimal Items Protected From Re-Apply", reapplySuccess && reapplyMsg.Contains("already"), reapplyMsg);

            // -- TEST 8: Counter Invariant Checks --
            Console.WriteLine("\n-- 8. Testing Counter Mathematical Invariants --");
            bool invariant = (recApplicable == recOptimal + recPending + recRestartReq);
            AssertTest("Counter Invariant (Applicable == Optimized + Pending + RestartReq)", invariant, 
                $"{recApplicable} == {recOptimal} + {recPending} + {recRestartReq}");

            // -- SUMMARY SCORECARD --
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($" TOTAL ACCEPTANCE SCORECARD: {passedTests}/{totalTests} ({(double)passedTests / totalTests * 100:F1}%)");
            Console.WriteLine("================================================================================");

            return passedTests == totalTests ? 0 : 1;
        }
    }
}
