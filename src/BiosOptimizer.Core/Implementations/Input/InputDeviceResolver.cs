#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BiosOptimizer.Core.Interfaces;

namespace BiosOptimizer.Core.Implementations.Input
{
    public class DeviceResolutionSummary
    {
        public List<ConnectedInputDevice> AllDevices { get; set; } = new();
        public List<ConnectedInputDevice> PhysicalMice { get; set; } = new();
        public List<ConnectedInputDevice> Touchpads { get; set; } = new();
        public List<ConnectedInputDevice> PhysicalKeyboards { get; set; } = new();

        public string MouseDeviceName { get; set; } = "HID-compatible pointing device";
        public string KeyboardDeviceName { get; set; } = "Standard HID Keyboard";
        public string MouseStatus { get; set; } = "Not detected";
        public string KeyboardStatus { get; set; } = "Not detected";
    }

    public static class InputDeviceResolver
    {
        #region Win32 SetupAPI / User32 Interop

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        private const uint RIM_TYPEMOUSE = 0;
        private const uint RIM_TYPEKEYBOARD = 1;
        private const uint RIM_TYPEHID = 2;

        private const uint RIDI_DEVICENAME = 0x20000007;

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDevicePropertyW(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            ref DEVPROPKEY PropertyKey,
            out uint PropertyType,
            byte[]? PropertyBuffer,
            uint PropertyBufferSize,
            out uint RequiredSize,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            StringBuilder DeviceInstanceId,
            uint DeviceInstanceIdSize,
            out uint RequiredSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;

        private static DEVPROPKEY DEVPKEY_Device_DeviceDesc = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
        private static DEVPROPKEY DEVPKEY_Device_FriendlyName = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        private static DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new DEVPROPKEY { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
        private static DEVPROPKEY DEVPKEY_Device_Manufacturer = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 13 };
        private static DEVPROPKEY DEVPKEY_Device_Parent = new DEVPROPKEY { fmtid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 8 };
        private static DEVPROPKEY DEVPKEY_Device_Class = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 9 };
        private static DEVPROPKEY DEVPKEY_Device_Service = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 6 };

        #endregion

        private class PnpNode
        {
            public string InstanceId { get; set; } = string.Empty;
            public string DeviceDesc { get; set; } = string.Empty;
            public string FriendlyName { get; set; } = string.Empty;
            public string BusReportedDesc { get; set; } = string.Empty;
            public string Manufacturer { get; set; } = string.Empty;
            public string ParentInstanceId { get; set; } = string.Empty;
            public string ClassName { get; set; } = string.Empty;
            public string Service { get; set; } = string.Empty;
        }

