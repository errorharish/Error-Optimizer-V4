#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BiosOptimizer.Core.Common;
using BiosOptimizer.Core.Services;
using BiosOptimizer.Core.V4.Rollback;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    [Collection("BackupStoreTests")]
    public class BackupStoreTests : IDisposable
    {
        private readonly string _testRoot;

        public BackupStoreTests()
        {
            _testRoot = Path.Combine(Path.GetTempPath(), $"backup_store_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testRoot);
            BackupStore.SetCustomRootForTesting(_testRoot);
            ToastService.ResetBackupDebounce();
            ToastService.OnToastShown = null;
        }

        public void Dispose()
        {
            BackupStore.SetCustomRootForTesting(null);
            ToastService.ResetBackupDebounce();
            ToastService.OnToastShown = null;
            if (Directory.Exists(_testRoot))
            {
                try { Directory.Delete(_testRoot, true); } catch { }
            }
        }

        [Fact]
        public void T_BS_1_RootResolvesUnderProjectFolder_InDevelopmentMode()
        {
            string devExeDir = @"D:\SOFTWARE PROJECTS\ERROR OPTIMIZER\dist\win-x64";
            string resolvedRoot = AppEnvironment.ResolveBackupRoot(AppMode.Development, devExeDir);
            Assert.Contains(@"backups", resolvedRoot, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"ERROR OPTIMIZER", resolvedRoot, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_BS_2_RootResolvesUnderProgramData_InInstalledMode()
        {
            string installedDir = @"C:\Program Files\Error Optimizer";
            string resolvedRoot = AppEnvironment.ResolveBackupRoot(AppMode.Installed, installedDir);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            Assert.StartsWith(progData, resolvedRoot, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"ErrorOptimizer\backups", resolvedRoot, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void T_BS_3_Create_WritesToCorrectSubfolder_AppendsToIndex()
        {
            string sourceFile = Path.Combine(_testRoot, "test_config.conf");
            File.WriteAllText(sourceFile, "bst.instance.Pie64.fps=120");

            var ctx = new BackupContext(
                Component: "emulator",
                SessionId: "session-123",
                EmulatorKind: "BlueStacks5",
                Target: "bluestacks.conf");

            var entry = BackupStore.Create(sourceFile, ctx);

            Assert.NotNull(entry);
            Assert.True(File.Exists(entry.BackupPath));
            Assert.Contains(@"emulator\BlueStacks5", entry.BackupPath);
            Assert.Equal("bluestacks.conf", entry.Target);
            Assert.NotEmpty(entry.Sha256);

            var list = BackupStore.List();
            Assert.Contains(list, e => e.Id == entry.Id);
        }

        [Fact]
        public void T_BS_4_List_ReturnsNewestFirst()
        {
            string file1 = Path.Combine(_testRoot, "f1.txt");
            string file2 = Path.Combine(_testRoot, "f2.txt");
            File.WriteAllText(file1, "content1");
            File.WriteAllText(file2, "content2");

            var e1 = BackupStore.Create(file1, new BackupContext("v4core", "session-1", Target: "t1"));
            System.Threading.Thread.Sleep(50);
            var e2 = BackupStore.Create(file2, new BackupContext("v4core", "session-1", Target: "t2"));

            var list = BackupStore.List();
            Assert.True(list.Count >= 2);
            Assert.Equal(e2.Id, list[0].Id);
            Assert.Equal(e1.Id, list[1].Id);
        }

        [Fact]
        public void T_BS_5_Prune_KeepsFiveNewestPerTarget()
        {
            string src = Path.Combine(_testRoot, "prune_source.txt");
            File.WriteAllText(src, "prune test data");

            // Mark session as completed so entries are eligible for pruning
            string sess = "prune-session";
            BackupStore.RecordSessionStart(sess);
            BackupStore.RecordSessionCompleted(sess);
            BackupStore.RecordSessionRollback(sess); // rollbackApplied=true means not protected unrolled session

            for (int i = 0; i < 8; i++)
            {
                File.WriteAllText(src, $"data_{i}");
                BackupStore.Create(src, new BackupContext("v4core", sess, Target: "prune_target"));
                System.Threading.Thread.Sleep(10);
            }

            var before = BackupStore.List("prune_target");
            Assert.Equal(8, before.Count);

            BackupStore.Prune("prune_target", keep: 5);

            var after = BackupStore.List("prune_target");
            Assert.Equal(5, after.Count);
        }

        [Fact]
        public void T_BS_6_Prune_SkipsRecentManifestReferencedBackups()
        {
            string src = Path.Combine(_testRoot, "active_session.txt");
            File.WriteAllText(src, "active session data");

            string activeSession = "active-unrolled-session";
            BackupStore.RecordSessionStart(activeSession);
            // Notice: session is < 24h old and rollbackApplied is false

            for (int i = 0; i < 7; i++)
            {
                File.WriteAllText(src, $"active_data_{i}");
                BackupStore.Create(src, new BackupContext("v4core", activeSession, Target: "protected_target"));
                System.Threading.Thread.Sleep(10);
            }

            // Prune attempt should protect active session backups
            BackupStore.Prune("protected_target", keep: 3);

            var after = BackupStore.List("protected_target");
            Assert.Equal(7, after.Count);
        }

        [Fact]
        public void T_BS_7_Restore_OverwritesSource_VerifiesSha256()
        {
            string src = Path.Combine(_testRoot, "restore_test.txt");
            string originalContent = "original pristine content";
            File.WriteAllText(src, originalContent);

            var entry = BackupStore.Create(src, new BackupContext("v4core", "session-restore", Target: "restore_target"));

            // Modify source
            File.WriteAllText(src, "corrupted/modified content");
            Assert.NotEqual(originalContent, File.ReadAllText(src));

            // Restore
            BackupStore.Restore(entry.Id);

            Assert.Equal(originalContent, File.ReadAllText(src));
        }

        [Fact]
        public void T_BS_8_CorruptIndexJson_RebuildByScanningTree()
        {
            string src = Path.Combine(_testRoot, "rebuild_test.txt");
            File.WriteAllText(src, "rebuild data");

            var entry = BackupStore.Create(src, new BackupContext("v4core", "session-rebuild", Target: "rebuild_target"));

            // Corrupt index.json
            string indexPath = Path.Combine(_testRoot, "index.json");
            File.WriteAllText(indexPath, "{ invalid corrupted json !!! }");

            // List should auto-detect corruption and rebuild index from disk tree
            var list = BackupStore.List();
            Assert.NotEmpty(list);
            Assert.Contains(list, e => e.BackupPath == entry.BackupPath);
        }

        [Fact]
        public void T_BS_9_ConcurrentCreateFromTwoThreads_BothIndexedCorrectly()
        {
            string fileA = Path.Combine(_testRoot, "concurrentA.txt");
            string fileB = Path.Combine(_testRoot, "concurrentB.txt");
            File.WriteAllText(fileA, "data A");
            File.WriteAllText(fileB, "data B");

            Parallel.Invoke(
                () => BackupStore.Create(fileA, new BackupContext("v4core", "concurrent-session", Target: "targetA")),
                () => BackupStore.Create(fileB, new BackupContext("v4core", "concurrent-session", Target: "targetB"))
            );

            var list = BackupStore.List();
            Assert.Contains(list, e => e.Target == "targetA");
            Assert.Contains(list, e => e.Target == "targetB");
        }

        [Fact]
        public void T_BS_10_CreateFiresToastServiceShowOnSuccess()
        {
            string? shownTitle = null;
            string? shownSub = null;
            ToastKind? shownKind = null;
            ToastService.OnToastShown = (t, s, k, d) =>
            {
                shownTitle = t;
                shownSub = s;
                shownKind = k;
            };

            string src = Path.Combine(_testRoot, "toast_success.conf");
            File.WriteAllText(src, "test=123");
            var entry = BackupStore.Create(src, new BackupContext("emulator", "session-toast", EmulatorKind: "MsiAppPlayer5", Target: "toast_success.conf"));

            Assert.NotNull(shownTitle);
            Assert.Contains("Backup created", shownTitle);
            Assert.NotNull(shownSub);
            Assert.Contains("MSI App Player 5", shownSub);
            Assert.Contains("toast_success.conf", shownSub);
            Assert.Equal(ToastKind.Success, shownKind);
        }

        [Fact]
        public void T_BS_11_CreateFiresErrorToastOnFailure()
        {
            string? shownTitle = null;
            string? shownSub = null;
            ToastKind? shownKind = null;
            TimeSpan? shownDuration = null;
            ToastService.OnToastShown = (t, s, k, d) =>
            {
                shownTitle = t;
                shownSub = s;
                shownKind = k;
                shownDuration = d;
            };

            string nonExistent = Path.Combine(_testRoot, "non_existent_file.conf");
            var ex = Assert.Throws<FileNotFoundException>(() =>
            {
                BackupStore.Create(nonExistent, new BackupContext("emulator", "session-toast", EmulatorKind: "MsiAppPlayer5", Target: "non_existent_file.conf"));
            });

            Assert.NotNull(shownTitle);
            Assert.Equal("Backup failed", shownTitle);
            Assert.Equal(ToastKind.Error, shownKind);
            Assert.Equal(TimeSpan.FromSeconds(8), shownDuration);
        }
    }
}
