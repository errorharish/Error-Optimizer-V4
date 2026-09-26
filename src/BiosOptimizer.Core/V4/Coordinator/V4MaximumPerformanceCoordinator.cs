#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Metrics;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Coordinator
{
    /// <summary>
    /// Unified Facade and Coordinator for all Error Optimizer V4 Maximum Performance subsystems.
    /// </summary>
    public class V4MaximumPerformanceCoordinator
    {
        private static readonly Lazy<V4MaximumPerformanceCoordinator> _instance = new(() => new V4MaximumPerformanceCoordinator());
        public static V4MaximumPerformanceCoordinator Instance => _instance.Value;

        public ProcessIntelligenceEngine ProcessIntelligence { get; }
        public WorkloadDetectionEngine WorkloadEngine { get; }
        public ProtectedProcessPolicy SafetyPolicy { get; }
        public ProcessIdentitySafety IdentitySafety { get; }
        public MaximumPerformancePlanner Planner { get; }
        public MaximumPerformanceExecutor Executor { get; }
        public OptimizationBenchmarkEngine BenchmarkEngine { get; }
        public V4RollbackEngine RollbackEngine { get; }

        public V4MaximumPerformanceCoordinator()
        {
            SafetyPolicy = new ProtectedProcessPolicy();
            IdentitySafety = new ProcessIdentitySafety();
            WorkloadEngine = new WorkloadDetectionEngine();
            ProcessIntelligence = new ProcessIntelligenceEngine(SafetyPolicy, WorkloadEngine);
            BenchmarkEngine = new OptimizationBenchmarkEngine();
            RollbackEngine = new V4RollbackEngine();
            Planner = new MaximumPerformancePlanner(
                processIntelligence: ProcessIntelligence,
                workloadEngine: WorkloadEngine,
                safetyPolicy: SafetyPolicy);
            Executor = new MaximumPerformanceExecutor(
                planner: Planner,
                processIntelligence: ProcessIntelligence,
                identitySafety: IdentitySafety,
                benchmarkEngine: BenchmarkEngine,
                rollbackEngine: RollbackEngine,
                workloadEngine: WorkloadEngine);
        }

        public async Task<ProcessSnapshotDto> ScanProcessesAsync()
        {
            return await ProcessIntelligence.CaptureSnapshotAsync();
        }

        public async Task<OptimizationPlanDto> GeneratePlanAsync(int pass = 1)
        {
            return await Planner.GeneratePlanAsync(pass);
        }

        public async Task<OptimizationResultDto> ExecuteFullOptimizationAsync(int maxPasses = 4, CancellationToken ct = default)
        {
            return await Executor.ExecuteAdaptiveOptimizationAsync(maxPasses, ct);
        }

        public OptimizationMetricsDto GetLiveMetrics()
        {
            var metrics = BenchmarkEngine.SampleCurrentMetrics();
            var workload = WorkloadEngine.DetectActiveWorkloads(System.Diagnostics.Process.GetProcesses());

            metrics.IsGamingWorkloadActive = workload.IsGamingDetected;
            metrics.IsEditingWorkloadActive = workload.IsEditingDetected;
            metrics.ActiveWorkloadSummary = string.Join(", ", workload.ActiveWorkloadNames);

            return metrics;
        }

        public bool RollbackOptimization(string batchId, out int restored, out int failed, out string msg)
        {
            return RollbackEngine.RollbackBatch(batchId, out restored, out failed, out msg);
        }
    }
}
