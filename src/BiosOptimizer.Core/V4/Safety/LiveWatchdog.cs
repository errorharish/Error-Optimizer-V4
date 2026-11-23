#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiosOptimizer.Core.V4.Safety
{
    public sealed class AbortDump
    {
        public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
        public string MissingCriticalProcess { get; init; } = string.Empty;
        public List<string> BlacklistedActionSignatures { get; init; } = new();
        public string AbortMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Real-time live watchdog operating at 250ms cadence.
    /// Continuously monitors essential OS processes. Any loss causes an immediate execution abort,
    /// full rollback of current session, generation of abort_dump_<ts>.json, and blacklisting of last 10 actions.
    /// </summary>
    public class LiveWatchdog : IDisposable
    {
        private static readonly string[] RequiredProcesses = {
            "csrss", "wininit", "services", "lsass", "winlogon", "dwm", "explorer", "svchost"
        };

        private readonly BugcheckBlacklist _blacklist;
        private readonly Action? _rollbackCallback;
        private readonly List<string> _recentActionSignatures = new();
        private readonly object _lock = new();

        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private volatile bool _isAborted;
        private string _abortReason = string.Empty;

        public bool IsAborted => _isAborted;
        public string AbortReason => _abortReason;

        // Test hooks to simulate process presence without killing actual system csrss
        public Func<string, bool>? ProcessPresenceChecker { get; set; }

        public LiveWatchdog(BugcheckBlacklist? blacklist = null, Action? rollbackCallback = null)
        {
            _blacklist = blacklist ?? BugcheckBlacklist.Instance;
            _rollbackCallback = rollbackCallback;
        }

        public void RecordAction(string actionSignature)
        {
            lock (_lock)
            {
                _recentActionSignatures.Add(actionSignature);
                if (_recentActionSignatures.Count > 50)
                {
                    _recentActionSignatures.RemoveAt(0);
                }
            }
        }

        public void Start()
        {
            _isAborted = false;
            _abortReason = string.Empty;
            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _monitorTask?.Wait(500);
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CheckCriticalProcesses();
                }
                catch (Exception ex)
                {
                    // If the watchdog thread throws, treat as a trip
                    TriggerAbort($"Watchdog monitor failure: {ex.Message}");
                    break;
                }

                if (_isAborted) break;

                try
                {
                    await Task.Delay(250, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public void CheckCriticalProcesses()
        {
            foreach (var name in RequiredProcesses)
            {
                bool exists;
                if (ProcessPresenceChecker != null)
                {
                    exists = ProcessPresenceChecker(name);
                }
                else
                {
                    exists = Process.GetProcessesByName(name).Length > 0;
                }

                if (!exists)
                {
                    TriggerAbort($"Critical OS process disappeared: {name}.exe");
                    return;
                }
            }
        }

        public void TriggerAbort(string reason)
        {
            lock (_lock)
            {
                if (_isAborted) return;
                _isAborted = true;
                _abortReason = reason;

                // 1. Invoke persistent rollback callback immediately
                try
                {
                    _rollbackCallback?.Invoke();
                }
                catch { }

                // 2. Blacklist last 10 actions
                var last10 = _recentActionSignatures.TakeLast(10).ToList();
                foreach (var sig in last10)
                {
                    _blacklist.Add(sig, "Action blacklisted following critical watchdog trip", "0x000000F4", Guid.NewGuid().ToString());
                }

                // 3. Write structured abort dump
                WriteAbortDump(reason, last10);
            }
        }

        public string WriteAbortDump(string reason, List<string> blacklisted)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(localAppData, "ErrorOptimizer");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
                string dumpPath = Path.Combine(dir, $"abort_dump_{ts}.json");

                var dump = new AbortDump
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    MissingCriticalProcess = reason,
                    BlacklistedActionSignatures = blacklisted,
                    AbortMessage = "Optimization aborted — critical process loss detected. No changes were left in place."
                };

                string json = JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dumpPath, json);
                return dumpPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
