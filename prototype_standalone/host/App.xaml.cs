using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BiosOptimizer.Core.Implementations.Config;
using ErrorOptimizerPrototypeHost.Services;

namespace ErrorOptimizerPrototypeHost;

public partial class App : Application
{
    public static bool IsExplicitExit { get; private set; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void RequestExplicitExit()
    {
        LogStartup("App.RequestExplicitExit() called!");
        IsExplicitExit = true;
        try { HostBackgroundSupervisor.Instance.RunExitMaintenanceIfConfigured(); } catch { }
        try { Current?.Dispatcher.BeginInvoke(() => Current.Shutdown()); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LogStartup("==========================================================");
        LogStartup($"WPF Host OnStartup initiated at {DateTime.UtcNow:o}");
        LogStartup($"Process: {Environment.ProcessPath}");
        LogStartup($"BaseDirectory: {AppContext.BaseDirectory}");
        LogStartup($"CurrentDirectory: {Directory.GetCurrentDirectory()}");
        LogStartup($"Args: {string.Join(" ", e.Args ?? Array.Empty<string>())}");

        string[] args = e.Args ?? Array.Empty<string>();
        bool isBackgroundFlag = args.Any(a =>
            a.Equals("--startup-background", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--background-worker", StringComparison.OrdinalIgnoreCase));
        bool isMinimizedFlag = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        bool isStartupFlag = isBackgroundFlag || args.Any(a =>
            a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        if (!HostBackgroundSupervisor.Instance.TryAcquirePrimaryInstance(signalIfSecondary: !isBackgroundFlag))
        {
            LogStartup("Another instance is already running; signaling primary instance and shutting down secondary.");
            Shutdown(0);
            return;
        }

        try
        {
            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
            {
                var settings = UnifiedSettingsStore.LoadSafe();
                if (!string.Equals(settings.HostExecutablePath, procPath, StringComparison.OrdinalIgnoreCase))
                {
                    settings.HostExecutablePath = procPath;
                    UnifiedSettingsStore.Save(settings);
                }

                if (isStartupFlag && !isBackgroundFlag && settings.StartMinimized)
                    isBackgroundFlag = true;
            }
        }
        catch (Exception ex)
        {
            LogStartup($"Settings check notice: {ex.Message}");
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        LogStartup("Instantiating MainWindow...");
        MainWindow window;
        try
        {
            window = new MainWindow();
            MainWindow = window;
            LogStartup("MainWindow instantiated successfully.");
        }
        catch (Exception ex)
        {
            string logPath = LogDiagnosticException("MainWindow Instantiation Failure", ex);
            MessageBox.Show(
                $"Failed to initialize ERROR OPTIMIZER window:\n\n{ex.Message}\n\nDetails: {ex.InnerException?.Message ?? "None"}\n\nDiagnostic log written to:\n{logPath}",
                "ERROR OPTIMIZER Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        HostBackgroundSupervisor.Instance.Start(
            isStartupFlag,
            isBackgroundFlag || isMinimizedFlag,
            () => window.RestoreFromBackground(),
            RequestExplicitExit);

        if (isBackgroundFlag)
        {
            LogStartup("Running in background mode. Hiding window and ensuring tray icon is visible.");
            HostBackgroundSupervisor.Instance.EnsureTrayVisible();
            return;
        }

        if (isMinimizedFlag)
        {
            LogStartup("Starting in minimized state.");
            window.WindowState = WindowState.Minimized;
        }

        LogStartup("Calling MainWindow.Show()...");
        window.Show();
        LogStartup("MainWindow.Show() executed successfully.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogStartup($"WPF Host exiting with code {e.ApplicationExitCode}");
        try { HostBackgroundSupervisor.Instance.Dispose(); } catch { }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string logPath = LogDiagnosticException("DispatcherUnhandledException", e.Exception);

        string innerMsg = e.Exception.InnerException != null
            ? $"\n\nDetails: {e.Exception.InnerException.GetType().Name}: {e.Exception.InnerException.Message}"
            : string.Empty;

        string xamlMsg = string.Empty;
        if (e.Exception is System.Windows.Markup.XamlParseException xpe && xpe.LineNumber > 0)
        {
            xamlMsg = $"\nXAML Location: Line {xpe.LineNumber}, Position {xpe.LinePosition}";
        }

        MessageBox.Show(
            $"Unexpected window host error:\n\n{e.Exception.Message}{innerMsg}{xamlMsg}\n\nDiagnostic log written to:\n{logPath}",
            "ERROR OPTIMIZER",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogDiagnosticException("AppDomain.UnhandledException", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogDiagnosticException("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    public static void LogStartup(string message)
    {
        try
        {
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] {message}";
            WriteToLogFiles(line);
        }
        catch { }
    }

    public static string LogDiagnosticException(string context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"==========================================================");
        sb.AppendLine($"CRITICAL EXCEPTION: {context}");
        sb.AppendLine($"Timestamp: {DateTime.UtcNow:o}");
        sb.AppendLine($"Process: {Environment.ProcessPath}");
        sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        sb.AppendLine($"CurrentDirectory: {Directory.GetCurrentDirectory()}");
        sb.AppendLine($"OSVersion: {Environment.OSVersion}");
        sb.AppendLine($"64BitOS: {Environment.Is64BitOperatingSystem}, 64BitProcess: {Environment.Is64BitProcess}");
        sb.AppendLine();

        int depth = 0;
        Exception? cur = ex;
        while (cur != null)
        {
            sb.AppendLine($"[Level {depth}] {cur.GetType().FullName}: {cur.Message}");
            if (cur is System.Windows.Markup.XamlParseException xpe)
            {
                sb.AppendLine($"  XAML Line: {xpe.LineNumber}, Position: {xpe.LinePosition}");
                if (xpe.BaseUri != null) sb.AppendLine($"  BaseUri: {xpe.BaseUri}");
                if (xpe.KeyContext != null) sb.AppendLine($"  KeyContext: {xpe.KeyContext}");
            }
            if (!string.IsNullOrEmpty(cur.StackTrace))
            {
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(cur.StackTrace);
            }
            sb.AppendLine();
            cur = cur.InnerException;
            depth++;
        }

        string report = sb.ToString();
        return WriteToLogFiles(report);
    }

    private static string WriteToLogFiles(string content)
    {
        string chosenPath = string.Empty;

        // Try 1: Machine-level shared log folder (%ProgramData%\ErrorOptimizer\logs)
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
            {
                string logDir = Path.Combine(programData, "ErrorOptimizer", "logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, "host_startup.log");
                File.AppendAllText(logFile, content + Environment.NewLine, Encoding.UTF8);
                chosenPath = logFile;
            }
        }
        catch { }

        // Try 2: User-level log folder (%LocalAppData%\ErrorOptimizer\logs)
        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                string logDir = Path.Combine(localApp, "ErrorOptimizer", "logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, "host_startup.log");
                File.AppendAllText(logFile, content + Environment.NewLine, Encoding.UTF8);
                if (string.IsNullOrEmpty(chosenPath)) chosenPath = logFile;
            }
        }
        catch { }

        return !string.IsNullOrEmpty(chosenPath) ? chosenPath : "host_startup.log";
    }
}
