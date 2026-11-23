using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.IPC.Contracts;
using Xunit;



namespace BiosOptimizer.Core.Tests.V4

{

    public class V4NavigationTests

    {

        [Fact]

        public void MaxPerformanceViewModel_AllReadOnlyProperties_ReturnExpectedValues()

        {

            var mockIpc = new MockIpcClient();

            var vm = new MaxPerformanceViewModel(mockIpc);



            // Test initial property values

            Assert.Equal("0.0 GB", vm.FormattedInitialRam);

            Assert.Equal("0.0 GB", vm.FormattedFinalRam);

            Assert.Equal("0 MB recovered", vm.FormattedRamRecovered);

            Assert.Equal("0.0%", vm.FormattedInitialCpu);

            Assert.Equal("0.0%", vm.FormattedFinalCpu);

            Assert.Equal(0, vm.ProcessesReducedCount);

            Assert.Equal(0, vm.ActionsAppliedCount);

            Assert.Equal(0, vm.ActionsSkippedCount);

            Assert.Equal("Protected", vm.GamingProtectionStatus);

            Assert.Equal("Protected", vm.EditingProtectionStatus);

            Assert.Equal("Protected", vm.WindowsProtectionStatus);

        }



        [Fact]

        public async Task MaxPerformanceViewModel_NavigationLifecycle_SucceedsRepeatedly()

        {

            var mockIpc = new MockIpcClient();

            var vm = new MaxPerformanceViewModel(mockIpc);



            // Simulate navigating to V4

            await vm.OnNavigatedToAsync();

            Assert.True(vm.IsReadyMode);



            // Simulate navigating away

            await vm.OnNavigatedFromAsync();



            // Simulate navigating to V4 again

            await vm.OnNavigatedToAsync();

            Assert.True(vm.IsReadyMode);



            // Simulate navigating away again

            await vm.OnNavigatedFromAsync();

        }



        [Fact]

        public async Task MainViewModel_SwitchingBetweenV3AndV4_TransitionsSmoothly()

        {

            var mockIpc = new MockIpcClient();

            var mainVm = new MainViewModel(mockIpc);



            // Initial state: V3 Dashboard

            Assert.False(mainVm.IsV4Mode);



            // Switch to V4

            mainVm.SwitchToV4Command.Execute(null);

            // Allow async task

            await Task.Delay(100);



            Assert.True(mainVm.IsV4Mode);

            Assert.IsType<MaxPerformanceViewModel>(mainVm.CurrentView);



            // Switch back to V3

            mainVm.SwitchToV3Command.Execute(null);

            await Task.Delay(100);



            Assert.False(mainVm.IsV4Mode);

            Assert.IsType<DashboardViewModel>(mainVm.CurrentView);



            // Switch to V4 again (repeat cycle)

            mainVm.SwitchToV4Command.Execute(null);

            await Task.Delay(100);



            Assert.True(mainVm.IsV4Mode);

            Assert.IsType<MaxPerformanceViewModel>(mainVm.CurrentView);

        }



        [Fact]

        public void ErrorViewModel_FormatsDiagnosticDetailsCorrectly()

        {

            var ex = new InvalidOperationException("A TwoWay or OneWayToSource binding cannot work on the read-only property 'FormattedInitialRam'");

            bool retried = false;



            var errorVm = new ErrorViewModel(

                error: $"UI Rendering Error: {ex.GetType().Name} - {ex.Message}",

                stack: $"{ex.Message}\n\n{ex.StackTrace}",

                retryAction: () => { retried = true; }

            );



            Assert.Contains("InvalidOperationException", errorVm.ErrorMessage);

            Assert.Contains("FormattedInitialRam", errorVm.ErrorMessage);

            Assert.Contains("FormattedInitialRam", errorVm.StackTrace);



            errorVm.RetryCommand.Execute(null);

            Assert.True(retried);

        }



        [Fact]

        public async Task MainViewModel_VersionSwitching_PreservesIndependentNavigationRoutes()

        {

            var mockIpc = new MockIpcClient();

            var mainVm = new MainViewModel(mockIpc);



            // Initially V3: navigate to InputOptimizer

            await mainVm.NavigateAsync("InputOptimizer");

            Assert.False(mainVm.IsV4Mode);

            Assert.True(mainVm.IsV3Mode);

            Assert.IsType<InputOptimizerViewModel>(mainVm.CurrentView);

            Assert.Equal("ERROR OPTIMIZER V3", mainVm.HeaderBrandTitle);

            Assert.Equal("Classic Suite", mainVm.HeaderWorkspaceLabel);



            // Switch to V4: should load MaxPerformance

            mainVm.SwitchToV4Command.Execute(null);

            await Task.Delay(100);

            Assert.True(mainVm.IsV4Mode);

            Assert.False(mainVm.IsV3Mode);

            Assert.IsType<MaxPerformanceViewModel>(mainVm.CurrentView);

            Assert.Equal("ERROR OPTIMIZER V4", mainVm.HeaderBrandTitle);

            Assert.Equal("Professional Suite", mainVm.HeaderWorkspaceLabel);



            // Navigate to V4Dashboard in V4
            await mainVm.NavigateAsync("V4Dashboard");
            Assert.True(mainVm.IsV4Mode);
            Assert.IsType<V4DashboardViewModel>(mainVm.CurrentView);

            // Switch back to V3: should restore InputOptimizer
            mainVm.SwitchToV3Command.Execute(null);
            await Task.Delay(100);
            Assert.False(mainVm.IsV4Mode);
            Assert.True(mainVm.IsV3Mode);
            Assert.IsType<InputOptimizerViewModel>(mainVm.CurrentView);

            // Switch back to V4: should restore V4Dashboard
            mainVm.SwitchToV4Command.Execute(null);
            await Task.Delay(100);
            Assert.True(mainVm.IsV4Mode);
            Assert.IsType<V4DashboardViewModel>(mainVm.CurrentView);

        }



