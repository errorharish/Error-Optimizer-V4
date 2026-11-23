#nullable enable
using System.Collections.Generic;
using System.Linq;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.GUI.Services;
using Xunit;

namespace BiosOptimizer.Core.Tests
{
    public class PerformanceStatusAuditCoordinatorTests
    {
        [Fact]
        public void CreateInitialAnalyzingStatuses_StartsInAnalyzingState_NeverHardcoded()
        {
            var (gaming, editing, streaming) = PerformanceStatusAuditCoordinator.CreateInitialAnalyzingStatuses();

            Assert.Equal(WorkloadPerformanceState.Analyzing, gaming.Status);
            Assert.Equal("ANALYZING", gaming.StatusText);
            Assert.False(gaming.IsAuditComplete);
            Assert.False(gaming.CanInvokeShortcut);

            Assert.Equal(WorkloadPerformanceState.Analyzing, editing.Status);
            Assert.Equal("ANALYZING", editing.StatusText);
            Assert.False(editing.IsAuditComplete);

            Assert.Equal(WorkloadPerformanceState.Analyzing, streaming.Status);
            Assert.Equal("ANALYZING", streaming.StatusText);
            Assert.False(streaming.IsAuditComplete);
        }

        [Fact]
        public void EvaluateAllWorkloads_WhenServiceOffline_ReturnsUnavailableWithTruthfulReason()
        {
            var (gaming, editing, streaming) = PerformanceStatusAuditCoordinator.Instance.EvaluateAllWorkloads(
                isServiceOnline: false,
                hasCompletedInitialScan: true,
                isAnalyzing: false,
                context: new OptimizationOpportunityContext(),
                oneClickPlan: BuildFullyOptimizedOneClickPlan());

            Assert.Equal(WorkloadPerformanceState.Unavailable, gaming.Status);
            Assert.Equal("UNAVAILABLE", gaming.StatusText);
            Assert.Contains("Service offline", gaming.Reason);
            Assert.False(gaming.CanInvokeShortcut);

            Assert.Equal(WorkloadPerformanceState.Unavailable, editing.Status);
            Assert.Equal(WorkloadPerformanceState.Unavailable, streaming.Status);
        }

        [Fact]
        public void EvaluateGamingWorkload_WhenMeaningfulOpportunitiesExist_ReturnsNotOptimizedWithDirectShortcut()
        {
            var ctx = new OptimizationOpportunityContext
            {
                ActivePowerPlan = "Balanced",
                MemorySnapshot = new MemoryTelemetryState { MemoryPressurePercent = 82.0 },
                SafeReducibleProcesses = 6,
                ProtectedProcesses = 4,
                PendingGpuOptimizationsCount = 3,
                GpuHardwareName = "NVIDIA GeForce RTX 4070"
            };
            var plan = BuildUnoptimizedOneClickPlan();

            var status = PerformanceStatusAuditCoordinator.Instance.EvaluateGamingWorkload(
                ctx,
                plan,
                isActiveWorkload: true,
                activeWorkloadLabel: "Gaming (valorant)",
                hasDedicatedGpu: true,
                dedicatedGpuName: "NVIDIA GeForce RTX 4070");

            Assert.Equal(WorkloadPerformanceState.NotOptimized, status.Status);
            Assert.Equal("NOT OPTIMIZED", status.StatusText);
            Assert.True(status.IsAuditComplete);
            Assert.False(status.IsVerified);
            Assert.True(status.MeaningfulOpportunityCount >= 2);
            Assert.DoesNotContain("OneClick", status.NavigationTarget);
            Assert.Equal("GpuRegistryValues:GPU", status.NavigationTarget);
            Assert.Equal("GpuRegistryValues", status.TargetPageKey);
            Assert.Equal("gaming.gpu_shader", status.TargetOpportunityId);
            Assert.Equal("View & Optimize →", status.ActionButtonText);
            Assert.True(status.CanInvokeShortcut);
            Assert.Contains(status.AuditItems, a => a.IsProtected && a.AuditOutcome == "SKIPPED — PROTECTED");
        }

