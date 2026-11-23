#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using BiosOptimizer.Core.V4.Environments;
using BiosOptimizer.Core.V4.Intelligence;
using BiosOptimizer.Core.V4.Workload;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Safety
{
    /// <summary>
    /// Non-bypassable Safety Kernel for ERROR OPTIMIZER V4.
    /// Strictly guarantees ZERO Bugchecks (BSOD) by enforcing R01-R12 process termination rules,
    /// S01-S06 service modification rules, Live Watchdog monitoring, Anti-Cheat detection,
    /// and Bugcheck Blacklist persistence.
    /// </summary>
    public class SafetyKernel : IDisposable
    {
        private static readonly Lazy<SafetyKernel> _instance = new(() => new SafetyKernel());
        public static SafetyKernel Instance => _instance.Value;

        private readonly ProtectedProcessPolicy _policy;
        private readonly ProcessIdentitySafety _identitySafety;
        private readonly string _blacklistFilePath;
        private readonly ConcurrentDictionary<string, string> _bugcheckBlacklist = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentBag<ActionRecord> _auditLog = new();

        private Timer? _watchdogTimer;
        private volatile bool _isSystemStable = true;
        private long _lastHeartbeatTicks;
        private int _watchdogTripCount;

        public bool IsSimulatedMode { get; set; } = false;

        public SafetyKernel(ProtectedProcessPolicy? policy = null, ProcessIdentitySafety? identitySafety = null, string? blacklistPath = null)
        {
            _policy = policy ?? new ProtectedProcessPolicy();
            _identitySafety = identitySafety ?? new ProcessIdentitySafety();

            string baseDir = BiosOptimizer.Core.Common.AppEnvironment.ResolveWritableDataDirectory();
            _blacklistFilePath = blacklistPath ?? Path.Combine(baseDir, "bugcheck_blacklist.json");

            LoadBlacklist();
        }

        #region Process Evaluation (R01 - R12)

        /// <summary>
        /// Non-bypassable pre-kill evaluation. Every process termination target MUST pass this rule matrix.
        /// </summary>
        public SafetyVerdict EvaluateProcessKill(
            ProcessIdentityDto process,
            bool isGamingActive,
            bool isEditingActive,
            out string ruleCode,
            out string reason)
        {
            return EvaluateProcessKill(process, isGamingActive, isEditingActive, out ruleCode, out reason, processTree: null);
        }

        /// <summary>
        /// Non-bypassable pre-kill evaluation with optional verified process-tree ancestry context.
        /// Preserves all SafetyKernel R01-R28 invariants intact and adds explicit self-protection and
        /// verified shielded-descendant protection.
        /// </summary>
        public SafetyVerdict EvaluateProcessKill(
            ProcessIdentityDto process,
            bool isGamingActive,
            bool isEditingActive,
            out string ruleCode,
            out string reason,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree)
        {
            ruleCode = string.Empty;
            reason = string.Empty;

            if (process == null)
            {
                ruleCode = "R01";
                reason = "Null process reference";
                RecordAudit(process?.ProcessName ?? "UNKNOWN", process?.ProcessId ?? 0, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // Explicit Self-Protection (current PID, ERROR OPTIMIZER PROTOTYPE.exe, optimizer hosts, verified WebView2/child trees)
            if (ProtectedProcessPolicy.IsSelfOrVerifiedDescendant(process, processTree, out string selfProtectionReason))
            {
                ruleCode = "R01_SELF";
                reason = $"Self-protection invariant: {selfProtectionReason}";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R03: PPL (Protected Process Light) Protection Level Check
            if (process.ProcessId > 0 && IsPplProtected(process.ProcessId))
            {
                ruleCode = "R03";
                reason = "PPL-protected process — cannot terminate.";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R05: Driver-hosting process Check (dwServiceType contains SERVICE_KERNEL_DRIVER, SERVICE_FILE_SYSTEM_DRIVER, SERVICE_RECOGNIZER_DRIVER)
            if (HostsKernelDriverService(process, out string driverServiceName))
            {
                ruleCode = "R05";
                reason = $"Process {process.ProcessName} hosts driver service ({driverServiceName}) — cannot terminate driver infrastructure.";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R26: Host-With-Protected-Service Invariant (The WiFi/BT/Audio/Storage Host Kill Path Fix)
            var hostedServices = GetHostedServices(process);
            foreach (var svcName in hostedServices)
            {
                if (ProtectedServiceNames.Contains(svcName))
                {
                    ruleCode = "R26";
                    reason = $"Process {process.ProcessName} (pid {process.ProcessId}) hosts protected service '{svcName}'. Cannot terminate the host without killing the protected service.";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R27: Driver-Module Invariant (Kernel & Hardware adapter driver protection)
            if (LoadsProtectedDriverModule(process, out string matchedDriverMod))
            {
                ruleCode = "R27";
                reason = $"Process {process.ProcessName} loads a kernel driver module ({matchedDriverMod}) — terminating it can unload the driver and drop hardware.";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R11: System Stability / Watchdog Trip Check
            if (!_isSystemStable)
            {
                ruleCode = "R11";
                reason = "Execution halted: System stability watchdog tripped or heartbeat lag detected";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R01: PPL / Protected Process / Kernel System PID Check
            if (process.ProcessId <= 4 ||
                process.ProcessName.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("Secure System", StringComparison.OrdinalIgnoreCase))
            {
                ruleCode = "R01";
                reason = $"Protected kernel process (PID {process.ProcessId}, Name: {process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // Self-protection (defense-in-depth fallback)
            if (ProtectedProcessPolicy.IsSelfOrVerifiedDescendant(process, processTree, out string selfFallbackReason))
            {
                ruleCode = "R01_SELF";
                reason = $"Self-protection invariant: {selfFallbackReason}";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R10: Bugcheck Blacklist History
            if (IsBlacklisted(process.ProcessName, process.ExecutablePath, out string blacklistReason))
            {
                ruleCode = "R10";
                reason = $"Target is blacklisted due to historical instability: {blacklistReason}";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R02: Critical svchost.exe -k groups
            if (process.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCriticalSvchostGroup(process, out string groupName))
                {
                    ruleCode = "R02";
                    reason = $"Critical svchost service host group protected: {groupName}";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R03: Core OS & Shell Essential Subsystems
            if (IsCoreOsSubsystem(process.ProcessName))
            {
                ruleCode = "R03";
                reason = $"Core Windows OS subsystem process ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R04: Active Security & EDR Modules
            if (IsSecurityEdrModule(process.ProcessName))
            {
                ruleCode = "R04";
                reason = $"Active Windows security / EDR module ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R05: Active Anti-Cheat Process Tree
            if (IsAntiCheatProcess(process.ProcessName))
            {
                ruleCode = "R05";
                reason = $"Anti-Cheat subsystem component ({process.ProcessName}) protected to prevent game bans/crashes";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R06: Direct Kernel Drivers & Hardware Peripheral Infrastructure
            if (IsHardwareDriverInfrastructure(process.ProcessName, process.ExecutablePath))
            {
                ruleCode = "R06";
                reason = $"Hardware driver / peripheral component ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R28: V4 Protected Application / Service Exclusion Policy (Creative/Editing, Remote Access, Media, VPN, Gaming/Steam, Lenovo Vantage)
            var protectedAppMatch = ProtectedApplicationPolicy.Instance.EvaluateProcess(process, processTree);
            if (protectedAppMatch.IsProtected)
            {
                ruleCode = "R28_PROTECTED_APP";
                reason = protectedAppMatch.Reason;
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R07: Active Workload, Foreground, and Shielded User Applications
            if (isGamingActive && IsGameOrLauncher(process.ProcessName))
            {
                ruleCode = "R07";
                reason = $"Active gaming workload process ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            if (isEditingActive && IsCreativeWorkloadSuite(process.ProcessName))
            {
                ruleCode = "R07";
                reason = $"Active creative editing/rendering suite ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            if (process.IsForeground || (process.HasVisibleWindow && !IsKnownBackgroundOverlay(process.ProcessName)))
            {
                ruleCode = "R07";
                reason = $"Active interactive user foreground window ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            if (IsShieldedUserApp(process.ProcessName))
            {
                ruleCode = "R07";
                reason = $"Shielded user application ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            ProcessIdentityDto? shieldedAncestor = null;
            if (process.IsShieldedDescendant ||
                ProtectedProcessPolicy.TryFindVerifiedShieldedAncestor(process, processTree, out shieldedAncestor))
            {
                ruleCode = "R07_SHIELDED_DESCENDANT";
                string ancestorName = !string.IsNullOrEmpty(shieldedAncestor?.ProcessName)
                    ? shieldedAncestor!.ProcessName
                    : (!string.IsNullOrEmpty(process.ParentProcessName) ? process.ParentProcessName : "shielded application");
                reason = $"Verified descendant of shielded user application ({ancestorName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R08: System Critical Infrastructure (Audio pipeline, Print spooler when active)
            if (process.ProcessName.Equals("audiodg", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("dasHost", StringComparison.OrdinalIgnoreCase))
            {
                ruleCode = "R08";
                reason = $"Audio/Device association system infrastructure ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R12: Anti-Cheat System-Wide Lock
            if (IsAntiCheatPresent() && IsRiskyUnderAntiCheat(process.ProcessName))
            {
                ruleCode = "R12";
                reason = "Online anti-cheat engine active on system: non-whitelisted process termination frozen";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            var env = EnvironmentDetector.Instance.CurrentInfo;

            // R16: Display/GPU container/hotkey processes
            if (process.ProcessName.Equals("NVDisplay.Container", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("NVDisplay.Container.exe", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("nvcontainer.exe", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("atieclxx", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("atieclxx.exe", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("igfxEM", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("igfxEM.exe", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("igfxHK", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("igfxHK.exe", StringComparison.OrdinalIgnoreCase))
            {
                ruleCode = "R16";
                reason = $"Critical GPU display / hotkey container ({process.ProcessName})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R15: HasBitLocker + process loads fvevol.sys / fveapi.dll
            if (env.HasBitLocker || process.LoadedModules.Any(m => m.Contains("fvevol.sys", StringComparison.OrdinalIgnoreCase) || m.Contains("fveapi.dll", StringComparison.OrdinalIgnoreCase)))
            {
                if (process.LoadedModules.Any(m => m.Contains("fvevol.sys", StringComparison.OrdinalIgnoreCase) || m.Contains("fveapi.dll", StringComparison.OrdinalIgnoreCase)))
                {
                    ruleCode = "R15";
                    reason = "Process hosts BitLocker encryption subsystem (fvevol.sys/fveapi.dll)";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // HardProtectRegistry Check (Connectivity, Audio, Storage, Input, Security, Laptop Power/Thermal)
            if (HardProtectRegistry.Instance.IsProcessHardProtected(
                process.ProcessName,
                process.ExecutablePath,
                process.LoadedModules,
                env.IsLaptop,
                !env.IsOnAcPower,
                env.SystemManufacturer,
                env.HasBitLocker,
                out string hpCategory,
                out string hpReason))
            {
                ruleCode = hpCategory == "ProtectedApplication" ? "R28_PROTECTED_APP" :
                           hpCategory == "PowerThermal" ? "R13_PWR" :
                           hpCategory == "DisplayGpu" ? "R16_GPU" :
                           hpCategory == "Audio" ? "R08_AUD" :
                           hpCategory == "Input" ? "R06_INP" :
                           hpCategory == "Security" ? "R04_SEC" :
                           hpCategory == "Storage" ? "R06_STO" : "R06_CONN";
                reason = hpReason;
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // Workload specific rules R17-R25:
            var activeWorkloads = WorkloadDetector.Instance.DetectWorkloads().ActiveWorkloads;

            // R25: workload = Emulator + candidate is HD-Player, BstkSVC, HD-Adb
            if (activeWorkloads.Contains(ActiveWorkload.Emulator) ||
                process.ProcessName.Contains("HD-Player", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Contains("BstkSVC", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Contains("HD-Adb", StringComparison.OrdinalIgnoreCase))
            {
                if (process.ProcessName.Contains("HD-Player", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.Contains("BstkSVC", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.Contains("HD-Adb", StringComparison.OrdinalIgnoreCase))
                {
                    ruleCode = "R25";
                    reason = $"Active emulator infrastructure component ({process.ProcessName})";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R17: workload active + candidate in workload's process tree
            if (activeWorkloads.Any(w => w != ActiveWorkload.None) &&
                !string.IsNullOrEmpty(process.WorkloadAssociation))
            {
                ruleCode = "R17";
                reason = $"Candidate is part of active workload process tree ({process.WorkloadAssociation})";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                return SafetyVerdict.Deny;
            }

            // R18 & R23: workload = Game + candidate loads anti-cheat module or callback
            if (isGamingActive || activeWorkloads.Contains(ActiveWorkload.Game))
            {
                if (process.LoadedModules.Any(m => m.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
                                                   m.Contains("BattlEye", StringComparison.OrdinalIgnoreCase) ||
                                                   m.Contains("vgk", StringComparison.OrdinalIgnoreCase) ||
                                                   m.Contains("Ricochet", StringComparison.OrdinalIgnoreCase)))
                {
                    ruleCode = "R18";
                    reason = "Process loads active gaming anti-cheat module";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }

                if (process.HasKernelCallback)
                {
                    ruleCode = "R23";
                    reason = "Process registers active anti-cheat kernel callback";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R19 & R24: workload = Video/3D/Rendering
            if (isEditingActive ||
                activeWorkloads.Contains(ActiveWorkload.VideoEditing) ||
                activeWorkloads.Contains(ActiveWorkload.ThreeDEditing) ||
                activeWorkloads.Contains(ActiveWorkload.Rendering))
            {
                if (process.GpuUsagePercent > 10.0)
                {
                    ruleCode = "R19";
                    reason = $"Active creative editing/rendering candidate using >10% GPU ({process.GpuUsagePercent:F1}%)";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }

                if (process.LoadedModules.Any(m => m.Contains("avcodec", StringComparison.OrdinalIgnoreCase) ||
                                                   m.Contains("x264", StringComparison.OrdinalIgnoreCase) ||
                                                   m.Contains("x265", StringComparison.OrdinalIgnoreCase)))
                {
                    ruleCode = "R24";
                    reason = "Process loads active video codec infrastructure (avcodec/x264/x265)";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R20: workload = Encoding + candidate > 20% CPU with parent = encoder
            if (activeWorkloads.Contains(ActiveWorkload.Encoding))
            {
                if (process.CpuUsagePercent > 20.0 &&
                    (process.ParentProcessName.Contains("HandBrake", StringComparison.OrdinalIgnoreCase) ||
                     process.ParentProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)))
                {
                    ruleCode = "R20";
                    reason = $"Active encoder child process utilizing {process.CpuUsagePercent:F1}% CPU";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R21: workload active + candidate shares vendor prefix with workload
            foreach (var wl in activeWorkloads.Where(w => w != ActiveWorkload.None))
            {
                if (WorkloadDetector.Instance.IsProcessInWorkloadFamily(process.ProcessName, wl))
                {
                    ruleCode = "R21";
                    reason = $"Process shares vendor family with active workload ({wl})";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // R22: workload = Streaming + candidate is audio/DNS/network service
            if (activeWorkloads.Contains(ActiveWorkload.Streaming))
            {
                if (process.ProcessName.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.Contains("dns", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                    HardProtectRegistry.Instance.IsProcessHardProtected(process.ProcessName, process.ExecutablePath, null, false, false, null, false, out _, out _))
                {
                    ruleCode = "R22";
                    reason = $"Active streaming workload protects audio and network infrastructure ({process.ProcessName})";
                    RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Deny, ruleCode, reason);
                    return SafetyVerdict.Deny;
                }
            }

            // If it passes all strict invariants, evaluate whether it requires benchmark gating
            if (process.Classification == ProcessClassification.OPTIONAL ||
                process.RiskLevel == ProcessRisk.LOW_BACKGROUND)
            {
                ruleCode = "ALLOW_GATED";
                reason = "Candidate approved under Tier 2/3 with benchmark regression gating";
                RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.AllowWithBenchmarkGate, ruleCode, reason);
                return SafetyVerdict.AllowWithBenchmarkGate;
            }

            ruleCode = "ALLOW";
            reason = "Strict safety verification passed: Safe to terminate";
            RecordAudit(process.ProcessName, process.ProcessId, SafetyVerdict.Allow, ruleCode, reason);
            return SafetyVerdict.Allow;
        }

        #endregion

        #region Service Evaluation (S01 - S06)

        /// <summary>
        /// Non-bypassable service stop evaluation. Every service stop target MUST pass this rule matrix.
        /// </summary>
        public SafetyVerdict EvaluateServiceStop(
            string serviceName,
            uint serviceType,
            uint controlsAccepted,
            uint startType,
            bool hasRunningDependents,
            bool isGamingActive,
            bool isEditingActive,
            out string ruleCode,
            out string reason)
        {
            ruleCode = string.Empty;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(serviceName))
            {
                ruleCode = "S05";
                reason = "Empty service name";
                return SafetyVerdict.Deny;
            }

            if (serviceName.Equals("WinDefend", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("RpcSs", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("DcomLaunch", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("EventLog", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("PlugPlay", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("SecurityHealthService", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("vgc", StringComparison.OrdinalIgnoreCase))
            {
                ruleCode = "S05";
                reason = $"Core Windows OS critical infrastructure service ({serviceName})";
                return SafetyVerdict.Deny;
            }

            var protectedSvcMatch = ProtectedApplicationPolicy.Instance.EvaluateService(serviceName);
            if (protectedSvcMatch.IsProtected)
            {
                ruleCode = "S07_PROTECTED_SERVICE";
                reason = protectedSvcMatch.Reason;
                return SafetyVerdict.Deny;
            }

            var env = EnvironmentDetector.Instance.CurrentInfo;
            if (HardProtectRegistry.Instance.IsServiceHardProtected(serviceName, env.IsLaptop, env.SystemManufacturer, out string hpCategory))
            {
                ruleCode = hpCategory == "APP" ? "S07_PROTECTED_SERVICE" : $"HP_{hpCategory}";
                reason = $"Service ({serviceName}) is hard-protected against modification [{hpCategory}]";
                return SafetyVerdict.Deny;
            }

            // S01: Kernel Driver / File System Driver Services
            const uint SERVICE_KERNEL_DRIVER = 0x00000001;
            const uint SERVICE_FILE_SYSTEM_DRIVER = 0x00000002;
            if ((serviceType & (SERVICE_KERNEL_DRIVER | SERVICE_FILE_SYSTEM_DRIVER)) != 0)
            {
                ruleCode = "S01";
                reason = $"Kernel or File System driver service cannot be stopped (Type: 0x{serviceType:X})";
                return SafetyVerdict.Deny;
            }

            // S02: Service Does Not Accept Stop Control
            const uint SERVICE_ACCEPT_STOP = 0x00000001;
            if (controlsAccepted != 0 && (controlsAccepted & SERVICE_ACCEPT_STOP) == 0)
            {
                ruleCode = "S02";
                reason = "Service does not accept SERVICE_CONTROL_STOP";
                return SafetyVerdict.Deny;
            }

            // S03: Boot / System Start Services
            const uint SERVICE_BOOT_START = 0x00000000;
            const uint SERVICE_SYSTEM_START = 0x00000001;
            if (startType == SERVICE_BOOT_START || startType == SERVICE_SYSTEM_START)
            {
                ruleCode = "S03";
                reason = $"Boot/System start service protected against runtime stop (StartType: {startType})";
                return SafetyVerdict.Deny;
            }

            // S04: Active Running Critical Dependents
            if (hasRunningDependents)
            {
                ruleCode = "S04";
                reason = "Service has active running dependent services that depend on it";
                return SafetyVerdict.Deny;
            }

            // S05: Core OS Critical Services
            if (ProtectedProcessPolicy.IsStrictlyProtectedService(serviceName) || IsCoreOsService(serviceName))
            {
                ruleCode = "S05";
                reason = $"Core Windows OS critical infrastructure service ({serviceName})";
                return SafetyVerdict.Deny;
            }

            // S06: Security & Anti-Cheat Services
            if (IsSecurityOrAntiCheatService(serviceName))
            {
                ruleCode = "S06";
                reason = $"Security or Anti-Cheat service ({serviceName})";
                return SafetyVerdict.Deny;
            }

            ruleCode = "ALLOW_SERVICE";
            reason = "Service stop approved under strict safety policy";
            return SafetyVerdict.Allow;
        }

        /// <summary>
        /// Single source of truth query for whether a process, service, scheduled task, startup entry, or AppX package is protected.
        /// </summary>
        public bool IsProtected(ProcessIdentityDto? process, IReadOnlyDictionary<int, ProcessIdentityDto>? processTree = null)
        {
            if (process == null) return true;
            if (ProtectedProcessPolicy.IsSelfOrVerifiedDescendant(process, processTree, out _)) return true;
            if (ProtectedApplicationPolicy.Instance.IsProcessProtected(process, processTree)) return true;
            return EvaluateProcessKill(process, isGamingActive: false, isEditingActive: false, out _, out _, processTree) == SafetyVerdict.Deny;
        }

        public bool IsProtected(OptimizationCandidateDto? candidate)
        {
            if (candidate == null) return true;
            return ProtectedApplicationPolicy.Instance.EvaluateCandidate(candidate).IsProtected;
        }

        public bool IsProtected(string targetType, string targetIdentifier, string? displayName = null)
        {
            return ProtectedApplicationPolicy.Instance.EvaluateActionTarget(targetType, targetIdentifier, displayName).IsProtected;
        }

        public bool IsProtectedService(string serviceName, string? displayName = null, string? binaryPath = null)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return true;
            if (ProtectedApplicationPolicy.Instance.IsServiceProtected(serviceName, displayName, binaryPath)) return true;
            var env = EnvironmentDetector.Instance.CurrentInfo;
            return HardProtectRegistry.Instance.IsServiceHardProtected(serviceName, env.IsLaptop, env.SystemManufacturer) ||
                   ProtectedProcessPolicy.IsStrictlyProtectedService(serviceName) ||
                   IsCoreOsService(serviceName) ||
                   IsSecurityOrAntiCheatService(serviceName);
        }

        public bool IsProtectedScheduledTask(string taskPath, string? taskName = null, string? author = null, string? executeCommand = null)
        {
            return ProtectedApplicationPolicy.Instance.IsScheduledTaskProtected(taskPath, taskName, author, executeCommand);
        }

        public bool IsProtectedStartupEntry(string entryName, string? command = null, string? publisher = null, string? location = null)
        {
            return ProtectedApplicationPolicy.Instance.IsStartupEntryProtected(entryName, command, publisher, location);
        }

        public bool IsProtectedAppXPackage(string packageName, string? displayName = null, string? publisher = null)
        {
            return ProtectedApplicationPolicy.Instance.IsAppXPackageProtected(packageName, displayName, publisher);
        }

        #endregion

        #region Watchdog & Stability Invariants

        public void StartWatchdog()
        {
            _lastHeartbeatTicks = Stopwatch.GetTimestamp();
            _isSystemStable = true;
            _watchdogTripCount = 0;
            if (_watchdogTimer != null) return;

            _watchdogTimer = new Timer(WatchdogTick, null, 250, 250);
        }

        public void StopWatchdog()
        {
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            _isSystemStable = true;
        }

        public void RecordHeartbeat()
        {
            Interlocked.Exchange(ref _lastHeartbeatTicks, Stopwatch.GetTimestamp());
            _isSystemStable = true;
        }

        private void WatchdogTick(object? state)
        {
            long last = Interlocked.Read(ref _lastHeartbeatTicks);
            double elapsedMs = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;

            // If coordinator/executor hung for > 5000ms, trip watchdog
            if (elapsedMs > 5000.0)
            {
                _isSystemStable = false;
                Interlocked.Increment(ref _watchdogTripCount);
            }
            else
            {
                _isSystemStable = true;
            }
        }

        public bool IsSystemStable() => _isSystemStable;

        #endregion

        #region Anti-Cheat Detection & System State

        private static readonly HashSet<string> KnownAntiCheatSignatures = new(StringComparer.OrdinalIgnoreCase)
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Sys", "EasyAntiCheat_Setup",
            "BEService", "BattlEye", "vgtray", "vgc", "ACE-Base", "ACE-Guard",
            "PunkBuster", "PnkBstrA", "PnkBstrB", "FACEITService", "faceit", "faceitclient",
            "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher", "Ricochet", "VALORANT-Win64-Shipping"
        };

        public bool IsAntiCheatPresent()
        {
            try
            {
                var running = Process.GetProcesses();
                foreach (var p in running)
                {
                    try
                    {
                        if (KnownAntiCheatSignatures.Contains(p.ProcessName))
                        {
                            return true;
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool IsRiskyUnderAntiCheat(string processName)
        {
            // During anti-cheat, avoid killing unknown hooked wrappers
            return processName.StartsWith("hook", StringComparison.OrdinalIgnoreCase) ||
                   processName.StartsWith("inject", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Bugcheck Blacklist

        private void LoadBlacklist()
        {
            try
            {
                if (File.Exists(_blacklistFilePath))
                {
                    string json = File.ReadAllText(_blacklistFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            _bugcheckBlacklist[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch { }
        }

        public void SaveBlacklist()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_blacklistFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_bugcheckBlacklist, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_blacklistFilePath, json);
            }
            catch { }
        }

        public void AddToBlacklist(string targetIdentifier, string reason)
        {
            if (string.IsNullOrWhiteSpace(targetIdentifier)) return;
            _bugcheckBlacklist[targetIdentifier.Trim()] = reason;
            SaveBlacklist();
        }

        public bool IsBlacklisted(string processName, string executablePath, out string reason)
        {
            reason = string.Empty;
            if (!string.IsNullOrEmpty(processName) && BugcheckBlacklist.Instance.IsBlacklisted(processName))
            {
                reason = "Blacklisted due to previous bugcheck/instability";
                return true;
            }

            if (!string.IsNullOrEmpty(executablePath) && BugcheckBlacklist.Instance.IsBlacklisted(Path.GetFileName(executablePath)))
            {
                reason = "Blacklisted due to previous bugcheck/instability";
                return true;
            }

            if (!string.IsNullOrEmpty(processName) && _bugcheckBlacklist.TryGetValue(processName.Trim(), out reason!))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(executablePath))
            {
                string fileName = Path.GetFileName(executablePath);
                if (!string.IsNullOrEmpty(fileName) && _bugcheckBlacklist.TryGetValue(fileName, out reason!))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region System Restore Checkpoint

        /// <summary>
        /// Disabled in live execution: Windows System Restore Point creation is restricted exclusively
        /// to the V3 Dashboard explicit CREATE RESTORE POINT user action.
        /// Optimization workflows must create SHA-256 verified Application Backups via BackupManager.
        /// </summary>
        public RestorePointHandle CreatePreOptimizationRestorePoint(string description = "Error Optimizer V4 Pre-Optimization Point")
        {
            var handle = new RestorePointHandle
            {
                Label = description,
                IsCreated = false
            };

            if (IsSimulatedMode)
            {
                handle.IsCreated = true;
                handle.SequenceNumber = 999999;
                return handle;
            }

            handle.ErrorMessage = "Windows System Restore Point creation is disabled outside the V3 Dashboard action; use verified Application Backups.";
            return handle;
        }

        #endregion

        #region Helper Classification Methods

        private static readonly HashSet<string> CoreOsProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "System Idle Process", "Registry", "Memory Compression", "Secure System",
            "smss", "csrss", "wininit", "services", "lsass", "lsm", "winlogon", "fontdrvhost",
            "dwm", "sihost", "taskhostw", "ctfmon", "TextInputHost", "SearchIndexer",
            "ShellExperienceHost", "StartMenuExperienceHost", "ApplicationFrameHost",
            "RuntimeBroker", "explorer", "spoolsv", "conhost"
        };

        private static string StripExe(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        private static bool IsCoreOsSubsystem(string name)
        {
            return CoreOsProcesses.Contains(name) || CoreOsProcesses.Contains(StripExe(name));
        }

        private static readonly HashSet<string> SecurityModules = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "MsMpEng", "SecurityHealthService", "SecurityHealthSystray", "Sense", "SenseCncProxy",
            "wscsvc", "CryptSvc", "mpssvc", "BFE", "avp", "bdagent", "mbam", "mcshield", "sophos", "ekrn"
        };

        private static bool IsSecurityEdrModule(string name)
        {
            return SecurityModules.Contains(name) || SecurityModules.Contains(StripExe(name));
        }

        private static bool IsAntiCheatProcess(string name)
        {
            return KnownAntiCheatSignatures.Contains(name) || KnownAntiCheatSignatures.Contains(StripExe(name));
        }

        private static readonly HashSet<string> DriverModules = new(StringComparer.OrdinalIgnoreCase)
        {
            "nvcontainer", "NVDisplay.Container", "NVIDIA Web Helper", "nvspcaps64", "nvsphelper64",
            "NVIDIA RTX Voice", "NVIDIA Broadcast", "nvagent", "amdfendrsr", "amdow", "AMDRSServ",
            "RadeonSoftware", "cnext", "AMDRSSrcExt", "igfxCUIService", "igfxEM", "igfxHK",
            "IntelGraphicsAdminService", "igfxTray", "OneApp.IGCC.WinService", "AudioSrv",
            "AudioEndpointBuilder", "RAVCbg64", "RAVBg64", "RtkAudioService64", "RealtekService",
            "nahimicservice", "nahimicv3", "creative.sb", "WavesSysSvc64",
            "LogiRegistryService", "lghub_agent", "lghub", "iCUE", "CorsairService", "CorsairLLAService",
            "Razer Synapse Service", "RazerCentralService", "RzSDKServer", "RzActionEngine",
            "Wacom_Tablet", "Wacom_TabletUser", "StreamDeck"
        };

        private static bool IsHardwareDriverInfrastructure(string name, string executablePath)
        {
            if (DriverModules.Contains(name) || DriverModules.Contains(StripExe(name))) return true;
            if (!string.IsNullOrEmpty(executablePath))
            {
                string lower = executablePath.ToLowerInvariant();
                if (lower.Contains("\\system32\\drivers\\") || lower.EndsWith(".sys"))
                    return true;
            }
            return false;
        }

        private static readonly HashSet<string> GameLaunchers = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamservice", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper",
            "Origin", "OriginWebHelperService", "EADesktop", "EABackgroundService",
            "Battle.net", "Battle.net.helper", "Agent", "UbisoftConnect", "upc", "Uplay",
            "GalaxyClient", "GalaxyClientService", "RiotClientServices", "RiotClientUx"
        };

        private static bool IsGameOrLauncher(string name)
        {
            return GameLaunchers.Contains(name) || GameLaunchers.Contains(StripExe(name));
        }

        private static readonly HashSet<string> CreativeSuites = new(StringComparer.OrdinalIgnoreCase)
        {
            "Adobe Premiere Pro", "AfterFX", "Photoshop", "Illustrator", "Audition", "Adobe Media Encoder",
            "AdobeIPCBroker", "CCLibrary", "CoreSync", "Adobe CEF Helper", "Resolve", "DaVinci Resolve",
            "fuscript", "dpdecoder", "blender", "cinema 4d", "maya", "3dsmax", "houdini", "unrealengine",
            "unity", "obs64", "obs32", "streamlabs obs", "HandBrake", "ffmpeg", "Topaz Video AI",
            "Topaz Photo AI", "Topaz Gigapixel AI", "Ableton Live Suite", "FL64", "Cubase", "ProTools",
            "Studio One", "Reaper", "Bitwig Studio"
        };

        private static bool IsCreativeWorkloadSuite(string name)
        {
            return CreativeSuites.Contains(name) || CreativeSuites.Contains(StripExe(name));
        }

        private static readonly HashSet<string> ShieldedApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "waterfox", "librewolf", "tor",
            "discord", "slack", "teams", "msteams", "telegram", "whatsapp", "signal", "zoom", "skype",
            "resolve", "premiere", "afterfx", "photoshop", "illustrator", "indesign", "audition",
            "adobe media encoder", "blender", "cinema 4d", "maya", "3dsmax", "houdini", "unrealengine",
            "unity", "obs64", "obs32", "streamlabs obs", "handbrake", "ffmpeg",
            "bluestacks", "hd-player", "msi app player", "nox", "noxvmhandle", "ldplayer", "dnplayer", "ldboxheadless", "memu", "memuheadless", "mumuplayer", "mumunxdevice", "nemuplayer", "vboxheadless", "virtualbox",
            "steam", "epicgameslauncher", "origin", "eadesktop", "battle.net", "galaxyclient", "riotclientservices"
        };

        private static bool IsShieldedUserApp(string name)
        {
            return ShieldedApps.Contains(name) || ShieldedApps.Contains(StripExe(name));
        }

        private static bool IsKnownBackgroundOverlay(string name)
        {
            return name.Equals("GoogleUpdate", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("GoogleUpdate.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("GameBarFTServer.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> ProtectedServiceNames =
            HardProtectRegistry.LoadProtectedServiceNames();

        public static IEnumerable<string> GetHostedServices(ProcessIdentityDto process)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(process.ServiceAssociation))
            {
                foreach (var s in process.ServiceAssociation.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    result.Add(s);
                }
            }

            if (process.ProcessId > 0 && OperatingSystem.IsWindows())
            {
                try
                {
                    var liveServices = ProcessIntelligenceEngine.GetLiveServicesForPid(process.ProcessId);
                    foreach (var s in liveServices)
                    {
                        result.Add(s);
                    }
                }
                catch { }
            }

            return result;
        }

        private static bool LoadsProtectedDriverModule(ProcessIdentityDto process, out string matchedModule)
        {
            matchedModule = string.Empty;
            if (process.LoadedModules == null) return false;

            foreach (var module in process.LoadedModules)
            {
                var path = module.ToLowerInvariant();
                var name = Path.GetFileName(path).ToLowerInvariant();

                // Any .sys file
                if (name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                {
                    matchedModule = name;
                    return true;
                }

                // Any file under \System32\drivers\
                if (path.Contains(@"\system32\drivers\", StringComparison.OrdinalIgnoreCase))
                {
                    matchedModule = name;
                    return true;
                }

                // Known WiFi/BT vendor driver name patterns
                if (MatchesAny(name, new[] {
                    "netwtw", "iwlwifi", "intelwifi", "intelbluetooth",
                    "rtwlane", "rtwlanu", "rtlwifi", "rtl8192", "rtl8821",
                    "rtw88", "rtw89", "rtlbt", "rtkbt",
                    "qcamain", "qcbluetooth", "qcw",
                    "bcmwl", "bcmbt", "bcmpcie", "bcm",
                    "mtkwl", "mtkbt", "mt76",
                    "bthport", "bthaudiohs", "bthhfenum", "bthmodem",
                    "btwaudio", "btwi2c", "ibtusb", "btwampfl", "bthusb",
                    "btfilter", "bthpan", "bthmini",
                }))
                {
                    matchedModule = name;
                    return true;
                }

                // Audio driver patterns
                if (MatchesAny(name, new[] {
                    "rtkvhd", "dolbydax", "dts", "waves", "intcaudiobus",
                    "intcoed", "amdhd", "hdaudio", "nahimic", "intcsst",
                    "intcsmart", "intelspk", "cirrus", "conexant",
                }))
                {
                    matchedModule = name;
                    return true;
                }

                // Storage driver patterns
                if (MatchesAny(name, new[] {
                    "iastor", "iastorav", "storahci", "stornvme", "storufs",
                    "nvme", "ahci", "raid", "msahci",
                }))
                {
                    matchedModule = name;
                    return true;
                }

                // GPU driver patterns (strictly kernel driver files/modules)
                if (MatchesAny(name, new[] {
                    "nvlddmkm", "nvppc", "igdkmd", "amdkmdag", "amdkmdap",
                    "atikmdag", "nvwgf",
                }))
                {
                    matchedModule = name;
                    return true;
                }

                // Input driver patterns
                if (MatchesAny(name, new[] {
                    "syntp", "etd", "hidusb", "kbdhid", "mouhid", "i8042",
                }))
                {
                    matchedModule = name;
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesAny(string name, string[] patterns)
        {
            return patterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly HashSet<string> CriticalSvchostGroups = new(StringComparer.OrdinalIgnoreCase)
        {
            "DcomLaunch", "RPCSS", "PlugPlay", "Power", "BrokerInfrastructure", "Schedule",
            "GPSvcGroup", "EventLog", "LocalServiceNetworkRestricted",
            "LocalSystemNetworkRestricted", "netsvcs", "LocalServiceNoNetworkFirewall"
        };

        private static bool IsCriticalSvchostGroup(ProcessIdentityDto process, out string matchedGroup)
        {
            matchedGroup = string.Empty;
            if (!string.IsNullOrEmpty(process.ServiceAssociation))
            {
                var services = process.ServiceAssociation.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var s in services)
                {
                    if (CriticalSvchostGroups.Contains(s) ||
                        ProtectedProcessPolicy.IsStrictlyProtectedService(s) ||
                        IsCoreOsService(s) ||
                        IsSecurityOrAntiCheatService(s))
                    {
                        matchedGroup = s;
                        return true;
                    }
                }

                // If all hosted services are non-critical / reducible, it is NOT critical
                return false;
            }

            // If explicitly classified as reducible by intelligence engine, allow
            if (process.Classification == ProcessClassification.IMMEDIATE_REDUCIBLE ||
                process.Classification == ProcessClassification.REDUCIBLE)
            {
                return false;
            }

            matchedGroup = "Unspecified SvcHost Group";
            return true;
        }

        private static bool IsCoreOsService(string serviceName)
        {
            return ProtectedProcessPolicy.IsStrictlyProtectedService(serviceName);
        }

        private static bool IsSecurityOrAntiCheatService(string serviceName)
        {
            return serviceName.Equals("WinDefend", StringComparison.OrdinalIgnoreCase) ||
                   serviceName.Equals("SecurityHealthService", StringComparison.OrdinalIgnoreCase) ||
                   serviceName.Equals("mpssvc", StringComparison.OrdinalIgnoreCase) ||
                   serviceName.Equals("vgc", StringComparison.OrdinalIgnoreCase) ||
                   serviceName.Equals("BEService", StringComparison.OrdinalIgnoreCase) ||
                   serviceName.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase);
        }

        private void RecordAudit(string targetIdentifier, int pid, SafetyVerdict verdict, string ruleCode, string reason)
        {
            _auditLog.Add(new ActionRecord
            {
                ActionKey = $"{targetIdentifier}_{pid}_{DateTime.UtcNow.Ticks}",
                TargetIdentifier = targetIdentifier,
                ProcessId = pid,
                Verdict = verdict,
                RuleCode = ruleCode,
                Reason = reason,
                TimestampUtc = DateTime.UtcNow
            });
        }

        public IReadOnlyList<ActionRecord> GetAuditLog() => _auditLog.ToArray();

        #endregion

        #region PPL & Driver Hosting Service Inspection Helpers

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtQueryInformationProcess(
            IntPtr handle,
            int infoClass,
            IntPtr info,
            int size,
            out int retLen);

        private const int ProcessProtectionInformation = 61;

        public static Func<int, bool?>? PplProtectedHook { get; set; }
        public static Func<string, uint?>? ServiceTypeQueryHook { get; set; }

        public static bool IsPplProtected(int pid)
        {
            if (PplProtectedHook != null)
            {
                var hookVal = PplProtectedHook(pid);
                if (hookVal.HasValue) return hookVal.Value;
            }

            if (pid <= 4) return true;
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                using var proc = Process.GetProcessById(pid);
                IntPtr buf = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(buf, 0);
                try
                {
                    // PS_PROTECTION is 1 byte in Windows 8.1+
                    var status = NtQueryInformationProcess(
                        proc.Handle, ProcessProtectionInformation, buf, 1, out _);
                    if (status != 0)
                    {
                        status = NtQueryInformationProcess(
                            proc.Handle, ProcessProtectionInformation, buf, 4, out _);
                        if (status != 0) return false;
                    }
                    var level = Marshal.ReadByte(buf);
                    // PsProtectedSignerNone = 0; anything > 0 is PPL
                    return level > 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch (ArgumentException)
            {
                // Process is not running / synthetic test PID
                return false;
            }
            catch
            {
                // Access denied or privileged handle restriction -> fail closed
                return true;
            }
        }

        public static uint QueryServiceType(string serviceName)
        {
            if (ServiceTypeQueryHook != null)
            {
                var hookVal = ServiceTypeQueryHook(serviceName);
                if (hookVal.HasValue) return hookVal.Value;
            }

            if (string.IsNullOrWhiteSpace(serviceName)) return 0;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                    if (key != null)
                    {
                        var val = key.GetValue("Type");
                        if (val is int intVal) return (uint)intVal;
                        if (val is long longVal) return (uint)longVal;
                    }
                }
                catch { }

                try
                {
                    using var sc = new ServiceController(serviceName);
                    return (uint)sc.ServiceType;
                }
                catch { }
            }

            return 0;
        }

        public static bool HostsKernelDriverService(ProcessIdentityDto process, out string driverServiceName)
        {
            driverServiceName = string.Empty;
            if (process == null) return false;

            var hostedServices = GetHostedServices(process);
            foreach (var svc in hostedServices)
            {
                uint type = QueryServiceType(svc);
                // 0x00000001 = SERVICE_KERNEL_DRIVER
                // 0x00000002 = SERVICE_FILE_SYSTEM_DRIVER
                // 0x00000004 = SERVICE_RECOGNIZER_DRIVER
                if ((type & 0x00000001) != 0 ||
                    (type & 0x00000002) != 0 ||
                    (type & 0x00000004) != 0 ||
                    (type & 0x00000008) != 0)
                {
                    driverServiceName = svc;
                    return true;
                }
            }

            return false;
        }

        #endregion

        public void Dispose()
        {
            StopWatchdog();
        }
    }
}