        [Fact]

        public async Task MainViewModel_V3Pages_DoNotResolveToV4ViewModels()

        {

            var mockIpc = new MockIpcClient();

            var mainVm = new MainViewModel(mockIpc);



            await mainVm.NavigateAsync("Dashboard");

            Assert.IsType<DashboardViewModel>(mainVm.CurrentView);

            Assert.IsNotType<MaxPerformanceViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("ProcessReduction");

            Assert.IsType<DashboardViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("InputOptimizer");

            Assert.IsType<InputOptimizerViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("SystemInfo");

            Assert.IsType<SystemInfoViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("RegistryTweak");

            Assert.IsType<RegistryTweakViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("StartupManager");

            Assert.IsType<StartupManagerViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("ServiceManager");

            Assert.IsType<DashboardViewModel>(mainVm.CurrentView);



            await mainVm.NavigateAsync("Normal");

            Assert.IsType<TierViewModel>(mainVm.CurrentView);

            // In V3 mode: MaxPerformance MUST resolve to TierViewModel (V3 Maximum Performance Profile), NOT V4
            await mainVm.NavigateAsync("MaxPerformance");
            Assert.IsType<TierViewModel>(mainVm.CurrentView);
            Assert.IsNotType<MaxPerformanceViewModel>(mainVm.CurrentView);
            Assert.True(mainVm.IsV3Mode);
            Assert.False(mainVm.IsV4Mode);
            Assert.Equal("MAX PERFORMANCE", ((TierViewModel)mainVm.CurrentView).ProfileDisplayName);
            Assert.Equal("Maximum Performance", ((TierViewModel)mainVm.CurrentView).TierName);
        }

        [Fact]
        public async Task MainViewModel_V3AndV4MaximumPerformance_AreIsolatedAndDoNotCollide()
        {
            var mockIpc = new MockIpcClient();
            var mainVm = new MainViewModel(mockIpc);

            // 1. In V3 mode: navigate to MaxPerformance
            await mainVm.NavigateAsync("MaxPerformance");
            Assert.True(mainVm.IsV3Mode);
            Assert.False(mainVm.IsV4Mode);
            Assert.IsType<TierViewModel>(mainVm.CurrentView);
            var v3Tier = (TierViewModel)mainVm.CurrentView;
            Assert.Equal("MAX PERFORMANCE", v3Tier.ProfileDisplayName);
            Assert.Equal("Maximum Performance", v3Tier.TierName);

            // 2. Switch to V4 mode: must display V4 MaxPerformanceViewModel
            mainVm.SwitchToV4Command.Execute(null);
            await Task.Delay(50);
            Assert.True(mainVm.IsV4Mode);
            Assert.False(mainVm.IsV3Mode);
            Assert.IsType<MaxPerformanceViewModel>(mainVm.CurrentView);

            // 3. In V4 mode: explicitly navigate to V4MaxPerformance
            await mainVm.NavigateAsync("V4MaxPerformance");
            Assert.True(mainVm.IsV4Mode);
            Assert.IsType<MaxPerformanceViewModel>(mainVm.CurrentView);

            // 4. Switch back to V3 mode: must restore the V3 TierViewModel, NOT redirect to V4
            mainVm.SwitchToV3Command.Execute(null);
            await Task.Delay(50);
            Assert.True(mainVm.IsV3Mode);
            Assert.False(mainVm.IsV4Mode);
            Assert.IsType<TierViewModel>(mainVm.CurrentView);
            Assert.Equal("MAX PERFORMANCE", ((TierViewModel)mainVm.CurrentView).ProfileDisplayName);

            // 5. Switching back to V4 restores V4 MaxPerformanceViewModel
            mainVm.SwitchToV4Command.Execute(null);
            await Task.Delay(50);
            Assert.True(mainVm.IsV4Mode);
            Assert.IsType<MaxPerformanceViewModel>(mainVm.CurrentView);
        }



        [Fact]

        public async Task MainViewModel_UtilityPages_PreserveActiveVersionState()

        {

            var mockIpc = new MockIpcClient();

            var mainVm = new MainViewModel(mockIpc);



            // In V3: visit Settings

            await mainVm.NavigateAsync("Dashboard");

            await mainVm.NavigateAsync("Settings");

            Assert.False(mainVm.IsV4Mode);

            Assert.True(mainVm.IsV3Mode);

            Assert.IsType<SettingsViewModel>(mainVm.CurrentView);



            // Switch to V4: visit About

            mainVm.SwitchToV4Command.Execute(null);

            await Task.Delay(100);

            await mainVm.NavigateAsync("About");

            Assert.True(mainVm.IsV4Mode);

            Assert.False(mainVm.IsV3Mode);

            Assert.IsType<AboutViewModel>(mainVm.CurrentView);

        }
    }
}
