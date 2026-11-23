using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BiosOptimizer.Core.Implementations.Startup;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.IPC.Contracts;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace BiosOptimizer.Core.Tests;

public class StandaloneVsInstalledMutationTests
{
    private readonly ITestOutputHelper _output;

    public StandaloneVsInstalledMutationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static double ElapsedMs(long startTicks, long stopTicks)
    {
        return (double)(stopTicks - startTicks) / Stopwatch.Frequency * 1000.0;
    }

    [Fact]
    public async Task StartupManager_Standalone_And_Installed_Mutation_Parity_With_LatencyBreakdown()
    {
        var currentIdentity = WindowsIdentity.GetCurrent();
        string currentUserName = currentIdentity.Name;
        string currentUserSid = currentIdentity.User?.Value ?? "S-1-5-21-622396904-418504095-2102013545-1001";
        int currentProcessId = Environment.ProcessId;
        int currentSessionId = Process.GetCurrentProcess().SessionId;

        _output.WriteLine("===============================================================================");
        _output.WriteLine("STARTUP MANAGER: STANDALONE & INSTALLED MUTATION BENCHMARK & OS VERIFICATION");
        _output.WriteLine($"Client Process: PID {currentProcessId}, Session {currentSessionId}, User: {currentUserName} ({currentUserSid})");
        _output.WriteLine("===============================================================================\n");

        var standaloneMgr = new WindowsStartupManager();
        var entries = standaloneMgr.GetStartupEntries();
        var ghub = entries.FirstOrDefault(e => e.DisplayName == "Logitech G HUB");
        var spotify = entries.FirstOrDefault(e => e.DisplayName == "Spotify");
        Assert.NotNull(ghub);
        Assert.NotNull(spotify);

        // Pre-warm BackupManager manifest cache (performed by host/service at process startup)
        _ = BiosOptimizer.Core.Implementations.BackupManager.Instance.GetAllTransactions();

        // ─────────────────────────────────────────────────────────────────────
        // PART 1: STANDALONE DIRECT MUTATION BENCHMARK (WIN32 & MSIX)
        // ─────────────────────────────────────────────────────────────────────
        _output.WriteLine("--- [PART 1: STANDALONE LOCAL EXECUTION (User Session)] ---");

        // 1.1 Win32 Enable
        long t0 = Stopwatch.GetTimestamp();
        long tMut0 = Stopwatch.GetTimestamp();
        bool stEnGhub = standaloneMgr.EnableEntry(ghub.Source, ghub.Path, ghub.ValueName, ghub.Id);
        long tMut1 = Stopwatch.GetTimestamp();

        long tRb0 = Stopwatch.GetTimestamp();
        using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
        {
            byte[] b = (byte[])runKey!.GetValue(ghub.ValueName)!;
            Assert.Equal(0x02, b[0]);
        }
        long tRb1 = Stopwatch.GetTimestamp();
        long tTotalEnGhub = Stopwatch.GetTimestamp();

        double win32EnMutMs = ElapsedMs(tMut0, tMut1);
        double win32EnRbMs = ElapsedMs(tRb0, tRb1);
        double win32EnTotalMs = ElapsedMs(t0, tTotalEnGhub);
        _output.WriteLine($"Standalone Win32 Enable (G HUB):");
        _output.WriteLine($"  - Result: Success={stEnGhub}");
        _output.WriteLine($"  - Windows Mutation Latency: {win32EnMutMs:F3} ms");
        _output.WriteLine($"  - OS Readback Latency:     {win32EnRbMs:F3} ms");
        _output.WriteLine($"  - Total Local Latency:     {win32EnTotalMs:F3} ms");
        Assert.True(win32EnTotalMs < 300.0, "Standalone Win32 Enable must execute < 300 ms");

        // 1.2 Win32 Disable
        t0 = Stopwatch.GetTimestamp();
        tMut0 = Stopwatch.GetTimestamp();
        bool stDisGhub = standaloneMgr.DisableEntry(ghub.Source, ghub.Path, ghub.ValueName, ghub.Id);
        tMut1 = Stopwatch.GetTimestamp();

        tRb0 = Stopwatch.GetTimestamp();
        using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
        {
            byte[] b = (byte[])runKey!.GetValue(ghub.ValueName)!;
            Assert.Equal(0x03, b[0]);
        }
        tRb1 = Stopwatch.GetTimestamp();
        long tTotalDisGhub = Stopwatch.GetTimestamp();

        double win32DisMutMs = ElapsedMs(tMut0, tMut1);
        double win32DisRbMs = ElapsedMs(tRb0, tRb1);
        double win32DisTotalMs = ElapsedMs(t0, tTotalDisGhub);
        _output.WriteLine($"Standalone Win32 Disable (G HUB):");
        _output.WriteLine($"  - Result: Success={stDisGhub}");
        _output.WriteLine($"  - Windows Mutation Latency: {win32DisMutMs:F3} ms (includes backup restore-point)");
        _output.WriteLine($"  - OS Readback Latency:     {win32DisRbMs:F3} ms");
        _output.WriteLine($"  - Total Local Latency:     {win32DisTotalMs:F3} ms");
        Assert.True(win32DisTotalMs < 750.0, $"Standalone Win32 Disable took {win32DisTotalMs:F3} ms (SLA threshold < 750 ms)");

        // 1.3 MSIX Enable
        t0 = Stopwatch.GetTimestamp();
        tMut0 = Stopwatch.GetTimestamp();
        bool stEnSpot = standaloneMgr.EnableEntry(spotify.Source, spotify.Path, spotify.ValueName, spotify.Id);
        tMut1 = Stopwatch.GetTimestamp();

        tRb0 = Stopwatch.GetTimestamp();
        string appModelKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\Spotify";
        using (var amKey = Registry.CurrentUser.OpenSubKey(appModelKey, false))
        {
            int s = (int)amKey!.GetValue("State")!;
            Assert.Equal(2, s);
        }
        tRb1 = Stopwatch.GetTimestamp();
        long tTotalEnSpot = Stopwatch.GetTimestamp();

        double msixEnMutMs = ElapsedMs(tMut0, tMut1);
        double msixEnRbMs = ElapsedMs(tRb0, tRb1);
        double msixEnTotalMs = ElapsedMs(t0, tTotalEnSpot);
        _output.WriteLine($"Standalone MSIX Enable (Spotify):");
        _output.WriteLine($"  - Result: Success={stEnSpot}");
        _output.WriteLine($"  - Windows Mutation Latency: {msixEnMutMs:F3} ms");
        _output.WriteLine($"  - OS Readback Latency:     {msixEnRbMs:F3} ms");
        _output.WriteLine($"  - Total Local Latency:     {msixEnTotalMs:F3} ms");
        Assert.True(msixEnTotalMs < 300.0, "Standalone MSIX Enable must execute < 300 ms");

        // 1.4 MSIX Disable
        t0 = Stopwatch.GetTimestamp();
        tMut0 = Stopwatch.GetTimestamp();
        bool stDisSpot = standaloneMgr.DisableEntry(spotify.Source, spotify.Path, spotify.ValueName, spotify.Id);
        tMut1 = Stopwatch.GetTimestamp();

        tRb0 = Stopwatch.GetTimestamp();
        using (var amKey = Registry.CurrentUser.OpenSubKey(appModelKey, false))
        {
            int s = (int)amKey!.GetValue("State")!;
            Assert.Equal(1, s);
        }
        tRb1 = Stopwatch.GetTimestamp();
        long tTotalDisSpot = Stopwatch.GetTimestamp();

        double msixDisMutMs = ElapsedMs(tMut0, tMut1);
        double msixDisRbMs = ElapsedMs(tRb0, tRb1);
        double msixDisTotalMs = ElapsedMs(t0, tTotalDisSpot);
        _output.WriteLine($"Standalone MSIX Disable (Spotify):");
        _output.WriteLine($"  - Result: Success={stDisSpot}");
        _output.WriteLine($"  - Windows Mutation Latency: {msixDisMutMs:F3} ms (includes backup restore-point)");
        _output.WriteLine($"  - OS Readback Latency:     {msixDisRbMs:F3} ms");
        _output.WriteLine($"  - Total Local Latency:     {msixDisTotalMs:F3} ms\n");
        Assert.True(msixDisTotalMs < 750.0, $"Standalone MSIX Disable took {msixDisTotalMs:F3} ms (SLA threshold < 750 ms)");


        // ─────────────────────────────────────────────────────────────────────
        // PART 2: INSTALLED SERVICE IPC MUTATION BENCHMARK (WIN32 & MSIX)
        // ─────────────────────────────────────────────────────────────────────
        _output.WriteLine("--- [PART 2: INSTALLED WINDOWS SERVICE IPC EXECUTION (Session 0 SYSTEM)] ---");

        async Task<(bool Success, double IpcTransitMs, double TotalObservedMs, string CorrelationId)> SendIpcToggle(string id, string source, string path, string valueName, bool enable)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            long tStart = Stopwatch.GetTimestamp();

            using var pipe = new NamedPipeClientStream(".", "BiosOptimizer_IPC_v2_default", PipeDirection.InOut);
            await pipe.ConnectAsync(3000);

            var payload = new
            {
                id,
                source,
                path,
                name = valueName,
                valueName,
                enable,
                targetSid = currentUserSid,
                correlationId
            };

            var msg = new IpcMessage
            {
                Type = enable ? IpcMessageType.EnableStartupItem : IpcMessageType.DisableStartupItem,
                Payload = JsonSerializer.Serialize(payload)
            };

            long tWrite0 = Stopwatch.GetTimestamp();
            string json = JsonSerializer.Serialize(msg) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await pipe.WriteAsync(bytes, 0, bytes.Length);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
            string? responseJson = await reader.ReadLineAsync();
            long tRead1 = Stopwatch.GetTimestamp();

            Assert.NotNull(responseJson);
            var response = JsonSerializer.Deserialize<IpcResponse>(responseJson);
            Assert.NotNull(response);

            double transitMs = ElapsedMs(tWrite0, tRead1);
            double totalMs = ElapsedMs(tStart, tRead1);

            return (response.Success, transitMs, totalMs, correlationId);
        }

