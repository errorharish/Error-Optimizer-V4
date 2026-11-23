#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Safety
{
    public enum ProtectedAppCategory
    {
        None = 0,
        CreativeEditing,
        RemoteAccess,
        Media,
        VpnNetwork,
        Gaming,
        LaptopOem,
        PeripheralHardware,
        StreamingBroadcast
    }

    public sealed class ProtectedAppMatchResult
    {
        public static readonly ProtectedAppMatchResult NotProtected = new() { IsProtected = false };

        public bool IsProtected { get; init; }
        public string VendorId { get; init; } = string.Empty;
        public string VendorDisplayName { get; init; } = string.Empty;
        public ProtectedAppCategory Category { get; init; } = ProtectedAppCategory.None;
        public string CategoryLabel { get; init; } = string.Empty;
        public string ClassificationTag { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public bool MonitorAllowed { get; init; } = true;
        public bool OptimizeAllowed { get; init; } = false;
    }

    public sealed class ProtectedVendorProfile
    {
        public string VendorId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public ProtectedAppCategory Category { get; init; }
        public string CategoryLabel { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool IsEnabledByDefault { get; init; } = true;
        public HashSet<string> ExactProcessNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string[] ProcessPrefixes { get; init; } = Array.Empty<string>();
        public HashSet<string> ExactServiceNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string[] ServicePrefixes { get; init; } = Array.Empty<string>();
        public string[] VerifiedPathTokens { get; init; } = Array.Empty<string>();
        public string[] PublisherTokens { get; init; } = Array.Empty<string>();
        public string[] TaskAndStartupTokens { get; init; } = Array.Empty<string>();
        public string[] AppxPackageTokens { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Unified Protected Application &amp; Service Exclusion Policy for V4 Maximum Performance.
    /// Enforces MONITOR = YES, OPTIMIZE = NO across Processes, Services, Scheduled Tasks,
    /// Startup Entries, and AppX/Background Packages using multi-signal verification
    /// (canonical process/service names, install paths, digital signatures, and parent-child process trees).
    /// </summary>
    public sealed class ProtectedApplicationPolicy
    {
        private static readonly Lazy<ProtectedApplicationPolicy> _instance = new(() => new ProtectedApplicationPolicy());
        public static ProtectedApplicationPolicy Instance => _instance.Value;

        private readonly List<ProtectedVendorProfile> _profiles;
        private readonly ConcurrentDictionary<string, bool> _vendorOverrideStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ProtectedVendorProfile> _customProtectedApps = new(StringComparer.OrdinalIgnoreCase);

        // Suspicious/temporary folder tokens where spoofed or transient binaries might run.
        // A binary in these folders is NEVER granted path/name-based vendor protection unless digitally signed by the vendor.
        private static readonly string[] UntrustedTempPathTokens =
        {
            @"\appdata\local\temp\",
            @"\windows\temp\",
            @"\temp\",
            @"\tmp\",
            @"\recycle.bin\"
        };

        public ProtectedApplicationPolicy()
        {
            _profiles = BuildDefaultProfiles();
            foreach (var p in _profiles)
            {
                _vendorOverrideStates[p.VendorId] = p.IsEnabledByDefault;
            }
        }

        public IReadOnlyList<ProtectedVendorProfile> Profiles => _profiles;

        /// <summary>
        /// Architecture hook for future advanced settings screen: allows checking or toggling a vendor profile.
        /// Protected by default.
        /// </summary>
        public bool IsVendorProtectionEnabled(string vendorId)
        {
            return !_vendorOverrideStates.TryGetValue(vendorId, out bool enabled) || enabled;
        }

        public void SetVendorProtectionEnabled(string vendorId, bool enabled)
        {
            if (!string.IsNullOrWhiteSpace(vendorId))
            {
                _vendorOverrideStates[vendorId] = enabled;
            }
        }

        public void RegisterCustomProtectedApplication(ProtectedVendorProfile customProfile)
        {
            if (customProfile != null && !string.IsNullOrWhiteSpace(customProfile.VendorId))
            {
                _customProtectedApps[customProfile.VendorId] = customProfile;
                _vendorOverrideStates[customProfile.VendorId] = true;
            }
        }

        private IEnumerable<ProtectedVendorProfile> GetActiveProfiles()
        {
            foreach (var p in _profiles)
            {
                if (IsVendorProtectionEnabled(p.VendorId))
                    yield return p;
            }
            foreach (var custom in _customProtectedApps.Values)
            {
                if (IsVendorProtectionEnabled(custom.VendorId))
                    yield return custom;
            }
        }

        public List<ProtectedApplicationSummaryDto> GetProtectedApplicationsSummary()
        {
            var list = new List<ProtectedApplicationSummaryDto>();
            foreach (var p in GetActiveProfiles())
            {
                list.Add(new ProtectedApplicationSummaryDto
                {
                    VendorId = p.VendorId,
                    DisplayName = p.DisplayName,
                    CategoryLabel = p.CategoryLabel,
                    Description = p.Description,
                    ProtectionStatus = "Protected (Monitor = Yes, Optimize = No)"
                });
            }
            return list;
        }

        // ── 1. Process Evaluation (with Process Tree Propagation) ─────────────

        public bool IsProcessProtected(
            ProcessIdentityDto? proc,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree = null)
        {
            return EvaluateProcess(proc, processTree).IsProtected;
        }

        public ProtectedAppMatchResult EvaluateProcess(
            ProcessIdentityDto? proc,
            IReadOnlyDictionary<int, ProcessIdentityDto>? processTree = null)
        {
            if (proc == null || string.IsNullOrWhiteSpace(proc.ProcessName))
                return ProtectedAppMatchResult.NotProtected;

            // Direct evaluation of the process itself
            var directMatch = EvaluateProcessIdentity(
                proc.ProcessName,
                proc.ExecutablePath,
                proc.Publisher,
                proc.IsDigitallySigned,
                proc.ServiceAssociation,
                proc.AppxPackageFamily);

            if (directMatch.IsProtected)
                return directMatch;

            // Parent / Ancestor process tree propagation (protects helper/renderer/encoder/CEF/ffmpeg children)
            // Validates parent start time <= child start time + 2s to prevent PID reuse from falsely propagating protection.
            if (processTree != null && proc.ParentProcessId > 4)
            {
                var currentChild = proc;
                int currentParentId = proc.ParentProcessId;
                int depth = 0;
                var visited = new HashSet<int> { proc.ProcessId };

                while (currentParentId > 4 && depth < 12 && visited.Add(currentParentId))
                {
                    if (processTree.TryGetValue(currentParentId, out var parentProc))
                    {
                        if (!ProcessIdentitySafety.IsVerifiedParentChild(parentProc, currentChild))
                        {
                            break;
                        }

                        var parentMatch = EvaluateProcessIdentity(
                            parentProc.ProcessName,
                            parentProc.ExecutablePath,
                            parentProc.Publisher,
                            parentProc.IsDigitallySigned,
                            parentProc.ServiceAssociation,
                            parentProc.AppxPackageFamily);

                        if (parentMatch.IsProtected)
                        {
                            return new ProtectedAppMatchResult
                            {
                                IsProtected = true,
                                VendorId = parentMatch.VendorId,
                                VendorDisplayName = parentMatch.VendorDisplayName,
                                Category = parentMatch.Category,
                                CategoryLabel = parentMatch.CategoryLabel,
                                ClassificationTag = parentMatch.ClassificationTag,
                                Reason = $"Verified child/helper process of protected {parentMatch.VendorDisplayName} application ({parentProc.ProcessName}, PID {parentProc.ProcessId})."
                            };
                        }

                        currentChild = parentProc;
                        currentParentId = parentProc.ParentProcessId;
                        depth++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        public ProtectedAppMatchResult EvaluateProcessIdentity(
            string? processName,
            string? executablePath = null,
            string? publisher = null,
            bool isDigitallySigned = false,
            string? serviceAssociation = null,
            string? appxPackageFamily = null)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return ProtectedAppMatchResult.NotProtected;

            string cleanName = NormalizeProcessName(processName);
            string normPath = NormalizePath(executablePath);
            bool isUntrustedTempPath = IsUntrustedTemporaryPath(normPath);

            foreach (var profile in GetActiveProfiles())
            {
                bool hasVerifiedPublisher = isDigitallySigned && MatchesAnyToken(publisher, profile.PublisherTokens);
                bool hasVerifiedPath = !isUntrustedTempPath && MatchesAnyToken(normPath, profile.VerifiedPathTokens);

                // If the binary is running out of a Temp directory and is NOT digitally signed by the vendor,
                // reject name-only matching to prevent spoofed binaries in temp folders from being protected.
                if (isUntrustedTempPath && !hasVerifiedPublisher)
                {
                    continue;
                }

                // Signal 1: Exact canonical process name
                if (profile.ExactProcessNames.Contains(cleanName))
                {
                    return CreateMatch(profile, "PROTECTED",
                        $"Protected {profile.DisplayName} process ({cleanName}).");
                }

                // Signal 2: Process prefix match (e.g. Lenovo.Modern.ImController.PluginHost.*)
                if (MatchesAnyPrefix(cleanName, profile.ProcessPrefixes))
                {
                    return CreateMatch(profile, "PROTECTED",
                        $"Protected {profile.DisplayName} component ({cleanName}).");
                }

                // Signal 3: Associated service is a protected vendor service
                if (!string.IsNullOrWhiteSpace(serviceAssociation))
                {
                    var services = serviceAssociation.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var svc in services)
                    {
                        string cleanSvc = svc.Trim();
                        if (profile.ExactServiceNames.Contains(cleanSvc) || MatchesAnyPrefix(cleanSvc, profile.ServicePrefixes))
                        {
                            return CreateMatch(profile, "PROTECTED_SERVICE",
                                $"Hosts protected {profile.DisplayName} service ({cleanSvc}).");
                        }
                    }
                }

                // Signal 4: Verified vendor install directory (e.g. \Program Files\Adobe\, \Program Files\Topaz Labs LLC\)
                if (hasVerifiedPath)
                {
                    return CreateMatch(profile, "PROTECTED",
                        $"Verified {profile.DisplayName} installation path ({executablePath}).");
                }

                // Signal 5: Verified digital signature publisher
                if (hasVerifiedPublisher)
                {
                    return CreateMatch(profile, "PROTECTED",
                        $"Digitally signed by protected vendor {profile.DisplayName} ({publisher}).");
                }

                // Signal 6: AppX package family match
                if (!string.IsNullOrWhiteSpace(appxPackageFamily) && MatchesAnyToken(appxPackageFamily, profile.AppxPackageTokens))
                {
                    return CreateMatch(profile, "PROTECTED",
                        $"Belongs to protected {profile.DisplayName} package ({appxPackageFamily}).");
                }
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        // ── 2. Service Evaluation ─────────────────────────────────────────────

        public bool IsServiceProtected(string? serviceName, string? displayName = null, string? binaryPath = null)
        {
            return EvaluateService(serviceName, displayName, binaryPath).IsProtected;
        }

        public ProtectedAppMatchResult EvaluateService(string? serviceName, string? displayName = null, string? binaryPath = null)
        {
            if (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(displayName))
                return ProtectedAppMatchResult.NotProtected;

            string cleanSvc = (serviceName ?? string.Empty).Trim();
            string cleanDisplay = (displayName ?? string.Empty).Trim();
            string normPath = NormalizePath(binaryPath);
            bool isUntrustedTempPath = IsUntrustedTemporaryPath(normPath);

            foreach (var profile in GetActiveProfiles())
            {
                if (isUntrustedTempPath)
                    continue;

                if (!string.IsNullOrEmpty(cleanSvc))
                {
                    if (profile.ExactServiceNames.Contains(cleanSvc) || MatchesAnyPrefix(cleanSvc, profile.ServicePrefixes))
                    {
                        return CreateMatch(profile, "PROTECTED_SERVICE",
                            $"Protected {profile.DisplayName} background service ({cleanSvc}).");
                    }
                }

                if (!string.IsNullOrEmpty(cleanDisplay))
                {
                    if (profile.ExactServiceNames.Contains(cleanDisplay) || MatchesAnyPrefix(cleanDisplay, profile.ServicePrefixes))
                    {
                        return CreateMatch(profile, "PROTECTED_SERVICE",
                            $"Protected {profile.DisplayName} service ({cleanDisplay}).");
                    }
                }

                if (!string.IsNullOrEmpty(normPath) && MatchesAnyToken(normPath, profile.VerifiedPathTokens))
                {
                    return CreateMatch(profile, "PROTECTED_SERVICE",
                        $"Protected {profile.DisplayName} service binary path ({binaryPath}).");
                }
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        // ── 3. Scheduled Task Evaluation ──────────────────────────────────────

        public bool IsScheduledTaskProtected(string? taskPath, string? taskName = null, string? author = null, string? executeCommand = null)
        {
            return EvaluateScheduledTask(taskPath, taskName, author, executeCommand).IsProtected;
        }

        public ProtectedAppMatchResult EvaluateScheduledTask(
            string? taskPath,
            string? taskName = null,
            string? author = null,
            string? executeCommand = null)
        {
            string combinedTask = $"{taskPath} {taskName}".Trim();
            if (string.IsNullOrWhiteSpace(combinedTask) && string.IsNullOrWhiteSpace(author) && string.IsNullOrWhiteSpace(executeCommand))
                return ProtectedAppMatchResult.NotProtected;

            string normCmd = NormalizePath(executeCommand);

            foreach (var profile in GetActiveProfiles())
            {
                if (MatchesAnyToken(combinedTask, profile.TaskAndStartupTokens))
                {
                    return CreateMatch(profile, "PROTECTED_TASK",
                        $"Protected {profile.DisplayName} scheduled task ({combinedTask}).");
                }

                if (!string.IsNullOrWhiteSpace(author) && MatchesAnyToken(author, profile.PublisherTokens))
                {
                    return CreateMatch(profile, "PROTECTED_TASK",
                        $"Scheduled task authored by protected vendor {profile.DisplayName} ({author}).");
                }

                if (!string.IsNullOrWhiteSpace(normCmd) && !IsUntrustedTemporaryPath(normCmd) &&
                    MatchesAnyToken(normCmd, profile.VerifiedPathTokens))
                {
                    return CreateMatch(profile, "PROTECTED_TASK",
                        $"Scheduled task executes protected {profile.DisplayName} binary ({executeCommand}).");
                }
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        // ── 4. Startup Entry Evaluation ───────────────────────────────────────

        public bool IsStartupEntryProtected(string? entryName, string? command = null, string? publisher = null, string? location = null)
        {
            return EvaluateStartupEntry(entryName, command, publisher, location).IsProtected;
        }

        public ProtectedAppMatchResult EvaluateStartupEntry(
            string? entryName,
            string? command = null,
            string? publisher = null,
            string? location = null)
        {
            if (string.IsNullOrWhiteSpace(entryName) && string.IsNullOrWhiteSpace(command))
                return ProtectedAppMatchResult.NotProtected;

            string cleanName = (entryName ?? string.Empty).Trim();
            string normCmd = NormalizePath(command);
            bool isUntrustedTemp = IsUntrustedTemporaryPath(normCmd);

            foreach (var profile in GetActiveProfiles())
            {
                if (isUntrustedTemp)
                    continue;

                if (!string.IsNullOrEmpty(cleanName) &&
                    (profile.ExactProcessNames.Contains(NormalizeProcessName(cleanName)) ||
                     MatchesAnyToken(cleanName, profile.TaskAndStartupTokens)))
                {
                    return CreateMatch(profile, "PROTECTED_STARTUP",
                        $"Protected {profile.DisplayName} startup component ({cleanName}).");
                }

                if (!string.IsNullOrEmpty(normCmd) && MatchesAnyToken(normCmd, profile.VerifiedPathTokens))
                {
                    return CreateMatch(profile, "PROTECTED_STARTUP",
                        $"Startup entry launches protected {profile.DisplayName} binary ({command}).");
                }

                if (!string.IsNullOrWhiteSpace(publisher) && MatchesAnyToken(publisher, profile.PublisherTokens))
                {
                    return CreateMatch(profile, "PROTECTED_STARTUP",
                        $"Startup entry signed by protected vendor {profile.DisplayName} ({publisher}).");
                }
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        // ── 5. AppX / Background Package Evaluation ──────────────────────────

        public static bool IsProtectedPackage(string packageName) => Instance.IsAppXPackageProtected(packageName);

        public bool IsAppXPackageProtected(string? packageName, string? displayName = null, string? publisher = null)
        {
            return EvaluateAppXPackage(packageName, displayName, publisher).IsProtected;
        }

        public ProtectedAppMatchResult EvaluateAppXPackage(string? packageName, string? displayName = null, string? publisher = null)
        {
            if (string.IsNullOrWhiteSpace(packageName) && string.IsNullOrWhiteSpace(displayName))
                return ProtectedAppMatchResult.NotProtected;

            string combined = $"{packageName} {displayName}".Trim();

            foreach (var profile in GetActiveProfiles())
            {
                if (MatchesAnyToken(combined, profile.AppxPackageTokens))
                {
                    return CreateMatch(profile, "PROTECTED_PACKAGE",
                        $"Protected {profile.DisplayName} package ({packageName ?? displayName}).");
                }

                if (!string.IsNullOrWhiteSpace(publisher) && MatchesAnyToken(publisher, profile.PublisherTokens))
                {
                    return CreateMatch(profile, "PROTECTED_PACKAGE",
                        $"AppX package published by protected vendor {profile.DisplayName}.");
                }
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        // ── 6. Unified Candidate / Identifier Check ───────────────────────────

        public ProtectedAppMatchResult EvaluateCandidate(OptimizationCandidateDto? candidate)
        {
            if (candidate == null)
                return ProtectedAppMatchResult.NotProtected;

            return EvaluateActionTarget(candidate.TargetType, candidate.TargetIdentifier, candidate.DisplayName);
        }

        public ProtectedAppMatchResult EvaluateActionTarget(string? targetType, string? targetIdentifier, string? displayName = null)
        {
            if (string.IsNullOrWhiteSpace(targetIdentifier))
                return ProtectedAppMatchResult.NotProtected;

            string type = (targetType ?? string.Empty).Trim();

            if (type.Equals("Service", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ServiceStop", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateService(targetIdentifier, displayName);
            }

            if (type.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Task", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateScheduledTask(targetIdentifier, displayName);
            }

            if (type.Equals("Startup", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("StartupItem", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("StartupEntry", StringComparison.OrdinalIgnoreCase))
            {
                string entryName = targetIdentifier;
                int pipeIdx = targetIdentifier.IndexOf('|');
                if (pipeIdx >= 0 && pipeIdx + 1 < targetIdentifier.Length)
                {
                    entryName = targetIdentifier[(pipeIdx + 1)..];
                }
                return EvaluateStartupEntry(entryName, displayName);
            }

            if (type.Equals("AppXBackground", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("AppX", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateAppXPackage(targetIdentifier, displayName);
            }

            // Default / Process check: targetIdentifier may be "ProcessName (PID)" or "ProcessName"
            string procName = ExtractProcessNameFromIdentifier(targetIdentifier);
            var procMatch = EvaluateProcessIdentity(procName);
            if (procMatch.IsProtected)
                return procMatch;

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                string displayProc = ExtractProcessNameFromIdentifier(displayName);
                var displayMatch = EvaluateProcessIdentity(displayProc);
                if (displayMatch.IsProtected)
                    return displayMatch;
            }

            return ProtectedAppMatchResult.NotProtected;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static ProtectedAppMatchResult CreateMatch(ProtectedVendorProfile profile, string classificationTag, string reason)
        {
            return new ProtectedAppMatchResult
            {
                IsProtected = true,
                VendorId = profile.VendorId,
                VendorDisplayName = profile.DisplayName,
                Category = profile.Category,
                CategoryLabel = profile.CategoryLabel,
                ClassificationTag = classificationTag,
                Reason = reason,
                MonitorAllowed = true,
                OptimizeAllowed = false
            };
        }

        private static string NormalizeProcessName(string name)
        {
            string trimmed = name.Trim();
            if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^4];
            return trimmed;
        }

        private static string ExtractProcessNameFromIdentifier(string identifier)
        {
            string trimmed = identifier.Trim();
            if (trimmed.StartsWith("Process:", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed["Process:".Length..].Trim();

            int parenIdx = trimmed.LastIndexOf('(');
            if (parenIdx > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                trimmed = trimmed[..parenIdx].Trim();
            }

            return NormalizeProcessName(trimmed);
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            return path.Trim().Replace('/', '\\').ToLowerInvariant();
        }

        private static bool IsUntrustedTemporaryPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
                return false;

            foreach (var token in UntrustedTempPathTokens)
            {
                if (normalizedPath.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool MatchesAnyToken(string? input, string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(input) || tokens == null || tokens.Length == 0)
                return false;

            foreach (var token in tokens)
            {
                if (!string.IsNullOrEmpty(token) && input.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool MatchesAnyPrefix(string? input, string[] prefixes)
        {
            if (string.IsNullOrWhiteSpace(input) || prefixes == null || prefixes.Length == 0)
                return false;

            foreach (var prefix in prefixes)
            {
                if (!string.IsNullOrEmpty(prefix) && input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<ProtectedVendorProfile> BuildDefaultProfiles()
        {
            return new List<ProtectedVendorProfile>
            {
                // 1. Adobe Creative Cloud & Editing Applications
                new ProtectedVendorProfile
                {
                    VendorId = "ADOBE",
                    DisplayName = "Adobe",
                    Category = ProtectedAppCategory.CreativeEditing,
                    CategoryLabel = "CREATIVE / EDITING",
                    Description = "Adobe Premiere Pro, After Effects, Photoshop, Media Encoder, Creative Cloud & licensing/sync services",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Adobe Premiere Pro", "AfterFX", "Photoshop", "Adobe Media Encoder", "AMECommand",
                        "Illustrator", "Adobe Audition", "Lightroom", "LightroomClassic", "Acrobat", "AcroRd32",
                        "Creative Cloud", "Creative Cloud Helper", "Creative Cloud UI Helper",
                        "Adobe Desktop Service", "AdobeIPCBroker", "CCXProcess", "CCLibrary", "CoreSync",
                        "AdobeARM", "AdobeARMHelper", "AdobeUpdateService", "AGMService", "AGSService",
                        "Adobe CEF Helper", "Adobe Crash Processor", "CRWindowsClientService",
                        "dynamiclinkmanager", "ImporterREDServer", "PProHeadless", "TeamProjectsLocalHub",
                        "AdobeNotificationClient", "AdobeGenuineHelper", "AdobeCollabSync", "node_adobe"
                    },
                    ProcessPrefixes = new[]
                    {
                        "Adobe Premiere", "Adobe Media", "Adobe CEF", "Adobe Desktop", "AdobeIPC"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "AdobeARMservice", "AGMService", "AGSService", "AdobeUpdateService",
                        "AdobeGenuineMonitorService", "AdobeGenuineSoftwareIntegrityService",
                        "CCLibrary", "AdobeIPCBroker"
                    },
                    ServicePrefixes = new[]
                    {
                        "AdobeARM", "AdobeUpdate", "AdobeGenuine"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\adobe\",
                        @"\program files (x86)\adobe\",
                        @"\program files\common files\adobe\",
                        @"\program files (x86)\common files\adobe\",
                        @"\appdata\local\adobe\",
                        @"\appdata\roaming\adobe\"
                    },
                    PublisherTokens = new[]
                    {
                        "Adobe Inc", "Adobe Systems"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Adobe Acrobat Update", "AdobeGCInvoker", "AdobeAAMUpdater", "AdobeARM",
                        "Adobe Creative Cloud", "CCXProcess", "AdobeUpdateService", @"\Adobe\"
                    },
                    AppxPackageTokens = new[]
                    {
                        "AdobeSystemsIncorporated", "Adobe.CC", "AdobeNotificationClient", "AcrobatNotificationClient"
                    }
                },

                // 2. Maxon & Red Giant (Cinema 4D, Maxon App, Redshift, ZBrush, Universe, Trapcode, Magic Bullet)
                new ProtectedVendorProfile
                {
                    VendorId = "MAXON_REDGIANT",
                    DisplayName = "Maxon / Red Giant",
                    Category = ProtectedAppCategory.CreativeEditing,
                    CategoryLabel = "CREATIVE / EDITING",
                    Description = "Cinema 4D, Maxon App, Red Giant Service, Universe, Trapcode, Magic Bullet, Redshift & ZBrush",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Cinema 4D", "Cinema4D", "c4dpy", "Maxon App", "MaxonApp", "MaxonAppGui",
                        "Maxon Install Helper", "Redshift", "redshiftBenchmark", "redshiftCmdLine",
                        "ZBrush", "Forger", "Cinebench", "mxnotify",
                        "Red Giant Service", "RedGiantService", "Red Giant Link", "RedGiantLink",
                        "Red Giant Application Manager", "PluralEyes"
                    },
                    ProcessPrefixes = new[]
                    {
                        "Maxon ", "MaxonApp", "Red Giant ", "RedGiant"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Maxon Install Helper", "MaxonAppService", "net.maxon.app.daemon", "RedshiftService",
                        "Red Giant Service", "RedGiantService", "com.redgiant.service"
                    },
                    ServicePrefixes = new[]
                    {
                        "Maxon", "Red Giant", "RedGiant"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\maxon",
                        @"\program files (x86)\maxon",
                        @"\programdata\maxon\",
                        @"\appdata\local\maxon\",
                        @"\program files\red giant",
                        @"\program files (x86)\red giant",
                        @"\programdata\red giant\",
                        @"\appdata\local\red giant\"
                    },
                    PublisherTokens = new[]
                    {
                        "Maxon Computer", "Maxon", "Red Giant"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Maxon", "Red Giant", "RedGiant", "Cinema 4D", "ZBrush"
                    },
                    AppxPackageTokens = new[]
                    {
                        "Maxon", "RedGiant"
                    }
                },

                // 3. Topaz Labs (Topaz Video AI, Photo AI, Gigapixel AI)
                new ProtectedVendorProfile
                {
                    VendorId = "TOPAZ",
                    DisplayName = "Topaz",
                    Category = ProtectedAppCategory.CreativeEditing,
                    CategoryLabel = "CREATIVE / EDITING",
                    Description = "Topaz Video AI, Topaz Photo AI, Topaz Gigapixel AI & active AI model/FFmpeg encoding workers",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Topaz Video AI", "Topaz Photo AI", "Topaz Gigapixel AI",
                        "Topaz DeNoise AI", "Topaz Sharpen AI", "Topaz Video Enhance AI",
                        "tvai", "tpai", "tgai"
                    },
                    ProcessPrefixes = new[]
                    {
                        "Topaz Video", "Topaz Photo", "Topaz Gigapixel", "Topaz DeNoise", "Topaz Sharpen"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "TopazVideoAIService", "TopazPhotoAIService"
                    },
                    ServicePrefixes = new[]
                    {
                        "Topaz"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\topaz labs",
                        @"\program files\topaz labs llc",
                        @"\programdata\topaz labs",
                        @"\appdata\local\topaz labs"
                    },
                    PublisherTokens = new[]
                    {
                        "Topaz Labs"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Topaz Labs", "Topaz Video AI", "Topaz Photo AI", "Topaz Gigapixel AI"
                    },
                    AppxPackageTokens = new[]
                    {
                        "TopazLabs"
                    }
                },

                // 4. DaVinci Resolve / Blender / Verified Creative Suites
                new ProtectedVendorProfile
                {
                    VendorId = "CREATIVE_SUITES",
                    DisplayName = "DaVinci Resolve / Creative Suites",
                    Category = ProtectedAppCategory.CreativeEditing,
                    CategoryLabel = "CREATIVE / EDITING",
                    Description = "DaVinci Resolve, Blackmagic Design, Blender, Autodesk Maya/3ds Max, SideFX Houdini & VEGAS Pro",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Resolve", "DaVinci Resolve", "fuscript", "dpdecoder", "BMDPanelDaemon",
                        "DaVinciPanelDaemon", "FairlightStudioUtility", "BlackmagicRawPlayer", "BlackmagicRawSpeedTest",
                        "blender", "blender-launcher", "maya", "mayabatch", "3dsmax", "houdini", "houdinifx",
                        "vegas190", "vegas200", "vegas210", "Nuke", "AvidMediaComposer"
                    },
                    ProcessPrefixes = new[]
                    {
                        "DaVinci", "Blackmagic"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "BMDPanelDaemon", "DaVinciPanelDaemon", "QPSQLServer"
                    },
                    ServicePrefixes = new[]
                    {
                        "Blackmagic", "DaVinci"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\blackmagic design\",
                        @"\program files (x86)\blackmagic design\",
                        @"\program files\blender foundation\",
                        @"\program files\autodesk\",
                        @"\program files\side effects software\",
                        @"\program files\vegas\"
                    },
                    PublisherTokens = new[]
                    {
                        "Blackmagic Design", "Blender Foundation", "Autodesk", "Side Effects Software"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Blackmagic Design", "DaVinci Resolve", "Blender Foundation"
                    },
                    AppxPackageTokens = new[]
                    {
                        "BlenderFoundation", "BlackmagicDesign"
                    }
                },

                // 5. UltraViewer (Remote Access)
                new ProtectedVendorProfile
                {
                    VendorId = "ULTRAVIEWER",
                    DisplayName = "UltraViewer",
                    Category = ProtectedAppCategory.RemoteAccess,
                    CategoryLabel = "REMOTE ACCESS",
                    Description = "UltraViewer remote desktop session host, service & background connection helpers",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "UltraViewer_Desktop", "UltraViewer_Service", "UltraViewService", "UltraViewer"
                    },
                    ProcessPrefixes = new[]
                    {
                        "UltraViewer", "UltraViewService"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "UltraViewService", "UltraViewer_Service", "UltraViewer"
                    },
                    ServicePrefixes = new[]
                    {
                        "UltraViewer", "UltraViewService"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\ultraviewer\",
                        @"\program files (x86)\ultraviewer\",
                        @"\appdata\local\ultraviewer\"
                    },
                    PublisherTokens = new[]
                    {
                        "DucFabulous", "UltraViewer"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "UltraViewer", "UltraViewService"
                    },
                    AppxPackageTokens = Array.Empty<string>()
                },

                // 6. Spotify (Media & Audio Playback)
                new ProtectedVendorProfile
                {
                    VendorId = "SPOTIFY",
                    DisplayName = "Spotify",
                    Category = ProtectedAppCategory.Media,
                    CategoryLabel = "MEDIA",
                    Description = "Spotify desktop player, CEF playback/UI helpers, audio session & media key handlers",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Spotify", "SpotifyWebHelper", "SpotifyMigrator", "SpotifyStartupTask", "SpotifyUpdate"
                    },
                    ProcessPrefixes = new[]
                    {
                        "Spotify"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "SpotifyService"
                    },
                    ServicePrefixes = new[]
                    {
                        "Spotify"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\appdata\roaming\spotify\",
                        @"\appdata\local\spotify\",
                        @"\windowsapps\spotifyab.spotifymusic"
                    },
                    PublisherTokens = new[]
                    {
                        "Spotify AB"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Spotify", "SpotifyWebHelper"
                    },
                    AppxPackageTokens = new[]
                    {
                        "SpotifyAB.SpotifyMusic"
                    }
                },

                // 7. Urban VPN (VPN & Network Tunneling)
                new ProtectedVendorProfile
                {
                    VendorId = "URBAN_VPN",
                    DisplayName = "Urban VPN",
                    Category = ProtectedAppCategory.VpnNetwork,
                    CategoryLabel = "VPN / NETWORK",
                    Description = "Urban VPN client, tunnel service, routing helpers & virtual TAP/Wintun network adapter",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "urban-vpn", "urban-vpn-service", "UrbanVPN", "UrbanVPN-Service", "urban-vpn-gui", "urbanvpn"
                    },
                    ProcessPrefixes = new[]
                    {
                        "urban-vpn", "UrbanVPN"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "UrbanVPN-Service", "urban-vpn-service", "UrbanVPN", "urban-vpn", "UrbanVPNTap", "UrbanVPNWintun"
                    },
                    ServicePrefixes = new[]
                    {
                        "UrbanVPN", "urban-vpn"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\urbanvpn\",
                        @"\program files (x86)\urbanvpn\",
                        @"\program files\urban vpn\",
                        @"\program files (x86)\urban vpn\",
                        @"\appdata\local\urbanvpn\"
                    },
                    PublisherTokens = new[]
                    {
                        "Urban VPN", "Urban Cyber Security"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "UrbanVPN", "Urban VPN", "urban-vpn"
                    },
                    AppxPackageTokens = new[]
                    {
                        "UrbanVPN"
                    }
                },

                // 8. Steam & Gaming Ecosystem
                new ProtectedVendorProfile
                {
                    VendorId = "STEAM",
                    DisplayName = "Steam",
                    Category = ProtectedAppCategory.Gaming,
                    CategoryLabel = "GAMING",
                    Description = "Steam client, Steam Client Service, steamwebhelper, GameOverlayUI, active games & anti-cheat",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "steam", "steamservice", "steamwebhelper", "GameOverlayUI",
                        "steamerrorreporter", "steamerrorreporter64", "streaming_client", "steam_monitor",
                        "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices",
                        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEDaisy", "vgc", "vgtray",
                        "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher", "FACEITService"
                    },
                    ProcessPrefixes = new[]
                    {
                        "steamwebhelper", "GameOverlayUI", "EasyAntiCheat", "EAAntiCheat"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Steam Client Service", "steamservice", "EasyAntiCheat", "EasyAntiCheat_EOS",
                        "BEService", "vgc", "EAAntiCheatService", "FACEITService"
                    },
                    ServicePrefixes = new[]
                    {
                        "Steam Client", "EasyAntiCheat", "EAAntiCheat"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files (x86)\steam\",
                        @"\program files\steam\",
                        @"\steamlibrary\steamapps\common\"
                    },
                    PublisherTokens = new[]
                    {
                        "Valve Corp", "Valve Corporation"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Steam", "SteamClient"
                    },
                    AppxPackageTokens = new[]
                    {
                        "Valve.Steam"
                    }
                },

                // 9. Lenovo Vantage & OEM Hardware/Thermal/Battery Control
                new ProtectedVendorProfile
                {
                    VendorId = "LENOVO_VANTAGE",
                    DisplayName = "Lenovo Vantage",
                    Category = ProtectedAppCategory.LaptopOem,
                    CategoryLabel = "LAPTOP / OEM",
                    Description = "Lenovo Vantage, LenovoVantageService, ImController, AddinHost, thermal/fan/battery & power mode controls",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "LenovoVantage", "LenovoVantageService",
                        "Lenovo.Modern.ImController",
                        "Lenovo.Modern.ImController.PluginHost",
                        "Lenovo.Modern.ImController.PluginHost.Device",
                        "Lenovo.Modern.ImController.PluginHost.CompanionApp",
                        "Lenovo.Modern.ImController.PluginHost.SettingsApp",
                        "Lenovo.Vantage.AddinHost",
                        "Lenovo.Vantage.AddinHost.x86",
                        "Lenovo.Vantage.AddinHost.amd64",
                        "LenovoPowerManagement", "LITSSvc", "LenovoSystemUpdate",
                        "LenovoUtility", "LenovoHotkeys", "UDClientService"
                    },
                    ProcessPrefixes = new[]
                    {
                        "LenovoVantage", "Lenovo.Modern.ImController", "Lenovo.Vantage."
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "LenovoVantageService", "ImControllerService", "Lenovo.Modern.ImController",
                        "LITSSvc", "LenovoFnAndFunctionKeys", "LenovoPowerManagement", "IBMPMSVC", "SUService"
                    },
                    ServicePrefixes = new[]
                    {
                        "LenovoVantage", "Lenovo.Modern.ImController"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\lenovo\",
                        @"\program files (x86)\lenovo\",
                        @"\programdata\lenovo\vantage\",
                        @"\programdata\lenovo\imcontroller\",
                        @"\windowsapps\e046963f.lenovocompanion",
                        @"\windowsapps\e0469640.lenovoutility"
                    },
                    PublisherTokens = new[]
                    {
                        "Lenovo"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        @"\Lenovo\", "LenovoVantage", "Lenovo.Modern.ImController", "ImController", "LenovoCompanion"
                    },
                    AppxPackageTokens = new[]
                    {
                        "E046963F.LenovoCompanion", "E0469640.LenovoUtility", "LenovoCompanion", "LenovoSettings"
                    }
                },

                // 10. Logitech G HUB & Hardware Peripheral Ecosystem (Keyboards, Mice, Headsets, Lightsync, Macros)
                new ProtectedVendorProfile
                {
                    VendorId = "LOGITECH",
                    DisplayName = "Logitech",
                    Category = ProtectedAppCategory.PeripheralHardware,
                    CategoryLabel = "PERIPHERAL / HARDWARE",
                    Description = "Logitech G HUB, Gaming Software, Options+, drivers, peripheral daemons, macros, lighting & device-management services",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "lghub", "lghub_agent", "lghub_system_tray", "lghub_updater",
                        "logioptionsplus", "logioptionsplus_agent", "logioptionsplus_updater", "logioptionsplus_appbroker",
                        "LCore", "LogitechGHub", "LogiBolt", "LogiOptions", "LogiOptionsMgr",
                        "LogiRegistryService", "logi_lamparray_service", "LogitechUpdate",
                        "LogiAnalyticsService", "LogiOptionsPlusAppBroker"
                    },
                    ProcessPrefixes = new[]
                    {
                        "lghub", "logioptions", "Logi", "Logitech"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "LGHUBConfigService", "LogiRegistryService", "logi_lamparray_service",
                        "LogiBoltService", "LogiOptionsPlusService", "LogitechUpdateService",
                        "LogiLampArrayService", "LGHUBAgentService"
                    },
                    ServicePrefixes = new[]
                    {
                        "LGHUB", "Logi", "Logitech"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\lghub\",
                        @"\program files\logitech\",
                        @"\program files (x86)\logitech\",
                        @"\program files\logi\",
                        @"\programdata\lghub\",
                        @"\appdata\local\lghub\",
                        @"\appdata\local\logioptionsplus\",
                        @"\appdata\roaming\logitech\"
                    },
                    PublisherTokens = new[]
                    {
                        "Logitech", "Logitech Inc", "Logitech Europe S.A.", "Logitech Europe SA", "Logitech Inc."
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "LGHUB", "Logitech", "LogiOptionsPlus", "LogiBolt", "LogiRegistryService", @"\Logitech\"
                    },
                    AppxPackageTokens = new[]
                    {
                        "Logitech", "LogiOptions"
                    }
                },

                // 11. Razer Synapse, Chroma & Peripheral Device Ecosystem (Keyboards, Mice, Headsets, Macros, Lighting)
                new ProtectedVendorProfile
                {
                    VendorId = "RAZER",
                    DisplayName = "Razer",
                    Category = ProtectedAppCategory.PeripheralHardware,
                    CategoryLabel = "PERIPHERAL / HARDWARE",
                    Description = "Razer Synapse, Razer Central, Chroma SDK, device-management services, macros, lighting & peripheral drivers",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Razer Synapse Service", "Razer Synapse 3", "Razer Synapse",
                        "RazerCentralService", "RazerCentral", "RzActionEngine", "RzSDKServer",
                        "RzChromaStreamServer", "RzChromaSDKHost", "RzChromaAppServer",
                        "RzDeviceEngine", "RazerCortex", "RzGamerSync", "RazerUpdateHelper",
                        "RazerChromaStreamServer", "RzAppEngine", "RazerIngameEngine"
                    },
                    ProcessPrefixes = new[]
                    {
                        "Razer", "Rz"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "Razer Synapse Service", "RazerCentralService", "RzActionEngine", "RzSDKServer",
                        "RzChromaStreamServer", "RzDeviceEngineService", "Razer Chroma Stream Server",
                        "Razer Chroma SDK Server", "Razer Device Engine Service"
                    },
                    ServicePrefixes = new[]
                    {
                        "Razer", "Rz"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\razer\",
                        @"\program files (x86)\razer\",
                        @"\program files (x86)\razer chroma sdk\",
                        @"\programdata\razer\",
                        @"\appdata\local\razer\"
                    },
                    PublisherTokens = new[]
                    {
                        "Razer Inc", "Razer Inc.", "Razer USA Ltd.", "Razer USA Ltd"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "Razer", "RzSynapse", "RazerSynapse", "RazerCentral", "RazerCortex", @"\Razer\"
                    },
                    AppxPackageTokens = new[]
                    {
                        "Razer", "RazerInc"
                    }
                },

                // 12. OBS Studio & Broadcasting / Capture Ecosystem (OBS, Streamlabs, Virtual Audio, Elgato)
                new ProtectedVendorProfile
                {
                    VendorId = "STREAMING_BROADCAST",
                    DisplayName = "OBS Studio / Broadcast",
                    Category = ProtectedAppCategory.StreamingBroadcast,
                    CategoryLabel = "STREAMING / BROADCAST",
                    Description = "OBS Studio, Streamlabs, live capture, encoding processes, virtual audio cables & webcam/capture-card services",
                    ExactProcessNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "obs64", "obs32", "obs", "streamlabs obs", "Streamlabs Desktop",
                        "prism live studio", "twitch studio", "vmix64", "vmix",
                        "WaveLink", "WaveLinkServer", "Voicemeeter", "voicemeeter8", "voicemeeterpro",
                        "elgato capture device service", "CameraHub", "Control Center"
                    },
                    ProcessPrefixes = new[]
                    {
                        "obs", "streamlabs", "voicemeeter", "WaveLink"
                    },
                    ExactServiceNames = new(StringComparer.OrdinalIgnoreCase)
                    {
                        "ElgatoCaptureDeviceService", "WaveLinkAudioService"
                    },
                    ServicePrefixes = new[]
                    {
                        "Elgato", "WaveLink"
                    },
                    VerifiedPathTokens = new[]
                    {
                        @"\program files\obs-studio\",
                        @"\program files (x86)\obs-studio\",
                        @"\program files\streamlabs obs\",
                        @"\program files\elgato\",
                        @"\program files (x86)\elgato\",
                        @"\program files\vb\voicemeeter\"
                    },
                    PublisherTokens = new[]
                    {
                        "OBS Project", "Streamlabs", "Elgato Systems", "Corsair Memory, Inc.", "VB-Audio Software"
                    },
                    TaskAndStartupTokens = new[]
                    {
                        "OBS", "Streamlabs", "Voicemeeter", "Elgato"
                    },
                    AppxPackageTokens = Array.Empty<string>()
                }
            };
        }
    }
}
