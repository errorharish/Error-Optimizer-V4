using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Xunit;
using Xunit.Abstractions;

namespace BiosOptimizer.Core.Tests;

public class TaskManagerUIAutomationProbe
{
    private readonly ITestOutputHelper _output;

    public TaskManagerUIAutomationProbe(ITestOutputHelper output)
    {
        _output = output;
    }

    public enum UiaOutcome
    {
        Success,
        Blocked_UIPI,
        Blocked_WindowNotFound,
        Blocked_NavigationNotFound,
        Blocked_TargetNotFound,
        Blocked_ActionFailed
    }

    [Fact]
    public void TaskManager_LiveUIAutomation_ForensicProbe()
    {
        _output.WriteLine($"[UIA Probe] Starting at {DateTime.UtcNow:O}");
        var outcome = ProbeTaskManagerUI("Logitech G HUB", "Spotify");
        _output.WriteLine($"[UIA Probe] Final Outcome: {outcome}");
    }

    private UiaOutcome ProbeTaskManagerUI(string targetWin32, string targetMsix)
    {
        Process? proc = null;
        try
        {
            // Check if Taskmgr is already running or launch it
            var existing = Process.GetProcessesByName("Taskmgr");
            if (existing.Length > 0)
            {
                proc = existing[0];
                _output.WriteLine($"[UIA] Connected to existing Taskmgr PID: {proc.Id}, Session: {proc.SessionId}");
            }
            else
            {
                var psi = new ProcessStartInfo("taskmgr.exe")
                {
                    UseShellExecute = true
                };
                proc = Process.Start(psi);
                _output.WriteLine($"[UIA] Launched new Taskmgr PID: {proc?.Id}, Session: {proc?.SessionId}");
                Thread.Sleep(3000);
            }

            if (proc == null || proc.HasExited)
            {
                _output.WriteLine("[UIA] Process failed to launch or exited prematurely.");
                return UiaOutcome.Blocked_WindowNotFound;
            }

            // Find Window via UIAutomation RootElement
            var root = AutomationElement.RootElement;
            var pidCond = new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id);
            AutomationElement? tmWnd = null;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var allMatching = root.FindAll(TreeScope.Children, pidCond);
                    _output.WriteLine($"[UIA] Found {allMatching.Count} top-level windows for PID {proc.Id}");
                    foreach (AutomationElement w in allMatching)
                    {
                        string cName = w.Current.ClassName ?? "";
                        string wName = w.Current.Name ?? "";
                        _output.WriteLine($"[UIA] Window candidate: Name='{wName}', ClassName='{cName}'");
                        if (cName.Contains("TaskManagerWindow", StringComparison.OrdinalIgnoreCase) || wName.Contains("Task Manager", StringComparison.OrdinalIgnoreCase))
                        {
                            tmWnd = w;
                            break;
                        }
                    }

                    if (tmWnd == null && allMatching.Count > 0)
                    {
                        // Fall back to first non-overlay window
                        foreach (AutomationElement w in allMatching)
                        {
                            if (!w.Current.ClassName.Contains("Overlay", StringComparison.OrdinalIgnoreCase))
                            {
                                tmWnd = w;
                                break;
                            }
                        }
                    }

                    if (tmWnd != null) break;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[UIA] RootElement search exception (UIPI likely): {ex.GetType().Name} - {ex.Message}");
                    return UiaOutcome.Blocked_UIPI;
                }
                Thread.Sleep(1000);
            }

            if (tmWnd == null)
            {
                _output.WriteLine($"[UIA] Could not locate Task Manager window for PID {proc.Id}. Elevated UIPI or Session mismatch.");
                return UiaOutcome.Blocked_WindowNotFound;
            }

            _output.WriteLine($"[UIA] Located Window: '{tmWnd.Current.Name}' (ClassName: '{tmWnd.Current.ClassName}')");

            // Inspect elements to find Startup Apps Navigation Tab
            // Windows 11 Taskmgr uses a NavigationPane with ListItems or Buttons
            AutomationElement? startupNav = null;
            try
            {
                var allButtons = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement b in allButtons)
                {
                    string name = b.Current.Name ?? "";
                    string aId = b.Current.AutomationId ?? "";
                    if (name.Contains("Startup", StringComparison.OrdinalIgnoreCase) || aId.Contains("Startup", StringComparison.OrdinalIgnoreCase))
                    {
                        startupNav = b;
                        _output.WriteLine($"[UIA] Found Startup Navigation Button: Name='{name}', AutomationId='{aId}'");
                        break;
                    }
                }

                if (startupNav == null)
                {
                    var allListItems = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    foreach (AutomationElement li in allListItems)
                    {
                        string name = li.Current.Name ?? "";
                        string aId = li.Current.AutomationId ?? "";
                        if (name.Contains("Startup", StringComparison.OrdinalIgnoreCase) || aId.Contains("Startup", StringComparison.OrdinalIgnoreCase))
                        {
                            startupNav = li;
                            _output.WriteLine($"[UIA] Found Startup Navigation ListItem: Name='{name}', AutomationId='{aId}'");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[UIA] Element enumeration exception: {ex.Message}");
                return UiaOutcome.Blocked_UIPI;
            }

            if (startupNav == null)
            {
                _output.WriteLine("[UIA] Startup apps navigation element could not be located in Task Manager tree.");
                return UiaOutcome.Blocked_NavigationNotFound;
            }

            // Click or Invoke Startup navigation
            bool navSuccess = false;
            if (startupNav.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj) && invObj is InvokePattern inv)
            {
                inv.Invoke();
                navSuccess = true;
                _output.WriteLine("[UIA] Invoked Startup navigation via InvokePattern.");
            }
            else if (startupNav.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj) && selObj is SelectionItemPattern sel)
            {
                sel.Select();
                navSuccess = true;
                _output.WriteLine("[UIA] Selected Startup navigation via SelectionItemPattern.");
            }

            if (!navSuccess)
            {
                _output.WriteLine("[UIA] Neither InvokePattern nor SelectionItemPattern supported on navigation element.");
                return UiaOutcome.Blocked_ActionFailed;
            }

            Thread.Sleep(3000); // Allow list to render

            // Enumerate all descendants to find table / rows / items
            var allDescendants = tmWnd.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            _output.WriteLine($"[UIA] Total descendants in Task Manager: {allDescendants.Count}");

            var controlTypeCounts = new System.Collections.Generic.Dictionary<string, int>();
            AutomationElement? matchElem = null;

            foreach (AutomationElement el in allDescendants)
            {
                try
                {
                    string ct = el.Current.ControlType.ProgrammaticName;
                    controlTypeCounts[ct] = controlTypeCounts.GetValueOrDefault(ct) + 1;
                    string name = el.Current.Name ?? "";
                    string aId = el.Current.AutomationId ?? "";

                    if (name.Contains(targetWin32, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(targetMsix, StringComparison.OrdinalIgnoreCase) ||
                        aId.Contains(targetWin32, StringComparison.OrdinalIgnoreCase))
                    {
                        _output.WriteLine($"[UIA] FOUND MATCH: ControlType='{ct}', Name='{name}', AutomationId='{aId}'");
                        matchElem = el;
                    }
                }
                catch { }
            }

            var treeItems = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            _output.WriteLine($"[UIA] Enumerated {treeItems.Count} real TreeItems in Task Manager Startup apps:");
            AutomationElement? targetItem = null;
            int idx = 1;
            foreach (AutomationElement ti in treeItems)
            {
                string tName = ti.Current.Name ?? "";
                _output.WriteLine($"  {idx++,2}. '{tName}' (AutomationId: {ti.Current.AutomationId})");
                if (tName.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                    tName.Contains("lghub", StringComparison.OrdinalIgnoreCase))
                {
                    targetItem = ti;
                }
            }

            if (targetItem != null)
            {
                _output.WriteLine($"[UIA] Selecting target: '{targetItem.Current.Name}'");
                if (targetItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sipObj) && sipObj is SelectionItemPattern sip)
                {
                    sip.Select();
                    Thread.Sleep(1000);
                    _output.WriteLine("[UIA] Successfully selected target TreeItem.");
                }

                // Check command bar buttons
                var buttons = tmWnd.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                _output.WriteLine($"[UIA] Available Buttons while item selected: {buttons.Count}");
                foreach (AutomationElement b in buttons)
                {
                    string bName = b.Current.Name ?? "";
                    string bId = b.Current.AutomationId ?? "";
                    bool isEnabled = b.Current.IsEnabled;
                    _output.WriteLine($"  Button: Name='{bName}', AutomationId='{bId}', Enabled={isEnabled}");
                }
            }

            if (matchElem != null)
            {
                _output.WriteLine($"[UIA] Target matched! Attempting to inspect supported patterns on '{matchElem.Current.Name}'");
                var patterns = matchElem.GetSupportedPatterns();
                foreach (var p in patterns)
                {
                    _output.WriteLine($"[UIA] Supported Pattern: {p.ProgrammaticName}");
                }
                return UiaOutcome.Success;
            }

            _output.WriteLine($"[UIA] Target '{targetWin32}' or '{targetMsix}' not found in {allDescendants.Count} UI Automation elements.");
            return UiaOutcome.Blocked_TargetNotFound;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[UIA] Unhandled exception in ProbeTaskManagerUI: {ex}");
            return UiaOutcome.Blocked_ActionFailed;
        }
        finally
        {
            // If we launched a new process, clean up
            // Don't kill if user was using taskmgr
        }
    }
}
