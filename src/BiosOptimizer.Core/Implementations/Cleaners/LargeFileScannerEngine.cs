using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;

namespace BiosOptimizer.Core.Implementations.Cleaners;

public class LargeFileScannerEngine : ICleanerEngine
{
    public string EngineId => "large-files";
    public string DisplayName => "Large File Scanner";

    public CleanupScanDto Scan()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string driveRoot = Path.GetPathRoot(userProfile) ?? "C:\\";
        var cleaner = new StorageCleanerEngine();
        const long minSizeBytes = 100L * 1024 * 1024; // 100 MB threshold
        var largeFiles = cleaner.FindLargeFiles(driveRoot, minSizeBytes, CancellationToken.None);

        var scan = new CleanupScanDto
        {
            Category = "Storage",
            IsSafeToClean = false, // Explicit user selection required for Large Files
            ScannedPaths = largeFiles.Select(f => f.FullPath).ToList(),
            FileCount = largeFiles.Count,
            TotalSizeInBytes = largeFiles.Sum(f => f.SizeBytes)
        };

        if (scan.ScannedPaths.Count == 0 && !string.IsNullOrWhiteSpace(userProfile))
        {
            scan.ScannedPaths.Add(userProfile);
        }

        return scan;
    }

    public long Clean()
    {
        // Never automatically delete large files.
        return 0;
    }
}
