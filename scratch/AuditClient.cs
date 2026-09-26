using System;
using System.Text.Json;
using System.Collections.Generic;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.ActionHandlers;

namespace AuditClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var record = new TransactionRecord
            {
                TransactionId = "123",
                Snapshots = new List<SnapshotRecord>
                {
                    new ServiceSnapshotRecord { ServiceName = "test", OriginalStartupType = BiosOptimizer.Core.Interfaces.ServiceStartupType.Automatic }
                }
            };

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false };
            var json = JsonSerializer.Serialize(record, options);
            Console.WriteLine("JSON: " + json);

            try {
                var loaded = JsonSerializer.Deserialize<TransactionRecord>(json, options);
                Console.WriteLine("Loaded count: " + loaded?.Snapshots.Count);
                if (loaded?.Snapshots.Count > 0) {
                    Console.WriteLine("Type: " + loaded.Snapshots[0].GetType().Name);
                }
            } catch (Exception ex) {
                Console.WriteLine("Error: " + ex);
            }
        }
    }
}
