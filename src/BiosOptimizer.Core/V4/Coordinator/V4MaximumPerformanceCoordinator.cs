#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Metrics;
using BiosOptimizer.Core.V4.Persistent;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Coordinator
{
    /// <summary>
    /// Unified Facade and Coordinator for all Error Optimizer V4 Maximum Performance subsystems.
    /// Supports Gaming, Video Editing, and Streaming workloads with non-bypassable peripheral and hardware protection.
    /// </summary>
    public class V4MaximumPerformanceCoordinator
    {
        private static readonly Lazy<V4MaximumPerformanceCoordinator> _instance = new(() => new V4MaximumPerformanceCoordinator());
        public static V4MaximumPerformanceCoordinator Instance => _instance.Value;

        public SafetyKernel SafetyKernel { get; }
        public ProcessIntelligenceEngine ProcessIntelligence { get; }
        public WorkloadDetectionEngine WorkloadEngine { get; }
        public ProtectedProcessPolicy SafetyPolicy { get; }
        public ProcessIdentitySafety IdentitySafety { get; }
        public MaximumPerformancePlanner Planner { get; }
        public MaximumPerformanceExecutor Executor { get; }
        public OptimizationBenchmarkEngine BenchmarkEngine { get; }
        public V4RollbackEngine RollbackEngine { get; }
        public ProcessTargetBandEngine TargetBandEngine { get; }
        public WorkloadProcessGuardEngine WorkloadGuardEngine { get; }
        public OptimizedBaselineStore BaselineStore { get; }
        public OemBackgroundIntelligenceEngine OemIntelligence { get; }

        public V4MaximumPerformanceCoordinator()
        {
            SafetyPolicy = new ProtectedProcessPolicy();
            IdentitySafety = new ProcessIdentitySafety();
            SafetyKernel = new SafetyKernel(SafetyPolicy, IdentitySafety);
            WorkloadEngine = new WorkloadDetectionEngine();
            ProcessIntelligence = new ProcessIntelligenceEngine(SafetyPolicy, WorkloadEngine);
            BenchmarkEngine = new OptimizationBenchmarkEngine();
            RollbackEngine = new V4RollbackEngine();
            TargetBandEngine = new ProcessTargetBandEngine();
            WorkloadGuardEngine = new WorkloadProcessGuardEngine(WorkloadEngine, ProcessIntelligence, SafetyPolicy);
            BaselineStore = new OptimizedBaselineStore();
            OemIntelligence = OemBackgroundIntelligenceEngine.Instance;

            Planner = new MaximumPerformancePlanner(
                processIntelligence: ProcessIntelligence,
                workloadEngine: WorkloadEngine,
                safetyPolicy: SafetyPolicy,
                targetBandEngine: TargetBandEngine,
                safetyKernel: SafetyKernel,
                oemIntelligence: OemIntelligence);

            Executor = new MaximumPerformanceExecutor(
                planner: Planner,
                processIntelligence: ProcessIntelligence,
                identitySafety: IdentitySafety,
                safetyKernel: SafetyKernel,
                benchmarkEngine: BenchmarkEngine,
                rollbackEngine: RollbackEngine,
                workloadEngine: WorkloadEngine,
                targetBandEngine: TargetBandEngine,
                workloadGuardEngine: WorkloadGuardEngine,
                baselineStore: BaselineStore);
        }

        public async Task<ProcessSnapshotDto> ScanProcessesAsync()
        {
            return await ProcessIntelligence.CaptureSnapshotAsync();
        }

        public async Task<OptimizationPlanDto> GeneratePlanAsync(int pass = 1, string workloadProfile = "Gaming")
        {
            return await Planner.GeneratePlanAsync(pass, workloadProfile);
        }

        public async Task<OptimizationResultDto> ExecuteFullOptimizationAsync(
            int maxPasses = 4,
            string workloadProfile = "Gaming",
            bool isCustomSelection = false,
            List<string>? selectedCandidateIds = null,
            CancellationToken ct = default)
        {
            return await Executor.ExecuteAdaptiveOptimizationAsync(maxPasses, workloadProfile, isCustomSelection, selectedCandidateIds, ct);
        }

        public async Task<WorkloadGuardTelemetryDto> GetWorkloadGuardTelemetryAsync()
        {
            return await WorkloadGuardEngine.AnalyzeWorkloadStateAsync();
        }

        public async Task<int> SuppressWorkloadDriftAsync()
        {
            return await WorkloadGuardEngine.SuppressBackgroundDriftAsync();
        }

        public OptimizationMetricsDto GetLiveMetrics()
        {
            var metrics = BenchmarkEngine.SampleCurrentMetrics();
            var runningProcs = System.Diagnostics.Process.GetProcesses();
            var workload = WorkloadEngine.DetectActiveWorkloads(runningProcs);

            metrics.IsGamingWorkloadActive = workload.IsGamingDetected;
            metrics.IsEditingWorkloadActive = workload.IsEditingDetected;
            metrics.ActiveWorkloadSummary = string.Join(", ", workload.ActiveWorkloadNames);

            // Populate target band telemetry
            metrics.TargetBand = TargetBandEngine.CalculateTargetBand(metrics.CurrentProcessCount);
            metrics.SafeMinimumEstimateLow = metrics.TargetBand.TargetFootprintFloor;
            metrics.SafeMinimumEstimateHigh = metrics.TargetBand.TargetFootprintCeiling;

            return metrics;
        }

        public bool RollbackOptimization(string batchId, out int restored, out int failed, out string msg)
        {
            return RollbackEngine.RollbackBatch(batchId, out restored, out failed, out msg);
        }

        public async Task<PersistedBaselineDto?> LoadBaselineAsync()
        {
            return await BaselineStore.LoadBaselineAsync();
        }

        public async Task<(bool needed, string reason)> CheckIncrementalScanNeededAsync()
        {
            var snapshot = await ScanProcessesAsync();
            bool needed = BaselineStore.IsIncrementalScanNeeded(snapshot, out string reason);
            return (needed, reason);
        }

        /// <summary>
        /// Executes a full forensic diagnostic run with process accounting and detailed trace logging.
        /// </summary>
        public async Task<OptimizationResultDto> RunForensicDiagnosticAsync(CancellationToken ct = default)
        {
            return await Executor.ExecuteAdaptiveOptimizationAsync(maxPasses: 4, "Gaming", false, null, ct);
        }
    }

    /// <summary>
    /// Canonical Core Optimization Coordinator for ERROR OPTIMIZER — VERSION V4.
    /// </summary>
    public class CoreOptimizationCoordinator : V4MaximumPerformanceCoordinator
    {
        private static readonly Lazy<CoreOptimizationCoordinator> _coreInstance = new(() => new CoreOptimizationCoordinator());
        public static new CoreOptimizationCoordinator Instance => _coreInstance.Value;
    }
}
