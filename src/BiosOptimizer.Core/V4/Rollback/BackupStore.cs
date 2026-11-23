#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using BiosOptimizer.Core.Common;
using BiosOptimizer.Core.Services;

namespace BiosOptimizer.Core.V4.Rollback
{
    public sealed record BackupEntry(
        string Id,
        string Target,
        string SourcePath,
        string BackupPath,
        DateTimeOffset CreatedUtc,
        long SizeBytes,
        string Sha256,
        string SessionId,
        string Component);

    public sealed record BackupContext(
        string Component,
        string SessionId,
        string? EmulatorKind = null,
        string? Version = null,
        string? Target = null);

    public sealed class SessionManifest
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public bool RollbackApplied { get; set; }
        public List<string> BackupsCreated { get; set; } = new();
    }

    public static class BackupStore
    {
        private static string? _customRoot;
        private static readonly object _fileLock = new();

        private static List<BackupEntry>? _cachedIndex;
        private static DateTime _cachedIndexWriteUtc = DateTime.MinValue;
        private static long _cachedIndexLength = -1;
        private static DateTime _cachedIndexCapturedUtc = DateTime.MinValue;
        private static readonly TimeSpan IndexCacheTtl = TimeSpan.FromSeconds(30);

        public static void InvalidateCache()
        {
            lock (_fileLock)
            {
                _cachedIndex = null;
                _cachedIndexWriteUtc = DateTime.MinValue;
                _cachedIndexLength = -1;
                _cachedIndexCapturedUtc = DateTime.MinValue;
            }
        }

        public static string Root => _customRoot ?? AppEnvironment.BackupRoot;

        public static void SetCustomRootForTesting(string? root)
        {
            lock (_fileLock)
            {
                _customRoot = root;
                InvalidateCache();
            }
        }

        private static string GetIndexPath() => Path.Combine(Root, "index.json");
        private static string GetManifestsPath() => Path.Combine(Root, "manifests");
        private static string GetLogPath() => Path.Combine(Root, "backups.log");

        private static void EnsureDirectories()
        {
            if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);
            if (!Directory.Exists(GetManifestsPath())) Directory.CreateDirectory(GetManifestsPath());
        }

        public static BackupEntry Create(string sourcePath, BackupContext ctx)
        {
            try
            {
                EnsureDirectories();
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException($"Source file not found for backup: '{sourcePath}'", sourcePath);
                }

                string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
                string fileName = Path.GetFileName(sourcePath);
                string targetName = ctx.Target ?? fileName;

                // Determine subfolder structure
                string relDir;
                string destFileName;

                if (ctx.Component.Contains("emulator", StringComparison.OrdinalIgnoreCase) || ctx.EmulatorKind != null)
                {
                    string emu = ctx.EmulatorKind ?? "BlueStacks5";
                    if (fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        relDir = Path.Combine("emulator", "FreeFire", "apk", ctx.Version ?? "unknown");
                        destFileName = $"{fileName}.v4bak";
                    }
                    else if (fileName.EndsWith(".obb", StringComparison.OrdinalIgnoreCase))
                    {
                        relDir = Path.Combine("emulator", "FreeFire", "obb", ctx.Version ?? "unknown");
                        destFileName = $"{fileName}.v4bak";
                    }
                    else if (fileName.EndsWith(".vdi", StringComparison.OrdinalIgnoreCase))
                    {
                        relDir = Path.Combine("emulator", emu, "vdi");
                        destFileName = $"{fileName}.v4bak";
                    }
                    else if (fileName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    {
                        relDir = Path.Combine("emulator", emu, fileName);
                        destFileName = $"{timestamp}.v4bak";
                    }
                    else
                    {
                        relDir = Path.Combine("emulator", emu);
                        destFileName = $"{fileName}.{timestamp}.v4bak";
                    }
                }
                else if (ctx.Component.StartsWith("v4", StringComparison.OrdinalIgnoreCase))
                {
                    relDir = Path.Combine(ctx.Component, ctx.Target ?? "misc", ctx.SessionId);
                    destFileName = $"{fileName}.{timestamp}.v4bak";
                }
                else if (ctx.Component.StartsWith("v3", StringComparison.OrdinalIgnoreCase))
                {
                    relDir = Path.Combine("v3", ctx.SessionId);
                    destFileName = $"{fileName}.{timestamp}.v4bak";
                }
                else
                {
                    relDir = Path.Combine(ctx.Component, ctx.SessionId);
                    destFileName = $"{fileName}.{timestamp}.v4bak";
                }

                string targetDir = Path.Combine(Root, relDir);
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                string backupPath = Path.Combine(targetDir, destFileName);
                string tempBackupPath = $"{backupPath}.{Environment.ProcessId}.tmp";
                try
                {
                    File.Copy(sourcePath, tempBackupPath, overwrite: true);
                    if (File.Exists(backupPath))
                        File.Replace(tempBackupPath, backupPath, null, ignoreMetadataErrors: true);
                    else
                        File.Move(tempBackupPath, backupPath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tempBackupPath)) File.Delete(tempBackupPath); } catch { }
                }

                var fileInfo = new FileInfo(backupPath);
                string sha256 = ComputeSha256(backupPath);
                string backupId = Guid.NewGuid().ToString("N");

                var entry = new BackupEntry(
                    Id: backupId,
                    Target: targetName,
                    SourcePath: Path.GetFullPath(sourcePath),
                    BackupPath: Path.GetFullPath(backupPath),
                    CreatedUtc: DateTimeOffset.UtcNow,
                    SizeBytes: fileInfo.Length,
                    Sha256: sha256,
                    SessionId: ctx.SessionId,
                    Component: ctx.Component);

                AppendToManifest(entry);
                AppendToIndex(entry);
                LogAction($"CREATE Id={entry.Id} Target={entry.Target} Source={entry.SourcePath} Backup={entry.BackupPath}");

                string displayKind = ctx.EmulatorKind switch
                {
                    "MsiAppPlayer5" => "MSI App Player 5",
                    "BlueStacks5" => "BlueStacks 5",
                    _ => ctx.EmulatorKind ?? ctx.Component
                };

                try
                {
                    ToastService.NotifyBackupSuccess(
                        displayKind,
                        Path.GetFileName(sourcePath),
                        fileInfo.Length);
                }
                catch (Exception ex)
                {
                    try
                    {
                        string backupLog = GetLogPath();
                        File.AppendAllText(backupLog, $"Toast failed: {ex.Message}{Environment.NewLine}");
                    }
                    catch { }
                }

                return entry;
            }
            catch (Exception ex)
            {
                try
                {
                    ToastService.NotifyBackupFailure(ex.Message);
                }
                catch (Exception toastEx)
                {
                    try
                    {
                        string backupLog = GetLogPath();
                        File.AppendAllText(backupLog, $"Toast failed: {toastEx.Message}{Environment.NewLine}");
                    }
                    catch { }
                }
                throw;
            }
        }

        public static void Restore(string backupId)
        {
            EnsureDirectories();
            var entries = LoadIndex();
            var entry = entries.FirstOrDefault(e => string.Equals(e.Id, backupId, StringComparison.OrdinalIgnoreCase) ||
                                                    string.Equals(e.BackupPath, backupId, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                throw new FileNotFoundException($"Backup entry '{backupId}' not found in store index");
            }

            if (!File.Exists(entry.BackupPath))
            {
                throw new FileNotFoundException($"Backup archive file missing: '{entry.BackupPath}'", entry.BackupPath);
            }

            // Verify integrity
            string currentSha = ComputeSha256(entry.BackupPath);
            if (!string.Equals(currentSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup integrity check failed for '{entry.Id}': SHA256 mismatch");
            }

            string? sourceDir = Path.GetDirectoryName(entry.SourcePath);
            if (!string.IsNullOrEmpty(sourceDir) && !Directory.Exists(sourceDir))
            {
                Directory.CreateDirectory(sourceDir);
            }

            File.Copy(entry.BackupPath, entry.SourcePath, overwrite: true);
            LogAction($"RESTORE Id={entry.Id} Target={entry.Target} RestoredTo={entry.SourcePath}");
        }

        public static (bool Valid, string Reason, BackupEntry? Entry) Validate(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                return (false, "Backup ID is required.", null);

            EnsureDirectories();
            var entries = LoadIndex();
            var entry = entries.FirstOrDefault(e =>
                string.Equals(e.Id, backupId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.BackupPath, backupId, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                return (false, $"Backup snapshot '{backupId}' was not found in the store index.", null);

            if (!File.Exists(entry.BackupPath))
                return (false, $"Backup snapshot archive file is missing: '{entry.BackupPath}'.", entry);

            if (entry.SizeBytes > 0 && new FileInfo(entry.BackupPath).Length != entry.SizeBytes)
                return (false, $"Integrity check failed for '{entry.Id}': archive size mismatch.", entry);

            if (string.IsNullOrWhiteSpace(entry.Sha256))
                return (false, $"Integrity check failed for '{entry.Id}': missing SHA-256 checksum.", entry);

            string currentSha = ComputeSha256(entry.BackupPath);
            if (!string.Equals(currentSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return (false, $"Integrity check failed for '{entry.Id}': SHA-256 mismatch.", entry);

            return (true, "SHA-256 archive integrity verified.", entry);
        }

        public static (bool Deleted, string Reason) Delete(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                return (false, "Backup ID is required.");

            EnsureDirectories();
            return WithStoreLock(() =>
            {
                var entries = LoadIndex();
                var entry = entries.FirstOrDefault(e =>
                    string.Equals(e.Id, backupId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.BackupPath, backupId, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                    return (false, $"Backup snapshot '{backupId}' was not found.");

                if (!string.IsNullOrWhiteSpace(entry.BackupPath) && File.Exists(entry.BackupPath))
                {
                    File.Delete(entry.BackupPath);
                }

                entries.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                SaveIndex(entries);

                if (!string.IsNullOrWhiteSpace(entry.SessionId))
                {
                    var manifest = LoadManifest(entry.SessionId);
                    if (manifest != null && manifest.BackupsCreated.RemoveAll(id => string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase)) > 0)
                    {
                        SaveManifest(manifest);
                    }
                }

                LogAction($"DELETE Id={entry.Id} Target={entry.Target} DeletedPath={entry.BackupPath}");
                return (true, $"Backup snapshot '{entry.Id}' deleted cleanly.");
            });
        }

        public static (int DeletedCount, int FailedCount, long FreedBytes) DeleteMultiple(IEnumerable<string> backupIds)
        {
            if (backupIds == null) return (0, 0, 0);
            var idSet = new HashSet<string>(backupIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (idSet.Count == 0) return (0, 0, 0);

            EnsureDirectories();
            return WithStoreLock(() =>
            {
                var entries = LoadIndex();
                int deleted = 0;
                int failed = 0;
                long freedBytes = 0;
                var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var affectedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries)
                {
                    if (idSet.Contains(entry.Id) || (!string.IsNullOrEmpty(entry.BackupPath) && idSet.Contains(entry.BackupPath)))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(entry.BackupPath) && File.Exists(entry.BackupPath))
                            {
                                var fi = new FileInfo(entry.BackupPath);
                                freedBytes += fi.Length;
                                File.Delete(entry.BackupPath);
                            }
                            toRemove.Add(entry.Id);
                            if (!string.IsNullOrWhiteSpace(entry.SessionId))
                            {
                                affectedSessions.Add(entry.SessionId);
                            }
                            deleted++;
                            LogAction($"DELETE_MULTI Id={entry.Id} Target={entry.Target} DeletedPath={entry.BackupPath}");
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                }

                if (toRemove.Count > 0)
                {
                    entries.RemoveAll(e => toRemove.Contains(e.Id));
                    SaveIndex(entries);

                    foreach (var sessionId in affectedSessions)
                    {
                        var manifest = LoadManifest(sessionId);
                        if (manifest != null && manifest.BackupsCreated.RemoveAll(id => toRemove.Contains(id)) > 0)
                        {
                            SaveManifest(manifest);
                        }
                    }
                }

                return (deleted, failed, freedBytes);
            });
        }

        public static (int DeletedCount, int FailedCount, long FreedBytes) DeleteAll()
        {
            EnsureDirectories();
            return WithStoreLock(() =>
            {
                var entries = LoadIndex();
                int deleted = 0;
                int failed = 0;
                long freedBytes = 0;

                foreach (var entry in entries)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(entry.BackupPath) && File.Exists(entry.BackupPath))
                        {
                            var fi = new FileInfo(entry.BackupPath);
                            freedBytes += fi.Length;
                            File.Delete(entry.BackupPath);
                        }
                        deleted++;
                    }
                    catch
                    {
                        failed++;
                    }
                }

                SaveIndex(new List<BackupEntry>());

                // Also clean up manifests
                try
                {
                    string manifestsDir = GetManifestsPath();
                    if (Directory.Exists(manifestsDir))
                    {
                        foreach (var mFile in Directory.GetFiles(manifestsDir, "*.json"))
                        {
                            try { File.Delete(mFile); } catch { }
                        }
                    }
                }
                catch { }

                LogAction($"DELETE_ALL DeletedCount={deleted} FreedBytes={freedBytes}");
                return (deleted, failed, freedBytes);
            });
        }

        public static IReadOnlyList<BackupEntry> List(string? filter = null)
        {
            EnsureDirectories();
            var entries = LoadIndex();
            var seen = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

            // Include legacy backup snapshots non-destructively when migrating from legacy root (skip during isolated custom-root runs)
            if (_customRoot == null)
            {
                foreach (var legacyRoot in AppEnvironment.GetLegacyBackupDirectories())
                {
                    string legacyIdx = Path.Combine(legacyRoot, "index.json");
                    if (!File.Exists(legacyIdx)) continue;
                    try
                    {
                        string json = File.ReadAllText(legacyIdx);
                        var legacyEntries = JsonSerializer.Deserialize<List<BackupEntry>>(json);
                        if (legacyEntries == null) continue;
                        foreach (var le in legacyEntries)
                        {
                            if (!string.IsNullOrWhiteSpace(le.Id) && seen.Add(le.Id))
                                entries.Add(le);
                        }
                    }
                    catch { }
                }
            }

            var ordered = entries.OrderByDescending(e => e.CreatedUtc).ToList();

            if (string.IsNullOrWhiteSpace(filter))
            {
                return ordered;
            }

            return ordered.Where(e =>
                e.Target.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Component.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.SessionId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static T WithStoreLock<T>(Func<T> action)
        {
            Mutex? mutex = null;
            bool acquired = false;
            try
            {
                try
                {
                    mutex = new Mutex(false, @"Global\V4_BackupStore");
                    try { acquired = mutex.WaitOne(2500); }
                    catch (AbandonedMutexException) { acquired = true; }
                }
                catch
                {
                    // Non-elevated session or cross-session ACL on Global mutex: fall back to process lock
                }

                lock (_fileLock)
                {
                    return action();
                }
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            }
        }

        public static void Prune(string? target = null, int keep = 5)
        {
            EnsureDirectories();
            WithStoreLock(() =>
            {
                var entries = LoadIndex();
                var remainingEntries = new List<BackupEntry>();
                var targets = entries.Select(e => e.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var t in targets)
                {
                    if (!string.IsNullOrEmpty(target) && !string.Equals(t, target, StringComparison.OrdinalIgnoreCase))
                    {
                        remainingEntries.AddRange(entries.Where(e => string.Equals(e.Target, t, StringComparison.OrdinalIgnoreCase)));
                        continue;
                    }

                    var group = entries.Where(e => string.Equals(e.Target, t, StringComparison.OrdinalIgnoreCase))
                                       .OrderByDescending(e => e.CreatedUtc)
                                       .ToList();

                    var protectedFromPrune = new List<BackupEntry>();
                    var candidates = new List<BackupEntry>();

                    foreach (var e in group)
                    {
                        var manifest = LoadManifest(e.SessionId);
                        bool isRecentUnrolledSession = manifest != null &&
                            !manifest.RollbackApplied &&
                            (DateTimeOffset.UtcNow - e.CreatedUtc) < TimeSpan.FromHours(24);

                        if (isRecentUnrolledSession)
                        {
                            protectedFromPrune.Add(e);
                        }
                        else
                        {
                            candidates.Add(e);
                        }
                    }

                    var keptCandidates = candidates.Take(keep).ToList();
                    var toDelete = candidates.Skip(keep).ToList();

                    foreach (var del in toDelete)
                    {
                        try
                        {
                            if (File.Exists(del.BackupPath))
                            {
                                File.Delete(del.BackupPath);
                            }
                            LogAction($"PRUNE Id={del.Id} Target={del.Target} DeletedPath={del.BackupPath}");
                        }
                        catch { }
                    }

                    remainingEntries.AddRange(protectedFromPrune);
                    remainingEntries.AddRange(keptCandidates);
                }

                SaveIndex(remainingEntries);
                return 0;
            });
        }

        public static void RecordSessionStart(string sessionId)
        {
            EnsureDirectories();
            string manifestPath = Path.Combine(GetManifestsPath(), $"{sessionId}.json");
            if (!File.Exists(manifestPath))
            {
                var manifest = new SessionManifest
                {
                    SessionId = sessionId,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    BackupsCreated = new List<string>()
                };
                SaveManifest(manifest);
            }
        }

        public static void RecordSessionCompleted(string sessionId)
        {
            EnsureDirectories();
            var manifest = LoadManifest(sessionId) ?? new SessionManifest { SessionId = sessionId };
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            SaveManifest(manifest);
        }

        public static void RecordSessionRollback(string sessionId)
        {
            EnsureDirectories();
            var manifest = LoadManifest(sessionId) ?? new SessionManifest { SessionId = sessionId };
            manifest.RollbackApplied = true;
            SaveManifest(manifest);
        }

        private static void AppendToManifest(BackupEntry entry)
        {
            try
            {
                var manifest = LoadManifest(entry.SessionId) ?? new SessionManifest
                {
                    SessionId = entry.SessionId,
                    CreatedUtc = entry.CreatedUtc,
                    BackupsCreated = new List<string>()
                };
                if (!manifest.BackupsCreated.Contains(entry.Id))
                {
                    manifest.BackupsCreated.Add(entry.Id);
                }
                SaveManifest(manifest);
            }
            catch { }
        }

        private static SessionManifest? LoadManifest(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            string path = Path.Combine(GetManifestsPath(), $"{sessionId}.json");
            if (!File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<SessionManifest>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveManifest(SessionManifest manifest)
        {
            string path = Path.Combine(GetManifestsPath(), $"{manifest.SessionId}.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(manifest, options);
            File.WriteAllText(path, json);
        }

        private static void AppendToIndex(BackupEntry entry)
        {
            WithStoreLock(() =>
            {
                string indexPath = GetIndexPath();
                var list = File.Exists(indexPath) ? LoadIndex() : new List<BackupEntry>();
                list.RemoveAll(e => e.Id == entry.Id || string.Equals(e.BackupPath, entry.BackupPath, StringComparison.OrdinalIgnoreCase));
                list.Add(entry);
                SaveIndex(list);
                return 0;
            });
        }

        private static List<BackupEntry> LoadIndex()
        {
            lock (_fileLock)
            {
                string indexPath = GetIndexPath();
                if (!File.Exists(indexPath))
                {
                    InvalidateCache();
                    return HasBackupArtifactsOnDisk() ? RebuildIndex() : new List<BackupEntry>();
                }

                try
                {
                    var fileInfo = new FileInfo(indexPath);
                    DateTime lastWrite = fileInfo.LastWriteTimeUtc;
                    long length = fileInfo.Length;
                    DateTime now = DateTime.UtcNow;

                    if (_cachedIndex != null &&
                        _cachedIndexWriteUtc == lastWrite &&
                        _cachedIndexLength == length &&
                        (now - _cachedIndexCapturedUtc) < IndexCacheTtl)
                    {
                        return new List<BackupEntry>(_cachedIndex);
                    }

                    string json = File.ReadAllText(indexPath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        InvalidateCache();
                        return HasBackupArtifactsOnDisk() ? RebuildIndex() : new List<BackupEntry>();
                    }

                    var list = JsonSerializer.Deserialize<List<BackupEntry>>(json) ?? new List<BackupEntry>();
                    _cachedIndex = new List<BackupEntry>(list);
                    _cachedIndexWriteUtc = lastWrite;
                    _cachedIndexLength = length;
                    _cachedIndexCapturedUtc = now;
                    return list;
                }
                catch
                {
                    InvalidateCache();
                    return HasBackupArtifactsOnDisk() ? RebuildIndex() : new List<BackupEntry>();
                }
            }
        }

        private static bool HasBackupArtifactsOnDisk()
        {
            try
            {
                return Directory.Exists(Root) &&
                       Directory.EnumerateFiles(Root, "*.v4bak", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        private static void SaveIndex(List<BackupEntry> entries)
        {
            string indexPath = GetIndexPath();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(entries, options);

            string tmp = indexPath + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(indexPath))
                    File.Replace(tmp, indexPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, indexPath, overwrite: true);
            }
            catch
            {
                File.Move(tmp, indexPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                InvalidateCache();
            }
        }

        public static List<BackupEntry> RebuildIndex()
        {
            EnsureDirectories();
            var list = new List<BackupEntry>();

            if (!Directory.Exists(Root)) return list;

            try
            {
                var files = Directory.GetFiles(Root, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals("index.json", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("backups.log", StringComparison.OrdinalIgnoreCase) ||
                        file.Contains(@"\manifests\", StringComparison.OrdinalIgnoreCase) ||
                        file.Contains(@"/manifests/", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var fi = new FileInfo(file);
                        string rel = Path.GetRelativePath(Root, file);
                        string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string component = parts.Length > 0 ? parts[0] : "general";
                        string target = Path.GetFileNameWithoutExtension(name).Replace(".v4bak", "");

                        list.Add(new BackupEntry(
                            Id: Guid.NewGuid().ToString("N"),
                            Target: target,
                            SourcePath: file,
                            BackupPath: Path.GetFullPath(file),
                            CreatedUtc: fi.LastWriteTimeUtc,
                            SizeBytes: fi.Length,
                            Sha256: ComputeSha256(file),
                            SessionId: "rebuilt_session",
                            Component: component
                        ));
                    }
                    catch { }
                }

                SaveIndex(list);
            }
            catch { }

            return list;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void LogAction(string message)
        {
            lock (_fileLock)
            {
                try
                {
                    EnsureDirectories();
                    string logFile = GetLogPath();

                    // Check rotation (5 MB)
                    if (File.Exists(logFile) && new FileInfo(logFile).Length > 5 * 1024 * 1024)
                    {
                        for (int i = 2; i >= 1; i--)
                        {
                            string src = $"{logFile}.{i}";
                            string dst = $"{logFile}.{i + 1}";
                            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
                        }
                        File.Move(logFile, $"{logFile}.1", overwrite: true);
                    }

                    string line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";
                    File.AppendAllText(logFile, line, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
