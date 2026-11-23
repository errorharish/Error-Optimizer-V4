#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Implementations.Memory;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Workloads;

namespace BiosOptimizer.Core.V4.Intelligence
{
    public enum MemoryPressureSeverity
    {
        OptimalBaseline,       // Ample RAM and commit headroom; standby is actively accelerating disk cache
        ModeratePressure,      // RAM load 75-85%, commit load 75-85%, some headroom
        CriticalExhaustion     // Available physical < 1.5GB (or < 2.5GB on 16GB+) OR commit load > 88%
    }

    public class MemoryPressureDiagnosticsDto
    {
        public double TotalPhysicalGb { get; set; }
        public double AvailablePhysicalGb { get; set; }
        public double UsedPhysicalGb { get; set; }
        public double PhysicalLoadPercent { get; set; }
        public double CommitLimitGb { get; set; }
        public double CommitUsedGb { get; set; }
        public double CommitLoadPercent { get; set; }
        public double CommitHeadroomGb { get; set; }
        public double StandbyCacheMb { get; set; }
        public double LowPriorityStandbyMb { get; set; }
        public double ModifiedMb { get; set; }
        public double MemoryCompressionWorkingSetMb { get; set; }
        public bool IsActiveWorkloadProtected { get; set; }
        public string ActiveWorkloadName { get; set; } = string.Empty;
        public MemoryPressureSeverity Severity { get; set; } = MemoryPressureSeverity.OptimalBaseline;
        public string DiagnosticSummary { get; set; } = string.Empty;
        public bool IsSafeToTrimOrPurge { get; set; }
    }

    /// <summary>
    /// Intelligently diagnoses memory and commit pressure, replacing naive fixed-threshold purges.
    /// Strictly protects active games, emulators, and creative rendering suites from soft page faults.
    /// Distinguishes permanent RAM reclamation from transient cache state transitions.
    /// </summary>
    public class MemoryPressureIntelligenceEngine
    {
        private static readonly Lazy<MemoryPressureIntelligenceEngine> _instance =
            new(() => new MemoryPressureIntelligenceEngine());
        public static MemoryPressureIntelligenceEngine Instance => _instance.Value;

        private readonly WorkloadDetectionEngine _workloadEngine;
        private readonly V4RollbackEngine _rollbackEngine;

        public MemoryPressureIntelligenceEngine(
            WorkloadDetectionEngine? workloadEngine = null,
            V4RollbackEngine? rollbackEngine = null)
        {
            _workloadEngine = workloadEngine ?? new WorkloadDetectionEngine();
            _rollbackEngine = rollbackEngine ?? new V4RollbackEngine();
        }

