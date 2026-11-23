#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.Json;

namespace BiosOptimizer.Core.V4.Safety
{
    public sealed class LastSessionLog
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset LastActionTimestampUtc { get; set; }
        public List<LastSessionAction> Actions { get; set; } = new();
    }

    public sealed class LastSessionAction
    {
        public string ActionSignature { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; set; }
    }

    public class CrashDetector
    {
        private readonly BugcheckBlacklist _blacklist;
        private readonly string _lastSessionPath;

        public CrashDetector(BugcheckBlacklist? blacklist = null, string? lastSessionPath = null)
        {
            _blacklist = blacklist ?? BugcheckBlacklist.Instance;

            if (!string.IsNullOrEmpty(lastSessionPath))
            {
                _lastSessionPath = lastSessionPath;
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _lastSessionPath = Path.Combine(localAppData, "ErrorOptimizer", "last_session.json");
            }
        }

        public LastSessionLog? LoadLastSession()
        {
            try
            {
                if (File.Exists(_lastSessionPath))
                {
                    string json = File.ReadAllText(_lastSessionPath);
                    return JsonSerializer.Deserialize<LastSessionLog>(json);
                }
            }
            catch { }
            return null;
        }

        public void SaveLastSession(LastSessionLog session)
        {
            try
            {
                string dir = Path.GetDirectoryName(_lastSessionPath)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_lastSessionPath, json);
            }
            catch { }
        }

        /// <summary>
        /// Scans Windows System Event Log for EventID 1001 (BugCheck) within 120s of the last session's actions.
        /// </summary>
        public int CheckForRecentBugchecks(IEnumerable<(string Code, DateTimeOffset Timestamp)>? mockEvents = null)
        {
            var session = LoadLastSession();
            if (session == null || session.Actions.Count == 0) return 0;

            int blacklistedCount = 0;
            var bugcheckEvents = new List<(string Code, DateTimeOffset Timestamp)>();

            if (mockEvents != null)
            {
                bugcheckEvents.AddRange(mockEvents);
            }
            else
            {
                try
                {
                    string query = "*[System[(EventID=1001) and Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting' or @Name='BugCheck']]]";
                    var eventQuery = new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true };
                    using var reader = new EventLogReader(eventQuery);

                    for (var ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
                    {
                        using (ev)
                        {
                            DateTime? timeCreated = ev.TimeCreated;
                            if (timeCreated.HasValue)
                            {
                                string msg = ev.FormatDescription() ?? string.Empty;
                                string code = ExtractBugcheckCode(msg);
                                bugcheckEvents.Add((code, new DateTimeOffset(timeCreated.Value.ToUniversalTime())));
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var bc in bugcheckEvents)
            {
                foreach (var action in session.Actions)
                {
                    double diffSeconds = Math.Abs((bc.Timestamp - action.TimestampUtc).TotalSeconds);
                    if (diffSeconds <= 120.0)
                    {
                        _blacklist.Add(action.ActionSignature, action.Description, bc.Code, session.SessionId);
                        blacklistedCount++;
                    }
                }
            }

            return blacklistedCount;
        }

        public static string ExtractBugcheckCode(string message)
        {
            if (string.IsNullOrEmpty(message)) return "0x00000000";

            string[] knownCodes = {
                "0x000000F4", "0xC000021A", "0x000000EF", "0x000000CE",
                "0x000000D1", "0x0000007E", "0x00000116", "0x0000003B"
            };

            foreach (var code in knownCodes)
            {
                if (message.Contains(code, StringComparison.OrdinalIgnoreCase))
                {
                    return code;
                }
            }

            // General 0x pattern
            int idx = message.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx + 10 <= message.Length)
            {
                return message.Substring(idx, 10);
            }

            return "0x000000F4";
        }
    }
}