        private static readonly Dictionary<string, string> KnownVendors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["046D"] = "Logitech",
            ["1532"] = "Razer",
            ["1B1C"] = "Corsair",
            ["1038"] = "SteelSeries",
            ["045E"] = "Microsoft",
            ["04D9"] = "Holtek",
            ["048D"] = "ITE Tech",
            ["093A"] = "PixArt",
            ["04F2"] = "Chicony",
            ["06CB"] = "Synaptics",
            ["04F3"] = "ELAN",
            ["28BD"] = "XP-Pen",
            ["056E"] = "Elecom",
            ["258A"] = "Glorious",
            ["03F0"] = "HP",
            ["17AA"] = "Lenovo",
            ["0461"] = "Primax",
            ["18F8"] = "Alienware",
            ["0B05"] = "ASUS",
            ["047D"] = "Kensington",
            ["17EF"] = "Lenovo",
            ["2BAE"] = "Finalmouse",
            ["3367"] = "Wooting",
            ["3434"] = "Keychron"
        };

        private static string GetDevPropertyString(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData, DEVPROPKEY key)
        {
            uint req = 0;
            SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, ref key, out _, null, 0, out req, 0);
            if (req == 0) return string.Empty;
            byte[] buf = new byte[req];
            if (SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, ref key, out _, buf, req, out req, 0))
            {
                string val = Encoding.Unicode.GetString(buf);
                int nullIdx = val.IndexOf('\0');
                if (nullIdx >= 0) val = val.Substring(0, nullIdx);
                return val.Trim();
            }
            return string.Empty;
        }

        private static string InstanceIdFromRawPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return string.Empty;
            string s = rawPath;
            if (s.StartsWith(@"\\?\") || s.StartsWith(@"\\.\")) s = s.Substring(4);
            int lastHash = s.LastIndexOf('#');
            if (lastHash > 0 && s.Contains('{'))
            {
                s = s.Substring(0, lastHash);
            }
            return s.Replace('#', '\\');
        }

        private static string CleanLocalizedInfString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            int semi = input.LastIndexOf(';');
            if (semi >= 0 && semi < input.Length - 1)
            {
                return input.Substring(semi + 1).Trim();
            }
            return input.Trim();
        }

        /// <summary>
        /// Collects all present Windows PnP devices across all classes via SetupAPI.
        /// </summary>
        private static Dictionary<string, PnpNode> CollectPnpDeviceTree()
        {
            var pnpMap = new Dictionary<string, PnpNode>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Guid emptyGuid = Guid.Empty;
                IntPtr devInfoSet = SetupDiGetClassDevs(ref emptyGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
                if (devInfoSet != IntPtr.Zero && devInfoSet != new IntPtr(-1))
                {
                    try
                    {
                        SP_DEVINFO_DATA devInfoData = new SP_DEVINFO_DATA();
                        devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                        uint idx = 0;
                        while (SetupDiEnumDeviceInfo(devInfoSet, idx++, ref devInfoData))
                        {
                            StringBuilder sb = new StringBuilder(512);
                            SetupDiGetDeviceInstanceId(devInfoSet, ref devInfoData, sb, (uint)sb.Capacity, out _);
                            string inst = sb.ToString();

                            pnpMap[inst] = new PnpNode
                            {
                                InstanceId = inst,
                                DeviceDesc = CleanLocalizedInfString(GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_DeviceDesc)),
                                FriendlyName = CleanLocalizedInfString(GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_FriendlyName)),
                                BusReportedDesc = CleanLocalizedInfString(GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_BusReportedDeviceDesc)),
                                Manufacturer = CleanLocalizedInfString(GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_Manufacturer)),
                                ParentInstanceId = GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_Parent),
                                ClassName = GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_Class),
                                Service = GetDevPropertyString(devInfoSet, ref devInfoData, DEVPKEY_Device_Service)
                            };
                        }
                    }
                    finally
                    {
                        SetupDiDestroyDeviceInfoList(devInfoSet);
                    }
                }
            }
            catch { }
            return pnpMap;
        }

        /// <summary>
        /// Performs full, verified detection and identification of all physical, touchpad, and keyboard devices.
        /// </summary>
        public static DeviceResolutionSummary ResolveConnectedDevices()
        {
            var summary = new DeviceResolutionSummary();
            var pnpMap = CollectPnpDeviceTree();

            uint count = 0;
            uint structSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            GetRawInputDeviceList(null, ref count, structSize);

            if (count == 0)
            {
                // Fallback scan using PnP tree directly if RawInput returned zero devices
                ResolveFromPnpTreeDirectly(pnpMap, summary);
                return summary;
            }

            var rawList = new RAWINPUTDEVICELIST[count];
            if (GetRawInputDeviceList(rawList, ref count, structSize) == unchecked((uint)-1))
            {
                ResolveFromPnpTreeDirectly(pnpMap, summary);
                return summary;
            }

            var allDevices = new List<ConnectedInputDevice>();

            for (int i = 0; i < rawList.Length; i++)
            {
                var dev = rawList[i];
                uint nameLength = 0;
                GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameLength);

                string rawPath = string.Empty;
                if (nameLength > 0)
                {
                    IntPtr pData = Marshal.AllocHGlobal((int)nameLength * 2);
                    try
                    {
                        if (GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, pData, ref nameLength) != unchecked((uint)-1))
                        {
                            rawPath = Marshal.PtrToStringAuto(pData) ?? string.Empty;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pData);
                    }
                }

                string instId = InstanceIdFromRawPath(rawPath);
                pnpMap.TryGetValue(instId, out var node);

                PnpNode? parentNode = null;
                if (node != null && !string.IsNullOrEmpty(node.ParentInstanceId))
                {
                    pnpMap.TryGetValue(node.ParentInstanceId, out parentNode);
                }

                string instUpper = instId.ToUpperInvariant();
                string pathUpper = rawPath.ToUpperInvariant();

                // Extract VID and PID
                string vid = string.Empty;
                string pid = string.Empty;
                int vIdx = instUpper.IndexOf("VID_");
                if (vIdx >= 0 && instUpper.Length >= vIdx + 8) vid = instUpper.Substring(vIdx + 4, 4);
                int pIdx = instUpper.IndexOf("PID_");
                if (pIdx >= 0 && instUpper.Length >= pIdx + 8) pid = instUpper.Substring(pIdx + 4, 4);

                // If not found in device instance, check parent instance ID
                if (string.IsNullOrEmpty(vid) && parentNode != null)
                {
                    string pInstUpper = parentNode.InstanceId.ToUpperInvariant();
                    int pvIdx = pInstUpper.IndexOf("VID_");
                    if (pvIdx >= 0 && pInstUpper.Length >= pvIdx + 8) vid = pInstUpper.Substring(pvIdx + 4, 4);
                    int ppIdx = pInstUpper.IndexOf("PID_");
                    if (ppIdx >= 0 && pInstUpper.Length >= ppIdx + 8) pid = pInstUpper.Substring(ppIdx + 4, 4);
                }

                // Check Vendor
                string mfg = string.Empty;
                if (!string.IsNullOrEmpty(vid) && KnownVendors.TryGetValue(vid, out var known))
                {
                    mfg = known;
                }
                else if (node != null && !string.IsNullOrEmpty(node.Manufacturer) && !node.Manufacturer.Contains("Standard") && !node.Manufacturer.Contains("Microsoft"))
                {
                    mfg = node.Manufacturer;
                }
                else if (parentNode != null && !string.IsNullOrEmpty(parentNode.Manufacturer) && !parentNode.Manufacturer.Contains("Standard") && !parentNode.Manufacturer.Contains("Microsoft"))
                {
                    mfg = parentNode.Manufacturer;
                }
                else if (instUpper.Contains("ELAN") || pathUpper.Contains("ELAN"))
                {
                    mfg = "ELAN";
                }
                else if (instUpper.Contains("SYN") || pathUpper.Contains("SYN"))
                {
                    mfg = "Synaptics";
                }

                // Check Virtual devices
                bool isVirtual = pathUpper.Contains("VIRTUAL") || pathUpper.Contains("ROOT") || pathUpper.Contains("VJOY") ||
                                 pathUpper.Contains("PARSEC") || pathUpper.Contains("VIGEM") || pathUpper.Contains("STEAM");

                // Check Touchpad identification
                bool isTouchpad = false;
                if (dev.dwType == RIM_TYPEMOUSE || dev.dwType == RIM_TYPEHID)
                {
                    if (instUpper.Contains("ELAN") || instUpper.Contains("SYN") || instUpper.Contains("TOUCHPAD") ||
                        (parentNode != null && (parentNode.InstanceId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase) ||
                                                parentNode.DeviceDesc.Contains("I2C", StringComparison.OrdinalIgnoreCase) ||
                                                parentNode.DeviceDesc.Contains("touch pad", StringComparison.OrdinalIgnoreCase))) ||
                        pnpMap.Values.Any(v => v.ParentInstanceId.Equals(node?.ParentInstanceId, StringComparison.OrdinalIgnoreCase) &&
                                              v.DeviceDesc.Contains("touch pad", StringComparison.OrdinalIgnoreCase)))
                    {
                        isTouchpad = true;
                    }
                }

                // Connection type
                string transport = "USB Wired";
                bool isInternal = false;
                if (isTouchpad || instUpper.StartsWith("ACPI") || (parentNode != null && parentNode.InstanceId.StartsWith("ACPI")))
                {
                    transport = "Internal (I2C / ACPI)";
                    isInternal = true;
                }
                else if (instUpper.StartsWith("ACPI\\MSFT0001") || (node != null && node.Service.Equals("i8042prt", StringComparison.OrdinalIgnoreCase)))
                {
                    transport = "Internal (PS/2)";
                    isInternal = true;
                }
                else if (pathUpper.Contains("BTHENUM") || pathUpper.Contains("BLUETOOTH"))
                {
                    transport = "Bluetooth";
                }
                else if (pathUpper.Contains("WIRELESS") || (parentNode != null && parentNode.BusReportedDesc.Contains("Receiver", StringComparison.OrdinalIgnoreCase)))
                {
                    transport = "2.4GHz Wireless";
                }

                // Device Type classification
                string devType = dev.dwType switch
                {
                    RIM_TYPEMOUSE => isTouchpad ? "Touchpad" : "Mouse",
                    RIM_TYPEKEYBOARD => "Keyboard",
                    _ => isTouchpad ? "Touchpad" : "HID"
                };

                // Friendly Name resolution in order of accuracy
                string friendlyName = string.Empty;
                if (!string.IsNullOrEmpty(node?.FriendlyName))
                {
                    friendlyName = node.FriendlyName;
                }
                else if (parentNode != null && !string.IsNullOrEmpty(parentNode.BusReportedDesc) &&
                         parentNode.InstanceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                {
                    friendlyName = parentNode.BusReportedDesc;
                }
                else if (!string.IsNullOrEmpty(node?.BusReportedDesc) && !node.BusReportedDesc.Contains("PCI to ISA Bridge"))
                {
                    friendlyName = node.BusReportedDesc;
                }
                else if (isTouchpad)
                {
                    friendlyName = !string.IsNullOrEmpty(mfg) ? $"{mfg} Precision Touchpad" : "Precision Touchpad";
                }
                else if (!string.IsNullOrEmpty(node?.DeviceDesc) && !node.DeviceDesc.Contains("PCI to ISA Bridge"))
                {
                    friendlyName = node.DeviceDesc;
                }
                else
                {
                    friendlyName = devType switch
                    {
                        "Mouse" => "HID-compatible pointing device",
                        "Touchpad" => "Precision Touchpad",
                        "Keyboard" => "Standard HID Keyboard",
                        _ => "HID Input Device"
                    };
                }

                if (string.IsNullOrEmpty(mfg))
                {
                    mfg = isTouchpad ? "ELAN Microelectronics" : (isInternal ? "Standard System Input" : "Generic HID");
                }

                allDevices.Add(new ConnectedInputDevice
                {
                    DeviceId = dev.hDevice.ToString(),
                    DeviceName = friendlyName,
                    DeviceType = devType,
                    Manufacturer = mfg,
                    TransportType = transport,
                    ButtonCount = dev.dwType == RIM_TYPEMOUSE ? 5 : 104,
                    HasWheel = dev.dwType == RIM_TYPEMOUSE || isTouchpad,
                    IsHighPrecision = true,
                    Vid = vid,
                    Pid = pid,
                    DeviceClass = node?.ClassName ?? devType,
                    DeviceInterface = node?.Service ?? string.Empty,
                    RawDevicePath = rawPath,
                    InstanceId = instId,
                    IsPhysical = !isVirtual,
                    IsInternal = isInternal,
                    IsVirtual = isVirtual,
                    IsTouchpad = isTouchpad
                });
            }

            summary.AllDevices = allDevices;

            // Grouping and Deduplication
            // Physical mice: genuine external mice (not touchpads, not virtual)
            var mice = allDevices
                .Where(d => d.DeviceType == "Mouse" && !d.IsTouchpad && !d.IsVirtual)
                .GroupBy(d => !string.IsNullOrEmpty(d.Vid) && !string.IsNullOrEmpty(d.Pid) ? $"{d.Vid}_{d.Pid}" : d.DeviceName)
                .Select(g => g.First())
                .ToList();

            // Touchpads: internal precision touchpads
            var touchpads = allDevices
                .Where(d => d.IsTouchpad && !d.IsVirtual)
                .GroupBy(d => d.DeviceName)
                .Select(g => g.First())
                .ToList();

            // Keyboards: distinct physical keyboards (group composite collections sharing same VID/PID)
            var keyboards = allDevices
                .Where(d => d.DeviceType == "Keyboard" && !d.IsVirtual)
                .GroupBy(d => !string.IsNullOrEmpty(d.Vid) && !string.IsNullOrEmpty(d.Pid) ? $"{d.Vid}_{d.Pid}" : d.DeviceName)
                .Select(g => g.First())
                .ToList();

            summary.PhysicalMice = mice;
            summary.Touchpads = touchpads;
            summary.PhysicalKeyboards = keyboards;

            // Compute Mouse Tile Display & Status
            if (mice.Count > 1)
            {
                summary.MouseDeviceName = mice[0].DeviceName;
                summary.MouseStatus = "Multiple devices";
            }
            else if (mice.Count == 1)
            {
                summary.MouseDeviceName = mice[0].DeviceName;
                summary.MouseStatus = "Detected";
            }
            else if (touchpads.Count > 0)
            {
                summary.MouseDeviceName = touchpads[0].DeviceName;
                summary.MouseStatus = "Detected";
            }
            else
            {
                summary.MouseDeviceName = "No pointer detected";
                summary.MouseStatus = "Not detected";
            }

            // Compute Keyboard Tile Display & Status
            if (keyboards.Count > 1)
            {
                // Prefer external keyboard if available, else primary built-in
                var externalKbd = keyboards.FirstOrDefault(k => !k.IsInternal);
                summary.KeyboardDeviceName = (externalKbd ?? keyboards[0]).DeviceName;
                summary.KeyboardStatus = "Multiple devices";
            }
            else if (keyboards.Count == 1)
            {
                summary.KeyboardDeviceName = keyboards[0].DeviceName;
                summary.KeyboardStatus = "Detected";
            }
            else
            {
                summary.KeyboardDeviceName = "No keyboard detected";
                summary.KeyboardStatus = "Not detected";
            }

            return summary;
        }

        private static void ResolveFromPnpTreeDirectly(Dictionary<string, PnpNode> pnpMap, DeviceResolutionSummary summary)
        {
            var pnpMice = pnpMap.Values
                .Where(n => n.ClassName.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pnpKeyboards = pnpMap.Values
                .Where(n => n.ClassName.Equals("Keyboard", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pnpTouchpads = pnpMap.Values
                .Where(n => n.DeviceDesc.Contains("touch pad", StringComparison.OrdinalIgnoreCase) ||
                            n.FriendlyName.Contains("touch pad", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var allDevs = new List<ConnectedInputDevice>();

            foreach (var m in pnpMice)
            {
                string name = !string.IsNullOrEmpty(m.FriendlyName) ? m.FriendlyName :
                              (!string.IsNullOrEmpty(m.DeviceDesc) ? m.DeviceDesc : "HID-compliant mouse");
                allDevs.Add(new ConnectedInputDevice
                {
                    DeviceId = m.InstanceId,
                    DeviceName = name,
                    DeviceType = "Mouse",
                    Manufacturer = !string.IsNullOrEmpty(m.Manufacturer) ? m.Manufacturer : "Standard Pointing Device",
                    TransportType = m.InstanceId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase) ? "Internal (PS/2 / ACPI)" : "USB Wired",
                    DeviceClass = "Mouse",
                    InstanceId = m.InstanceId,
                    IsPhysical = true,
                    IsInternal = m.InstanceId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase)
                });
            }

            foreach (var k in pnpKeyboards)
            {
                string name = !string.IsNullOrEmpty(k.FriendlyName) ? k.FriendlyName :
                              (!string.IsNullOrEmpty(k.DeviceDesc) ? k.DeviceDesc : "Standard HID Keyboard");
                allDevs.Add(new ConnectedInputDevice
                {
                    DeviceId = k.InstanceId,
                    DeviceName = name,
                    DeviceType = "Keyboard",
                    Manufacturer = !string.IsNullOrEmpty(k.Manufacturer) ? k.Manufacturer : "Standard System Keyboard",
                    TransportType = k.InstanceId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase) ? "Internal (PS/2 / ACPI)" : "USB Wired",
                    DeviceClass = "Keyboard",
                    InstanceId = k.InstanceId,
                    IsPhysical = true,
                    IsInternal = k.InstanceId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase)
                });
            }

            foreach (var t in pnpTouchpads)
            {
                string name = !string.IsNullOrEmpty(t.FriendlyName) ? t.FriendlyName :
                              (!string.IsNullOrEmpty(t.DeviceDesc) ? t.DeviceDesc : "Precision Touchpad");
                allDevs.Add(new ConnectedInputDevice
                {
                    DeviceId = t.InstanceId,
                    DeviceName = name,
                    DeviceType = "Touchpad",
                    Manufacturer = !string.IsNullOrEmpty(t.Manufacturer) ? t.Manufacturer : "Precision Touchpad",
                    TransportType = "Internal (I2C / ACPI)",
                    DeviceClass = "Mouse",
                    InstanceId = t.InstanceId,
                    IsPhysical = true,
                    IsTouchpad = true,
                    IsInternal = true
                });
            }

            summary.AllDevices = allDevs;

            if (pnpMice.Count > 0)
            {
                var primary = pnpMice[0];
                summary.MouseDeviceName = !string.IsNullOrEmpty(primary.FriendlyName) ? primary.FriendlyName :
                                         (!string.IsNullOrEmpty(primary.DeviceDesc) ? primary.DeviceDesc : "HID-compatible pointing device");
                summary.MouseStatus = pnpMice.Count > 1 ? "Multiple devices" : "Detected";
            }
            else if (pnpTouchpads.Count > 0)
            {
                summary.MouseDeviceName = "Precision Touchpad";
                summary.MouseStatus = "Detected";
            }

            if (pnpKeyboards.Count > 0)
            {
                var primary = pnpKeyboards[0];
                summary.KeyboardDeviceName = !string.IsNullOrEmpty(primary.FriendlyName) ? primary.FriendlyName :
                                            (!string.IsNullOrEmpty(primary.DeviceDesc) ? primary.DeviceDesc : "Standard HID Keyboard");
                summary.KeyboardStatus = pnpKeyboards.Count > 1 ? "Multiple devices" : "Detected";
            }
        }
    }
}