        public MemoryPressureDiagnosticsDto SampleMemoryState()
        {
            var dto = new MemoryPressureDiagnosticsDto();
            try
            {
                var snap = HardwareDetectionService.Instance.GetSnapshot();
                var mem = snap.Memory;

                dto.TotalPhysicalGb = Math.Round(mem.InstalledPhysicalGb > 0 ? mem.InstalledPhysicalGb : mem.UsablePhysicalGb, 2);
                dto.AvailablePhysicalGb = Math.Round(mem.AvailablePhysicalGb, 2);
                dto.UsedPhysicalGb = Math.Round(mem.UsedPhysicalGb, 2);
                dto.PhysicalLoadPercent = Math.Round(mem.LoadPercentage, 1);
                dto.CommitLimitGb = Math.Round(mem.CommitLimitGb, 2);
                dto.CommitUsedGb = Math.Round(mem.CommitUsedGb, 2);
                dto.CommitLoadPercent = Math.Round(mem.CommitPercentage, 1);
                dto.CommitHeadroomGb = Math.Round(Math.Max(0, mem.CommitLimitGb - mem.CommitUsedGb), 2);

                // Sample Kernel Memory List telemetry if available
                var memState = WindowsMemoryTelemetryProvider.Instance.SampleCurrentMemoryState();
                dto.StandbyCacheMb = Math.Round(memState.StandbyBytes / (1024.0 * 1024.0), 1);
                dto.LowPriorityStandbyMb = Math.Round(memState.LowPriorityStandbyBytes / (1024.0 * 1024.0), 1);
                dto.ModifiedMb = Math.Round(memState.ModifiedBytes / (1024.0 * 1024.0), 1);

                // Sample Memory Compression working set
                try
                {
                    var compProcs = Process.GetProcessesByName("Memory Compression");
                    if (compProcs.Length > 0)
                    {
                        dto.MemoryCompressionWorkingSetMb = Math.Round(compProcs[0].WorkingSet64 / (1024.0 * 1024.0), 1);
                    }
                }
                catch { }

                // Check active workload context (gaming, emulator, creative rendering)
                var runningProcs = Process.GetProcesses();
                var workload = _workloadEngine.DetectActiveWorkloads(runningProcs);

                if (workload.IsGamingDetected || workload.IsEmulatorDetected || workload.IsEditingDetected)
                {
                    dto.IsActiveWorkloadProtected = true;
                    dto.ActiveWorkloadName = string.Join(", ", workload.ActiveWorkloadNames);
                }

                // Diagnose Severity
                bool physicalConstrained = (dto.TotalPhysicalGb <= 8.5 && dto.AvailablePhysicalGb < 1.5) ||
                                           (dto.TotalPhysicalGb > 8.5 && dto.AvailablePhysicalGb < 2.5) ||
                                           dto.PhysicalLoadPercent > 85.0;

                bool commitConstrained = dto.CommitLoadPercent > 88.0 || dto.CommitHeadroomGb < 1.2;

                if (physicalConstrained || commitConstrained)
                {
                    dto.Severity = MemoryPressureSeverity.CriticalExhaustion;
                    dto.DiagnosticSummary = commitConstrained
                        ? $"Commit Pressure Critical ({dto.CommitLoadPercent:F0}% commit load, {dto.CommitHeadroomGb:F1} GB headroom remaining)"
                        : $"Physical RAM Exhaustion ({dto.PhysicalLoadPercent:F0}% load, {dto.AvailablePhysicalGb:F1} GB free)";
                    
                    // Only safe to trim or purge if no active game/emulator/renderer is currently executing
                    dto.IsSafeToTrimOrPurge = !dto.IsActiveWorkloadProtected;
                }
                else if (dto.PhysicalLoadPercent >= 75.0 || dto.CommitLoadPercent >= 78.0)
                {
                    dto.Severity = MemoryPressureSeverity.ModeratePressure;
                    dto.DiagnosticSummary = $"Moderate Memory Utilization ({dto.PhysicalLoadPercent:F0}% physical load, {dto.CommitLoadPercent:F0}% commit load)";
                    dto.IsSafeToTrimOrPurge = false; // Suppress purges during moderate load to prevent cache churn
                }
                else
                {
                    dto.Severity = MemoryPressureSeverity.OptimalBaseline;
                    dto.DiagnosticSummary = $"Optimal Memory Baseline ({dto.PhysicalLoadPercent:F0}% load, {dto.AvailablePhysicalGb:F1} GB free, {dto.CommitHeadroomGb:F1} GB commit headroom)";
                    dto.IsSafeToTrimOrPurge = false;
                }
            }
            catch (Exception ex)
            {
                dto.DiagnosticSummary = $"Memory telemetry diagnostic unavailable: {ex.Message}";
            }

            return dto;
        }

