using System;
using System.IO;
using System.Text;

class Program
{
    static void Main(string[] args)
    {
        string filePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\BiosOptimizer\logs\runtime-crash.log";
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length == 0) return;
            long pos = Math.Max(0, fs.Length - 10000);
            fs.Seek(pos, SeekOrigin.Begin);
            byte[] buffer = new byte[10000];
            fs.Read(buffer, 0, buffer.Length);
            string text = Encoding.UTF8.GetString(buffer);
            Console.WriteLine(text.Substring(text.IndexOf('\n') + 1));
        }
    }
}
