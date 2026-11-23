using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using BiosOptimizer.Core.Common;

namespace BiosOptimizer.Service
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Log process start using verified writable data root
            try
            {
                string logDir = AppEnvironment.ResolveWritableDataDirectory("logs");
                string logPath = Path.Combine(logDir, "service.log");
                File.AppendAllText(
                    logPath,
                    $"{DateTime.UtcNow:O} process-start args={string.Join(" ", args)}\n");
            }
            catch
            {
                // Silently ignore logging failures on non-elevated launch
            }

            if (args.Contains("--service"))
            {
                ServiceBase.Run(new ErrorOptimizerService());
            }
            else
            {
                // Manual console mode for debugging
                var svc = new ErrorOptimizerService();
                svc.TestStartupAndStop(args);
            }
        }
    }
}
