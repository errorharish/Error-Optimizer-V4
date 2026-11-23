using System;
using System.IO;
using System.Linq;
using BiosOptimizer.Core.V4.Rollback;
using Microsoft.Win32;

namespace CrashFaultInjectionHarness
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                int checkpoint = 0;
                string subKey = string.Empty;
                string valName = "TestVal";
                string storageDir = string.Empty;
                string batchId = $"CRASH-BATCH-{Guid.NewGuid():N}";
                string baselineVal = "100";
                string baselineKind = "DWord";
                string targetVal = "200";
                bool recover = false;

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--checkpoint" && i + 1 < args.Length) int.TryParse(args[++i], out checkpoint);
                    else if (args[i] == "--sub-key" && i + 1 < args.Length) subKey = args[++i];
                    else if (args[i] == "--val-name" && i + 1 < args.Length) valName = args[++i];
                    else if (args[i] == "--storage-dir" && i + 1 < args.Length) storageDir = args[++i];
                    else if (args[i] == "--batch-id" && i + 1 < args.Length) batchId = args[++i];
                    else if (args[i] == "--baseline-val" && i + 1 < args.Length) baselineVal = args[++i];
                    else if (args[i] == "--baseline-kind" && i + 1 < args.Length) baselineKind = args[++i];
                    else if (args[i] == "--target-val" && i + 1 < args.Length) targetVal = args[++i];
                    else if (args[i] == "--recover") recover = true;
                }

                if (string.IsNullOrEmpty(storageDir))
                {
                    storageDir = Path.Combine(Path.GetTempPath(), "EO_CrashHarness_Tx");
                }
                Directory.CreateDirectory(storageDir);

                int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                Console.WriteLine($"[HARNESS_DIAG] PID={currentPid}, Mode={(recover ? "RECOVER" : $"CHECKPOINT_{checkpoint}")}, Storage={storageDir}");
                Console.Out.Flush();

                var engine = new V4RollbackEngine(storageDir);

                if (recover)
                {
                    bool ok = engine.RecoverIncompleteTransactions(out int rec, out int fail);
                    Console.WriteLine($"RECOVERY_RESULT: OK={ok}, Recovered={rec}, Failed={fail}");
                    return ok ? 0 : 2;
                }

                string targetIdentifier = $@"HKCU\{subKey}\{valName}";

                switch (checkpoint)
                {
                    case 1:
                        // Boundary 1: Crash before baseline capture
                        Console.WriteLine("FAULT_INJECTION: Checkpoint 1 - Terminating before baseline capture");
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_1");
                        break;

                    case 2:
                        // Boundary 2: Crash after baseline capture, before durable persistence
                        using (var k = Registry.CurrentUser.OpenSubKey(subKey, false))
                        {
                            var captured = k?.GetValue(valName)?.ToString() ?? "VALUE_DID_NOT_EXIST";
                            Console.WriteLine($"Captured baseline: {captured}. Terminating before durable persistence");
                        }
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_2");
                        break;

                    case 3:
                        // Boundary 3: Crash after durable persistence, before mutation
                        engine.RecordTransaction(new V4TransactionRecord
                        {
                            BatchId = batchId,
                            TargetType = "Registry",
                            TargetIdentifier = targetIdentifier,
                            PreviousState = baselineVal,
                            NewState = targetVal,
                            ValueKind = baselineKind,
                            Reason = "CrashTest Checkpoint 3"
                        });
                        Console.WriteLine("Transaction persisted to disk. Terminating before mutation");
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_3");
                        break;

                    case 4:
                        // Boundary 4: Crash during mutation (partial / intermediate state)
                        engine.RecordTransaction(new V4TransactionRecord
                        {
                            BatchId = batchId,
                            TargetType = "Registry",
                            TargetIdentifier = targetIdentifier,
                            PreviousState = baselineVal,
                            NewState = targetVal,
                            ValueKind = baselineKind,
                            Reason = "CrashTest Checkpoint 4"
                        });
                        using (var k = Registry.CurrentUser.CreateSubKey(subKey, true))
                        {
                            // Partial mutation: write intermediate corrupt value, kill process immediately
                            k.SetValue(valName, 999999, RegistryValueKind.DWord);
                            k.Flush();
                        }
                        Console.WriteLine("Intermediate mutation written. Terminating during mutation");
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_4");
                        break;

                    case 5:
                        // Boundary 5: Crash after mutation, before readback verification
                        engine.RecordTransaction(new V4TransactionRecord
                        {
                            BatchId = batchId,
                            TargetType = "Registry",
                            TargetIdentifier = targetIdentifier,
                            PreviousState = baselineVal,
                            NewState = targetVal,
                            ValueKind = baselineKind,
                            Reason = "CrashTest Checkpoint 5"
                        });
                        using (var k = Registry.CurrentUser.CreateSubKey(subKey, true))
                        {
                            k.SetValue(valName, int.Parse(targetVal), RegistryValueKind.DWord);
                            k.Flush();
                        }
                        Console.WriteLine("Mutation completed. Terminating before readback verification");
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_5");
                        break;

                    case 6:
                        // Boundary 6: Crash after readback, before transaction completion
                        engine.RecordTransaction(new V4TransactionRecord
                        {
                            BatchId = batchId,
                            TargetType = "Registry",
                            TargetIdentifier = targetIdentifier,
                            PreviousState = baselineVal,
                            NewState = targetVal,
                            ValueKind = baselineKind,
                            Reason = "CrashTest Checkpoint 6"
                        });
                        using (var k = Registry.CurrentUser.CreateSubKey(subKey, true))
                        {
                            k.SetValue(valName, int.Parse(targetVal), RegistryValueKind.DWord);
                            k.Flush();
                            var readBack = k.GetValue(valName);
                            Console.WriteLine($"Readback verified: {readBack}. Terminating before completion state update");
                        }
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_6");
                        break;

                    case 7:
                        // Boundary 7: Crash during multi-entry rollback midway
                        // Create 3 keys and 3 transactions
                        for (int idx = 1; idx <= 3; idx++)
                        {
                            string itemValName = $"{valName}_{idx}";
                            string itemTargetId = $@"HKCU\{subKey}\{itemValName}";
                            using (var k = Registry.CurrentUser.CreateSubKey(subKey, true))
                            {
                                k.SetValue(itemValName, 500 + idx, RegistryValueKind.DWord);
                                k.Flush();
                            }
                            engine.RecordTransaction(new V4TransactionRecord
                            {
                                BatchId = batchId,
                                TargetType = "Registry",
                                TargetIdentifier = itemTargetId,
                                PreviousState = (100 + idx).ToString(),
                                NewState = (500 + idx).ToString(),
                                ValueKind = "DWord",
                                Reason = $"Multi-entry rollback test {idx}"
                            });
                        }
                        // Start rollback of item 1 manually
                        var txs = engine.GetTransactions(batchId);
                        var tx1 = txs.FirstOrDefault(t => t.TargetIdentifier.EndsWith("_1", StringComparison.OrdinalIgnoreCase)) ?? txs.FirstOrDefault();
                        if (tx1 != null)
                        {
                            V4RollbackEngine.Rollback(tx1);
                            tx1.Restored = true;
                            engine.RecordTransaction(tx1);
                        }
                        Console.WriteLine("Multi-entry rollback: Item 1 rolled back. Terminating before items 2 and 3");
                        TerminateProcessImmediately("FAULT_INJECTION_CHECKPOINT_7");
                        break;

                    case 8:
                        // Boundary 8: Abrupt application / service termination
                        engine.RecordTransaction(new V4TransactionRecord
                        {
                            BatchId = batchId,
                            TargetType = "Registry",
                            TargetIdentifier = targetIdentifier,
                            PreviousState = baselineVal,
                            NewState = targetVal,
                            ValueKind = baselineKind,
                            Reason = "Abrupt Service Termination Test"
                        });
                        using (var k = Registry.CurrentUser.CreateSubKey(subKey, true))
                        {
                            k.SetValue(valName, int.Parse(targetVal), RegistryValueKind.DWord);
                            k.Flush();
                        }
                        Console.WriteLine("Service killed abruptly (SIGKILL simulation)");
                        System.Diagnostics.Process.GetCurrentProcess().Kill();
                        break;

                    case 9:
                        // Checkpoint 9: Clean recovery execution
                        bool recOk = engine.RecoverIncompleteTransactions(out int rCount, out int fCount);
                        Console.WriteLine($"RECOVERY_AFTER_RESTART: OK={recOk}, Recovered={rCount}, Failed={fCount}");
                        return recOk ? 0 : 3;

                    default:
                        Console.WriteLine($"Unknown checkpoint {checkpoint}");
                        return 1;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in harness: {ex.Message}");
                return 99;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private static void TerminateProcessImmediately(string reason, uint exitCode = 0xC0000001)
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Console.WriteLine($"TERMINATING_IMMEDIATELY: PID={pid}, Reason={reason}, ExitCode=0x{exitCode:X8}");
            Console.Out.Flush();
            TerminateProcess(System.Diagnostics.Process.GetCurrentProcess().Handle, exitCode);
        }
    }
}
