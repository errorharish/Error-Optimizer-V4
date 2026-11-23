#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.Interfaces;

namespace BiosOptimizer.Core.Implementations.Memory
{
    public class ExternalEmptyStandbyListProvider : IStandbyCleanupProvider
    {
        private static readonly Lazy<ExternalEmptyStandbyListProvider> _instance = new(() => new ExternalEmptyStandbyListProvider());
        public static ExternalEmptyStandbyListProvider Instance => _instance.Value;

        private const string KnownExeName = "EmptyStandbyList.exe";
        private const string TrustedSha256 = "6d2b18f8a8ba787d3fa4c6e36ed6c7af66b10083ce555a21ec24b2ada3821cbe";

        public bool IsAvailable => ValidateExecutable(out _) || WindowsMemoryListProvider.Instance.IsSupported;

        public string ResolvedExecutablePath => FindExecutable();

        public string FindExecutable()
        {
            // 1. Strictly check Application Base Directory (pinned, non-arbitrary path)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string pathInBase = Path.Combine(baseDir, KnownExeName);
            if (File.Exists(pathInBase)) return Path.GetFullPath(pathInBase);

            // 2. Check core/ subfolder in application base
            string coreDir = Path.Combine(baseDir, "core", KnownExeName);
            if (File.Exists(coreDir)) return Path.GetFullPath(coreDir);

            // Never search current working directory or environment PATH!
            return string.Empty;
        }

        public bool ValidateExecutable(out string reason)
        {
            string path = FindExecutable();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                reason = "EmptyStandbyList.exe not found in application directory.";
                return false;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length < 1000)
                {
                    reason = $"EmptyStandbyList.exe file size ({fileInfo.Length} bytes) is invalid or corrupted.";
                    return false;
                }

                // Verify cryptographic SHA-256 hash to prevent malicious binary substitution
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                byte[] hashBytes = sha.ComputeHash(fs);
                string hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                if (!hashHex.Equals(TrustedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"EmptyStandbyList.exe hash mismatch: expected {TrustedSha256}, actual {hashHex}. Execution blocked.";
                    return false;
                }

                reason = "Executable cryptographically validated and ready.";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Validation error: {ex.Message}";
                return false;
            }
        }

        public async Task<StandbyCleanupExecutionResult> ExecuteAsync(bool lowPriorityOnly = false, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            int callerThreadId = Environment.CurrentManagedThreadId;
            bool isStaThread = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

            LogStage("START_EMPTY_STANDBY", $"CallerThread={callerThreadId}, IsSta={isStaThread}, LowPriOnly={lowPriorityOnly}");

            var result = new StandbyCleanupExecutionResult();

            // PREFER NATIVE KERNEL MEMORY MANAGEMENT PATH
            // Native kernel call (NtSetSystemInformation) is in-process, faster, and eliminates external process execution risks
            if (WindowsMemoryListProvider.Instance.IsSupported)
            {
                LogStage("NATIVE_KERNEL_PURGE", "Invoking native Windows NT memory list management...");
                var beforeSnap = WindowsMemoryListProvider.Instance.GetCurrentSnapshot();
                result.Before = beforeSnap;

                var nativeRes = await WindowsMemoryListProvider.Instance.ExecuteStandbyPurgeAsync(lowPriorityOnly, cancellationToken);
                result.After = nativeRes.After;
                result.Duration = sw.Elapsed;

                if (nativeRes.Succeeded)
                {
                    result.Status = StandbyCleanupStatus.SuccessVerified;
                    result.SummaryMessage = $"STANDBY CACHE RECLAIMED (NT Kernel Native): {nativeRes.After.AvailableFormatted} available ({nativeRes.After.UsedPercentage:F0}% load)";
                    LogStage("FINAL_STATE", $"Status=SUCCESS (Native NT Kernel), Duration={result.Duration.TotalMilliseconds:F1}ms");
                    return result;
                }
            }

            // FALLBACK TO CRYPTOGRAPHICALLY VALIDATED EXTERNAL BINARY
            string exePath = FindExecutable();
            result.ExecutablePath = exePath;

            string valReason = string.Empty;
            bool isValid = !string.IsNullOrEmpty(exePath) && ValidateExecutable(out valReason);
            if (!isValid)
            {
                result.Status = StandbyCleanupStatus.NotFound;
                result.ErrorDetails = $"EmptyStandbyList.exe unavailable: {(string.IsNullOrEmpty(valReason) ? "Binary not found in application path" : valReason)}";
                result.SummaryMessage = "Standby list cleanup unavailable (Native NT privileges not elevated and verified binary missing).";
                LogStage("FINAL_STATE", $"Status=NOT_FOUND, Reason={result.ErrorDetails}");
                return result;
            }

            LogStage("STATE_READ_STARTED", "Capturing before-cleanup memory snapshot...");
            var before = WindowsMemoryListProvider.Instance.GetCurrentSnapshot();
            result.Before = before;
            LogStage("STATE_READ_COMPLETED", $"Before: Standby={before.StandbyFormatted}, LowPri={before.LowPriorityStandbyFormatted}, Available={before.AvailableFormatted}");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            string args = lowPriorityOnly ? "priority0standbylist" : "standbylist";
            LogStage("PROCESS_LAUNCH_STARTED", $"Exe='{exePath}', Args='{args}'");

            var execRes = await TaskExecutionSupervisor.ExecuteProcessAsync(
                exePath,
                args,
                TimeSpan.FromSeconds(5),
                linkedCts.Token);

            result.StandardOutput = execRes.StandardOutput;
            result.StandardError = execRes.StandardError;
            result.ExitCode = execRes.ExitCode;
            result.Duration = sw.Elapsed;

            LogStage("PROCESS_EXITED", $"ExitCode={execRes.ExitCode}, State={execRes.State}, Duration={result.Duration.TotalMilliseconds:F1}ms");

            var after = WindowsMemoryListProvider.Instance.GetCurrentSnapshot();
            result.After = after;
            LogStage("STATE_READ_COMPLETED", $"After: Standby={after.StandbyFormatted}, LowPri={after.LowPriorityStandbyFormatted}, Available={after.AvailableFormatted}");

            if (execRes.State == ExecutionTerminalState.Completed && execRes.ExitCode == 0)
            {
                result.Status = StandbyCleanupStatus.SuccessVerified;
                result.SummaryMessage = $"STANDBY CACHE RECLAIMED: {after.AvailableFormatted} available ({after.UsedPercentage:F0}% load)";
            }
            else
            {
                result.Status = StandbyCleanupStatus.VerificationFailed;
                result.ErrorDetails = execRes.StandardError;
                result.SummaryMessage = "Standby cache purge encountered an execution fault.";
            }

            LogStage("FINAL_STATE", $"Status={result.Status}, Duration={result.Duration.TotalMilliseconds:F1}ms");
            return result;
        }

        private static void LogStage(string stage, string detail)
        {
            System.Diagnostics.Debug.WriteLine($"[STANDBY_CLEANUP] [{stage}] {detail}");
        }
    }
}
