using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Xml.Linq;
using BiosOptimizer.Core.Implementations.Config;
using BiosOptimizer.Core.Interfaces;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations.Startup;

[SupportedOSPlatform("windows")]
public class WindowsStartupManager : IStartupManager
{
    private class StartupLocation
    {
        public string Source { get; set; } = "";
        public string FriendlySource { get; set; } = "";
        public RegistryKey? Root { get; set; }
        public string Path { get; set; } = "";
        public string ApprovedPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public bool RequiresAdmin { get; set; }
    }

    // Windows Task Manager -> Startup apps authoritative locations (Excludes RunOnce keys)
    private readonly List<StartupLocation> _locations = new()
    {
        new StartupLocation
        {
            Source = "HKCU_RUN",
            FriendlySource = @"HKCU\Run",
            Root = Registry.CurrentUser,
            Path = @"Software\Microsoft\Windows\CurrentVersion\Run",
            ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            IsFolder = false,
            RequiresAdmin = false
        },
        new StartupLocation
        {
            Source = "HKLM_RUN",
            FriendlySource = @"HKLM\Run",
            Root = Registry.LocalMachine,
            Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            IsFolder = false,
            RequiresAdmin = true
        },
        new StartupLocation
        {
            Source = "HKLM_WOW6432_RUN",
            FriendlySource = @"HKLM\WOW6432Node\Run",
            Root = Registry.LocalMachine,
            Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            IsFolder = false,
            RequiresAdmin = true
        },
        new StartupLocation
        {
            Source = "STARTUP_FOLDER_USER",
            FriendlySource = "Startup Folder (User)",
            Root = Registry.CurrentUser,
            Path = Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            IsFolder = true,
            RequiresAdmin = false
        },
        new StartupLocation
        {
            Source = "STARTUP_FOLDER_COMMON",
            FriendlySource = "Startup Folder (All Users)",
            Root = Registry.LocalMachine,
            Path = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            IsFolder = true,
            RequiresAdmin = true
        }
    };

    private static readonly Dictionary<string, (string DisplayName, string Publisher, string Version, string ExePath, string LogoPath, bool HasStartupTask)> _packageCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastPackageCacheTime = DateTime.MinValue;
    private static readonly Dictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public List<StartupEntryState> GetStartupEntries()
    {
        var entries = new List<StartupEntryState>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Classic Registry Run and Startup Folder locations (matching Task Manager)
        foreach (var loc in _locations)
        {
            try
            {
                if (loc.IsFolder)
                {
                    ScanFolderLocation(loc, entries, seenKeys);
                }
                else if (loc.Root != null)
                {
                    ScanRegistryLocation(loc, entries, seenKeys);
                }
            }
            catch { }
        }

        // 2. Modern Packaged Apps declaring windows.startupTask in AppxManifest.xml
        try
        {
            ScanMsixStartupTasks(entries, seenKeys);
        }
        catch { }

        return entries;
    }

    private void ScanRegistryLocation(StartupLocation loc, List<StartupEntryState> entries, HashSet<string> seenKeys)
    {
        if (loc.Root == null) return;

        bool isSystem = WindowsStartupRegistrar.IsRunningAsSystem();
        var hivesToScan = new List<(RegistryKey? Root, string Path, string ApprovedPath, string Sid)>();

        // When running under LocalSystem, query active user hives in HKEY_USERS for HKCU entries instead of Session 0 S-1-5-18
        if (loc.Root == Registry.CurrentUser)
        {
            if (!isSystem)
            {
                hivesToScan.Add((Registry.CurrentUser, loc.Path, loc.ApprovedPath, ""));
            }
            else
            {
                foreach (var sid in WindowsStartupRegistrar.GetInteractiveUserSids())
                {
                    hivesToScan.Add((Registry.Users, $@"{sid}\{loc.Path}", $@"{sid}\{loc.ApprovedPath}", sid));
                }
            }
        }
        else
        {
            hivesToScan.Add((loc.Root, loc.Path, loc.ApprovedPath, ""));
        }

        foreach (var (root, path, approvedPath, ownerSid) in hivesToScan)
        {
            if (root == null) continue;
            try
            {
                using var key = root.OpenSubKey(path, false);
                if (key == null) continue;

                using var approvedKey = root.OpenSubKey(approvedPath, false);

                foreach (var valName in key.GetValueNames())
                {
                    string dedupeKey = $"{loc.Source}|{valName}";
                    if (!seenKeys.Add(dedupeKey)) continue;

                    var val = key.GetValue(valName);
                    string commandLine = val?.ToString() ?? "";
                    var state = StartupState.Enabled;

                    if (approvedKey != null)
                    {
                        var appVal = approvedKey.GetValue(valName) as byte[];
                        if (appVal != null && appVal.Length > 0 && (appVal[0] == 0x03 || appVal[0] == 0x01 || (appVal[0] & 1) != 0))
                        {
                            state = StartupState.Disabled;
                        }
                    }

                    // Check for active user Task Manager override if item was not found disabled in this hive
                    if (state == StartupState.Enabled)
                    {
                        try
                        {
                            if (!isSystem)
                            {
                                using var userAppKey = Registry.CurrentUser.OpenSubKey(loc.ApprovedPath, false);
                                var uVal = userAppKey?.GetValue(valName) as byte[];
                                if (uVal != null && uVal.Length > 0 && (uVal[0] == 0x03 || uVal[0] == 0x01 || (uVal[0] & 1) != 0))
                                {
                                    state = StartupState.Disabled;
                                }
                            }
                            else if (!string.IsNullOrEmpty(ownerSid))
                            {
                                using var userAppKey = Registry.Users.OpenSubKey($@"{ownerSid}\{loc.ApprovedPath}", false);
                                var uVal = userAppKey?.GetValue(valName) as byte[];
                                if (uVal != null && uVal.Length > 0 && (uVal[0] == 0x03 || uVal[0] == 0x01 || (uVal[0] & 1) != 0))
                                {
                                    state = StartupState.Disabled;
                                }
                            }
                        }
                        catch { }
                    }

                    var entry = BuildStartupEntry(loc, valName, commandLine, val, state);
                    if (!string.IsNullOrEmpty(ownerSid))
                    {
                        entry.Path = $@"{ownerSid}\{loc.Path}";
                    }
                    entries.Add(entry);
                }
            }
            catch { }
        }
    }

