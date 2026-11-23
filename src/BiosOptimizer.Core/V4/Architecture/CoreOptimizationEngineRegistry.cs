#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.V4.Coordinator;
using BiosOptimizer.Core.V4.Environments;
using BiosOptimizer.Core.V4.Execution;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Metrics;
using BiosOptimizer.Core.V4.Persistent;
using BiosOptimizer.Core.V4.Planning;
using BiosOptimizer.Core.V4.Rollback;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Tuning;
using BiosOptimizer.Core.V4.Verification;
using BiosOptimizer.Core.V4.Workloads;

namespace BiosOptimizer.Core.V4.Architecture
{
    public class EngineServiceDescriptor
    {
        public int ServiceNumber { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public string PrimaryInterfaceOrClass { get; set; } = string.Empty;
        public string Responsibility { get; set; } = string.Empty;
        public string Status { get; set; } = "SOURCE IMPLEMENTED";
    }

    /// <summary>
    /// Authoritative Service Registry documenting and connecting all 26 internal Core Optimization engines.
    /// Guarantees that every subsystem has a single source of truth and prevents duplicate handlers.
    /// </summary>
    public sealed class CoreOptimizationEngineRegistry
    {
        private static readonly Lazy<CoreOptimizationEngineRegistry> _instance = new(() => new CoreOptimizationEngineRegistry());
        public static CoreOptimizationEngineRegistry Instance => _instance.Value;

        public IReadOnlyList<EngineServiceDescriptor> AuthoritativeServices { get; }

