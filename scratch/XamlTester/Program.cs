using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BiosOptimizer.GUI;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.GUI.Views;
using BiosOptimizer.IPC.Contracts;

namespace XamlTester
{
    class DummyIpcClient : IIpcClient
    {
        public bool IsServiceAvailable => true;
        public string ServiceBuildId => "20260825-000000";
        public event Action<bool> ServiceAvailabilityChanged = delegate { };

        public Task<IpcResponse> SendRequestAsync(IpcMessageType type, string payload = null, CancellationToken ct = default)
            => Task.FromResult(new IpcResponse { Success = true });

        public async IAsyncEnumerable<IpcResponse> SendStreamingRequestAsync(IpcMessageType type, string payload = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new IpcResponse { Success = true };
            await Task.CompletedTask;
        }

        public Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("================================================================================");
            Console.WriteLine(" ERROR OPTIMIZER V3 — XAML & BINDING RUNTIME VALIDATION TEST");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            var app = new Application();
            
            try
            {
                var themeDict = new ResourceDictionary { Source = new Uri("/ErrorOptimizer;component/Resources/Theme.xaml", UriKind.RelativeOrAbsolute) };
                var iconDict = new ResourceDictionary { Source = new Uri("/ErrorOptimizer;component/Resources/Icons.xaml", UriKind.RelativeOrAbsolute) };
                app.Resources.MergedDictionaries.Add(themeDict);
                app.Resources.MergedDictionaries.Add(iconDict);
                app.Resources.Add("BoolToVis", new BooleanToVisibilityConverter());
                app.Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INFO] Resource loading info: {ex.Message}");
            }

            int passed = 0;
            int total = 0;

            void AssertTest(string name, Action action)
            {
                total++;
                try
                {
                    action();
                    passed++;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("[PASS] ");
                    Console.ResetColor();
                    Console.WriteLine(name);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[FAIL] ");
                    Console.ResetColor();
                    Console.WriteLine($"{name} -> {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"       Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                }
            }

            var dummyIpc = new DummyIpcClient();

            // -- TEST 1: Instantiate InputOptimizerView --
            InputOptimizerView inputView = null!;
            AssertTest("Instantiate InputOptimizerView", () =>
            {
                inputView = new InputOptimizerView();
            });

            // -- TEST 2: Instantiate InputOptimizerViewModel & Bind DataContext --
            InputOptimizerViewModel inputVm = null!;
            AssertTest("Instantiate InputOptimizerViewModel & Bind DataContext", () =>
            {
                inputVm = new InputOptimizerViewModel(dummyIpc);
                inputView.DataContext = inputVm;
            });

            // -- TEST 3: Populate Items with ApplicabilityDisplay & Measure Layout --
            AssertTest("Render Telemetry Breakdown with Read-Only ApplicabilityDisplay (OneWay)", () =>
            {
                var item = new InputOptimizationItemModel
                {
                    Id = "mouse.test_item",
                    Name = "Test Mouse Precision Setting",
                    Category = "Mouse",
                    Risk = "CORE",
                    Applicable = true,
                    CurrentValue = "0",
                    TargetValue = "1",
                    Status = "PENDING",
                    Reason = @"HKEY_CURRENT_USER\Control Panel\Mouse\TestVal",
                    Description = "Test description verifying OneWay read-only binding."
                };

                inputVm.AllDiagnosticItems.Add(item);
                inputVm.NormalApplicableItems.Add(item);
                inputVm.AdvancedApplicableItems.Add(item);
                inputVm.ShowTechDetails = true;

                inputView.Measure(new Size(1280, 800));
                inputView.Arrange(new Rect(0, 0, 1280, 800));
                inputView.UpdateLayout();
            });

            // -- TEST 4: Instantiate OneClickOptimizationView --
            AssertTest("Instantiate OneClickOptimizationView & Layout", () =>
            {
                var view = new OneClickOptimizationView();
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.UpdateLayout();
            });

            // -- TEST 5: Instantiate TierView --
            AssertTest("Instantiate TierView & Layout", () =>
            {
                var view = new TierView();
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.UpdateLayout();
            });

            // -- TEST 6: Instantiate StorageView --
            AssertTest("Instantiate StorageView & Layout", () =>
            {
                var view = new StorageView();
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.UpdateLayout();
            });

            // -- TEST 7: Instantiate SystemInfoView --
            AssertTest("Instantiate SystemInfoView & Layout", () =>
            {
                var view = new SystemInfoView();
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.UpdateLayout();
            });

            // -- TEST 8: Instantiate DashboardView --
            AssertTest("Instantiate DashboardView & Layout", () =>
            {
                var view = new DashboardView();
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.UpdateLayout();
            });

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($" TOTAL XAML SCORECARD: {passed}/{total} ({(double)passed / total * 100:F1}%)");
            Console.WriteLine("================================================================================");

            if (passed != total)
            {
                Environment.Exit(1);
            }
        }
    }
}
