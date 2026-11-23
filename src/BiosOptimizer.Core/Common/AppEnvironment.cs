#nullable enable
using System;
using System.IO;

namespace BiosOptimizer.Core.Common
{
    public enum AppMode
    {
        Development,
        Installed
    }

    public static class AppEnvironment
    {
        // Full path to the app's own directory.
        public static string AppDirectory { get; } =
            (AppContext.BaseDirectory ?? AppDomain.CurrentDomain.BaseDirectory ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Detects mode from the running exe path. No config file, no env var.
        public static AppMode Mode { get; } = DetectMode();

        // Root under which all backups live.
        public static string BackupRoot { get; } = ResolveBackupRoot();

        // Writable root for app state, settings, schedules, logs, and reports.
        public static string WritableDataRoot { get; } = ResolveWritableDataRoot();

        public static string? BackupRootFallbackReason { get; private set; }
        public static string? DataRootFallbackReason { get; private set; }

        public static bool TryProbeWritableDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string probeFile = Path.Combine(dir, $".eo_write_probe_{Environment.ProcessId}_{Guid.NewGuid():N}.tmp");
                using (File.Create(probeFile, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string ResolveWritableDataRoot()
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ErrorOptimizer");

            string programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ErrorOptimizer");

            if (!string.IsNullOrWhiteSpace(programData) && TryProbeWritableDirectory(programData))
                return programData;

            if (!string.IsNullOrWhiteSpace(local) && TryProbeWritableDirectory(local))
            {
                DataRootFallbackReason = $"Primary data root '{programData}' is not writable; using '{local}'.";
                return local;
            }

            string tempFallback = Path.Combine(Path.GetTempPath(), "ErrorOptimizer");
            TryProbeWritableDirectory(tempFallback);
            DataRootFallbackReason = $"Primary and local data roots unavailable; using '{tempFallback}'.";
            return tempFallback;
        }

        public static string ResolveWritableDataDirectory(params string[] subFolders)
        {
            string path = WritableDataRoot;
            if (subFolders != null)
            {
                foreach (var sub in subFolders)
                {
                    if (!string.IsNullOrWhiteSpace(sub))
                        path = Path.Combine(path, sub);
                }
            }

            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch { }

            return path;
        }

        /// <summary>
        /// Resolves a read path preferring the active writable root, falling back to ProgramData when readable.
        /// </summary>
        public static string ResolveDataReadPath(string relativePath)
        {
            string primary = Path.Combine(WritableDataRoot, relativePath);
            if (File.Exists(primary))
                return primary;

            string programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ErrorOptimizer",
                relativePath);
            if (!string.Equals(primary, programData, StringComparison.OrdinalIgnoreCase) && File.Exists(programData))
                return programData;

            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ErrorOptimizer",
                relativePath);
            if (!string.Equals(primary, local, StringComparison.OrdinalIgnoreCase) && File.Exists(local))
                return local;

            return primary;
        }

        public static AppMode DetectMode(string? appDir = null)
        {
            var dir = (appDir ?? AppDirectory).ToLowerInvariant();

            // Explicit dev/test build output folders (dist\win-x64 is production/installed layout)
            if (dir.Contains(@"\publish\v4") ||
                dir.Contains(@"/publish/v4") ||
                dir.Contains(@"\bin\release\net") ||
                dir.Contains(@"/bin/release/net") ||
                dir.Contains(@"\bin\debug\net") ||
                dir.Contains(@"/bin/debug/net") ||
                dir.Contains(@"\error optimizer\publish") ||
                dir.Contains(@"/error optimizer/publish"))
            {
                return AppMode.Development;
            }

            // Explicit dev marker file next to exe → Development
            string checkDir = appDir ?? AppDirectory;
            if (File.Exists(Path.Combine(checkDir, ".development")))
                return AppMode.Development;

            // Otherwise assume installed / standalone production distribution
            return AppMode.Installed;
        }

        public static IReadOnlyList<string> GetLegacyBackupDirectories()
        {
            var list = new List<string>();
            string progDataLegacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ErrorOptimizer", "backups");
            string localLegacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ErrorOptimizer", "backups");

            string progDataSpaced = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Error Optimizer", "Backups");
            string progDataSpacedLower = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Error Optimizer", "backups");
            string localSpaced = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Error Optimizer", "Backups");
            string localSpacedLower = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Error Optimizer", "backups");

            if (!string.Equals(progDataLegacy, BackupRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(progDataLegacy))
                list.Add(progDataLegacy);
            if (!string.Equals(localLegacy, BackupRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(localLegacy))
                list.Add(localLegacy);
            if (!string.Equals(progDataSpaced, BackupRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(progDataSpaced))
                list.Add(progDataSpaced);
            if (!string.Equals(progDataSpacedLower, BackupRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(progDataSpacedLower))
                list.Add(progDataSpacedLower);
            if (!string.Equals(localSpaced, BackupRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(localSpaced))
                list.Add(localSpaced);
            if (!string.Equals(localSpacedLower, BackupRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(localSpacedLower))
                list.Add(localSpacedLower);

            return list;
        }

        public static string ResolveBackupRoot(AppMode? modeOverride = null, string? appDirOverride = null)
        {
            var mode = modeOverride ?? (appDirOverride != null ? DetectMode(appDirOverride) : Mode);
            var appDir = appDirOverride ?? AppDirectory;

            string candidate;
            if (mode == AppMode.Development)
            {
                // Walk up from the exe to find the project root.
                // Start at AppDirectory, go up until we find a folder that
                // contains both "src" and "dist" (or "publish").
                DirectoryInfo? current = null;
                try { current = new DirectoryInfo(appDir); } catch { }

                string? devRoot = null;
                while (current != null)
                {
                    bool hasSrc = Directory.Exists(Path.Combine(current.FullName, "src"));
                    bool hasBuild = Directory.Exists(Path.Combine(current.FullName, "dist"))
                                 || Directory.Exists(Path.Combine(current.FullName, "publish"));
                    if (hasSrc && hasBuild)
                    {
                        devRoot = Path.Combine(current.FullName, "backups");
                        break;
                    }
                    current = current.Parent;
                }

                // Fallback if project root not found — put backups next to exe
                candidate = devRoot ?? Path.Combine(appDir, "Backups");
            }
            else
            {
                // Installed / dist production mode (Canonical single root: %ProgramData%\ErrorOptimizer\backups)
                candidate = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ErrorOptimizer", "backups");
            }

            // Verify if candidate can be accessed AND written to at runtime
            if (appDirOverride == null && modeOverride == null)
            {
                if (!TryProbeWritableDirectory(candidate))
                {
                    // Fallback in order:
                    // 1. %LOCALAPPDATA%\ErrorOptimizer\backups\
                    // 2. <AppDirectory>\Backups\
                    // 3. %TEMP%\ErrorOptimizer_Backups\
                    string fb1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer", "backups");
                    string fb2 = Path.Combine(AppDirectory, "Backups");
                    string fb3 = Path.Combine(Path.GetTempPath(), "ErrorOptimizer_Backups");

                    string[] fallbacks = { fb1, fb2, fb3 };
                    string? chosen = null;

                    foreach (var fb in fallbacks)
                    {
                        if (TryProbeWritableDirectory(fb))
                        {
                            chosen = fb;
                            break;
                        }
                    }

                    chosen ??= fb3;
                    BackupRootFallbackReason = $"Primary backup root '{candidate}' is not writable; fell back to '{chosen}'";
                    return chosen;
                }
            }

            return candidate;
        }
    }
}
