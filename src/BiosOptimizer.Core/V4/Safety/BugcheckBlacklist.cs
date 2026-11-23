#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiosOptimizer.Core.V4.Safety
{
    public sealed class BugcheckBlacklistEntry
    {
        [JsonPropertyName("action_signature")]
        public string ActionSignature { get; set; } = string.Empty;

        [JsonPropertyName("action_description")]
        public string ActionDescription { get; set; } = string.Empty;

        [JsonPropertyName("bugcheck_code")]
        public string BugcheckCode { get; set; } = string.Empty;

        [JsonPropertyName("detected_at_utc")]
        public string DetectedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("o");

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;
    }

    public sealed class BugcheckBlacklistDoc
    {
        [JsonPropertyName("entries")]
        public List<BugcheckBlacklistEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Persistent blacklist preventing actions that triggered bugchecks (BSOD) from ever running again.
    /// Stored at %ProgramData%\ErrorOptimizer\bugcheck_blacklist.json.
    /// </summary>
    public class BugcheckBlacklist
    {
        private static readonly Lazy<BugcheckBlacklist> _instance = new(() => new BugcheckBlacklist());
        public static BugcheckBlacklist Instance => _instance.Value;

        private readonly string _filePath;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<string, BugcheckBlacklistEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<BugcheckBlacklistEntry> Entries => _entries.Values.ToList();

        public BugcheckBlacklist(string? customPath = null)
        {
            if (!string.IsNullOrEmpty(customPath))
            {
                _filePath = customPath;
            }
            else
            {
                string dir = BiosOptimizer.Core.Common.AppEnvironment.ResolveWritableDataDirectory();
                _filePath = Path.Combine(dir, "bugcheck_blacklist.json");
            }

            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                _entries.Clear();
                try
                {
                    if (File.Exists(_filePath))
                    {
                        string json = File.ReadAllText(_filePath);
                        var doc = JsonSerializer.Deserialize<BugcheckBlacklistDoc>(json);
                        if (doc?.Entries != null)
                        {
                            foreach (var e in doc.Entries)
                            {
                                if (!string.IsNullOrEmpty(e.ActionSignature))
                                {
                                    _entries[e.ActionSignature] = e;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public void Add(string actionSignature, string actionDescription, string bugcheckCode, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(actionSignature)) return;

            var entry = new BugcheckBlacklistEntry
            {
                ActionSignature = actionSignature.Trim(),
                ActionDescription = actionDescription,
                BugcheckCode = bugcheckCode,
                DetectedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                SessionId = sessionId
            };

            _entries[entry.ActionSignature] = entry;
            Save();
        }

        public void Add(string actionSignature, string bugcheckCode = "0x000000EF")
        {
            Add(actionSignature, "Bugcheck detected", bugcheckCode, "session-0");
        }

        public bool IsBlacklisted(string actionSignature)
        {
            if (string.IsNullOrWhiteSpace(actionSignature)) return false;
            return _entries.ContainsKey(actionSignature.Trim());
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                try
                {
                    if (File.Exists(_filePath))
                    {
                        File.Delete(_filePath);
                    }
                }
                catch { }
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_filePath)!;
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var doc = new BugcheckBlacklistDoc
                    {
                        Entries = _entries.Values.ToList()
                    };

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(doc, options);
                    File.WriteAllText(_filePath, json);
                }
                catch { }
            }
        }
    }
}
