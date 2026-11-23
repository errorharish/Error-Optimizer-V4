#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using BiosOptimizer.Core.V4.Recommendations;

namespace BiosOptimizer.Core.V4.Architecture
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GlobalAppliedStatus
    {
        NOT_APPLIED,
        ALREADY_APPLIED,
        ALREADY_OPTIMAL,
        ACTIVE_PROFILE,
        PROFILE_TRANSITION_REQUIRED,
        EXTERNALLY_MODIFIED,
        CONFIGURATION_CHANGED,
        CONFIGURATION_CONFLICT,
        CONFLICT_DETECTED,
        PARTIALLY_APPLIED,
        STATE_VERIFICATION_REQUIRED,
        INELIGIBLE,
        EXECUTION_FAILED,
        ROLLBACK_AVAILABLE
    }

    public sealed class GlobalAppliedTargetRecord
    {
        [JsonPropertyName("canonicalTarget")]
        public string CanonicalTarget { get; set; } = string.Empty;

        [JsonPropertyName("canonicalId")]
        public string CanonicalId { get; set; } = string.Empty;

        [JsonPropertyName("owningCategory")]
        public string OwningCategory { get; set; } = string.Empty;

        [JsonPropertyName("appliedByModule")]
        public string AppliedByModule { get; set; } = string.Empty;

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = "SetRegistryValue";

        [JsonPropertyName("intendedValue")]
        public string IntendedValue { get; set; } = string.Empty;

        [JsonPropertyName("previousValue")]
        public string PreviousValue { get; set; } = string.Empty;

        [JsonPropertyName("appliedValue")]
        public string AppliedValue { get; set; } = string.Empty;

        [JsonPropertyName("currentObservedValue")]
        public string CurrentObservedValue { get; set; } = string.Empty;

        [JsonPropertyName("appliedTimestampUtc")]
        public DateTime AppliedTimestampUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("lastVerifiedUtc")]
        public DateTime LastVerifiedUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("verificationStatus")]
        public string VerificationStatus { get; set; } = "VERIFIED";

        /// <summary>
        /// Authoritative operation state classification: "AppliedAndVerified", "AlreadyOptimal", "Unverified", or "ExternallyModified".
        /// Strictly separates optimizer-applied changes from pre-existing confirmed optimal observations.
        /// </summary>
        [JsonPropertyName("stateClassification")]
        public string StateClassification { get; set; } = "AppliedAndVerified";

        [JsonPropertyName("applicabilityContext")]
        public string ApplicabilityContext { get; set; } = string.Empty;

        [JsonPropertyName("rollbackAvailable")]
        public bool RollbackAvailable { get; set; } = true;

        [JsonPropertyName("transactionId")]
        public string TransactionId { get; set; } = string.Empty;

        [JsonPropertyName("hardwareFingerprint")]
        public string HardwareFingerprint { get; set; } = Environment.MachineName;

        [JsonPropertyName("osVersion")]
        public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    }

    public sealed class GlobalAppliedStateContainer
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 2;

        [JsonPropertyName("lastSavedUtc")]
        public DateTime LastSavedUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("hardwareFingerprint")]
        public string HardwareFingerprint { get; set; } = Environment.MachineName;

        [JsonPropertyName("osVersion")]
        public string OsVersion { get; set; } = Environment.OSVersion.VersionString;

        [JsonPropertyName("records")]
        public List<GlobalAppliedTargetRecord> Records { get; set; } = new();
    }

    /// <summary>
    /// Authoritative backend-owned global applied-state store for ERROR OPTIMIZER V4.
    /// Tracks applied system optimizations across all categories, persists before/after states,
    /// verifies live system effects, detects external manual configuration changes, and
    /// strictly prevents duplicate or conflicting execution.
    /// </summary>
    public sealed class GlobalAppliedStateManager
    {
        private static readonly Lazy<GlobalAppliedStateManager> _instance = new(() => new GlobalAppliedStateManager());
        public static GlobalAppliedStateManager Instance => _instance.Value;

        private readonly string _storageFilePath;
        private readonly string _backupFilePath;
        private readonly ConcurrentDictionary<string, GlobalAppliedTargetRecord> _records = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _fileLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        public GlobalAppliedStateManager() : this(null) { }

        public GlobalAppliedStateManager(string? customStorageDir)
        {
            string baseDir;
            if (!string.IsNullOrWhiteSpace(customStorageDir))
            {
                baseDir = customStorageDir;
            }
            else
            {
                try
                {
                    baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "ErrorOptimizer", "AppliedState");
                }
                catch
                {
                    baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ErrorOptimizer", "AppliedState");
                }
            }

            try
            {
                if (!Directory.Exists(baseDir))
                    Directory.CreateDirectory(baseDir);
            }
            catch { }

            _storageFilePath = Path.Combine(baseDir, "global_applied_state.json");
            _backupFilePath = Path.Combine(baseDir, "global_applied_state.json.bak");
            LoadFromDisk();
        }

        /// <summary>
        /// Forces a fresh reload of persisted state records from disk (used on restart/reconciliation).
        /// </summary>
        public void ReloadFromDisk()
        {
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            lock (_fileLock)
            {
                bool loaded = TryLoadRecordsFromFile(_storageFilePath);
                if (!loaded && File.Exists(_backupFilePath))
                {
                    Debug.WriteLine("[GlobalAppliedStateManager] Primary state file missing or corrupt; recovering from backup.");
                    bool backupLoaded = TryLoadRecordsFromFile(_backupFilePath);
                    if (backupLoaded)
                    {
                        try
                        {
                            File.Copy(_backupFilePath, _storageFilePath, overwrite: true);
                        }
                        catch (Exception copyEx)
                        {
                            Debug.WriteLine($"[GlobalAppliedStateManager] Failed to restore primary state file from backup: {copyEx.Message}");
                        }
                    }
                }
            }
        }

        private bool TryLoadRecordsFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return false;

                // 1. Try deserialize modern container format (Schema V2)
                try
                {
                    var container = JsonSerializer.Deserialize<GlobalAppliedStateContainer>(json, JsonOptions);
                    if (container?.Records != null && container.Records.Count > 0)
                    {
                        _records.Clear();
                        foreach (var rec in container.Records)
                        {
                            if (!string.IsNullOrWhiteSpace(rec.CanonicalTarget))
                                _records[rec.CanonicalTarget] = rec;
                        }
                        return true;
                    }
                }
                catch { }

                // 2. Fallback to legacy array format (Schema V1)
                var list = JsonSerializer.Deserialize<List<GlobalAppliedTargetRecord>>(json, JsonOptions);
                if (list != null)
                {
                    _records.Clear();
                    foreach (var rec in list)
                    {
                        if (!string.IsNullOrWhiteSpace(rec.CanonicalTarget))
                            _records[rec.CanonicalTarget] = rec;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GlobalAppliedStateManager] Failed to load applied state from {filePath}: {ex.Message}");
            }
            return false;
        }

        private void SaveToDisk()
        {
            lock (_fileLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_storageFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var container = new GlobalAppliedStateContainer
                    {
                        SchemaVersion = 2,
                        LastSavedUtc = DateTime.UtcNow,
                        HardwareFingerprint = Environment.MachineName,
                        OsVersion = Environment.OSVersion.VersionString,
                        Records = _records.Values.ToList()
                    };

                    byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(container, JsonOptions);
                    string tempPath = _storageFilePath + ".tmp";

                    // 1. Write to temporary file with WriteThrough flag to force immediate physical disk flush
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        fs.Write(utf8Bytes, 0, utf8Bytes.Length);
                        fs.Flush(flushToDisk: true);
                    }

                    // 2. Atomic file replacement with automatic backup preservation
                    if (File.Exists(_storageFilePath))
                    {
                        File.Replace(tempPath, _storageFilePath, _backupFilePath, true);
                    }
                    else
                    {
                        File.Move(tempPath, _storageFilePath);
                    }

                    try
                    {
                        File.Copy(_storageFilePath, _backupFilePath, overwrite: true);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GlobalAppliedStateManager] Failed to save applied state atomically: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Evaluates the authoritative live applied-state of a target by reading live OS state
        /// and cross-referencing with Error Optimizer transaction history and GlobalSystemSettingRegistry policies.
        /// Never trusts stale persisted state without verifying against the live system configuration.
        /// </summary>
        public GlobalAppliedStatus EvaluateTargetState(
            string canonicalTarget,
            string expectedOptimalValue,
            out string currentObservedValue)
            => EvaluateTargetState(canonicalTarget, expectedOptimalValue, out currentObservedValue, null);

        public GlobalAppliedStatus EvaluateTargetState(
            string canonicalTarget,
            string expectedOptimalValue,
            out string currentObservedValue,
            string? handlerObservedValue)
        {
            currentObservedValue = ReadLiveSystemValue(canonicalTarget);
            if (string.IsNullOrWhiteSpace(currentObservedValue) && !string.IsNullOrWhiteSpace(handlerObservedValue))
            {
                currentObservedValue = handlerObservedValue;
            }

            bool hasRecord = _records.TryGetValue(canonicalTarget, out var record);
            bool isPreviouslyAppliedByOptimizer = hasRecord && record != null &&
                (string.Equals(record.StateClassification, "AppliedAndVerified", StringComparison.OrdinalIgnoreCase) ||
                 (string.IsNullOrWhiteSpace(record.StateClassification) && (record.RollbackAvailable || !string.IsNullOrWhiteSpace(record.TransactionId))));

            // Normalize strings for comparison
            string obsNorm = (currentObservedValue ?? string.Empty).Trim();
            string expNorm = (expectedOptimalValue ?? string.Empty).Trim();

            // 1. Authoritative Policy Profile Evaluation
            if (GlobalSystemSettingRegistry.Instance.TryGetPolicy(canonicalTarget, out var policy))
            {
                if (policy.IsProfileDependent)
                {
                    foreach (var kvp in policy.ProfileValues)
                    {
                        if (IsValueEquivalent(obsNorm, kvp.Value, canonicalTarget))
                        {
                            if (IsValueEquivalent(obsNorm, expNorm, canonicalTarget))
                            {
                                if (hasRecord && record != null)
                                {
                                    record.LastVerifiedUtc = DateTime.UtcNow;
                                    record.CurrentObservedValue = currentObservedValue ?? string.Empty;
                                    record.VerificationStatus = "VERIFIED";
                                    return isPreviouslyAppliedByOptimizer
                                        ? GlobalAppliedStatus.ALREADY_APPLIED
                                        : GlobalAppliedStatus.ALREADY_OPTIMAL;
                                }
                                return GlobalAppliedStatus.ACTIVE_PROFILE;
                            }
                            else
                            {
                                return GlobalAppliedStatus.PROFILE_TRANSITION_REQUIRED;
                            }
                        }
                    }
                }
            }

            // 2. Standard Optimal Value Evaluation against live OS state
            bool isOptimal = IsValueEquivalent(obsNorm, expNorm, canonicalTarget);

            if (isOptimal)
            {
                if (hasRecord && record != null)
                {
                    record.LastVerifiedUtc = DateTime.UtcNow;
                    record.CurrentObservedValue = currentObservedValue ?? string.Empty;
                    record.VerificationStatus = "VERIFIED";
                    return isPreviouslyAppliedByOptimizer
                        ? GlobalAppliedStatus.ALREADY_APPLIED
                        : GlobalAppliedStatus.ALREADY_OPTIMAL;
                }
                return GlobalAppliedStatus.ALREADY_OPTIMAL;
            }

            if (hasRecord && record != null)
            {
                // Live system configuration differs from desired optimal state: discard stale optimal observation
                // or mark previously optimizer-applied record as EXTERNALLY_MODIFIED so it re-evaluates as eligible.
                record.CurrentObservedValue = currentObservedValue ?? string.Empty;
                record.VerificationStatus = "CHANGED";
                record.StateClassification = "ExternallyModified";

                if (!isPreviouslyAppliedByOptimizer)
                {
                    // Stale pre-existing AlreadyOptimal observation — remove so fresh scan cleanly re-evaluates
                    _records.TryRemove(canonicalTarget, out _);
                    SaveToDisk();
                    return GlobalAppliedStatus.NOT_APPLIED;
                }

                SaveToDisk();
                return GlobalAppliedStatus.EXTERNALLY_MODIFIED;
            }

            return GlobalAppliedStatus.NOT_APPLIED;
        }

        /// <summary>
        /// Determines whether a requesting module is authorized to execute on a target.
        /// Prevents duplicate execution, conflicting overwrites, and unauthorized cross-category mutations.
        /// Independently enforces module ownership, Defender protection rules, and live OS state.
        /// </summary>
        public bool CanExecute(
            string requestingModule,
            string canonicalTarget,
            string targetValue,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;

            if (string.IsNullOrWhiteSpace(canonicalTarget))
            {
                rejectionReason = "Target identifier cannot be empty.";
                return false;
            }

            // 0. GlobalSystemSettingRegistry Policy & Conflict Gate
            if (!GlobalSystemSettingRegistry.Instance.ValidateExecution(requestingModule, canonicalTarget, targetValue, out string regRejection))
            {
                rejectionReason = regRejection;
                return false;
            }

            // 1. Authoritative Category Ownership Gate
            if (!GlobalOptimizationRegistry.Instance.IsAuthoritativeForCategory(canonicalTarget, requestingModule))
            {
                string authOwner = GlobalOptimizationRegistry.Instance.GetAuthoritativeOwner(canonicalTarget);
                rejectionReason = $"Module '{requestingModule}' is not authorized to modify target '{canonicalTarget}'. Authoritative owner is '{authOwner}'.";
                return false;
            }

            // 2. Defender / Core System Protected Rules Gate
            var (isBlocked, protectReason) = OptimizationSettingOwnership.Instance.EvaluateDefenderProtection(canonicalTarget);
            if (isBlocked)
            {
                rejectionReason = $"Target '{canonicalTarget}' violates protected rules: {protectReason}";
                return false;
            }

            // 3. Live System State & Duplicate Execution Gate
            var status = EvaluateTargetState(canonicalTarget, targetValue, out var currentVal);
            if (status == GlobalAppliedStatus.ALREADY_APPLIED ||
                status == GlobalAppliedStatus.ALREADY_OPTIMAL ||
                status == GlobalAppliedStatus.ACTIVE_PROFILE)
            {
                string owner = _records.TryGetValue(canonicalTarget, out var r) ? r.OwningCategory : requestingModule;
                rejectionReason = $"Target is already optimal ({canonicalTarget} = '{currentVal}', owner: '{owner}'). Duplicate execution prevented.";
                return false;
            }

            if (status == GlobalAppliedStatus.CONFLICT_DETECTED ||
                status == GlobalAppliedStatus.CONFIGURATION_CONFLICT)
            {
                rejectionReason = $"Target has an active configuration conflict on {canonicalTarget}.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Records an applied optimization into the authoritative global state store.
        /// </summary>
        public void RecordApplied(
            string canonicalTarget,
            string canonicalId,
            string owningCategory,
            string appliedByModule,
            string previousValue,
            string appliedValue,
            bool verified,
            bool rollbackAvailable,
            string transactionId = "")
        {
            RecordApplied(canonicalTarget, canonicalId, owningCategory, appliedByModule, previousValue, appliedValue, verified, rollbackAvailable, transactionId, "SetRegistryValue");
        }

        /// <summary>
        /// Records an applied optimization into the authoritative global state store with explicit operation.
        /// </summary>
        public void RecordApplied(
            string canonicalTarget,
            string canonicalId,
            string owningCategory,
            string appliedByModule,
            string previousValue,
            string appliedValue,
            bool verified,
            bool rollbackAvailable,
            string transactionId,
            string operation)
        {
            if (string.IsNullOrWhiteSpace(canonicalTarget)) return;

            var rec = new GlobalAppliedTargetRecord
            {
                CanonicalTarget = canonicalTarget,
                CanonicalId = canonicalId,
                OwningCategory = owningCategory,
                AppliedByModule = appliedByModule,
                Operation = operation,
                IntendedValue = appliedValue ?? string.Empty,
                PreviousValue = previousValue ?? string.Empty,
                AppliedValue = appliedValue ?? string.Empty,
                CurrentObservedValue = appliedValue ?? string.Empty,
                AppliedTimestampUtc = DateTime.UtcNow,
                LastVerifiedUtc = DateTime.UtcNow,
                VerificationStatus = verified ? "VERIFIED" : "UNVERIFIED",
                StateClassification = verified ? "AppliedAndVerified" : "Unverified",
                ApplicabilityContext = $"{Environment.MachineName}|{Environment.OSVersion.Version}",
                RollbackAvailable = rollbackAvailable,
                TransactionId = transactionId,
                HardwareFingerprint = Environment.MachineName,
                OsVersion = Environment.OSVersion.VersionString
            };

            _records[canonicalTarget] = rec;
            SaveToDisk();
        }

        /// <summary>
        /// Persists a verified pre-existing AlreadyOptimal observation without misclassifying it as AppliedAndVerified.
        /// If an existing AppliedAndVerified record with rollback metadata already exists for this canonical target,
        /// preserves its rollback metadata and refreshes its verification timestamp &amp; observed value.
        /// </summary>
        public void RecordObservedOptimal(
            string canonicalTarget,
            string canonicalId,
            string owningCategory,
            string observedByModule,
            string observedValue,
            string targetOptimalValue,
            string operation = "SetRegistryValue",
            string applicabilityContext = "")
        {
            if (string.IsNullOrWhiteSpace(canonicalTarget)) return;

            if (_records.TryGetValue(canonicalTarget, out var existing))
            {
                string resolvedObs = !string.IsNullOrWhiteSpace(observedValue) ? observedValue : (targetOptimalValue ?? string.Empty);
                if (string.Equals(existing.StateClassification, "AppliedAndVerified", StringComparison.OrdinalIgnoreCase) &&
                    existing.RollbackAvailable)
                {
                    bool changed = !string.Equals(existing.CurrentObservedValue, resolvedObs, StringComparison.OrdinalIgnoreCase) ||
                                   !string.Equals(existing.VerificationStatus, "VERIFIED", StringComparison.OrdinalIgnoreCase);
                    existing.CurrentObservedValue = resolvedObs;
                    existing.LastVerifiedUtc = DateTime.UtcNow;
                    existing.VerificationStatus = "VERIFIED";
                    if (changed) SaveToDisk();
                    return;
                }

                if (string.Equals(existing.StateClassification, "AlreadyOptimal", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.CurrentObservedValue, resolvedObs, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.IntendedValue, targetOptimalValue ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.VerificationStatus, "VERIFIED_OPTIMAL", StringComparison.OrdinalIgnoreCase))
                {
                    existing.LastVerifiedUtc = DateTime.UtcNow;
                    return;
                }
            }

            var rec = new GlobalAppliedTargetRecord
            {
                CanonicalTarget = canonicalTarget,
                CanonicalId = canonicalId,
                OwningCategory = owningCategory,
                AppliedByModule = observedByModule,
                Operation = operation,
                IntendedValue = targetOptimalValue ?? string.Empty,
                PreviousValue = observedValue ?? string.Empty,
                AppliedValue = string.Empty, // No mutation was performed by optimizer
                CurrentObservedValue = !string.IsNullOrWhiteSpace(observedValue) ? observedValue : (targetOptimalValue ?? string.Empty),
                AppliedTimestampUtc = DateTime.UtcNow,
                LastVerifiedUtc = DateTime.UtcNow,
                VerificationStatus = "VERIFIED_OPTIMAL",
                StateClassification = "AlreadyOptimal",
                ApplicabilityContext = !string.IsNullOrWhiteSpace(applicabilityContext)
                    ? applicabilityContext
                    : $"{Environment.MachineName}|{Environment.OSVersion.Version}",
                RollbackAvailable = false,
                TransactionId = string.Empty,
                HardwareFingerprint = Environment.MachineName,
                OsVersion = Environment.OSVersion.VersionString
            };

            _records[canonicalTarget] = rec;
            SaveToDisk();
        }

        /// <summary>
        /// Records a rollback operation, removing or updating the target record.
        /// </summary>
        public void RecordRollback(string canonicalTarget, string restoredValue)
        {
            if (string.IsNullOrWhiteSpace(canonicalTarget)) return;
            if (_records.TryRemove(canonicalTarget, out _))
            {
                SaveToDisk();
            }
        }

        /// <summary>
        /// Retrieves all tracked applied target records.
        /// </summary>
        public IReadOnlyList<GlobalAppliedTargetRecord> GetAllRecords()
        {
            return _records.Values.ToList();
        }

        public bool TryGetRecord(string canonicalTarget, out GlobalAppliedTargetRecord? record)
        {
            return _records.TryGetValue(canonicalTarget, out record);
        }

        public bool IsApplied(string targetOrId)
        {
            if (string.IsNullOrWhiteSpace(targetOrId)) return false;
            if (_records.ContainsKey(targetOrId)) return true;
            return _records.Values.Any(r => 
                string.Equals(r.CanonicalId, targetOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.CanonicalTarget, targetOrId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Optional read-only hook for deterministic regression testing (simulates live OS state changes without mutating host PC).
        /// </summary>
        public static Func<string, string?>? LiveValueOverrideHook { get; set; }

        private static bool TryParseLongOrHex(string val, out long result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(val)) return false;
            string s = val.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out ulong uhex))
                {
                    result = unchecked((long)uhex);
                    return true;
                }
                return false;
            }
            if (long.TryParse(s, out result)) return true;
            if (ulong.TryParse(s, out ulong udec))
            {
                result = unchecked((long)udec);
                return true;
            }
            return false;
        }

        private static string NormalizeSemanticToken(string val, string canonicalTarget)
        {
            string s = (val ?? string.Empty).Trim();
            if (canonicalTarget.StartsWith("SVC:", StringComparison.OrdinalIgnoreCase))
            {
                if (s.Equals("3", StringComparison.OrdinalIgnoreCase) || s.Equals("Demand", StringComparison.OrdinalIgnoreCase) || s.Equals("DemandStart", StringComparison.OrdinalIgnoreCase) || s.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                    return "Manual";
                if (s.Equals("2", StringComparison.OrdinalIgnoreCase) || s.Equals("Auto", StringComparison.OrdinalIgnoreCase) || s.Equals("AutoStart", StringComparison.OrdinalIgnoreCase) || s.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
                    return "Automatic";
                if (s.Equals("4", StringComparison.OrdinalIgnoreCase) || s.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                    return "Disabled";
            }
            if (canonicalTarget.StartsWith("TASK:", StringComparison.OrdinalIgnoreCase))
            {
                if (s.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || s.Equals("1", StringComparison.OrdinalIgnoreCase) || s.Equals("False", StringComparison.OrdinalIgnoreCase))
                    return "Disabled";
                if (s.Equals("Enabled", StringComparison.OrdinalIgnoreCase) || s.Equals("Ready", StringComparison.OrdinalIgnoreCase) || s.Equals("Running", StringComparison.OrdinalIgnoreCase) || s.Equals("Queued", StringComparison.OrdinalIgnoreCase) || s.Equals("True", StringComparison.OrdinalIgnoreCase))
                    return "Enabled";
            }
            return s;
        }

        private static bool IsValueEquivalent(string val1, string val2, string canonicalTarget)
        {
            if (string.IsNullOrWhiteSpace(val1) && string.IsNullOrWhiteSpace(val2))
                return false;
            if (string.IsNullOrWhiteSpace(val1) || string.IsNullOrWhiteSpace(val2))
                return false;

            string n1 = NormalizeSemanticToken(val1, canonicalTarget);
            string n2 = NormalizeSemanticToken(val2, canonicalTarget);

            if (string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase))
                return true;

            // Handle decimal vs hex and signed/unsigned 32-bit DWORD representations (e.g. 38 vs 0x26, -1 vs 0xFFFFFFFF vs 4294967295)
            if (TryParseLongOrHex(n1, out long l1) && TryParseLongOrHex(n2, out long l2))
            {
                if (l1 == l2) return true;
                if (unchecked((uint)l1) == unchecked((uint)l2)) return true;
            }

            // Boolean / string representations
            if ((n1 == "1" || n1.Equals("True", StringComparison.OrdinalIgnoreCase)) &&
                (n2 == "1" || n2.Equals("True", StringComparison.OrdinalIgnoreCase)))
                return true;

            if ((n1 == "0" || n1.Equals("False", StringComparison.OrdinalIgnoreCase)) &&
                (n2 == "0" || n2.Equals("False", StringComparison.OrdinalIgnoreCase)))
                return true;

            // AppX Package representations
            if ((n1.Equals("Removed", StringComparison.OrdinalIgnoreCase) || n1.Equals("Not Installed", StringComparison.OrdinalIgnoreCase)) &&
                (n2.Equals("Removed", StringComparison.OrdinalIgnoreCase) || n2.Equals("Not Installed", StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        public static string ReadLiveSystemValue(string canonicalTarget)
        {
            if (LiveValueOverrideHook != null)
            {
                var mocked = LiveValueOverrideHook(canonicalTarget);
                if (mocked != null) return mocked;
            }

            try
            {
                if (canonicalTarget.StartsWith("REG:", StringComparison.OrdinalIgnoreCase))
                {
                    string path = canonicalTarget[4..];
                    int slashIdx = path.IndexOf('\\');
                    if (slashIdx == -1) return string.Empty;

                    string rootStr = path[..slashIdx].ToUpperInvariant();
                    string rest = path[(slashIdx + 1)..];

                    int lastSlash = rest.LastIndexOf('\\');
                    if (lastSlash == -1) return string.Empty;

                    string subKey = rest[..lastSlash];
                    string valName = rest[(lastSlash + 1)..];

                    RegistryKey? baseKey = rootStr switch
                    {
                        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
                        "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
                        "HKCR" or "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
                        "HKU" or "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
                        _ => null
                    };

                    if (baseKey == null) return string.Empty;

                    using (baseKey)
                    using (var key = baseKey.OpenSubKey(subKey, false))
                    {
                        if (key == null) return string.Empty;
                        var val = key.GetValue(valName);
                        if (val == null) return string.Empty;

                        if (val is byte[] bytes)
                        {
                            return BitConverter.ToString(bytes).Replace("-", " ");
                        }
                        if (val is string[] strArr)
                        {
                            return string.Join(";", strArr);
                        }

                        return val.ToString() ?? string.Empty;
                    }
                }
                else if (canonicalTarget.StartsWith("SVC:", StringComparison.OrdinalIgnoreCase))
                {
                    string svcName = canonicalTarget[4..];
                    // Read Startup type from registry
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svcName}", false);
                    if (key != null)
                    {
                        int start = Convert.ToInt32(key.GetValue("Start", -1));
                        return start switch
                        {
                            2 => "Automatic",
                            3 => "Manual",
                            4 => "Disabled",
                            _ => start.ToString()
                        };
                    }
                    try
                    {
                        using var sc = new ServiceController(svcName);
                        return sc.Status.ToString();
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }
                else if (canonicalTarget.StartsWith("APPX:", StringComparison.OrdinalIgnoreCase))
                {
                    string pkgName = canonicalTarget[5..];
                    // Authoritative current-user AppX registration check (matches RemoveAppxPackage scope)
                    using var appxKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages", false);
                    if (appxKey != null)
                    {
                        var names = appxKey.GetSubKeyNames();
                        if (names.Any(n => n.Contains(pkgName, StringComparison.OrdinalIgnoreCase)))
                            return "Installed";
                        return "Removed";
                    }
                    return string.Empty;
                }
                else if (canonicalTarget.StartsWith("POWER:", StringComparison.OrdinalIgnoreCase))
                {
                    var active = BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.GetActiveSchemeAsync().GetAwaiter().GetResult();
                    return string.IsNullOrEmpty(active.guid) ? "Balanced" : active.guid;
                }
                else if (canonicalTarget.StartsWith("TASK:", StringComparison.OrdinalIgnoreCase))
                {
                    string taskPath = canonicalTarget[5..].Trim();
                    if (!taskPath.StartsWith('\\')) taskPath = "\\" + taskPath;

                    // Authoritative Windows Task Scheduler COM query (matches WindowsScheduledTaskManager)
                    try
                    {
                        Type? schedType = Type.GetTypeFromProgID("Schedule.Service");
                        if (schedType != null)
                        {
                            dynamic scheduler = Activator.CreateInstance(schedType)!;
                            scheduler.Connect();

                            int lastSlash = taskPath.LastIndexOf('\\');
                            if (lastSlash >= 0)
                            {
                                string folderPath = lastSlash == 0 ? "\\" : taskPath[..lastSlash];
                                string taskName = taskPath[(lastSlash + 1)..];
                                dynamic folder = scheduler.GetFolder(folderPath);
                                dynamic task = folder.GetTask(taskName);

                                int comState = (int)task.State; // 1 = Disabled, 2 = Queued, 3 = Ready, 4 = Running
                                bool enabled = (bool)task.Enabled;
                                if (comState == 1 || !enabled) return "Disabled";
                                if (comState == 2 || comState == 3 || comState == 4) return "Enabled";
                            }
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        return "Disabled";
                    }
                    catch
                    {
                        // Never falsely claim "Enabled" if COM query failed; return empty so handler readback resolves live state
                    }

                    return string.Empty;
                }
                else if (canonicalTarget.StartsWith("INPUT:", StringComparison.OrdinalIgnoreCase))
                {
                    string setting = canonicalTarget[6..];
                    if (setting.Equals("MOUSE_SPEED", StringComparison.OrdinalIgnoreCase))
                    {
                        if (SystemParametersInfo(0x0070 /* SPI_GETMOUSESPEED */, 0, out int speed, 0))
                            return speed.ToString();
                    }
                    return string.Empty;
                }
            }
            catch
            {
                // Live reading gracefully returns empty on permission or missing target
            }

            return string.Empty;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);
    }
}