        public bool ExecuteApplicationAwareMemoryRefresh(
            string batchId,
            string workloadProfile,
            out string outcomeReason,
            out int trimmedCount,
            out long reclaimedBytes)
        {
            trimmedCount = 0;
            reclaimedBytes = 0;

            var diag = SampleMemoryState();

            // 1. If an active gaming, emulator, or creative rendering workload is running:
            // Standby memory purging or working set trimming is STRICTLY SUPPRESSED.
            // Purging standby cache evicts buffered game assets/textures, causing severe soft page fault spikes.
            if (diag.IsActiveWorkloadProtected)
            {
                outcomeReason = $"Active workload protected ({diag.ActiveWorkloadName}): Standby cache is actively buffering assets; trimming suppressed to prevent soft page fault micro-stutters.";
                return true; // Honest ALREADY_OPTIMAL
            }

            // 2. If memory headroom is healthy and operating at optimal baseline:
            // No action needed. Standby list is accelerating Windows filesystem caching.
            if (diag.Severity == MemoryPressureSeverity.OptimalBaseline)
            {
                outcomeReason = $"Memory operates at verified optimal baseline ({diag.PhysicalLoadPercent:F0}% load, {diag.AvailablePhysicalGb:F1} GB free, {diag.CommitHeadroomGb:F1} GB commit headroom). Standby cache accelerates disk caching; purging suppressed to avoid cache churn.";
                return true; // Honest ALREADY_OPTIMAL
            }

            // 3. If memory load is moderate:
            if (diag.Severity == MemoryPressureSeverity.ModeratePressure)
            {
                outcomeReason = $"Memory utilization is moderate ({diag.PhysicalLoadPercent:F0}% load). Standby list and process working sets are balanced; aggressive trimming is unnecessary.";
                return true; // Honest ALREADY_OPTIMAL
            }

            // 4. Under genuine critical memory exhaustion without active workload:
            // Safely execute targeted, application-aware actions.
            try
            {
                // A. Reclaim low-priority standby pages (priorities 0-2 only)
                var memProvider = WindowsMemoryListProvider.Instance;
                if (memProvider.IsLowPriorityStandbySupported)
                {
                    var purgeResult = memProvider.ExecuteStandbyPurgeAsync(lowPriorityOnly: true).GetAwaiter().GetResult();
                    reclaimedBytes += purgeResult.StandbyReclaimedBytes;
                }

                // B. Converge orphaned, windowless background update daemons
                TrimOrphanedBackgroundUpdateDaemons(batchId, out trimmedCount, out long daemonBytes);
                reclaimedBytes += daemonBytes;

                _rollbackEngine.RecordTransaction(new V4TransactionRecord
                {
                    BatchId = batchId,
                    TargetType = "MemoryPressureRefresh",
                    TargetIdentifier = "StandbyCache:LowPriorityPurge",
                    PreviousState = $"{diag.PhysicalLoadPercent:F0}% Load ({diag.AvailablePhysicalGb:F1} GB Free)",
                    NewState = "Reclaimed Low-Priority Standby Pages",
                    Reason = $"Intelligent Memory Pressure Relief applied under critical memory load ({diag.DiagnosticSummary}); pages transition to free list without touching active workloads."
                });

                outcomeReason = $"Relieved critical memory pressure: Reclaimed {reclaimedBytes / (1024.0 * 1024.0):F1} MB low-priority standby cache; converged {trimmedCount} orphaned update daemon working sets.";
                return true;
            }
            catch (Exception ex)
            {
                outcomeReason = $"Memory pressure relief encountered an error: {ex.Message}";
                return false;
            }
        }

        private void TrimOrphanedBackgroundUpdateDaemons(string batchId, out int trimmedCount, out long reclaimedBytes)
        {
            trimmedCount = 0;
            reclaimedBytes = 0;

            // Strictly restricted to unneeded background update daemons.
            // NEVER touches Discord, Teams, Spotify, browsers with open windows, or active user apps.
            string[] safeOrphanedDaemons = { "GoogleUpdate", "MicrosoftEdgeUpdate", "DropboxUpdate", "AdobeUpdateService" };

            try
            {
                foreach (var name in safeOrphanedDaemons)
                {
                    var procs = Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        try
                        {
                            // Verify the process has no visible main window and is not system process
                            if (p.MainWindowHandle == IntPtr.Zero && p.Id > 4)
                            {
                                long ws = p.WorkingSet64;
                                if (ws > 40 * 1024 * 1024) // > 40 MB
                                {
                                    EmptyWorkingSet(p.Handle);
                                    trimmedCount++;
                                    reclaimedBytes += Math.Max(0, ws - p.WorkingSet64);

                                    _rollbackEngine.RecordTransaction(new V4TransactionRecord
                                    {
                                        BatchId = batchId,
                                        TargetType = "WorkingSetTrim",
                                        TargetIdentifier = $"{p.ProcessName} (PID {p.Id})",
                                        PreviousState = $"{ws / (1024 * 1024)} MB",
                                        NewState = "Trimmed",
                                        Reason = "Trimmed orphaned background update daemon under critical memory pressure (pages fault back dynamically on demand)"
                                    });
                                }
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch { }
        }

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
    }
}
