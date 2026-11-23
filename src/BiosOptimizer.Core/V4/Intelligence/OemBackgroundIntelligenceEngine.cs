#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Intelligence
{
    public enum OemVendor
    {
        Unknown,
        Lenovo,
        HP,
        Dell,
        ASUS,
        MSI,
        Acer,
        Intel,
        Nvidia
    }

    public enum OemComponentType
    {
        Service,
        ScheduledTask,
        StartupEntry,
        Process
    }

    public class OemComponentInfo
    {
        public OemVendor Vendor { get; set; } = OemVendor.Unknown;
        public string ComponentName { get; set; } = string.Empty;
        public OemComponentType ComponentType { get; set; }
        public string TargetIdentifier { get; set; } = string.Empty;
        public bool IsProtected { get; set; }
        public string ProtectionReason { get; set; } = string.Empty;
        public bool IsReducible { get; set; }
        public string ReducibleReason { get; set; } = string.Empty;
        public string CurrentState { get; set; } = string.Empty;
        public string TargetState { get; set; } = string.Empty;
        public bool IsAlreadyOptimal { get; set; }
    }

    /// <summary>
    /// Vector 5: OEM Background Component Optimization Engine.
    /// Discovers OEM background telemetry, updaters, and analytics across Lenovo, HP, Dell, ASUS, MSI, Acer, Intel, and NVIDIA.
    /// Strictly protects thermal, fan, power management, GPU display containers, hotkeys, drivers, and active workloads.
    /// </summary>
    public class OemBackgroundIntelligenceEngine
    {
        private static readonly Lazy<OemBackgroundIntelligenceEngine> _lazy = new(() => new OemBackgroundIntelligenceEngine());
        public static OemBackgroundIntelligenceEngine Instance => _lazy.Value;

        // Strictly protected OEM services across vendors (thermal, fan, battery, display, hotkeys, driver dependencies)
        private static readonly Dictionary<string, string> StrictlyProtectedOemServices = new(StringComparer.OrdinalIgnoreCase)
        {
            // Lenovo
            ["LenovoCooling"] = "Lenovo Intelligent Cooling / Fan Management",
            ["LenovoPower"] = "Lenovo Power Management Driver Subsystem",
            ["LenovoBattery"] = "Lenovo Battery Conservation Threshold Manager",
            ["LenovoHotkeys"] = "Lenovo Fn / Hotkey OSD Subsystem",
            ["LITSSvc"] = "Lenovo Intelligent Thermal Solution Service",
            ["Lnvvgc"] = "Lenovo Vantage Gaming Controller Driver Interface",
            ["IBMPMSVC"] = "Lenovo / IBM Power Management Service",

            // HP
            ["HPCoolSense"] = "HP CoolSense Thermal & Accelerometer Management",
            ["HPPowerManager"] = "HP Power Manager Service",
            ["HPHotkey"] = "HP Hotkey UWP Service",
            ["CaslExec"] = "HP Common API Service Layer (Hardware Hotkeys)",

            // Dell
            ["DellPowerManager"] = "Dell Power Manager Thermal & Battery Profiles",
            ["DellThermal"] = "Dell Thermal Management Service",
            ["AWCCService"] = "Alienware Command Center Thermal / Fan Engine",

            // ASUS
            ["ASUSOptimization"] = "ASUS System Optimization / Hotkeys / Power",
            ["AsusFanControlService"] = "ASUS Fan Control & Dynamic Thermal Engine",
            ["ASUSSwitch"] = "ASUS Graphics MUX Switcher",
            ["ASUSSystemControl"] = "ASUS System Control Interface Driver Service",

            // MSI
            ["MSICoolerBoost"] = "MSI Cooler Boost Thermal Service",
            ["MSIDragonPower"] = "MSI Power Mode Management",

            // Acer
            ["AcerCoolBoost"] = "Acer CoolBoost Fan Management Service",
            ["AcerThermal"] = "Acer Dynamic Thermal Management",

            // Intel
            ["IPF"] = "Intel Innovation Platform Framework (Thermal / Energy)",
            ["Intel(R) Innovation Platform Framework"] = "Intel Innovation Platform Framework Service",
            ["DTT"] = "Intel Dynamic Tuning Technology",
            ["Intel(R) Dynamic Tuning Technology"] = "Intel Dynamic Tuning Technology Service",
            ["cplspcon"] = "Intel Content Protection HECI Service",
            ["igfxCUIService2.0.0.0"] = "Intel Graphics Command Center Core Display Service",

            // NVIDIA
            ["NVDisplay.ContainerLocalSystem"] = "NVIDIA Display Driver Container (Display Driver Core)",
            ["NvContainerLocalSystem"] = "NVIDIA LocalSystem Display Container"
        };

        // Reducible OEM Services across vendors (optional telemetry, background updaters, usage analytics)
        private static readonly Dictionary<string, (OemVendor Vendor, string Reason)> ReducibleOemServices = new(StringComparer.OrdinalIgnoreCase)
        {
            // Lenovo
            ["LenovoVantageTelemetry"] = (OemVendor.Lenovo, "Lenovo Vantage Background Telemetry Collector"),
            ["Lenovo.UsageStats"] = (OemVendor.Lenovo, "Lenovo Device Usage Statistics Collector"),
            ["LenovoAppExplorerUpdater"] = (OemVendor.Lenovo, "Lenovo App Explorer Promotional Updater"),

            // HP
            ["HPAppHelperCap"] = (OemVendor.HP, "HP Application Helper Capability Telemetry"),
            ["HPNetworkCap"] = (OemVendor.HP, "HP Network Capability Background Analytics"),
            ["HPSupportSolutionsFrameworkService"] = (OemVendor.HP, "HP Support Solutions Framework Polling"),
            ["HPPrintScanDoctorService"] = (OemVendor.HP, "HP Print & Scan Doctor Background Updater"),
            ["TouchpointAnalyticsClientService"] = (OemVendor.HP, "HP Touchpoint Analytics Telemetry Client"),

            // Dell
            ["DellDataVault"] = (OemVendor.Dell, "Dell Data Vault Telemetry Collector"),
            ["DellDataVaultWdm"] = (OemVendor.Dell, "Dell Data Vault WDM Telemetry Broker"),
            ["DellSupportAssist"] = (OemVendor.Dell, "Dell SupportAssist Background Telemetry Agent"),
            ["DellTechHub"] = (OemVendor.Dell, "Dell TechHub Background Analytics Collector"),
            ["DellDigitalDelivery"] = (OemVendor.Dell, "Dell Digital Delivery Background Downloader"),

            // ASUS
            ["AsusSystemAnalysis"] = (OemVendor.ASUS, "ASUS System Analysis Background Telemetry"),
            ["AsusSoftwareManager"] = (OemVendor.ASUS, "ASUS Software Manager Background Updater"),
            ["ArmouryCrate.ServiceEngine"] = (OemVendor.ASUS, "ASUS Armoury Crate Background Analytics Engine"),
            ["ASUSLinkNear"] = (OemVendor.ASUS, "ASUS Link Near Background Peer Discovery"),

            // MSI
            ["MSICenterService"] = (OemVendor.MSI, "MSI Center Background Telemetry Collector"),
            ["MSIAppPlayerHelper"] = (OemVendor.MSI, "MSI App Player Background Helper"),

            // Acer
            ["AcerCareCenterService"] = (OemVendor.Acer, "Acer Care Center Background Telemetry"),
            ["AcerRegistrationService"] = (OemVendor.Acer, "Acer Registration Background Polling"),

            // Intel
            ["SurSvc"] = (OemVendor.Intel, "Intel System Usage Report (SUR) Telemetry Service"),
            ["esrv_svc"] = (OemVendor.Intel, "Intel System Energy Server Telemetry Service"),
            ["DSAService"] = (OemVendor.Intel, "Intel Driver & Support Assistant Background Polling"),
            ["IntelGraphicsSoftware.Service"] = (OemVendor.Intel, "Intel Arc Graphics Background Service"),
            ["IntelGraphicsSoftware.Service.Graphics"] = (OemVendor.Intel, "Intel Arc Graphics Telemetry Broker"),
            ["PresentMonService"] = (OemVendor.Intel, "Intel PresentMon Background Capture Service"),

            // NVIDIA
            ["NvTelemetryContainer"] = (OemVendor.Nvidia, "NVIDIA Telemetry Container (Standalone Telemetry)"),

            // OEM Helpers & Analytics (Core hotkey, audio & fan drivers strictly protected)
            ["NhNotifSys"] = (OemVendor.Lenovo, "Nahimic Notification System Helper (Audio Driver Preserved)"),
            ["SEGameTool"] = (OemVendor.Lenovo, "OEM Game Tool Background Analytics")
        };

        // Reducible OEM Scheduled Tasks
        private static readonly Dictionary<string, (OemVendor Vendor, string Reason)> ReducibleOemTasks = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"\Lenovo\CustomerFeedback"] = (OemVendor.Lenovo, "Lenovo Customer Feedback Survey Schedule"),
            [@"\Lenovo\Vantage\Schedule\LenovoVantageEventSchedule"] = (OemVendor.Lenovo, "Lenovo Vantage Event Telemetry Schedule"),
            [@"\HP\HP Touchpoint Analytics Client"] = (OemVendor.HP, "HP Touchpoint Analytics Scheduled Telemetry"),
            [@"\Hewlett-Packard\HP Support Assistant\HP Support Solutions Framework Report"] = (OemVendor.HP, "HP Support Framework Diagnostic Report"),
            [@"\Dell\DellDataVault\DataVaultCollection"] = (OemVendor.Dell, "Dell Data Vault Telemetry Collection"),
            [@"\Dell\SupportAssist\SupportAssistAgentAutoUpdate"] = (OemVendor.Dell, "Dell SupportAssist Auto-Updater"),
            [@"\ASUS\ASUS System Analysis"] = (OemVendor.ASUS, "ASUS System Analysis Background Task"),
            [@"\ASUS\Armoury Crate\ExperienceProgram"] = (OemVendor.ASUS, "ASUS User Experience Improvement Task"),
            [@"\MSI\MSI Center\UpdateTask"] = (OemVendor.MSI, "MSI Center Scheduled Update Check"),
            [@"\Acer\CareCenter\UpdateCheck"] = (OemVendor.Acer, "Acer Care Center Scheduled Update Check"),
            [@"\Intel\Sur\SurHost"] = (OemVendor.Intel, "Intel Computing Improvement Program (SUR) Host"),
            [@"\NvTmMon"] = (OemVendor.Nvidia, "NVIDIA Telemetry Monitor Task"),
            [@"\NvTmRep"] = (OemVendor.Nvidia, "NVIDIA Telemetry Crash / Usage Reporter Task")
        };

        /// <summary>
        /// Detects active OEM vendors present on the current machine (hardware manufacturer, CPU vendor, GPU vendor).
        /// </summary>
        public HashSet<OemVendor> DetectActiveVendors()
        {
            var vendors = new HashSet<OemVendor>();
            try
            {
                var hw = HardwareDetectionService.Instance.GetSnapshot();
                string mfg = (hw.Manufacturer ?? string.Empty).ToLowerInvariant();
                string model = (hw.Model ?? string.Empty).ToLowerInvariant();
                string cpu = (hw.Cpu.Name ?? string.Empty).ToLowerInvariant();
                string gpu = (hw.PrimaryGpu.Name ?? string.Empty).ToLowerInvariant();

                // 1. System Manufacturer
                if (mfg.Contains("lenovo") || model.Contains("lenovo")) vendors.Add(OemVendor.Lenovo);
                if (mfg.Contains("hp") || mfg.Contains("hewlett") || model.Contains("hp")) vendors.Add(OemVendor.HP);
                if (mfg.Contains("dell") || mfg.Contains("alienware")) vendors.Add(OemVendor.Dell);
                if (mfg.Contains("asus") || mfg.Contains("asustek")) vendors.Add(OemVendor.ASUS);
                if (mfg.Contains("msi") || mfg.Contains("micro-star")) vendors.Add(OemVendor.MSI);
                if (mfg.Contains("acer")) vendors.Add(OemVendor.Acer);

                // 2. CPU Vendor
                if (cpu.Contains("intel") || hw.Cpu.Architecture.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    vendors.Add(OemVendor.Intel);

                // 3. GPU Vendor
                if (gpu.Contains("nvidia") || gpu.Contains("geforce") || gpu.Contains("rtx") || gpu.Contains("gtx"))
                    vendors.Add(OemVendor.Nvidia);
                if (gpu.Contains("intel") || gpu.Contains("arc") || gpu.Contains("iris") || gpu.Contains("uhd"))
                    vendors.Add(OemVendor.Intel);
            }
            catch { }

            return vendors;
        }

        /// <summary>
        /// Scans live OEM services, scheduled tasks, and startup entries across all detected and active OEM vendors.
        /// </summary>
        public List<OemComponentInfo> DiscoverOemComponents()
        {
            var components = new List<OemComponentInfo>();
            var activeVendors = DetectActiveVendors();

            // 1. Scan Live Services
            try
            {
                var services = ServiceController.GetServices();
                foreach (var s in services)
                {
                    try
                    {
                        string sName = s.ServiceName;
                        string dName = s.DisplayName;

                        // Check strict OEM protection first
                        if (StrictlyProtectedOemServices.TryGetValue(sName, out var protReason) ||
                            StrictlyProtectedOemServices.Any(p => sName.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            components.Add(new OemComponentInfo
                            {
                                Vendor = ResolveVendorForService(sName),
                                ComponentName = dName,
                                ComponentType = OemComponentType.Service,
                                TargetIdentifier = sName,
                                IsProtected = true,
                                ProtectionReason = protReason ?? "Essential OEM thermal / power / display hardware management",
                                IsReducible = false,
                                CurrentState = s.Status.ToString(),
                                TargetState = s.Status.ToString(),
                                IsAlreadyOptimal = true
                            });
                            continue;
                        }

                        // Check reducible OEM services
                        if (ReducibleOemServices.TryGetValue(sName, out var redInfo) ||
                            ReducibleOemServices.Any(r => sName.StartsWith(r.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            var info = redInfo.Vendor != OemVendor.Unknown
                                ? redInfo
                                : ReducibleOemServices.First(r => sName.StartsWith(r.Key, StringComparison.OrdinalIgnoreCase)).Value;

                            string currentStart = "Unknown";
                            try
                            {
                                using var sKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sName}");
                                var startVal = sKey?.GetValue("Start");
                                currentStart = startVal switch
                                {
                                    2 => "Automatic",
                                    3 => "Manual",
                                    4 => "Disabled",
                                    _ => s.StartType.ToString()
                                };
                            }
                            catch
                            {
                                currentStart = s.StartType.ToString();
                            }

                            bool isAlreadyOptimal = (currentStart == "Disabled" || currentStart == "Manual") &&
                                                   (s.Status == ServiceControllerStatus.Stopped || s.Status == ServiceControllerStatus.StopPending);

                            components.Add(new OemComponentInfo
                            {
                                Vendor = info.Vendor,
                                ComponentName = dName,
                                ComponentType = OemComponentType.Service,
                                TargetIdentifier = sName,
                                IsProtected = false,
                                IsReducible = true,
                                ReducibleReason = info.Reason,
                                CurrentState = currentStart,
                                TargetState = "Disabled",
                                IsAlreadyOptimal = isAlreadyOptimal
                            });
                        }
                    }
                    catch { }
                    finally
                    {
                        s.Dispose();
                    }
                }
            }
            catch { }

            // 2. Scan Live Scheduled Tasks
            try
            {
                var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType != null)
                {
                    dynamic? scheduler = Activator.CreateInstance(schedulerType);
                    if (scheduler != null)
                    {
                        scheduler.Connect();
                        foreach (var kvp in ReducibleOemTasks)
                        {
                            try
                            {
                                string taskPath = kvp.Key;
                                string folderPath = Path.GetDirectoryName(taskPath)?.Replace('/', '\\') ?? "\\";
                                string taskName = Path.GetFileName(taskPath);

                                dynamic folder = scheduler.GetFolder(folderPath);
                                dynamic task = folder.GetTask(taskName);
                                if (task != null)
                                {
                                    bool isEnabled = task.Enabled;
                                    components.Add(new OemComponentInfo
                                    {
                                        Vendor = kvp.Value.Vendor,
                                        ComponentName = taskName,
                                        ComponentType = OemComponentType.ScheduledTask,
                                        TargetIdentifier = taskPath,
                                        IsProtected = false,
                                        IsReducible = true,
                                        ReducibleReason = kvp.Value.Reason,
                                        CurrentState = isEnabled ? "Enabled" : "Disabled",
                                        TargetState = "Disabled",
                                        IsAlreadyOptimal = !isEnabled
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return components;
        }

        private static OemVendor ResolveVendorForService(string serviceName)
        {
            if (serviceName.StartsWith("Lenovo", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("Lnv", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("IBMPM", StringComparison.OrdinalIgnoreCase))
                return OemVendor.Lenovo;
            if (serviceName.StartsWith("HP", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("Casl", StringComparison.OrdinalIgnoreCase))
                return OemVendor.HP;
            if (serviceName.StartsWith("Dell", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("AWCC", StringComparison.OrdinalIgnoreCase))
                return OemVendor.Dell;
            if (serviceName.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("Armoury", StringComparison.OrdinalIgnoreCase))
                return OemVendor.ASUS;
            if (serviceName.StartsWith("MSI", StringComparison.OrdinalIgnoreCase))
                return OemVendor.MSI;
            if (serviceName.StartsWith("Acer", StringComparison.OrdinalIgnoreCase))
                return OemVendor.Acer;
            if (serviceName.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("IPF", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("DTT", StringComparison.OrdinalIgnoreCase) || serviceName.StartsWith("cpls", StringComparison.OrdinalIgnoreCase))
                return OemVendor.Intel;
            if (serviceName.StartsWith("NV", StringComparison.OrdinalIgnoreCase))
                return OemVendor.Nvidia;
            return OemVendor.Unknown;
        }

        /// <summary>
        /// Generates actionable OptimizationCandidateDto items for eligible OEM components.
        /// Excludes already-optimal, protected, and ineligible items.
        /// </summary>
        public List<OptimizationCandidateDto> GenerateCandidates(List<OemComponentInfo> components)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var c in components)
            {
                // Strict Safety Check
                if (c.IsProtected || c.IsAlreadyOptimal || !c.IsReducible)
                    continue;

                // Pre-execution SafetyKernel Check
                var candidate = new OptimizationCandidateDto
                {
                    CandidateId = $"OEM_{c.Vendor}_{c.ComponentType}_{c.TargetIdentifier}",
                    TargetType = c.ComponentType switch
                    {
                        OemComponentType.Service => "Service",
                        OemComponentType.ScheduledTask => "ScheduledTask",
                        OemComponentType.StartupEntry => "StartupEntry",
                        _ => "OemComponent"
                    },
                    TargetIdentifier = c.TargetIdentifier,
                    DisplayName = $"OEM [{c.Vendor}]: {c.ComponentName}",
                    Category = "OEM Background Workload",
                    Subcategory = c.Vendor.ToString(),
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    InitialState = c.CurrentState,
                    TargetState = c.TargetState,
                    Reason = c.ReducibleReason,
                    SafetyConfidence = 0.95,
                    IsSelected = true,
                    IsPersistent = true,
                    WorkloadCompatibility = "All",
                    Classification = "Recommended",
                    ExpectedBenefit = $"Disables unnecessary OEM background telemetry / updater: {c.ReducibleReason}",
                    PotentialRisk = "OEM telemetry and automatic background checks paused; core hardware features remain unaffected.",
                    RequiredPermissions = "Administrator / Background Service",
                    ReversibilityStatus = "Reversible via Rollback Journal",
                    BackendHandler = "OemBackgroundIntelligenceEngine",
                    VerificationMethod = c.ComponentType switch
                    {
                        OemComponentType.Service => "Service SCM Status & Start Registry Query",
                        OemComponentType.ScheduledTask => "Task Scheduler Enabled Query",
                        _ => "OS State Query"
                    },
                    EvidenceQuality = "measured",
                    EligibilityExplanation = $"Non-essential OEM component from {c.Vendor}. Thermal, power, and display controls are strictly shielded."
                };

                candidates.Add(candidate);
            }

            return candidates;
        }
    }
}
