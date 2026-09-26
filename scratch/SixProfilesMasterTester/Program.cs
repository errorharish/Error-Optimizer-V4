using System;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Profiles;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Power;

namespace SixProfilesMasterTester
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("===================================================================");
            Console.WriteLine("STARTING 6-PROFILE ADAPTIVE OPTIMIZATION ENGINE MASTER AUDIT");
            Console.WriteLine("===================================================================");

            try
            {
                RunAuditAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[MASTER AUDIT FATAL ERROR]: {ex}");
                Environment.Exit(1);
            }
        }

        static async Task RunAuditAsync()
        {
            await Task.Yield();
            var engine = MasterProfileEngine.Instance;
            var hw = HardwareProfiler.GetQuickProfile(forceRefresh: true);
            var wl = UserWorkloadDetector.Instance.ClassifyCurrentWorkload();

            Console.WriteLine($"\n[HARDWARE CONTEXT]");
            Console.WriteLine($"  CPU: {hw.CpuModel} ({hw.CpuLogicalCores} Threads)");
            Console.WriteLine($"  RAM: {hw.RamTotalGb:F1} GB ({hw.RamUsagePercent:F1}% Used)");
            Console.WriteLine($"  Storage: {hw.StorageType} ({hw.TotalFreeDiskSpaceGb:F1} GB Free)");
            Console.WriteLine($"  Power: {(hw.IsBatteryPowered ? "Battery Powered" : "AC Connected")}");
            Console.WriteLine($"  Workload: {wl.Category} (Primary: '{wl.PrimaryProcessName}', Confidence: {wl.ConfidenceScore}%)");

            string[] profileIds = new[]
            {
                "tier1_safe",        // 1. Normal
                "tier2_standard",    // 2. Pro
                "tier3_aggressive",  // 3. Ultimate
                "tier4_experimental",// 4. Debloat
                "tier5_extreme",     // 5. BIOS Safe
                "tier6_maximum"      // 6. Max Performance
            };

            foreach (var profId in profileIds)
            {
                var policy = ProfilePolicy.GetPolicyById(profId);
                Console.WriteLine($"\n-------------------------------------------------------------------");
                Console.WriteLine($"[TEST PROFILE: {policy.DisplayName.ToUpper()}] (Aggressiveness: {policy.Aggressiveness}/10)");
                Console.WriteLine($"  Allowed Categories: {string.Join(", ", policy.AllowedCategories)}");
                Console.WriteLine($"  Max Allowed Risks: {string.Join(", ", policy.MaxAllowedRisks)}");
                Console.WriteLine($"  Service Changes: {policy.AllowServiceChanges} | Power Plan Switching: {policy.AllowPowerPlanSwitching}");
                Console.WriteLine($"  Workload Adaptation: {policy.WorkloadAdaptationEnabled} | Machine Learning: {policy.MachineLearningEnabled}");

                var plan = engine.GeneratePlan(policy, hw, wl);
                Console.WriteLine($"  >> Total Candidates: {plan.TotalCandidates}");
                Console.WriteLine($"  >> Applicable: {plan.ApplicableCount}");
                Console.WriteLine($"  >> Already Optimal: {plan.AlreadyOptimalCount}");
                Console.WriteLine($"  >> Unsupported/Not Applicable: {plan.UnsupportedCount}");

                foreach (var item in plan.Items.Take(6))
                {
                    Console.WriteLine($"     * [{item.State}] '{item.Definition.Name}'");
                    Console.WriteLine($"       Category: {item.Definition.Category} | Priority: {item.Definition.Priority} | Risk: {item.Definition.RiskLevel}");
                    Console.WriteLine($"       Current: '{item.CurrentState}' -> Target: '{item.TargetState}'");
                    Console.WriteLine($"       Why: {item.ApplicabilityReason}");
                    Console.WriteLine($"       Learning History: {item.LearningHistory.SummaryText}");
                }

                if (plan.Items.Count == 0 && policy.AllowedCategories.Count > 0)
                {
                    throw new Exception($"Profile '{policy.DisplayName}' generated 0 plan items despite valid allowed categories.");
                }

                Console.WriteLine($"  [PASS] Profile '{policy.DisplayName}' plan generated dynamically and safely.");
            }

            // ----------------------------------------------------
            // TEST: Machine-Specific Learning Engine Persistence
            // ----------------------------------------------------
            Console.WriteLine("\n-------------------------------------------------------------------");
            Console.WriteLine("[TEST: MACHINE LEARNING & HISTORY RECORDING]");
            var learning = OptimizationLearningEngine.Instance;
            string testOptId = "INPUT.POINTER_SPEED_DEFAULT";
            var recordBefore = learning.GetRecord(testOptId);
            int attemptsBefore = recordBefore.Attempts;
            Console.WriteLine($"  Record Before: Attempts={attemptsBefore}, Successes={recordBefore.Successes}, Effectiveness={recordBefore.EffectivenessScore:F0}%");

            learning.RecordOutcome(testOptId, "tier2_standard", true, "10 / 20", "10 / 20");
            var recordAfter = learning.GetRecord(testOptId);
            Console.WriteLine($"  Record After: Attempts={recordAfter.Attempts}, Successes={recordAfter.Successes}, Effectiveness={recordAfter.EffectivenessScore:F0}%");

            if (recordAfter.Attempts != attemptsBefore + 1)
                throw new Exception($"Machine learning attempt count did not increment. Expected {attemptsBefore + 1}, got {recordAfter.Attempts}");
            Console.WriteLine("  [PASS] Machine-specific learning and history recording verified.");

            Console.WriteLine("\n===================================================================");
            Console.WriteLine("*** ALL 6 PROFILES MASTER ENGINE AUDIT COMPLETED SUCCESSFULLY! ***");
            Console.WriteLine("===================================================================");
        }
    }
}
