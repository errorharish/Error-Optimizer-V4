using System;
using System.IO;
using System.Linq;
using BiosOptimizer.Core.V4.Rollback;
using Microsoft.Win32;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4RollbackTests : IDisposable
    {
        private readonly string _testSubKeyPath;

        public V4RollbackTests()
        {
            _testSubKeyPath = $@"Software\ErrorOptimizerTests\{Guid.NewGuid():N}";
        }

        public void Dispose()
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Software\ErrorOptimizerTests", writable: true);
                root?.DeleteSubKeyTree(_testSubKeyPath.Substring(_testSubKeyPath.LastIndexOf('\\') + 1), throwOnMissingSubKey: false);
            }
            catch { }
        }

        [Fact]
        public void RecordAndQueryTransactions_WorksSuccessfully()
        {
            var rollback = new V4RollbackEngine();
            string batchId = $"TEST-BATCH-{Guid.NewGuid():N}";

            var record = new V4TransactionRecord
            {
                BatchId = batchId,
                TargetType = "Service",
                TargetIdentifier = "TestDiagTrack",
                PreviousState = "Automatic",
                NewState = "Disabled",
                Reason = "Test telemetry service disabling"
            };

            rollback.RecordTransaction(record);

            var transactions = rollback.GetTransactions(batchId);
            Assert.NotEmpty(transactions);
            Assert.Contains(transactions, t => t.TargetIdentifier == "TestDiagTrack");
        }

        [Fact]
        public void RollbackRegistry_ExistingDWord_RestoredExact()
        {
            string valueName = "TestDword";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, 999, RegistryValueKind.DWord);
            }

            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, "42", "DWord", null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(RegistryValueKind.DWord, key.GetValueKind(valueName));
                Assert.Equal(42, (int)key.GetValue(valueName)!);
            }
        }

        [Fact]
        public void RollbackRegistry_PreviouslyMissingValue_Deleted()
        {
            string valueName = "TestMissingVal";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, 12345, RegistryValueKind.DWord);
            }

            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, "VALUE_DID_NOT_EXIST", null, null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Null(key.GetValue(valueName));
            }
        }

        [Fact]
        public void RollbackRegistry_EmptyBinaryValue_RestoredExact()
        {
            string valueName = "TestEmptyBin";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            }

            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, "EMPTY", "Binary", null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(RegistryValueKind.Binary, key.GetValueKind(valueName));
                var bytes = (byte[])key.GetValue(valueName)!;
                Assert.Empty(bytes);
            }
        }

        [Fact]
        public void RollbackRegistry_BinaryValue_RestoredExact()
        {
            string valueName = "TestBinaryVal";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, new byte[] { 0xFF }, RegistryValueKind.Binary);
            }

            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, "02-00-00-00-11-22-33-44", "Binary", null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(RegistryValueKind.Binary, key.GetValueKind(valueName));
                var bytes = (byte[])key.GetValue(valueName)!;
                Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44 }, bytes);
            }
        }

        [Fact]
        public void RollbackRegistry_LargeQwordValue_RestoredExact()
        {
            string valueName = "TestLargeQword";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, 1L, RegistryValueKind.QWord);
            }

            long expectedQword = 987654321012345678L;
            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, expectedQword.ToString(), "QWord", null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(RegistryValueKind.QWord, key.GetValueKind(valueName));
                Assert.Equal(expectedQword, (long)key.GetValue(valueName)!);
            }
        }

        [Fact]
        public void RollbackRegistry_MultiStringWithEmptyEntries_RestoredExact()
        {
            string valueName = "TestMultiStr";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, new[] { "Initial" }, RegistryValueKind.MultiString);
            }

            string multiPayload = "ItemA||||ItemB||ItemC";
            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, multiPayload, "MultiString", null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(RegistryValueKind.MultiString, key.GetValueKind(valueName));
                var strings = (string[])key.GetValue(valueName)!;
                Assert.Equal(new[] { "ItemA", "", "ItemB", "ItemC" }, strings);
            }
        }

        [Fact]
        public void RollbackRegistry_ExpandString_RestoredWithoutUnintendedExpansion()
        {
            string valueName = "TestExpandStr";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            using (var key = Registry.CurrentUser.CreateSubKey(_testSubKeyPath, true))
            {
                key.SetValue(valueName, "Overwritten", RegistryValueKind.String);
            }

            string expandPayload = @"%SystemRoot%\System32\Drivers";
            bool ok = V4RollbackEngine.RollbackRegistry(targetKey, expandPayload, "ExpandString", null, out string err);
            Assert.True(ok, $"Rollback failed: {err}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind(valueName));
                var rawValue = (string)key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
                Assert.Equal(expandPayload, rawValue);
            }
        }

        [Fact]
        public void RollbackRegistry_RepeatedRollback_IsIdempotent()
        {
            string valueName = "TestIdempotent";
            string targetKey = $@"HKCU\{_testSubKeyPath}\{valueName}";

            // Run 1: Missing value
            bool ok1 = V4RollbackEngine.RollbackRegistry(targetKey, "VALUE_DID_NOT_EXIST", null, null, out string err1);
            Assert.True(ok1, $"Run 1 failed: {err1}");

            // Run 2: Missing value again
            bool ok2 = V4RollbackEngine.RollbackRegistry(targetKey, "VALUE_DID_NOT_EXIST", null, null, out string err2);
            Assert.True(ok2, $"Run 2 failed: {err2}");

            // Run 3: Set value
            bool ok3 = V4RollbackEngine.RollbackRegistry(targetKey, "1234", "DWord", null, out string err3);
            Assert.True(ok3, $"Run 3 failed: {err3}");

            // Run 4: Set value again
            bool ok4 = V4RollbackEngine.RollbackRegistry(targetKey, "1234", "DWord", null, out string err4);
            Assert.True(ok4, $"Run 4 failed: {err4}");

            using (var key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, false))
            {
                Assert.NotNull(key);
                Assert.Equal(1234, (int)key.GetValue(valueName)!);
            }
        }

        [Fact]
        public void RollbackRegistry_CorruptOrMalformedTargetKey_FailsLoudly()
        {
            bool ok1 = V4RollbackEngine.RollbackRegistry("", "123", "DWord", null, out string err1);
            Assert.False(ok1);
            Assert.Contains("null or empty", err1, StringComparison.OrdinalIgnoreCase);

            bool ok2 = V4RollbackEngine.RollbackRegistry("INVALID_HIVE\\Some\\Path\\Val", "123", "DWord", null, out string err2);
            Assert.False(ok2);
            Assert.Contains("Unsupported hive", err2, StringComparison.OrdinalIgnoreCase);

            bool ok3 = V4RollbackEngine.RollbackRegistry(@"HKLM\OnlySubKeyWithoutDelimiter", "123", "DWord", null, out string err3);
            Assert.False(ok3);
            Assert.Contains("Malformed", err3, StringComparison.OrdinalIgnoreCase);
        }
    }
}
