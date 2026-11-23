#nullable enable
using BiosOptimizer.Core.Implementations;

namespace BiosOptimizer.GUI.Services
{
    public static class ElevationHelper
    {
        public static bool IsCurrentProcessElevated() => SystemPowerOperations.IsCurrentProcessElevated();
        public static bool IsProcessElevated(int pid) => false;
        public static System.Threading.Tasks.Task<bool> RelaunchElevatedAsync(string reason) => System.Threading.Tasks.Task.FromResult(false);
    }
}