        [Fact]
        public void EvaluateAllWorkloads_WhenAllSafeChecksVerified_ReturnsOptimizedWithAuditShortcut_AndRespectsProtectedApps()
        {
            var ctx = new OptimizationOpportunityContext
            {
                ActivePowerPlan = "High Performance",
                MemorySnapshot = new MemoryTelemetryState { MemoryPressurePercent = 42.0 },
                SafeReducibleProcesses = 0,
                ProtectedProcesses = 5, // Discord, Steam, OBS, Premiere protected
                PendingGpuOptimizationsCount = 0
            };
            var plan = BuildFullyOptimizedOneClickPlan();

            var (gaming, editing, streaming) = PerformanceStatusAuditCoordinator.Instance.EvaluateAllWorkloads(
                isServiceOnline: true,
                hasCompletedInitialScan: true,
                isAnalyzing: false,
                context: ctx,
                oneClickPlan: plan,
                hasDedicatedGpu: true,
                dedicatedGpuName: "NVIDIA GeForce RTX 4070",
                storageUsedPercent: 45);

            // Gaming
            Assert.Equal(WorkloadPerformanceState.Optimized, gaming.Status);
            Assert.Equal("OPTIMIZED", gaming.StatusText);
            Assert.Equal(0, gaming.RemainingOptimizationCount);
            Assert.True(gaming.IsVerified);
            Assert.DoesNotContain("OneClick", gaming.NavigationTarget);
            Assert.Equal("AiWorkload:GAMES", gaming.NavigationTarget);
            Assert.Equal("View Audit →", gaming.ActionButtonText);
            Assert.Contains(gaming.AuditItems, a => a.AuditOutcome == "SKIPPED — PROTECTED");

            // Editing
            Assert.Equal(WorkloadPerformanceState.Optimized, editing.Status);
            Assert.Equal("OPTIMIZED", editing.StatusText);
            Assert.Equal(0, editing.RemainingOptimizationCount);
            Assert.DoesNotContain("OneClick", editing.NavigationTarget);
            Assert.Equal("AiWorkload:CREATIVE", editing.NavigationTarget);

            // Streaming
            Assert.Equal(WorkloadPerformanceState.Optimized, streaming.Status);
            Assert.Equal("OPTIMIZED", streaming.StatusText);
            Assert.Equal(0, streaming.RemainingOptimizationCount);
            Assert.DoesNotContain("OneClick", streaming.NavigationTarget);
            Assert.Equal("AiWorkload:RECORDING", streaming.NavigationTarget);
        }

        [Fact]
        public void EvaluateAllWorkloads_WhenUnoptimized_RoutesEachWorkloadToDistinctRealOptimizationDestination_NeverOneClick()
        {
            var ctx = new OptimizationOpportunityContext
            {
                ActivePowerPlan = "Balanced",
                MemorySnapshot = new MemoryTelemetryState { MemoryPressurePercent = 79.0 },
                SafeReducibleProcesses = 5,
                ProtectedProcesses = 3,
                PendingGpuOptimizationsCount = 2,
                GpuHardwareName = "NVIDIA GeForce RTX 4070"
            };
            var plan = BuildUnoptimizedOneClickPlan();

            var (gaming, editing, streaming) = PerformanceStatusAuditCoordinator.Instance.EvaluateAllWorkloads(
                isServiceOnline: true,
                hasCompletedInitialScan: true,
                isAnalyzing: false,
                context: ctx,
                oneClickPlan: plan,
                hasDedicatedGpu: true,
                dedicatedGpuName: "NVIDIA GeForce RTX 4070",
                storageUsedPercent: 65);

            Assert.DoesNotContain("OneClick", gaming.NavigationTarget);
            Assert.DoesNotContain("OneClick", editing.NavigationTarget);
            Assert.DoesNotContain("OneClick", streaming.NavigationTarget);

            Assert.NotEqual(gaming.NavigationTarget, editing.NavigationTarget);
            Assert.NotEqual(editing.NavigationTarget, streaming.NavigationTarget);
            Assert.NotEqual(gaming.NavigationTarget, streaming.NavigationTarget);

            Assert.NotEqual(gaming.TargetPageKey, editing.TargetPageKey);
            Assert.NotEqual(editing.TargetPageKey, streaming.TargetPageKey);
            Assert.NotEqual(gaming.TargetPageKey, streaming.TargetPageKey);
        }

