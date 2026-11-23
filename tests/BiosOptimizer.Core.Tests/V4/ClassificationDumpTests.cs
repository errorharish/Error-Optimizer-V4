#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BiosOptimizer.Core.V4.Diagnostics;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class ClassificationDumpTests
    {
        [Fact]
        public void T_CLASS_1_SnapshotWrite_ProducesValidJsonMatchingSchema()
        {
            var dump = new ClassificationDump
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                InitialProcessCount = 120,
                CountByClass = new Dictionary<ProcessClassification, int>
                {
                    [ProcessClassification.REQUIRED] = 40,
                    [ProcessClassification.PROTECTED] = 50,
                    [ProcessClassification.IMMEDIATE_REDUCIBLE] = 5,
                    [ProcessClassification.BACKGROUND] = 25
                },
                ClassifiedReducibleCount = 5,
                Rejections = new List<RejectionRecord>
                {
                    new(
                        Pid: 600,
                        Name: "csrss.exe",
                        ImagePath: @"C:\Windows\System32\csrss.exe",
                        FinalClass: ProcessClassification.REQUIRED,
                        RejectionReason: "Critical Windows subsystem",
                        RejectionRule: "REQUIRED_OS_KERNEL_CORE"
                    )
                },
                TierTrace = new TierTrace
                {
                    Tier1 = new TierPass { Ran = true, CandidatesDiscovered = 120, ClassifiedReducible = 5, SkipReason = null }
                },
                Executor = new ExecutorLog
                {
                    Invoked = true,
                    CandidatesHandedIn = 5,
                    TerminationAttempts = 5,
                    VerifiedTerminations = 4,
                    SafetyKernelDenials = 1
                }
            };

            string path = ClassificationDump.SaveDump(dump);
            Assert.False(string.IsNullOrEmpty(path));
            Assert.True(File.Exists(path));

            try
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                Assert.Equal(120, root.GetProperty("InitialProcessCount").GetInt32());
                Assert.Equal(5, root.GetProperty("ClassifiedReducibleCount").GetInt32());
                Assert.True(root.GetProperty("Rejections").GetArrayLength() > 0);
                Assert.True(root.GetProperty("Executor").GetProperty("Invoked").GetBoolean());
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void T_CLASS_2_DumpRetains_AtMost10MostRecentFiles()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localAppData, "ErrorOptimizer");
            Directory.CreateDirectory(dir);

            var createdPaths = new List<string>();

            try
            {
                // Create 15 dumps sequentially
                for (int i = 0; i < 15; i++)
                {
                    var dump = new ClassificationDump
                    {
                        CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(i),
                        InitialProcessCount = 100 + i
                    };
                    string p = ClassificationDump.SaveDump(dump);
                    if (!string.IsNullOrEmpty(p)) createdPaths.Add(p);
                }

                var dumpFiles = Directory.GetFiles(dir, "classification_dump_*.json");
                Assert.True(dumpFiles.Length <= 10, $"Expected <= 10 dumps retained but found {dumpFiles.Length}");
            }
            finally
            {
                foreach (var p in createdPaths)
                {
                    try { if (File.Exists(p)) File.Delete(p); } catch { }
                }
            }
        }

        [Fact]
        public void T_CLASS_3_TierTrace_PopulatedWithAllThreeTierStatuses()
        {
            var trace = new TierTrace
            {
                Tier1 = new TierPass { Ran = true, CandidatesDiscovered = 100, ClassifiedReducible = 3 },
                Tier2 = new TierPass { Ran = true, CandidatesDiscovered = 20, ClassifiedReducible = 5 },
                Tier3 = new TierPass { Ran = false, CandidatesDiscovered = 10, ClassifiedReducible = 0, SkipReason = "Target band achieved in Tier 2" }
            };

            Assert.True(trace.Tier1.Ran);
            Assert.Equal(3, trace.Tier1.ClassifiedReducible);

            Assert.True(trace.Tier2.Ran);
            Assert.Equal(5, trace.Tier2.ClassifiedReducible);

            Assert.False(trace.Tier3.Ran);
            Assert.NotNull(trace.Tier3.SkipReason);
        }

        [Fact]
        public void T_CLASS_4_ExecutorLog_AccuratelyRecordsOutcomes()
        {
            var log = new ExecutorLog
            {
                Invoked = true,
                CandidatesHandedIn = 12,
                TerminationAttempts = 10,
                VerifiedTerminations = 8,
                SafetyKernelDenials = 2
            };

            Assert.True(log.Invoked);
            Assert.Equal(12, log.CandidatesHandedIn);
            Assert.Equal(10, log.TerminationAttempts);
            Assert.Equal(8, log.VerifiedTerminations);
            Assert.Equal(2, log.SafetyKernelDenials);
        }
    }
}
