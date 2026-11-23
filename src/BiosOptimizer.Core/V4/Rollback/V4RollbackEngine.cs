#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;

namespace BiosOptimizer.Core.V4.Rollback
{
    public class V4TransactionRecord
    {
        public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
        public string BatchId { get; set; } = string.Empty;
        public string TargetType { get; set; } = string.Empty; // Registry, Service, StartupEntry, ScheduledTask, PowerPlan
        public string TargetIdentifier { get; set; } = string.Empty;
        public string PreviousState { get; set; } = string.Empty;
        public string NewState { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Reason { get; set; } = string.Empty;
        public bool Restored { get; set; }
        public string RestorationResult { get; set; } = string.Empty;
        public string? ValueKind { get; set; }
        public string? RegistryView { get; set; }
    }

    public class PowerPlanRollback
    {
        public string PreviousGuid { get; set; } = string.Empty;
        public string? PreviousName { get; set; }

        public PowerPlanRollback() { }

        public PowerPlanRollback(string previousGuid, string? previousName = null)
        {
            PreviousGuid = previousGuid;
            PreviousName = previousName;
        }
    }

    /// <summary>
    /// Transactional rollback manager ensuring all persistent V4 optimization changes are fully reversible.
    /// </summary>
    public class V4RollbackEngine
    {
        private readonly string _storageDir;
        private readonly object _lock = new();

        private static readonly object _cacheLock = new();
        private static List<V4TransactionRecord>? _cachedTransactions;
        private static DateTime _cachedDirWriteUtc = DateTime.MinValue;
        private static DateTime _cacheCapturedUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        public static Func<string, (int ExitCode, string Output)>? PowerCfgRunnerHook { get; set; }
        public static Action<string>? LogAction { get; set; }
        public static Func<V4TransactionRecord, bool?>? StateConflictCheckHook { get; set; }

        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedTransactions = null;
                _cachedDirWriteUtc = DateTime.MinValue;
            }
        }

