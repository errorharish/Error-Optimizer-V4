#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Persistent
{
    public class BackgroundAppInfo
    {
        public string PackageName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool AllowsBackgroundExecution { get; set; } = true;
        public bool IsProtected { get; set; }
        public bool IsReducible { get; set; }
        public bool IsAlreadyOptimal { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Vector 2: Discovers AppX background activity and generates background permission optimization candidates.
    /// Inspects live BackgroundAccessApplications policies, GlobalUserDisabled, and per-package disabled states.
    /// Strictly protects user-shielded apps, hardware drivers, and required system apps.
    /// </summary>
    public class BackgroundAppEngine
    {
        private static readonly Dictionary<string, string> ReducibleBackgroundApps = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.BingNews"] = "Microsoft News background live tile updater",
            ["Microsoft.BingWeather"] = "Microsoft Weather background live tile updater",
            ["Microsoft.GetHelp"] = "Get Help background diagnostics",
            ["Microsoft.Getstarted"] = "Tips and Get Started background helper",
            ["Microsoft.YourPhone"] = "Phone Link background sync (when not in use)",
            ["Microsoft.ZuneVideo"] = "Movies & TV background push notifications",
            ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub background polling",
            ["Microsoft.MicrosoftSolitaireCollection"] = "Solitaire Collection background telemetry",
            ["Microsoft.GamingApp"] = "Xbox App background presence (when gaming inactive)",
            ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar background presence (when gaming inactive)",
            ["AmazonVideo.PrimeVideo"] = "Prime Video background sync",
            ["Clipchamp.Clipchamp"] = "Clipchamp background telemetry",
            ["MicrosoftWindows.Client.WebExperience"] = "Windows Widgets Board and Start Feed Provider",
            ["Microsoft.StartExperiencesApp"] = "Windows Start Experiences and News Feed Helper",
            ["Microsoft.Windows.PeopleExperienceHost"] = "Windows People Experience Host Background Sync",
            ["Microsoft.WindowsMaps"] = "Windows Maps Background Live Tiles and Updates",
            ["Microsoft.Todos"] = "Microsoft To-Do Background Sync Helper",
            ["Microsoft.PowerAutomateDesktop"] = "Power Automate Desktop Background Helper"
        };

        public BackgroundAppInfo EvaluateApp(string packageName, string? displayName = null, string? publisher = null)
        {
            string resolvedDisplay = string.IsNullOrWhiteSpace(displayName) ? packageName : displayName;
            var protectedMatch = ProtectedApplicationPolicy.Instance.EvaluateAppXPackage(packageName, resolvedDisplay, publisher);
            if (protectedMatch.IsProtected)
            {
                return new BackgroundAppInfo
                {
                    PackageName = packageName,
                    DisplayName = resolvedDisplay,
                    AllowsBackgroundExecution = true,
                    IsProtected = true,
                    IsReducible = false,
                    IsAlreadyOptimal = false,
                    Category = "PROTECTED_PACKAGE",
                    Reason = protectedMatch.Reason
                };
            }

            bool isReducible = false;
            string? reason = null;

            foreach (var kv in ReducibleBackgroundApps)
            {
                if (packageName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    isReducible = true;
                    reason = kv.Value;
                    break;
                }
            }

            return new BackgroundAppInfo
            {
                PackageName = packageName,
                DisplayName = resolvedDisplay,
                AllowsBackgroundExecution = true,
                IsProtected = false,
                IsReducible = isReducible,
                IsAlreadyOptimal = false,
                Category = isReducible ? "Reducible" : "General",
                Reason = reason ?? "Standard AppX package"
            };
        }

        public List<BackgroundAppInfo> DiscoverBackgroundApps()
        {
            var results = new List<BackgroundAppInfo>();
            var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Check Global Background Apps Policy
                bool globalDisabled = false;
                using var rootKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", writable: false);
                if (rootKey != null)
                {
                    if (rootKey.GetValue("GlobalUserDisabled") is int g && g == 1)
                    {
                        globalDisabled = true;
                    }

                    // Enumerate live subkeys under BackgroundAccessApplications
                    foreach (var subKeyName in rootKey.GetSubKeyNames())
                    {
                        seenPackages.Add(subKeyName);
                        using var subKey = rootKey.OpenSubKey(subKeyName, writable: false);
                        int disabledVal = subKey?.GetValue("Disabled") is int d ? d : 0;
                        int userDisabled = subKey?.GetValue("DisabledByUser") is int u ? u : 0;
                        bool isCurrentlyDisabled = globalDisabled || disabledVal == 1 || userDisabled == 1;

                        var info = EvaluateApp(subKeyName);
                        info.AllowsBackgroundExecution = !isCurrentlyDisabled;
                        info.IsAlreadyOptimal = isCurrentlyDisabled;

                        if (info.IsReducible)
                        {
                            results.Add(info);
                        }
                    }
                }
            }
            catch { }

            // 2. Also ensure known catalog entries are evaluated even if not yet in BackgroundAccessApplications subkeys
            foreach (var kv in ReducibleBackgroundApps)
            {
                if (!seenPackages.Any(s => s.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    var info = EvaluateApp(kv.Key, kv.Value.Split(' ')[0]);
                    if (!info.IsProtected && info.IsReducible)
                    {
                        info.Reason = kv.Value;
                        results.Add(info);
                    }
                }
            }

            return results;
        }

        public List<OptimizationCandidateDto> GenerateCandidates(List<BackgroundAppInfo> apps)
        {
            var candidates = new List<OptimizationCandidateDto>();

            foreach (var a in apps)
            {
                if (a.IsProtected || a.IsAlreadyOptimal || !a.IsReducible ||
                    ProtectedApplicationPolicy.Instance.IsAppXPackageProtected(a.PackageName, a.DisplayName))
                {
                    continue;
                }

                candidates.Add(new OptimizationCandidateDto
                {
                    CandidateId = $"APPX_{a.PackageName}",
                    TargetType = "AppXBackground",
                    TargetIdentifier = a.PackageName,
                    DisplayName = $"AppX Background: {a.DisplayName}",
                    Category = "Background Apps",
                    RiskLevel = ProcessRisk.SAFE_TO_TERMINATE,
                    InitialState = "Allowed",
                    TargetState = "Disabled",
                    Reason = $"Disable UWP background execution permission: {a.Reason}",
                    SafetyConfidence = 0.95,
                    IsSelected = true,
                    IsPersistent = true,
                    WorkloadCompatibility = "All",
                    Classification = "Recommended",
                    ExpectedBenefit = "Prevents Windows UWP app background execution permission and battery drain.",
                    PotentialRisk = "UWP live tiles or background push notifications paused while app is not active.",
                    RequiredPermissions = "User / Registry Setting",
                    ReversibilityStatus = "Reversible via Rollback Journal",
                    BackendHandler = "BackgroundAppEngine / Registry",
                    VerificationMethod = "BackgroundAccessApplications Query",
                    EvidenceQuality = "measured",
                    EligibilityExplanation = "UWP app background execution policy toggle."
                });
            }

            return candidates;
        }
    }
}
