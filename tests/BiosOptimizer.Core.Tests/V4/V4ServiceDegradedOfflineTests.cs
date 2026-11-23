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
    internal class MockDegradedIpcClient : IIpcClient
    {
        public bool IsServiceAvailable { get; set; } = false;
        public IpcConnectionState ConnectionState { get; set; } = IpcConnectionState.Degraded;
        public string ServiceBuildId { get; set; } = "Offline";
        public bool IsOnline => IsServiceAvailable;
        public string StatusText => IsServiceAvailable ? "ONLINE" : "DEGRADED";
        public System.Windows.Media.Brush StatusBrush => System.Windows.Media.Brushes.Orange;

        public event Action<bool>? ServiceAvailabilityChanged;
        public event Action<IpcConnectionState>? ConnectionStateChanged;

        public bool ShouldSimulateTimeout { get; set; } = false;

        public Task<IpcResponse> SendRequestAsync(IpcMessageType type, string? payload = null, CancellationToken ct = default)
        {
            if (ShouldSimulateTimeout)
            {
                throw new OperationCanceledException("Simulated timeout in test");
            }

            if (!IsServiceAvailable)
            {
                return Task.FromResult(new IpcResponse { Success = false, ErrorMessage = "Service Offline" });
            }

            var resp = new IpcResponse { Success = true };
            if (type == IpcMessageType.V4ScanProcessIntelligence)
            {
                resp.Data = "{\"TotalProcessCount\": 195, \"CpuUtilizationPercent\": 9.8, \"UsedRamMb\": 6144, \"ReducibleProcessCount\": 32, \"ProtectedProcessCount\": 110, \"RequiredProcessCount\": 38, \"WorkloadProcessCount\": 15}";
            }
            else if (type == IpcMessageType.V4GetLiveMetrics)
            {
                resp.Data = "{\"RamUsedMb\": 6144, \"CpuUtilizationPercent\": 9.8, \"ActiveWorkloadSummary\": \"Gaming Protected\"}";
            }
            return Task.FromResult(resp);
        }

        public async IAsyncEnumerable<IpcResponse> SendStreamingRequestAsync(IpcMessageType type, string? payload = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await SendRequestAsync(type, payload, ct);
        }

        public Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            return Task.FromResult(IsServiceAvailable);
        }

        public void SetConnectionState(IpcConnectionState state)
        {
            ConnectionState = state;
            IsServiceAvailable = (state == IpcConnectionState.Connected);
            ConnectionStateChanged?.Invoke(state);
            ServiceAvailabilityChanged?.Invoke(IsServiceAvailable);
        }
    }

    public class V4ServiceDegradedOfflineTests
    {
        [Fact]
        public void MaxPerformanceViewModel_WhenServiceDegraded_InitializesInSafeOfflineMode()
        {
            var mockIpc = new MockDegradedIpcClient
            {
                IsServiceAvailable = false,
                ConnectionState = IpcConnectionState.Degraded
            };

            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.True(vm.IsServiceOffline);
            Assert.False(vm.IsServiceAvailable);
            Assert.True(vm.IsReadyMode);
            Assert.False(vm.IsOptimizingMode);
            Assert.False(vm.IsBusy);
            Assert.Contains("offline", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MaxPerformanceViewModel_WhenServiceOffline_MetricsShowUnavailable()
        {
            var mockIpc = new MockDegradedIpcClient
            {
                IsServiceAvailable = false,
                ConnectionState = IpcConnectionState.Offline
            };

            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.Equal("Unavailable", vm.ProcessCountDisplay);
            Assert.Equal("Unavailable", vm.FormattedCpuLoad);
            Assert.Equal("Unavailable", vm.FormattedRamUsage);
            Assert.False(vm.IsMetricAvailable);
            Assert.Contains("Unavailable", vm.DynamicReducibleWorkloadText);
        }

        [Fact]
        public void MaxPerformanceViewModel_WhenServiceOffline_OptimizeCommandIsDisabled()
        {
            var mockIpc = new MockDegradedIpcClient
            {
                IsServiceAvailable = false,
                ConnectionState = IpcConnectionState.Degraded
            };

            var vm = new MaxPerformanceViewModel(mockIpc);

            Assert.False(vm.MaximumOptimizeCommand.CanExecute(null));
            Assert.Contains("unavailable", vm.OptimizeButtonToolTip, StringComparison.OrdinalIgnoreCase);

            // Attempting to invoke confirmation while offline must be blocked
            vm.OpenSafetyConfirmation();
            Assert.False(vm.IsConfirmSafetyMode);
            Assert.True(vm.IsReadyMode);
            Assert.Contains("unavailable", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MaxPerformanceViewModel_WhenServiceRecovers_MetricsPopulate()
        {
            var mockIpc = new MockDegradedIpcClient
            {
                IsServiceAvailable = false,
                ConnectionState = IpcConnectionState.Degraded
            };

            var vm = new MaxPerformanceViewModel(mockIpc);
            Assert.True(vm.IsServiceOffline);
            Assert.Equal("Unavailable", vm.ProcessCountDisplay);

            // Simulate service coming back online
            mockIpc.IsServiceAvailable = true;
            mockIpc.ConnectionState = IpcConnectionState.Connected;

            // Trigger retry
            await vm.RetryServiceAsync();

            Assert.False(vm.IsServiceOffline);
            Assert.True(vm.IsServiceAvailable);
            Assert.True(vm.IsMetricAvailable);
            Assert.Equal("195", vm.ProcessCountDisplay);
            Assert.Equal("9.8%", vm.FormattedCpuLoad);
            Assert.Equal("6.0 GB", vm.FormattedRamUsage);
        }

        [Fact]
        public async Task MaxPerformanceViewModel_WhenRequestTimesOut_NeverStuckInLoading()
        {
            var mockIpc = new MockDegradedIpcClient
            {
                IsServiceAvailable = true,
                ShouldSimulateTimeout = true
            };

            var vm = new MaxPerformanceViewModel(mockIpc);

            await vm.RefreshIntelligenceAsync();

            // Must cleanly recover to Ready and not stay in busy/optimizing
            Assert.False(vm.IsBusy);
            Assert.True(vm.IsReadyMode);
            Assert.False(vm.IsOptimizingMode);
            Assert.True(vm.IsServiceOffline);
            Assert.Equal("Unavailable", vm.ProcessCountDisplay);
        }
    }
}
