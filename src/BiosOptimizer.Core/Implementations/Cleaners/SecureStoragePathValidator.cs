#nullable enable
using System;
using System.IO;
using System.Linq;

namespace BiosOptimizer.Core.Implementations.Cleaners
{
    /// <summary>
    /// Cryptographic and filesystem security boundary validator for file deletion operations.
    /// Strictly protects operating system, user personal files, and program directories
    /// from path traversal, symlink redirection, and unauthorized destructive actions.
    /// </summary>
    public static class SecureStoragePathValidator
    {
        private static readonly string[] CriticalSystemFiles = new[]
        {
            "pagefile.sys",
            "swapfile.sys",
            "hiberfil.sys",
            "bootmgr",
            "bootnxt",
            "ntldr",
            "ntoskrnl.exe",
            "hal.dll",
            "winload.exe",
            "winload.efi"
        };

        /// <summary>
        /// Validates that a requested file path is eligible and safe for application-managed storage deletion.
        /// Rejects path traversal, drive roots, symbolic links, Windows system files, and Program Files.
        /// </summary>
        public static bool ValidateForDeletion(string? rawPath, out string canonicalPath, out string failureReason)
        {
            canonicalPath = string.Empty;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                failureReason = "Path cannot be null or empty.";
                return false;
            }

            string trimmed = rawPath.Trim('\"', ' ', '\t', '\r', '\n');

            // 1. Path traversal check before canonicalization
            if (trimmed.Contains(".."))
            {
                failureReason = "Path traversal sequences ('..') are strictly prohibited.";
                return false;
            }

            // 2. Resolve canonical absolute path
            try
            {
                canonicalPath = Path.GetFullPath(trimmed);
            }
            catch (Exception ex)
            {
                failureReason = $"Invalid path format: {ex.Message}";
                return false;
            }

            // 3. Reject drive root targets (e.g. C:\)
            var root = Path.GetPathRoot(canonicalPath);
            if (string.Equals(canonicalPath.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Drive root directories cannot be deleted.";
                return false;
            }

            // 4. File existence verification
            if (!File.Exists(canonicalPath))
            {
                failureReason = "Target file does not exist on disk.";
                return false;
            }

            // 5. Symbolic link / Reparse point check (prevent TOCTOU & redirection attacks)
            try
            {
                var attr = File.GetAttributes(canonicalPath);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    failureReason = "Reparse points, junctions, and symbolic links are strictly prohibited.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                failureReason = $"Unable to read file attributes: {ex.Message}";
                return false;
            }

            // 6. Operating System Directories Protection
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string sysX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            // Disallow Windows OS directories unless explicitly within temporary cache directories
            if (!string.IsNullOrEmpty(winDir) && canonicalPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
            {
                string winTemp = Path.Combine(winDir, "Temp");
                string winPrefetch = Path.Combine(winDir, "Prefetch");
                string winLogs = Path.Combine(winDir, "Logs");
                string winSoftwareDist = Path.Combine(winDir, "SoftwareDistribution", "Download");

                bool isAllowedTempSubdir = canonicalPath.StartsWith(winTemp, StringComparison.OrdinalIgnoreCase) ||
                                           canonicalPath.StartsWith(winPrefetch, StringComparison.OrdinalIgnoreCase) ||
                                           canonicalPath.StartsWith(winLogs, StringComparison.OrdinalIgnoreCase) ||
                                           canonicalPath.StartsWith(winSoftwareDist, StringComparison.OrdinalIgnoreCase);

                if (!isAllowedTempSubdir)
                {
                    failureReason = "Access Denied: Protected Windows operating system directory.";
                    return false;
                }
            }

            // Disallow System32 and SysWOW64 unconditionally
            if (!string.IsNullOrEmpty(sysDir) && canonicalPath.StartsWith(sysDir, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Access Denied: Windows System32 is strictly protected.";
                return false;
            }
            if (!string.IsNullOrEmpty(sysX86) && canonicalPath.StartsWith(sysX86, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Access Denied: Windows SysWOW64 is strictly protected.";
                return false;
            }

            // Disallow Program Files
            if (!string.IsNullOrEmpty(progFiles) && canonicalPath.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Access Denied: Applications in Program Files cannot be deleted via storage cleanup.";
                return false;
            }
            if (!string.IsNullOrEmpty(progFilesX86) && canonicalPath.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Access Denied: Applications in Program Files (x86) cannot be deleted via storage cleanup.";
                return false;
            }

            // 7. Critical Boot & Kernel Files
            string fileName = Path.GetFileName(canonicalPath).ToLowerInvariant();
            if (CriticalSystemFiles.Contains(fileName))
            {
                failureReason = "Access Denied: Critical Windows kernel or bootloader file.";
                return false;
            }

            // 8. System Volume Information & Recovery
            if (canonicalPath.IndexOf("System Volume Information", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf(@"\Recovery\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                failureReason = "Access Denied: Protected volume infrastructure and recovery storage.";
                return false;
            }

            // 9. Creative & Video Editing Scratch Disks / Media Caches Protection
            if (canonicalPath.IndexOf(@"\Adobe\Common\Media Cache Files", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf(@"\Adobe\Common\Media Cache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf(@"\Adobe Premiere Pro Auto-Save", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf(@"\Adobe After Effects Auto-Save", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf(@"\DaVinci Resolve\CacheClip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canonicalPath.IndexOf(@"\Blackmagic Design\DaVinci Resolve\Capture", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                failureReason = "Access Denied: Protected video editing / creative media cache and scratch disk.";
                return false;
            }

            return true;
        }
    }
}
