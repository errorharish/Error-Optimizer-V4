#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Persistent
{
    public class ServiceIntelligenceItem
    {
        public string ServiceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StartType { get; set; } = string.Empty;
        public bool IsProtected { get; set; }
        public bool IsReducible { get; set; }
        public bool IsAlreadyOptimal { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Vector 1: Analyzes Windows Services, dependencies, and start types to safely reduce persistent background service workloads.
    /// Strictly protects kernel drivers, security, audio, power, display, and services with active dependents.
    /// </summary>
    public class ServiceDependencyEngine
    {
        // Safe candidate services for Maximum Performance gaming & editing systems (telemetry, feedback, diagnostic tracing, retail demo, remote registry, third-party updaters, OEM bloat)
        // NOTE: Protected application services (Adobe, Maxon, Red Giant, Topaz, DaVinci Resolve, UltraViewer, Spotify, Urban VPN, Steam, Lenovo Vantage) are strictly excluded.
        private static readonly Dictionary<string, string> ReducibleBackgroundServices = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows Telemetry & Diagnostics
            ["DiagTrack"] = "Connected User Experiences and Telemetry",
            ["dmwappushservice"] = "Device Management Wireless Application Protocol Push message Routing Service",
            ["RetailDemo"] = "Windows Retail Demo Service",
            ["MapsBroker"] = "Downloaded Maps Manager Service",
            ["lfsvc"] = "Geolocation Service",
            ["SensorService"] = "Sensor Service",
            ["SensrSvc"] = "Sensor Monitoring Service",
            ["SensorDataService"] = "Sensor Data Service",
            ["wisvc"] = "Windows Insider Service",
            ["RemoteRegistry"] = "Remote Registry Service",
            ["WerSvc"] = "Windows Error Reporting Service",
            ["PcaSvc"] = "Program Compatibility Assistant Service",
            ["SharedAccess"] = "Internet Connection Sharing",
            ["TapiSrv"] = "Telephony Service",
            ["RpcLocator"] = "Remote Procedure Call (RPC) Locator Service",
            ["SSDPSRV"] = "SSDP Discovery Service",
            ["OneSyncSvc"] = "Windows Sync Host Service",
            ["UdkUserSvc"] = "User Data Access / UDK Service",
            ["WpnUserService"] = "Windows Push Notifications User Service",
            ["InventorySvc"] = "Windows Inventory and Compatibility Collector",
            ["whesvc"] = "Windows Health Experience Service",
            ["WSAIFabricSvc"] = "Windows Subsystem for Android / AI Fabric Service",
            ["smphost"] = "Microsoft Storage Spaces SMP Service",

            // Third-Party Background Updaters & Helpers (Protected vendors excluded)
            ["LGHUBUpdaterService"] = "Logitech G HUB Background Updater Service",
            ["logi_lamparray_service"] = "Logitech LampArray Background Service",
            ["GoogleUpdate"] = "Google Update Service",
            ["gupdate"] = "Google Update Service (gupdate)",
            ["gupdatem"] = "Google Update Service (gupdatem)",
            ["edgeupdate"] = "Microsoft Edge Update Service",
            ["edgeupdatem"] = "Microsoft Edge Update Service (edgeupdatem)",
            ["CCleaner Crash Reporting"] = "CCleaner Crash Reporting Service",
            ["BraveElevationService"] = "Brave Elevation Service",
            ["Opera Scheduled Autoupdate"] = "Opera Scheduled Autoupdate Service",
            ["DropboxUpdate"] = "Dropbox Update Service",
            ["EpicOnlineServices"] = "Epic Online Services Background Host",

            // OEM Analytics & Peripheral Services (Lenovo Vantage excluded)
            ["HPAppHelperCap"] = "HP Application Helper Service",
            ["HPNetworkCap"] = "HP Network Capability Service",
            ["HPSupportSolutionsFrameworkService"] = "HP Support Solutions Framework",
            ["HPPrintScanDoctorService"] = "HP Print and Scan Doctor",
            ["DellDataVault"] = "Dell Data Vault Collector Service",
            ["DellSupportAssist"] = "Dell SupportAssist Background Service",
            ["AsusSystemAnalysis"] = "ASUS System Analysis Service",
            ["AsusSoftwareManager"] = "ASUS Software Manager",
            ["ArmouryCrate.ServiceEngine"] = "ASUS Armoury Crate Background Engine",
            ["LogiAnalytics"] = "Logitech Analytics Service",
            ["RazerSynapseService"] = "Razer Synapse Background Service"
        };

        private static readonly HashSet<string> StrictlyProtectedServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "SecurityHealthService", "mpssvc", "AudioSrv", "AudioEndpointBuilder",
            "netprofm", "Dhcp", "Dnscache", "EventLog", "EventSystem", "Power", "PlugPlay",
            "RpcSs", "RpcEptMapper", "DcomLaunch", "BrokerInfrastructure", "SystemEventsBroker",
            "TimeBrokerSvc", "Themes", "Winmgmt", "Schedule", "CryptSvc", "LanmanWorkstation",
            "LanmanServer", "ProfSvc", "gpsvc", "LSM", "FontCache", "StateRepository", "BFE",
            "CoreMessagingRegistrar", "InstallService", "KeyIso", "SamSs", "VaultSvc", "W32Time",
            "nvcontainer", "NVDisplay.Container", "AMDRSServ", "RtkAudioService64", "EasyAntiCheat",
            "BEService", "vgc"
        };

        public static bool IsServiceReducible(string serviceName, string? displayName = null)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                return false;
            if (StrictlyProtectedServices.Contains(serviceName))
                return false;
            if (ProtectedApplicationPolicy.Instance.IsServiceProtected(serviceName, displayName))
                return false;
            return ReducibleBackgroundServices.ContainsKey(serviceName);
        }

        public List<ServiceIntelligenceItem> ScanServices()
        {
            var results = new List<ServiceIntelligenceItem>();

            try
            {
                var services = ServiceController.GetServices();
                foreach (var s in services)
                {
                    try
                    {
                        string sName = s.ServiceName;
                        string dName = s.DisplayName;
                        string category = "General";
                        string reason = "Standard background service";

                        var protectedAppMatch = ProtectedApplicationPolicy.Instance.EvaluateService(sName, dName);
                        bool isProtected = StrictlyProtectedServices.Contains(sName) || protectedAppMatch.IsProtected;
                        bool isReducible = !isProtected && ReducibleBackgroundServices.TryGetValue(sName, out var reducibleReason);
                        string? matchedReducibleReason = isReducible ? ReducibleBackgroundServices[sName] : null;

                        if (!isProtected && !isReducible)
                        {
                            foreach (var kvp in ReducibleBackgroundServices)
                            {
                                if (sName.StartsWith(kvp.Key + "_", StringComparison.OrdinalIgnoreCase))
                                {
                                    isReducible = true;
                                    matchedReducibleReason = kvp.Value;
                                    break;
                                }
                            }
                        }

                        // Check running dependent services
                        if (!isProtected && isReducible)
                        {
                            try
                            {
                                if (s.DependentServices.Any(dep => dep.Status == ServiceControllerStatus.Running &&
                                    (StrictlyProtectedServices.Contains(dep.ServiceName) || ProtectedApplicationPolicy.Instance.IsServiceProtected(dep.ServiceName, dep.DisplayName))))
                                {
                                    isProtected = true;
                                    isReducible = false;
                                    category = "PROTECTED_SERVICE";
                                    reason = "Has active protected running dependent services";
                                }
                            }
                            catch { }
                        }

                        // Read authoritative start type from registry
                        string startTypeStr = s.StartType.ToString();
                        int regStart = -1;
                        try
                        {
                            using var sKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sName}");
                            if (sKey?.GetValue("Start") is int si)
                            {
                                regStart = si;
                                startTypeStr = si switch
                                {
                                    2 => "Automatic",
                                    3 => "Manual",
                                    4 => "Disabled",
                                    _ => startTypeStr
                                };
                            }
                        }
                        catch { }

                        bool isAlreadyOptimal = (regStart == 4 || startTypeStr.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) &&
                                               (s.Status == ServiceControllerStatus.Stopped || s.Status == ServiceControllerStatus.StopPending);

                        if (protectedAppMatch.IsProtected)
                        {
                            category = "PROTECTED_SERVICE";
                            reason = protectedAppMatch.Reason;
                        }
                        else if (isProtected)
                        {
                            category = "Protected";
                            reason = "Critical Windows subsystem / Security / Driver dependency";
                        }
                        else if (isReducible)
                        {
                            category = "Reducible";
                            reason = matchedReducibleReason ?? "Non-essential background service";
                        }

                        results.Add(new ServiceIntelligenceItem
                        {
                            ServiceName = sName,
                            DisplayName = dName,
                            Status = s.Status.ToString(),
                            StartType = startTypeStr,
                            IsProtected = isProtected,
                            IsReducible = isReducible,
                            IsAlreadyOptimal = isAlreadyOptimal,
                            Category = category,
                            Reason = reason
                        });
                    }
                    catch { }
                    finally
                    {
                        s.Dispose();
                    }
                }
            }
            catch { }

            return results;
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<ServiceIntelligenceItem> services)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var s in services)
            {
                if (s.IsProtected || s.IsAlreadyOptimal || ProtectedApplicationPolicy.Instance.IsServiceProtected(s.ServiceName, s.DisplayName))
                {
                    continue;
                }

                if (s.IsReducible && (s.StartType != "Disabled" || s.Status == "Running"))
                {
                    candidates.Add(new OptimizationCandidateDto
                    {
                        CandidateId = $"SVC_{s.ServiceName}",
                        TargetType = "Service",
                        TargetIdentifier = s.ServiceName,
                        DisplayName = $"Service: {s.DisplayName}",
                        Category = "Services",
                        RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                        InitialState = s.StartType,
                        TargetState = "Disabled",
                        Reason = $"Disable unnecessary background service: {s.Reason}",
                        SafetyConfidence = 0.95,
                        IsSelected = true,
                        IsPersistent = true
                    });
                }
            }

            return candidates;
        }
    }
}
