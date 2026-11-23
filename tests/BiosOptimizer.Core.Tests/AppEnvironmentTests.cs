#nullable enable
using System;
using System.IO;
using BiosOptimizer.Core.Common;
using Xunit;

namespace BiosOptimizer.Core.Tests
{
    public class AppEnvironmentTests
    {
        [Fact]
        public void T_ENV_1_ExeUnderDistWinX64_IsDevelopment()
        {
            string path = @"D:\SOFTWARE PROJECTS\ERROR OPTIMIZER\dist\win-x64";
            var mode = AppEnvironment.DetectMode(path);
            Assert.Equal(AppMode.Development, mode);
        }

        [Fact]
        public void T_ENV_2_ExeUnderPublishV4_IsDevelopment()
        {
            string path = @"D:\SOFTWARE PROJECTS\ERROR OPTIMIZER\publish\v4";
            var mode = AppEnvironment.DetectMode(path);
            Assert.Equal(AppMode.Development, mode);
        }

        [Fact]
        public void T_ENV_3_ExeUnderBinReleaseNet8_IsDevelopment()
        {
            string path = @"D:\SOFTWARE PROJECTS\ERROR OPTIMIZER\src\BiosOptimizer.GUI\bin\Release\net8.0-windows10.0.19041\win-x64";
            var mode = AppEnvironment.DetectMode(path);
            Assert.Equal(AppMode.Development, mode);
        }

        [Fact]
        public void T_ENV_4_ExeUnderProgramFiles_IsInstalled()
        {
            string path = @"C:\Program Files\Error Optimizer";
            var mode = AppEnvironment.DetectMode(path);
            Assert.Equal(AppMode.Installed, mode);
        }

        [Fact]
        public void T_ENV_5_DevelopmentMarkerFile_OverridesToDevelopment()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"env_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // Without marker file, standard temp directory evaluates to Installed
                var defaultMode = AppEnvironment.DetectMode(tempDir);
                Assert.Equal(AppMode.Installed, defaultMode);

                // With .development marker file
                string marker = Path.Combine(tempDir, ".development");
                File.WriteAllText(marker, "dev");

                var devMode = AppEnvironment.DetectMode(tempDir);
                Assert.Equal(AppMode.Development, devMode);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
