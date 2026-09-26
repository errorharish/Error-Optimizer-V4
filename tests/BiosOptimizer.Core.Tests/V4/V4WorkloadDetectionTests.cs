using System.Diagnostics;
using BiosOptimizer.Core.V4.Workloads;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4WorkloadDetectionTests
    {
        [Fact]
        public void WorkloadDetectionEngine_DetectsCurrentProcesses()
        {
            var engine = new WorkloadDetectionEngine();
            var processes = Process.GetProcesses();

            var status = engine.DetectActiveWorkloads(processes);
            Assert.NotNull(status);
            Assert.NotNull(status.ActiveWorkloadNames);
            Assert.NotNull(status.ActiveWorkloadProcesses);
        }

        [Theory]
        [InlineData("steam", true, false, true)]
        [InlineData("steam", false, false, false)]
        [InlineData("Adobe Premiere Pro", false, true, true)]
        [InlineData("Adobe Premiere Pro", true, false, false)]
        [InlineData("Resolve", false, true, true)]
        public void IsWorkloadCritical_RespectsActiveWorkloadFlags(string procName, bool gaming, bool editing, bool expectedCritical)
        {
            var engine = new WorkloadDetectionEngine();
            bool isCrit = engine.IsWorkloadCritical(procName, gaming, editing);
            Assert.Equal(expectedCritical, isCrit);
        }
    }
}
