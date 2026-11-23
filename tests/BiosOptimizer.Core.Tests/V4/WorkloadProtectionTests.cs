#nullable enable
using System;
using System.Collections.Generic;
using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Workload;
using BiosOptimizer.IPC.Contracts;
using Xunit;

namespace BiosOptimizer.Core.Tests.V4
{
    public class WorkloadProtectionTests
    {
        private readonly WorkloadDetector _detector = WorkloadDetector.Instance;
        private readonly SafetyKernel _kernel = SafetyKernel.Instance;

        [Fact]
        public void T_WL_1_GameRunning_ShieldsGameFamily()
        {
            var processes = new[] { "steam.exe", "steamservice.exe", "GameOverlayUI.exe", "csgo.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.Game, report.ActiveWorkloads);

            var proc = new ProcessIdentityDto
            {
                ProcessId = 5555,
                ProcessName = "steamservice.exe",
                DisplayName = "Steam Client Service"
            };

            var verdict = _kernel.EvaluateProcessKill(proc, isGamingActive: true, isEditingActive: false, out string ruleCode, out string reason);
            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.True(ruleCode == "R07" || ruleCode == "R1" || ruleCode == "R23" || ruleCode.StartsWith("HP_") || ruleCode == "R28_PROTECTED_APP");
        }

        [Fact]
        public void T_WL_2_VideoEditorRunning_ShieldsEditingFamily()
        {
            var processes = new[] { "Resolve.exe", "fuscript.exe", "dpDecoder.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.VideoEditing, report.ActiveWorkloads);

            var proc = new ProcessIdentityDto
            {
                ProcessId = 6666,
                ProcessName = "Resolve.exe",
                DisplayName = "DaVinci Resolve"
            };

            var verdict = _kernel.EvaluateProcessKill(proc, isGamingActive: false, isEditingActive: true, out string ruleCode, out _);
            Assert.Equal(SafetyVerdict.Deny, verdict);
            Assert.True(ruleCode == "R07" || ruleCode == "R2" || ruleCode == "R23" || ruleCode.StartsWith("HP_") || ruleCode == "R28_PROTECTED_APP");
        }

        [Fact]
        public void T_WL_3_AudioDawRunning_ShieldsAudioWorkload()
        {
            var processes = new[] { "Ableton Live 11 Suite.exe", "ableton.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.AudioEditing, report.ActiveWorkloads);
        }

        [Fact]
        public void T_WL_4_ThreeDModelingRunning_ShieldsBlenderMaya()
        {
            var processes = new[] { "blender.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.ThreeDEditing, report.ActiveWorkloads);
        }

        [Fact]
        public void T_WL_5_VideoEncoderRunning_ShieldsEncodingProcesses()
        {
            var processes = new[] { "HandBrake.exe", "ffmpeg.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.Encoding, report.ActiveWorkloads);
        }

        [Fact]
        public void T_WL_6_StreamingRunning_ShieldsObsAndBroadcasting()
        {
            var processes = new[] { "obs64.exe", "obs-browser-page.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.Streaming, report.ActiveWorkloads);
        }

        [Fact]
        public void T_WL_7_AndroidEmulatorRunning_ShieldsEmulatorFamily()
        {
            var processes = new[] { "HD-Player.exe", "BstkSVC.exe" };
            var report = _detector.DetectWorkloads(processes);

            Assert.Contains(ActiveWorkload.Emulator, report.ActiveWorkloads);
        }

        [Fact]
        public void T_WL_8_WorkloadRegressionGate_AbortsAndTriggersRollback()
        {
            var gate = new BenchmarkGate();
            bool rollbackTriggered = false;

            var baseline = new BenchmarkSnapshot
            {
                CpuUtilizationPercent = 10.0,
                GpuUtilizationPercent = 20.0,
                WorkloadMetricValue = 16.6,
                HasReliableCounterSource = true
            };

            // Post-pass with > 10% regression on frame time or > 5% on CPU
            var regressed = new BenchmarkSnapshot
            {
                CpuUtilizationPercent = 25.0, // +15% regression (> 5% threshold)
                GpuUtilizationPercent = 20.0,
                WorkloadMetricValue = 25.0,   // +50% frame time regression (> 10% threshold)
                HasReliableCounterSource = true
            };

            var eval = gate.EvaluateRegression(baseline, regressed, ActiveWorkload.Game, () =>
            {
                rollbackTriggered = true;
            });

            Assert.False(eval.Passed);
            Assert.True(eval.TriggeredRollback);
            Assert.True(rollbackTriggered, "Rollback callback must be invoked upon detected regression");
            Assert.Contains("regression", eval.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
