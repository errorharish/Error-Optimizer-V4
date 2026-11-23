using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using BiosOptimizer.Core.Implementations.Startup;
using BiosOptimizer.Core.Interfaces;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace BiosOptimizer.Core.Tests;

public class TaskManagerLiveUITests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private Process? _taskmgrProcess;

    public TaskManagerLiveUITests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        if (_taskmgrProcess != null && !_taskmgrProcess.HasExited)
        {
            try
            {
                _taskmgrProcess.Kill();
                _taskmgrProcess.Dispose();
            }
            catch { }
        }
    }

    private (AutomationElement Window, Process Proc) EnsureTaskManagerOnStartupApps()
    {
        // Clean up any lingering taskmgr instances before launching fresh
        foreach (var p in Process.GetProcessesByName("Taskmgr"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
        Thread.Sleep(500);

        var proc = Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        Assert.NotNull(proc);
        _taskmgrProcess = proc;
        Thread.Sleep(3000);

        var root = AutomationElement.RootElement;
        AutomationElement? tmWnd = null;

        for (int i = 0; i < 8; i++)
        {
            var windows = root.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement w in windows)
            {
                string cName = w.Current.ClassName ?? "";
                string wName = w.Current.Name ?? "";
                if (cName.Contains("TaskManagerWindow", StringComparison.OrdinalIgnoreCase) ||
                    wName.Contains("Task Manager", StringComparison.OrdinalIgnoreCase))
                {
                    tmWnd = w;
                    break;
                }
            }
            if (tmWnd != null) break;
            Thread.Sleep(1000);
        }

        Assert.NotNull(tmWnd);
        _output.WriteLine($"[Live UI] Task Manager Window located: Name '{tmWnd.Current.Name}' (PID {proc.Id})");

        // Navigate to Startup Apps
        AutomationElement? startupNav = null;
        var allListItems = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        foreach (AutomationElement li in allListItems)
        {
            string name = li.Current.Name ?? "";
            if (name.Contains("Startup", StringComparison.OrdinalIgnoreCase))
            {
                startupNav = li;
                break;
            }
        }

        Assert.NotNull(startupNav);
        if (startupNav.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj) && selObj is SelectionItemPattern sel)
        {
            sel.Select();
        }
        else if (startupNav.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj) && invObj is InvokePattern inv)
        {
            inv.Invoke();
        }

        Thread.Sleep(2000);
        return (tmWnd, proc);
    }

    private void ClickCommandBarButton(AutomationElement tmWnd, string buttonName)
    {
        var buttons = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        AutomationElement? targetBtn = null;
        foreach (AutomationElement b in buttons)
        {
            if (string.Equals(b.Current.Name, buttonName, StringComparison.OrdinalIgnoreCase))
            {
                targetBtn = b;
                break;
            }
        }

        Assert.NotNull(targetBtn);
        Assert.True(targetBtn.Current.IsEnabled, $"Button '{buttonName}' must be enabled in Task Manager command bar.");

        if (targetBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj) && invObj is InvokePattern inv)
        {
            inv.Invoke();
            _output.WriteLine($"[Live UI] Successfully clicked Task Manager button: '{buttonName}'");
        }
        else
        {
            throw new InvalidOperationException($"Button '{buttonName}' does not support InvokePattern.");
        }
    }

    [Fact]
    public void TaskManager_LiveUI_Win32_GHub_BidirectionalAcceptance()
    {
        var (tmWnd, proc) = EnsureTaskManagerOnStartupApps();
        var eoMgr = new WindowsStartupManager();

        var treeItems = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
        AutomationElement? ghubItem = null;
        foreach (AutomationElement ti in treeItems)
        {
            string tName = ti.Current.Name ?? "";
            if (tName.Contains("lghub", StringComparison.OrdinalIgnoreCase) || tName.Contains("Logitech", StringComparison.OrdinalIgnoreCase))
            {
                ghubItem = ti;
                break;
            }
        }

        Assert.NotNull(ghubItem);
        _output.WriteLine($"[Live UI] Found G HUB TreeItem: '{ghubItem.Current.Name}' (AutomationId: {ghubItem.Current.AutomationId})");

        // Select G HUB in Task Manager
        if (ghubItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sipObj) && sipObj is SelectionItemPattern sip)
        {
            sip.Select();
            Thread.Sleep(1000);
        }

        using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
        Assert.NotNull(runKey);
        byte[] origGhub = (byte[])runKey.GetValue("LGHUB", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        try
        {
            // STEP 1: Enable G HUB through real Task Manager UI
            _output.WriteLine("[Live UI] Step 1: Clicking 'Enable' in Task Manager UI for G HUB...");
            ClickCommandBarButton(tmWnd, "Enable");
            Thread.Sleep(1500);

            // Read genuine Windows OS registry state
            byte[] readbackEn = (byte[])runKey.GetValue("LGHUB")!;
            _output.WriteLine($"[Live UI] OS Readback after TM 'Enable' click: byte[0] = 0x{readbackEn[0]:X2}");
            Assert.Equal(0x02, readbackEn[0]);

            // Confirm ERROR OPTIMIZER immediately discovers Enabled
            var eoItemEn = eoMgr.GetStartupEntries().First(e => e.DisplayName == "Logitech G HUB");
            _output.WriteLine($"[Live UI] ERROR OPTIMIZER State after TM click: {eoItemEn.State}");
            Assert.Equal(StartupState.Enabled, eoItemEn.State);

            // STEP 2: Disable G HUB through real Task Manager UI
            _output.WriteLine("[Live UI] Step 2: Clicking 'Disable' in Task Manager UI for G HUB...");
            ClickCommandBarButton(tmWnd, "Disable");
            Thread.Sleep(1500);

            // Read genuine Windows OS registry state
            byte[] readbackDis = (byte[])runKey.GetValue("LGHUB")!;
            _output.WriteLine($"[Live UI] OS Readback after TM 'Disable' click: byte[0] = 0x{readbackDis[0]:X2}");
            Assert.Equal(0x03, readbackDis[0]);

            // Confirm ERROR OPTIMIZER immediately discovers Disabled
            var eoItemDis = eoMgr.GetStartupEntries().First(e => e.DisplayName == "Logitech G HUB");
            _output.WriteLine($"[Live UI] ERROR OPTIMIZER State after TM click: {eoItemDis.State}");
            Assert.Equal(StartupState.Disabled, eoItemDis.State);
        }
        finally
        {
            runKey.SetValue("LGHUB", origGhub, RegistryValueKind.Binary);
        }
    }

    [Fact]
    public void TaskManager_LiveUI_MSIX_Spotify_BidirectionalAcceptance()
    {
        var (tmWnd, proc) = EnsureTaskManagerOnStartupApps();
        var eoMgr = new WindowsStartupManager();

        var treeItems = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
        AutomationElement? spotifyItem = null;
        foreach (AutomationElement ti in treeItems)
        {
            string tName = ti.Current.Name ?? "";
            if (tName.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            {
                spotifyItem = ti;
                break;
            }
        }

        Assert.NotNull(spotifyItem);
        _output.WriteLine($"[Live UI] Found Spotify TreeItem: '{spotifyItem.Current.Name}' (AutomationId: {spotifyItem.Current.AutomationId})");

        // Select Spotify in Task Manager
        if (spotifyItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sipObj) && sipObj is SelectionItemPattern sip)
        {
            sip.Select();
            Thread.Sleep(1000);
        }

        string appModelSubKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\Spotify";
        using var amKey = Registry.CurrentUser.OpenSubKey(appModelSubKey, true);
        Assert.NotNull(amKey);
        int origAmState = (int)amKey.GetValue("State", 1);

        using var saKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupApp", true);
        byte[]? origSa = (byte[]?)saKey?.GetValue("Spotify");

        try
        {
            // STEP 1: Enable Spotify through real Task Manager UI
            _output.WriteLine("[Live UI] Step 1: Clicking 'Enable' in Task Manager UI for Spotify...");
            ClickCommandBarButton(tmWnd, "Enable");
            Thread.Sleep(1500);

            // Read genuine Windows OS registry state
            int amStateEn = (int)amKey.GetValue("State", -1);
            _output.WriteLine($"[Live UI] AppModel State after TM 'Enable' click: {amStateEn}");
            Assert.Equal(2, amStateEn);

            if (saKey != null)
            {
                byte[]? saBytes = (byte[]?)saKey.GetValue("Spotify");
                if (saBytes != null)
                {
                    _output.WriteLine($"[Live UI] StartupApproved byte[0] after TM 'Enable' click: 0x{saBytes[0]:X2} (Note: Windows Task Manager targets AppModel State)");
                }
            }

            // Confirm ERROR OPTIMIZER immediately discovers Enabled
            var eoItemEn = eoMgr.GetStartupEntries().First(e => e.DisplayName == "Spotify");
            _output.WriteLine($"[Live UI] ERROR OPTIMIZER State after TM click: {eoItemEn.State}");
            Assert.Equal(StartupState.Enabled, eoItemEn.State);

            // STEP 2: Disable Spotify through real Task Manager UI
            _output.WriteLine("[Live UI] Step 2: Clicking 'Disable' in Task Manager UI for Spotify...");
            ClickCommandBarButton(tmWnd, "Disable");
            Thread.Sleep(1500);

            // Read genuine Windows OS registry state
            int amStateDis = (int)amKey.GetValue("State", -1);
            _output.WriteLine($"[Live UI] AppModel State after TM 'Disable' click: {amStateDis}");
            Assert.Equal(1, amStateDis);

            if (saKey != null)
            {
                byte[]? saBytes = (byte[]?)saKey.GetValue("Spotify");
                if (saBytes != null)
                {
                    _output.WriteLine($"[Live UI] StartupApproved byte[0] after TM 'Disable' click: 0x{saBytes[0]:X2}");
                }
            }

            // Confirm ERROR OPTIMIZER immediately discovers Disabled
            var eoItemDis = eoMgr.GetStartupEntries().First(e => e.DisplayName == "Spotify");
            _output.WriteLine($"[Live UI] ERROR OPTIMIZER State after TM click: {eoItemDis.State}");
            Assert.Equal(StartupState.Disabled, eoItemDis.State);
        }
        finally
        {
            amKey.SetValue("State", origAmState, RegistryValueKind.DWord);
            if (saKey != null && origSa != null)
            {
                saKey.SetValue("Spotify", origSa, RegistryValueKind.Binary);
            }
        }
    }
}
