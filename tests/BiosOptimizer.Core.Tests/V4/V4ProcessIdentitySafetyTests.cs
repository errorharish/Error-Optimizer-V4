using System;
using System.Diagnostics;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class V4ProcessIdentitySafetyTests
    {
        private readonly ProcessIdentitySafety _safety = new();

        [Fact]
        public void ValidateIdentity_WithKernelPid_RejectsImmediately()
        {
            var candidate = new OptimizationCandidateDto
            {
                ProcessId = 4,
                DisplayName = "System"
            };

            bool valid = _safety.ValidateIdentity(candidate, out string abortReason);
            Assert.False(valid);
            Assert.Contains("kernel", abortReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateIdentity_WithExitedPid_DetectsExitedProcess()
        {
            // Pick a high nonexistent PID
            var candidate = new OptimizationCandidateDto
            {
                ProcessId = 999999,
                DisplayName = "NonExistentApp"
            };

            bool valid = _safety.ValidateIdentity(candidate, out string abortReason);
            Assert.False(valid);
            Assert.Contains("exited", abortReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateIdentity_WithCurrentProcess_AndMatchingName_Passes()
        {
            var current = Process.GetCurrentProcess();
            var startTime = ProcessIdentitySafety.GetProcessStartTime(current.Id);

            var candidate = new OptimizationCandidateDto
            {
                ProcessId = current.Id,
                DisplayName = current.ProcessName,
                TargetIdentifier = current.ProcessName,
                ProcessStartTime = startTime
            };

            bool valid = _safety.ValidateIdentity(candidate, out string abortReason);
            Assert.True(valid, $"Identity validation should pass for current process: {abortReason}");
        }

        [Fact]
        public void ValidateIdentity_WithMismatchingName_RejectsAsPidReuse()
        {
            var current = Process.GetCurrentProcess();

            var candidate = new OptimizationCandidateDto
            {
                ProcessId = current.Id,
                DisplayName = "FakeDisguisedProcessName",
                TargetIdentifier = "FakeDisguisedProcessName"
            };

            bool valid = _safety.ValidateIdentity(candidate, out string abortReason);
            Assert.False(valid);
            Assert.Contains("PID reuse detected", abortReason);
        }
    }
}
