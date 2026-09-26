using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    internal class MockIpcClient : IIpcClient
    {
        public bool IsServiceAvailable => true;
        public IpcConnectionState ConnectionState => IpcConnectionState.Connected;
        public string ServiceBuildId => "1.0.0";

        public event Action<bool>? ServiceAvailabilityChanged;
        public event Action<IpcConnectionState>? ConnectionStateChanged;

        public Task<IpcResponse> SendRequestAsync(IpcMessageType type, string? payload = null, CancellationToken ct = default)
        {
            var resp = new IpcResponse { Success = true };
            if (type == IpcMessageType.V4ScanProcessIntelligence)
            {
                resp.Data = "{\"TotalProcessCount\": 247, \"CpuUtilizationPercent\": 14.2, \"UsedRamMb\": 8192, \"ReducibleProcessCount\": 45, \"ProtectedProcessCount\": 120, \"RequiredProcessCount\": 40, \"WorkloadProcessCount\": 20}";
            }
            else if (type == IpcMessageType.V4GetLiveMetrics)
            {
                resp.Data = "{\"RamUsedMb\": 8192, \"CpuUtilizationPercent\": 14.2, \"ActiveWorkloadSummary\": \"Gaming + Editing Protected\"}";
            }
            return Task.FromResult(resp);
        }

        public async IAsyncEnumerable<IpcResponse> SendStreamingRequestAsync(IpcMessageType type, string? payload = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await SendRequestAsync(type, payload, ct);
        }

        public Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);

        public void SetConnectionState(IpcConnectionState state) { }
    }

    public class V4BeginnerUxTests
    {
        [Fact]
        public void MaxPerformanceViewModel_InitializesInReadyStateWithBeginnerMetrics()
        {
            var mockIpc = new MockIpcClient();
            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.True(vm.IsReadyMode);
            Assert.False(vm.IsConfirmSafetyMode);
            Assert.False(vm.IsOptimizingMode);
            Assert.False(vm.IsCompletedMode);
            Assert.False(vm.ShowAdvancedDetails);
            Assert.Equal("READY", vm.BackupStatusText);
            Assert.Equal("ENABLED", vm.ProtectedComponentsStatusText);
            Assert.Equal("Protected", vm.GamingProtectionStatus);
            Assert.Equal("Protected", vm.EditingProtectionStatus);
            Assert.Equal("Protected", vm.WindowsProtectionStatus);
        }

        [Fact]
        public void SafetyConfirmationWorkflow_TransitionsToConfirmSafetyAndAllowsCancel()
        {
            var mockIpc = new MockIpcClient();
            var vm = new MaxPerformanceViewModel(mockIpc);

            // User clicks [ MAXIMUM OPTIMIZE ]
            vm.MaximumOptimizeCommand.Execute(null);
            Assert.True(vm.IsConfirmSafetyMode);
            Assert.False(vm.IsReadyMode);

            // User clicks [ CANCEL ]
            vm.CancelSafetyCommand.Execute(null);
            Assert.True(vm.IsReadyMode);
            Assert.False(vm.IsConfirmSafetyMode);
        }

        [Fact]
        public void RollbackAvailability_RequiresValidTransactionBatchId()
        {
            var mockIpc = new MockIpcClient();
            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.False(vm.HasRollbackAvailable);
            Assert.True(string.IsNullOrEmpty(vm.LastBatchId));

            // Simulating completion of optimization with a batch ID
            vm.LastBatchId = "batch_test_12345";
            Assert.True(vm.HasRollbackAvailable);
        }

        [Fact]
        public void ViewDetails_TogglesTechnicalInformationDrawer()
        {
            var mockIpc = new MockIpcClient();
            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.False(vm.ShowAdvancedDetails);

            vm.ToggleDetailsCommand.Execute(null);
            Assert.True(vm.ShowAdvancedDetails);

            vm.CloseDetailsCommand.Execute(null);
            Assert.False(vm.ShowAdvancedDetails);
        }

        [Fact]
        public void StopReason_UsesCorrectDynamicSafetyMessageInsteadOfFixed70Stop()
        {
            var mockIpc = new MockIpcClient();
            var vm = new MaxPerformanceViewModel(mockIpc);

            // Verify stop reason does not use fixed numbers
            Assert.DoesNotContain("70 reached", vm.StopReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("No additional safe background optimization was found.", vm.StopReason);
        }

        [Fact]
        public void BeginnerWording_UsesDynamicDeterminedAutomaticallyInsteadOf70To85Floor()
        {
            var mockIpc = new MockIpcClient();
            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.Equal("Maximum Safe Optimization: Determined Automatically", vm.DynamicSafeOptimizationText);
            Assert.Equal("Maximum safe optimization is determined automatically for this PC.", vm.SystemOptimizationNoticeText);
            Assert.Contains("Potentially Reducible Background Workload:", vm.DynamicReducibleWorkloadText);
            Assert.DoesNotContain("70–85", vm.SafeMinimumRangeText);
            Assert.DoesNotContain("safe floor estimate", vm.DynamicSafeOptimizationText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
