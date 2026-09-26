using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace AutomationRunner
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Starting ErrorOptimizer...");
            var psi = new ProcessStartInfo(@"D:\PROJECTS FOR EXE\ON PROCESS\ERROR OPTIMIZER\dist\win-x64\ErrorOptimizer.exe");
            var process = Process.Start(psi);
            
            Thread.Sleep(3000); // Wait for main window to load
            
            var mainWindow = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Error Optimizer Pro"));
            if (mainWindow == null) mainWindow = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Error Optimizer"));

            if (mainWindow == null)
            {
                Console.WriteLine("Could not find main window. Did it crash on startup?");
                try { process.Kill(); } catch { }
                return;
            }
            
            Console.WriteLine("Found main window. Clicking Normal...");
            ClickButton(mainWindow, "Normal");
            Thread.Sleep(2000);
            
            Console.WriteLine("Clicking Pro...");
            ClickButton(mainWindow, "Pro");
            Thread.Sleep(2000);

            Console.WriteLine("Clicking Registry Tweaks...");
            ClickButton(mainWindow, "Registry Tweaks");
            Thread.Sleep(2000);
            
            Console.WriteLine("Closing ErrorOptimizer...");
            try { process.Kill(); } catch { }
        }
        
        static void ClickButton(AutomationElement parent, string name)
        {
            var btn = parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
            if (btn != null)
            {
                var invokePattern = btn.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (invokePattern != null) {
                    invokePattern.Invoke();
                } else {
                    // Try selection item pattern
                    var selPattern = btn.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                    if (selPattern != null) {
                        selPattern.Select();
                    } else {
                        Console.WriteLine("Button found but no invoke/select pattern: " + name);
                    }
                }
            }
            else
            {
                Console.WriteLine("Could not find button: " + name);
            }
        }
    }
}
