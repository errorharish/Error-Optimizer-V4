using System.Runtime.InteropServices;
using Windows.Management.Deployment;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Services;
using Microsoft.Win32;

namespace BiosOptimizer.CLI.Adapters;

public class WindowsPackageManager : IPackageManager
{
    private List<InstalledPackage>? _cachedPackages;
    private DateTime _lastPackageScan = DateTime.MinValue;

    public static string? GetActiveUserSid()
    {
        var info = InteractiveUserResolver.ResolveActiveUser();
        return info.IsAvailable ? info.UserSid : null;
    }

    public List<InstalledPackage> GetInstalledPackages()
    {
        if (_cachedPackages != null && (DateTime.UtcNow - _lastPackageScan).TotalSeconds < 5)
        {
            return _cachedPackages;
        }
        var list = new List<InstalledPackage>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return list;

        string? activeSid = GetActiveUserSid();

        try
        {
            var pm = new PackageManager();
            var packages = !string.IsNullOrEmpty(activeSid)
                ? pm.FindPackagesForUser(activeSid)
                : pm.FindPackagesForUser("");

            foreach (var pkg in packages)
            {
                var dispName = "";
                var pubDispName = "";
                try { dispName = pkg.DisplayName; } catch { }
                try { pubDispName = pkg.PublisherDisplayName; } catch { }
                
                if (string.IsNullOrWhiteSpace(dispName)) dispName = pkg.Id.Name;
                if (string.IsNullOrWhiteSpace(pubDispName)) pubDispName = GetFriendlyPublisher(pkg.Id.Publisher);
                
                list.Add(new InstalledPackage
                {
                    Name = pkg.Id.Name,
                    DisplayName = dispName,
                    FullName = pkg.Id.FullName,
                    FamilyName = pkg.Id.FamilyName,
                    Publisher = pkg.Id.Publisher,
                    PublisherDisplayName = pubDispName,
                    Version = $"{pkg.Id.Version.Major}.{pkg.Id.Version.Minor}.{pkg.Id.Version.Build}.{pkg.Id.Version.Revision}",
                    IsFramework = pkg.IsFramework,
                    IsResource = pkg.IsResourcePackage,
                    IsSystemComponent = pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System,
                    UserInstalled = true,
                    IsInstalledForActiveUser = true,
                    IsStagedOnly = false,
                    Provisioned = false,
                    LifecycleState = PackageLifecycleState.CurrentUserRegistered | PackageLifecycleState.Staged
                });
            }
        }
        catch (Exception)
        {
            try
            {
                string psCmd = !string.IsNullOrEmpty(activeSid)
                    ? $"Get-AppxPackage -User '{activeSid}' | Select-Object Name, PackageFullName, Publisher, Version, IsFramework, IsResourcePackage, SignatureKind | ConvertTo-Json -Depth 2"
                    : "Get-AppxPackage | Select-Object Name, PackageFullName, Publisher, Version, IsFramework, IsResourcePackage, SignatureKind | ConvertTo-Json -Depth 2";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var elem in doc.RootElement.EnumerateArray())
                        {
                            var name = elem.GetProperty("Name").GetString();
                            var fullName = elem.GetProperty("PackageFullName").GetString();
                            var publisher = elem.GetProperty("Publisher").GetString();
                            var version = elem.GetProperty("Version").GetString();
                            var isFramework = elem.TryGetProperty("IsFramework", out var f) && f.GetBoolean();
                            var isResource = elem.TryGetProperty("IsResourcePackage", out var r) && r.GetBoolean();
                            var signatureKind = elem.TryGetProperty("SignatureKind", out var s) ? s.ToString() : "";
                            
                            var dispName = name ?? "";
                            var pubDispName = GetFriendlyPublisher(publisher ?? "");
                            
                            list.Add(new InstalledPackage
                            {
                                Name = name ?? "",
                                DisplayName = dispName,
                                FullName = fullName ?? "",
                                FamilyName = "",
                                Publisher = publisher ?? "",
                                PublisherDisplayName = pubDispName,
                                Version = version ?? "",
                                IsFramework = isFramework,
                                IsResource = isResource,
                                IsSystemComponent = signatureKind == "System",
                                UserInstalled = true,
                                IsInstalledForActiveUser = true,
                                IsStagedOnly = false,
                                LifecycleState = PackageLifecycleState.CurrentUserRegistered | PackageLifecycleState.Staged
                            });
                        }
                    }
                    else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var elem = doc.RootElement;
                        var name = elem.GetProperty("Name").GetString();
                        var fullName = elem.GetProperty("PackageFullName").GetString();
                        var publisher = elem.GetProperty("Publisher").GetString();
                        var version = elem.GetProperty("Version").GetString();
                        var isFramework = elem.TryGetProperty("IsFramework", out var f) && f.GetBoolean();
                        var isResource = elem.TryGetProperty("IsResourcePackage", out var r) && r.GetBoolean();
                        var signatureKind = elem.TryGetProperty("SignatureKind", out var s) ? s.ToString() : "";
                        
                        var dispName = name ?? "";
                        var pubDispName = GetFriendlyPublisher(publisher ?? "");
                        
                        list.Add(new InstalledPackage
                        {
                            Name = name ?? "",
                            DisplayName = dispName,
                            FullName = fullName ?? "",
                            FamilyName = "",
                            Publisher = publisher ?? "",
                            PublisherDisplayName = pubDispName,
                            Version = version ?? "",
                            IsFramework = isFramework,
                            IsResource = isResource,
                            IsSystemComponent = signatureKind == "System",
                            UserInstalled = true,
                            IsInstalledForActiveUser = true,
                            IsStagedOnly = false,
                            LifecycleState = PackageLifecycleState.CurrentUserRegistered | PackageLifecycleState.Staged
                        });
                    }
                }
            }
            catch { }
        }

        _cachedPackages = list;
        _lastPackageScan = DateTime.UtcNow;
        return list;
    }

    private string GetFriendlyPublisher(string rawPublisher)
    {
        if (string.IsNullOrWhiteSpace(rawPublisher)) return string.Empty;
        
        var parts = rawPublisher.Split(',');
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p.StartsWith("O=")) return p.Substring(2);
        }
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p.StartsWith("CN=")) return p.Substring(3);
        }
        return rawPublisher;
    }

    public InstalledPackage? GetPackage(string name)
    {
        return GetPackage(name, activeUserOnly: true);
    }

    public InstalledPackage? GetPackage(string name, bool activeUserOnly)
    {
        var packages = GetInstalledPackages();
        return packages.FirstOrDefault(p => 
            (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || 
             p.FullName.Equals(name, StringComparison.OrdinalIgnoreCase) || 
             p.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
            (!activeUserOnly || p.IsInstalledForActiveUser));
    }

    public PackageStateEvaluation EvaluatePackageState(string name)
    {
        var eval = new PackageStateEvaluation { PackageName = name };
        var activeUser = InteractiveUserResolver.ResolveActiveUser();

        string activeSid = activeUser.IsAvailable ? (activeUser.UserSid ?? string.Empty) : string.Empty;

        try
        {
            // 1. Check active user registration
            if (!string.IsNullOrEmpty(activeSid))
            {
                var pm = new PackageManager();
                var userPkgs = pm.FindPackagesForUser(activeSid);
                var match = userPkgs.FirstOrDefault(p =>
                    p.Id.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.FullName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    eval.FullPackageName = match.Id.FullName;
                    eval.LifecycleState |= PackageLifecycleState.CurrentUserRegistered;
                    eval.RegisteredUsers.Add(activeUser.UserName ?? activeSid);
                    eval.IsProtected = match.IsFramework || match.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System;
                    if (eval.IsProtected)
                    {
                        eval.LifecycleState |= PackageLifecycleState.ProtectedSystemManaged;
                        eval.ProtectionReason = match.IsFramework ? "Windows Shared Framework Component" : "Windows Operating System Core Component";
                    }
                }
                else
                {
                    eval.LifecycleState |= PackageLifecycleState.RemovedForCurrentUser;
                }
            }
            else
            {
                eval.DiagnosticEvidence = "No unambiguous interactive console user resolved.";
            }

            // 2. Query all-users staging & registrations
            var allUsersPkg = GetInstalledPackages().FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                p.FullName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                p.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (allUsersPkg != null)
            {
                if (string.IsNullOrEmpty(eval.FullPackageName))
                    eval.FullPackageName = allUsersPkg.FullName;

                eval.IsStagedInRepository = true;
                eval.LifecycleState |= PackageLifecycleState.Staged;

                if (allUsersPkg.IsFramework || allUsersPkg.IsSystemComponent)
                {
                    eval.IsProtected = true;
                    eval.LifecycleState |= PackageLifecycleState.ProtectedSystemManaged;
                    eval.ProtectionReason = allUsersPkg.IsFramework ? "Windows Shared Framework Component" : "Windows Operating System Core Component";
                }
            }

            // 3. Check provisioned package status via PowerShell
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"(Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq '{name}' -or $_.PackageName -like '*{name}*' }}).PackageName\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    string provOut = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(4000);
                    if (!string.IsNullOrWhiteSpace(provOut))
                    {
                        eval.IsProvisioned = true;
                        eval.LifecycleState |= PackageLifecycleState.Provisioned;
                    }
                }
            }
            catch { }

            if (!eval.LifecycleState.HasFlag(PackageLifecycleState.CurrentUserRegistered) && !eval.LifecycleState.HasFlag(PackageLifecycleState.OtherUserRegistered))
            {
                eval.LifecycleState |= PackageLifecycleState.RemovedForAllUsers;
            }
        }
        catch (Exception ex)
        {
            eval.DiagnosticEvidence = $"Package evaluation exception: {ex.Message}";
        }

        return eval;
    }

    public bool RemovePackage(string fullName, bool allUsers)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;

        // 1. Try WinRT PackageManager with bounded timeout
        try
        {
            var task = Task.Run(() =>
            {
                var pm = new PackageManager();
                var op = allUsers
                    ? pm.RemovePackageAsync(fullName, RemovalOptions.RemoveForAllUsers)
                    : pm.RemovePackageAsync(fullName);
                return op.GetAwaiter().GetResult();
            });

            if (task.Wait(TimeSpan.FromSeconds(10)))
            {
                _cachedPackages = null; // Invalidate cache
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsPackageManager] WinRT RemovePackage warning: {ex.Message}");
        }

        // 2. Resilient fallback: PowerShell Remove-AppxPackage / Remove-AppxProvisionedPackage
        try
        {
            var activeSid = GetActiveUserSid();
            string psCmd;
            if (allUsers)
            {
                psCmd = $"Remove-AppxPackage -Package '{fullName}' -AllUsers -ErrorAction SilentlyContinue; " +
                        $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.PackageName -eq '{fullName}' }} | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
            }
            else if (!string.IsNullOrEmpty(activeSid))
            {
                psCmd = $"Get-AppxPackage -User '{activeSid}' -Package '{fullName}' | Remove-AppxPackage -User '{activeSid}' -ErrorAction SilentlyContinue";
            }
            else
            {
                psCmd = $"Remove-AppxPackage -Package '{fullName}' -ErrorAction SilentlyContinue";
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (proc.WaitForExit(8000))
                {
                    Task.WaitAll(new Task[] { outTask, errTask }, 1000);
                    _cachedPackages = null;
                    return true;
                }
                else
                {
                    try { proc.Kill(true); } catch { }
                }
            }
        }
        catch { }

        return false;
    }

    public bool RestorePackage(string familyName)
    {
        return false;
    }
}
