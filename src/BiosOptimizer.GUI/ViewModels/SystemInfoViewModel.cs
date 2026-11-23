#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Models;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels.Base;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.GUI.ViewModels
{
    public class SystemInfoViewModel : ViewModelBase
    {
        private readonly IIpcClient _ipc;
        private readonly SystemDiagnosticsReader _reader = new();
        private readonly SystemHealthAnalysisEngine _healthEngine = new();

        private string _status = "● SYSTEM INFORMATION READY";
        private bool _isScanning;
        private bool _isHealthModalOpen;
        private bool _isHealthScanning;
        private bool _isHealthScanCompleted;
        private string _healthModalStage = "HARDWARE ANALYSIS";
        private string _healthModalCheck = "Checking CPU Configuration";
        private int _healthModalProgress = 0;
        private string _healthModalProgressText = "0 / 18 CHECKS PROCESSED";
        private string _currentFindingFilter = "ALL";

        // Unified & Section Expansion States
        private bool _isTechnicalDetailsExpanded;
        public bool IsTechnicalDetailsExpanded
        {
            get => _isTechnicalDetailsExpanded;
            set
            {
                _isTechnicalDetailsExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TechnicalDetailsToggleText));
            }
        }
        public string TechnicalDetailsToggleText => IsTechnicalDetailsExpanded ? "HIDE TECHNICAL DETAILS ▲" : "VIEW TECHNICAL DETAILS ▼";

        private bool _isGpuDetailsExpanded;
        private bool _isCpuDetailsExpanded;
        private bool _isMemoryDetailsExpanded;
        private bool _isStorageDetailsExpanded;
        private bool _isSecurityDetailsExpanded;
        private bool _isWindowsDetailsExpanded;
        private bool _isNetworkDetailsExpanded;
        private bool _isPerformanceDetailsExpanded;

        public bool IsGpuDetailsExpanded { get => _isGpuDetailsExpanded; set { _isGpuDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsCpuDetailsExpanded { get => _isCpuDetailsExpanded; set { _isCpuDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsMemoryDetailsExpanded { get => _isMemoryDetailsExpanded; set { _isMemoryDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsStorageDetailsExpanded { get => _isStorageDetailsExpanded; set { _isStorageDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsSecurityDetailsExpanded { get => _isSecurityDetailsExpanded; set { _isSecurityDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsWindowsDetailsExpanded { get => _isWindowsDetailsExpanded; set { _isWindowsDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsNetworkDetailsExpanded { get => _isNetworkDetailsExpanded; set { _isNetworkDetailsExpanded = value; OnPropertyChanged(); } }
        public bool IsPerformanceDetailsExpanded { get => _isPerformanceDetailsExpanded; set { _isPerformanceDetailsExpanded = value; OnPropertyChanged(); } }

        // Structured Diagnostics
        private CompleteSystemDiagnostics _diagnostics = new();

        public CompleteSystemDiagnostics Diagnostics
        {
            get => _diagnostics;
            private set
            {
                _diagnostics = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Cpu));
                OnPropertyChanged(nameof(Memory));
                OnPropertyChanged(nameof(Storage));
                OnPropertyChanged(nameof(Motherboard));
                OnPropertyChanged(nameof(Windows));
                OnPropertyChanged(nameof(Security));
                OnPropertyChanged(nameof(Graphics));
                OnPropertyChanged(nameof(Power));
                OnPropertyChanged(nameof(Network));
                OnPropertyChanged(nameof(Drivers));
                OnPropertyChanged(nameof(HealthReport));
                OnPropertyChanged(nameof(PrimaryGpuSummary));
                OnPropertyChanged(nameof(StorageSummary));
                OnPropertyChanged(nameof(OverallHealthStatus));
                OnPropertyChanged(nameof(HealthBadgeBrush));
                OnPropertyChanged(nameof(HealthRingColor));
                OnPropertyChanged(nameof(HealthActionText));
                OnPropertyChanged(nameof(HealthActionSubtext));
                OnPropertyChanged(nameof(HealthTooltip));
                OnPropertyChanged(nameof(HardwareHealthStatus));
                OnPropertyChanged(nameof(WindowsHealthStatus));
                OnPropertyChanged(nameof(SecurityHealthStatus));
                OnPropertyChanged(nameof(StorageHealthStatus));
                OnPropertyChanged(nameof(PerformanceHealthStatus));
                OnPropertyChanged(nameof(DriversHealthStatus));
            }
        }

        public DetailedCpuInfo Cpu => Diagnostics.Cpu;
        public DetailedMemoryInfo Memory => Diagnostics.Memory;
        public DetailedStorageInfo Storage => Diagnostics.Storage;
        public DetailedMotherboardInfo Motherboard => Diagnostics.Motherboard;
        public DetailedWindowsInfo Windows => Diagnostics.Windows;
        public DetailedSecurityInfo Security => Diagnostics.Security;
        public DetailedGraphicsDiagnostics Graphics => Diagnostics.Graphics;
        public DetailedPowerInfo Power => Diagnostics.Power;
        public DetailedNetworkInfo Network => Diagnostics.Network;
        public DetailedDriverInfo Drivers => Diagnostics.Drivers;
        public SystemHealthReport HealthReport => Diagnostics.HealthReport;

        public ObservableCollection<DetailedGpuInfo> GpusList { get; } = new();
        public ObservableCollection<SystemHealthFinding> FilteredFindings { get; } = new();

        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public bool IsScanning { get => _isScanning; set { _isScanning = value; OnPropertyChanged(); } }

        public bool IsHealthModalOpen { get => _isHealthModalOpen; set { _isHealthModalOpen = value; OnPropertyChanged(); } }
        public bool IsHealthScanning
        {
            get => _isHealthScanning;
            set
            {
                _isHealthScanning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HealthRingColor));
                OnPropertyChanged(nameof(HealthActionText));
                OnPropertyChanged(nameof(HealthActionSubtext));
                OnPropertyChanged(nameof(HealthTooltip));
            }
        }
        public bool IsHealthScanCompleted
        {
            get => _isHealthScanCompleted;
            set
            {
                _isHealthScanCompleted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HealthActionText));
                OnPropertyChanged(nameof(HealthActionSubtext));
            }
        }

        public string HealthModalStage { get => _healthModalStage; set { _healthModalStage = value; OnPropertyChanged(); } }
        public string HealthModalCheck { get => _healthModalCheck; set { _healthModalCheck = value; OnPropertyChanged(); } }
        public int HealthModalProgress { get => _healthModalProgress; set { _healthModalProgress = value; OnPropertyChanged(); } }
        public string HealthModalProgressText { get => _healthModalProgressText; set { _healthModalProgressText = value; OnPropertyChanged(); } }

        public string CurrentFindingFilter
        {
            get => _currentFindingFilter;
            set
            {
                _currentFindingFilter = value;
                OnPropertyChanged();
                ApplyFindingsFilter();
            }
        }

        // Hero Summary & Health Ring Helpers
        public string OverallHealthStatus => IsHealthScanCompleted ? HealthReport.OverallRating : "NOT MEASURED";
        public string HealthBadgeBrush => OverallHealthStatus switch
        {
            "EXCELLENT" => "#10B981",
            "GOOD" => "#10B981",
            "WARNING" => "#F59E0B",
            "CRITICAL" => "#E53935",
            _ => "#6B7280"
        };

        public string HealthRingColor => IsHealthScanning ? "#60A5FA" : HealthBadgeBrush;

        public string HealthActionText => IsHealthScanning ? "ANALYZING..." :
            (IsHealthScanCompleted ? OverallHealthStatus : "SYSTEM HEALTH");

        public string HealthActionSubtext => IsHealthScanning ? "Diagnosing..." :
            (IsHealthScanCompleted ? $"{HealthReport.PassedChecks}/{HealthReport.TotalChecks} CHECKS PASS" : "RUN DIAGNOSTIC");

        public string HealthTooltip => IsHealthScanning
            ? "Real-time system diagnostics in progress..."
            : "Click to run a comprehensive system health analysis";

        // Subsystem Health Status Indicators for Health Hero
        public string HardwareHealthStatus => !IsHealthScanCompleted ? "NOT MEASURED" : (HealthReport.Findings.Any(f => f.Category == "Hardware" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ? "ATTENTION" : "HEALTHY");
        public string WindowsHealthStatus => !IsHealthScanCompleted ? "NOT MEASURED" : (HealthReport.Findings.Any(f => f.Category == "Windows" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ? "ATTENTION" : "HEALTHY");
        public string SecurityHealthStatus => !IsHealthScanCompleted ? "NOT MEASURED" : (HealthReport.Findings.Any(f => f.Category == "Security" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ? "ATTENTION" : "HEALTHY");
        public string StorageHealthStatus => !IsHealthScanCompleted ? "NOT MEASURED" : (HealthReport.Findings.Any(f => f.Category == "Storage" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ? "ATTENTION" : "HEALTHY");
        public string PerformanceHealthStatus => !IsHealthScanCompleted ? "NOT MEASURED" : (HealthReport.Findings.Any(f => f.Category == "Performance" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ? "ATTENTION" : "HEALTHY");
        public string DriversHealthStatus => !IsHealthScanCompleted ? "NOT MEASURED" : (HealthReport.Findings.Any(f => f.Category == "Drivers" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ? "ATTENTION" : "HEALTHY");

        public string PrimaryGpuSummary
        {
            get
            {
                if (Diagnostics.Gpus.Count == 0) return "Graphics Adapter";
                if (Diagnostics.Gpus.Count == 1) return Diagnostics.Gpus[0].Name;
                var ded = Diagnostics.Gpus.FirstOrDefault(g => g.AdapterType.Contains("DEDICATED"));
                var integ = Diagnostics.Gpus.FirstOrDefault(g => g.AdapterType.Contains("INTEGRATED"));
                if (ded != null && integ != null) return $"{ded.Name} + {integ.Name}";
                return string.Join(" + ", Diagnostics.Gpus.Select(g => g.Name));
            }
        }

        public string StorageSummary
        {
            get
            {
                var sys = Storage.Partitions.FirstOrDefault(p => p.IsSystemDrive) ?? Storage.Partitions.FirstOrDefault();
                if (sys != null)
                    return $"{sys.DriveLetter} ({sys.FreeText}, {sys.UsagePercentage:F0}% Used)";
                return "System Storage Ready";
            }
        }

        // Clean Essential Overview Summary Properties
        public string CpuCoresSummary
        {
            get
            {
                if (Cpu.PhysicalCores > 0 && Cpu.LogicalProcessors > 0)
                    return $"{Cpu.PhysicalCores} Cores • {Cpu.LogicalProcessors} Threads";
                if (Cpu.LogicalProcessors > 0)
                    return $"{Cpu.LogicalProcessors} Logical Processors";
                return Cpu.Architecture;
            }
        }

        public string PrimaryGpuVramSummary
        {
            get
            {
                var gpu = Diagnostics.Gpus.FirstOrDefault();
                if (gpu != null && !string.IsNullOrEmpty(gpu.DedicatedVramText) && gpu.DedicatedVramText != "LOADING...")
                    return $"{gpu.DedicatedVramText} Dedicated VRAM";
                return Graphics.DirectXVersion;
            }
        }

        public string MemoryCapacityUsageSummary
        {
            get
            {
                if (Memory.UsagePercentage > 0)
                    return $"{Memory.UsagePercentage:F0}% Used • {Memory.AvailableRamText} Free";
                return Memory.AvailableRamText;
            }
        }

        public string PrimaryStorageFreeSummary
        {
            get
            {
                var sys = Storage.Partitions.FirstOrDefault(p => p.IsSystemDrive) ?? Storage.Partitions.FirstOrDefault();
                if (sys != null && !string.IsNullOrEmpty(sys.FreeText) && sys.FreeText != "LOADING...")
                    return $"{sys.FreeText} Free ({sys.UsedText} Used)";
                return Storage.PrimaryStorageType;
            }
        }

        public string WindowsBuildSummary
        {
            get
            {
                if (!string.IsNullOrEmpty(Windows.Version) && Windows.Version != "LOADING...")
                    return $"Version {Windows.Version} • Build {Windows.OsBuild}";
                return $"Build {Windows.OsBuild}";
            }
        }

        public string PowerBatteryHealthSummary
        {
            get
            {
                if (Power.HasBattery)
                    return $"{Power.BatteryPercentage}% ({Power.BatteryHealth})";
                return Power.PowerPlan;
            }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ToggleTechnicalDetailsCommand { get; }
        public ICommand RunHealthAnalysisCommand { get; }
        public ICommand CloseHealthModalCommand { get; }
        public ICommand FilterFindingsCommand { get; }

        public ICommand ToggleGpuDetailsCommand { get; }
        public ICommand ToggleCpuDetailsCommand { get; }
        public ICommand ToggleMemoryDetailsCommand { get; }
        public ICommand ToggleStorageDetailsCommand { get; }
        public ICommand ToggleSecurityDetailsCommand { get; }
        public ICommand ToggleWindowsDetailsCommand { get; }
        public ICommand ToggleNetworkDetailsCommand { get; }
        public ICommand TogglePerformanceDetailsCommand { get; }

        private System.Threading.CancellationTokenSource? _scanCts;

        public SystemInfoViewModel(IIpcClient ipc)
        {
            _ipc = ipc;

            RefreshCommand = new RelayCommand(async _ => await RefreshDiagnosticsAsync());
            RunHealthAnalysisCommand = new RelayCommand(async _ => await RunHealthScanAsync());
            CloseHealthModalCommand = new RelayCommand(_ => IsHealthModalOpen = false);
            FilterFindingsCommand = new RelayCommand(param => CurrentFindingFilter = param?.ToString() ?? "ALL");

            ToggleGpuDetailsCommand = new RelayCommand(_ => IsGpuDetailsExpanded = !IsGpuDetailsExpanded);
            ToggleCpuDetailsCommand = new RelayCommand(_ => IsCpuDetailsExpanded = !IsCpuDetailsExpanded);
            ToggleMemoryDetailsCommand = new RelayCommand(_ => IsMemoryDetailsExpanded = !IsMemoryDetailsExpanded);
            ToggleStorageDetailsCommand = new RelayCommand(_ => IsStorageDetailsExpanded = !IsStorageDetailsExpanded);
            ToggleSecurityDetailsCommand = new RelayCommand(_ => IsSecurityDetailsExpanded = !IsSecurityDetailsExpanded);
            ToggleWindowsDetailsCommand = new RelayCommand(_ => IsWindowsDetailsExpanded = !IsWindowsDetailsExpanded);
            ToggleNetworkDetailsCommand = new RelayCommand(_ => IsNetworkDetailsExpanded = !IsNetworkDetailsExpanded);
            TogglePerformanceDetailsCommand = new RelayCommand(_ => IsPerformanceDetailsExpanded = !IsPerformanceDetailsExpanded);
            ToggleTechnicalDetailsCommand = new RelayCommand(_ => IsTechnicalDetailsExpanded = !IsTechnicalDetailsExpanded);
        }

        public override async Task OnNavigatedToAsync()
        {
            await RefreshDiagnosticsAsync();
        }

        public async Task RefreshDiagnosticsAsync()
        {
            _scanCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _scanCts = cts;

            Status = "● SCANNING SYSTEM HARDWARE & DIAGNOSTICS...";
            IsScanning = true;

            try
            {
                var newDiag = await Task.Run(() =>
                {
                    if (cts.Token.IsCancellationRequested) return null;
                    return _reader.ReadCompleteDiagnostics();
                }, cts.Token);

                if (newDiag == null || cts.Token.IsCancellationRequested) return;

                await UiDispatcher.RunAsync(() =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    Diagnostics = newDiag;
                    GpusList.Clear();
                    foreach (var g in newDiag.Gpus) GpusList.Add(g);

                    Status = "● SYSTEM INFORMATION READY";
                    IsScanning = false;
                });
            }
            catch (OperationCanceledException)
            {
                // Cleanly ignore cancellation
            }
            catch (Exception ex)
            {
                await UiDispatcher.RunAsync(() =>
                {
                    Status = "● ERROR READING SYSTEM INFORMATION: " + ex.Message;
                    IsScanning = false;
                });
            }
        }

        public async Task RunHealthScanAsync()
        {
            IsHealthModalOpen = true;
            IsHealthScanning = true;
            IsHealthScanCompleted = false;
            HealthModalProgress = 0;
            HealthModalStage = "HARDWARE ANALYSIS";
            HealthModalCheck = "Initializing Hardware & Security Scan Engine...";
            HealthModalProgressText = "0 / 18 CHECKS PROCESSED";

            var progress = new Progress<SystemHealthScanProgress>(p =>
            {
                UiDispatcher.RunAsync(() =>
                {
                    HealthModalStage = p.StageName;
                    HealthModalCheck = p.CurrentCheckTitle;
                    HealthModalProgress = p.ProgressPercent;
                    HealthModalProgressText = $"{p.CurrentCheckIndex} / {p.TotalChecks} CHECKS PROCESSED";
                });
            });

            try
            {
                var report = await _healthEngine.RunHealthAnalysisAsync(progress);

                await UiDispatcher.RunAsync(() =>
                {
                    Diagnostics.HealthReport = report;
                    OnPropertyChanged(nameof(HealthReport));
                    OnPropertyChanged(nameof(OverallHealthStatus));
                    OnPropertyChanged(nameof(HealthBadgeBrush));
                    OnPropertyChanged(nameof(HealthRingColor));
                    OnPropertyChanged(nameof(HealthActionText));
                    OnPropertyChanged(nameof(HealthActionSubtext));
                    OnPropertyChanged(nameof(HardwareHealthStatus));
                    OnPropertyChanged(nameof(WindowsHealthStatus));
                    OnPropertyChanged(nameof(SecurityHealthStatus));
                    OnPropertyChanged(nameof(StorageHealthStatus));
                    OnPropertyChanged(nameof(PerformanceHealthStatus));
                    OnPropertyChanged(nameof(DriversHealthStatus));

                    ApplyFindingsFilter();

                    HealthModalProgress = 100;
                    HealthModalProgressText = "18 / 18 CHECKS COMPLETED";
                    HealthModalStage = "FINAL REPORT";
                    HealthModalCheck = $"Diagnostic Complete — System Health: {report.OverallRating} ({report.OverallScore}/100)";
                    IsHealthScanning = false;
                    IsHealthScanCompleted = true;
                });
            }
            catch (Exception ex)
            {
                await UiDispatcher.RunAsync(() =>
                {
                    HealthModalCheck = "Health analysis error: " + ex.Message;
                    IsHealthScanning = false;
                    IsHealthScanCompleted = true;
                });
            }
        }

        private void ApplyFindingsFilter()
        {
            FilteredFindings.Clear();
            var all = HealthReport.Findings;
            if (all == null) return;

            foreach (var f in all)
            {
                if (CurrentFindingFilter == "ALL" ||
                    (CurrentFindingFilter == "WARNINGS" && (f.Severity == "WARNING" || f.Severity == "CRITICAL")) ||
                    (CurrentFindingFilter == "PASS" && f.Severity == "PASS") ||
                    string.Equals(f.Category, CurrentFindingFilter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredFindings.Add(f);
                }
            }
        }
    }
}
