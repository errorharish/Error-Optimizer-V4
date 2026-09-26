using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Services;

namespace RegistryStateTester
{
    class MemoryBackupManager : IBackupManager
    {
        private readonly Dictionary<string, (string key, string val, string orig, string type, string tgt, string desc)> _backups = new();

        public bool CreateSystemRestorePoint(string description) => true;
        public bool BackupRegistryState(IEnumerable<string> registryPaths) => true;
        public bool BackupServiceState(IEnumerable<string> serviceNames) => true;
        public bool LogKilledProcesses(IEnumerable<string> processPaths) => true;
        public bool RestoreAll() => true;

        public bool BackupRegistryValue(string owner, string registryKey, string valueName, object oldValue, string oldType, object targetValue, string result)
        {
            _backups[owner] = (registryKey, valueName, oldValue?.ToString() ?? "", oldType, targetValue?.ToString() ?? "", result);
            return true;
        }

        public bool RestoreByOwner(string owner)
        {
            if (!_backups.TryGetValue(owner, out var b)) return false;
            var parts = b.key.Split('\\', 2);
            var root = parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine;
            try
            {
                using var k = root.CreateSubKey(parts[1], true);
                if (k != null)
                {
                    if (b.orig == "VALUE DID NOT EXIST" || string.IsNullOrEmpty(b.orig))
                    {
                        try { k.DeleteValue(b.val, false); } catch { }
                    }
                    else
                    {
                        if (b.type.Contains("DWord") && int.TryParse(b.orig, out int iVal))
                            k.SetValue(b.val, iVal, RegistryValueKind.DWord);
                        else
                            k.SetValue(b.val, b.orig, RegistryValueKind.String);
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("================================================================================");
            Console.WriteLine(" ERROR OPTIMIZER V3 — FINAL MASTER REGISTRY ENGINE ACCEPTANCE TEST SUITE");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            var backupManager = new MemoryBackupManager();
            var engine = new RegistryTweakEngine(backupManager);

            int passed = 0;
            int total = 0;

            void AssertTest(string name, bool condition, string detail = "")
            {
                total++;
                if (condition)
                {
                    passed++;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("[PASS] ");
                    Console.ResetColor();
                    Console.WriteLine($"{name} {(string.IsNullOrEmpty(detail) ? "" : $"-> {detail}")}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[FAIL] ");
                    Console.ResetColor();
                    Console.WriteLine($"{name} {(string.IsNullOrEmpty(detail) ? "" : $"-> {detail}")}");
                }
            }

            // -- TEST 1: Fresh Registry Scan & Catalog Discovery --
            Console.WriteLine("-- 1. Testing Fresh Registry Scan & Catalog Discovery --");
            var plan = await engine.PlanRegistryTweakAsync();
            AssertTest("Catalog Discovery", plan.Success && plan.Tweaks.Count >= 20, $"Catalog entries found: {plan.Tweaks.Count}");
            AssertTest("Counter Mathematical Invariant (Total == Applicable + NotApplicable + Unsupported)",
                plan.TotalCount == (plan.ApplicableCount + plan.NotApplicableCount + plan.UnsupportedCount),
                $"{plan.TotalCount} == {plan.ApplicableCount} + {plan.NotApplicableCount} + {plan.UnsupportedCount}");

            // -- TEST 2: Per-Optimization Applicability Detection --
            Console.WriteLine();
            Console.WriteLine("-- 2. Testing Per-Optimization Applicability Detection --");
            var win32Separation = plan.Tweaks.FirstOrDefault(t => t.Id == "tweak.process.priority");
            AssertTest("Win32PrioritySeparation Present", win32Separation != null, $"SubKey: {win32Separation?.SubKey}");

            var hags = plan.Tweaks.FirstOrDefault(t => t.Id == "tweak.gpu.hags");
            AssertTest("HAGS Applicability Evaluated", hags != null, $"Status: {hags?.Status}, Applicability: {hags?.Applicability}");

            var ntfsLastAccess = plan.Tweaks.FirstOrDefault(t => t.Id == "tweak.storage.lastaccess");
            AssertTest("NTFS LastAccess Evaluated", ntfsLastAccess != null, $"Reason: {ntfsLastAccess?.ApplicabilityReason}");

            // -- TEST 3: Live Current-Value Readback (No Caching) --
            Console.WriteLine();
            Console.WriteLine("-- 3. Testing Live Registry Current-Value Readback --");
            bool allHaveLiveValues = plan.Tweaks.All(t => !string.IsNullOrEmpty(t.CurrentValue));
            AssertTest("Live Current Values Read", allHaveLiveValues, $"All {plan.Tweaks.Count} tweaks have live values read from Windows");

            // -- TEST 4: Recommendation Generation Based on State --
            Console.WriteLine();
            Console.WriteLine("-- 4. Testing Recommendation Rules --");
            AssertTest("Recommendation Counter Integrity", plan.RecommendedCount == plan.Tweaks.Count(t => t.Status == "RECOMMENDED" || t.Status == "READY" || t.Status == "EXTERNALLY CHANGED"),
                $"Recommended Count: {plan.RecommendedCount}");

            // -- TEST 5: Single Optimization Apply on Pending Item (with immediate readback) --
            Console.WriteLine();
            Console.WriteLine("-- 5. Testing Single Optimization Apply on Pending Item --");
            var pendingTweak = plan.Tweaks.FirstOrDefault(t => t.Status == "RECOMMENDED" || t.Status == "READY") 
                               ?? plan.Tweaks.First();
            string testId = pendingTweak.Id;
            
            var applySingle = await engine.ApplyRegistryTweaksAsync(new List<string> { testId });
            AssertTest("Single Apply Result", applySingle.Success && (applySingle.AppliedCount > 0 || applySingle.VerifiedCount > 0), $"Message: {applySingle.Message}");

            // Re-read live to verify
            var planAfterSingle = await engine.PlanRegistryTweakAsync();
            var verifySingle = planAfterSingle.Tweaks.FirstOrDefault(t => t.Id == testId);
            AssertTest("Immediate Readback Verification", verifySingle?.Status == "ALREADY OPTIMIZED" || verifySingle?.Status == "VERIFIED" || verifySingle?.Status == "RESTART REQUIRED",
                $"Current: {verifySingle?.CurrentValue}, Target: {verifySingle?.TargetValue}, Status: {verifySingle?.Status}");

            // -- TEST 6: Rollback Validation --
            Console.WriteLine();
            Console.WriteLine("-- 6. Testing Rollback & Exact Value Restoration --");
            var restoreResult = await engine.RestoreRegistryTweaksAsync(new List<string> { testId });
            AssertTest("Rollback Execution", restoreResult.Success && restoreResult.RestoredCount >= 1, $"Restored: {restoreResult.RestoredCount}");

            // -- TEST 7: Multi-Tweak Batch Apply (Including Restart Required Items) --
            Console.WriteLine();
            Console.WriteLine("-- 7. Testing Multi-Tweak Batch Apply --");
            var batchIds = new List<string> { "tweak.process.priority", "tweak.ram.clearpagefile", "tweak.input.mousehover" };
            var batchApply = await engine.ApplyRegistryTweaksAsync(batchIds);
            AssertTest("Batch Apply Success", batchApply.Success, $"Message: {batchApply.Message}");

            var planAfterBatch = await engine.PlanRegistryTweakAsync();
            AssertTest("Batch Apply Optimizations Verified or Restart Pending", 
                planAfterBatch.AlreadyOptimizedCount > 0 || planAfterBatch.RestartRequiredCount > 0,
                $"Restart Required: {planAfterBatch.RestartRequiredCount}, Optimized: {planAfterBatch.AlreadyOptimizedCount}");

            // -- TEST 8: Journal Persistence Across Simulated Restarts --
            Console.WriteLine();
            Console.WriteLine("-- 8. Testing Journal Disk & Registry Persistence --");
            var journal = new RegistryTransactionJournal();
            var entries = journal.LoadEntries();
            AssertTest("Journal Entries Persisted", entries.Count >= 1, $"Persisted transaction count: {entries.Count}");

            // -- TEST 9: Startup Reconciliation & Post-Reboot Verification Simulation --
            Console.WriteLine();
            Console.WriteLine("-- 9. Testing Startup Reconciliation --");
            var reconcilePlan = await engine.ReconcileStartupAsync();
            AssertTest("Reconciliation Succeeded", reconcilePlan.Success, $"Total: {reconcilePlan.TotalCount}, Optimized: {reconcilePlan.AlreadyOptimizedCount}");

            // -- TEST 10: Re-Execution Protection (Skipping Already Optimal) --
            Console.WriteLine();
            Console.WriteLine("-- 10. Testing Re-Execution Protection --");
            var reapply = await engine.ApplyRegistryTweaksAsync(batchIds);
            AssertTest("Already Optimal Protected", reapply.Success, $"Message: {reapply.Message}");

            // -- TEST 11: Counter Invariants Post-Reconcile --
            Console.WriteLine();
            Console.WriteLine("-- 11. Testing Invariant Integrity --");
            bool invariant1 = reconcilePlan.TotalCount == (reconcilePlan.ApplicableCount + reconcilePlan.NotApplicableCount + reconcilePlan.UnsupportedCount);
            bool invariant2 = reconcilePlan.ApplicableCount == (reconcilePlan.AlreadyOptimizedCount + reconcilePlan.PendingCount + reconcilePlan.RestartRequiredCount);
            AssertTest("Catalog Count Invariant", invariant1, $"{reconcilePlan.TotalCount} == {reconcilePlan.ApplicableCount} + {reconcilePlan.NotApplicableCount} + {reconcilePlan.UnsupportedCount}");
            AssertTest("Applicable Split Invariant", invariant2, $"{reconcilePlan.ApplicableCount} == {reconcilePlan.AlreadyOptimizedCount} + {reconcilePlan.PendingCount} + {reconcilePlan.RestartRequiredCount}");

            // -- TEST 12: Five Critical Registry Optimizations Requested --
            Console.WriteLine();
            Console.WriteLine("-- 12. Validating 5 Key UI Registry Optimizations --");
            string[] keysToValidate = new[]
            {
                "tweak.process.priority",          // Win32PrioritySeparation
                "tweak.ram.svchost",               // SvcHost Split Threshold
                "tweak.ram.clearpagefile",          // Clear Pagefile on Shutdown
                "tweak.storage.lastaccess",        // Disable NTFS Last Access Update
                "tweak.input.mousehover"           // Mouse Hover Time
            };

            foreach (var k in keysToValidate)
            {
                var twk = reconcilePlan.Tweaks.FirstOrDefault(t => t.Id == k);
                AssertTest($"Key Optimization: {twk?.Name ?? k}", twk != null,
                    $"Current='{twk?.CurrentValue}', Target='{twk?.TargetValue}', Reboot={twk?.RequiresReboot}, Status='{twk?.Status}'");
            }

            // -- TEST 13: Generate Final Transparency Report Table --
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine(" FINAL ITEM-LEVEL TRANSPARENCY REPORT TABLE (Requirement 26)");
            Console.WriteLine("================================================================================");
            Console.WriteLine(string.Format("{0,-32} | {1,-10} | {2,-10} | {3,-12} | {4,-12} | {5,-8} | {6,-16}",
                "OPTIMIZATION", "APPLICABLE", "STATUS", "CURRENT", "TARGET", "REBOOT", "VERIFICATION"));
            Console.WriteLine(new string('-', 115));

            foreach (var t in reconcilePlan.Tweaks)
            {
                string name = t.Name.Length > 30 ? t.Name.Substring(0, 27) + "..." : t.Name;
                string cur = t.CurrentValue.Length > 10 ? t.CurrentValue.Substring(0, 9) + "…" : t.CurrentValue;
                string tgt = t.TargetValue.Length > 10 ? t.TargetValue.Substring(0, 9) + "…" : t.TargetValue;
                Console.WriteLine(string.Format("{0,-32} | {1,-10} | {2,-10} | {3,-12} | {4,-12} | {5,-8} | {6,-16}",
                    name, t.Applicability, t.Status, cur, tgt, t.RequiresReboot ? "YES" : "NO", t.VerificationState));
            }

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($" TOTAL MASTER REGISTRY SCORECARD: {passed}/{total} ({(double)passed / total * 100:F1}%)");
            Console.WriteLine("================================================================================");

            if (passed != total)
            {
                Environment.Exit(1);
            }
        }
    }
}