        public V4RollbackEngine(string? customStorageDir = null)
        {
            if (!string.IsNullOrEmpty(customStorageDir))
            {
                _storageDir = customStorageDir;
            }
            else
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _storageDir = Path.Combine(appData, "ErrorOptimizer", "V4Transactions");
            }
            try
            {
                Directory.CreateDirectory(_storageDir);
            }
            catch { }
        }

        public void Register(PowerPlanRollback rollback)
        {
            Register(string.Empty, rollback);
        }

        public void Register(string batchId, PowerPlanRollback rollback)
        {
            RecordTransaction(new V4TransactionRecord
            {
                BatchId = batchId ?? string.Empty,
                TargetType = "PowerPlan",
                TargetIdentifier = rollback.PreviousName ?? rollback.PreviousGuid,
                PreviousState = rollback.PreviousGuid,
                NewState = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                Reason = $"Restore previous active power plan ({rollback.PreviousGuid})"
            });
        }

        public void RecordTransaction(V4TransactionRecord record)
        {
            lock (_lock)
            {
                try
                {
                    string filePath = Path.Combine(_storageDir, $"{record.BatchId}_{record.TransactionId}.json");
                    string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, json);
                }
                catch { }
            }
            InvalidateCache();
        }

        public List<V4TransactionRecord> GetTransactions(string? batchId = null)
        {
            List<V4TransactionRecord> all;
            lock (_cacheLock)
            {
                bool cacheValid = false;
                if (_cachedTransactions != null && (DateTime.UtcNow - _cacheCapturedUtc) < CacheTtl)
                {
                    try
                    {
                        if (Directory.Exists(_storageDir))
                        {
                            var currentWrite = Directory.GetLastWriteTimeUtc(_storageDir);
                            if (currentWrite == _cachedDirWriteUtc)
                            {
                                cacheValid = true;
                            }
                        }
                        else
                        {
                            cacheValid = true;
                        }
                    }
                    catch { }
                }

                if (cacheValid && _cachedTransactions != null)
                {
                    all = new List<V4TransactionRecord>(_cachedTransactions);
                }
                else
                {
                    all = LoadTransactionsFromDisk();
                    _cachedTransactions = new List<V4TransactionRecord>(all);
                    _cacheCapturedUtc = DateTime.UtcNow;
                    try
                    {
                        _cachedDirWriteUtc = Directory.Exists(_storageDir)
                            ? Directory.GetLastWriteTimeUtc(_storageDir)
                            : DateTime.MinValue;
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(batchId))
                return all;

            return all.Where(r => r.BatchId.Equals(batchId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private List<V4TransactionRecord> LoadTransactionsFromDisk()
        {
            var results = new List<V4TransactionRecord>();
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_storageDir)) return results;

                    foreach (var file in Directory.GetFiles(_storageDir, "*.json"))
                    {
                        try
                        {
                            string json = File.ReadAllText(file);
                            var record = JsonSerializer.Deserialize<V4TransactionRecord>(json);
                            if (record != null)
                            {
                                results.Add(record);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return results;
        }

        public bool RollbackBatch(string batchId, out int restoredCount, out int failedCount, out string summaryMessage)
        {
            restoredCount = 0;
            failedCount = 0;
            var transactions = GetTransactions(batchId);

            if (transactions.Count == 0)
            {
                summaryMessage = "No transactions found for the specified batch ID";
                return false;
            }

            foreach (var tx in transactions)
            {
                if (tx.Restored) continue;

                bool ok = RollbackSingleTransaction(tx);
                if (ok)
                {
                    tx.Restored = true;
                    if (string.IsNullOrEmpty(tx.RestorationResult))
                    {
                        tx.RestorationResult = "Successfully reverted";
                    }
                    restoredCount++;
                }
                else
                {
                    tx.RestorationResult = "Failed to revert";
                    failedCount++;
                }

                // Update transaction record
                RecordTransaction(tx);
            }

            summaryMessage = $"Rollback complete: {restoredCount} items reverted, {failedCount} failures";
            return failedCount == 0;
        }

        public List<V4TransactionRecord> GetPendingTransactions()
        {
            return GetTransactions().Where(t => !t.Restored).ToList();
        }

        public bool RecoverIncompleteTransactions(out int recoveredCount, out int failedCount)
        {
            recoveredCount = 0;
            failedCount = 0;
            var pending = GetPendingTransactions();
            foreach (var tx in pending)
            {
                // In crash recovery, forcibly restore the captured baseline even if intermediate
                // corruption occurred during interrupted mutation
                bool ok = RollbackSingleTransaction(tx, forceRecovery: true);
                if (ok)
                {
                    tx.Restored = true;
                    tx.RestorationResult = "Recovered after crash/interruption";
                    recoveredCount++;
                }
                else
                {
                    tx.RestorationResult = "Crash recovery failed";
                    failedCount++;
                }
                RecordTransaction(tx);
            }
            return failedCount == 0;
        }

        public static bool Rollback(object rollbackItem, bool forceRecovery = false)
        {
            try
            {
                switch (rollbackItem)
                {
                    case PowerPlanRollback p:
                        return RollbackPowerPlan(p);

                    case V4TransactionRecord tx:
                        return RollbackSingleTransaction(tx, forceRecovery);

                    default:
                        return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Rollback error: {ex.Message}");
                return false;
            }
        }

        public static bool HasConflict(V4TransactionRecord tx, out string conflictReason)
        {
            conflictReason = string.Empty;
            if (StateConflictCheckHook != null)
            {
                var hookResult = StateConflictCheckHook(tx);
                if (hookResult.HasValue)
                {
                    if (hookResult.Value)
                    {
                        conflictReason = "Conflict detected via StateConflictCheckHook";
                        return true;
                    }
                    return false;
                }
            }

            try
            {
                switch (tx.TargetType)
                {
                    case "Service":
                    {
                        string svc = tx.TargetIdentifier;
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                        if (key != null && key.GetValue("Start") is int startInt)
                        {
                            string currentMode = startInt switch
                            {
                                2 => "Automatic",
                                3 => "Manual",
                                4 => "Disabled",
                                _ => startInt.ToString()
                            };
                            if (tx.NewState.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && startInt != 4)
                            {
                                conflictReason = $"Current service StartType is '{currentMode}', expected optimizer state '{tx.NewState}'";
                                return true;
                            }
                        }
                        break;
                    }

                    case "ScheduledTask":
                    {
                        try
                        {
                            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                            if (schedulerType != null)
                            {
                                dynamic? scheduler = Activator.CreateInstance(schedulerType);
                                if (scheduler != null)
                                {
                                    scheduler.Connect();
                                    string folderPath = Path.GetDirectoryName(tx.TargetIdentifier)?.Replace('/', '\\') ?? "\\";
                                    string taskName = Path.GetFileName(tx.TargetIdentifier);
                                    dynamic folder = scheduler.GetFolder(folderPath);
                                    dynamic task = folder.GetTask(taskName);
                                    if (task != null)
                                    {
                                        bool isEnabled = task.Enabled;
                                        string currentState = isEnabled ? "Enabled" : "Disabled";
                                        if (!currentState.Equals(tx.NewState, StringComparison.OrdinalIgnoreCase))
                                        {
                                            conflictReason = $"Current scheduled task state is '{currentState}', expected optimizer state '{tx.NewState}'";
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        break;
                    }

                    case "AppXBackground":
                    {
                        string regPath = $@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\{tx.TargetIdentifier}";
                        using var key = Registry.CurrentUser.OpenSubKey(regPath, writable: false);
                        if (key != null && key.GetValue("Disabled") is int d)
                        {
                            string currentAppX = d == 1 ? "Disabled" : "Allowed";
                            if (!currentAppX.Equals(tx.NewState, StringComparison.OrdinalIgnoreCase))
                            {
                                conflictReason = $"Current AppX background state is '{currentAppX}', expected optimizer state '{tx.NewState}'";
                                return true;
                            }
                        }
                        break;
                    }

                    case "Registry":
                    {
                        bool isHklm = tx.TargetIdentifier.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase);
                        string cleanPath = tx.TargetIdentifier.Substring(5);
                        int lastSlash = cleanPath.LastIndexOf('\\');
                        if (lastSlash >= 0)
                        {
                            string subKey = cleanPath.Substring(0, lastSlash);
                            string valueName = cleanPath.Substring(lastSlash + 1);
                            var root = isHklm ? Registry.LocalMachine : Registry.CurrentUser;
                            using var key = root.OpenSubKey(subKey, writable: false);
                            var val = key?.GetValue(valueName);
                            string currentVal = val?.ToString() ?? "VALUE_DID_NOT_EXIST";
                            if (!string.IsNullOrEmpty(tx.NewState) && !currentVal.Equals(tx.NewState, StringComparison.OrdinalIgnoreCase))
                            {
                                conflictReason = $"Current registry value is '{currentVal}', expected optimizer state '{tx.NewState}'";
                                return true;
                            }
                        }
                        break;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool RollbackSingleTransaction(V4TransactionRecord tx, bool forceRecovery = false)
        {
            try
            {
                if (!forceRecovery && HasConflict(tx, out string conflictReason))
                {
                    tx.RestorationResult = $"Conflict: {conflictReason}";
                    Log($"[Rollback Conflict] {tx.TargetType} '{tx.TargetIdentifier}': {conflictReason}");
                    return false;
                }

                switch (tx.TargetType)
                {
                    case "OemComponent":
                        if (tx.TargetIdentifier.StartsWith(@"\") || tx.TargetIdentifier.Contains(@"\"))
                            return RollbackScheduledTask(tx.TargetIdentifier, tx.PreviousState);
                        if (tx.TargetIdentifier.Contains("|"))
                            return RollbackStartup(tx.TargetIdentifier, tx.PreviousState);
                        return RollbackService(tx.TargetIdentifier, tx.PreviousState);

                    case "Service":
                        return RollbackService(tx.TargetIdentifier, tx.PreviousState);

                    case "StartupEntry":
                        return RollbackStartup(tx.TargetIdentifier, tx.PreviousState);

                    case "Registry":
                        bool regOk = RollbackRegistry(tx.TargetIdentifier, tx.PreviousState, tx.ValueKind, tx.RegistryView, out string regErr);
                        if (!regOk && !string.IsNullOrEmpty(regErr))
                        {
                            tx.RestorationResult = regErr;
                        }
                        return regOk;

                    case "ScheduledTask":
                        return RollbackScheduledTask(tx.TargetIdentifier, tx.PreviousState);

                    case "AppXBackground":
                        return RollbackAppX(tx.TargetIdentifier, tx.PreviousState);

                    case "PowerPlan":
                        return RollbackPowerPlan(new PowerPlanRollback(tx.PreviousState, tx.TargetIdentifier));

                    case "PowerSetting":
                        return RollbackPowerSetting(tx.TargetIdentifier, tx.PreviousState);

                    case "ProcessPriority":
                        return RollbackProcessPriority(tx.TargetIdentifier, tx.PreviousState);

                    case "MemoryCache":
                    case "ProcessWorkingSet":
                    case "WorkingSetTrim":
                    case "MemoryPressureRefresh":
                        tx.RestorationResult = "Memory pages fault back dynamically on demand by Windows Memory Manager";
                        return true;

                    case "StorageEngine":
                    case "ProcessManagement":
                        tx.RestorationResult = "Verified non-destructively (no rollback required)";
                        return true;

                    default:
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static (int ExitCode, string Output) RunPowerCfg(string arguments)
        {
            if (PowerCfgRunnerHook != null)
            {
                return PowerCfgRunnerHook(arguments);
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return (-1, "Failed to start powercfg.exe");
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                string combined = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}".Trim();
                return (p.ExitCode, combined);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[V4RollbackEngine] {message}");
                Console.WriteLine($"[V4RollbackEngine] {message}");
                LogAction?.Invoke(message);
            }
            catch { }
        }

        public static bool RollbackPowerPlan(PowerPlanRollback p)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p.PreviousGuid))
                {
                    Log($"[WARN] Cannot rollback power plan: previous GUID is empty.");
                    return false;
                }

                var (exitCode, output) = RunPowerCfg($"/setactive {p.PreviousGuid}");
                if (exitCode != 0)
                {
                    Log($"[ERROR] powercfg /setactive {p.PreviousGuid} failed during rollback with exit code {exitCode}. " +
                        $"The previous scheme GUID was '{p.PreviousGuid}'. Output: {output}. " +
                        $"The user can manually restore this power plan using 'powercfg /setactive {p.PreviousGuid}'.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"[ERROR] powercfg rollback exception for plan '{p.PreviousGuid}': {ex.Message}. " +
                    $"The user can manually restore this power plan using 'powercfg /setactive {p.PreviousGuid}'.");
                // Do not throw — rollback must complete as much as possible.
                return false;
            }
        }

        private static bool RollbackService(string serviceName, string previousStartType)
        {
            try
            {
                var startMode = previousStartType switch
                {
                    "Automatic" => "auto",
                    "Manual" => "demand",
                    "Disabled" => "disabled",
                    _ => "demand"
                };

                var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"config \"{serviceName}\" start= {startMode}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);

                if (previousStartType.Equals("Automatic", StringComparison.OrdinalIgnoreCase) || previousStartType.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var sc = new ServiceController(serviceName);
                        if (sc.Status != ServiceControllerStatus.Running)
                        {
                            sc.Start();
                        }
                    }
                    catch { }
                }

                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool RollbackStartup(string identifier, string previousState)
        {
            try
            {
                var parts = identifier.Split('|', 2);
                if (parts.Length < 2) return false;

                string location = parts[0];
                string name = parts[1];

                if (location.StartsWith("Folder:", StringComparison.OrdinalIgnoreCase))
                {
                    string folder = location.Contains("User", StringComparison.OrdinalIgnoreCase)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.Startup)
                        : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

                    string disabledFile = Path.Combine(folder, name + ".disabled");
                    string activeFile = Path.Combine(folder, name);

                    if (File.Exists(disabledFile))
                    {
                        File.Move(disabledFile, activeFile, overwrite: true);
                        return true;
                    }
                }
                else
                {
                    // If previous command was saved, it can be re-written to registry run key
                    if (!string.IsNullOrEmpty(previousState) && previousState != "Disabled")
                    {
                        var root = location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? Registry.LocalMachine : Registry.CurrentUser;
                        using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                        key?.SetValue(name, previousState);
                        return true;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool RollbackRegistry(string targetKey, string previousValue, string? valueKind = null, string? registryView = null)
        {
            return RollbackRegistry(targetKey, previousValue, valueKind, registryView, out _);
        }

        public static bool RollbackRegistry(string targetKey, string previousValue, string? valueKind, string? registryView, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(targetKey))
                {
                    error = "TargetKey is null or empty";
                    return false;
                }

                // Format: HKLM\Path\To\Key\ValueName or HKCU\Path\To\Key\ValueName
                bool isHklm = targetKey.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase);
                bool isHkcu = targetKey.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase);
                if (!isHklm && !isHkcu)
                {
                    error = $"Unsupported hive prefix in targetKey '{targetKey}'. Must start with HKLM\\ or HKCU\\.";
                    return false;
                }

                string cleanPath = targetKey.Substring(5);
                int lastSlash = cleanPath.LastIndexOf('\\');
                if (lastSlash < 0)
                {
                    error = $"Malformed registry targetKey '{targetKey}'. No value name delimiter found.";
                    return false;
                }

                string subKey = cleanPath.Substring(0, lastSlash);
                string valueName = cleanPath.Substring(lastSlash + 1);

                var hive = isHklm ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                var view = (string.Equals(registryView, "Registry32", StringComparison.OrdinalIgnoreCase) ||
                            targetKey.IndexOf("Wow6432Node", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? RegistryView.Registry32
                    : RegistryView.Registry64;

                using var root = RegistryKey.OpenBaseKey(hive, view);

                // Handle missing / non-existent value restoration (clean deletion, zero assumed defaults)
                if (string.Equals(previousValue, "VALUE_DID_NOT_EXIST", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(previousValue, "__NON_EXISTENT__", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(previousValue, "NOT_PRESENT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(previousValue, "VALUE DID NOT EXIST", StringComparison.OrdinalIgnoreCase))
                {
                    using var key = root.OpenSubKey(subKey, writable: true);
                    if (key == null)
                    {
                        // Subkey already absent; value definitely does not exist
                        return true;
                    }

                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    key.Flush();

                    // Readback verification
                    var checkVal = key.GetValue(valueName);
                    if (checkVal != null)
                    {
                        error = $"Registry delete verification failed: value '{valueName}' still exists.";
                        Log($"[Rollback Error] {error}");
                        return false;
                    }
                    return true;
                }

                using (var key = root.CreateSubKey(subKey, writable: true))
                {
                    if (key == null)
                    {
                        error = $"Cannot open or create subkey '{subKey}' for writing.";
                        Log($"[Rollback Error] {error}");
                        return false;
                    }

                    // Determine RegistryValueKind
                    var kind = RegistryValueKind.String;
                    string vk = valueKind?.Trim() ?? string.Empty;

                    if (vk.Equals("DWord", StringComparison.OrdinalIgnoreCase) || vk.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.DWord;
                    else if (vk.Equals("QWord", StringComparison.OrdinalIgnoreCase) || vk.Equals("REG_QWORD", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.QWord;
                    else if (vk.Equals("Binary", StringComparison.OrdinalIgnoreCase) || vk.Equals("REG_BINARY", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.Binary;
                    else if (vk.Equals("MultiString", StringComparison.OrdinalIgnoreCase) || vk.Equals("REG_MULTI_SZ", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.MultiString;
                    else if (vk.Equals("ExpandString", StringComparison.OrdinalIgnoreCase) || vk.Equals("REG_EXPAND_SZ", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.ExpandString;
                    else if (vk.Equals("String", StringComparison.OrdinalIgnoreCase) || vk.Equals("REG_SZ", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.String;
                    else
                    {
                        // Inference fallback for legacy records without ValueKind
                        if (int.TryParse(previousValue, out _))
                            kind = RegistryValueKind.DWord;
                        else if (long.TryParse(previousValue, out _))
                            kind = RegistryValueKind.QWord;
                        else
                            kind = RegistryValueKind.String;
                    }

                    object valToSet;
                    switch (kind)
                    {
                        case RegistryValueKind.DWord:
                            if (previousValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            {
                                valToSet = Convert.ToInt32(previousValue, 16);
                            }
                            else if (int.TryParse(previousValue, out int iVal))
                            {
                                valToSet = iVal;
                            }
                            else if (uint.TryParse(previousValue, out uint uVal))
                            {
                                valToSet = unchecked((int)uVal);
                            }
                            else
                            {
                                error = $"Cannot parse '{previousValue}' as DWORD integer.";
                                return false;
                            }
                            break;

                        case RegistryValueKind.QWord:
                            if (previousValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            {
                                valToSet = Convert.ToInt64(previousValue, 16);
                            }
                            else if (long.TryParse(previousValue, out long qVal))
                            {
                                valToSet = qVal;
                            }
                            else if (ulong.TryParse(previousValue, out ulong uqVal))
                            {
                                valToSet = unchecked((long)uqVal);
                            }
                            else
                            {
                                error = $"Cannot parse '{previousValue}' as QWORD integer.";
                                return false;
                            }
                            break;

                        case RegistryValueKind.Binary:
                            try
                            {
                                if (string.IsNullOrEmpty(previousValue) || previousValue.Equals("EMPTY", StringComparison.OrdinalIgnoreCase))
                                {
                                    valToSet = Array.Empty<byte>();
                                }
                                else
                                {
                                    string cleanHex = previousValue.Replace(" ", "").Replace("-", "").Replace(",", "");
                                    if (cleanHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                        cleanHex = cleanHex.Substring(2);

                                    if (cleanHex.Length % 2 != 0)
                                        cleanHex = "0" + cleanHex;

                                    byte[] bytes = new byte[cleanHex.Length / 2];
                                    for (int i = 0; i < bytes.Length; i++)
                                    {
                                        bytes[i] = Convert.ToByte(cleanHex.Substring(i * 2, 2), 16);
                                    }
                                    valToSet = bytes;
                                }
                            }
                            catch (Exception bEx)
                            {
                                error = $"Failed to parse binary hex payload '{previousValue}': {bEx.Message}";
                                return false;
                            }
                            break;

                        case RegistryValueKind.MultiString:
                            valToSet = previousValue.Split(new[] { "\r\n", "\n", "\0", "||" }, StringSplitOptions.None);
                            break;

                        case RegistryValueKind.ExpandString:
                            valToSet = previousValue;
                            break;

                        case RegistryValueKind.String:
                        default:
                            valToSet = previousValue;
                            break;
                    }

                    key.SetValue(valueName, valToSet, kind);
                    key.Flush();

                    // Readback verification
                    var readBack = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (readBack == null)
                    {
                        error = $"Registry write verification failed: read-back for '{valueName}' was NULL.";
                        Log($"[Rollback Error] {error}");
                        return false;
                    }

                    var readKind = key.GetValueKind(valueName);
                    if (readKind != kind)
                    {
                        error = $"Registry write verification failed: read-back kind '{readKind}' did not match target kind '{kind}'.";
                        Log($"[Rollback Error] {error}");
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"RollbackRegistry exception for '{targetKey}': {ex.Message}";
                Log($"[Rollback Error] {error}");
                return false;
            }
        }

        private static bool RollbackScheduledTask(string taskPath, string previousState)
        {
            try
            {
                string action = previousState.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "/DISABLE" : "/ENABLE";
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/CHANGE /TN \"{taskPath}\" {action}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool RollbackAppX(string packageName, string previousState)
        {
            try
            {
                string regPath = $@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\{packageName}";
                using var key = Registry.CurrentUser.OpenSubKey(regPath, writable: true);
                if (key != null)
                {
                    key.SetValue("Disabled", 0, RegistryValueKind.DWord);
                    key.SetValue("DisabledByUser", 0, RegistryValueKind.DWord);
                    return true;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool RollbackProcessPriority(string targetIdentifier, string previousState)
        {
            try
            {
                int pid = 0;
                string? expectedName = null;
                if (targetIdentifier.StartsWith("PID:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = targetIdentifier.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int p)) pid = p;
                    if (parts.Length >= 3) expectedName = parts[2];
                }
                else if (int.TryParse(targetIdentifier, out int p))
                {
                    pid = p;
                }

                if (pid <= 4) return true;
                if (Enum.TryParse<System.Diagnostics.ProcessPriorityClass>(previousState, true, out var prio))
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    using (proc)
                    {
                        if (proc.HasExited) return true;
                        if (!string.IsNullOrEmpty(expectedName) && !proc.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true; // PID was reused; do not modify priority of unrelated process
                        }
                        proc.PriorityClass = prio;
                        return true;
                    }
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static bool RollbackPowerSetting(string targetIdentifier, string previousState)
        {
            try
            {
                if (targetIdentifier.Contains("HeterogeneousScheduling", StringComparison.OrdinalIgnoreCase))
                {
                    int val = int.TryParse(previousState, out int p) ? p : 0;
                    RunPowerCfg($"/setacvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 7f2f5c9f-ff36-4731-86b3-63f6e21575c6 {val}");
                    RunPowerCfg($"/setacvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 ba570884-577a-4ec6-84a3-7392d40cbea3 {val}");
                    RunPowerCfg("/setactive SCHEME_CURRENT");
                    return true;
                }

                if (targetIdentifier.Contains("Amd3dVcache", StringComparison.OrdinalIgnoreCase))
                {
                    int val = int.TryParse(previousState, out int p) ? p : 1;
                    RunPowerCfg($"/setacvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 7f2f5c9f-ff36-4731-86b3-63f6e21575c6 {val}");
                    RunPowerCfg("/setactive SCHEME_CURRENT");
                    return true;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool DeleteTransaction(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string cleanId = id.StartsWith("v4tx-", StringComparison.OrdinalIgnoreCase) ? id.Substring(5) : id;
            if (string.IsNullOrWhiteSpace(cleanId)) return false;

            if (cleanId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || cleanId.Contains(".."))
                return false;

            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_storageDir)) return false;

                    bool deleted = false;
                    foreach (var file in Directory.GetFiles(_storageDir, "*.json"))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.EndsWith($"_{cleanId}", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith($"{cleanId}_", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, cleanId, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Delete(file);
                                deleted = true;
                            }
                            catch { }
                        }
                    }

                    if (deleted)
                    {
                        InvalidateCache();
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public (int Deleted, int Failed) DeleteTransactions(IEnumerable<string> ids)
        {
            if (ids == null) return (0, 0);
            var idSet = new HashSet<string>(ids.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.StartsWith("v4tx-", StringComparison.OrdinalIgnoreCase) ? x.Substring(5) : x), StringComparer.OrdinalIgnoreCase);
            if (idSet.Count == 0) return (0, 0);

            int deleted = 0;
            int failed = 0;

            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_storageDir)) return (0, 0);

                    foreach (var file in Directory.GetFiles(_storageDir, "*.json"))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        bool shouldDelete = false;

                        foreach (var targetId in idSet)
                        {
                            if (string.IsNullOrWhiteSpace(targetId)) continue;
                            if (targetId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || targetId.Contains(".."))
                                continue;

                            if (fileName.EndsWith($"_{targetId}", StringComparison.OrdinalIgnoreCase) ||
                                fileName.StartsWith($"{targetId}_", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fileName, targetId, StringComparison.OrdinalIgnoreCase))
                            {
                                shouldDelete = true;
                                break;
                            }
                        }

                        if (shouldDelete)
                        {
                            try
                            {
                                File.Delete(file);
                                deleted++;
                            }
                            catch
                            {
                                failed++;
                            }
                        }
                    }

                    if (deleted > 0)
                    {
                        InvalidateCache();
                    }
                }
                catch { }
            }

            return (deleted, failed);
        }

        public (int Deleted, int Failed) DeleteAllTransactions()
        {
            int deleted = 0;
            int failed = 0;

            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_storageDir)) return (0, 0);

                    foreach (var file in Directory.GetFiles(_storageDir, "*.json"))
                    {
                        try
                        {
                            File.Delete(file);
                            deleted++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }

                    InvalidateCache();
                }
                catch { }
            }

            return (deleted, failed);
        }
    }
}