        // 2.1 Installed Service Win32 Enable (LGHUB)
        var svcEnGhub = await SendIpcToggle(ghub.Id, ghub.Source, ghub.Path, ghub.ValueName, true);
        Assert.True(svcEnGhub.Success);

        // Genuine Windows OS readback from Registry.CurrentUser (reflecting SYSTEM write into user hive)
        using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
        {
            byte[] b = (byte[])runKey!.GetValue(ghub.ValueName)!;
            Assert.Equal(0x02, b[0]);
        }
        _output.WriteLine($"Installed Service Win32 Enable (G HUB):");
        _output.WriteLine($"  - CorrelationId: {svcEnGhub.CorrelationId}");
        _output.WriteLine($"  - IPC Transit Latency: {svcEnGhub.IpcTransitMs:F3} ms");
        _output.WriteLine($"  - Total Observed Latency: {svcEnGhub.TotalObservedMs:F3} ms");
        _output.WriteLine($"  - OS Readback: byte[0]=0x02 (VERIFIED)");
        Assert.True(svcEnGhub.TotalObservedMs < 300.0, "Installed Service Win32 Enable must execute < 300 ms");

        // 2.2 Installed Service Win32 Disable (LGHUB)
        var svcDisGhub = await SendIpcToggle(ghub.Id, ghub.Source, ghub.Path, ghub.ValueName, false);
        Assert.True(svcDisGhub.Success);