        public CoreOptimizationEngineRegistry()
        {
            AuthoritativeServices = new List<EngineServiceDescriptor>
            {
                new() { ServiceNumber = 1, ServiceName = "HardwareCapabilityDetection", PrimaryInterfaceOrClass = nameof(HardwareDetectionService), Responsibility = "Probes CPU topology, GPU WDDM/VRAM, RAM, and storage geometry." },
                new() { ServiceNumber = 2, ServiceName = "WindowsCapabilityDetection", PrimaryInterfaceOrClass = nameof(EnvironmentDetector), Responsibility = "Detects exact Windows build, release ID, edition, and HVCI security state." },
                new() { ServiceNumber = 3, ServiceName = "UniversalEligibilityEngine", PrimaryInterfaceOrClass = nameof(UniversalEligibilityEngine), Responsibility = "Evaluates hardware, OS, power, and conflict eligibility for all candidates." },
                new() { ServiceNumber = 4, ServiceName = "ProcessIntelligenceEngine", PrimaryInterfaceOrClass = nameof(ProcessIntelligenceEngine), Responsibility = "Deep Win32 process inspection, parent-child graphs, and safe categorization." },
                new() { ServiceNumber = 5, ServiceName = "ProcessIdentityValidation", PrimaryInterfaceOrClass = nameof(ProcessIdentitySafety), Responsibility = "Guards against PID reuse and path spoofing via creation time verification." },
                new() { ServiceNumber = 6, ServiceName = "BackgroundOpportunityDiscovery", PrimaryInterfaceOrClass = nameof(ProcessIntelligenceEngine), Responsibility = "Identifies reducible background daemons, update assistants, and telemetry." },
                new() { ServiceNumber = 7, ServiceName = "ServiceDependencyIntelligence", PrimaryInterfaceOrClass = nameof(ServiceDependencyEngine), Responsibility = "Analyzes Windows services, dependencies, and safe reduction opportunities." },
                new() { ServiceNumber = 8, ServiceName = "StartupIntelligence", PrimaryInterfaceOrClass = nameof(StartupIntelligenceEngine), Responsibility = "Evaluates HKCU/HKLM Run keys, Startup folders, and StartupApproved entries." },
                new() { ServiceNumber = 9, ServiceName = "WorkloadDetectionEngine", PrimaryInterfaceOrClass = nameof(WorkloadDetectionEngine), Responsibility = "Detects active gaming, Android emulators (BlueStacks/LDPlayer), and video editing." },
                new() { ServiceNumber = 10, ServiceName = "CPUHeterogeneousTopologyEngine", PrimaryInterfaceOrClass = nameof(CPUHeterogeneousTopologyEngine), Responsibility = "Intel Thread Director hybrid routing and AMD dual-CCD 3D V-Cache CPPC gating." },
                new() { ServiceNumber = 11, ServiceName = "MemoryPressureIntelligenceEngine", PrimaryInterfaceOrClass = nameof(MemoryPressureIntelligenceEngine), Responsibility = "Tracks commit headroom, compression, and active workload memory shields." },
                new() { ServiceNumber = 12, ServiceName = "GPUCapabilityAndPressureEngine", PrimaryInterfaceOrClass = nameof(GPUCapabilityAndPressureEngine), Responsibility = "Evaluates HAGS prerequisites (>=6GB VRAM, WDDM 2.7+) and hybrid GPU routing." },
                new() { ServiceNumber = 13, ServiceName = "StorageIOMonitoringEngine", PrimaryInterfaceOrClass = nameof(StorageIOMonitoringEngine), Responsibility = "Win32 4KB cluster alignment verification and TRIM command pass-through." },
                new() { ServiceNumber = 14, ServiceName = "NetworkCapabilityEngine", PrimaryInterfaceOrClass = nameof(NetworkCapabilityEngine), Responsibility = "Calibrates TCP autotuning and gates low-latency ACK pacing to gaming." },
                new() { ServiceNumber = 15, ServiceName = "WorkloadSchedulingEngine", PrimaryInterfaceOrClass = nameof(WorkloadSchedulingEngine), Responsibility = "Coordinates process priorities and MMCSS audio/input task scheduling." },
                new() { ServiceNumber = 16, ServiceName = "OptimizationDependencyGraph", PrimaryInterfaceOrClass = nameof(OptimizationDependencyGraph), Responsibility = "Maintains prerequisite relationships and execution order between tweaks." },
                new() { ServiceNumber = 17, ServiceName = "OptimizationConflictEngine", PrimaryInterfaceOrClass = nameof(OptimizationConflictEngine), Responsibility = "Detects and prevents mutually exclusive profiles and battery/power conflicts." },
                new() { ServiceNumber = 18, ServiceName = "SafeOpportunityPlanner", PrimaryInterfaceOrClass = nameof(MaximumPerformancePlanner), Responsibility = "Synthesizes system telemetry and generates unified optimization plans." },
                new() { ServiceNumber = 19, ServiceName = "AdaptiveConvergenceEngine", PrimaryInterfaceOrClass = nameof(MaximumPerformanceExecutor), Responsibility = "Iterative safe execution loop with instant stop on opportunity exhaustion." },
                new() { ServiceNumber = 20, ServiceName = "AuthorizedExecutionEngine", PrimaryInterfaceOrClass = nameof(SystemPerformanceOptimizer), Responsibility = "Executes verified system mutations under strict SafetyKernel authorization." },
                new() { ServiceNumber = 21, ServiceName = "StateCaptureEngine", PrimaryInterfaceOrClass = nameof(V4RollbackEngine), Responsibility = "Captures exact pre-optimization states and records transactional journals." },
                new() { ServiceNumber = 22, ServiceName = "VerificationEngine", PrimaryInterfaceOrClass = nameof(OptimizationVerificationEngine), Responsibility = "Independently validates that system modifications achieved target states." },
                new() { ServiceNumber = 23, ServiceName = "RollbackEngine", PrimaryInterfaceOrClass = nameof(V4RollbackEngine), Responsibility = "Reverts Registry, Power, Priority, and Memory actions with idempotence." },
                new() { ServiceNumber = 24, ServiceName = "PerformanceRegressionEngine", PrimaryInterfaceOrClass = nameof(OptimizationBenchmarkEngine), Responsibility = "Monitors system responsiveness and triggers rollbacks upon degradation." },
                new() { ServiceNumber = 25, ServiceName = "TelemetryEngine", PrimaryInterfaceOrClass = nameof(OptimizationBenchmarkEngine), Responsibility = "Captures multi-dimensional hardware metrics (CPU, RAM, Commit, GPU, I/O)." },
                new() { ServiceNumber = 26, ServiceName = "HistoricalEffectivenessEngine", PrimaryInterfaceOrClass = nameof(OptimizedBaselineStore), Responsibility = "Stores persistent optimization baselines and convergence progression history." }
            };
        }
    }
}
