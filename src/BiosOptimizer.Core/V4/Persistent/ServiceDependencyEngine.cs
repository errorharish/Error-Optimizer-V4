#nullable enable
using System;
using System.Collections.Generic;
using System.ServiceProcess;
using BiosOptimizer.IPC.Contracts;

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
        public string Category { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Analyzes Windows Services, dependencies, and start types to safely reduce persistent background service workloads.
    /// </summary>
    public class ServiceDependencyEngine
    {
        // Safe candidate services for Maximum Performance gaming & editing systems (telemetry, feedback, diagnostic tracing, retail demo, remote registry)
        private static readonly Dictionary<string, string> ReducibleBackgroundServices = new(StringComparer.OrdinalIgnoreCase)
        {
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
            ["TapiSrv"] = "Telephony Service"
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
                        bool isProtected = StrictlyProtectedServices.Contains(sName);
                        bool isReducible = ReducibleBackgroundServices.ContainsKey(sName);

                        string category = "General";
                        string reason = "Standard background service";

                        if (isProtected)
                        {
                            category = "Protected";
                            reason = "Critical Windows subsystem / Security / Driver dependency";
                        }
                        else if (isReducible)
                        {
                            category = "Reducible";
                            reason = ReducibleBackgroundServices[sName];
                        }

                        results.Add(new ServiceIntelligenceItem
                        {
                            ServiceName = sName,
                            DisplayName = dName,
                            Status = s.Status.ToString(),
                            StartType = s.StartType.ToString(),
                            IsProtected = isProtected,
                            IsReducible = isReducible,
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
                if (s.IsReducible && s.StartType != "Disabled")
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
                        Reason = $"Disable unnecessary background telemetry/tracking service: {s.Reason}",
                        SafetyConfidence = 0.95,
                        IsSelected = true
                    });
                }
            }

            return candidates;
        }
    }
}
