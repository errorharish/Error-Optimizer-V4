using BiosOptimizer.Core.V4.Recommendations;
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// Master registry of non-terminable, non-stoppable system components.
    /// Loaded at startup from data/hard_protect_services.json and data/hard_protect_processes.json
    /// with embedded structural fallbacks.
    /// Guarantees connectivity, audio, display, storage, input, security, and power thermal safety.
    /// </summary>
    public class HardProtectRegistry
    {
        private static readonly Lazy<HardProtectRegistry> _instance = new(() => new HardProtectRegistry());
        public static HardProtectRegistry Instance => _instance.Value;

        // Service sets (case-insensitive)
        private readonly HashSet<string> _connectivityServices = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _audioServices = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _storageServices = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _securityServices = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _laptopPowerThermalServices = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _gpuServices = new(StringComparer.OrdinalIgnoreCase);

        // Process sets & patterns (case-insensitive)
        private readonly HashSet<string> _protectedProcessNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _connectivityModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _connectivityPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _connectivityNamePrefixes = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _audioModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _audioProcesses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _audioKeywords = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _displayGpuModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _displayGpuProcesses = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _storageProcesses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _inputProcesses = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _securityProcesses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _securityModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _securityVendors = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<string>> _laptopPowerThermalProcesses = new(StringComparer.OrdinalIgnoreCase);

        public int ConnectivityServicesCount => _connectivityServices.Count;
        public int AudioServicesCount => _audioServices.Count;
        public int StorageServicesCount => _storageServices.Count;
        public int SecurityProcessesCount => _securityProcesses.Count + _securityVendors.Count;
        public int GpuProcessesCount => _displayGpuProcesses.Count;
        public int StorageProcessesCount => _storageProcesses.Count;
        public int InputProcessesCount => _inputProcesses.Count;
        public int PowerThermalServicesCount => _laptopPowerThermalServices.Count;

        public static bool IsServiceProtected(string serviceName, bool isLaptop = false, string? oem = null)
            => Instance.IsServiceHardProtected(serviceName, isLaptop, oem);

        public static bool IsProtected(string target, out string reason)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                reason = string.Empty;
                return false;
            }
            if (IsServiceProtected(target))
            {
                reason = $"Target '{target}' is a protected Windows service.";
                return true;
            }
            if (IsProcessProtected(target))
            {
                reason = $"Target '{target}' is a protected Windows process.";
                return true;
            }
            if (target.Contains(@"\Services\stornvme", StringComparison.OrdinalIgnoreCase))
            {
                reason = "NVMe Storport driver parameters under 'stornvme' are protected Windows kernel storage infrastructure.";
                return true;
            }
            if (target.Contains("FeatureSettingsOverride", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Hardware speculative execution mitigations (Spectre Variant 2) are protected by Windows security policy.";
                return true;
            }
            var (isBlocked, blockReason) = OptimizationSettingOwnership.Instance.EvaluateDefenderProtection(target);
            if (isBlocked)
            {
                reason = blockReason;
                return true;
            }
            reason = string.Empty;
            return false;
        }

        public static bool IsProcessProtected(string processName, bool isLaptop = false, string? oem = null)
            => Instance.IsProcessHardProtected(processName, null, null, isLaptop, false, oem, false, out _, out _);

        public static HashSet<string> LoadProtectedServiceNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inst = Instance;
            foreach (var s in inst._connectivityServices) set.Add(s);
            foreach (var s in inst._audioServices) set.Add(s);
            foreach (var s in inst._storageServices) set.Add(s);
            foreach (var s in inst._securityServices) set.Add(s);
            foreach (var s in inst._laptopPowerThermalServices) set.Add(s);
            foreach (var s in inst._gpuServices) set.Add(s);

            string[] coreSec = {
                "WinDefend", "MsMpEng", "SecurityHealthService", "SecurityHealthSystray", "Sense", "SenseCncProxy",
                "wscsvc", "CryptSvc", "RpcSs", "RpcEptMapper", "DcomLaunch", "BrokerInfrastructure",
                "SystemEventsBroker", "TimeBrokerSvc", "StateRepository", "CoreMessagingRegistrar",
                "EventLog", "EventSystem", "Schedule", "ProfSvc", "gpsvc", "LanmanWorkstation",
                "LanmanServer", "Dhcp", "Dnscache", "netprofm", "nlasvc", "Power", "PlugPlay",
                "W32Time", "VaultSvc", "KeyIso", "SamSs", "LsaIso", "mpssvc", "BFE", "InstallService",
                "WlanSvc", "bthserv", "AudioSrv", "AudioEndpointBuilder", "DeviceAssociationService",
                "Wcmsvc", "NcbService", "NetSetupSvc", "Netman", "Nsi", "WinHttpAutoProxySvc", "RasMan",
                "SstpSvc", "IKEEXT", "PolicyAgent", "BthAvctpSvc", "BthHFSrv", "BTAGService",
                "BluetoothUserService", "BthA2dp", "BthAvrcpTg", "Disk", "volume", "partmgr", "mountmgr",
                "fltmgr", "fileinfo", "filecrypt", "storahci", "stornvme", "storufs", "storport",
                "EhStorClass", "EhStorTcgDrv", "msahci", "BDESVC", "dot3svc", "eaphost", "WwanSvc", "RmSvc"
            };
            foreach (var s in coreSec) set.Add(s);

            return set;
        }

        public HardProtectRegistry(string? dataDir = null)
        {
            LoadData(dataDir);
        }

        private void LoadData(string? dataDir)
        {
            // Seed defaults first
            SeedBuiltInDefaults();

            try
            {
                string dir = dataDir ?? FindDataDirectory();
                string svcFile = Path.Combine(dir, "hard_protect_services.json");
                string procFile = Path.Combine(dir, "hard_protect_processes.json");

                if (File.Exists(svcFile))
                {
                    string json = File.ReadAllText(svcFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("connectivity", out var conn))
                        foreach (var item in conn.EnumerateArray()) _connectivityServices.Add(item.GetString()!);
                    if (root.TryGetProperty("audio", out var aud))
                        foreach (var item in aud.EnumerateArray()) _audioServices.Add(item.GetString()!);
                    if (root.TryGetProperty("storage", out var sto))
                        foreach (var item in sto.EnumerateArray()) _storageServices.Add(item.GetString()!);
                    if (root.TryGetProperty("security", out var sec))
                        foreach (var item in sec.EnumerateArray()) _securityServices.Add(item.GetString()!);
                    if (root.TryGetProperty("laptopPowerThermal", out var pwr))
                        foreach (var item in pwr.EnumerateArray()) _laptopPowerThermalServices.Add(item.GetString()!);
                }

                if (File.Exists(procFile))
                {
                    string json = File.ReadAllText(procFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("connectivity", out var conn))
                    {
                        if (conn.TryGetProperty("modules", out var mods))
                            foreach (var item in mods.EnumerateArray()) _connectivityModules.Add(item.GetString()!);
                        if (conn.TryGetProperty("paths", out var pths))
                            foreach (var item in pths.EnumerateArray()) _connectivityPaths.Add(item.GetString()!);
                        if (conn.TryGetProperty("namePrefixes", out var prfx))
                            foreach (var item in prfx.EnumerateArray()) _connectivityNamePrefixes.Add(item.GetString()!);
                    }

                    if (root.TryGetProperty("audio", out var aud))
                    {
                        if (aud.TryGetProperty("modules", out var mods))
                            foreach (var item in mods.EnumerateArray()) _audioModules.Add(item.GetString()!);
                        if (aud.TryGetProperty("processes", out var procs))
                            foreach (var item in procs.EnumerateArray())
                            {
                                string name = item.GetString()!;
                                _audioProcesses.Add(name);
                                _protectedProcessNames.Add(name);
                            }
                        if (aud.TryGetProperty("nameKeywords", out var keys))
                            foreach (var item in keys.EnumerateArray()) _audioKeywords.Add(item.GetString()!);
                    }

                    if (root.TryGetProperty("displayGpu", out var gpu))
                    {
                        if (gpu.TryGetProperty("modules", out var mods))
                            foreach (var item in mods.EnumerateArray()) _displayGpuModules.Add(item.GetString()!);
                        if (gpu.TryGetProperty("processes", out var procs))
                            foreach (var item in procs.EnumerateArray())
                            {
                                string name = item.GetString()!;
                                _displayGpuProcesses.Add(name);
                                _protectedProcessNames.Add(name);
                            }
                    }

                    if (root.TryGetProperty("storage", out var sto))
                    {
                        if (sto.TryGetProperty("processes", out var procs))
                            foreach (var item in procs.EnumerateArray())
                            {
                                string name = item.GetString()!;
                                _storageProcesses.Add(name);
                                _protectedProcessNames.Add(name);
                            }
                    }

                    if (root.TryGetProperty("input", out var inp))
                    {
                        if (inp.TryGetProperty("processes", out var procs))
                            foreach (var item in procs.EnumerateArray())
                            {
                                string name = item.GetString()!;
                                _inputProcesses.Add(name);
                                _protectedProcessNames.Add(name);
                            }
                    }

                    if (root.TryGetProperty("security", out var sec))
                    {
                        if (sec.TryGetProperty("modules", out var mods))
                            foreach (var item in mods.EnumerateArray()) _securityModules.Add(item.GetString()!);
                        if (sec.TryGetProperty("processes", out var procs))
                            foreach (var item in procs.EnumerateArray())
                            {
                                string name = item.GetString()!;
                                _securityProcesses.Add(name);
                                _protectedProcessNames.Add(name);
                            }
                        if (sec.TryGetProperty("vendors", out var vends))
                            foreach (var item in vends.EnumerateArray()) _securityVendors.Add(item.GetString()!);
                    }

                    if (root.TryGetProperty("laptopPowerThermal", out var lpt))
                    {
                        foreach (var prop in lpt.EnumerateObject())
                        {
                            var list = new List<string>();
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                list.Add(item.GetString()!);
                                _protectedProcessNames.Add(item.GetString()!);
                            }
                            _laptopPowerThermalProcesses[prop.Name] = list;
                        }
                    }
                }
            }
            catch
            {
                // Fallbacks already seeded
            }
        }

        private static string FindDataDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(baseDir, "data");
            if (Directory.Exists(p1)) return p1;

            string cwd = Directory.GetCurrentDirectory();
            string p2 = Path.Combine(cwd, "data");
            if (Directory.Exists(p2)) return p2;

            string p3 = Path.Combine(cwd, "src", "BiosOptimizer.Core", "data");
            if (Directory.Exists(p3)) return p3;

            return baseDir;
        }

        private void SeedBuiltInDefaults()
        {
            // Connectivity services
            string[] connServices = {
                "WlanSvc", "dot3svc", "eaphost", "WwanSvc", "RmSvc", "Wcmsvc", "NcbService",
                "NetSetupSvc", "Netman", "netprofm", "NlaSvc", "Dhcp", "Dnscache", "Nsi",
                "LanmanWorkstation", "LanmanServer", "WinHttpAutoProxySvc", "RasMan", "SstpSvc",
                "IKEEXT", "PolicyAgent", "bthserv", "BthAvctpSvc", "BthHFSrv", "BTAGService",
                "BluetoothUserService", "BthA2dp", "BthAvrcpTg"
            };
            foreach (var s in connServices) _connectivityServices.Add(s);

            // Audio services
            string[] audServices = { "AudioSrv", "AudioEndpointBuilder", "DeviceAssociationService" };
            foreach (var s in audServices) _audioServices.Add(s);

            // Storage services
            string[] stoServices = { "Disk", "volume", "partmgr", "mountmgr", "fltmgr", "fileinfo", "filecrypt", "storahci", "stornvme", "storufs", "storport", "EhStorClass", "EhStorTcgDrv", "msahci", "StorSvc", "BrokerInfrastructure", "DcomLaunch" };
            foreach (var s in stoServices) _storageServices.Add(s);

            // GPU services
            string[] gpuSvcs = { "NvContainerLocalSystem", "nvsvc", "NVDisplay.ContainerLocalSystem", "AmdCrashDefenderService", "amdfendrs", "amdfendrsr" };
            foreach (var s in gpuSvcs) _gpuServices.Add(s);

            // Security services
            string[] secServices = { "WinDefend", "WdNisSvc", "Sense", "wscsvc", "mpssvc", "BFE", "BDESVC" };
            foreach (var s in secServices) _securityServices.Add(s);

            // Laptop power thermal
            string[] pwrServices = { "DellPowerManagerService", "DellDataVault", "DellTechHub", "AWCCService", "HPPowerManager", "HPSupportAssistant", "OmenHSA", "OmenCap", "LenovoVantageService", "Lenovo.Modern.ImController", "LenovoPowerManagement", "ASUSOptimization", "ASUSSystemAnalysis", "ArmouryCrateControlInterface", "AsusROGLSLService", "MSICenter", "MSICenterService", "AcerPowerManager", "AcerQuickAccess", "RazerSynapseService", "Razer Synapse Service", "SamsungSettings", "SamsungPowerManagement", "DptfService", "dptf_helper", "ipf_uf", "ipf_helper", "AmdPpm", "AmdPower" };
            foreach (var s in pwrServices) _laptopPowerThermalServices.Add(s);

            // Display GPU processes
            string[] gpuProcs = { "dwm.exe", "NVDisplay.Container.exe", "nvcontainer.exe", "nvsphelper64.exe", "AMD External Events Utility", "atieclxx.exe", "RadeonSoftware.exe", "RadeonSettings.exe", "amdfendrsr.exe", "igfxEM.exe", "igfxHK.exe", "igfxTray.exe", "IntelCpHDCPSvc.exe", "IntelGraphics.exe", "IntelDisplay.exe" };
            foreach (var p in gpuProcs) { _displayGpuProcesses.Add(p); _protectedProcessNames.Add(p); }

            // Storage processes
            string[] stoProcs = { "IAStorDataMgrSvc.exe", "IAStorIcon.exe", "RstMwService.exe", "SamsungMagician.exe", "MagicianSvc.exe", "CrucialStorageExecutive.exe", "SearchIndexer.exe" };
            foreach (var p in stoProcs) { _storageProcesses.Add(p); _protectedProcessNames.Add(p); }

            // Input processes
            string[] inpProcs = { "TextInputHost.exe", "ctfmon.exe", "TabletInputService.exe", "SynTPEnh.exe", "SynTPHelper.exe", "ETDService.exe", "ETDCtrl.exe", "iCUE.exe", "Corsair.Service.exe", "lghub.exe", "lghub_agent.exe", "Razer Synapse Service.exe", "RazerSynapse.exe", "SteelSeriesGG.exe", "DragonCenter.exe", "Wacom_Tablet.exe" };
            foreach (var p in inpProcs) { _inputProcesses.Add(p); _protectedProcessNames.Add(p); }

            // Security processes
            string[] secProcs = { "MsMpEng.exe", "DefenderSessionHelper.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "SecurityHealthService.exe", "SecurityHealthSystray.exe", "SGrmBroker.exe", "LsaIso.exe" };
            foreach (var p in secProcs) { _securityProcesses.Add(p); _protectedProcessNames.Add(p); }

            // Security vendors (verified vendor names & specific product prefixes — no overbroad 2-3 char tokens)
            string[] secVendors = {
                "Kaspersky", "ESET", "Bitdefender", "McAfee", "Symantec", "Norton", "Avast",
                "avgui", "avgsvc", "avgtpx", "avgidsagent", "avgwdsvc",
                "Malwarebytes", "CrowdStrike", "SentinelOne", "Carbon Black", "Sophos", "Trend Micro",
                "Webroot", "Avira", "F-Secure",
                "K7TS", "K7CrvSvc", "K7RTScan", "K7SysMon", "K7AV", "K7FW",
                "Comodo",
                "360Tray", "360Safe", "360sd", "360rp", "ZhuDongFangYu", "QHSafeTray",
                "Dr.Web"
            };
            foreach (var v in secVendors) _securityVendors.Add(v);

            // Connectivity modules & prefixes (strictly kernel/driver & wireless stack — excludes user-mode wlanapi/wlanhlp/wlanmsm/wlansec DLLs)
            string[] connMods = { "bthport.sys", "bthaudiohs.sys", "bthhfenum.sys", "bthmodem.sys", "btwaudio.sys", "btwi2c.sys", "ibtusb.sys", "rtbth", "IntelBluetooth", "QualcommBth", "Netwtw", "rtwlan", "Qcamain" };
            foreach (var m in connMods) _connectivityModules.Add(m);

            string[] connPaths = { "\\Intel\\Wireless\\", "\\Realtek\\Wireless\\", "\\Qualcomm\\Wireless\\", "\\Broadcom\\Wireless\\" };
            foreach (var p in connPaths) _connectivityPaths.Add(p);

            string[] connPrfx = { "IntelWiFi", "IntelBluetooth", "IntelPROSet", "iwlwifi", "RtWlan", "RtkWlan", "RtkBtManServ", "RealtekWireless", "WlanExt", "WlanSvc", "Qcamain", "QCAWiFi", "qcwlan", "BcmWl", "Broadcom" };
            foreach (var p in connPrfx) _connectivityNamePrefixes.Add(p);

            // Audio processes, modules & keywords (specific APO/driver signatures — no bare DTS/Waves substrings)
            string[] audProcs = {
                "audiodg.exe", "AudioSrv", "AudioEndpointBuilder", "dasHost.exe",
                "RtkAudUService64.exe", "RtkAudioService64.exe", "RAVBg64.exe", "RAVCpl64.exe",
                "RTHDVCPL.exe", "RtHDVBg.exe", "RealtekService.exe", "WavesSysSvc64.exe", "DtsApo4Service.exe"
            };
            foreach (var p in audProcs) { _audioProcesses.Add(p); _protectedProcessNames.Add(p); }

            string[] audMods = { "RTKVHD64.sys", "RTKVHD.sys", "DolbyDAX", "DtsApo", "DtsAudio", "WavesSysSvc", "WavesAudio", "MaxxAudio", "IntcAudioBus.sys", "IntcOED.sys", "amdhd", "HDAudio.sys", "Nahimic" };
            foreach (var m in audMods) _audioModules.Add(m);

            string[] audKeys = { "Realtek", "Dolby", "DtsApo", "DtsAudio", "DTSAudioService", "WavesSysSvc", "WavesMaxxAudio", "Waves MaxxAudio", "Nahimic", "Sonic Studio", "Sonic Radar", "A-Volute", "Intel SST", "Intel Smart Sound", "AMD HD Audio", "NVIDIA HD Audio", "Cirrus Logic", "Conexant" };
            foreach (var k in audKeys) _audioKeywords.Add(k);
        }

        public bool IsServiceHardProtected(string serviceName, bool isLaptop, string? oem, out string category)
        {
            category = string.Empty;
            if (string.IsNullOrWhiteSpace(serviceName)) return true;

            string clean = serviceName.Trim();
            if (_connectivityServices.Contains(clean) || clean.StartsWith("bth", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("wlan", StringComparison.OrdinalIgnoreCase))
            {
                category = "CONN";
                return true;
            }
            if (_audioServices.Contains(clean) || clean.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
            {
                category = "AUD";
                return true;
            }
            if (_gpuServices.Contains(clean) || clean.StartsWith("nv", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("amd", StringComparison.OrdinalIgnoreCase))
            {
                category = "GPU";
                return true;
            }
            if (_storageServices.Contains(clean))
            {
                category = "STO";
                return true;
            }
            if (_securityServices.Contains(clean) || clean.StartsWith("windefend", StringComparison.OrdinalIgnoreCase))
            {
                category = "SEC";
                return true;
            }
            if (_laptopPowerThermalServices.Contains(clean))
            {
                category = "PWR";
                return true;
            }
            if (ProtectedApplicationPolicy.Instance.IsServiceProtected(clean))
            {
                category = "APP";
                return true;
            }

            return false;
        }

        public bool IsServiceHardProtected(string serviceName, bool isLaptop = false, string? oem = null)
            => IsServiceHardProtected(serviceName, isLaptop, oem, out _);

        public bool IsProcessHardProtected(
            string processName,
            string? imagePath,
            IEnumerable<string>? loadedModules,
            bool isLaptop,
            bool onBattery,
            string? oem,
            bool hasBitLocker,
            out string category,
            out string reason)
        {
            category = string.Empty;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(processName))
            {
                category = "Invalid";
                reason = "Empty process name";
                return true;
            }

            string cleanName = processName.Trim();
            string cleanNameWithExt = cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cleanName : cleanName + ".exe";
            string cleanNameNoExt = cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cleanName[..^4] : cleanName;

            // 1. Direct Connectivity Process Check
            if (_connectivityNamePrefixes.Any(p => cleanNameNoExt.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
                                                   (p.Length >= 6 && cleanNameNoExt.Contains(p, StringComparison.OrdinalIgnoreCase))))
            {
                category = "Connectivity";
                reason = $"Matches connectivity vendor prefix/keyword ({cleanName})";
                return true;
            }

            if (!string.IsNullOrEmpty(imagePath))
            {
                if (_connectivityPaths.Any(p => imagePath.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    category = "Connectivity";
                    reason = $"Located in connectivity driver path ({imagePath})";
                    return true;
                }
            }

            if (loadedModules != null)
            {
                foreach (var mod in loadedModules)
                {
                    string modFile = Path.GetFileName(mod);
                    if (string.IsNullOrEmpty(modFile)) continue;
                    if (_connectivityModules.Any(m => modFile.Equals(m, StringComparison.OrdinalIgnoreCase) ||
                                                      modFile.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        category = "Connectivity";
                        reason = $"Loads connectivity driver/module ({modFile})";
                        return true;
                    }
                }
            }

            // 2. Audio Infrastructure
            if (_audioProcesses.Contains(cleanName) || _audioProcesses.Contains(cleanNameWithExt) ||
                cleanName.Equals("audiodg", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Equals("audiodg.exe", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Equals("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Equals("AudioEndpointBuilder", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Equals("dasHost", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Equals("dasHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                category = "Audio";
                reason = $"Core Windows/hardware audio process ({cleanName})";
                return true;
            }

            if (_audioKeywords.Any(k => cleanNameNoExt.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                category = "Audio";
                reason = $"Matches audio hardware vendor keyword ({cleanName})";
                return true;
            }

            if (loadedModules != null)
            {
                foreach (var mod in loadedModules)
                {
                    string modFile = Path.GetFileName(mod);
                    if (string.IsNullOrEmpty(modFile)) continue;
                    if (_audioModules.Any(m => modFile.Equals(m, StringComparison.OrdinalIgnoreCase) ||
                                               modFile.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        category = "Audio";
                        reason = $"Loads audio driver/APO module ({modFile})";
                        return true;
                    }
                }
            }

            // 3. Display / GPU
            if (_displayGpuProcesses.Contains(cleanName) || _displayGpuProcesses.Contains(cleanNameWithExt))
            {
                category = "DisplayGpu";
                reason = $"Display/GPU subsystem process ({cleanName})";
                return true;
            }

            if (loadedModules != null)
            {
                foreach (var mod in loadedModules)
                {
                    string modFile = Path.GetFileName(mod);
                    if (string.IsNullOrEmpty(modFile)) continue;
                    if (_displayGpuModules.Any(m => modFile.Equals(m, StringComparison.OrdinalIgnoreCase) ||
                                                    modFile.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        category = "DisplayGpu";
                        reason = $"Loads GPU kernel driver ({modFile})";
                        return true;
                    }
                }
            }

            // 4. Storage Infrastructure
            if (_storageProcesses.Contains(cleanName) || _storageProcesses.Contains(cleanNameWithExt))
            {
                if (cleanName.StartsWith("SearchIndexer", StringComparison.OrdinalIgnoreCase) && !onBattery)
                {
                    // SearchIndexer only protected on battery
                }
                else
                {
                    category = "Storage";
                    reason = $"Storage/Disk management process ({cleanName})";
                    return true;
                }
            }

            // 5. Input Devices & Peripherals
            if (_inputProcesses.Contains(cleanName) || _inputProcesses.Contains(cleanNameWithExt))
            {
                category = "Input";
                reason = $"Touchpad/Mouse/Input device host ({cleanName})";
                return true;
            }

            // 6. Security & Anti-Virus
            if (_securityProcesses.Contains(cleanName) || _securityProcesses.Contains(cleanNameWithExt))
            {
                category = "Security";
                reason = $"Windows Security / Defender process ({cleanName})";
                return true;
            }

            if (_securityVendors.Any(v => cleanNameNoExt.StartsWith(v, StringComparison.OrdinalIgnoreCase) ||
                                          (v.Length >= 6 && cleanNameNoExt.Contains(v, StringComparison.OrdinalIgnoreCase))))
            {
                category = "Security";
                reason = $"Third-party Anti-Virus / Security vendor ({cleanName})";
                return true;
            }

            if (hasBitLocker && loadedModules != null)
            {
                if (loadedModules.Any(m => m.Contains("fvevol.sys", StringComparison.OrdinalIgnoreCase) ||
                                           m.Contains("fveapi.dll", StringComparison.OrdinalIgnoreCase)))
                {
                    category = "Security";
                    reason = $"BitLocker encryption module host";
                    return true;
                }
            }

            // 7. Laptop Power & Thermal
            if (isLaptop)
            {
                if (_laptopPowerThermalServices.Contains(cleanName) || _laptopPowerThermalServices.Contains(cleanNameWithExt) ||
                    _laptopPowerThermalServices.Contains(cleanNameNoExt))
                {
                    category = "PowerThermal";
                    reason = $"OEM Laptop power and thermal control host ({cleanName})";
                    return true;
                }

                if (!string.IsNullOrEmpty(oem))
                {
                    foreach (var kvp in _laptopPowerThermalProcesses)
                    {
                        if (oem.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            if (kvp.Value.Any(p => p.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                                                   p.Equals(cleanNameWithExt, StringComparison.OrdinalIgnoreCase)))
                            {
                                category = "PowerThermal";
                                reason = $"{kvp.Key} Laptop power and thermal control process ({cleanName})";
                                return true;
                            }
                        }
                    }
                }
            }

            // 8. V4 Protected Application / Service Exclusion Policy
            var appMatch = ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(cleanNameNoExt, imagePath);
            if (appMatch.IsProtected)
            {
                category = "ProtectedApplication";
                reason = appMatch.Reason;
                return true;
            }

            return false;
        }
    }
}
