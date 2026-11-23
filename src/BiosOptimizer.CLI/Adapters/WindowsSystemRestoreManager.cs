using System.Management;
using BiosOptimizer.Core.Interfaces;

namespace BiosOptimizer.CLI.Adapters;

public class WindowsSystemRestoreManager : ISystemRestoreManager
{
    public bool CreateRestorePoint(string description)
    {
        // Windows System Restore Point creation is restricted exclusively to the V3 Dashboard explicit CREATE RESTORE POINT action.
        return false;
    }
}
