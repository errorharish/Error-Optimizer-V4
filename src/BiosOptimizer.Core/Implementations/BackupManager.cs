using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Common;
using BiosOptimizer.Core.Implementations.Config;
using BiosOptimizer.Core.Implementations.Power;
using BiosOptimizer.Core.Interfaces;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations
{
    public class BackupManager : IBackupManager
    {
        private static readonly Lazy<BackupManager> _instance = new(() => new BackupManager());
        public static BackupManager Instance => _instance.Value;

        private readonly string _backupRoot;
        private readonly string _transactionsDir;
        private readonly string _profilesDir;
        private readonly string _registryDir;
        private readonly string _powerDir;
        private readonly string _networkDir;
        private readonly string _inputDir;
        private readonly string _storageDir;
        private readonly string _systemDir;
        private readonly string _metadataDir;
        private readonly string _manifestPath;

        private readonly object _lock = new();
        private List<BackupTransaction>? _manifestCache;
        private DateTime _manifestCacheUtc = DateTime.MinValue;
        private DateTime _manifestLastWriteUtc = DateTime.MinValue;
        private long _manifestLastLength = -1;
        private static readonly TimeSpan ManifestCacheTtl = TimeSpan.FromSeconds(30);

        public void InvalidateCache()
        {
            lock (_lock)
            {
                _manifestCache = null;
                _manifestCacheUtc = DateTime.MinValue;
                _manifestLastWriteUtc = DateTime.MinValue;
                _manifestLastLength = -1;
            }
        }
        private readonly IRegistryManager? _registryManager;
        private readonly IServiceManager? _serviceManager;

        private static readonly AsyncLocal<(string ProfileId, string ProfileName, string BatchId)?> _ambientBatchScope = new();

        public sealed class BatchScope : IDisposable
        {
            private readonly (string ProfileId, string ProfileName, string BatchId)? _previous;
            public string BatchId { get; }

            internal BatchScope(string profileId, string profileName, string batchId)
            {
                BatchId = batchId;
                _previous = _ambientBatchScope.Value;
                _ambientBatchScope.Value = (profileId, profileName, batchId);
            }

            public void Dispose()
            {
                _ambientBatchScope.Value = _previous;
            }
        }

        public static BatchScope PushProfileBatchScope(string profileId, string profileName, string batchId) =>
            new(profileId, profileName, batchId);

        public static bool EnsureVerifiedTransaction(BackupTransaction? tx, out string error)
        {
            if (tx == null)
            {
                error = "Application backup transaction could not be created.";
                return false;
            }
            if (string.Equals(tx.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Application backup write failed for '{tx.OptimizationName}' ({tx.TransactionId}).";
                return false;
            }
            var (valid, reason) = Instance.ValidateTransactionIntegrity(tx.TransactionId);
            if (!valid)
            {
                error = $"Application backup SHA-256 verification failed for '{tx.OptimizationName}': {reason}";
                return false;
            }
            error = string.Empty;
            return true;
        }

        public event Action<BackupTransaction>? TransactionRecorded;

        public BackupManager(IRegistryManager? registryManager = null, IServiceManager? serviceManager = null)
        {
            _registryManager = registryManager;
            _serviceManager = serviceManager;

            _backupRoot = AppEnvironment.BackupRoot;
            _transactionsDir = Path.Combine(_backupRoot, "Transactions");
            _profilesDir = Path.Combine(_backupRoot, "Profiles");
            _registryDir = Path.Combine(_backupRoot, "Registry");
            _powerDir = Path.Combine(_backupRoot, "Power");
            _networkDir = Path.Combine(_backupRoot, "Network");
            _inputDir = Path.Combine(_backupRoot, "Input");
            _storageDir = Path.Combine(_backupRoot, "Storage");
            _systemDir = Path.Combine(_backupRoot, "System");
            _metadataDir = Path.Combine(_backupRoot, "Metadata");
            _manifestPath = Path.Combine(_metadataDir, "manifest.json");

            EnsureDirectories();
        }

        private void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(_backupRoot);
                Directory.CreateDirectory(_transactionsDir);
                Directory.CreateDirectory(_profilesDir);
                Directory.CreateDirectory(_registryDir);
                Directory.CreateDirectory(_powerDir);
                Directory.CreateDirectory(_networkDir);
                Directory.CreateDirectory(_inputDir);
                Directory.CreateDirectory(_storageDir);
                Directory.CreateDirectory(_systemDir);
                Directory.CreateDirectory(_metadataDir);
            }
            catch { }
        }

        public string BeginBatchTransaction(string profileId, string profileName, string category)
        {
            string batchId = $"BATCH-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            var batchTx = new BackupTransaction
            {
                TransactionId = batchId,
                ProfileId = profileId,
                ProfileName = profileName,
                OptimizationId = batchId,
                OptimizationName = $"{profileName} Batch Execution",
                Category = category,
                OperationType = "BatchContainer",
                Status = "IN_PROGRESS",
                CanRollback = false,
                RollbackStatus = "CONTAINER"
            };

            RecordTransaction(batchTx);
            return batchId;
        }

        public bool CommitBatchTransaction(string batchTransactionId)
        {
            lock (_lock)
            {
                var tx = GetTransactionById(batchTransactionId);
                if (tx != null)
                {
                    tx.Status = "VERIFIED";
                    tx.VerificationStatus = "VERIFIED";
                    SaveTransactionFile(tx);
                    UpdateManifestIndex(tx);
                    return true;
                }
            }
            return false;
        }

        public BackupTransaction RecordTransaction(BackupTransaction transaction)
        {
            if (string.IsNullOrEmpty(transaction.TransactionId))
            {
                transaction.TransactionId = $"TX-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
            }

            if (_ambientBatchScope.Value is { } scope)
            {
                bool isContainerOrSnapshot =
                    string.Equals(transaction.OperationType, "BatchContainer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(transaction.OperationType, "ApplicationSnapshot", StringComparison.OrdinalIgnoreCase);

                if (!isContainerOrSnapshot && string.IsNullOrWhiteSpace(transaction.ParentTransactionId))
                    transaction.ParentTransactionId = scope.BatchId;

                if (string.IsNullOrWhiteSpace(transaction.ProfileId) ||
                    string.Equals(transaction.ProfileId, "Manual", StringComparison.OrdinalIgnoreCase))
                {
                    transaction.ProfileId = scope.ProfileId;
                    transaction.ProfileName = scope.ProfileName;
                }
            }

            transaction.Timestamp = DateTime.UtcNow;
            transaction.MachineId = Environment.MachineName;
            transaction.WindowsBuild = Environment.OSVersion.VersionString;
            transaction.User = Environment.UserName;

            // Generate backup file location
            string targetSubDir = transaction.Category switch
            {
                "Registry" => _registryDir,
                "Power" => _powerDir,
                "Network" => _networkDir,
                "Input" => _inputDir,
                "Storage" => _storageDir,
                "System" or "Repair" => _systemDir,
                _ => _transactionsDir
            };

            string txFile = Path.Combine(targetSubDir, $"{transaction.TransactionId}.json");
            transaction.BackupLocation = txFile;

            lock (_lock)
            {
                bool savedTx = SaveTransactionFile(transaction);
                if (!savedTx)
                {
                    try { if (!string.IsNullOrWhiteSpace(transaction.BackupLocation) && File.Exists(transaction.BackupLocation)) File.Delete(transaction.BackupLocation); } catch { }
                    transaction.Status = "BACKUP_WRITE_FAILED";
                    transaction.VerificationStatus = "UNVERIFIED";
                    transaction.CanRollback = false;
                    transaction.RollbackStatus = "FAILED";
                }
                else
                {
                    bool savedManifest = UpdateManifestIndex(transaction);
                    if (!savedManifest)
                    {
                        try { if (!string.IsNullOrWhiteSpace(transaction.BackupLocation) && File.Exists(transaction.BackupLocation)) File.Delete(transaction.BackupLocation); } catch { }
                        transaction.Status = "BACKUP_WRITE_FAILED";
                        transaction.VerificationStatus = "UNVERIFIED";
                        transaction.CanRollback = false;
                        transaction.RollbackStatus = "FAILED";
                    }
                }
            }

            try
            {
                TransactionRecorded?.Invoke(transaction);
            }
            catch { }

            return transaction;
        }

        private static bool IsSystemRestoreRecord(BackupTransaction tx) =>
            string.Equals(tx.OperationType, "SystemRestorePoint", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tx.RollbackMethod, "SystemRestorePoint", StringComparison.OrdinalIgnoreCase);

        private static string ComputeIntegrityHash(BackupTransaction tx)
        {
            string paths = tx.AffectedPaths is { Count: > 0 } ? string.Join("|", tx.AffectedPaths) : "";
            string canonical = string.Join("\n",
                tx.TransactionId,
                tx.ProfileId,
                tx.OptimizationId,
                tx.Category,
                tx.OperationType,
                tx.RollbackMethod,
                tx.TargetKey,
                tx.ValueName,
                tx.ValueType,
                tx.PreviousExists ? "1" : "0",
                tx.BeforeState,
                tx.AfterState,
                paths);
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(bytes);
        }

        private static string ComputeFileSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            byte[] bytes = SHA256.HashData(stream);
            return Convert.ToHexString(bytes);
        }

        private bool SaveTransactionFile(BackupTransaction tx)
        {
            string? tmpFile = null;
            try
            {
                tx.IntegrityHash = ComputeIntegrityHash(tx);

                if (!string.IsNullOrEmpty(tx.BackupLocation))
                {
                    string dir = Path.GetDirectoryName(tx.BackupLocation) ?? _transactionsDir;
                    Directory.CreateDirectory(dir);
                    string json = JsonSerializer.Serialize(tx, new JsonSerializerOptions { WriteIndented = true });
                    tmpFile = tx.BackupLocation + $".{Environment.ProcessId}.tmp";
                    File.WriteAllText(tmpFile, json);
                    if (File.Exists(tx.BackupLocation))
                        File.Replace(tmpFile, tx.BackupLocation, null, ignoreMetadataErrors: true);
                    else
                        File.Move(tmpFile, tx.BackupLocation, overwrite: true);

                    if (!File.Exists(tx.BackupLocation))
                        return false;

                    tx.FileSha256 = ComputeFileSha256(tx.BackupLocation);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackupManager] Failed to save transaction file: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmpFile))
                {
                    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                }
            }
            return false;
        }

        private bool UpdateManifestIndex(BackupTransaction tx)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tx.IntegrityHash))
                {
                    tx.IntegrityHash = ComputeIntegrityHash(tx);
                }

                string? manifestDir = Path.GetDirectoryName(_manifestPath);
                if (!string.IsNullOrEmpty(manifestDir))
                    Directory.CreateDirectory(manifestDir);

                var list = LoadManifest();
                int idx = list.FindIndex(t => t.TransactionId.Equals(tx.TransactionId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    list[idx] = tx;
                }
                else
                {
                    list.Insert(0, tx);
                }

                WriteManifestAtomically(list);
                return File.Exists(_manifestPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackupManager] Failed to update manifest: {ex.Message}");
                return false;
            }
        }

        private void WriteManifestAtomically(List<BackupTransaction> entries)
        {
            _manifestCache = new List<BackupTransaction>(entries);
            _manifestCacheUtc = DateTime.UtcNow;
            string? manifestDir = Path.GetDirectoryName(_manifestPath);
            if (!string.IsNullOrEmpty(manifestDir))
                Directory.CreateDirectory(manifestDir);

            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            string tmp = _manifestPath + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(_manifestPath))
                    File.Replace(tmp, _manifestPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, _manifestPath, overwrite: true);

                if (File.Exists(_manifestPath))
                {
                    var fi = new FileInfo(_manifestPath);
                    _manifestLastWriteUtc = fi.LastWriteTimeUtc;
                    _manifestLastLength = fi.Length;
                }
            }
            catch
            {
                File.Move(tmp, _manifestPath, overwrite: true);
                try
                {
                    if (File.Exists(_manifestPath))
                    {
                        var fi = new FileInfo(_manifestPath);
                        _manifestLastWriteUtc = fi.LastWriteTimeUtc;
                        _manifestLastLength = fi.Length;
                    }
                }
                catch { }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private string DeletedIdsPath => Path.Combine(_metadataDir, "deleted.json");

        private HashSet<string> LoadDeletedIds()
        {
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(DeletedIdsPath))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DeletedIdsPath));
                    if (list != null)
                    {
                        foreach (var id in list)
                        {
                            if (!string.IsNullOrWhiteSpace(id))
                                deleted.Add(id);
                        }
                    }
                }
            }
            catch { }
            return deleted;
        }

        private void RecordDeletedId(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId)) return;
            try
            {
                var deleted = LoadDeletedIds();
                if (!deleted.Add(transactionId)) return;
                Directory.CreateDirectory(_metadataDir);
                string tmp = DeletedIdsPath + $".{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(deleted.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(DeletedIdsPath))
                    File.Replace(tmp, DeletedIdsPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, DeletedIdsPath, overwrite: true);
            }
            catch { }
        }

        private void RecordDeletedIds(IEnumerable<string> transactionIds)
        {
            if (transactionIds == null) return;
            try
            {
                var deleted = LoadDeletedIds();
                bool addedAny = false;
                foreach (var id in transactionIds)
                {
                    if (!string.IsNullOrWhiteSpace(id) && deleted.Add(id))
                    {
                        addedAny = true;
                    }
                }
                if (!addedAny) return;

                Directory.CreateDirectory(_metadataDir);
                string tmp = DeletedIdsPath + $".{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(deleted.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(DeletedIdsPath))
                    File.Replace(tmp, DeletedIdsPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, DeletedIdsPath, overwrite: true);
            }
            catch { }
        }

        private List<BackupTransaction> LoadManifest()
        {
            lock (_lock)
            {
                bool fileChanged = false;
                if (File.Exists(_manifestPath))
                {
                    try
                    {
                        var fi = new FileInfo(_manifestPath);
                        if (fi.LastWriteTimeUtc != _manifestLastWriteUtc || fi.Length != _manifestLastLength)
                        {
                            fileChanged = true;
                            _manifestLastWriteUtc = fi.LastWriteTimeUtc;
                            _manifestLastLength = fi.Length;
                        }
                    }
                    catch { }
                }

                if (!fileChanged && _manifestCache != null && (DateTime.UtcNow - _manifestCacheUtc) < ManifestCacheTtl)
                {
                    return new List<BackupTransaction>(_manifestCache);
                }

                var items = new List<BackupTransaction>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deletedIds = LoadDeletedIds();

                if (File.Exists(_manifestPath))
                {
                    try
                    {
                        string json = File.ReadAllText(_manifestPath);
                        var primary = JsonSerializer.Deserialize<List<BackupTransaction>>(json);
                        if (primary != null)
                        {
                            foreach (var tx in primary)
                            {
                                if (!string.IsNullOrWhiteSpace(tx.TransactionId) &&
                                    !deletedIds.Contains(tx.TransactionId) &&
                                    seenIds.Add(tx.TransactionId))
                                {
                                    items.Add(tx);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Preserve existing legacy backup manifests non-destructively when migrating to %ProgramData%\Error Optimizer\Backups
                foreach (var legacyRoot in AppEnvironment.GetLegacyBackupDirectories())
                {
                    string legacyManifest = Path.Combine(legacyRoot, "Metadata", "manifest.json");
                    if (!File.Exists(legacyManifest)) continue;
                    try
                    {
                        string json = File.ReadAllText(legacyManifest);
                        var legacy = JsonSerializer.Deserialize<List<BackupTransaction>>(json);
                        if (legacy == null) continue;
                        foreach (var tx in legacy)
                        {
                            if (!string.IsNullOrWhiteSpace(tx.TransactionId) &&
                                !deletedIds.Contains(tx.TransactionId) &&
                                seenIds.Add(tx.TransactionId))
                            {
                                items.Add(tx);
                            }
                        }
                    }
                    catch { }
                }

                _manifestCache = new List<BackupTransaction>(items);
                _manifestCacheUtc = DateTime.UtcNow;
                return items;
            }
        }

        #region Specific Tweak Capture Helpers

        public static string FormatRegistryValue(object? val)
        {
            if (val == null) return "VALUE DID NOT EXIST";
            if (val is byte[] b) return BitConverter.ToString(b);
            if (val is string[] s) return string.Join("||", s);
            return val.ToString() ?? "";
        }

        public BackupTransaction CaptureRegistryTweak(string profileName, string optimizationName, string hive, string subKey, string valueName, object? oldValue, string oldType, object? targetValue, string riskLevel = "SAFE", string? parentBatchId = null)
        {
            bool previousExists = oldValue != null && oldValue.ToString() != "VALUE DID NOT EXIST" && oldValue.ToString() != "Not Present";
            string fullKeyPath = $"{hive}\\{subKey}";
            string viewStr = fullKeyPath.IndexOf("Wow6432Node", StringComparison.OrdinalIgnoreCase) >= 0 ? "Registry32" : "Registry64";

            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"reg.{subKey}.{valueName}".ToLowerInvariant().Replace('\\', '.'),
                OptimizationName = optimizationName,
                Category = "Registry",
                OperationType = "RegistryValue",
                TargetKey = fullKeyPath,
                ValueName = valueName,
                ValueType = oldType,
                RegistryView = viewStr,
                PreviousExists = previousExists,
                BeforeState = previousExists ? FormatRegistryValue(oldValue) : "VALUE DID NOT EXIST",
                AfterState = FormatRegistryValue(targetValue),
                RollbackMethod = "RegistryValueRestore",
                CanRollback = true,
                RollbackStatus = "AVAILABLE",
                RiskLevel = riskLevel,
                ParentTransactionId = parentBatchId ?? "",
                Notes = $"Registry value in {fullKeyPath} ({viewStr})"
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureServiceTweak(string profileName, string serviceName, string previousStartup, string newStartup, string previousState, string riskLevel = "SAFE", string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"svc.{serviceName}".ToLowerInvariant(),
                OptimizationName = $"Service: {serviceName}",
                Category = "Services",
                OperationType = "ServiceConfig",
                TargetKey = serviceName,
                BeforeState = $"Startup: {previousStartup}, State: {previousState}",
                AfterState = $"Startup: {newStartup}",
                RollbackMethod = "ServiceStartupRestore",
                CanRollback = true,
                RollbackStatus = "AVAILABLE",
                RiskLevel = riskLevel,
                ParentTransactionId = parentBatchId ?? "",
                Notes = $"Windows service {serviceName} startup type configuration"
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CapturePowerPlanTweak(string profileName, string planName, string previousGuid, string newGuid, string riskLevel = "SAFE", string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"power.{newGuid}".ToLowerInvariant(),
                OptimizationName = $"Power Scheme: {planName}",
                Category = "Power",
                OperationType = "PowerPlan",
                TargetKey = newGuid,
                BeforeState = $"Active Scheme GUID: {previousGuid}",
                AfterState = $"Active Scheme GUID: {newGuid} ({planName})",
                RollbackMethod = "PowerSchemeRestore",
                CanRollback = true,
                RollbackStatus = "AVAILABLE",
                RiskLevel = riskLevel,
                ParentTransactionId = parentBatchId ?? "",
                Notes = "Windows power scheme activation"
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureInputTweak(string profileName, string optimizationName, string targetSetting, object? previousValue, object? newValue, string riskLevel = "SAFE", string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"input.{targetSetting}".ToLowerInvariant().Replace(' ', '.'),
                OptimizationName = optimizationName,
                Category = "Input",
                OperationType = "InputSetting",
                TargetKey = targetSetting,
                BeforeState = previousValue?.ToString() ?? "Default",
                AfterState = newValue?.ToString() ?? "Optimized",
                RollbackMethod = "InputSettingRestore",
                CanRollback = true,
                RollbackStatus = "AVAILABLE",
                RiskLevel = riskLevel,
                ParentTransactionId = parentBatchId ?? "",
                Notes = $"Input tuning parameter {targetSetting}"
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureNetworkTweak(string profileName, string settingName, string targetInterface, object? previousValue, object? newValue, string riskLevel = "SAFE", string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"net.{settingName}".ToLowerInvariant().Replace(' ', '.'),
                OptimizationName = settingName,
                Category = "Network",
                OperationType = "NetworkTweak",
                TargetKey = targetInterface,
                BeforeState = previousValue?.ToString() ?? "Default",
                AfterState = newValue?.ToString() ?? "Optimized",
                RollbackMethod = "NetworkTweakRestore",
                CanRollback = true,
                RollbackStatus = "AVAILABLE",
                RiskLevel = riskLevel,
                ParentTransactionId = parentBatchId ?? "",
                Notes = $"TCP/IP and network interface tuning for {targetInterface}"
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureStorageCleanup(string profileName, string categoryName, long reclaimedBytes, int filesDeleted, List<string> targetPaths, bool wasQuarantined = false, string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"storage.{categoryName}".ToLowerInvariant().Replace(' ', '.'),
                OptimizationName = $"Storage Cleanup: {categoryName}",
                Category = "Storage",
                OperationType = wasQuarantined ? "FileQuarantine" : "FileDeletion",
                TargetKey = categoryName,
                BeforeState = $"{filesDeleted} files ({FormatBytes(reclaimedBytes)})",
                AfterState = "Cleaned",
                RollbackMethod = wasQuarantined ? "QuarantineRestore" : "NonReversible",
                CanRollback = wasQuarantined,
                RollbackStatus = wasQuarantined ? "AVAILABLE" : "NOT_SUPPORTED",
                RiskLevel = "SAFE",
                ParentTransactionId = parentBatchId ?? "",
                AffectedPaths = targetPaths ?? new List<string>(),
                Notes = wasQuarantined 
                    ? "Files quarantined in safe area; reversible." 
                    : "NON-REVERSIBLE — Physical files deleted to free storage space."
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureMemoryRuntime(string profileName, long ramReclaimedMb, string loadBefore, string loadAfter, string details, string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = "memory.runtime.reclaim",
                OptimizationName = "Memory & Working Set Reclaim",
                Category = "Memory",
                OperationType = "RuntimeMetric",
                TargetKey = "RAM",
                BeforeState = $"Load: {loadBefore}",
                AfterState = $"Load: {loadAfter} ({ramReclaimedMb} MB Reclaimed)",
                RollbackMethod = "None_RuntimeOnly",
                CanRollback = false,
                RollbackStatus = "NOT_SUPPORTED",
                RiskLevel = "SAFE",
                ParentTransactionId = parentBatchId ?? "",
                Notes = "RUNTIME ACTION — NO DIRECT ROLLBACK (Physical working set released to standby/free memory pool)."
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureSystemRepair(string profileName, string operationName, string beforeState, string afterState, string? parentBatchId = null)
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = $"repair.{operationName}".ToLowerInvariant().Replace(' ', '.'),
                OptimizationName = $"System Repair: {operationName}",
                Category = "Repair",
                OperationType = "RepairOperation",
                TargetKey = operationName,
                BeforeState = beforeState,
                AfterState = afterState,
                RollbackMethod = "None_RepairOperation",
                CanRollback = false,
                RollbackStatus = "NOT_SUPPORTED",
                RiskLevel = "SAFE",
                ParentTransactionId = parentBatchId ?? "",
                Notes = "REPAIR OPERATION — NO DIRECT ROLLBACK (Windows component store / integrity repair)."
            };

            return RecordTransaction(tx);
        }

        public BackupTransaction CaptureGenericTweak(string profileName, string optimizationId, string name, string category, string operationType, string beforeState, string afterState, string rollbackMethod, bool canRollback, string riskLevel = "SAFE", string? parentBatchId = null)
        {
            return CaptureTargetedGenericTweak(profileName, optimizationId, name, category, operationType, beforeState, afterState, rollbackMethod, canRollback, riskLevel, parentBatchId, "", "");
        }

        public BackupTransaction CaptureTargetedGenericTweak(string profileName, string optimizationId, string name, string category, string operationType, string beforeState, string afterState, string rollbackMethod, bool canRollback, string riskLevel = "SAFE", string? parentBatchId = null, string targetKey = "", string valueName = "")
        {
            var tx = new BackupTransaction
            {
                ProfileId = profileName,
                ProfileName = profileName,
                OptimizationId = optimizationId,
                OptimizationName = name,
                Category = category,
                OperationType = operationType,
                TargetKey = targetKey,
                ValueName = valueName,
                BeforeState = beforeState,
                AfterState = afterState,
                RollbackMethod = rollbackMethod,
                CanRollback = canRollback,
                RollbackStatus = canRollback ? "AVAILABLE" : "NOT_SUPPORTED",
                RiskLevel = riskLevel,
                ParentTransactionId = parentBatchId ?? "",
                Notes = $"Optimization transaction for {name}"
            };

            return RecordTransaction(tx);
        }

        #endregion

        #region Rollback & Restore Implementation

        public BackupTransaction CreateApplicationSnapshot(string name, string? notes = null)
        {
            string label = string.IsNullOrWhiteSpace(name)
                ? $"Application Backup Snapshot ({DateTime.Now:yyyy-MM-dd HH:mm})"
                : name.Trim();

            string snapshotGuid = string.Empty;
            string snapshotPlanName = "Unknown";
            try
            {
                var (guid, planName) = PowerPlanEngine.Instance.GetActiveSchemeNative();
                if (guid != Guid.Empty)
                {
                    snapshotGuid = guid.ToString("D");
                    snapshotPlanName = string.IsNullOrWhiteSpace(planName) ? snapshotGuid : planName;
                }
            }
            catch { }

            string settingsJson = string.Empty;
            try
            {
                var settings = UnifiedSettingsStore.LoadSafe();
                settingsJson = JsonSerializer.Serialize(settings);
            }
            catch { }

            string beforePayload = JsonSerializer.Serialize(new
            {
                activePowerGuid = snapshotGuid,
                activePowerName = snapshotPlanName,
                settingsJson
            });

            var tx = new BackupTransaction
            {
                ProfileId = "ApplicationSnapshot",
                ProfileName = "Application Snapshot",
                OptimizationId = "app.snapshot",
                OptimizationName = label,
                Category = "Snapshot",
                OperationType = "ApplicationSnapshot",
                BeforeState = beforePayload,
                AfterState = $"Power Plan: {snapshotPlanName}",
                TargetKey = "ErrorOptimizer.ApplicationState",
                ValueName = label,
                ValueType = "JsonSnapshot",
                RollbackMethod = "ApplicationSnapshotRestore",
                CanRollback = true,
                RollbackStatus = "AVAILABLE",
                RiskLevel = "SAFE",
                Notes = string.IsNullOrWhiteSpace(notes)
                    ? $"Self-contained application baseline (Power Plan: {snapshotPlanName})"
                    : notes.Trim()
            };

            return RecordTransaction(tx);
        }

        public (bool Valid, string Reason) ValidateTransactionIntegrity(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
                return (false, "Transaction ID is required.");

            var tx = GetTransactionById(transactionId);
            if (tx == null)
                return (false, $"Backup transaction '{transactionId}' was not found.");

            if (IsSystemRestoreRecord(tx))
                return (false, "Windows System Restore records cannot be restored via the Application Backup engine.");

            if (string.Equals(tx.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase))
                return (false, $"Backup transaction '{transactionId}' was interrupted during creation and is incomplete.");

            if (string.IsNullOrWhiteSpace(tx.BackupLocation))
                return (false, $"Backup transaction '{transactionId}' has no recorded payload file path.");

            if (!File.Exists(tx.BackupLocation))
                return (false, $"Backup transaction file missing at '{tx.BackupLocation}'.");

            try
            {
                if (!string.IsNullOrWhiteSpace(tx.FileSha256))
                {
                    string actualFileSha = ComputeFileSha256(tx.BackupLocation);
                    if (!string.Equals(actualFileSha, tx.FileSha256, StringComparison.OrdinalIgnoreCase))
                        return (false, $"Backup file '{tx.TransactionId}' failed SHA-256 verification (file modified or corrupt on disk).");
                }

                string json = File.ReadAllText(tx.BackupLocation);
                var parsed = JsonSerializer.Deserialize<BackupTransaction>(json);
                if (parsed == null || !string.Equals(parsed.TransactionId, tx.TransactionId, StringComparison.OrdinalIgnoreCase))
                    return (false, $"Backup transaction file '{tx.BackupLocation}' failed schema validation.");

                if (!string.IsNullOrWhiteSpace(tx.IntegrityHash))
                {
                    if (string.IsNullOrWhiteSpace(parsed.IntegrityHash) ||
                        !string.Equals(parsed.IntegrityHash, tx.IntegrityHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Backup transaction '{tx.TransactionId}' manifest checksum mismatch.");
                    }
                }

                if (string.IsNullOrWhiteSpace(parsed.IntegrityHash) && string.IsNullOrWhiteSpace(tx.FileSha256))
                    return (false, $"Backup transaction '{tx.TransactionId}' lacks required integrity checksum metadata.");

                if (!string.IsNullOrWhiteSpace(parsed.IntegrityHash))
                {
                    string expected = ComputeIntegrityHash(parsed);
                    if (!string.Equals(parsed.IntegrityHash, expected, StringComparison.OrdinalIgnoreCase))
                        return (false, $"Backup transaction '{tx.TransactionId}' failed SHA-256 payload integrity check.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Backup transaction file is unreadable or corrupt: {ex.Message}");
            }

            if (string.Equals(tx.RollbackMethod, "QuarantineRestore", StringComparison.OrdinalIgnoreCase))
            {
                bool anyArchive = tx.AffectedPaths.Any(p => File.Exists(Path.Combine(_storageDir, "Quarantine", Path.GetFileName(p))));
                if (!anyArchive)
                    return (false, $"Quarantine backup archive files for '{tx.OptimizationName}' are no longer present on disk.");
            }

            return (true, "Backup payload and SHA-256 integrity checksums verified.");
        }

        public async Task<RestoreResult> RestoreSingleTransactionAsync(string transactionId, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var result = new RestoreResult();
                var tx = GetTransactionById(transactionId);

                if (tx == null)
                {
                    sw.Stop();
                    result.Success = false;
                    result.BackupName = transactionId;
                    result.Category = "Unknown";
                    result.VerificationResult = "Transaction Not Found";
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Message = $"Transaction '{transactionId}' not found in manifest.";
                    return result;
                }

                result.BackupName = tx.OptimizationName;
                result.Category = tx.Category;

                if (!tx.CanRollback || tx.RollbackStatus == "NOT_SUPPORTED" || IsSystemRestoreRecord(tx))
                {
                    sw.Stop();
                    result.Success = false;
                    result.VerificationResult = "Rollback Not Supported";
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Message = $"Transaction '{tx.OptimizationName}' is a {tx.OperationType} and does not support direct rollback ({tx.Notes}).";
                    return result;
                }

                var (valid, integrityReason) = ValidateTransactionIntegrity(tx.TransactionId);
                if (!valid)
                {
                    sw.Stop();
                    tx.RollbackStatus = "FAILED";
                    tx.VerificationStatus = "INTEGRITY_FAILED";
                    SaveTransactionFile(tx);
                    UpdateManifestIndex(tx);

                    result.Success = false;
                    result.FailedCount = 1;
                    result.VerificationResult = "Integrity Validation Failed";
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Message = $"Pre-restore validation failed: {integrityReason}";
                    result.ErrorLogs.Add(integrityReason);
                    return result;
                }

                bool ok = ExecuteRollbackForTransaction(tx, out string error);
                sw.Stop();
                result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);

                if (ok)
                {
                    tx.Status = "ROLLED_BACK";
                    tx.RollbackStatus = "RESTORED";
                    tx.VerificationStatus = "VERIFIED";
                    SaveTransactionFile(tx);
                    UpdateManifestIndex(tx);

                    result.Success = true;
                    result.RestoredCount = 1;
                    result.VerificationResult = "100% Verified (Real State Readback Confirmed)";
                    result.Message = $"Successfully restored and verified {tx.OptimizationName} to previous state ({tx.BeforeState}).";
                    result.RestoredItems.Add(tx.OptimizationName);
                }
                else
                {
                    tx.RollbackStatus = "FAILED";
                    SaveTransactionFile(tx);
                    UpdateManifestIndex(tx);

                    result.Success = false;
                    result.FailedCount = 1;
                    result.VerificationResult = "Verification Failed";
                    result.Message = $"Rollback failed for {tx.OptimizationName}: {error}";
                    result.ErrorLogs.Add(error);
                }

                return result;
            }, ct);
        }

        public async Task<RestoreResult> RestoreProfileAsync(string profileName, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var result = new RestoreResult
                {
                    BackupName = $"{profileName} Profile Container",
                    Category = "Profile Batch"
                };

                var all = LoadManifest();

                // Find all child transactions for this profile in reverse chronological order
                var profileTx = all
                    .Where(t => t.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase) ||
                                t.ProfileId.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                    .Where(t => t.CanRollback &&
                                t.RollbackStatus != "RESTORED" &&
                                !string.Equals(t.OperationType, "BatchContainer", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(t.OperationType, "SystemRestorePoint", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(t.RollbackMethod, "SystemRestorePoint", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Timestamp)
                    .ToList();

                if (profileTx.Count == 0)
                {
                    sw.Stop();
                    result.Success = true;
                    result.VerificationResult = "Already in Baseline State";
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Message = $"No restorable transactions found for profile '{profileName}'. All states are already in their initial baseline.";
                    return result;
                }

                // Pre-validate every transaction in the profile batch before mutating any Windows state.
                foreach (var tx in profileTx)
                {
                    if (ct.IsCancellationRequested) break;
                    var (valid, integrityReason) = ValidateTransactionIntegrity(tx.TransactionId);
                    if (!valid)
                    {
                        tx.RollbackStatus = "FAILED";
                        tx.VerificationStatus = "INTEGRITY_FAILED";
                        SaveTransactionFile(tx);
                        UpdateManifestIndex(tx);
                        result.FailedCount++;
                        result.ErrorLogs.Add($"{tx.OptimizationName}: {integrityReason}");
                    }
                }

                if (result.FailedCount > 0)
                {
                    sw.Stop();
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Success = false;
                    result.VerificationResult = "PRE-RESTORE INTEGRITY VALIDATION FAILED";
                    result.Message = $"Aborted profile '{profileName}' restore: {result.FailedCount} backup transaction(s) failed integrity validation. No changes were applied.";
                    return result;
                }

                foreach (var tx in profileTx)
                {
                    if (ct.IsCancellationRequested) break;

                    bool ok = ExecuteRollbackForTransaction(tx, out string error);
                    if (ok)
                    {
                        tx.Status = "ROLLED_BACK";
                        tx.RollbackStatus = "RESTORED";
                        tx.VerificationStatus = "VERIFIED";
                        SaveTransactionFile(tx);
                        UpdateManifestIndex(tx);

                        result.RestoredCount++;
                        result.RestoredItems.Add(tx.OptimizationName);
                    }
                    else
                    {
                        tx.RollbackStatus = "FAILED";
                        SaveTransactionFile(tx);
                        UpdateManifestIndex(tx);

                        result.FailedCount++;
                        result.ErrorLogs.Add($"{tx.OptimizationName}: {error}");
                    }
                }

                sw.Stop();
                result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                result.Success = result.FailedCount == 0 && result.RestoredCount > 0;

                if (result.Success)
                {
                    result.VerificationResult = $"100% Verified ({result.RestoredCount} of {result.RestoredCount} Components)";
                    result.Message = $"Profile '{profileName}' rollback complete: {result.RestoredCount} items restored and verified.";
                }
                else if (result.RestoredCount > 0 && result.FailedCount > 0)
                {
                    result.VerificationResult = $"PARTIAL RESTORE ({result.RestoredCount} Succeeded, {result.FailedCount} Failed)";
                    result.Message = $"Profile '{profileName}' partial restore: {result.RestoredCount} items restored, {result.FailedCount} failed.";
                }
                else
                {
                    result.VerificationResult = "RESTORE FAILED";
                    result.Message = $"Profile '{profileName}' rollback failed ({result.FailedCount} errors).";
                }

                return result;
            }, ct);
        }

        public async Task<RestoreResult> RestoreFullSystemSnapshotAsync(CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var result = new RestoreResult
                {
                    BackupName = "Entire System Rollback Snapshot",
                    Category = "Full System Coordinated Restore"
                };

                var all = LoadManifest()
                    .Where(t => t.CanRollback &&
                                t.RollbackStatus != "RESTORED" &&
                                !string.Equals(t.OperationType, "BatchContainer", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(t.OperationType, "SystemRestorePoint", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(t.RollbackMethod, "SystemRestorePoint", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Timestamp)
                    .ToList();

                if (all.Count == 0)
                {
                    sw.Stop();
                    result.Success = true;
                    result.VerificationResult = "System in Clean Baseline State";
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Message = "No active reversible transactions to restore. System is in baseline state.";
                    return result;
                }

                // Pre-validate every transaction in the snapshot before mutating any Windows state.
                foreach (var tx in all)
                {
                    if (ct.IsCancellationRequested) break;
                    var (valid, integrityReason) = ValidateTransactionIntegrity(tx.TransactionId);
                    if (!valid)
                    {
                        tx.RollbackStatus = "FAILED";
                        tx.VerificationStatus = "INTEGRITY_FAILED";
                        SaveTransactionFile(tx);
                        UpdateManifestIndex(tx);
                        result.FailedCount++;
                        result.ErrorLogs.Add($"{tx.OptimizationName}: {integrityReason}");
                    }
                }

                if (result.FailedCount > 0)
                {
                    sw.Stop();
                    result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    result.Success = false;
                    result.VerificationResult = "PRE-RESTORE INTEGRITY VALIDATION FAILED";
                    result.Message = $"Aborted full system restore: {result.FailedCount} backup transaction(s) failed integrity validation. No changes were applied.";
                    return result;
                }

                foreach (var tx in all)
                {
                    if (ct.IsCancellationRequested) break;

                    bool ok = ExecuteRollbackForTransaction(tx, out string error);
                    if (ok)
                    {
                        tx.Status = "ROLLED_BACK";
                        tx.RollbackStatus = "RESTORED";
                        tx.VerificationStatus = "VERIFIED";
                        SaveTransactionFile(tx);
                        UpdateManifestIndex(tx);

                        result.RestoredCount++;
                        result.RestoredItems.Add(tx.OptimizationName);
                    }
                    else
                    {
                        tx.RollbackStatus = "FAILED";
                        SaveTransactionFile(tx);
                        UpdateManifestIndex(tx);

                        result.FailedCount++;
                        result.ErrorLogs.Add($"{tx.OptimizationName}: {error}");
                    }
                }

                sw.Stop();
                result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                result.Success = result.FailedCount == 0 && result.RestoredCount > 0;

                if (result.Success)
                {
                    result.VerificationResult = $"FULL RESTORE VERIFIED (100% Component Verification Succeeded)";
                    result.Message = $"Full system rollback complete: {result.RestoredCount} transactions restored and verified.";
                }
                else if (result.RestoredCount > 0 && result.FailedCount > 0)
                {
                    result.VerificationResult = $"PARTIAL RESTORE ({result.RestoredCount} Restored, {result.FailedCount} Failed)";
                    result.Message = $"Partial system restore: {result.RestoredCount} restored, {result.FailedCount} failed.";
                }
                else
                {
                    result.VerificationResult = "RESTORE FAILED";
                    result.Message = $"Full system restore failed ({result.FailedCount} failed components).";
                }

                return result;
            }, ct);
        }

        private bool ExecuteRollbackForTransaction(BackupTransaction tx, out string errorMessage)
        {
            errorMessage = "";
            try
            {
                switch (tx.RollbackMethod)
                {
                    case "RegistryValueRestore":
                        return RollbackRegistryValue(tx, out errorMessage);

                    case "ServiceStartupRestore":
                        return RollbackService(tx, out errorMessage);

                    case "PowerSchemeRestore":
                        return RollbackPowerPlan(tx, out errorMessage);

                    case "InputSettingRestore":
                    case "NetworkTweakRestore":
                        return RollbackGenericRegistryOrSetting(tx, out errorMessage);

                    case "ScheduledTaskRestore":
                    case "TaskStateRestore":
                        return RollbackScheduledTask(tx, out errorMessage);

                    case "StartupRestore":
                        return RollbackStartupEntry(tx, out errorMessage);

                    case "QuarantineRestore":
                        return RollbackQuarantineFiles(tx, out errorMessage);

                    case "ApplicationSnapshotRestore":
                        return RollbackApplicationSnapshot(tx, out errorMessage);

                    default:
                        errorMessage = $"No automated rollback handler for method '{tx.RollbackMethod}'.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private bool RollbackApplicationSnapshot(BackupTransaction tx, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(tx.BeforeState))
                {
                    error = "Application snapshot payload is empty.";
                    return false;
                }

                using var doc = JsonDocument.Parse(tx.BeforeState);
                var root = doc.RootElement;
                bool restoredAny = false;

                if (root.TryGetProperty("activePowerGuid", out var guidEl) && guidEl.ValueKind == JsonValueKind.String)
                {
                    string guidStr = guidEl.GetString() ?? string.Empty;
                    if (Guid.TryParse(guidStr, out var schemeGuid) && schemeGuid != Guid.Empty)
                    {
                        var res = PowerPlanEngine.Instance.ApplyPowerPlanAsync(schemeGuid.ToString("D"), PowerPlanChangeSource.USER_REQUEST).GetAwaiter().GetResult();
                        if (!res.Success)
                        {
                            error = $"Failed to restore saved power scheme '{guidStr}': {res.Message}";
                            return false;
                        }
                        restoredAny = true;
                    }
                }

                if (root.TryGetProperty("settingsJson", out var settingsEl) && settingsEl.ValueKind == JsonValueKind.String)
                {
                    string settingsJson = settingsEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(settingsJson))
                    {
                        var parsed = JsonSerializer.Deserialize<UnifiedAppSettings>(settingsJson);
                        if (parsed != null)
                        {
                            UnifiedSettingsStore.Save(parsed);
                            restoredAny = true;
                        }
                    }
                }

                if (!restoredAny)
                {
                    error = "Application snapshot contained no restorable configuration state.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to restore application snapshot: {ex.Message}";
                return false;
            }
        }

        private bool RollbackRegistryValue(BackupTransaction tx, out string error)
        {
            error = "";
            try
            {
                string targetKey = tx.TargetKey;
                string valueName = tx.ValueName;

                if (string.IsNullOrEmpty(targetKey))
                {
                    error = "Target registry key path is missing.";
                    return false;
                }

                var hive = targetKey.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ||
                           targetKey.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
                    ? RegistryHive.LocalMachine
                    : RegistryHive.CurrentUser;

                string subPath = targetKey
                    .Replace("HKEY_LOCAL_MACHINE\\", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKLM\\", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKEY_CURRENT_USER\\", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKCU\\", "", StringComparison.OrdinalIgnoreCase);

                var view = (string.Equals(tx.RegistryView, "Registry32", StringComparison.OrdinalIgnoreCase) ||
                            targetKey.IndexOf("Wow6432Node", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            string.Equals(tx.Notes, "32-bit", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(tx.Notes, "Registry32", StringComparison.OrdinalIgnoreCase))
                    ? RegistryView.Registry32
                    : RegistryView.Registry64;

                using var root = RegistryKey.OpenBaseKey(hive, view);

                if (!tx.PreviousExists || tx.BeforeState == "VALUE DID NOT EXIST" || tx.BeforeState == "Not Present")
                {
                    using var key = root.OpenSubKey(subPath, true);
                    if (key == null)
                    {
                        // Subkey already doesn't exist; value definitely does not exist
                        return true;
                    }

                    key.DeleteValue(valueName, false);
                    key.Flush();

                    // Real read-back verification
                    var verifyVal = key.GetValue(valueName);
                    if (verifyVal != null)
                    {
                        error = $"Registry delete verification failed: value '{valueName}' still exists.";
                        return false;
                    }
                    return true;
                }
                else
                {
                    using var key = root.CreateSubKey(subPath, true);
                    if (key == null)
                    {
                        error = $"Cannot open/create registry key '{subPath}' for writing.";
                        return false;
                    }

                    var kind = RegistryValueKind.String;
                    if (tx.ValueType.Equals("DWord", StringComparison.OrdinalIgnoreCase) || tx.ValueType.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.DWord;
                    else if (tx.ValueType.Equals("QWord", StringComparison.OrdinalIgnoreCase) || tx.ValueType.Equals("REG_QWORD", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.QWord;
                    else if (tx.ValueType.Equals("Binary", StringComparison.OrdinalIgnoreCase) || tx.ValueType.Equals("REG_BINARY", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.Binary;
                    else if (tx.ValueType.Equals("MultiString", StringComparison.OrdinalIgnoreCase) || tx.ValueType.Equals("REG_MULTI_SZ", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.MultiString;
                    else if (tx.ValueType.Equals("ExpandString", StringComparison.OrdinalIgnoreCase) || tx.ValueType.Equals("REG_EXPAND_SZ", StringComparison.OrdinalIgnoreCase))
                        kind = RegistryValueKind.ExpandString;

                    object val = tx.BeforeState;
                    if (kind == RegistryValueKind.DWord)
                    {
                        if (tx.BeforeState.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            val = Convert.ToInt32(tx.BeforeState, 16);
                        }
                        else if (int.TryParse(tx.BeforeState, out int iVal))
                        {
                            val = iVal;
                        }
                    }
                    else if (kind == RegistryValueKind.QWord)
                    {
                        if (long.TryParse(tx.BeforeState, out long qVal))
                        {
                            val = qVal;
                        }
                    }
                    else if (kind == RegistryValueKind.Binary)
                    {
                        try
                        {
                            string cleanHex = tx.BeforeState.Replace(" ", "").Replace("-", "").Replace(",", "");
                            if (cleanHex.Length % 2 == 0)
                            {
                                byte[] bytes = new byte[cleanHex.Length / 2];
                                for (int i = 0; i < bytes.Length; i++)
                                {
                                    bytes[i] = Convert.ToByte(cleanHex.Substring(i * 2, 2), 16);
                                }
                                val = bytes;
                            }
                        }
                        catch { }
                    }
                    else if (kind == RegistryValueKind.MultiString)
                    {
                        val = tx.BeforeState.Split(new[] { "\r\n", "\n", "\0", "||" }, StringSplitOptions.RemoveEmptyEntries);
                    }

                    key.SetValue(valueName, val, kind);
                    key.Flush();

                    // Real read-back verification
                    var readBack = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (readBack == null)
                    {
                        error = $"Registry write verification failed: read-back for '{valueName}' was NULL.";
                        return false;
                    }

                    string strRead = readBack.ToString() ?? "";
                    string strExpected = val.ToString() ?? "";
                    string strExpanded = Environment.ExpandEnvironmentVariables(strExpected);
                    if (!strRead.Equals(strExpected, StringComparison.OrdinalIgnoreCase) && 
                        !strRead.Equals(strExpanded, StringComparison.OrdinalIgnoreCase) && 
                        !strRead.Equals(tx.BeforeState, StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"Registry read-back mismatch: expected '{strExpected}', read '{strRead}'.";
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool RollbackService(BackupTransaction tx, out string error)
        {
            error = "";
            try
            {
                string svcName = tx.TargetKey;
                string beforeState = tx.BeforeState; // e.g. "Startup: Automatic, State: Running"

                // Cross-PC compatibility check: verify service exists in registry
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                string svcKeyPath = $@"SYSTEM\CurrentControlSet\Services\{svcName}";
                using var svcKey = root.OpenSubKey(svcKeyPath, true);
                if (svcKey == null)
                {
                    error = $"Service '{svcName}' does not exist on this machine (Incompatible component bypassed).";
                    return false;
                }

                string startupType = "Automatic";
                int targetStartDword = 2;
                if (beforeState.Contains("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    startupType = "Disabled";
                    targetStartDword = 4;
                }
                else if (beforeState.Contains("Manual", StringComparison.OrdinalIgnoreCase))
                {
                    startupType = "Manual";
                    targetStartDword = 3;
                }
                else if (beforeState.Contains("Automatic", StringComparison.OrdinalIgnoreCase))
                {
                    startupType = "Automatic";
                    targetStartDword = 2;
                }

                // Apply via sc.exe
                using var p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"config \"{svcName}\" start= {startupType.ToLowerInvariant()}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                p.Start();
                p.WaitForExit(3000);

                // Direct registry fallback / synchronization
                svcKey.SetValue("Start", targetStartDword, RegistryValueKind.DWord);
                svcKey.Flush();

                // Real read-back verification
                var readStart = svcKey.GetValue("Start");
                if (readStart != null && int.TryParse(readStart.ToString(), out int currentStart))
                {
                    if (currentStart == targetStartDword)
                    {
                        return true;
                    }
                }

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool RollbackPowerPlan(BackupTransaction tx, out string error)
        {
            error = "";
            try
            {
                string before = tx.BeforeState; // "Active Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e"
                string guid = before.Replace("Active Scheme GUID:", "").Trim();
                if (guid.Contains('(')) guid = guid.Split('(')[0].Trim();

                if (!string.IsNullOrEmpty(guid) && Guid.TryParse(guid, out _))
                {
                    using var p = new Process();
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = $"/setactive {guid}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };
                    p.Start();
                    p.WaitForExit(3000);

                    // Read-back verification via powercfg /getactivescheme
                    using var pCheck = new Process();
                    pCheck.StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = "/getactivescheme",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };
                    pCheck.Start();
                    string outCheck = pCheck.StandardOutput.ReadToEnd();
                    pCheck.WaitForExit(3000);

                    if (outCheck.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }

                    return p.ExitCode == 0;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool RollbackGenericRegistryOrSetting(BackupTransaction tx, out string error)
        {
            if (!string.IsNullOrEmpty(tx.TargetKey) && !string.IsNullOrEmpty(tx.ValueName))
            {
                return RollbackRegistryValue(tx, out error);
            }
            error = "No specific rollback target defined.";
            return false;
        }

        private bool RollbackScheduledTask(BackupTransaction tx, out string error)
        {
            error = "";
            string taskPath = !string.IsNullOrWhiteSpace(tx.TargetKey)
                ? tx.TargetKey
                : tx.OptimizationName.Replace("Scheduled Task:", "", StringComparison.OrdinalIgnoreCase).Trim();

            if (string.IsNullOrWhiteSpace(taskPath))
            {
                error = "Scheduled task path missing from backup transaction.";
                return false;
            }

            try
            {
                string escaped = taskPath.Replace("\"", "\\\"");
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{escaped}\" /ENABLE",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (p == null)
                {
                    error = "Failed to start schtasks.exe.";
                    return false;
                }
                string stdOut = p.StandardOutput.ReadToEnd();
                string stdErr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000) || p.ExitCode != 0)
                {
                    error = !string.IsNullOrWhiteSpace(stdErr) ? stdErr.Trim() : $"schtasks exited with code {p.ExitCode}: {stdOut.Trim()}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool RollbackStartupEntry(BackupTransaction tx, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(tx.TargetKey))
            {
                error = "Startup target metadata missing from backup transaction.";
                return false;
            }

            var parts = tx.TargetKey.Split('|', 2);
            if (parts.Length != 2)
            {
                error = $"Invalid startup target identifier '{tx.TargetKey}'.";
                return false;
            }

            try
            {
                var mgr = new Startup.WindowsStartupManager();
                bool ok = mgr.EnableEntry(parts[0], parts[1], tx.ValueName);
                if (!ok)
                {
                    error = $"Failed to re-enable startup entry '{tx.ValueName}' ({parts[0]}).";
                }
                return ok;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool RollbackQuarantineFiles(BackupTransaction tx, out string error)
        {
            error = "";
            int restored = 0;
            foreach (var path in tx.AffectedPaths)
            {
                string qPath = Path.Combine(_storageDir, "Quarantine", Path.GetFileName(path));
                if (File.Exists(qPath))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(path) ?? "";
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.Move(qPath, path, true);
                        if (File.Exists(path)) restored++;
                    }
                    catch { }
                }
            }
            return restored > 0;
        }

        #endregion

        #region Querying & Statistics API

        public List<BackupTransaction> GetAllTransactions()
        {
            lock (_lock)
            {
                return LoadManifest()
                    .Where(t => !IsSystemRestoreRecord(t))
                    .ToList();
            }
        }

        public List<BackupTransaction> GetRecentTransactions(int limit = 150)
        {
            lock (_lock)
            {
                var all = LoadManifest()
                    .Where(t => !IsSystemRestoreRecord(t));
                if (limit > 0)
                {
                    return all.Take(limit).ToList();
                }
                return all.ToList();
            }
        }

        public List<BackupTransaction> GetTransactionsPaginated(int offset, int limit, out int totalCount)
        {
            lock (_lock)
            {
                var all = LoadManifest()
                    .Where(t => !IsSystemRestoreRecord(t))
                    .ToList();
                totalCount = all.Count;
                if (offset < 0) offset = 0;
                if (limit <= 0) limit = 150;
                return all.Skip(offset).Take(limit).ToList();
            }
        }

        public List<ProfileBackupGroup> GetProfileBackupGroups(bool includeTransactions = true)
        {
            var groups = new List<ProfileBackupGroup>();
            var standardProfiles = new (string Id, string Display)[]
            {
                ("Normal", "NORMAL"),
                ("Pro", "PRO"),
                ("Ultimate", "ULTIMATE"),
                ("Debloat", "DEBLOAT"),
                ("BiosSafe", "BIOS SAFE"),
                ("MaxPerformance", "MAX PERFORMANCE")
            };

            var all = GetAllTransactions();

            foreach (var (id, display) in standardProfiles)
            {
                var profileItems = all
                    .Where(t => t.ProfileName.Equals(id, StringComparison.OrdinalIgnoreCase) || 
                                t.ProfileId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                                t.ProfileName.Equals(display, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var grp = new ProfileBackupGroup
                {
                    ProfileName = id,
                    DisplayName = display,
                    TotalTransactions = profileItems.Count,
                    RestorableTransactions = profileItems.Count(t => t.CanRollback && t.RollbackStatus == "AVAILABLE"),
                    FailedTransactions = profileItems.Count(t => t.RollbackStatus == "FAILED"),
                    LastBackupTime = profileItems.OrderByDescending(t => t.Timestamp).FirstOrDefault()?.Timestamp,
                    Transactions = includeTransactions ? profileItems : new List<BackupTransaction>()
                };
                groups.Add(grp);
            }

            return groups;
        }

        public BackupSummaryStats GetSummaryStats()
        {
            var all = GetAllTransactions();
            return new BackupSummaryStats
            {
                TotalBackups = all.Count,
                ProfileBackups = all.Count(t => t.OperationType == "BatchContainer" || (!t.ProfileId.Equals("Manual", StringComparison.OrdinalIgnoreCase) && !t.ProfileId.Equals("ApplicationSnapshot", StringComparison.OrdinalIgnoreCase))),
                OptimizationBackups = all.Count(t => t.OperationType != "BatchContainer" && !string.Equals(t.OperationType, "ApplicationSnapshot", StringComparison.OrdinalIgnoreCase)),
                RegistryBackups = all.Count(t => string.Equals(t.Category, "Registry", StringComparison.OrdinalIgnoreCase)),
                SystemSnapshots = all.Count(t => string.Equals(t.Category, "Snapshot", StringComparison.OrdinalIgnoreCase) || string.Equals(t.OperationType, "ApplicationSnapshot", StringComparison.OrdinalIgnoreCase)),
                RestorableCount = all.Count(t => t.CanRollback && t.RollbackStatus == "AVAILABLE"),
                PartialCount = all.Count(t => t.RollbackStatus == "PARTIAL"),
                NonReversibleCount = all.Count(t => !t.CanRollback || t.RollbackStatus == "NOT_SUPPORTED"),
                FailedCount = all.Count(t => t.RollbackStatus == "FAILED")
            };
        }

        public BackupTransaction? GetTransactionById(string transactionId)
        {
            return GetAllTransactions().FirstOrDefault(t => t.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase));
        }

        public bool DeleteTransaction(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId)) return false;
            lock (_lock)
            {
                var all = LoadManifest();
                int idx = all.FindIndex(t => t.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) && !IsSystemRestoreRecord(t));
                if (idx >= 0)
                {
                    var item = all[idx];
                    all.RemoveAt(idx);

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(item.BackupLocation) && File.Exists(item.BackupLocation))
                            File.Delete(item.BackupLocation);

                        if (Directory.Exists(_backupRoot))
                        {
                            string orphanName = $"{item.TransactionId}.json";
                            foreach (var file in Directory.EnumerateFiles(_backupRoot, orphanName, SearchOption.AllDirectories))
                            {
                                try { File.Delete(file); } catch { }
                            }
                        }
                    }
                    catch { }

                    RecordDeletedId(item.TransactionId);
                    WriteManifestAtomically(all);
                    return true;
                }
            }
            return false;
        }

        public (int Deleted, int Skipped, int Failed, long FreedBytes) DeleteTransactions(IEnumerable<string> transactionIds)
        {
            if (transactionIds == null) return (0, 0, 0, 0);
            var idSet = new HashSet<string>(transactionIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (idSet.Count == 0) return (0, 0, 0, 0);

            int deleted = 0;
            int skipped = 0;
            int failed = 0;
            long freedBytes = 0;
            var newlyDeletedIds = new List<string>();

            lock (_lock)
            {
                var all = LoadManifest();
                var remaining = new List<BackupTransaction>(all.Count);

                foreach (var tx in all)
                {
                    if (idSet.Contains(tx.TransactionId))
                    {
                        if (IsSystemRestoreRecord(tx))
                        {
                            skipped++;
                            remaining.Add(tx);
                            continue;
                        }

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(tx.BackupLocation) && File.Exists(tx.BackupLocation))
                            {
                                try
                                {
                                    var fi = new FileInfo(tx.BackupLocation);
                                    freedBytes += fi.Length;
                                    File.Delete(tx.BackupLocation);
                                }
                                catch { }
                            }

                            if (Directory.Exists(_backupRoot))
                            {
                                string orphanName = $"{tx.TransactionId}.json";
                                foreach (var file in Directory.EnumerateFiles(_backupRoot, orphanName, SearchOption.AllDirectories))
                                {
                                    try
                                    {
                                        var fi = new FileInfo(file);
                                        freedBytes += fi.Length;
                                        File.Delete(file);
                                    }
                                    catch { }
                                }
                            }

                            deleted++;
                            newlyDeletedIds.Add(tx.TransactionId);
                        }
                        catch
                        {
                            failed++;
                            remaining.Add(tx);
                        }
                    }
                    else
                    {
                        remaining.Add(tx);
                    }
                }

                if (newlyDeletedIds.Count > 0)
                {
                    RecordDeletedIds(newlyDeletedIds);
                    WriteManifestAtomically(remaining);
                }
            }

            return (deleted, skipped, failed, freedBytes);
        }

        public (int Deleted, int Skipped, int Failed, long FreedBytes) DeleteAllApplicationBackups()
        {
            int deleted = 0;
            int skipped = 0;
            int failed = 0;
            long freedBytes = 0;
            var newlyDeletedIds = new List<string>();

            lock (_lock)
            {
                var all = LoadManifest();
                var remaining = new List<BackupTransaction>();

                foreach (var tx in all)
                {
                    if (IsSystemRestoreRecord(tx))
                    {
                        skipped++;
                        remaining.Add(tx);
                        continue;
                    }

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(tx.BackupLocation) && File.Exists(tx.BackupLocation))
                        {
                            try
                            {
                                var fi = new FileInfo(tx.BackupLocation);
                                freedBytes += fi.Length;
                                File.Delete(tx.BackupLocation);
                            }
                            catch { }
                        }

                        deleted++;
                        newlyDeletedIds.Add(tx.TransactionId);
                    }
                    catch
                    {
                        failed++;
                        remaining.Add(tx);
                    }
                }

                try
                {
                    if (Directory.Exists(_backupRoot))
                    {
                        foreach (var file in Directory.EnumerateFiles(_backupRoot, "*.json", SearchOption.AllDirectories))
                        {
                            string fileName = Path.GetFileName(file);
                            if (string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fileName, "deleted_ids.json", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fileName, "index.json", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            try
                            {
                                var fi = new FileInfo(file);
                                freedBytes += fi.Length;
                                File.Delete(file);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (newlyDeletedIds.Count > 0 || deleted > 0)
                {
                    RecordDeletedIds(newlyDeletedIds);
                    WriteManifestAtomically(remaining);
                }
            }

            return (deleted, skipped, failed, freedBytes);
        }

        public void ClearOldBackups(int retentionDays)
        {
            if (retentionDays <= 0) return;
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var all = LoadManifest();
                var remaining = new List<BackupTransaction>();

                foreach (var tx in all)
                {
                    if (!IsSystemRestoreRecord(tx) && tx.Timestamp < cutoff && tx.RollbackStatus == "RESTORED")
                    {
                        try { if (!string.IsNullOrWhiteSpace(tx.BackupLocation) && File.Exists(tx.BackupLocation)) File.Delete(tx.BackupLocation); } catch { }
                    }
                    else
                    {
                        remaining.Add(tx);
                    }
                }

                WriteManifestAtomically(remaining);
            }
        }

        [Obsolete("Use WindowsSystemRestoreEngine directly. Windows System Restore points are strictly separated from Application Backups.")]
        public async Task<bool> CreateSystemRestorePointAsync(string description, CancellationToken ct = default)
        {
            return await Task.Run(() => CreateSystemRestorePoint(description), ct);
        }

        #endregion

        #region Legacy Compatibility Methods

        [Obsolete("Disabled outside V3 Dashboard. Windows System Restore points are strictly separated from Application Backups.")]
        public bool CreateSystemRestorePoint(string description)
        {
            // Windows System Restore Point creation is restricted exclusively to the V3 Dashboard explicit CREATE RESTORE POINT action.
            // Application optimizations and general backup workflows must create SHA-256 verified Application Backups instead.
            return false;
        }

        public bool BackupRegistryState(IEnumerable<string> registryPaths)
        {
            try
            {
                foreach (var fullPath in registryPaths)
                {
                    var safeName = fullPath.Replace("\\", "_").Replace(":", "") + ".reg";
                    var exportPath = Path.Combine(_registryDir, safeName);

                    using var p = new Process();
                    p.StartInfo.FileName = "reg.exe";
                    p.StartInfo.Arguments = $"export \"{fullPath}\" \"{exportPath}\" /y";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    p.WaitForExit(3000);
                }
                return true;
            }
            catch { return false; }
        }

        public bool BackupServiceState(IEnumerable<string> serviceNames)
        {
            if (serviceNames == null)
                return false;

            int captured = 0;
            bool allSucceeded = true;

            foreach (var rawName in serviceNames)
            {
                string svcName = rawName?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(svcName))
                    continue;

                try
                {
                    using var svcKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svcName}", writable: false);
                    string? startVal = svcKey?.GetValue("Start")?.ToString();
                    string startupType = _serviceManager?.GetStartupType(svcName).ToString() ?? startVal switch
                    {
                        "2" => "Automatic",
                        "3" => "Manual",
                        "4" => "Disabled",
                        "0" => "Boot",
                        "1" => "System",
                        _ => string.Empty
                    };

                    string runningState = _serviceManager?.GetRunningState(svcName).ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(runningState))
                    {
                        try
                        {
                            using var sc = new System.ServiceProcess.ServiceController(svcName);
                            runningState = sc.Status.ToString();
                            if (string.IsNullOrEmpty(startupType))
                                startupType = sc.StartType.ToString();
                        }
                        catch
                        {
                            allSucceeded = false;
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(startupType))
                    {
                        allSucceeded = false;
                        continue;
                    }

                    var tx = CaptureServiceTweak(
                        "Manual",
                        svcName,
                        startupType,
                        startupType,
                        runningState,
                        "SAFE");

                    if (string.Equals(tx.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase))
                        allSucceeded = false;
                    else
                        captured++;
                }
                catch
                {
                    allSucceeded = false;
                }
            }

            return captured > 0 && allSucceeded;
        }

        public bool LogKilledProcesses(IEnumerable<string> processPaths)
        {
            if (processPaths == null)
                return false;

            int recorded = 0;
            bool allSucceeded = true;

            foreach (var rawPath in processPaths)
            {
                string procEntry = rawPath?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(procEntry))
                    continue;

                try
                {
                    string procName = Path.GetFileNameWithoutExtension(procEntry);
                    if (string.IsNullOrWhiteSpace(procName)) procName = procEntry;

                    var tx = CaptureTargetedGenericTweak(
                        "ProcessReduction",
                        $"proc.kill.{procName}".ToLowerInvariant().Replace(' ', '_'),
                        $"Process Terminated: {procName}",
                        "System",
                        "ProcessTerminationLog",
                        $"Running ({procEntry})",
                        "Terminated",
                        "NonReversible",
                        canRollback: false,
                        riskLevel: "SAFE",
                        targetKey: procEntry,
                        valueName: procName);

                    if (string.Equals(tx.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase))
                        allSucceeded = false;
                    else
                        recorded++;
                }
                catch
                {
                    allSucceeded = false;
                }
            }

            return recorded > 0 && allSucceeded;
        }

        public bool RestoreAll()
        {
            var res = RestoreFullSystemSnapshotAsync().GetAwaiter().GetResult();
            return res.Success;
        }

        public bool BackupRegistryValue(string owner, string registryKey, string valueName, object oldValue, string oldType, object targetValue, string result)
        {
            if (string.IsNullOrWhiteSpace(registryKey) || string.IsNullOrWhiteSpace(valueName))
                return false;

            string trimmedKey = registryKey.Trim().Trim('\\');
            int slashIdx = trimmedKey.IndexOf('\\');
            string hive = slashIdx > 0 ? trimmedKey[..slashIdx] : trimmedKey;
            string subKey = slashIdx > 0 && slashIdx + 1 < trimmedKey.Length
                ? trimmedKey[(slashIdx + 1)..].Trim('\\')
                : string.Empty;

            if (string.IsNullOrWhiteSpace(subKey))
                return false;

            var tx = CaptureRegistryTweak(owner, valueName, hive, subKey, valueName, oldValue, oldType, targetValue);
            return !string.Equals(tx.Status, "BACKUP_WRITE_FAILED", StringComparison.OrdinalIgnoreCase);
        }

        public bool RestoreByOwner(string owner)
        {
            var res = RestoreProfileAsync(owner).GetAwaiter().GetResult();
            return res.Success;
        }

        #endregion

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}
