#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Diagnostics
{
    public sealed class ClassificationDump
    {
        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public int InitialProcessCount { get; init; }
        public Dictionary<ProcessClassification, int> CountByClass { get; init; } = new();
        public int ClassifiedReducibleCount { get; init; }
        public IReadOnlyList<RejectionRecord> Rejections { get; init; } = Array.Empty<RejectionRecord>();
        public Dictionary<string, string> CandidateRejectionExplanations { get; init; } = new();
        public TierTrace TierTrace { get; init; } = new();
        public ExecutorLog Executor { get; init; } = new();
        public ProvenanceDiagnosticsDto? ProvenanceDiagnostics { get; init; }

        private static readonly object _fileLock = new();

        public static string SaveDump(ClassificationDump dump)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(localAppData, "ErrorOptimizer");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string timestamp = dump.CapturedAtUtc.ToString("yyyyMMddTHHmmssZ");
                string filePath = Path.Combine(dir, $"classification_dump_{timestamp}.json");

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };

                string json = JsonSerializer.Serialize(dump, options);

                lock (_fileLock)
                {
                    File.WriteAllText(filePath, json);

                    // Keep only the last 10 dumps
                    var dumpFiles = Directory.GetFiles(dir, "classification_dump_*.json")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.CreationTimeUtc)
                        .ToList();

                    if (dumpFiles.Count > 10)
                    {
                        foreach (var oldFile in dumpFiles.Skip(10))
                        {
                            try { oldFile.Delete(); } catch { }
                        }
                    }
                }

                return filePath;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public sealed class ExecutorLog
    {
        public bool Invoked { get; set; }
        public int CandidatesHandedIn { get; set; }
        public int TerminationAttempts { get; set; }
        public int VerifiedTerminations { get; set; }
        public int SafetyKernelDenials { get; set; }
    }
}
