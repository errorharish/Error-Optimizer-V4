#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Threading.Tasks;
using BiosOptimizer.Core.V4.Diagnostics;
using BiosOptimizer.Core.V4.Environments;
using BiosOptimizer.Core.V4.Persistent;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workloads;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;
using ActiveWorkload = BiosOptimizer.Core.V4.Workload.ActiveWorkload;
using WorkloadDetector = BiosOptimizer.Core.V4.Workload.WorkloadDetector;

namespace BiosOptimizer.Core.V4.Intelligence
{
    /// <summary>
    /// Machine-aware Process Intelligence Engine.
    /// Replaces simplistic name heuristics with comprehensive multi-vector discovery:
    /// identity, publisher, digital signature, real parent-child tree hierarchy,
    /// service association, window visibility, and workload-aware classification.
    /// </summary>
    public class ProcessIntelligenceEngine
    {
        private readonly ProtectedProcessPolicy _safetyPolicy;
        private readonly WorkloadDetectionEngine _workloadEngine;

        public ClassificationDump? LastDump { get; private set; }

        public ProcessIntelligenceEngine(ProtectedProcessPolicy? safetyPolicy = null, WorkloadDetectionEngine? workloadEngine = null)
        {
            _safetyPolicy = safetyPolicy ?? new ProtectedProcessPolicy();
            _workloadEngine = workloadEngine ?? new WorkloadDetectionEngine();
        }

        public async Task<ProcessSnapshotDto> CaptureSnapshotAsync()
        {
            return await Task.Run(() =>
            {
                var snapshot = new ProcessSnapshotDto
                {
                    Timestamp = DateTime.UtcNow
                };

                Process[] processes;
                try
                {
                    processes = Process.GetProcesses();
                }
                catch
                {
                    processes = Array.Empty<Process>();
                }

                snapshot.TotalProcessCount = processes.Length;

                // 1. Detect system-wide active workloads (Gaming & Editing)
                var workloadStatus = _workloadEngine.DetectActiveWorkloads(processes);

                // 2. Collect foreground and visible window PIDs
                var visibleWindowPids = GetPidsWithVisibleWindows();
                int fgPid = GetForegroundProcessId();

                // 3. Track real Win32 parent-child process hierarchy
                var (parentChildMap, childParentMap) = BuildRealProcessHierarchy();

                // 4. Map Windows Services to Process IDs
                var servicePidMap = BuildServicePidMap();

                // 5. Build lookup map of visible parent PIDs
                var activeParentPids = new HashSet<int>();
                foreach (var p in processes)
                {
                    if (visibleWindowPids.Contains(p.Id) || p.Id == fgPid)
                    {
                        activeParentPids.Add(p.Id);
                    }
                }

                // 6. Multi-point delta CPU sampling over a controlled 100ms window
                var cpuStartTimes = new Dictionary<int, TimeSpan>(processes.Length);
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.Id > 4) cpuStartTimes[p.Id] = p.TotalProcessorTime;
                    }
                    catch { }
                }

                System.Threading.Thread.Sleep(100);

                var cpuPercents = new Dictionary<int, double>(processes.Length);
                int coreCount = Math.Max(1, Environment.ProcessorCount);
                foreach (var p in processes)
                {
                    try
                    {
                        if (cpuStartTimes.TryGetValue(p.Id, out var startTime))
                        {
                            var delta = p.TotalProcessorTime - startTime;
                            if (delta.TotalMilliseconds > 0)
                            {
                                double pct = (delta.TotalMilliseconds / (100.0 * coreCount)) * 100.0;
                                cpuPercents[p.Id] = Math.Clamp(Math.Round(pct, 1), 0.0, 100.0);
                            }
                        }
                    }
                    catch { }
                }

                // 7. Associate processes with live Startup and Scheduled Task provenance (normalized executable path)
                var (startupPathMap, uwpStartupFamilyMap) = DiscoverLiveStartupProvenance();
                var taskProvenanceMap = DiscoverLiveScheduledTaskProvenance();

                // 8. Discover duplicate background instances and persistent background CPU consumers
                var duplicatePids = new HashSet<int>();
                var seenProcNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in processes)
                {
                    if (p.Id <= 4) continue;
                    string name = p.ProcessName;
                    if (seenProcNames.TryGetValue(name, out int firstPid))
                    {
                        if (!visibleWindowPids.Contains(p.Id) && p.Id != fgPid)
                        {
                            duplicatePids.Add(p.Id);
                        }
                    }
                    else
                    {
                        seenProcNames[name] = p.Id;
                    }
                }

                var persistentCpuPids = new HashSet<int>();
                foreach (var kvp in cpuPercents)
                {
                    if (kvp.Value >= 1.0 && !visibleWindowPids.Contains(kvp.Key) && kvp.Key != fgPid && kvp.Key > 4)
                    {
                        persistentCpuPids.Add(kvp.Key);
                    }
                }

                var list = new List<ProcessIdentityDto>(processes.Length);

                foreach (var p in processes)
                {
                    var identity = AnalyzeProcess(
                        p,
                        visibleWindowPids,
                        fgPid,
                        parentChildMap,
                        childParentMap,
                        servicePidMap,
                        activeParentPids,
                        workloadStatus,
                        cpuPercents,
                        startupPathMap,
                        uwpStartupFamilyMap,
                        taskProvenanceMap,
                        duplicatePids,
                        persistentCpuPids);

                    list.Add(identity);
                }

                // Propagate Self-Protection, V4 Protected Application Policy, and Shielded User Applications
                // down verified parent-child process trees (validating parent.StartTime <= child.StartTime + 2s).
                PropagateProtectedProcessTree(list);

                // Finalize Stage 2 provenance completeness, UNKNOWN diagnostics, and summary metrics
                FinalizeProvenanceDiagnostics(list);
                var provenanceDiagnostics = BuildProvenanceDiagnostics(list);

                snapshot.Processes = list;
                snapshot.ProvenanceDiagnostics = provenanceDiagnostics;
                snapshot.ProtectedProcessCount = list.Count(p => p.Classification == ProcessClassification.PROTECTED);
                snapshot.RequiredProcessCount = list.Count(p => p.Classification == ProcessClassification.REQUIRED);
                snapshot.WorkloadProcessCount = list.Count(p => p.Classification == ProcessClassification.WORKLOAD_REQUIRED);
                snapshot.ActiveAppProcessCount = list.Count(p => p.Classification == ProcessClassification.ACTIVE_APPLICATION);
                snapshot.OptionalProcessCount = list.Count(p => p.Classification == ProcessClassification.OPTIONAL);
                snapshot.BackgroundProcessCount = list.Count(p => p.Classification == ProcessClassification.BACKGROUND);
                snapshot.ReducibleProcessCount = list.Count(p => p.Classification == ProcessClassification.REDUCIBLE || p.Classification == ProcessClassification.IMMEDIATE_REDUCIBLE);
                snapshot.UnknownProcessCount = list.Count(p => p.Classification == ProcessClassification.UNKNOWN);

                // Build and persist ClassificationDump (every process must be accounted for)
                BuildClassificationDump(list, provenanceDiagnostics);

