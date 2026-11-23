using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.Implementations.Cleaners;

public class DuplicateFileScannerEngine : ICleanerEngine
{
    public string EngineId => "duplicate-files";
    public string DisplayName => "Duplicate File Scanner";

    public CleanupScanDto Scan()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var cleaner = new StorageCleanerEngine();
        var groups = cleaner.FindDuplicates(docs, 1024L, CancellationToken.None);

        var scan = new CleanupScanDto
        {
            Category = "Storage",
            IsSafeToClean = false, // Explicit user selection required
            ScannedPaths = groups.SelectMany(g => g.FilePaths).ToList(),
            FileCount = groups.Sum(g => Math.Max(0, g.FilePaths.Count - 1)),
            TotalSizeInBytes = groups.Sum(g => g.ReclaimableBytes)
        };

        if (scan.ScannedPaths.Count == 0 && !string.IsNullOrWhiteSpace(docs))
        {
            scan.ScannedPaths.Add(docs);
        }

        return scan;
    }

    public long Clean()
    {
        // Never automatically delete
        return 0;
    }
}
