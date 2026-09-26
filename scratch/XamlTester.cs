using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using System.Threading;

namespace XamlTester
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            TestXaml(@"D:\PROJECTS FOR EXE\ON PROCESS\ERROR OPTIMIZER\src\BiosOptimizer.GUI\Views\TierView.xaml");
            TestXaml(@"D:\PROJECTS FOR EXE\ON PROCESS\ERROR OPTIMIZER\src\BiosOptimizer.GUI\Views\RegistryTweakView.xaml");
        }

        static void TestXaml(string file)
        {
            Console.WriteLine("Testing: " + file);
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    var pc = new ParserContext();
                    pc.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                    pc.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
                    pc.XmlnsDictionary.Add("local", "clr-namespace:BiosOptimizer.GUI.Views;assembly=ErrorOptimizer");
                    XamlReader.Load(fs, pc);
                }
                Console.WriteLine("SUCCESS!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                var inner = ex.InnerException;
                while (inner != null)
                {
                    Console.WriteLine("  INNER: " + inner.Message);
                    inner = inner.InnerException;
                }
            }
            Console.WriteLine("--------------------------------------------------");
        }
    }
}
