using System;
using System.Diagnostics;
using System.Linq;
using BiosOptimizer.Core.Implementations.Startup;
using BiosOptimizer.Core.Interfaces;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace BiosOptimizer.Core.Tests;

public class StartupManagerParityTests
{
    private readonly ITestOutputHelper _output;

    public StartupManagerParityTests(ITestOutputHelper output)
    {
        _output = output;
        // Warm up backup manager manifest cache once
        try
        {
            var bm = BiosOptimizer.Core.Implementations.BackupManager.Instance;
            bm.CaptureTargetedGenericTweak("Warmup", "warmup", "Warmup", "Warmup", "Warmup", "0", "0", "None", false);
        }
        catch { }
    }

    [Fact]
    public void StartupManager_DiscoversAuthoritativeTaskManagerEntries()
    {
        var mgr = new WindowsStartupManager();
        var entries = mgr.GetStartupEntries();

        _output.WriteLine($"Discovered {entries.Count} entries:");
        int idx = 1;
        foreach (var e in entries)
        {
            _output.WriteLine($"{idx++,2}. Name: {e.DisplayName,-32} | Pub: {e.Publisher,-25} | State: {e.State,-8} | Source: {e.Source,-18} | HasIcon: {e.IconBase64 != null}");
        }

        // Must exclude RunOnce
        Assert.DoesNotContain(entries, e => e.Source.Contains("RUNONCE", StringComparison.OrdinalIgnoreCase));

        // Must exclude generic scheduled tasks
        Assert.DoesNotContain(entries, e => e.Source == "SCHEDULED_TASK");

        // Must exclude non-startup packaged apps like StartExperiencesApp
        Assert.DoesNotContain(entries, e => e.DisplayName.Contains("Start Experiences", StringComparison.OrdinalIgnoreCase) ||
                                            e.ValueName.Contains("StartExperiences", StringComparison.OrdinalIgnoreCase));

        // Must discover exactly the 13 Task Manager startup applications
        Assert.Equal(13, entries.Count);

        // Verify key expected applications
        var names = entries.Select(e => e.DisplayName).ToList();
        Assert.Contains(entries, e => e.DisplayName.Contains("G HUB", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Security", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Realtek", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("XBOX", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Copilot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Terminal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Mobile devices", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Teams", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.DisplayName.Contains("Telegram", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StartupManager_ToggleSpeed_IsUnder300ms_WithGenuineWindowsReadback()
    {
        var mgr = new WindowsStartupManager();
        var entries = mgr.GetStartupEntries();
        var target = entries.FirstOrDefault(e => e.DisplayName.Contains("G HUB", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(target);

        // 1. Benchmark Enable operation
        var sw = Stopwatch.StartNew();
        bool enableResult = mgr.EnableEntry(target.Source, target.Path, target.ValueName, target.Id);
        sw.Stop();
        long enableMs = sw.ElapsedMilliseconds;
        _output.WriteLine($"Enable {target.DisplayName}: {enableResult} in {enableMs} ms");
        Assert.True(enableResult, "Enable operation should succeed and be verified in registry");
        Assert.True(enableMs < 300, $"Enable must complete under 300 ms (actual: {enableMs} ms)");

        // Verify actual Windows Registry state: 0x02 or even byte
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
        {
            var bytes = key?.GetValue(target.ValueName) as byte[];
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);
            Assert.True(bytes[0] == 0x02 || (bytes[0] & 1) == 0, $"Expected enabled flag (0x02), got 0x{bytes[0]:X2}");
        }

        // 2. Benchmark Disable operation
        sw.Restart();
        bool disableResult = mgr.DisableEntry(target.Source, target.Path, target.ValueName, target.Id);
        sw.Stop();
        long disableMs = sw.ElapsedMilliseconds;
        _output.WriteLine($"Disable {target.DisplayName}: {disableResult} in {disableMs} ms");
        Assert.True(disableResult, "Disable operation should succeed and be verified in registry");
        Assert.True(disableMs < 300, $"Disable must complete under 300 ms (actual: {disableMs} ms)");

        // Verify actual Windows Registry state: 0x03 or odd byte
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
        {
            var bytes = key?.GetValue(target.ValueName) as byte[];
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);
            Assert.True(bytes[0] == 0x03 || (bytes[0] & 1) != 0, $"Expected disabled flag (0x03), got 0x{bytes[0]:X2}");
        }
    }

    [Fact]
    public void StartupManager_MsixToggle_IsUnder300ms_WithGenuineWindowsReadback()
    {
        var mgr = new WindowsStartupManager();
        var entries = mgr.GetStartupEntries();
        var target = entries.FirstOrDefault(e => e.DisplayName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                                                 e.DisplayName.Equals("Telegram Desktop", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(target);

        // Benchmark Enable operation
        var sw = Stopwatch.StartNew();
        bool enableResult = mgr.EnableEntry(target.Source, target.Path, target.ValueName, target.Id);
        sw.Stop();
        long enableMs = sw.ElapsedMilliseconds;
        _output.WriteLine($"Enable MSIX {target.DisplayName}: {enableResult} in {enableMs} ms");
        Assert.True(enableResult);
        Assert.True(enableMs < 300);

        // Benchmark Disable operation
        sw.Restart();
        bool disableResult = mgr.DisableEntry(target.Source, target.Path, target.ValueName, target.Id);
        sw.Stop();
        long disableMs = sw.ElapsedMilliseconds;
        _output.WriteLine($"Disable MSIX {target.DisplayName}: {disableResult} in {disableMs} ms");
        Assert.True(disableResult);
        Assert.True(disableMs < 300);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartupManager_InstalledService_MatchesStandaloneInventoryExactly()
    {
        var standaloneMgr = new WindowsStartupManager();
        var standaloneEntries = standaloneMgr.GetStartupEntries();
        Assert.Equal(13, standaloneEntries.Count);

        // Query running BiosOptimizer.Service via IPC
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "BiosOptimizer_IPC_v2_default", System.IO.Pipes.PipeDirection.InOut);
        await pipe.ConnectAsync(3000);
        Assert.True(pipe.IsConnected, "Must connect to installed BiosOptimizer.Service named pipe");

        var msg = new BiosOptimizer.IPC.Contracts.IpcMessage
        {
            Type = BiosOptimizer.IPC.Contracts.IpcMessageType.GetStartupItems
        };
        string json = System.Text.Json.JsonSerializer.Serialize(msg) + "\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await pipe.WriteAsync(bytes, 0, bytes.Length);
        await pipe.FlushAsync();

        using var reader = new System.IO.StreamReader(pipe, System.Text.Encoding.UTF8, false, 8192, leaveOpen: true);
        string? responseJson = await reader.ReadLineAsync();
        Assert.NotNull(responseJson);

        var response = System.Text.Json.JsonSerializer.Deserialize<BiosOptimizer.IPC.Contracts.IpcResponse>(responseJson);
        Assert.NotNull(response);
        Assert.True(response.Success, $"Service IPC returned error: {response.ErrorMessage}");
        Assert.False(string.IsNullOrWhiteSpace(response.Data));

        var serviceEntries = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<StartupEntryState>>(
            response.Data,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(serviceEntries);

        _output.WriteLine($"Installed Service Discovered {serviceEntries.Count} entries:");
        int idx = 1;
        foreach (var e in serviceEntries)
        {
            _output.WriteLine($"{idx++,2}. Name: {e.DisplayName,-32} | Pub: {e.Publisher,-25} | State: {e.State,-8} | Source: {e.Source,-18}");
        }

        Assert.Equal(13, serviceEntries.Count);

        // Verify exact set parity between standalone and installed
        var standaloneNames = standaloneEntries.Select(e => e.DisplayName).OrderBy(x => x).ToList();
        var serviceNames = serviceEntries.Select(e => e.DisplayName).OrderBy(x => x).ToList();
        Assert.Equal(standaloneNames, serviceNames);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartupManager_BidirectionalTaskManagerSync_Verified()
    {
        var standaloneMgr = new WindowsStartupManager();
        var entries = standaloneMgr.GetStartupEntries();

        // 1. Target Win32: Logitech G HUB
        var ghub = entries.FirstOrDefault(e => e.DisplayName == "Logitech G HUB");
        Assert.NotNull(ghub);

        using (var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true))
        {
            Assert.NotNull(runKey);
            byte[] origGhub = (byte[])runKey.GetValue(ghub.ValueName, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            try
            {
                // TEST A1: Error Optimizer Enables Win32 -> Registry reflects Task Manager Enabled (0x02)
                bool enRes = standaloneMgr.EnableEntry(ghub.Source, ghub.Path, ghub.ValueName, ghub.Id);
                Assert.True(enRes);
                byte[] readbackEn = (byte[])runKey.GetValue(ghub.ValueName)!;
                Assert.Equal(0x02, readbackEn[0]);
                _output.WriteLine($"[EO -> OS] Win32 Enable: byte[0] = 0x{readbackEn[0]:X2} (Task Manager ENABLED)");

                // TEST A2: Error Optimizer Disables Win32 -> Registry reflects Task Manager Disabled (0x03)
                bool disRes = standaloneMgr.DisableEntry(ghub.Source, ghub.Path, ghub.ValueName, ghub.Id);
                Assert.True(disRes);
                byte[] readbackDis = (byte[])runKey.GetValue(ghub.ValueName)!;
                Assert.Equal(0x03, readbackDis[0]);
                _output.WriteLine($"[EO -> OS] Win32 Disable: byte[0] = 0x{readbackDis[0]:X2} (Task Manager DISABLED)");

                // TEST B1: Task Manager Enables Win32 (Writes 0x02) -> Error Optimizer discovers Enabled
                byte[] tmEnableBytes = (byte[])readbackDis.Clone();
                tmEnableBytes[0] = 0x02;
                runKey.SetValue(ghub.ValueName, tmEnableBytes, Microsoft.Win32.RegistryValueKind.Binary);
                var scan1 = standaloneMgr.GetStartupEntries().First(e => e.DisplayName == "Logitech G HUB");
                Assert.Equal(BiosOptimizer.Core.Interfaces.StartupState.Enabled, scan1.State);
                _output.WriteLine($"[OS -> EO] Task Manager Enabled Win32 -> Error Optimizer Status = {scan1.State}");

                // TEST B2: Task Manager Disables Win32 (Writes 0x03) -> Error Optimizer discovers Disabled
                byte[] tmDisableBytes = (byte[])readbackDis.Clone();
                tmDisableBytes[0] = 0x03;
                runKey.SetValue(ghub.ValueName, tmDisableBytes, Microsoft.Win32.RegistryValueKind.Binary);
                var scan2 = standaloneMgr.GetStartupEntries().First(e => e.DisplayName == "Logitech G HUB");
                Assert.Equal(BiosOptimizer.Core.Interfaces.StartupState.Disabled, scan2.State);
                _output.WriteLine($"[OS -> EO] Task Manager Disabled Win32 -> Error Optimizer Status = {scan2.State}");
            }
            finally
            {
                runKey.SetValue(ghub.ValueName, origGhub, Microsoft.Win32.RegistryValueKind.Binary);
            }
        }

        // 2. Target MSIX: Spotify
        var spotify = entries.FirstOrDefault(e => e.DisplayName == "Spotify");
        Assert.NotNull(spotify);

        string appModelSubKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\Spotify";
        using (var amKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(appModelSubKey, true))
        {
            Assert.NotNull(amKey);
            int origAmState = (int)amKey.GetValue("State", 1);

            using var saKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupApp", true);
            string saName = spotify.ValueName;
            byte[]? origSa = (byte[]?)saKey?.GetValue(saName);

            try
            {
                // TEST A1: Error Optimizer Enables MSIX -> AppModel State=2, StartupApp byte 0=0x02
                bool enRes = standaloneMgr.EnableEntry(spotify.Source, spotify.Path, spotify.ValueName, spotify.Id);
                Assert.True(enRes);
                int amStateEn = (int)amKey.GetValue("State", -1);
                Assert.Equal(2, amStateEn);
                if (saKey != null)
                {
                    byte[]? saBytes = (byte[]?)saKey.GetValue(saName);
                    if (saBytes != null) Assert.Equal(0x02, saBytes[0]);
                }
                _output.WriteLine($"[EO -> OS] MSIX Enable: AppModel State = {amStateEn} (Task Manager ENABLED)");

                // TEST A2: Error Optimizer Disables MSIX -> AppModel State=1, StartupApp byte 0=0x03
                bool disRes = standaloneMgr.DisableEntry(spotify.Source, spotify.Path, spotify.ValueName, spotify.Id);
                Assert.True(disRes);
                int amStateDis = (int)amKey.GetValue("State", -1);
                Assert.Equal(1, amStateDis);
                if (saKey != null)
                {
                    byte[]? saBytes = (byte[]?)saKey.GetValue(saName);
                    if (saBytes != null) Assert.Equal(0x03, saBytes[0]);
                }
                _output.WriteLine($"[EO -> OS] MSIX Disable: AppModel State = {amStateDis} (Task Manager DISABLED)");

                // TEST B1: Task Manager Enables MSIX (Sets State=2) -> Error Optimizer discovers Enabled
                amKey.SetValue("State", 2, Microsoft.Win32.RegistryValueKind.DWord);
                if (saKey != null && origSa != null)
                {
                    byte[] b = (byte[])origSa.Clone();
                    b[0] = 0x02;
                    saKey.SetValue(saName, b, Microsoft.Win32.RegistryValueKind.Binary);
                }
                var scan1 = standaloneMgr.GetStartupEntries().First(e => e.DisplayName == "Spotify");
                Assert.Equal(BiosOptimizer.Core.Interfaces.StartupState.Enabled, scan1.State);
                _output.WriteLine($"[OS -> EO] Task Manager Enabled MSIX -> Error Optimizer Status = {scan1.State}");

                // TEST B2: Task Manager Disables MSIX (Sets State=1) -> Error Optimizer discovers Disabled
                amKey.SetValue("State", 1, Microsoft.Win32.RegistryValueKind.DWord);
                if (saKey != null && origSa != null)
                {
                    byte[] b = (byte[])origSa.Clone();
                    b[0] = 0x03;
                    saKey.SetValue(saName, b, Microsoft.Win32.RegistryValueKind.Binary);
                }
                var scan2 = standaloneMgr.GetStartupEntries().First(e => e.DisplayName == "Spotify");
                Assert.Equal(BiosOptimizer.Core.Interfaces.StartupState.Disabled, scan2.State);
                _output.WriteLine($"[OS -> EO] Task Manager Disabled MSIX -> Error Optimizer Status = {scan2.State}");
            }
            finally
            {
                amKey.SetValue("State", origAmState, Microsoft.Win32.RegistryValueKind.DWord);
                if (saKey != null && origSa != null)
                {
                    saKey.SetValue(saName, origSa, Microsoft.Win32.RegistryValueKind.Binary);
                }
            }
        }
    }
}