                // Cleanup process handles
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }

                return snapshot;
            });
        }

        public ClassificationDump BuildClassificationDump(
            IReadOnlyList<ProcessIdentityDto> processes,
            ProvenanceDiagnosticsDto? provenanceDiagnostics = null)
        {
            var countByClass = new Dictionary<ProcessClassification, int>();
            foreach (ProcessClassification pc in Enum.GetValues(typeof(ProcessClassification)))
            {
                countByClass[pc] = 0;
            }
            foreach (var item in processes)
            {
                countByClass[item.Classification] = countByClass.GetValueOrDefault(item.Classification, 0) + 1;
            }

            var rejections = new List<RejectionRecord>();
            var candidateRejectionExplanations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int reducibleCount = 0;

            foreach (var proc in processes)
            {
                if (proc.Classification == ProcessClassification.IMMEDIATE_REDUCIBLE ||
                    proc.Classification == ProcessClassification.REDUCIBLE)
                {
                    reducibleCount++;
                }
                else
                {
                    string ruleCode = proc.Classification switch
                    {
                        ProcessClassification.REQUIRED => "S01",
                        ProcessClassification.WORKLOAD_REQUIRED => "R01",
                        ProcessClassification.PROTECTED => "S02",
                        ProcessClassification.ACTIVE_APPLICATION => proc.IsForeground ? "R02" : (proc.HasVisibleWindow ? "R03" : "R04"),
                        ProcessClassification.OPTIONAL => "R05",
                        ProcessClassification.BACKGROUND => "R06",
                        _ => "R07"
                    };

                    string explanation = proc.Classification switch
                    {
                        ProcessClassification.REQUIRED => $"Non-reducible: {proc.ProcessName} is a core Windows OS system component ({proc.ClassificationReason}).",
                        ProcessClassification.PROTECTED => $"Non-reducible: {proc.ProcessName} is hard-protected or security/driver infrastructure ({proc.ClassificationReason}).",
                        ProcessClassification.WORKLOAD_REQUIRED => $"Non-reducible: {proc.ProcessName} is actively utilized by gaming/creative/emulator workload ({proc.ClassificationReason}).",
                        ProcessClassification.ACTIVE_APPLICATION => $"Non-reducible: {proc.ProcessName} has an active user UI/foreground window ({proc.ClassificationReason}).",
                        ProcessClassification.OPTIONAL => $"Candidate deferred: {proc.ProcessName} is an optional secondary utility ({proc.ClassificationReason}).",
                        ProcessClassification.BACKGROUND => $"Candidate deferred: {proc.ProcessName} is a session 0 service pending Tier 2 escalation ({proc.ClassificationReason}).",
                        _ => $"Candidate rejected: {proc.ProcessName} fell through evidence gates and was retained for safety ({proc.ClassificationReason})."
                    };

                    candidateRejectionExplanations[$"{proc.ProcessName}:{proc.ProcessId}"] = explanation;

                    rejections.Add(new RejectionRecord(
                        proc.ProcessId,
                        proc.ProcessName,
                        proc.ExecutablePath,
                        proc.Classification,
                        proc.ClassificationReason,
                        ruleCode,
                        explanation
                    ));
                }
            }

            var dump = new ClassificationDump
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                InitialProcessCount = processes.Count,
                CountByClass = countByClass,
                ClassifiedReducibleCount = reducibleCount,
                Rejections = rejections,
                CandidateRejectionExplanations = candidateRejectionExplanations,
                ProvenanceDiagnostics = provenanceDiagnostics ?? BuildProvenanceDiagnostics(processes)
            };

            LastDump = dump;
            ClassificationDump.SaveDump(dump);
            return dump;
        }

        private ProcessIdentityDto AnalyzeProcess(
            Process p,
            HashSet<int> visibleWindowPids,
            int fgPid,
            Dictionary<int, List<int>> parentChildMap,
            Dictionary<int, (int ParentPid, string ParentName)> childParentMap,
            Dictionary<int, string> servicePidMap,
            HashSet<int> activeParentPids,
            WorkloadStatus workloadStatus,
            Dictionary<int, double> cpuPercents,
            Dictionary<string, List<StartupProvenanceDto>> startupPathMap,
            Dictionary<string, List<StartupProvenanceDto>> uwpStartupFamilyMap,
            Dictionary<string, List<ScheduledTaskProvenanceDto>> taskProvenanceMap,
            HashSet<int>? duplicatePids = null,
            HashSet<int>? persistentCpuPids = null)
        {
            var identity = new ProcessIdentityDto
            {
                ProcessId = p.Id,
                ProcessName = p.ProcessName,
                DisplayName = p.ProcessName,
                SessionId = GetSafeSessionId(p)
            };

            // CPU Utilization (from measured sample delta)
            if (cpuPercents.TryGetValue(p.Id, out double cpuVal))
            {
                identity.CpuUsagePercent = cpuVal;
            }

            // Memory: Working Set, Private Bytes, and Peak Working Set
            try
            {
                identity.RamWorkingSetMb = Math.Round(p.WorkingSet64 / (1024.0 * 1024.0), 1);
                identity.PrivateMemoryMb = Math.Round(p.PrivateMemorySize64 / (1024.0 * 1024.0), 1);
                identity.PeakWorkingSetMb = Math.Round(p.PeakWorkingSet64 / (1024.0 * 1024.0), 1);
            }
            catch
            {
                identity.RamWorkingSetMb = 0;
                identity.PrivateMemoryMb = 0;
                identity.PeakWorkingSetMb = 0;
            }

            if (duplicatePids != null && duplicatePids.Contains(p.Id))
            {
                identity.IsDuplicateInstance = true;
            }

            if (persistentCpuPids != null && persistentCpuPids.Contains(p.Id))
            {
                identity.IsPersistentBackgroundConsumer = true;
            }

            // Process Start Time (64-bit FILETIME via GetProcessTimes)
            identity.StartTime = ProcessIdentitySafety.GetProcessStartTime(p.Id);

            // Task A: Reliable Executable Path Resolution (MainModule -> native QueryFullProcessImageNameW fallback)
            bool pathResolved = ProcessIdentitySafety.TryResolveExecutablePath(
                p.Id,
                p,
                out string resolvedExePath,
                out string resolutionMethod,
                out string pathDiagnostic);

            identity.ExecutablePath = pathResolved ? resolvedExePath : string.Empty;
            identity.ExecutablePathResolutionMethod = resolutionMethod;
            identity.ExecutablePathDiagnostic = pathDiagnostic;

            // Task B: AppX / MSIX Package-Family Provenance (GetPackageFamilyName)
            bool hasAppxPackage = ProcessIdentitySafety.TryGetPackageFamilyName(
                p.Id,
                out string packageFamilyName,
                out string appxStatus);

            identity.AppxPackageFamily = hasAppxPackage ? packageFamilyName : string.Empty;
            identity.AppxPackageStatus = appxStatus;

            // Task C: Live Scheduled Task Provenance Correlation (normalized executable path only)
            CorrelateScheduledTasks(identity, taskProvenanceMap);

            // Task D: Live Startup Origin Provenance Correlation (normalized executable path + UWP package family)
            CorrelateStartupOrigins(identity, startupPathMap, uwpStartupFamilyMap);

            if (!string.IsNullOrEmpty(identity.ExecutablePath) && File.Exists(identity.ExecutablePath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(identity.ExecutablePath);
                    identity.Publisher = vi.CompanyName ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                    {
                        identity.DisplayName = vi.FileDescription.Trim();
                    }
                }
                catch { }

                // Digital signature check
                try
                {
                    var cert = X509Certificate.CreateFromSignedFile(identity.ExecutablePath);
                    identity.IsDigitallySigned = true;
                    identity.SignerName = cert.Subject;
                }
                catch
                {
                    identity.IsDigitallySigned = false;
                }
            }

            // Loaded Modules Inspection (for Driver-Module invariant R27)
            try
            {
                var modNames = new List<string>();
                foreach (ProcessModule m in p.Modules)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(m.FileName))
                        {
                            modNames.Add(m.FileName);
                        }
                    }
                    catch { }
                }
                identity.LoadedModules = modNames;
            }
            catch
            {
                identity.LoadedModules = new List<string>();
            }

            // Window and UI State
            identity.HasVisibleWindow = visibleWindowPids.Contains(p.Id);
            identity.IsForeground = (p.Id == fgPid && p.Id != 0);

            // Hierarchy Mapping
            if (childParentMap.TryGetValue(p.Id, out var parentInfo))
            {
                identity.ParentProcessId = parentInfo.ParentPid;
                identity.ParentProcessName = parentInfo.ParentName;
            }

            if (parentChildMap.TryGetValue(p.Id, out var children))
            {
                identity.HasChildProcesses = children.Count > 0;
                identity.ChildProcessIds = children;
            }

            // Service Association Mapping
            if (servicePidMap.TryGetValue(p.Id, out var sName))
            {
                identity.ServiceAssociation = sName;
            }

            // Check if process belongs to an active parent application (e.g. Chrome / Discord / Game renderer worker)
            bool isChildOfActiveApp = IsDescendantOfActiveApp(p.Id, activeParentPids, childParentMap);

            // Multi-Vector Classification
            Classify(identity, workloadStatus, isChildOfActiveApp);

            return identity;
        }

        private static bool IsDescendantOfActiveApp(
            int pid,
            HashSet<int> activeParentPids,
            Dictionary<int, (int ParentPid, string ParentName)> childParentMap)
        {
            int current = pid;
            int depth = 0;
            while (depth++ < 10 && childParentMap.TryGetValue(current, out var parentInfo) && parentInfo.ParentPid > 0)
            {
                if (activeParentPids.Contains(parentInfo.ParentPid))
                {
                    return true;
                }
                current = parentInfo.ParentPid;
            }
            return false;
        }

        public static void PropagateProtectedProcessTree(IList<ProcessIdentityDto> processes)
        {
            if (processes == null || processes.Count == 0)
                return;

            var tree = new Dictionary<int, ProcessIdentityDto>(processes.Count);
            foreach (var proc in processes)
            {
                if (proc.ProcessId > 0)
                {
                    tree[proc.ProcessId] = proc;
                }
            }

            foreach (var proc in processes)
            {
                // 1. Task F: Explicit Self-Protection & Verified Descendant Tree (e.g. ERROR OPTIMIZER PROTOTYPE.exe -> child msedgewebview2)
                if (proc.ProcessId > 4 && ProtectedProcessPolicy.IsSelfOrVerifiedDescendant(proc, tree, out string selfReason))
                {
                    proc.IsVerifiedSelfOrDescendant = true;
                    if (proc.Classification != ProcessClassification.REQUIRED)
                    {
                        proc.Classification = ProcessClassification.PROTECTED;
                        proc.RiskLevel = ProcessRisk.CRITICAL_SYSTEM;
                        proc.SafetyConfidence = 1.0;
                        proc.ProtectionRuleCode = "R01_SELF";
                        proc.ProtectionDecision = "Protected";
                        proc.ClassificationReason = $"Self-Protection: {selfReason}";
                    }
                    continue;
                }

                if (proc.Classification == ProcessClassification.REQUIRED ||
                    proc.Classification == ProcessClassification.PROTECTED)
                {
                    continue;
                }

                // 2. Task E: Verified ProtectedApplicationPolicy Descendant Propagation (checks parent.StartTime <= child.StartTime + 2s)
                var match = ProtectedApplicationPolicy.Instance.EvaluateProcess(proc, tree);
                if (match.IsProtected)
                {
                    proc.Classification = ProcessClassification.PROTECTED;
                    proc.RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY;
                    proc.SafetyConfidence = 1.0;
                    proc.ProtectionRuleCode = "R28_PROTECTED_APP";
                    proc.ProtectionDecision = "Protected";
                    proc.WorkloadAssociation = $"{match.CategoryLabel} ({match.VendorDisplayName})";
                    proc.ClassificationReason = $"Protected Application ({match.CategoryLabel} - {match.VendorDisplayName}): {match.Reason}";
                    continue;
                }

                // 3. Task E: Verified ShieldedUserApplications Descendant Propagation (Antigravity, VS Code, Chrome, IDEs, etc.)
                if (ProtectedProcessPolicy.TryFindVerifiedShieldedAncestor(proc, tree, out var shieldedAncestor) &&
                    shieldedAncestor != null)
                {
                    proc.IsShieldedDescendant = true;
                    proc.Classification = ProcessClassification.PROTECTED;
                    proc.RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY;
                    proc.SafetyConfidence = 1.0;
                    proc.ProtectionRuleCode = "R07_SHIELDED_DESCENDANT";
                    proc.ProtectionDecision = "Protected";
                    proc.WorkloadAssociation = $"Shielded Application Tree ({shieldedAncestor.ProcessName})";
                    proc.ClassificationReason = $"Verified descendant of shielded user application '{shieldedAncestor.ProcessName}' (PID {shieldedAncestor.ProcessId})";
                    continue;
                }

                // 4. Verified Security/Defender parent descendant propagation (e.g. MsMpEng -> DefenderSessionHelper)
                if (proc.ParentProcessId > 4 &&
                    tree.TryGetValue(proc.ParentProcessId, out var secParent) &&
                    secParent.ProtectionRuleCode.Equals("HP_SECURITY", StringComparison.OrdinalIgnoreCase) &&
                    ProcessIdentitySafety.IsVerifiedParentChild(secParent, proc))
                {
                    proc.Classification = ProcessClassification.PROTECTED;
                    proc.RiskLevel = ProcessRisk.CRITICAL_SYSTEM;
                    proc.SafetyConfidence = 1.0;
                    proc.ProtectionRuleCode = "HP_SECURITY";
                    proc.ProtectionDecision = "Protected";
                    proc.ClassificationReason = $"Verified child of security infrastructure '{secParent.ProcessName}' (PID {secParent.ProcessId})";
                }
            }
        }

        /// <summary>
        /// Task I: Populates per-process provenance completeness, protection rule codes,
        /// missing provenance fields, and explicit UNKNOWN retention diagnostics without
        /// ever forcing UNKNOWN processes into REDUCIBLE or BACKGROUND.
        /// </summary>
        public static void FinalizeProvenanceDiagnostics(IList<ProcessIdentityDto> processes)
        {
            if (processes == null) return;

            foreach (var proc in processes)
            {
                var missing = new List<string>();
                if (proc.ProcessId > 4)
                {
                    if (string.IsNullOrWhiteSpace(proc.ExecutablePath))
                        missing.Add($"ExecutablePath({proc.ExecutablePathResolutionMethod})");
                    if (proc.StartTime == default)
                        missing.Add("StartTime");
                    if (string.IsNullOrWhiteSpace(proc.Publisher))
                        missing.Add("Publisher");
                    if (proc.AppxPackageStatus.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
                        missing.Add("AppxPackageFamily");
                }
                proc.MissingProvenanceFields = missing;

                bool hasPath = !string.IsNullOrWhiteSpace(proc.ExecutablePath);
                bool hasStart = proc.StartTime != default;
                bool hasAppxChecked = !proc.AppxPackageStatus.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);

                if (proc.ProcessId <= 4)
                {
                    proc.ProvenanceCompleteness = "Minimal";
                }
                else if (hasPath && hasStart && hasAppxChecked)
                {
                    proc.ProvenanceCompleteness = "Complete";
                }
                else if (hasPath || hasStart)
                {
                    proc.ProvenanceCompleteness = "Partial";
                }
                else
                {
                    proc.ProvenanceCompleteness = "Minimal";
                }

                if (string.IsNullOrEmpty(proc.ProtectionRuleCode))
                {
                    proc.ProtectionRuleCode = proc.Classification switch
                    {
                        ProcessClassification.REQUIRED => "R01_KERNEL_OS",
                        ProcessClassification.PROTECTED => proc.IsVerifiedSelfOrDescendant
                            ? "R01_SELF"
                            : (proc.IsShieldedDescendant ? "R07_SHIELDED_DESCENDANT" : "R07_PROTECTED_POLICY"),
                        ProcessClassification.WORKLOAD_REQUIRED => "R17_WORKLOAD",
                        ProcessClassification.ACTIVE_APPLICATION => proc.IsForeground ? "R07_FOREGROUND" : "R07_ACTIVE_UI",
                        ProcessClassification.REDUCIBLE => "ELIGIBLE_CANDIDATE",
                        ProcessClassification.OPTIONAL => "DEFERRED_OPTIONAL",
                        ProcessClassification.BACKGROUND => "DEFERRED_SESSION0_BACKGROUND",
                        _ => "FAIL_CLOSED_UNKNOWN"
                    };
                }

                if (string.IsNullOrEmpty(proc.ProtectionDecision))
                {
                    proc.ProtectionDecision = proc.Classification switch
                    {
                        ProcessClassification.REDUCIBLE => "Allowed",
                        ProcessClassification.UNKNOWN => "FailClosed",
                        _ => "Protected"
                    };
                }

                if (proc.Classification == ProcessClassification.UNKNOWN)
                {
                    proc.ProtectionRuleCode = "FAIL_CLOSED_UNKNOWN";
                    proc.ProtectionDecision = "FailClosed";
                    if (string.IsNullOrEmpty(proc.ClassificationFailureReason))
                    {
                        string missingSummary = missing.Count > 0
                            ? $"Missing provenance fields: {string.Join(", ", missing)}."
                            : "Full provenance available, but no verified system, protected, workload, or reducible signature matched.";
                        string pathInfo = string.IsNullOrEmpty(proc.ExecutablePathDiagnostic)
                            ? string.Empty
                            : $" Path resolution: {proc.ExecutablePathResolutionMethod} ({proc.ExecutablePathDiagnostic}).";
                        proc.ClassificationFailureReason = $"Retained fail-closed as UNKNOWN. {missingSummary}{pathInfo}".Trim();
                    }
                }
            }
        }

        /// <summary>
        /// Task J: Builds the structured Stage 2 provenance diagnostic summary for Core Optimization reporting.
        /// </summary>
        public static ProvenanceDiagnosticsDto BuildProvenanceDiagnostics(IReadOnlyList<ProcessIdentityDto> processes)
        {
            var diag = new ProvenanceDiagnosticsDto
            {
                TotalProcessesInspected = processes.Count,
                PidReuseValidationFailures = (int)Math.Min(int.MaxValue, ProcessIdentitySafety.PidReuseValidationFailureCount)
            };

            var tree = new Dictionary<int, ProcessIdentityDto>(processes.Count);
            foreach (var p in processes)
            {
                if (p.ProcessId > 0)
                    tree[p.ProcessId] = p;
            }

            foreach (var p in processes)
            {
                if (!string.IsNullOrWhiteSpace(p.ExecutablePath))
                {
                    diag.ExecutablePathsResolved++;
                    if (p.ExecutablePathResolutionMethod.Equals("NativeQueryFullProcessImageName", StringComparison.OrdinalIgnoreCase))
                    {
                        diag.ExecutablePathsResolvedNativeFallback++;
                    }
                }
                else
                {
                    diag.ExecutablePathsUnresolved++;
                }

                if (!string.IsNullOrWhiteSpace(p.AppxPackageFamily))
                    diag.AppxAssociationsDiscovered++;

                if (p.ScheduledTaskAssociations.Count > 0)
                    diag.ScheduledTaskAssociationsDiscovered++;

                if (p.StartupOrigins.Count > 0)
                    diag.StartupAssociationsDiscovered++;

                if (p.ParentProcessId > 0 &&
                    tree.TryGetValue(p.ParentProcessId, out var parentProc) &&
                    ProcessIdentitySafety.IsVerifiedParentChild(parentProc, p))
                {
                    diag.ParentChildRelationshipsVerified++;
                }

                if (p.IsShieldedDescendant)
                    diag.ShieldedDescendantsIdentified++;

                if (p.IsVerifiedSelfOrDescendant)
                    diag.SelfProtectionChecksPassed++;

                if (p.Classification == ProcessClassification.UNKNOWN)
                    diag.UnknownClassifications++;

                string ruleKey = string.IsNullOrWhiteSpace(p.ProtectionRuleCode)
                    ? p.Classification.ToString()
                    : p.ProtectionRuleCode;
                diag.ProtectionReasonsByCategory[ruleKey] = diag.ProtectionReasonsByCategory.GetValueOrDefault(ruleKey, 0) + 1;
            }

            return diag;
        }

        private void Classify(ProcessIdentityDto identity, WorkloadStatus workloadStatus, bool isChildOfActiveApp)
        {
            string name = identity.ProcessName;
            string path = identity.ExecutablePath.ToLowerInvariant();

            // 0. Task F: Explicit Self-Protection BEFORE any candidate classification
            if (ProtectedProcessPolicy.IsSelfOrOptimizerHost(identity, out string selfReason))
            {
                identity.IsVerifiedSelfOrDescendant = true;
                identity.Classification = ProcessClassification.PROTECTED;
                identity.RiskLevel = ProcessRisk.CRITICAL_SYSTEM;
                identity.SafetyConfidence = 1.0;
                identity.ProtectionRuleCode = "R01_SELF";
                identity.ProtectionDecision = "Protected";
                identity.ClassificationReason = $"Self-Protection: {selfReason}";
                return;
            }

            // 1. Kernel / Critical Core OS
            if (identity.ProcessId <= 4 ||
                name.Equals("csrss", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("wininit", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("services", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("lsass", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("smss", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Secure System", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("fontdrvhost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dwm", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("sihost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("taskhostw", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("winlogon", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ctfmon", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dasHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase))
            {
                identity.Classification = ProcessClassification.REQUIRED;
                identity.RiskLevel = ProcessRisk.CRITICAL_SYSTEM;
                identity.SafetyConfidence = 1.0;
                identity.ProtectionRuleCode = "R01_KERNEL_OS";
                identity.ProtectionDecision = "Protected";
                identity.ClassificationReason = "Core Windows OS System Architecture";
                return;
            }

            // Gate 1A: V4 Protected Application / Service Exclusion Policy (Creative/Editing, Remote Access, Media, VPN, Gaming/Steam, Lenovo Vantage)
            var protectedAppMatch = ProtectedApplicationPolicy.Instance.EvaluateProcess(identity);
            if (protectedAppMatch.IsProtected)
            {
                identity.Classification = ProcessClassification.PROTECTED;
                identity.RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY;
                identity.SafetyConfidence = 1.0;
                identity.ProtectionRuleCode = "R28_PROTECTED_APP";
                identity.ProtectionDecision = "Protected";
                identity.WorkloadAssociation = $"{protectedAppMatch.CategoryLabel} ({protectedAppMatch.VendorDisplayName})";
                identity.ClassificationReason = $"Protected Application ({protectedAppMatch.CategoryLabel} - {protectedAppMatch.VendorDisplayName}): {protectedAppMatch.Reason}";
                return;
            }

            // Gate 1B: HardProtectRegistry Check (SafetyKernel R01-R25, S01-S06, Connectivity, Audio, GPU, Storage, Input, Security, Power/Thermal)
            var env = EnvironmentDetector.Instance.CurrentInfo;
            if (HardProtectRegistry.Instance.IsProcessHardProtected(
                name,
                identity.ExecutablePath,
                identity.LoadedModules,
                env.IsLaptop,
                !env.IsOnAcPower,
                env.SystemManufacturer,
                env.HasBitLocker,
                out string hpCategory,
                out string hpReason))
            {
                identity.Classification = ProcessClassification.PROTECTED;
                identity.RiskLevel = ProcessRisk.CRITICAL_SYSTEM;
                identity.SafetyConfidence = 1.0;
                identity.ProtectionRuleCode = $"HP_{hpCategory.ToUpperInvariant()}";
                identity.ProtectionDecision = "Protected";
                identity.ClassificationReason = $"Hard-Protected ({hpCategory}): {hpReason}";
                return;
            }

            // Gate 2: Active Workload Dependency (Gaming, Creative, Emulator, Streaming, etc.)
            if (_workloadEngine.IsWorkloadCritical(name, workloadStatus.IsGamingDetected, workloadStatus.IsEditingDetected, workloadStatus.IsEmulatorDetected))
            {
                identity.Classification = ProcessClassification.WORKLOAD_REQUIRED;
                identity.RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY;
                identity.SafetyConfidence = 1.0;
                identity.WorkloadAssociation = workloadStatus.IsGamingDetected ? "Gaming Engine / Platform" :
                    (workloadStatus.IsEmulatorDetected ? "Android Virtualization / Emulator Engine" : "Creative / Rendering Suite");
                identity.ClassificationReason = $"Active workload component: {identity.WorkloadAssociation}";
                return;
            }

            var activeWlReport = WorkloadDetector.Instance.DetectWorkloads();
            foreach (var wl in activeWlReport.ActiveWorkloads.Where(w => w != ActiveWorkload.None))
            {
                if (WorkloadDetector.Instance.IsProcessInWorkloadFamily(name, wl))
                {
                    identity.Classification = ProcessClassification.WORKLOAD_REQUIRED;
                    identity.RiskLevel = ProcessRisk.WORKLOAD_DEPENDENCY;
                    identity.SafetyConfidence = 1.0;
                    identity.WorkloadAssociation = wl.ToString();
                    identity.ClassificationReason = $"Active workload tree/vendor family protected ({wl})";
                    return;
                }
            }

            // 3. Protected Policy (Security, Active Drivers, Anti-Cheat, Input, Audio, Shielded Apps)
            if (_safetyPolicy.IsProcessProtected(identity, workloadStatus.IsGamingDetected, workloadStatus.IsEditingDetected))
            {
                identity.Classification = ProcessClassification.PROTECTED;
                identity.RiskLevel = _safetyPolicy.EvaluateRisk(identity, workloadStatus.IsGamingDetected, workloadStatus.IsEditingDetected);
                identity.SafetyConfidence = 0.95;
                identity.ClassificationReason = "Protected Windows Subsystem, Security, Driver, or Application Pipeline";
                return;
            }

            // 4. Active Desktop Applications (Visible UI or Foreground or child of active UI)
            if (identity.IsForeground)
            {
                identity.Classification = ProcessClassification.ACTIVE_APPLICATION;
                identity.RiskLevel = ProcessRisk.LOW_BACKGROUND;
                identity.SafetyConfidence = 0.95;
                identity.ClassificationReason = "Active foreground window in interactive use";
                return;
            }

            if (identity.HasVisibleWindow)
            {
                identity.Classification = ProcessClassification.ACTIVE_APPLICATION;
                identity.RiskLevel = ProcessRisk.LOW_BACKGROUND;
                identity.SafetyConfidence = 0.9;
                identity.ClassificationReason = "Visible top-level application window";
                return;
            }

            if (isChildOfActiveApp)
            {
                identity.Classification = ProcessClassification.ACTIVE_APPLICATION;
                identity.RiskLevel = ProcessRisk.LOW_BACKGROUND;
                identity.SafetyConfidence = 0.85;
                identity.ClassificationReason = $"Child worker process of active application ({identity.ParentProcessName})";
                return;
            }

            // 5. Verifiable Reducible Candidates (Telemetry, Updaters, OEM Bloat, Idle Webview Hosts, Reducible Services)
            if (IsVerifiableReducibleCandidate(identity, workloadStatus))
            {
                identity.Classification = ProcessClassification.REDUCIBLE;
                identity.RiskLevel = ProcessRisk.SAFE_TO_TERMINATE;
                identity.SafetyConfidence = 0.95;
                identity.ClassificationReason = "Verified non-essential background task (idle / telemetry / updater / OEM daemon)";
                return;
            }

            // 6. Optional Background Utilities (Non-critical secondary helper processes)
            if (IsOptionalBackgroundUtility(identity, workloadStatus))
            {
                identity.Classification = ProcessClassification.OPTIONAL;
                identity.RiskLevel = ProcessRisk.LOW_BACKGROUND;
                identity.SafetyConfidence = 0.85;
                identity.ClassificationReason = "Optional background utility (idle helper / non-essential daemon)";
                return;
            }

            // 7. Generic Session 0 Background Services (Not specifically marked reducible or protected)
            if (identity.SessionId == 0)
            {
                identity.Classification = ProcessClassification.BACKGROUND;
                identity.RiskLevel = ProcessRisk.LOW_BACKGROUND;
                identity.SafetyConfidence = 0.7;
                identity.ClassificationReason = "Windows session 0 background service";
                return;
            }

            // 8. Fallback: UNKNOWN (Defaults to safe handling - PROTECTED)
            identity.Classification = ProcessClassification.UNKNOWN;
            identity.RiskLevel = ProcessRisk.CRITICAL_SYSTEM;
            identity.SafetyConfidence = 0.5;
            identity.ClassificationReason = "Unverified background entity (defaulting to safe protected retention)";
        }

        private static bool IsVerifiableReducibleCandidate(ProcessIdentityDto identity, WorkloadStatus workload)
        {
            // Never mark self/optimizer host, verified descendants, or any V4 Protected Application or Service as reducible
            if (identity.IsVerifiedSelfOrDescendant ||
                identity.IsShieldedDescendant ||
                ProtectedProcessPolicy.IsSelfOrVerifiedDescendant(identity, null, out _) ||
                ProtectedApplicationPolicy.Instance.IsProcessProtected(identity))
            {
                return false;
            }

            string name = identity.ProcessName;
            string pub = identity.Publisher;
            string path = identity.ExecutablePath.ToLowerInvariant();
            // A. Associated with a verified Reducible Windows Service (provided no strictly protected service shares this PID)
            var liveServices = GetLiveServicesForPid(identity.ProcessId);
            string svc = liveServices.Count > 0 ? string.Join(",", liveServices) : identity.ServiceAssociation;

            if (!string.IsNullOrEmpty(svc))
            {
                var serviceList = svc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool hasProtected = false;
                bool hasReducible = false;

                foreach (var s in serviceList)
                {
                    string trimmed = s.Trim();
                    if (HardProtectRegistry.IsServiceProtected(trimmed) ||
                        ProtectedProcessPolicy.IsStrictlyProtectedService(trimmed) ||
                        ProtectedApplicationPolicy.Instance.IsServiceProtected(trimmed))
                    {
                        hasProtected = true;
                        break;
                    }
                    if (IsReducibleServiceName(trimmed))
                    {
                        hasReducible = true;
                    }
                }

                if (!hasProtected && hasReducible)
                {
                    return true;
                }
            }

            // B. Telemetry, Crash Reporters & Diagnostics
            if (name.Equals("CompatTelRunner", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DeviceCensus", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("wsqmcons", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Microsoft.Photos.AnalysisService", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Crashpad_handler", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CrashReport", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WerFault", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WerFaultSecure", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("diagtrack", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("werconcpl", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("feedbackhub", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("MoUsoCoreWorker", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Locator", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RpcLocator", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("unsecapp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("wermgr", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("compattelrunner", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SearchFilterHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SearchProtocolHost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // C. Standalone Third-Party Updaters & Update Assistants (Protected vendors excluded)
            if (name.Equals("GoogleUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("gupdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("gupdatem", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("edgeupdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("edgeupdatem", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("jusched", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CCleanerUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BraveUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("OperaUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DropboxUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DiscordUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("EpicOnlineServices", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("lghub_updater", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("OneDriveStandaloneUpdater", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ZoomUpdate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // D. OEM / Peripheral analytics & background telemetry agents (when not in gaming/editing; Lenovo Vantage excluded)
            if (!workload.IsGamingDetected && !workload.IsEditingDetected)
            {
                if (name.Equals("HPAppHelperCap", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("HPNetworkCap", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("HPSupportSolutionsFrameworkService", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("HPPrintScanDoctorService", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("TouchpointAnalytics", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DellDataVault", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DellSupportAssist", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DDVCollectorSvcApi", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AsusSystemAnalysis", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AsusSoftwareManager", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ArmouryCrate.ServiceEngine", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AsusAppService", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("LogiAnalytics", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("logi_lamparray_service.AMD64", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("logi_lamparray_service", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NhNotifSys", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SEGameTool", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("RazerSynapseService", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // E. Background Webview / Task Hosts with NO visible window
            if (!identity.HasVisibleWindow && !identity.IsForeground)
            {
                if (name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("backgroundTaskHost", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Widgets", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("WidgetService", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("WidgetBoard", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("MicrosoftStartFeedProvider", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("UserOOBEBroker", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ShellHost", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("PhoneExperienceHost", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // F. Idle Cloud sync clients with NO visible window and NO active creative render
            if (!workload.IsEditingDetected && !identity.HasVisibleWindow && !identity.IsForeground)
            {
                if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("GoogleDriveFS", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // G. Idle communication helpers with NO visible window
            if (!identity.HasVisibleWindow && !identity.IsForeground && !workload.IsGamingDetected && !workload.IsEditingDetected)
            {
                if (name.Equals("SkypeBackgroundHost", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // H. Gate 3 (a): Process under \AppData\ with parent = explorer or svchost, and no visible window
            if (identity.ExecutablePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) &&
                (identity.ParentProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                 identity.ParentProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase)) &&
                !identity.HasVisibleWindow && !identity.IsForeground)
            {
                return true;
            }

            // I. Gate 3 (b): OS-specific reducible fragment match (data/reducible_windows10.json, data/reducible_windows11.json)
            var osReducible = EnvironmentDetector.Instance.GetOsReducibleProcessNames();
            if (osReducible.Any(r => name.Equals(r, StringComparison.OrdinalIgnoreCase) || (name + ".exe").Equals(r, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // J. Gate 3 (c): Microsoft-signed non-critical OR Search/Widget hosts AND idle > 2 min AND no window AND no strictly protected service
            if (!identity.HasVisibleWindow && !identity.IsForeground)
            {
                if (name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("PhoneExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Cortana", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Widgets", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("WidgetService", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("smartscreen", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (identity.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(identity.ServiceAssociation) &&
                    (DateTime.UtcNow - identity.StartTime).TotalMinutes > 2 &&
                    identity.CpuUsagePercent < 1.0 &&
                    (name.Contains("GameBar", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("YourPhone", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("SearchUI", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Feedback", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("People", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            // K. Gate 3 (d): Enabled non-system Scheduled Task / Startup origin with low background activity
            // Excludes generic Windows host/interpreter binaries so provenance discovery does not promote OS hosts.
            bool isGenericWindowsHost =
                name.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dllhost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("rundll32", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("wscript", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("cscript", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("regsvr32", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WmiPrvSE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("schtasks", StringComparison.OrdinalIgnoreCase);

            bool hasActiveNonCoreTask = identity.ScheduledTaskAssociations.Any(t =>
                t.IsEnabled &&
                !t.TaskPath.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase));
            bool hasEnabledStartup = identity.StartupOrigins.Any(s =>
                s.EnabledStatus.Equals("Enabled", StringComparison.OrdinalIgnoreCase));

            if (!isGenericWindowsHost &&
                (hasActiveNonCoreTask || hasEnabledStartup) &&
                !path.StartsWith(Environment.SystemDirectory.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                if (!identity.HasVisibleWindow && !identity.IsForeground && identity.CpuUsagePercent < 0.5)
                {
                    return true;
                }
            }

            // L. Gate 3 (e): OEM updater / helper pattern with NO visible window and CPU < 1.0%
            if (!identity.HasVisibleWindow && !identity.IsForeground && identity.CpuUsagePercent < 1.0)
            {
                if (path.Contains(@"\oem\", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\updater\", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\telemetry\", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\crashreport\", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\helper\", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Helper", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CrashReport", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Analytics", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // M. Gate 3 (f): WebView2 / Electron / CEF helper whose host app has no visible window
            if (!identity.HasVisibleWindow && !identity.IsForeground)
            {
                if (name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NVIDIA Overlay", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("CefSharp.BrowserSubprocess", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\cef\", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\electron\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // N. Duplicate instances of background helpers or updaters without visible window
            if (identity.IsDuplicateInstance && !identity.HasVisibleWindow && !identity.IsForeground &&
                (name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Helper", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Crashpad", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Agent", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // O. Persistent background CPU consumer causing frame jitter without visible window or workload
            if (identity.IsPersistentBackgroundConsumer && !identity.HasVisibleWindow && !identity.IsForeground &&
                !workload.IsGamingDetected && !workload.IsEditingDetected && !workload.IsStreamingDetected && !workload.IsEmulatorDetected)
            {
                if (!identity.Publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
                    identity.ExecutablePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReducibleServiceName(string svc)
        {
            if (string.IsNullOrEmpty(svc)) return false;
            if (ProtectedApplicationPolicy.Instance.IsServiceProtected(svc)) return false;

            if (svc.Equals("DiagTrack", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("dmwappushservice", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("MapsBroker", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("RetailDemo", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("WerSvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("PcaSvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("RemoteRegistry", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("lfsvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("SensorService", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("SensrSvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("SensorDataService", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("wisvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("RpcLocator", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("SSDPSRV", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("LGHUBUpdaterService", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("logi_lamparray_service", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("GoogleUpdate", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("gupdate", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("gupdatem", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("edgeupdate", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("edgeupdatem", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("CCleaner Crash Reporting", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("BraveElevationService", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("Opera Scheduled Autoupdate", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("DropboxUpdate", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("EpicOnlineServices", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("HPAppHelperCap", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("HPNetworkCap", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("HPSupportSolutionsFrameworkService", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("HPPrintScanDoctorService", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("DellDataVault", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("DellSupportAssist", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("AsusSystemAnalysis", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("AsusSoftwareManager", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("ArmouryCrate.ServiceEngine", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("LogiAnalytics", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("RazerSynapseService", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (svc.StartsWith("OneSyncSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("WpnUserService_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("CDPUserSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("MessagingService_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("PimIndexMaintenanceSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("UnistoreSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("UserDataSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("UdkUserSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("cbdhsvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("NPSMSvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.StartsWith("webthreatdefusersvc_", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("DoSvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("DusmSvc", StringComparison.OrdinalIgnoreCase) ||
                svc.Equals("camsvc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsOptionalBackgroundUtility(ProcessIdentityDto identity, WorkloadStatus workload)
        {
            string name = identity.ProcessName;
            if (identity.HasVisibleWindow || identity.IsForeground) return false;

            // Secondary helper daemons with user context that are not critical to active workload
            if (name.EndsWith("Helper", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("CrashpadHandler", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("CrashReporter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static int GetSafeSessionId(Process p)
        {
            try { return p.SessionId; } catch { return -1; }
        }

        private static HashSet<int> GetPidsWithVisibleWindows()
        {
            var pids = new HashSet<int>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid > 0) pids.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
            return pids;
        }

        private static int GetForegroundProcessId()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }

        public static (Dictionary<int, List<int>> ParentChildMap, Dictionary<int, (int ParentPid, string ParentName)> ChildParentMap) BuildRealProcessHierarchy()
        {
            var parentChild = new Dictionary<int, List<int>>();
            var childParent = new Dictionary<int, (int ParentPid, string ParentName)>();
            var pidToName = new Dictionary<int, string>();

            IntPtr handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return (parentChild, childParent);
            }

            try
            {
                var entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (Process32First(handle, ref entry))
                {
                    do
                    {
                        int pid = (int)entry.th32ProcessID;
                        int ppid = (int)entry.th32ParentProcessID;
                        string exeName = Path.GetFileNameWithoutExtension(entry.szExeFile);

                        pidToName[pid] = exeName;

                        if (!parentChild.ContainsKey(ppid))
                        {
                            parentChild[ppid] = new List<int>();
                        }
                        parentChild[ppid].Add(pid);

                        if (!parentChild.ContainsKey(pid))
                        {
                            parentChild[pid] = new List<int>();
                        }

                        childParent[pid] = (ppid, string.Empty);

                    } while (Process32Next(handle, ref entry));
                }

                // Resolve parent names
                foreach (var pid in childParent.Keys.ToList())
                {
                    int ppid = childParent[pid].ParentPid;
                    string pName = pidToName.TryGetValue(ppid, out var pn) ? pn : string.Empty;
                    childParent[pid] = (ppid, pName);
                }
            }
            catch { }
            finally
            {
                CloseHandle(handle);
            }

            return (parentChild, childParent);
        }

        private static Dictionary<int, string> BuildServicePidMap()
        {
            var map = new Dictionary<int, string>();
            IntPtr hScManager = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE | SC_MANAGER_CONNECT);
            if (hScManager == IntPtr.Zero)
            {
                return map;
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                uint bytesNeeded = 0;
                uint servicesReturned = 0;
                uint resumeHandle = 0;

                // First call to determine required buffer size
                EnumServicesStatusExW(
                    hScManager,
                    SC_ENUM_PROCESS_INFO,
                    SERVICE_WIN32,
                    SERVICE_STATE_ALL,
                    IntPtr.Zero,
                    0,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                if (bytesNeeded > 0)
                {
                    buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                    bool success = EnumServicesStatusExW(
                        hScManager,
                        SC_ENUM_PROCESS_INFO,
                        SERVICE_WIN32,
                        SERVICE_STATE_ALL,
                        buffer,
                        bytesNeeded,
                        out bytesNeeded,
                        out servicesReturned,
                        ref resumeHandle,
                        null);

                    if (success || Marshal.GetLastWin32Error() == 0)
                    {
                        int structSize = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
                        long currentPtr = buffer.ToInt64();

                        for (int i = 0; i < servicesReturned; i++)
                        {
                            var status = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(new IntPtr(currentPtr), typeof(ENUM_SERVICE_STATUS_PROCESS))!;
                            if (status.ServiceStatusProcess.dwProcessId > 0 && !string.IsNullOrEmpty(status.lpServiceName))
                            {
                                int pid = (int)status.ServiceStatusProcess.dwProcessId;
                                if (map.TryGetValue(pid, out var existing))
                                {
                                    map[pid] = existing + "," + status.lpServiceName;
                                }
                                else
                                {
                                    map[pid] = status.lpServiceName;
                                }
                            }
                            currentPtr += structSize;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                CloseServiceHandle(hScManager);
            }

            return map;
        }

        public static List<string> GetLiveServicesForPid(int pid)
        {
            var services = new List<string>();
            if (pid <= 0 || !OperatingSystem.IsWindows()) return services;

            IntPtr hScManager = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE | SC_MANAGER_CONNECT);
            if (hScManager == IntPtr.Zero)
            {
                return services;
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                uint bytesNeeded = 0;
                uint servicesReturned = 0;
                uint resumeHandle = 0;

                EnumServicesStatusExW(
                    hScManager,
                    SC_ENUM_PROCESS_INFO,
                    SERVICE_WIN32,
                    SERVICE_STATE_ALL,
                    IntPtr.Zero,
                    0,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                if (bytesNeeded > 0)
                {
                    buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                    bool success = EnumServicesStatusExW(
                        hScManager,
                        SC_ENUM_PROCESS_INFO,
                        SERVICE_WIN32,
                        SERVICE_STATE_ALL,
                        buffer,
                        bytesNeeded,
                        out bytesNeeded,
                        out servicesReturned,
                        ref resumeHandle,
                        null);

                    if (success || Marshal.GetLastWin32Error() == 0)
                    {
                        int structSize = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
                        long currentPtr = buffer.ToInt64();

                        for (int i = 0; i < servicesReturned; i++)
                        {
                            var status = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(new IntPtr(currentPtr), typeof(ENUM_SERVICE_STATUS_PROCESS))!;
                            if (status.ServiceStatusProcess.dwProcessId == pid && !string.IsNullOrEmpty(status.lpServiceName))
                            {
                                services.Add(status.lpServiceName);
                            }
                            currentPtr += structSize;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                CloseServiceHandle(hScManager);
            }

            return services;
        }

        /// <summary>
        /// Task C: Correlates a running process to live Windows Scheduled Tasks strictly by normalized
        /// full executable action path (never by loose task-name matching). Supports multiple tasks per executable.
        /// </summary>
        public static void CorrelateScheduledTasks(
            ProcessIdentityDto identity,
            IReadOnlyDictionary<string, List<ScheduledTaskProvenanceDto>> taskProvenanceMap)
        {
            if (identity == null || taskProvenanceMap == null || taskProvenanceMap.Count == 0)
                return;

            string normExe = ProcessIdentitySafety.NormalizeExecutablePath(identity.ExecutablePath);
            if (string.IsNullOrEmpty(normExe))
                return;

            if (taskProvenanceMap.TryGetValue(normExe, out var tasks) && tasks.Count > 0)
            {
                identity.ScheduledTaskAssociations = tasks.ToList();
                identity.ScheduledTaskAssociation = string.Join("; ",
                    tasks.Select(t => t.TaskPath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Task D: Correlates a running process to Startup origins strictly by normalized executable path
        /// (plus AppX PackageFamilyName for UWP StartupTasks).
        /// </summary>
        public static void CorrelateStartupOrigins(
            ProcessIdentityDto identity,
            IReadOnlyDictionary<string, List<StartupProvenanceDto>> startupPathMap,
            IReadOnlyDictionary<string, List<StartupProvenanceDto>>? uwpStartupFamilyMap = null)
        {
            if (identity == null)
                return;

            var matched = new List<StartupProvenanceDto>();
            string normExe = ProcessIdentitySafety.NormalizeExecutablePath(identity.ExecutablePath);
            if (!string.IsNullOrEmpty(normExe) &&
                startupPathMap != null &&
                startupPathMap.TryGetValue(normExe, out var pathEntries))
            {
                matched.AddRange(pathEntries);
            }

            if (!string.IsNullOrEmpty(identity.AppxPackageFamily) &&
                uwpStartupFamilyMap != null &&
                uwpStartupFamilyMap.TryGetValue(identity.AppxPackageFamily, out var uwpEntries))
            {
                matched.AddRange(uwpEntries);
            }

            if (matched.Count > 0)
            {
                identity.StartupOrigins = matched;
                identity.StartupOrigin = string.Join("; ",
                    matched.Select(s => $"{s.Source}/{s.EntryName} ({s.EnabledStatus})").Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Task C: Live read-only enumeration of Windows Task Scheduler via Schedule.Service COM.
        /// Indexes all ExecAction entries by normalized executable path (supporting multiple tasks per executable).
        /// </summary>
        public static Dictionary<string, List<ScheduledTaskProvenanceDto>> DiscoverLiveScheduledTaskProvenance()
        {
            var map = new Dictionary<string, List<ScheduledTaskProvenanceDto>>(StringComparer.OrdinalIgnoreCase);
            if (!OperatingSystem.IsWindows())
                return map;

            object? serviceObj = null;
            try
            {
                var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType == null)
                    return map;

                serviceObj = Activator.CreateInstance(schedulerType);
                if (serviceObj == null)
                    return map;

                dynamic scheduler = serviceObj;
                scheduler.Connect();
                dynamic rootFolder = scheduler.GetFolder("\\");
                EnumerateTaskFolderRecursive(rootFolder, map);
            }
            catch
            {
                // Graceful fallback if Task Scheduler COM is unavailable or restricted
            }
            finally
            {
                if (serviceObj != null)
                {
                    try { Marshal.FinalReleaseComObject(serviceObj); } catch { }
                }
            }

            return map;
        }

        private static void EnumerateTaskFolderRecursive(
            dynamic folder,
            Dictionary<string, List<ScheduledTaskProvenanceDto>> map)
        {
            try
            {
                dynamic tasks = folder.GetTasks(1); // 1 = TASK_ENUM_HIDDEN
                foreach (dynamic task in tasks)
                {
                    try
                    {
                        string taskPath = Convert.ToString(task.Path) ?? string.Empty;
                        string taskName = Convert.ToString(task.Name) ?? string.Empty;
                        bool isEnabled = false;
                        try { isEnabled = Convert.ToBoolean(task.Enabled); } catch { }

                        dynamic definition = task.Definition;
                        string author = string.Empty;
                        try { author = Convert.ToString(definition.RegistrationInfo?.Author) ?? string.Empty; } catch { }

                        dynamic actions = definition.Actions;
                        foreach (dynamic action in actions)
                        {
                            try
                            {
                                int actionType = Convert.ToInt32(action.Type);
                                if (actionType != 0) // 0 = TASK_ACTION_EXEC
                                    continue;

                                string rawPath = Convert.ToString(action.Path) ?? string.Empty;
                                string rawArgs = Convert.ToString(action.Arguments) ?? string.Empty;
                                string workDir = Convert.ToString(action.WorkingDirectory) ?? string.Empty;

                                if (ParseCommandLine(rawPath, workDir, out string normalizedExe, out string parsedInlineArgs))
                                {
                                    string combinedArgs = string.IsNullOrWhiteSpace(parsedInlineArgs)
                                        ? rawArgs.Trim()
                                        : (string.IsNullOrWhiteSpace(rawArgs) ? parsedInlineArgs : $"{parsedInlineArgs} {rawArgs}".Trim());

                                    var dto = new ScheduledTaskProvenanceDto
                                    {
                                        TaskPath = taskPath,
                                        TaskName = taskName,
                                        ExecutableCommand = rawPath,
                                        NormalizedExecutablePath = normalizedExe,
                                        Arguments = combinedArgs,
                                        WorkingDirectory = workDir,
                                        IsEnabled = isEnabled,
                                        Author = author
                                    };

                                    if (!map.TryGetValue(normalizedExe, out var bucket))
                                    {
                                        bucket = new List<ScheduledTaskProvenanceDto>();
                                        map[normalizedExe] = bucket;
                                    }
                                    bucket.Add(dto);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                dynamic subFolders = folder.GetFolders(0);
                foreach (dynamic sub in subFolders)
                {
                    EnumerateTaskFolderRecursive(sub, map);
                }
            }
            catch { }
        }

        /// <summary>
        /// Task D: Read-only discovery of Startup origins across HKCU/HKLM Run &amp; RunOnce,
        /// WOW6432Node Run, User/Common Startup folders, StartupApproved status, and UWP StartupTasks.
        /// Never writes to registry or modifies startup entries.
        /// </summary>
        public static (Dictionary<string, List<StartupProvenanceDto>> ByExecutablePath,
                       Dictionary<string, List<StartupProvenanceDto>> ByUwpPackageFamily) DiscoverLiveStartupProvenance()
        {
            var byPath = new Dictionary<string, List<StartupProvenanceDto>>(StringComparer.OrdinalIgnoreCase);
            var byUwp = new Dictionary<string, List<StartupProvenanceDto>>(StringComparer.OrdinalIgnoreCase);

            if (!OperatingSystem.IsWindows())
                return (byPath, byUwp);

            // Load StartupApproved binary status maps (read-only)
            var hkcuApprovedRun = ReadStartupApprovedMap(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            var hkcuApprovedRun32 = ReadStartupApprovedMap(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32");
            var hkcuApprovedFolder = ReadStartupApprovedMap(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
            var hklmApprovedRun = ReadStartupApprovedMap(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            var hklmApprovedRun32 = ReadStartupApprovedMap(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32");
            var hklmApprovedFolder = ReadStartupApprovedMap(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");

            // 1-5. Registry Run / RunOnce / WOW6432Node Run
            ReadRegistryStartupKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU:Run", "User", hkcuApprovedRun, byPath);
            ReadRegistryStartupKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU:RunOnce", "User", null, byPath);
            ReadRegistryStartupKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM:Run", "Machine", hklmApprovedRun, byPath);
            ReadRegistryStartupKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM:RunOnce", "Machine", null, byPath);
            ReadRegistryStartupKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "WOW6432Node:Run", "Machine", hklmApprovedRun32, byPath);
            ReadRegistryStartupKey(Registry.CurrentUser, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKCU:WOW6432Node:Run", "User", hkcuApprovedRun32, byPath);

            // 6-7. User and Common Startup folders
            try
            {
                string userStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                ReadStartupFolder(userStartup, "Folder:UserStartup", "User", hkcuApprovedFolder, byPath);
            }
            catch { }

            try
            {
                string commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                ReadStartupFolder(commonStartup, "Folder:CommonStartup", "Machine", hklmApprovedFolder, byPath);
            }
            catch { }

            // 8. UWP StartupTask entries (AppModel\SystemAppData\<PackageFamilyName>\<TaskId>\State)
            ReadUwpStartupTasks(byUwp);

            return (byPath, byUwp);
        }

        /// <summary>
        /// Evaluates a Windows Explorer StartupApproved binary value (REG_BINARY).
        /// Returns Enabled (0x02/0x06), Disabled (0x01/0x03/0x07/odd), or Unknown.
        /// </summary>
        public static string EvaluateStartupApprovedStatus(byte[]? approvedBytes)
        {
            if (approvedBytes == null)
                return "Enabled"; // Absence of StartupApproved override means Windows runs the entry by default
            if (approvedBytes.Length < 4)
                return "Unknown";

            byte first = approvedBytes[0];
            if (first == 0x02 || first == 0x06)
                return "Enabled";
            if (first == 0x03 || first == 0x07 || first == 0x01 || (first & 0x01) == 1)
                return "Disabled";

            return "Unknown";
        }

        private static Dictionary<string, byte[]> ReadStartupApprovedMap(RegistryKey hive, string subKeyPath)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = hive.OpenSubKey(subKeyPath, writable: false);
                if (key == null) return result;

                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is byte[] data)
                    {
                        result[name] = data;
                    }
                }
            }
            catch { }
            return result;
        }

        private static void ReadRegistryStartupKey(
            RegistryKey hive,
            string subKeyPath,
            string sourceLabel,
            string scope,
            Dictionary<string, byte[]>? approvedMap,
            Dictionary<string, List<StartupProvenanceDto>> byPath)
        {
            try
            {
                using var key = hive.OpenSubKey(subKeyPath, writable: false);
                if (key == null) return;

                foreach (string valueName in key.GetValueNames())
                {
                    string? rawCommand = key.GetValue(valueName)?.ToString();
                    if (string.IsNullOrWhiteSpace(rawCommand)) continue;

                    ParseCommandLine(rawCommand, null, out string resolvedExe, out string args);
                    string enabledStatus = "Enabled";
                    if (approvedMap != null && approvedMap.TryGetValue(valueName, out byte[]? bytes))
                    {
                        enabledStatus = EvaluateStartupApprovedStatus(bytes);
                    }

                    string publisher = ReadFilePublisherSafe(resolvedExe);
                    var dto = new StartupProvenanceDto
                    {
                        EntryName = valueName,
                        OriginalCommand = rawCommand,
                        ResolvedExecutablePath = resolvedExe,
                        Arguments = args,
                        Source = sourceLabel,
                        Scope = scope,
                        Publisher = publisher,
                        EnabledStatus = enabledStatus
                    };

                    if (!string.IsNullOrEmpty(resolvedExe))
                    {
                        if (!byPath.TryGetValue(resolvedExe, out var bucket))
                        {
                            bucket = new List<StartupProvenanceDto>();
                            byPath[resolvedExe] = bucket;
                        }
                        bucket.Add(dto);
                    }
                }
            }
            catch { }
        }

        private static void ReadStartupFolder(
            string folderPath,
            string sourceLabel,
            string scope,
            Dictionary<string, byte[]> approvedMap,
            Dictionary<string, List<StartupProvenanceDto>> byPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            try
            {
                foreach (string file in Directory.GetFiles(folderPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string originalCmd = file;
                    string resolvedExe = string.Empty;
                    string args = string.Empty;

                    if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryResolveShortcutTarget(file, out string targetPath, out string targetArgs))
                        {
                            originalCmd = string.IsNullOrWhiteSpace(targetArgs) ? targetPath : $"\"{targetPath}\" {targetArgs}";
                            resolvedExe = ProcessIdentitySafety.NormalizeExecutablePath(targetPath);
                            args = targetArgs;
                        }
                    }
                    else if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedExe = ProcessIdentitySafety.NormalizeExecutablePath(file);
                    }

                    string enabledStatus = approvedMap.TryGetValue(fileName, out byte[]? bytes)
                        ? EvaluateStartupApprovedStatus(bytes)
                        : "Enabled";

                    if (!string.IsNullOrEmpty(resolvedExe))
                    {
                        var dto = new StartupProvenanceDto
                        {
                            EntryName = fileName,
                            OriginalCommand = originalCmd,
                            ResolvedExecutablePath = resolvedExe,
                            Arguments = args,
                            Source = sourceLabel,
                            Scope = scope,
                            Publisher = ReadFilePublisherSafe(resolvedExe),
                            EnabledStatus = enabledStatus
                        };

                        if (!byPath.TryGetValue(resolvedExe, out var bucket))
                        {
                            bucket = new List<StartupProvenanceDto>();
                            byPath[resolvedExe] = bucket;
                        }
                        bucket.Add(dto);
                    }
                }
            }
            catch { }
        }

        private static void ReadUwpStartupTasks(Dictionary<string, List<StartupProvenanceDto>> byUwp)
        {
            const string systemAppDataPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(systemAppDataPath, writable: false);
                if (root == null) return;

                foreach (string packageFamily in root.GetSubKeyNames())
                {
                    try
                    {
                        using var pkgKey = root.OpenSubKey(packageFamily, writable: false);
                        if (pkgKey == null) continue;

                        foreach (string taskId in pkgKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var taskKey = pkgKey.OpenSubKey(taskId, writable: false);
                                if (taskKey == null) continue;

                                object? stateObj = taskKey.GetValue("State");
                                if (stateObj == null) continue;

                                int state = Convert.ToInt32(stateObj);
                                // Windows StartupTaskState: 0=Disabled, 1=DisabledByUser, 2=Enabled, 3=DisabledByPolicy, 4=EnabledByPolicy
                                string enabledStatus = (state == 2 || state == 4)
                                    ? "Enabled"
                                    : (state == 0 || state == 1 || state == 3 ? "Disabled" : "Unknown");

                                var dto = new StartupProvenanceDto
                                {
                                    EntryName = taskId,
                                    OriginalCommand = $"{packageFamily}!{taskId}",
                                    ResolvedExecutablePath = string.Empty,
                                    Arguments = string.Empty,
                                    Source = "UWP:StartupTask",
                                    Scope = "User",
                                    Publisher = packageFamily,
                                    EnabledStatus = enabledStatus
                                };

                                if (!byUwp.TryGetValue(packageFamily, out var bucket))
                                {
                                    bucket = new List<StartupProvenanceDto>();
                                    byUwp[packageFamily] = bucket;
                                }
                                bucket.Add(dto);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool TryResolveShortcutTarget(string lnkPath, out string targetPath, out string arguments)
        {
            targetPath = string.Empty;
            arguments = string.Empty;
            object? shellObj = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;

                shellObj = Activator.CreateInstance(shellType);
                if (shellObj == null) return false;

                dynamic shell = shellObj;
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                targetPath = Convert.ToString(shortcut.TargetPath) ?? string.Empty;
                arguments = Convert.ToString(shortcut.Arguments) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(targetPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (shellObj != null)
                {
                    try { Marshal.FinalReleaseComObject(shellObj); } catch { }
                }
            }
        }

        private static string ReadFilePublisherSafe(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return string.Empty;
            try
            {
                if (File.Exists(exePath))
                {
                    return FileVersionInfo.GetVersionInfo(exePath).CompanyName?.Trim() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// Parses a registry Run or Scheduled Task command line into a normalized full executable path and arguments.
        /// Expands environment variables and resolves relative/System32 paths ONLY when verified on disk (never fabricates paths).
        /// </summary>
        public static bool ParseCommandLine(
            string? command,
            string? workingDirectory,
            out string normalizedExecutablePath,
            out string arguments)
        {
            normalizedExecutablePath = string.Empty;
            arguments = string.Empty;

            if (string.IsNullOrWhiteSpace(command))
                return false;

            string expanded = command.Trim();
            if (expanded.Contains('%'))
            {
                try { expanded = Environment.ExpandEnvironmentVariables(expanded); } catch { }
            }

            string exeToken;
            if (expanded.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = expanded.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    exeToken = expanded.Substring(1, closingQuote - 1).Trim();
                    arguments = expanded[(closingQuote + 1)..].Trim();
                }
                else
                {
                    exeToken = expanded.Trim('"').Trim();
                }
            }
            else
            {
                // Unquoted path: handle spaces before .exe/.com/.bat/.cmd/.msc/.vbs/.ps1
                int extIdx = FindExecutableExtensionIndex(expanded);
                if (extIdx > 0)
                {
                    exeToken = expanded[..extIdx].Trim();
                    arguments = expanded[extIdx..].Trim();
                }
                else
                {
                    int firstSpace = expanded.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        exeToken = expanded[..firstSpace].Trim();
                        arguments = expanded[(firstSpace + 1)..].Trim();
                    }
                    else
                    {
                        exeToken = expanded;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(exeToken))
                return false;

            // If relative or bare binary name, check WorkingDirectory and System32 only if real file exists
            if (!Path.IsPathRooted(exeToken))
            {
                string candidateName = exeToken.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? exeToken
                    : exeToken + ".exe";

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    string wdExpanded = workingDirectory.Contains('%')
                        ? Environment.ExpandEnvironmentVariables(workingDirectory.Trim().Trim('"'))
                        : workingDirectory.Trim().Trim('"');
                    string combined = Path.Combine(wdExpanded, exeToken);
                    if (File.Exists(combined))
                    {
                        normalizedExecutablePath = ProcessIdentitySafety.NormalizeExecutablePath(combined);
                        return true;
                    }
                    string combinedExe = Path.Combine(wdExpanded, candidateName);
                    if (File.Exists(combinedExe))
                    {
                        normalizedExecutablePath = ProcessIdentitySafety.NormalizeExecutablePath(combinedExe);
                        return true;
                    }
                }

                string sys32Path = Path.Combine(Environment.SystemDirectory, candidateName);
                if (File.Exists(sys32Path))
                {
                    normalizedExecutablePath = ProcessIdentitySafety.NormalizeExecutablePath(sys32Path);
                    return true;
                }

                string winDirPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), candidateName);
                if (File.Exists(winDirPath))
                {
                    normalizedExecutablePath = ProcessIdentitySafety.NormalizeExecutablePath(winDirPath);
                    return true;
                }

                return false;
            }

            normalizedExecutablePath = ProcessIdentitySafety.NormalizeExecutablePath(exeToken);
            return !string.IsNullOrEmpty(normalizedExecutablePath);
        }

        private static int FindExecutableExtensionIndex(string text)
        {
            string[] exts = { ".exe", ".com", ".bat", ".cmd" };
            int best = -1;
            foreach (string ext in exts)
            {
                int idx = text.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                while (idx > 0)
                {
                    int end = idx + ext.Length;
                    if (end == text.Length || char.IsWhiteSpace(text[end]))
                    {
                        if (best < 0 || end < best)
                            best = end;
                        break;
                    }
                    idx = text.IndexOf(ext, end, StringComparison.OrdinalIgnoreCase);
                }
            }
            return best;
        }

        #region Win32 Interop

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            const int MAX_PATH = 260;
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            public string szExeFile;
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        private const int SC_ENUM_PROCESS_INFO = 0;
        private const uint SERVICE_WIN32 = 0x00000030;
        private const uint SERVICE_STATE_ALL = 0x00000003;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ENUM_SERVICE_STATUS_PROCESS
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpServiceName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpDisplayName;
            public SERVICE_STATUS_PROCESS ServiceStatusProcess;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool EnumServicesStatusExW(
            IntPtr hSCManager,
            int infoLevel,
            uint dwServiceType,
            uint dwServiceState,
            IntPtr lpServices,
            uint cbBufSize,
            out uint pcbBytesNeeded,
            out uint lpServicesReturned,
            ref uint lpResumeHandle,
            string? pszGroupName);

        #endregion
    }
}
