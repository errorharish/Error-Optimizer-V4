namespace BiosOptimizer.Core.Interfaces;

[Flags]
public enum PackageLifecycleState
{
    Unknown = 0,
    CurrentUserRegistered = 1 << 0,
    OtherUserRegistered = 1 << 1,
    Provisioned = 1 << 2,
    Staged = 1 << 3,
    RemovedForCurrentUser = 1 << 4,
    RemovedForAllUsers = 1 << 5,
    ProtectedSystemManaged = 1 << 6
}

public class PackageStateEvaluation
{
    public string PackageName { get; set; } = string.Empty;
    public string FullPackageName { get; set; } = string.Empty;
    public PackageLifecycleState LifecycleState { get; set; } = PackageLifecycleState.Unknown;
    public List<string> RegisteredUsers { get; set; } = new();
    public bool IsProvisioned { get; set; }
    public bool IsStagedInRepository { get; set; }
    public bool IsProtected { get; set; }
    public string ProtectionReason { get; set; } = string.Empty;
    public string DiagnosticEvidence { get; set; } = string.Empty;
}

public class InstalledPackage
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string PublisherDisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsFramework { get; set; }
    public bool IsResource { get; set; }
    public bool IsSystemComponent { get; set; }
    public bool UserInstalled { get; set; }
    public bool Provisioned { get; set; }
    public bool IsStagedOnly { get; set; }
    public bool IsInstalledForActiveUser { get; set; }
    public PackageLifecycleState LifecycleState { get; set; } = PackageLifecycleState.Unknown;
}

public interface IPackageManager
{
    List<InstalledPackage> GetInstalledPackages();
    InstalledPackage? GetPackage(string name);
    InstalledPackage? GetPackage(string name, bool activeUserOnly);
    PackageStateEvaluation EvaluatePackageState(string name);
    bool RemovePackage(string fullName, bool allUsers);
    bool RestorePackage(string familyName);
}

public interface IOemApplicationDetector
{
    List<string> DetectOemApplications();
}
