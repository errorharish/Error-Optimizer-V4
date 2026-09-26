using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

class Test
{
    [DllImport("powrprof.dll")]
    static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, IntPtr Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, IntPtr PowerSettingGuid, IntPtr Buffer, ref uint BufferSize);

    [DllImport("Kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    static void Main()
    {
        uint index = 0;
        uint bufferSize = (uint)Marshal.SizeOf(typeof(Guid));
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);

        while (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, buffer, ref bufferSize) == 0)
        {
            Guid g = Marshal.PtrToStructure<Guid>(buffer);
            uint nameSize = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref nameSize);
            string name = "N/A";
            if (nameSize > 0)
            {
                IntPtr nameBuf = Marshal.AllocHGlobal((int)nameSize);
                if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, nameBuf, ref nameSize) == 0)
                {
                    name = Marshal.PtrToStringUni(nameBuf) ?? "N/A";
                }
                Marshal.FreeHGlobal(nameBuf);
            }
            Console.WriteLine($"Index {index}: GUID={g} | Name='{name}'");
            index++;
            bufferSize = (uint)Marshal.SizeOf(typeof(Guid));
        }
        Marshal.FreeHGlobal(buffer);
    }
}