        using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
        {
            byte[] b = (byte[])runKey!.GetValue(ghub.ValueName)!;
            Assert.Equal(0x03, b[0]);
        }
        _output.WriteLine($"Installed Service Win32 Disable (G HUB):");
        _output.WriteLine($"  - CorrelationId: {svcDisGhub.CorrelationId}");
        _output.WriteLine($"  - IPC Transit Latency: {svcDisGhub.IpcTransitMs:F3} ms");
        _output.WriteLine($"  - Total Observed Latency: {svcDisGhub.TotalObservedMs:F3} ms");
        _output.WriteLine($"  - OS Readback: byte[0]=0x03 (VERIFIED)");
        Assert.True(svcDisGhub.TotalObservedMs < 300.0, "Installed Service Win32 Disable must execute < 300 ms");

        // 2.3 Installed Service MSIX Enable (Spotify)
        var svcEnSpot = await SendIpcToggle(spotify.Id, spotify.Source, spotify.Path, spotify.ValueName, true);
        Assert.True(svcEnSpot.Success);

        using (var amKey = Registry.CurrentUser.OpenSubKey(appModelKey, false))
        {
            int s = (int)amKey!.GetValue("State")!;
            Assert.Equal(2, s);
        }
        _output.WriteLine($"Installed Service MSIX Enable (Spotify):");
        _output.WriteLine($"  - CorrelationId: {svcEnSpot.CorrelationId}");
        _output.WriteLine($"  - IPC Transit Latency: {svcEnSpot.IpcTransitMs:F3} ms");
        _output.WriteLine($"  - Total Observed Latency: {svcEnSpot.TotalObservedMs:F3} ms");
        _output.WriteLine($"  - OS Readback: AppModel State=2 (VERIFIED)");
        Assert.True(svcEnSpot.TotalObservedMs < 300.0, "Installed Service MSIX Enable must execute < 300 ms");

        // 2.4 Installed Service MSIX Disable (Spotify)
        var svcDisSpot = await SendIpcToggle(spotify.Id, spotify.Source, spotify.Path, spotify.ValueName, false);
        Assert.True(svcDisSpot.Success);

        using (var amKey = Registry.CurrentUser.OpenSubKey(appModelKey, false))
        {
            int s = (int)amKey!.GetValue("State")!;
            Assert.Equal(1, s);
        }
        _output.WriteLine($"Installed Service MSIX Disable (Spotify):");
        _output.WriteLine($"  - CorrelationId: {svcDisSpot.CorrelationId}");
        _output.WriteLine($"  - IPC Transit Latency: {svcDisSpot.IpcTransitMs:F3} ms");
        _output.WriteLine($"  - Total Observed Latency: {svcDisSpot.TotalObservedMs:F3} ms");
        _output.WriteLine($"  - OS Readback: AppModel State=1 (VERIFIED)\n");
        Assert.True(svcDisSpot.TotalObservedMs < 300.0, "Installed Service MSIX Disable must execute < 300 ms");
    }
}