        [Fact]
        public void EvaluateWorkloads_SupportsIndependentStatesPerWorkload()
        {
            // Scenario:
            // - Power, timers, RAM, background, and fast-clean are verified -> Streaming is OPTIMIZED (0 remaining)
            // - Deep clean (scratch/cache) has 1 moderate opportunity -> Editing is BALANCED (1 moderate remaining, 4 verified)
            // - Shader cache + visual tweaks have opportunities -> Gaming is NOT OPTIMIZED
            var ctx = new OptimizationOpportunityContext
            {
                ActivePowerPlan = "High Performance",
                MemorySnapshot = new MemoryTelemetryState { MemoryPressurePercent = 48.0 },
                SafeReducibleProcesses = 0,
                ProtectedProcesses = 3,
                PendingGpuOptimizationsCount = 0
            };

            var plan = BuildFullyOptimizedOneClickPlan();
            plan.Actions.First(a => a.Id == "shader-cache").Status = OneClickActionStatus.Recommended;
            plan.Actions.First(a => a.Id == "visual-tweaks").Status = OneClickActionStatus.Recommended;
            plan.Actions.First(a => a.Id == "deep-clean").Status = OneClickActionStatus.Recommended;

            var (gaming, editing, streaming) = PerformanceStatusAuditCoordinator.Instance.EvaluateAllWorkloads(
                isServiceOnline: true,
                hasCompletedInitialScan: true,
                isAnalyzing: false,
                context: ctx,
                oneClickPlan: plan,
                hasDedicatedGpu: true,
                dedicatedGpuName: "NVIDIA GeForce RTX 4070",
                storageUsedPercent: 50);

            Assert.Equal(WorkloadPerformanceState.NotOptimized, gaming.Status);
            Assert.Equal(WorkloadPerformanceState.Balanced, editing.Status);
            Assert.Equal(WorkloadPerformanceState.Optimized, streaming.Status);

            Assert.Equal("MaxPerformance:DWM_LATENCY", gaming.NavigationTarget);
            Assert.Equal("Storage:DEEP_CACHE", editing.NavigationTarget);
            Assert.Equal("AiWorkload:RECORDING", streaming.NavigationTarget);
        }

        [Fact]
        public void OneClickActionItem_IsApplicableToWorkload_FiltersAccurately()
        {
            var shaderAction = new OneClickActionItem
            {
                Id = "shader-cache",
                ApplicableWorkloads = new List<string> { "GAMING" }
            };

            Assert.True(shaderAction.IsApplicableToWorkload("GAMING"));
            Assert.False(shaderAction.IsApplicableToWorkload("EDITING"));
            Assert.False(shaderAction.IsApplicableToWorkload("STREAMING"));
            Assert.Equal("GAMING", shaderAction.WorkloadTagsText);
        }

        private static OneClickPlan BuildFullyOptimizedOneClickPlan()
        {
            return new OneClickPlan
            {
                Actions = new List<OneClickActionItem>
                {
                    new() { Id = "visual-tweaks", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING", "STREAMING" } },
                    new() { Id = "bcd-platform-timers", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING", "STREAMING" } },
                    new() { Id = "shader-cache", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING" } },
                    new() { Id = "fast-clean", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } },
                    new() { Id = "deep-clean", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "EDITING" } },
                    new() { Id = "background-workload", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } },
                    new() { Id = "ram-standby", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } },
                    new() { Id = "power-performance", Status = OneClickActionStatus.AlreadyCompleted, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } }
                }
            };
        }

        private static OneClickPlan BuildUnoptimizedOneClickPlan()
        {
            return new OneClickPlan
            {
                Actions = new List<OneClickActionItem>
                {
                    new() { Id = "visual-tweaks", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING", "STREAMING" } },
                    new() { Id = "bcd-platform-timers", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING", "STREAMING" } },
                    new() { Id = "shader-cache", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING" } },
                    new() { Id = "fast-clean", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } },
                    new() { Id = "deep-clean", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "EDITING" } },
                    new() { Id = "background-workload", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } },
                    new() { Id = "ram-standby", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } },
                    new() { Id = "power-performance", Status = OneClickActionStatus.Recommended, ApplicableWorkloads = new List<string> { "GAMING", "EDITING", "STREAMING" } }
                }
            };
        }
    }
}