    private void ScanFolderLocation(StartupLocation loc, List<StartupEntryState> entries, HashSet<string> seenKeys)
    {
        var foldersToScan = new List<(string FolderPath, List<(RegistryKey? Root, string ApprovedPath)> Hives)>();
        bool isSystem = WindowsStartupRegistrar.IsRunningAsSystem();

        var defaultHives = new List<(RegistryKey? Root, string ApprovedPath)>();
        if (!isSystem && loc.Root != null)
        {
            defaultHives.Add((loc.Root, loc.ApprovedPath));
        }
        else
        {
            foreach (var sid in WindowsStartupRegistrar.GetInteractiveUserSids())
            {
                defaultHives.Add((Registry.Users, $@"{sid}\{loc.ApprovedPath}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(loc.Path) && Directory.Exists(loc.Path))
        {
            foldersToScan.Add((loc.Path, defaultHives));
        }

        // When running under LocalSystem, resolve interactive user startup folder paths via ProfileList
        if (loc.Root == Registry.CurrentUser || isSystem)
        {
            foreach (var sid in WindowsStartupRegistrar.GetInteractiveUserSids())
            {
                try
                {
                    using var profKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
                    var profPath = profKey?.GetValue("ProfileImagePath")?.ToString();
                    if (!string.IsNullOrWhiteSpace(profPath) && Directory.Exists(profPath))
                    {
                        string userStartup = System.IO.Path.Combine(profPath, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
                        if (Directory.Exists(userStartup) && !foldersToScan.Any(f => string.Equals(f.FolderPath, userStartup, StringComparison.OrdinalIgnoreCase)))
                        {
                            var userHives = new List<(RegistryKey? Root, string ApprovedPath)>
                            {
                                (Registry.Users, $@"{sid}\{loc.ApprovedPath}")
                            };
                            if (!isSystem && loc.Root != null) userHives.Add((loc.Root, loc.ApprovedPath));
                            foldersToScan.Add((userStartup, userHives));
                        }
                    }
                }
                catch { }
            }
        }

        foreach (var (folderPath, hivesToScan) in foldersToScan)
        {
            try
            {
                foreach (var filePath in Directory.GetFiles(folderPath))
                {
                    string fileName = System.IO.Path.GetFileName(filePath);
                    string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".exe" && ext != ".bat" && ext != ".cmd") continue;

                    string dedupeKey = $"{loc.Source}|{fileName}";
                    if (!seenKeys.Add(dedupeKey)) continue;

                    string commandLine = filePath;
                    var state = StartupState.Enabled;

                    foreach (var (root, approvedPath) in hivesToScan)
                    {
                        if (root == null) continue;
                        try
                        {
                            using var approvedKey = root.OpenSubKey(approvedPath, false);
                            if (approvedKey != null)
                            {
                                var appVal = approvedKey.GetValue(fileName) as byte[];
                                if (appVal != null && appVal.Length > 0 && (appVal[0] == 0x03 || appVal[0] == 0x01 || (appVal[0] & 1) != 0))
                                {
                                    state = StartupState.Disabled;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    var entry = BuildStartupEntry(loc, fileName, commandLine, filePath, state);
                    entries.Add(entry);
                }
            }
            catch { }
        }
    }

    private void ScanMsixStartupTasks(List<StartupEntryState> entries, HashSet<string> seenKeys)
    {
        const string relativeRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        var hivesToScan = new List<(RegistryKey Root, string RootPath)>();
        bool isSystem = WindowsStartupRegistrar.IsRunningAsSystem();

        if (!isSystem)
        {
            hivesToScan.Add((Registry.CurrentUser, relativeRoot));
        }
        else
        {
            foreach (var sid in WindowsStartupRegistrar.GetInteractiveUserSids())
            {
                hivesToScan.Add((Registry.Users, $@"{sid}\{relativeRoot}"));
            }
        }

        RefreshPackageCacheIfNeeded();

        foreach (var (hiveRoot, rootPath) in hivesToScan)
        {
            using var rootKey = hiveRoot.OpenSubKey(rootPath, false);
            if (rootKey == null) continue;

            foreach (var pkgFamily in rootKey.GetSubKeyNames())
            {
                // CRITICAL TASK MANAGER FILTER: Only packages declaring windows.startupTask extension
                if (!_packageCache.TryGetValue(pkgFamily, out var pkgMeta) || !pkgMeta.HasStartupTask)
                {
                    continue;
                }

                using var pkgKey = rootKey.OpenSubKey(pkgFamily, false);
                if (pkgKey == null) continue;

                foreach (var taskSubName in pkgKey.GetSubKeyNames())
                {
                    // Skip internal Windows schema metadata keys
                    if (taskSubName.Equals("PSR", StringComparison.OrdinalIgnoreCase) ||
                        taskSubName.Equals("Schemas", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string dedupeKey = $"MSIX|{pkgFamily}|{taskSubName}";
                    if (!seenKeys.Add(dedupeKey)) continue;

                    using var taskKey = pkgKey.OpenSubKey(taskSubName, false);
                    if (taskKey == null) continue;

                    object? stateObj = taskKey.GetValue("State");
                    if (stateObj is not int stateInt) continue;

                    // Windows AppModel StartupTaskState:
                    // 0 = Disabled, 1 = DisabledByUser, 2 = Enabled, 3 = DisabledByPolicy, 4 = EnabledByPolicy
                    // This is the direct, authoritative state mutated by Windows Task Manager for MSIX packages.
                    var st = (stateInt == 2 || stateInt == 4) ? StartupState.Enabled : StartupState.Disabled;

                    var (displayName, publisher, version, exePath, iconBase64) = ResolvePackageMetadata(pkgFamily, taskSubName);
                    var (impact, impactMethod) = EvaluateImpact(pkgFamily, displayName, publisher, taskSubName);
                    var (category, isSys) = ClassifyStartupItem(taskSubName, displayName, publisher, exePath);

                    entries.Add(new StartupEntryState
                    {
                        Id = dedupeKey,
                        Source = "MSIX_STARTUP_TASK",
                        FriendlySource = "Store / MSIX Startup",
                        Path = $@"{rootPath}\{pkgFamily}\{taskSubName}",
                        ValueName = taskSubName,
                        DisplayName = displayName,
                        Publisher = publisher,
                        ExePath = exePath,
                        CommandLine = $"msix:{pkgFamily}!{taskSubName}",
                        Version = version,
                        OriginalValue = stateInt,
                        State = st,
                        Category = category,
                        Impact = impact,
                        ImpactMethod = impactMethod,
                        IsSystemItem = isSys,
                        RequiresAdmin = false,
                        FileExists = true,
                        IconBase64 = iconBase64
                    });
                }
            }
        }
    }

    private StartupEntryState BuildStartupEntry(StartupLocation loc, string valueName, string commandLine, object? originalValue, StartupState state)
    {
        string exePath = ExtractExePath(commandLine);
        bool fileExists = !string.IsNullOrEmpty(exePath) && File.Exists(exePath);

        string displayName = valueName;
        string publisher = "Unknown";
        string version = "N/A";
        string? iconBase64 = null;

        if (fileExists)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                    displayName = vi.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(vi.CompanyName))
                    publisher = vi.CompanyName.Trim();
                if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                    version = vi.FileVersion.Trim();
                else if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                    version = vi.ProductVersion.Trim();
            }
            catch { }

            iconBase64 = ExtractIconBase64(exePath);
        }

        if (publisher == "Unknown")
        {
            publisher = InferPublisher(valueName, exePath);
        }

        // Friendly name adjustments for classic items matching Task Manager
        if (valueName.Equals("LGHUB", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "Logitech G HUB";
            if (publisher == "Unknown") publisher = "Logitech, Inc.";
        }
        else if (valueName.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "Windows Security notification icon";
            if (publisher == "Unknown") publisher = "Microsoft Corporation";
        }
        else if (valueName.Equals("RtkAudUService", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "Realtek Audio Universal Service";
            if (publisher == "Unknown") publisher = "Realtek Semiconductor Corp.";
        }

        var (category, isSystem) = ClassifyStartupItem(valueName, displayName, publisher, exePath);
        var (impact, impactMethod) = EvaluateImpact(valueName, displayName, publisher, exePath);

        string id = loc.IsFolder ? $"FOLDER|{loc.Source}|{valueName}" : $"REG|{loc.Source}|{valueName}";

        return new StartupEntryState
        {
            Id = id,
            Source = loc.Source,
            FriendlySource = loc.FriendlySource,
            Path = loc.Path,
            ValueName = valueName,
            DisplayName = displayName,
            Publisher = publisher,
            ExePath = exePath,
            CommandLine = commandLine,
            Version = version,
            OriginalValue = originalValue,
            State = state,
            Category = category,
            Impact = impact,
            ImpactMethod = impactMethod,
            IsSystemItem = isSystem,
            RequiresAdmin = loc.RequiresAdmin,
            FileExists = fileExists,
            IconBase64 = iconBase64
        };
    }

    public static string? ExtractIconBase64(string? exePath, string? logoPath = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(exePath) && _iconCache.TryGetValue(exePath, out var cached))
            {
                return cached;
            }

            // 1. Packaged app logo
            if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
            {
                byte[] bytes = File.ReadAllBytes(logoPath);
                string b64 = Convert.ToBase64String(bytes);
                string dataUri = $"data:image/png;base64,{b64}";
                if (!string.IsNullOrEmpty(exePath)) _iconCache[exePath] = dataUri;
                return dataUri;
            }

            // 2. Win32 executable icon extraction
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    using var bmp = sysIcon.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    string b64 = Convert.ToBase64String(ms.ToArray());
                    string dataUri = $"data:image/png;base64,{b64}";
                    _iconCache[exePath] = dataUri;
                    return dataUri;
                }
            }
        }
        catch { }

        return null;
    }

    private static void RefreshPackageCacheIfNeeded()
    {
        if (_packageCache.Count > 0 && (DateTime.UtcNow - _lastPackageCacheTime).TotalMinutes < 2)
            return;

        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var targetUsers = new List<string>();
            bool isSystem = WindowsStartupRegistrar.IsRunningAsSystem();
            if (!isSystem)
            {
                targetUsers.Add("");
            }
            else
            {
                targetUsers.AddRange(WindowsStartupRegistrar.GetInteractiveUserSids());
                if (targetUsers.Count == 0) targetUsers.Add("");
            }

            _packageCache.Clear();

            foreach (var userSid in targetUsers)
            {
                IEnumerable<Windows.ApplicationModel.Package> packages;
                try
                {
                    packages = !string.IsNullOrEmpty(userSid)
                        ? pm.FindPackagesForUser(userSid)
                        : pm.FindPackagesForUser("");
                }
                catch
                {
                    try { packages = pm.FindPackages(); } catch { continue; }
                }

                foreach (var pkg in packages)
                {
                    try
                    {
                        string fam = pkg.Id.FamilyName;
                        if (string.IsNullOrEmpty(fam) || _packageCache.ContainsKey(fam)) continue;

                    string disp = "";
                    string pub = "";
                    try { disp = pkg.DisplayName; } catch { }
                    try { pub = pkg.PublisherDisplayName; } catch { }
                    string ver = "";
                    try { ver = $"{pkg.Id.Version.Major}.{pkg.Id.Version.Minor}.{pkg.Id.Version.Build}.{pkg.Id.Version.Revision}"; } catch { }
                    string installLoc = "";
                    try { installLoc = pkg.InstalledLocation?.Path ?? ""; } catch { }

                    bool hasStartupTask = false;
                    string logoPath = "";

                    if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                    {
                        string manifestPath = System.IO.Path.Combine(installLoc, "AppxManifest.xml");
                        if (File.Exists(manifestPath))
                        {
                            try
                            {
                                using var stream = File.OpenRead(manifestPath);
                                var doc = XDocument.Load(stream, LoadOptions.None);
                                var startupExtensions = doc.Descendants()
                                    .Where(e => e.Name.LocalName.Equals("Extension", StringComparison.OrdinalIgnoreCase) &&
                                                string.Equals((string?)e.Attribute("Category"), "windows.startupTask", StringComparison.OrdinalIgnoreCase));
                                if (startupExtensions.Any())
                                {
                                    hasStartupTask = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[StartupManager] Safe manifest XML parse failure for '{installLoc}': {ex.Message}");
                            }
                        }

                        if (hasStartupTask)
                        {
                            try
                            {
                                string[] candidateDirs = new[]
                                {
                                    installLoc,
                                    System.IO.Path.Combine(installLoc, "Assets"),
                                    System.IO.Path.Combine(installLoc, "Images"),
                                    System.IO.Path.Combine(installLoc, "assets")
                                };
                                foreach (var cDir in candidateDirs)
                                {
                                    if (!Directory.Exists(cDir)) continue;
                                    var logos = Directory.GetFiles(cDir, "*.png", SearchOption.AllDirectories)
                                        .Where(f => {
                                            string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                                            return n.Contains("logo") || n.Contains("applist") || n.Contains("icon") || n.Contains("square") || n.Contains("tile");
                                        })
                                        .ToList();

                                    var bestLogo = logos.FirstOrDefault(l => l.Contains("44", StringComparison.OrdinalIgnoreCase) || l.Contains("Square44", StringComparison.OrdinalIgnoreCase))
                                                  ?? logos.FirstOrDefault(l => l.Contains("100", StringComparison.OrdinalIgnoreCase) || l.Contains("150", StringComparison.OrdinalIgnoreCase))
                                                  ?? logos.FirstOrDefault();
                                    if (bestLogo != null)
                                    {
                                        logoPath = bestLogo;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    _packageCache[fam] = (disp, pub, ver, installLoc, logoPath, hasStartupTask);
                }
                catch { }
            }
        }

        _lastPackageCacheTime = DateTime.UtcNow;
        }
        catch { }
    }

    private (string DisplayName, string Publisher, string Version, string ExePath, string? IconBase64) ResolvePackageMetadata(string pkgFamily, string taskSubName)
    {
        RefreshPackageCacheIfNeeded();

        string disp = "";
        string pub = "";
        string ver = "Store App";
        string exe = pkgFamily;
        string logoPath = "";

        if (_packageCache.TryGetValue(pkgFamily, out var info))
        {
            disp = info.DisplayName;
            pub = info.Publisher;
            ver = string.IsNullOrEmpty(info.Version) ? ver : info.Version;
            exe = string.IsNullOrEmpty(info.ExePath) ? exe : info.ExePath;
            logoPath = info.LogoPath;
        }

        // Clean up empty, placeholder, or ms-resource strings and normalize known package names to match Task Manager
        string? canonicalName = FriendlyNameFromPackageFamily(pkgFamily);
        if (!string.IsNullOrEmpty(canonicalName))
        {
            disp = canonicalName;
        }
        else if (string.IsNullOrWhiteSpace(disp) || disp.StartsWith("@") || disp.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
        {
            string name = pkgFamily.Split('_')[0];
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            disp = string.IsNullOrWhiteSpace(name) ? taskSubName : name;
        }

        string friendlyPub = FriendlyPublisherFromPackageFamily(pkgFamily, pub);
        if (!string.IsNullOrEmpty(friendlyPub))
        {
            pub = friendlyPub;
        }

        string? iconBase64 = ExtractIconBase64(exe, logoPath);

        return (disp, pub, ver, exe, iconBase64);
    }

    private static string? FriendlyNameFromPackageFamily(string pkgFamily)
    {
        string lower = pkgFamily.ToLowerInvariant();
        if (lower.Contains("whatsapp")) return "WhatsApp";
        if (lower.Contains("intelarc") || lower.Contains("intelgraphics")) return "Intel® Graphics Software";
        if (lower.Contains("gamingapp") || lower.Contains("xbox")) return "XBOX";
        if (lower.Contains("microsoftofficehub") || lower.Contains("copilot")) return "Copilot";
        if (lower.Contains("powerautomate")) return "Power Automate Desktop";
        if (lower.Contains("startexperiencesapp")) return "Start Experiences App";
        if (lower.Contains("todos")) return "Microsoft To Do";
        if (lower.Contains("windowsterminal")) return "Terminal";
        if (lower.Contains("yourphone")) return "Phone Link";
        if (lower.Contains("crossdevice")) return "Mobile devices";
        if (lower.Contains("msteams") || lower.Contains("teams")) return "Microsoft Teams";
        if (lower.Contains("spotifymusic") || lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("telegramdesktop") || lower.Contains("telegram")) return "Telegram Desktop";
        return null;
    }

    private static string FriendlyPublisherFromPackageFamily(string pkgFamily, string rawPub)
    {
        string lower = (pkgFamily + " " + rawPub).ToLowerInvariant();
        if (lower.Contains("whatsapp")) return "WhatsApp Inc.";
        if (lower.Contains("intel")) return "INTEL CORP";
        if (lower.Contains("spotify")) return "Spotify AB";
        if (lower.Contains("telegram")) return "Telegram Messenger LLP";
        if (lower.Contains("claude")) return "Anthropic, PBC";
        if (lower.Contains("crossdevice")) return "Microsoft Windows";
        if (lower.Contains("microsoft") || lower.Contains("cw5n1h2txyewy") || lower.Contains("8wekyb3d8bbwe")) return "Microsoft Corporation";
        if (!string.IsNullOrWhiteSpace(rawPub) && !rawPub.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return rawPub;
        return "Unknown";
    }

    private string ExtractExePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        string clean = commandLine.Trim();

        if (clean.StartsWith("\""))
        {
            int nextQuote = clean.IndexOf('"', 1);
            if (nextQuote > 1)
            {
                clean = clean.Substring(1, nextQuote - 1);
            }
        }
        else
        {
            int exeIdx = clean.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0)
            {
                clean = clean.Substring(0, exeIdx + 4);
            }
            else
            {
                int spaceIdx = clean.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    clean = clean.Substring(0, spaceIdx);
                }
            }
        }

        try
        {
            clean = Environment.ExpandEnvironmentVariables(clean).Trim();
        }
        catch { }

        return clean;
    }

    private string InferPublisher(string valueName, string exePath)
    {
        string text = (valueName + " " + exePath).ToLowerInvariant();
        if (text.Contains("discord")) return "Discord Inc.";
        if (text.Contains("steam") || text.Contains("valvesoftware")) return "Valve Corporation";
        if (text.Contains("spotify")) return "Spotify AB";
        if (text.Contains("logitech") || text.Contains("lghub")) return "Logitech G HUB";
        if (text.Contains("epicgames") || text.Contains("epic games")) return "Epic Games, Inc.";
        if (text.Contains("riotgames") || text.Contains("riot client")) return "Riot Games, Inc.";
        if (text.Contains("razer")) return "Razer Inc.";
        if (text.Contains("voicemod")) return "Voicemod S.L.";
        if (text.Contains("bluestacks")) return "BlueStack Systems, Inc.";
        if (text.Contains("nvidia")) return "NVIDIA Corporation";
        if (text.Contains("realtek") || text.Contains("rtkaud")) return "Realtek Audio Universal Service";
        if (text.Contains("intel")) return "INTEL CORP";
        if (text.Contains("microsoft") || text.Contains("securityhealth") || text.Contains("windowsdefender")) return "Microsoft Corporation";
        if (text.Contains("google") || text.Contains("chrome")) return "Google LLC";
        if (text.Contains("telegram")) return "Telegram Messenger LLP";
        if (text.Contains("whatsapp")) return "WhatsApp Inc.";
        return "Unknown";
    }

    private (string Category, bool IsSystem) ClassifyStartupItem(string valueName, string displayName, string publisher, string exePath)
    {
        string text = (valueName + " " + displayName + " " + publisher + " " + exePath).ToLowerInvariant();

        if (text.Contains("securityhealth") || text.Contains("defender") || text.Contains("antivirus") || text.Contains("security"))
        {
            return ("SECURITY", true);
        }

        if (text.Contains("nvidia") || text.Contains("realtek") || text.Contains("rtkaud") || text.Contains("intel graphics") || text.Contains("amd radeon") || text.Contains("driver"))
        {
            return ("DRIVER", true);
        }

        if (publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            (exePath.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
             exePath.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)))
        {
            return ("SYSTEM", true);
        }

        return ("USER APPLICATION", false);
    }

    private (string Impact, string ImpactMethod) EvaluateImpact(string valueName, string displayName, string publisher, string exePath)
    {
        string text = (valueName + " " + displayName + " " + publisher + " " + exePath).ToLowerInvariant();

        // Heavy suites with multi-process renderers and background daemon networks
        if (text.Contains("discord") || text.Contains("steam") || text.Contains("epicgames") ||
            text.Contains("bluestacks") || text.Contains("voicemod") || text.Contains("creative cloud") ||
            text.Contains("razer synapse") || text.Contains("lghub") || text.Contains("overwolf") ||
            text.Contains("teams") || text.Contains("copilot"))
        {
            return ("HIGH", "ESTIMATED");
        }

        if (text.Contains("spotify") || text.Contains("viber") || text.Contains("telegram") ||
            text.Contains("skype") || text.Contains("slack") || text.Contains("urbanvpn") ||
            text.Contains("torrent") || text.Contains("onedrive") || text.Contains("dropbox") ||
            text.Contains("whatsapp") || text.Contains("phone link") || text.Contains("crossdevice") ||
            text.Contains("power automate") || text.Contains("todo"))
        {
            return ("MEDIUM", "ESTIMATED");
        }

        if (text.Contains("securityhealth") || text.Contains("rtkaud") || text.Contains("audio") ||
            text.Contains("tray") || text.Contains("ctfmon") || text.Contains("helper") ||
            text.Contains("intel graphics") || text.Contains("terminal"))
        {
            return ("LOW", "ESTIMATED");
        }

        return ("LOW", "NOT MEASURED");
    }

    public bool DisableEntry(string source, string path, string valueName, string? id = null)
    {
        return SetEntryState(source, path, valueName, id, enabled: false);
    }

    public bool EnableEntry(string source, string path, string valueName, string? id = null)
    {
        return SetEntryState(source, path, valueName, id, enabled: true);
    }

    private static string? ResolveTargetUserSid(string? id, string? path)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var match = System.Text.RegularExpressions.Regex.Match(id, @"S-1-5-21-\d+-\d+-\d+-\d+");
            if (match.Success) return match.Value;
        }
        if (!string.IsNullOrEmpty(path))
        {
            var match = System.Text.RegularExpressions.Regex.Match(path, @"S-1-5-21-\d+-\d+-\d+-\d+");
            if (match.Success) return match.Value;
        }
        if (!WindowsStartupRegistrar.IsRunningAsSystem())
        {
            return null; // Desktop user session uses CurrentUser directly
        }

        var activeSids = WindowsStartupRegistrar.GetInteractiveUserSids();
        if (activeSids.Count == 1)
        {
            return activeSids[0];
        }
        if (activeSids.Count > 1)
        {
            string consoleSid = WindowsStartupRegistrar.GetActiveConsoleSessionSid();
            if (!string.IsNullOrEmpty(consoleSid) && activeSids.Contains(consoleSid, StringComparer.OrdinalIgnoreCase))
            {
                return consoleSid;
            }
        }
        return null;
    }

    private bool SetEntryState(string source, string path, string valueName, string? id, bool enabled)
    {
        // 1. Safety enforcement: Windows Security components cannot be disabled
        if (!enabled &&
            (valueName.Contains("SecurityHealth", StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrEmpty(id) && id.Contains("SecurityHealth", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("Windows Security notification components cannot be disabled for system safety.");
        }

        // 2. Self-protection: ERROR OPTIMIZER startup toggle syncs with UnifiedSettingsStore
        if (valueName.StartsWith("ERROR OPTIMIZER", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(id) && id.Contains("ERROR OPTIMIZER", StringComparison.OrdinalIgnoreCase)))
        {
            var s = UnifiedSettingsStore.LoadSafe();
            s.StartWithWindows = enabled;
            UnifiedSettingsStore.Save(s);
            var regTx = WindowsStartupRegistrar.Instance.ConfigureStartupTransactional(enabled, s.StartMinimized, Environment.ProcessPath);
            return regTx.Success;
        }

        // Fast path 1: Modern MSIX task
        if (source == "MSIX_STARTUP_TASK" || (!string.IsNullOrEmpty(path) && path.Contains(@"AppModel\SystemAppData", StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(id) && id.StartsWith("MSIX|", StringComparison.OrdinalIgnoreCase)))
        {
            string mPath = !string.IsNullOrEmpty(path) ? path : @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
            return SetMsixTaskState(mPath, valueName, valueName, enabled, id);
        }

        // Fast path 2: Classic registry or startup folder by Source or Path
        var loc = _locations.FirstOrDefault(l =>
            (!string.IsNullOrEmpty(source) && l.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(path) && l.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

        if (loc != null)
        {
            return SetClassicStartupState(loc, valueName, valueName, enabled, id, path);
        }

        // Fallback: full enumeration only when targeted attributes were missing
        var entries = GetStartupEntries();
        StartupEntryState? entry = null;

        // 1. Exact match by Id
        if (!string.IsNullOrEmpty(id))
        {
            entry = entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // 2. Exact match by Source + ValueName (or DisplayName)
        if (entry == null && !string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(valueName))
        {
            entry = entries.FirstOrDefault(e =>
                string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.DisplayName, valueName, StringComparison.OrdinalIgnoreCase)));
        }

        // 3. Fallback match by ValueName or DisplayName
        if (entry == null && !string.IsNullOrEmpty(valueName))
        {
            entry = entries.FirstOrDefault(e =>
                string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.DisplayName, valueName, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null)
        {
            return false;
        }

        // Branch 1: Modern MSIX task
        if (entry.Source == "MSIX_STARTUP_TASK" || entry.Path.Contains(@"AppModel\SystemAppData", StringComparison.OrdinalIgnoreCase))
        {
            return SetMsixTaskState(entry.Path, entry.ValueName, entry.DisplayName, enabled, entry.Id);
        }

        // Branch 2: Classic registry or startup folder
        var locFallback = _locations.FirstOrDefault(l =>
            l.Source.Equals(entry.Source, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(entry.Path) && l.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)));

        if (locFallback != null)
        {
            return SetClassicStartupState(locFallback, entry.ValueName, entry.DisplayName, enabled, entry.Id, entry.Path);
        }

        return false;
    }

    private bool SetMsixTaskState(string path, string valueName, string displayName, bool enabled, string? id = null)
    {
        try
        {
            // Windows AppModel StartupTaskState: 2 = Enabled; 1 = DisabledByUser
            int targetDword = enabled ? 2 : 1;
            bool modified = false;

            var targets = new List<(RegistryKey Root, string SubKey)>();
            bool isSystem = WindowsStartupRegistrar.IsRunningAsSystem();
            string? targetSid = ResolveTargetUserSid(id, path);

            if (!isSystem)
            {
                targets.Add((Registry.CurrentUser, path));
            }
            else if (!string.IsNullOrEmpty(targetSid))
            {
                string cleanSubKey = path.StartsWith(targetSid, StringComparison.OrdinalIgnoreCase)
                    ? path
                    : $@"{targetSid}\{path}";
                targets.Add((Registry.Users, cleanSubKey));
            }
            else
            {
                Debug.WriteLine($"[StartupManager] Rejecting ambiguous user MSIX startup mutation for '{valueName}' under LocalSystem.");
                return false;
            }

            if (!enabled)
            {
                try
                {
                    BackupManager.Instance.CaptureTargetedGenericTweak(
                        "StartupManager",
                        $"msix.{valueName}".ToLowerInvariant(),
                        $"Disable Startup: {displayName}",
                        "Startup",
                        "MsixStartupTask",
                        "Enabled",
                        "Disabled",
                        "StartupRestore",
                        true,
                        "SAFE",
                        targetKey: $"MSIX_STARTUP_TASK|{path}",
                        valueName: valueName);
                }
                catch { }
            }

            foreach (var (root, subKey) in targets)
            {
                try
                {
                    using var key = root.OpenSubKey(subKey, true);
                    if (key != null)
                    {
                        key.SetValue("State", targetDword, RegistryValueKind.DWord);

                        // Immediate Windows readback verification
                        if (key.GetValue("State") is int s && (enabled ? (s == 2 || s == 4) : (s == 1 || s == 0 || s == 3)))
                        {
                            modified = true;
                        }
                    }
                }
                catch { }
            }

            // Also synchronize with StartupApproved\StartupApp for the specific target user hive
            try
            {
                byte[] binaryFlag = enabled
                    ? new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
                    : new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                byte[] ftBytes = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                Buffer.BlockCopy(ftBytes, 0, binaryFlag, 4, Math.Min(8, binaryFlag.Length - 4));

                var appTargets = new List<(RegistryKey Root, string AppPath)>();
                if (!isSystem)
                {
                    appTargets.Add((Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupApp"));
                }
                else if (!string.IsNullOrEmpty(targetSid))
                {
                    appTargets.Add((Registry.Users, $@"{targetSid}\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupApp"));
                }

                foreach (var (r, p) in appTargets)
                {
                    try
                    {
                        using var approvedApp = r.CreateSubKey(p, true);
                        if (approvedApp != null)
                        {
                            approvedApp.SetValue(valueName, binaryFlag, RegistryValueKind.Binary);
                            approvedApp.Flush();
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return modified;
        }
        catch
        {
            return false;
        }
    }

    private bool SetClassicStartupState(StartupLocation loc, string valueName, string displayName, bool enabled, string? id = null, string? path = null)
    {
        if (loc.Root == null) return false;

        try
        {
            var targets = new List<(RegistryKey Root, string ApprovedPath)>();
            bool isSystem = WindowsStartupRegistrar.IsRunningAsSystem();
            string? targetSid = ResolveTargetUserSid(id, path);

            if (loc.Root == Registry.CurrentUser)
            {
                if (!isSystem)
                {
                    targets.Add((Registry.CurrentUser, loc.ApprovedPath));
                }
                else if (!string.IsNullOrEmpty(targetSid))
                {
                    targets.Add((Registry.Users, $@"{targetSid}\{loc.ApprovedPath}"));
                }
                else
                {
                    Debug.WriteLine($"[StartupManager] Rejecting ambiguous user startup mutation for '{valueName}' under LocalSystem.");
                    return false;
                }
            }
            else
            {
                // Machine location (HKLM)
                targets.Add((loc.Root, loc.ApprovedPath));

                // Also update target user override hive if available
                if (!isSystem)
                {
                    targets.Add((Registry.CurrentUser, loc.ApprovedPath));
                }
                else if (!string.IsNullOrEmpty(targetSid))
                {
                    targets.Add((Registry.Users, $@"{targetSid}\{loc.ApprovedPath}"));
                }
            }

            // Standard Windows Task Manager StartupApproved format:
            // 0x02 = Enabled, 0x03 = Disabled by Task Manager
            byte[] stateVal = enabled
                ? new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
                : new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            byte[] ftBytes = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Buffer.BlockCopy(ftBytes, 0, stateVal, 4, Math.Min(8, stateVal.Length - 4));

            if (!enabled)
            {
                try
                {
                    BackupManager.Instance.CaptureTargetedGenericTweak(
                        "StartupManager",
                        $"{loc.Source}.{valueName}".ToLowerInvariant().Replace(' ', '.'),
                        $"Disable Startup: {displayName}",
                        "Startup",
                        "StartupApproved",
                        "Enabled",
                        "Disabled",
                        "StartupRestore",
                        true,
                        "SAFE",
                        targetKey: $"{loc.Source}|{loc.Path}",
                        valueName: valueName);
                }
                catch { }
            }

            bool verified = false;

            foreach (var (root, approvedPath) in targets)
            {
                try
                {
                    using var approvedKey = root.CreateSubKey(approvedPath, true);
                    if (approvedKey != null)
                    {
                        approvedKey.SetValue(valueName, stateVal, RegistryValueKind.Binary);

                        // Immediate Windows readback verification
                        var readback = approvedKey.GetValue(valueName) as byte[];
                        if (readback != null && readback.Length > 0)
                        {
                            bool matches = enabled
                                ? (readback[0] == 0x02 || (readback[0] & 1) == 0)
                                : (readback[0] == 0x03 || (readback[0] & 1) != 0);
                            if (matches) verified = true;
                        }
                    }
                }
                catch { }
            }

            return verified;
        }
        catch
        {
            return false;
        }
    }
}
