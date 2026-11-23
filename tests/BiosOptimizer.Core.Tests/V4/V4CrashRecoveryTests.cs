using System;
using System.Diagnostics;
using System.IO;
using BiosOptimizer.Core.V4.Rollback;
using Microsoft.Win32;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4CrashRecoveryTests : IDisposable
    {
        private readonly string _testSubKeyPath;
        private readonly string _storageDir;
        private readonly string _harnessExePath;

        public V4CrashRecoveryTests()
        {
            _testSubKeyPath = $@"Software\ErrorOptimizerCrashTests\{Guid.NewGuid():N}";
            _storageDir = Path.Combine(Path.GetTempPath(), $"EO_CrashTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_storageDir);

            // Locate harness binary
            string baseDir = AppContext.BaseDirectory;
            string candidate1 = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\CrashFaultInjectionHarness\bin\Debug\net8.0-windows10.0.19041.0\CrashFaultInjectionHarness.exe"));
            string candidate2 = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\CrashFaultInjectionHarness\bin\Debug\net8.0-windows10.0.19041.0\CrashFaultInjectionHarness.dll"));

            if (File.Exists(candidate1))
            {
                _harnessExePath = candidate1;
            }
            else if (File.Exists(candidate2))
            {
                _harnessExePath = candidate2;
            }
            else
            {
                // Fallback search
                var files = Directory.GetFiles(Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..")), "CrashFaultInjectionHarness.exe", SearchOption.AllDirectories);
                _harnessExePath = files.Length > 0 ? files[0] : candidate1;
            }
        }

        public void Dispose()
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Software\ErrorOptimizerCrashTests", writable: true);
                root?.DeleteSubKeyTree(_testSubKeyPath.Substring(_testSubKeyPath.LastIndexOf('\\') + 1), throwOnMissingSubKey: false);
            }
            catch { }

            try
            {
                if (Directory.Exists(_storageDir))
                {
                    Directory.Delete(_storageDir, true);
                }
            }
            catch { }
        }

        private (int ExitCode, string Output, bool Crashed) RunHarness(string args)
        {
            ProcessStartInfo psi;
            if (_harnessExePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi = new ProcessStartInfo("dotnet", $"\"{_harnessExePath}\" {args}");
            }
            else
            {
                psi = new ProcessStartInfo(_harnessExePath, args);
            }

            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var p = Process.Start(psi);
            Assert.NotNull(p);
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            bool crashed = p.ExitCode != 0;
            return (p.ExitCode, stdout + "\n" + stderr, crashed);
        }

        [Fact]
        public void Boundary1_CrashBeforeBaselineCapture_SystemUntouched()
        {
            string valName = "Val1";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 1 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\"");
            Assert.True(res.Crashed, "Child process should terminate abruptly");

            // Verify system remains untouched at baseline
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary2_CrashAfterCapture_BeforeDurablePersistence_SystemUntouched()
        {
            string valName = "Val2";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 2 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\"");
            Assert.True(res.Crashed, "Child process should terminate abruptly");

            // Verify no transaction record was written and baseline remains intact
            Assert.Empty(Directory.GetFiles(_storageDir, "*.json"));
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary3_CrashAfterDurablePersistence_BeforeMutation_RecoversCleanly()
        {
            string valName = "Val3";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 3 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val 100 --target-val 200");
            Assert.True(res.Crashed, "Child process should terminate abruptly");

            // Transaction exists on disk
            Assert.NotEmpty(Directory.GetFiles(_storageDir, "*.json"));

            // Fresh recovery process runs
            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            // Verify baseline 100 is verified
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary4_CrashDuringMutation_IntermediateCorruptState_RestoredExact()
        {
            string valName = "Val4";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 4 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val 100 --target-val 200");
            Assert.True(res.Crashed, "Child process should terminate abruptly");

            // Verify intermediate corrupt value 999999 was written
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(999999, (int)k.GetValue(valName)!);
            }

            // Fresh recovery process executes recovery
            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            // Verify exact baseline 100 is restored
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary5_CrashAfterMutation_BeforeReadback_RestoredExact()
        {
            string valName = "Val5";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 5 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val 100 --target-val 200");
            Assert.True(res.Crashed);

            // Mutation was applied before crash
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(200, (int)k.GetValue(valName)!);
            }

            // Fresh recovery process restores baseline
            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary6_CrashAfterReadback_BeforeCompletionUpdate_RestoredExact()
        {
            string valName = "Val6";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 6 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val 100 --target-val 200");
            Assert.True(res.Crashed);

            // Fresh recovery process executes recovery
            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary7_CrashDuringMultiEntryRollback_ResumesAndCompletesAllRemaining()
        {
            string baseName = "MultiVal";
            var res = RunHarness($"--checkpoint 7 --sub-key \"{_testSubKeyPath}\" --val-name {baseName} --storage-dir \"{_storageDir}\"");
            Assert.True(res.Crashed);

            // Checkpoint 7 restored item 1, but items 2 and 3 remained mutated at 502 and 503
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(101, (int)k.GetValue($"{baseName}_1")!);
                Assert.Equal(502, (int)k.GetValue($"{baseName}_2")!);
                Assert.Equal(503, (int)k.GetValue($"{baseName}_3")!);
            }

            // Fresh recovery process resumes incomplete rollback
            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            // Verify ALL THREE items are cleanly restored to baselines
            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(101, (int)k.GetValue($"{baseName}_1")!);
                Assert.Equal(102, (int)k.GetValue($"{baseName}_2")!);
                Assert.Equal(103, (int)k.GetValue($"{baseName}_3")!);
            }
        }

        [Fact]
        public void Boundary8_AbruptServiceTermination_RecoversCleanly()
        {
            string valName = "Val8";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            var res = RunHarness($"--checkpoint 8 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val 100 --target-val 200");
            Assert.True(res.Crashed);

            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void Boundary9_RepeatedRecovery_IsIdempotent()
        {
            string valName = "Val9";
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.SetValue(valName, 100, RegistryValueKind.DWord);
            }

            RunHarness($"--checkpoint 5 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val 100 --target-val 200");

            // Recovery pass 1
            var rec1 = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, rec1.ExitCode);

            // Recovery pass 2 (idempotency check)
            var rec2 = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, rec2.ExitCode);

            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Equal(100, (int)k.GetValue(valName)!);
            }
        }

        [Fact]
        public void MissingValue_CrashRecovery_DeletesValueWithoutAssumedDefaults()
        {
            string valName = "MissingValCrash";
            // Ensure value does NOT exist at baseline
            using (var k = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                k.DeleteValue(valName, throwOnMissingValue: false);
            }

            // Child mutates value to 200 and crashes
            var res = RunHarness($"--checkpoint 5 --sub-key \"{_testSubKeyPath}\" --val-name {valName} --storage-dir \"{_storageDir}\" --baseline-val VALUE_DID_NOT_EXIST --target-val 200");
            Assert.True(res.Crashed);

            // Fresh recovery process restores (deletes) value
            var recRes = RunHarness($"--recover --storage-dir \"{_storageDir}\"");
            Assert.Equal(0, recRes.ExitCode);

            using (var k = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(k);
                Assert.Null(k.GetValue(valName));
            }
        }
    }
}
