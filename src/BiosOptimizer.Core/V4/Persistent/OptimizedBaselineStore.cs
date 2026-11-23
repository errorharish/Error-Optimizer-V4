#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Persistent
{
    /// <summary>
    /// Persisted baseline state record for Error Optimizer V4.
    /// Stores machine-specific baseline metrics, applied optimizations, and hardware profile.
    /// Supports incremental re-scanning after Windows updates, driver installations, or new software.
    /// </summary>
    public class PersistedBaselineDto
    {
        public DateTime OptimizedAt { get; set; } = DateTime.UtcNow;
        public string MachineName { get; set; } = Environment.MachineName;
        public string OsVersion { get; set; } = Environment.OSVersion.ToString();
        public int BaselineProcessCount { get; set; }
        public double BaselineRamUsageMb { get; set; }
        public double BaselineCpuPercent { get; set; }
        public int PersistentOptimizationsCount { get; set; }
        public string TransactionBatchId { get; set; } = string.Empty;
        public bool IsBaselineActive { get; set; } = true;
    }

    /// <summary>
    /// Manages loading, saving, and incremental drift evaluation of the machine's optimized baseline.
    /// </summary>
    public class OptimizedBaselineStore
    {
        private readonly string _storagePath;
        private PersistedBaselineDto? _cachedBaseline;

        public OptimizedBaselineStore(string? customPath = null)
        {
            if (!string.IsNullOrEmpty(customPath))
            {
                _storagePath = customPath;
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(localAppData, "ErrorOptimizer", "V4");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch { }
                _storagePath = Path.Combine(dir, "optimized_baseline.json");
            }
        }

        public async Task<bool> SaveBaselineAsync(OptimizationResultDto result)
        {
            try
            {
                var baseline = new PersistedBaselineDto
                {
                    OptimizedAt = DateTime.UtcNow,
                    MachineName = Environment.MachineName,
                    OsVersion = Environment.OSVersion.ToString(),
                    BaselineProcessCount = result.FinalProcessCount,
                    BaselineRamUsageMb = result.FinalRamUsageMb,
                    BaselineCpuPercent = result.FinalCpuPercent,
                    PersistentOptimizationsCount = result.PersistentOptimizationsCount,
                    TransactionBatchId = result.TransactionBatchId,
                    IsBaselineActive = true
                };

                string json = JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true });
                string? dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllTextAsync(_storagePath, json);
                _cachedBaseline = baseline;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<PersistedBaselineDto?> LoadBaselineAsync()
        {
            if (_cachedBaseline != null) return _cachedBaseline;

            try
            {
                if (!File.Exists(_storagePath)) return null;

                string json = await File.ReadAllTextAsync(_storagePath);
                _cachedBaseline = JsonSerializer.Deserialize<PersistedBaselineDto>(json);
                return _cachedBaseline;
            }
            catch
            {
                return null;
            }
        }

        public bool HasBaseline()
        {
            return _cachedBaseline != null || File.Exists(_storagePath);
        }

        /// <summary>
        /// Evaluates whether an incremental re-optimization scan is recommended due to OS updates,
        /// newly installed services/software, or significant background workload drift.
        /// </summary>
        public bool IsIncrementalScanNeeded(ProcessSnapshotDto currentSnapshot, out string changeReason)
        {
            changeReason = string.Empty;
            var baseline = _cachedBaseline;

            if (baseline == null && File.Exists(_storagePath))
            {
                try
                {
                    string json = File.ReadAllText(_storagePath);
                    baseline = JsonSerializer.Deserialize<PersistedBaselineDto>(json);
                    _cachedBaseline = baseline;
                }
                catch { }
            }

            if (baseline == null)
            {
                changeReason = "No previous optimized baseline found. Full machine optimization recommended.";
                return true;
            }

            // Check OS Version drift (e.g. major Windows 11 feature update)
            if (!string.Equals(baseline.OsVersion, Environment.OSVersion.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                changeReason = "Windows OS build change detected. Incremental optimization recommended.";
                return true;
            }

            // Check Process Count drift (>15 processes above baseline)
            int drift = currentSnapshot.TotalProcessCount - baseline.BaselineProcessCount;
            if (drift > 15)
            {
                changeReason = $"Background process drift detected (+{drift} processes above optimized baseline {baseline.BaselineProcessCount}). Incremental re-optimization recommended.";
                return true;
            }

            // Check reducible candidates
            if (currentSnapshot.ReducibleProcessCount > 0)
            {
                changeReason = $"{currentSnapshot.ReducibleProcessCount} newly spawned reducible background tasks detected.";
                return true;
            }

            changeReason = "System is operating cleanly at its optimized baseline.";
            return false;
        }

        public void ClearBaseline()
        {
            _cachedBaseline = null;
            try
            {
                if (File.Exists(_storagePath)) File.Delete(_storagePath);
            }
            catch { }
        }
    }
}
