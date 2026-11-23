using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiosOptimizer.Core.Implementations;
using BiosOptimizer.Core.V4.Safety;
using Microsoft.Win32;

namespace ErrorOptimizerPrototypeHost.Services;

/// <summary>
/// Real Windows workload discovery, safe launcher, process monitor, affinity/priority enforcer,
/// and rollback controller backing <c>workload:*</c> WebView2 commands.
/// </summary>
public sealed class WorkloadMonitorBridge : IDisposable
{
    private const int SW_RESTORE = 9;

    private static readonly HashSet<string> CriticalExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dwm.exe", "csrss.exe", "svchost.exe", "winlogon.exe", "lsass.exe",
        "services.exe", "smss.exe", "wininit.exe", "system", "registry", "audiodg.exe", "fontdrvhost.exe"
    };

    private sealed record SavedProcessState(
        int Pid,
        ProcessPriorityClass OriginalPriority,
        IntPtr OriginalAffinity);

    private readonly Window _ownerWindow;
    private readonly Action<string> _postJson;
    private readonly WorkloadOptimizationEngine _engine = WorkloadOptimizationEngine.Instance;

    private readonly ConcurrentDictionary<int, (string Exe, string Path, string Origin)> _runningTracked = new();
    private readonly ConcurrentDictionary<int, string> _eoLaunchedPids = new();
    private readonly ConcurrentDictionary<int, SavedProcessState> _savedStates = new();
    private readonly object _monitorLock = new();

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _lastFgPid;
    private bool _disposed;

    public WorkloadMonitorBridge(Window ownerWindow, Action<string> postJson)
    {
        _ownerWindow = ownerWindow;
        _postJson = postJson;
    }

    public Task<List<object>> DiscoverInstalledAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _engine.BuildInstalledInventory();
            var list = new List<object>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _engine.InstalledInventory)
            {
                string fullPath = item.InstallationPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                    continue;
                if (!seenPaths.Add(fullPath))
                    continue;

                string exe = EnsureExeSuffix(Path.GetFileName(fullPath));
                string source = item.DetectionSource?.Contains("Registry", StringComparison.OrdinalIgnoreCase) == true
                    ? "REGISTRY"
                    : (item.IsCustomPath ? "CUSTOM" : "KNOWN_PATH");

                list.Add(new
                {
                    id = item.CanonicalAppId ?? item.Id,
                    name = item.DisplayName,
                    exe,
                    path = fullPath,
                    category = MapCategory(item.Type),
                    source
                });
            }

            // Augment with running workload sessions (games/creative/video AI/emulators) discovered live.
            _engine.DetectAndProcessWorkload();
            foreach (var session in _engine.ActiveSessions)
            {
                string fullPath = session.ExecutablePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                    continue;
                if (!seenPaths.Add(fullPath))
                    continue;

                string exe = EnsureExeSuffix(Path.GetFileName(fullPath));
                if (CriticalExes.Contains(exe))
                    continue;

                list.Add(new
                {
                    id = !string.IsNullOrWhiteSpace(session.ApplicationId) ? session.ApplicationId : exe.ToLowerInvariant(),
                    name = string.IsNullOrWhiteSpace(session.DisplayName) ? exe : session.DisplayName,
                    exe,
                    path = fullPath,
                    category = MapCategory(session.Type),
                    source = "RUNNING_PROCESS"
                });
            }

            return list;
        }, ct);
    }

    public async Task<string?> PickExecutableAsync()
    {
        return await _ownerWindow.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Game, Creative Tool, or Emulator Executable",
                Filter = "Executable files (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = false
            };

            return dlg.ShowDialog(_ownerWindow) == true ? dlg.FileName : null;
        });
    }

    public Task<(bool Ok, int Pid, string? Error)> LaunchAsync(string? path)
    {
        return Task.Run<(bool, int, string?)>(() =>
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (false, 0, $"Executable path not found: '{path}'");

            string exe = Path.GetFileName(path);
            if (CriticalExes.Contains(exe) || ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(exe, path).IsProtected)
                return (false, 0, $"Blocked by safety policy: '{exe}' cannot be launched as a managed workload.");

            try
            {
                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return (false, 0, "Operating system did not create a process handle.");

                int pid = proc.Id;
                _eoLaunchedPids[pid] = path;
                EmitProcessEvent(new
                {
                    type = "process-start",
                    pid,
                    exe = exe.ToLowerInvariant(),
                    path,
                    origin = "EO_LAUNCH",
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                return (true, pid, null);
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        });
    }

    public Task<(bool Ok, List<string> Applied, string? Error)> ApplyPlanAsync(int pid, JsonElement planEl)
    {
        return Task.Run<(bool, List<string>, string?)>(() =>
        {
            var applied = new List<string>();
            if (pid <= 4 || pid == Environment.ProcessId)
                return (false, applied, "Cannot modify system or self process.");

            try
            {
                using var proc = Process.GetProcessById(pid);
                string exe = EnsureExeSuffix(proc.ProcessName);
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }

                if (CriticalExes.Contains(exe) || ProtectedApplicationPolicy.Instance.EvaluateProcessIdentity(exe, path).IsProtected)
                    return (false, applied, $"Process '{exe}' is protected by safety policy.");

                _savedStates.TryAdd(pid, new SavedProcessState(pid, proc.PriorityClass, proc.ProcessorAffinity));

                if (planEl.ValueKind == JsonValueKind.Object)
                {
                    if (planEl.TryGetProperty("priority", out var prioEl) && prioEl.ValueKind == JsonValueKind.String)
                    {
                        ProcessPriorityClass targetPrio = (prioEl.GetString() ?? "NORMAL").ToUpperInvariant() switch
                        {
                            "HIGH" => ProcessPriorityClass.High,
                            "ABOVE_NORMAL" => ProcessPriorityClass.AboveNormal,
                            _ => ProcessPriorityClass.Normal
                        };

                        if (proc.PriorityClass != targetPrio)
                            proc.PriorityClass = targetPrio;
                        applied.Add($"CPU Priority ({targetPrio})");
                    }

                    if (planEl.TryGetProperty("affinity", out var affEl) && affEl.ValueKind == JsonValueKind.Array)
                    {
                        long mask = 0;
                        int maxBits = Math.Min(63, Environment.ProcessorCount);
                        foreach (var bit in affEl.EnumerateArray())
                        {
                            if (bit.TryGetInt32(out int coreIdx) && coreIdx >= 0 && coreIdx < maxBits)
                                mask |= 1L << coreIdx;
                        }

                        if (mask != 0)
                        {
                            try
                            {
                                proc.ProcessorAffinity = new IntPtr(mask);
                                applied.Add($"Exclusive Core Affinity (0x{mask:X})");
                            }
                            catch (Win32Exception)
                            {
                                // Preserve partial success when affinity modification is restricted by ACL/hybrid scheduler.
                            }
                        }
                    }
                }

                return (applied.Count > 0, applied, applied.Count > 0 ? null : "No supported workload adjustments were applied.");
            }
            catch (ArgumentException)
            {
                return (false, applied, $"Process PID {pid} exited before optimization.");
            }
            catch (Win32Exception ex)
            {
                return (false, applied, ex.Message);
            }
            catch (Exception ex)
            {
                return (false, applied, ex.Message);
            }
        });
    }

    public Task<(bool Ok, string? Error)> FocusProcessAsync(int pid)
    {
        return Task.Run<(bool, string?)>(() =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                IntPtr hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    return (false, "Target process currently has no focusable window handle.");

                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                bool ok = SetForegroundWindow(hwnd);
                return ok ? (true, null) : (false, "Windows foreground lock prevented focus transfer.");
            }
            catch (ArgumentException)
            {
                return (false, $"Process PID {pid} is no longer running.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    public Task<(bool Ok, string? Error)> RevertProcessAsync(int pid)
    {
        return Task.Run<(bool, string?)>(() =>
        {
            if (!_savedStates.TryRemove(pid, out var saved))
                return (true, null);

            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.PriorityClass = saved.OriginalPriority;
                    if (saved.OriginalAffinity != IntPtr.Zero)
                    {
                        try { proc.ProcessorAffinity = saved.OriginalAffinity; } catch { }
                    }
                }
                return (true, null);
            }
            catch (ArgumentException)
            {
                return (true, null); // Exited already
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    public void StartMonitor()
    {
        if (_disposed) return;

        lock (_monitorLock)
        {
            if (_monitorTask != null && !_monitorTask.IsCompleted)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var kvp in _runningTracked)
                {
                    EmitProcessEvent(new
                    {
                        type = "process-start",
                        pid = kvp.Key,
                        exe = kvp.Value.Exe,
                        path = kvp.Value.Path,
                        origin = kvp.Value.Origin,
                        at = now
                    });
                }
                if (_lastFgPid > 0)
                    EmitProcessEvent(new { type = "foreground-change", pid = _lastFgPid, at = now });
                return;
            }

            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();
            CancellationToken ct = _monitorCts.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(ct), ct);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                PollProcessesOnce(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }

            try
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void PollProcessesOnce(CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int fgPid = (int)GetForegroundProcessId();
        var livePids = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (p.Id <= 4) continue;
                livePids.Add(p.Id);

                if (_runningTracked.ContainsKey(p.Id))
                    continue;

                string exe = EnsureExeSuffix(p.ProcessName).ToLowerInvariant();
                if (CriticalExes.Contains(exe))
                    continue;

                string path = string.Empty;
                try { path = p.MainModule?.FileName ?? string.Empty; } catch { }

                string origin = _eoLaunchedPids.ContainsKey(p.Id) ? "EO_LAUNCH" : "EXTERNAL";
                if (_runningTracked.TryAdd(p.Id, (exe, path, origin)))
                {
                    EmitProcessEvent(new
                    {
                        type = "process-start",
                        pid = p.Id,
                        exe,
                        path = string.IsNullOrEmpty(path) ? null : path,
                        origin,
                        at = now
                    });
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }

        foreach (int pid in _runningTracked.Keys.ToList())
        {
            if (!livePids.Contains(pid))
            {
                _runningTracked.TryRemove(pid, out _);
                _eoLaunchedPids.TryRemove(pid, out _);
                _savedStates.TryRemove(pid, out _);
                EmitProcessEvent(new { type = "process-exit", pid, at = now });
            }
        }

        if (fgPid > 0 && fgPid != _lastFgPid)
        {
            _lastFgPid = fgPid;
            EmitProcessEvent(new { type = "foreground-change", pid = fgPid, at = now });
        }
    }

    private void EmitProcessEvent(object evt)
    {
        if (_disposed) return;
        _postJson(JsonSerializer.Serialize(new { @event = evt }));
    }

    private static string EnsureExeSuffix(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}.exe";
    }

    private static string MapCategory(WorkloadType type) => type switch
    {
        WorkloadType.Emulator => "EMULATORS",
        WorkloadType.VideoAi => "VIDEO AI",
        WorkloadType.Creative or WorkloadType.Recording => "CREATIVE",
        _ => "GAMES"
    };

    /// <summary>
    /// Cancels in-process workload monitoring and reverts priority/affinity overrides applied to tracked PIDs.
    /// </summary>
    public Task<int> StopAndRevertAllAsync()
    {
        lock (_monitorLock)
        {
            try { _monitorCts?.Cancel(); } catch { }
            try { _monitorCts?.Dispose(); } catch { }
            _monitorCts = null;
            _monitorTask = null;
        }

        int reverted = 0;
        foreach (int pid in _savedStates.Keys.ToList())
        {
            if (_savedStates.TryRemove(pid, out var saved))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.PriorityClass = saved.OriginalPriority;
                    if (saved.OriginalAffinity != IntPtr.Zero)
                        p.ProcessorAffinity = saved.OriginalAffinity;
                    reverted++;
                }
                catch { }
            }
        }
        return Task.FromResult(reverted);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndRevertAllAsync().GetAwaiter().GetResult();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static uint GetForegroundProcessId()
    {
        IntPtr handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(handle, out uint pid);
        return pid;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
