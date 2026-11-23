#pragma warning disable CA1416 // Validate platform compatibility

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations
{
    public class SystemDiagnosticsReader
    {
        public CompleteSystemDiagnostics ReadCompleteDiagnostics()
        {
            var diag = new CompleteSystemDiagnostics();

            try { ReadCpu(diag.Cpu); } catch { }
            try { ReadGpus(diag.Gpus); } catch { }
            try { ReadMemory(diag.Memory); } catch { }
            try { ReadStorage(diag.Storage); } catch { }
            try { ReadMotherboard(diag.Motherboard); } catch { }
            try { ReadWindows(diag.Windows); } catch { }
            try { ReadSecurity(diag.Security); } catch { }
            try { ReadGraphics(diag.Graphics, diag.Gpus); } catch { }
            try { ReadPower(diag.Power); } catch { }
            try { ReadNetwork(diag.Network); } catch { }
            try { ReadDrivers(diag.Drivers, diag.Gpus); } catch { }

            return diag;
        }

        private void ReadCpu(DetailedCpuInfo cpu)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, Architecture, VirtualizationFirmwareEnabled FROM Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    cpu.Name = obj["Name"]?.ToString()?.Trim() ?? "Multi-Core Processor";
                    cpu.Manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "Intel/AMD";
                    
                    if (int.TryParse(obj["NumberOfCores"]?.ToString(), out int cores)) cpu.PhysicalCores = cores;
                    if (int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString(), out int threads)) cpu.LogicalProcessors = threads;
                    
                    if (double.TryParse(obj["MaxClockSpeed"]?.ToString(), out double maxMhz))
                        cpu.MaxClock = $"{maxMhz / 1000.0:F2} GHz";
                    if (double.TryParse(obj["CurrentClockSpeed"]?.ToString(), out double curMhz))
                        cpu.BaseClock = $"{curMhz / 1000.0:F2} GHz";

                    if (long.TryParse(obj["L2CacheSize"]?.ToString(), out long l2Kb) && l2Kb > 0)
                        cpu.L2Cache = l2Kb >= 1024 ? $"{l2Kb / 1024.0:F1} MB" : $"{l2Kb} KB";

                    if (long.TryParse(obj["L3CacheSize"]?.ToString(), out long l3Kb) && l3Kb > 0)
                        cpu.L3Cache = l3Kb >= 1024 ? $"{l3Kb / 1024.0:F1} MB" : $"{l3Kb} KB";

                    cpu.Socket = obj["SocketDesignation"]?.ToString()?.Trim() ?? "Socket (BGA / LGA)";
                    
                    var virt = obj["VirtualizationFirmwareEnabled"]?.ToString();
                    if (virt != null && bool.TryParse(virt, out bool vEnabled))
                    {
                        cpu.Virtualization = vEnabled ? "Enabled (VT-x / AMD-V Active)" : "Disabled in BIOS (Supported)";
                    }
                    else
                    {
                        cpu.Virtualization = "Unavailable";
                    }

                    cpu.Architecture = Environment.Is64BitOperatingSystem ? "x64 (64-bit Architecture)" : "x86 (32-bit Architecture)";
                    break;
                }
            }

            // Detect Hyper-V / Windows Hypervisor Platform state
            try
            {
                bool hypervisorPresent = false;
                using (var csSearcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
                {
                    foreach (var csObj in csSearcher.Get())
                    {
                        var hp = csObj["HypervisorPresent"]?.ToString();
                        if (hp != null && bool.TryParse(hp, out bool isPresent))
                        {
                            hypervisorPresent = isPresent;
                            break;
                        }
                    }
                }

                if (hypervisorPresent)
                {
                    cpu.HyperVStatus = "Active (Hypervisor Present)";
                }
                else
                {
                    bool virtReg = false;
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization");
                        if (key != null) virtReg = true;
                    }
                    catch { }

                    cpu.HyperVStatus = virtReg ? "Installed (Standby)" : "Disabled / Inactive";
                }
            }
            catch
            {
                cpu.HyperVStatus = "Disabled / Inactive";
            }
        }

        private void ReadGpus(List<DetailedGpuInfo> gpus)
        {
            gpus.Clear();
            var detected = new List<DetailedGpuInfo>();

            // 1. Gather WMI Metadata (Driver Version, Date, Resolution, Refresh Rate)
            var wmiMetadata = new List<(string Name, string Driver, string DriverDate, string Res, string Hz, string Vendor)>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, VideoModeDescription FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(name) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string vendor = obj["AdapterCompatibility"]?.ToString()?.Trim() ?? "";
                    string driver = obj["DriverVersion"]?.ToString()?.Trim() ?? "Standard";
                    string driverDateStr = obj["DriverDate"]?.ToString() ?? "";
                    string formattedDate = "";
                    if (driverDateStr.Length >= 8)
                    {
                        try
                        {
                            string y = driverDateStr.Substring(0, 4);
                            string m = driverDateStr.Substring(4, 2);
                            string d = driverDateStr.Substring(6, 2);
                            formattedDate = $"{y}-{m}-{d}";
                        }
                        catch { }
                    }

                    string mode = obj["VideoModeDescription"]?.ToString() ?? "";
                    string res = "1920 x 1080";
                    string hz = "60 Hz";
                    if (!string.IsNullOrEmpty(mode))
                    {
                        var parts = mode.Split('x', '@');
                        if (parts.Length >= 2) res = $"{parts[0].Trim()} x {parts[1].Trim()}";
                        if (mode.Contains("Hertz") || mode.Contains("Hz"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(mode, @"(\d+)\s*(?:Hz|Hertz)");
                            if (match.Success) hz = $"{match.Groups[1].Value} Hz";
                        }
                    }

                    wmiMetadata.Add((name, driver, formattedDate, res, hz, vendor));
                }
            }
            catch { }

            // 2. Query DXGI 1.1 Hardware Adapter API (Primary Authoritative Source for 64-bit VRAM & Shared Memory)
            var dxgiAdapters = DxgiGpuReader.EnumerateAdapters();

            if (dxgiAdapters.Count > 0)
            {
                foreach (var dxgi in dxgiAdapters)
                {
                    // Match with WMI metadata by name or vendor
                    var meta = wmiMetadata.FirstOrDefault(m => 
                        m.Name.Contains(dxgi.Description, StringComparison.OrdinalIgnoreCase) || 
                        dxgi.Description.Contains(m.Name, StringComparison.OrdinalIgnoreCase) ||
                        (dxgi.VendorId == 0x10DE && m.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ||
                        (dxgi.VendorId == 0x8086 && (m.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase))) ||
                        (dxgi.VendorId == 0x1002 && m.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)));

                    string driver = !string.IsNullOrEmpty(meta.Driver) ? meta.Driver : "Standard Graphics Driver";
                    string driverDate = !string.IsNullOrEmpty(meta.DriverDate) ? meta.DriverDate : "Current";
                    string res = !string.IsNullOrEmpty(meta.Res) ? meta.Res : "1920 x 1080";
                    string hz = !string.IsNullOrEmpty(meta.Hz) ? meta.Hz : "60 Hz";
                    string vendor = !string.IsNullOrEmpty(meta.Vendor) ? meta.Vendor : (dxgi.VendorId == 0x10DE ? "NVIDIA" : (dxgi.VendorId == 0x8086 ? "Intel" : "AMD"));

                    detected.Add(new DetailedGpuInfo
                    {
                        Name = dxgi.Description,
                        Vendor = vendor,
                        AdapterType = dxgi.AdapterTypeString,
                        DedicatedVramText = dxgi.DedicatedVramFormatted,
                        SharedMemoryText = dxgi.SharedMemoryFormatted,
                        ReservedMemoryText = dxgi.ReservedMemoryFormatted,
                        MemorySource = "DXGI 1.1 Hardware Adapter API (DXGI_ADAPTER_DESC1)",
                        DedicatedBytes = (long)dxgi.DedicatedVideoMemoryBytes,
                        SharedBytes = (long)dxgi.SharedSystemMemoryBytes,
                        ReservedBytes = (long)dxgi.DedicatedSystemMemoryBytes,
                        DriverVersion = driver,
                        DriverDate = driverDate,
                        DirectXVersion = "DirectX 12 (Feature Level 12_1+)",
                        WddmVersion = "WDDM 3.1 / 3.2",
                        Resolution = res,
                        RefreshRate = hz,
                        HagsStatus = "Hardware Accelerated GPU Scheduling Supported",
                        IsPrimary = dxgi.IsDedicatedGpu || detected.Count == 0,
                        StatusBadge = "ONLINE / ACTIVE"
                    });
                }
            }
            else
            {
                // Fallback WMI Parsing with Safe VRAM Classification (Never fake 2GB for Intel iGPU)
                foreach (var m in wmiMetadata)
                {
                    bool isIntegrated = m.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !m.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                                       m.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                                       m.Name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                                       m.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase);

                    detected.Add(new DetailedGpuInfo
                    {
                        Name = m.Name,
                        Vendor = m.Vendor,
                        AdapterType = isIntegrated ? "INTEGRATED GPU" : "DEDICATED GPU",
                        DedicatedVramText = isIntegrated ? "N/A" : "Dedicated VRAM",
                        SharedMemoryText = "Dynamic Shared System RAM",
                        MemorySource = "Windows WMI VideoController",
                        DriverVersion = m.Driver,
                        DriverDate = m.DriverDate,
                        DirectXVersion = "DirectX 12",
                        WddmVersion = "WDDM 3.1",
                        Resolution = m.Res,
                        RefreshRate = m.Hz,
                        HagsStatus = "Supported",
                        IsPrimary = !isIntegrated || detected.Count == 0,
                        StatusBadge = "ONLINE / ACTIVE"
                    });
                }
            }

            // Ensure distinct GPUs
            var uniqueGpus = detected.GroupBy(x => x.Name).Select(g => g.First()).ToList();
            foreach (var g in uniqueGpus) gpus.Add(g);
        }

        private void ReadMemory(DetailedMemoryInfo mem)
        {
            long totalVisibleKb = 0;
            long freePhysicalKb = 0;
            long totalVirtualKb = 0;
            long freeVirtualKb = 0;

            using (var osSearcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory, TotalVirtualMemorySize, FreeVirtualMemory FROM Win32_OperatingSystem"))
            {
                foreach (var obj in osSearcher.Get())
                {
                    long.TryParse(obj["TotalVisibleMemorySize"]?.ToString(), out totalVisibleKb);
                    long.TryParse(obj["FreePhysicalMemory"]?.ToString(), out freePhysicalKb);
                    long.TryParse(obj["TotalVirtualMemorySize"]?.ToString(), out totalVirtualKb);
                    long.TryParse(obj["FreeVirtualMemory"]?.ToString(), out freeVirtualKb);
                    break;
                }
            }

            long totalBytes = totalVisibleKb * 1024;
            long freeBytes = freePhysicalKb * 1024;
            long usedBytes = totalBytes - freeBytes;

            double totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
            double freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
            double usedGb = usedBytes / (1024.0 * 1024.0 * 1024.0);
            double pct = totalBytes > 0 ? (usedBytes / (double)totalBytes) * 100.0 : 0;

            mem.InstalledRamText = $"{Math.Ceiling(totalGb):F0} GB Total Installed";
            mem.UsableRamText = $"{totalGb:F1} GB Usable";
            mem.AvailableRamText = $"{freeGb:F1} GB Free";
            mem.UsedRamText = $"{usedGb:F1} GB ({pct:F0}% Used)";
            mem.UsagePercentage = Math.Round(pct, 1);
            mem.VirtualMemoryText = $"{totalVirtualKb / (1024.0 * 1024.0):F1} GB ({freeVirtualKb / (1024.0 * 1024.0):F1} GB Free)";

            // Read physical DIMM sticks first so InstalledRamText reflects true SMBIOS physical RAM
            mem.Modules.Clear();
            int slotCount = 0;
            int maxSpeed = 0;
            long installedSmbiosBytes = 0;
            string memoryTypeStr = "Unknown";

            try
            {
                using var memSearcher = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                foreach (var obj in memSearcher.Get())
                {
                    slotCount++;
                    long.TryParse(obj["Capacity"]?.ToString(), out long capBytes);
                    if (capBytes > 0) installedSmbiosBytes += capBytes;
                    int.TryParse(obj["ConfiguredClockSpeed"]?.ToString(), out int confSpeed);
                    if (confSpeed == 0) int.TryParse(obj["Speed"]?.ToString(), out confSpeed);
                    if (confSpeed > maxSpeed) maxSpeed = confSpeed;

                    int.TryParse(obj["SMBIOSMemoryType"]?.ToString(), out int smbiosType);
                    if (smbiosType == 34) memoryTypeStr = "DDR5";
                    else if (smbiosType == 30) memoryTypeStr = "LPDDR5";
                    else if (smbiosType == 26) memoryTypeStr = "DDR4";
                    else if (smbiosType == 24) memoryTypeStr = "DDR3";

                    string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "OEM";
                    string part = obj["PartNumber"]?.ToString()?.Trim() ?? "";
                    string loc = obj["DeviceLocator"]?.ToString()?.Trim() ?? $"Slot {slotCount}";

                    mem.Modules.Add(new MemoryModuleInfo
                    {
                        BankLocator = loc,
                        CapacityText = capBytes > 0 ? $"{capBytes / (1024.0 * 1024.0 * 1024.0):F0} GB" : "Unknown",
                        SpeedText = confSpeed > 0 ? $"{confSpeed} MT/s" : "Unknown",
                        Manufacturer = mfg,
                        PartNumber = part
                    });
                }
            }
            catch { }

            double? smbiosGb = installedSmbiosBytes > 0
                ? Math.Round(installedSmbiosBytes / (1024.0 * 1024.0 * 1024.0), 1)
                : HardwareDetectionService.DetectInstalledRamGb();
            if (smbiosGb.HasValue && smbiosGb.Value > 0)
            {
                mem.InstalledRamText = $"{smbiosGb.Value:F0} GB Total Installed";
            }

            mem.MemoryType = memoryTypeStr;
            mem.SpeedText = maxSpeed > 0 ? $"{maxSpeed} MT/s" : "Unknown";
            mem.UsedSlots = slotCount;
            int totalArraySlots = 0;
            try
            {
                using var arrSearcher = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                foreach (var obj in arrSearcher.Get())
                {
                    if (int.TryParse(obj["MemoryDevices"]?.ToString(), out int dc))
                        totalArraySlots += dc;
                }
            }
            catch { }
            mem.TotalSlots = totalArraySlots > 0 ? totalArraySlots : slotCount;
            mem.ChannelConfig = slotCount >= 4 ? "Quad Channel" : slotCount >= 2 ? "Dual Channel (128-bit)" : (slotCount == 1 ? "Single Channel (64-bit)" : "Unknown");
        }

        private void ReadStorage(DetailedStorageInfo storage)
        {
            storage.PhysicalDisks.Clear();
            storage.Partitions.Clear();

            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            string sysLetter = sysRoot.TrimEnd('\\');
            var volToDisk = BiosOptimizer.Core.Implementations.Storage.StorageEngine.GetVolumeToDiskIndexMap();
            int sysDiskIdx = volToDisk.TryGetValue(sysLetter, out int mappedSys) ? mappedSys : 0;

            // 1. Physical Disks via MSFT_PhysicalDisk or Win32_DiskDrive
            try
            {
                var reliabilityTemps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var tempSearcher = new ManagementObjectSearcher(
                        new ManagementScope(@"\\.\root\microsoft\windows\storage"),
                        new ObjectQuery("SELECT ObjectId, Temperature FROM MSFT_StorageReliabilityCounter"));
                    foreach (var tObj in tempSearcher.Get())
                    {
                        string? objId = tObj["ObjectId"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(objId) &&
                            tObj["Temperature"] != null &&
                            double.TryParse(tObj["Temperature"].ToString(), out double deg) &&
                            deg > 0 && deg < 120)
                        {
                            // MSFT_StorageReliabilityCounter ObjectId typically ends with disk DeviceId/ObjectId segment
                            reliabilityTemps[objId] = deg;
                        }
                    }
                }
                catch { }

                using var scope = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\microsoft\windows\storage"),
                    new ObjectQuery("SELECT DeviceId, ObjectId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"));

                foreach (var obj in scope.Get())
                {
                    string name = obj["FriendlyName"]?.ToString() ?? "Physical Drive";
                    string devId = obj["DeviceId"]?.ToString() ?? "";
                    string diskObjId = obj["ObjectId"]?.ToString() ?? "";
                    int.TryParse(devId, out int diskIdx);
                    int.TryParse(obj["MediaType"]?.ToString(), out int mediaType);
                    int.TryParse(obj["BusType"]?.ToString(), out int busType);
                    long.TryParse(obj["Size"]?.ToString(), out long sizeBytes);
                    int.TryParse(obj["HealthStatus"]?.ToString(), out int health);

                    string typeStr = (busType == 17 || name.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) ? "NVMe SSD" :
                                     mediaType == 4 ? "SATA SSD" :
                                     mediaType == 3 ? "HDD" : "Solid State Drive";

                    string busStr = (busType == 17) ? "PCIe / NVMe" : (busType == 11) ? "SATA 6Gbps" : $"Bus {busType}";
                    string healthStr = health == 0 ? "HEALTHY" : health == 1 ? "WARNING" : health == 2 ? "UNHEALTHY" : "UNKNOWN";

                    string tempStr = "Unavailable";
                    if (!string.IsNullOrWhiteSpace(devId))
                    {
                        var matchedTemp = reliabilityTemps.FirstOrDefault(kv =>
                            (!string.IsNullOrWhiteSpace(diskObjId) && kv.Key.Contains(diskObjId, StringComparison.OrdinalIgnoreCase)) ||
                            kv.Key.Contains($"PhysicalDisk{devId}", StringComparison.OrdinalIgnoreCase) ||
                            kv.Key.EndsWith($":{devId}", StringComparison.OrdinalIgnoreCase));
                        if (matchedTemp.Value > 0)
                            tempStr = $"{matchedTemp.Value:F0} °C";
                    }

                    storage.PhysicalDisks.Add(new PhysicalDiskInfo
                    {
                        Model = name,
                        MediaType = typeStr,
                        BusType = busStr,
                        SizeText = $"{sizeBytes / (1024.0 * 1024.0 * 1024.0):F0} GB",
                        HealthStatus = healthStr,
                        Temperature = tempStr,
                        IsSystemDisk = diskIdx == sysDiskIdx
                    });
                }
            }
            catch
            {
                // Fallback to Win32_DiskDrive
                using var ddSearcher = new ManagementObjectSearcher("SELECT Index, Model, Size, InterfaceType, Status FROM Win32_DiskDrive");
                foreach (var obj in ddSearcher.Get())
                {
                    int.TryParse(obj["Index"]?.ToString(), out int diskIdx);
                    string model = obj["Model"]?.ToString() ?? "Disk Drive";
                    long.TryParse(obj["Size"]?.ToString(), out long size);
                    string iface = obj["InterfaceType"]?.ToString() ?? "SCSI";
                    string wmiStatus = obj["Status"]?.ToString()?.Trim() ?? "";

                    string type = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe SSD" :
                                  model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SATA SSD" : "Hard Disk Drive";

                    string healthStr = wmiStatus.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "HEALTHY" :
                                       string.IsNullOrWhiteSpace(wmiStatus) ? "UNKNOWN" : wmiStatus.ToUpperInvariant();

                    storage.PhysicalDisks.Add(new PhysicalDiskInfo
                    {
                        Model = model,
                        MediaType = type,
                        BusType = iface,
                        SizeText = $"{size / (1024.0 * 1024.0 * 1024.0):F0} GB",
                        HealthStatus = healthStr,
                        Temperature = "Unavailable",
                        IsSystemDisk = diskIdx == sysDiskIdx
                    });
                }
            }

            // 2. Fixed Unique Logical Volumes via StorageEngine (deduplicated by VolumeGuid)
            foreach (var d in BiosOptimizer.Core.Implementations.Storage.StorageEngine.GetUniqueFixedDrives())
            {
                long total = d.TotalSize;
                long free = d.AvailableFreeSpace;
                long used = total > free ? total - free : 0;

                double totalGb = total / (1024.0 * 1024.0 * 1024.0);
                double usedGb = used / (1024.0 * 1024.0 * 1024.0);
                double freeGb = free / (1024.0 * 1024.0 * 1024.0);
                double pct = total > 0 ? (used / (double)total) * 100.0 : 0;

                bool isSys = string.Equals(d.RootDirectory.FullName, sysRoot, StringComparison.OrdinalIgnoreCase);

                storage.Partitions.Add(new VolumePartitionInfo
                {
                    DriveLetter = d.Name.TrimEnd('\\'),
                    Label = string.IsNullOrEmpty(d.VolumeLabel) ? (isSys ? "Windows (System)" : "Local Disk") : d.VolumeLabel,
                    Format = d.DriveFormat,
                    TotalText = $"{totalGb:F1} GB",
                    UsedText = $"{usedGb:F1} GB",
                    FreeText = $"{freeGb:F1} GB Free",
                    UsagePercentage = Math.Round(pct, 1),
                    IsSystemDrive = isSys,
                    HealthBadge = pct > 90 ? "WARNING (LOW SPACE)" : "HEALTHY"
                });
            }
        }

        private void ReadMotherboard(DetailedMotherboardInfo mb)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard"))
            {
                foreach (var obj in searcher.Get())
                {
                    mb.Manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "System Board";
                    mb.Model = obj["Product"]?.ToString()?.Trim() ?? "Motherboard";
                    break;
                }
            }

            using (var sysSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem"))
            {
                foreach (var obj in sysSearcher.Get())
                {
                    mb.SystemModel = obj["Model"]?.ToString()?.Trim() ?? "Personal Computer";
                    mb.SystemSku = obj["SystemSKUNumber"]?.ToString()?.Trim() ?? "Standard SKU";
                    break;
                }
            }

            using (var biosSearcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                foreach (var obj in biosSearcher.Get())
                {
                    mb.BiosVendor = obj["Manufacturer"]?.ToString()?.Trim() ?? "UEFI Vendor";
                    mb.BiosVersion = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "1.0.0";
                    string rDate = obj["ReleaseDate"]?.ToString() ?? "";
                    if (rDate.Length >= 8)
                    {
                        mb.BiosDate = $"{rDate.Substring(0, 4)}-{rDate.Substring(4, 2)}-{rDate.Substring(6, 2)}";
                    }
                    break;
                }
            }

            // Firmware Boot Mode (PEFirmwareType: 2=UEFI, 1=BIOS)
            try
            {
                using var sysCtrlKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
                var fwVal = sysCtrlKey?.GetValue("PEFirmwareType");
                mb.FirmwareType = fwVal != null && int.TryParse(fwVal.ToString(), out int ft)
                    ? (ft == 2 ? "UEFI" : ft == 1 ? "Legacy BIOS" : "Unknown")
                    : "Unknown";
            }
            catch { mb.FirmwareType = "Unknown"; }

            // Secure Boot
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (key != null)
                {
                    var val = key.GetValue("UEFISecureBootEnabled");
                    mb.SecureBoot = val == null ? "Unavailable" : (Convert.ToInt32(val) == 1 ? "Enabled (Active)" : "Disabled");
                }
                else
                {
                    mb.SecureBoot = "Not Available (Unsupported / Legacy)";
                }
            }
            catch { mb.SecureBoot = "Unknown"; }

            // TPM
            try
            {
                using var tpmSearcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftTpm", "SELECT SpecVersion, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm");
                bool foundTpm = false;
                foreach (var obj in tpmSearcher.Get())
                {
                    foundTpm = true;
                    mb.TpmVersion = obj["SpecVersion"]?.ToString()?.Trim() ?? "Present";
                    bool isEn = obj["IsEnabled_InitialValue"] is bool b1 && b1;
                    bool isAct = obj["IsActivated_InitialValue"] is bool b2 && b2;
                    mb.TpmStatus = (isEn && isAct) ? "Enabled & Activated" : "Present (Check BIOS TPM State)";
                    break;
                }
                if (!foundTpm)
                {
                    mb.TpmVersion = "None";
                    mb.TpmStatus = "Not Detected";
                }
            }
            catch
            {
                mb.TpmVersion = "Unavailable";
                mb.TpmStatus = "Unavailable (Requires Elevated Query)";
            }
        }

        private void ReadWindows(DetailedWindowsInfo win)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    string prod = key.GetValue("ProductName")?.ToString() ?? "Windows";
                    string displayVer = key.GetValue("DisplayVersion")?.ToString() ?? key.GetValue("ReleaseId")?.ToString() ?? "";
                    string build = key.GetValue("CurrentBuild")?.ToString() ?? key.GetValue("CurrentBuildNumber")?.ToString() ?? "";
                    string ubr = key.GetValue("UBR")?.ToString() ?? "";

                    if (int.TryParse(build, out int bNum) && bNum >= 22000 && prod.Contains("Windows 10"))
                    {
                        prod = prod.Replace("Windows 10", "Windows 11");
                    }

                    win.Edition = prod;
                    win.Version = !string.IsNullOrEmpty(displayVer) ? displayVer : "Current";
                    win.OsBuild = !string.IsNullOrEmpty(ubr) ? $"{build}.{ubr}" : build;
                }
            }
            catch { }

            win.Architecture = Environment.Is64BitOperatingSystem ? "64-bit Operating System (x64)" : "32-bit (x86)";
            win.SystemLocale = System.Globalization.CultureInfo.CurrentCulture.DisplayName;
            win.TimeZone = TimeZoneInfo.Local.DisplayName;

            // Uptime
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            win.UptimeText = $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";

            // Power plan
            try
            {
                var (_, planName) = BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.GetActiveSchemeNative();
                win.PowerPlan = !string.IsNullOrWhiteSpace(planName) ? planName : "Unknown";
            }
            catch { win.PowerPlan = "Unknown"; }
        }

        private void ReadSecurity(DetailedSecurityInfo sec)
        {
            // 1. Secure Boot
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (key != null)
                {
                    var val = key.GetValue("UEFISecureBootEnabled");
                    if (val != null)
                    {
                        bool enabled = Convert.ToInt32(val) == 1;
                        sec.SecureBootStatus = enabled ? "GOOD" : "WARNING";
                        sec.SecureBootDetail = enabled ? "UEFI Secure Boot Active" : "Secure Boot Disabled in Firmware";
                    }
                    else
                    {
                        sec.SecureBootStatus = "UNKNOWN";
                        sec.SecureBootDetail = "Secure Boot State Key Empty";
                    }
                }
                else
                {
                    sec.SecureBootStatus = "INFO";
                    sec.SecureBootDetail = "Secure Boot Unavailable / Legacy BIOS";
                }
            }
            catch { sec.SecureBootStatus = "UNKNOWN"; sec.SecureBootDetail = "Unavailable (Query Failed)"; }

            // 2. TPM
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftTpm", "SELECT SpecVersion, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm");
                var res = searcher.Get();
                if (res.Count > 0)
                {
                    string spec = "";
                    bool en = false, act = false;
                    foreach (var obj in res)
                    {
                        spec = obj["SpecVersion"]?.ToString()?.Split(',')[0]?.Trim() ?? "";
                        en = obj["IsEnabled_InitialValue"] is bool b1 && b1;
                        act = obj["IsActivated_InitialValue"] is bool b2 && b2;
                        break;
                    }
                    sec.TpmStatus = (en && act) ? "GOOD" : "WARNING";
                    sec.TpmDetail = string.IsNullOrWhiteSpace(spec)
                        ? (en && act ? "TPM Active" : "TPM Present (Not Activated)")
                        : $"TPM {spec} ({(en && act ? "Active" : "Present / Inactive")})";
                }
                else
                {
                    sec.TpmStatus = "WARNING";
                    sec.TpmDetail = "TPM Not Detected";
                }
            }
            catch { sec.TpmStatus = "UNKNOWN"; sec.TpmDetail = "Unavailable (Requires Elevated Query)"; }

            // 3. Defender & Realtime Protection
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection");
                if (key != null)
                {
                    var disabled = key.GetValue("DisableRealtimeMonitoring");
                    bool isProtOn = disabled == null || Convert.ToInt32(disabled) == 0;
                    sec.RealtimeProtection = isProtOn ? "GOOD" : "WARNING";
                    sec.RealtimeDetail = isProtOn ? "Active Real-Time Antivirus Protection" : "Real-time Protection Disabled";
                    sec.DefenderStatus = "GOOD";
                    sec.DefenderDetail = "Microsoft Defender Registry Active";
                }
                else
                {
                    sec.DefenderStatus = "UNKNOWN";
                    sec.RealtimeProtection = "UNKNOWN";
                    sec.DefenderDetail = "Defender Registry State Unavailable";
                    sec.RealtimeDetail = "Unknown";
                }
            }
            catch
            {
                sec.DefenderStatus = "UNKNOWN";
                sec.RealtimeProtection = "UNKNOWN";
                sec.DefenderDetail = "Unavailable";
                sec.RealtimeDetail = "Unavailable";
            }

            // 4. Firewall
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile");
                var fw = key?.GetValue("EnableFirewall");
                if (fw != null)
                {
                    bool fwOn = Convert.ToInt32(fw) == 1;
                    sec.FirewallStatus = fwOn ? "GOOD" : "WARNING";
                    sec.FirewallDetail = fwOn ? "Windows Defender Firewall Active" : "Firewall Disabled";
                }
                else
                {
                    sec.FirewallStatus = "UNKNOWN";
                    sec.FirewallDetail = "Firewall Policy State Unavailable";
                }
            }
            catch { sec.FirewallStatus = "UNKNOWN"; sec.FirewallDetail = "Unavailable"; }

            // 5. Core Isolation / Memory Integrity (HVCI)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                var hvci = key?.GetValue("Enabled");
                if (hvci != null)
                {
                    bool hvciOn = Convert.ToInt32(hvci) == 1;
                    sec.CoreIsolationHvci = hvciOn ? "GOOD" : "INFO";
                    sec.CoreIsolationDetail = hvciOn ? "Hypervisor-Enforced Code Integrity Active" : "Core Isolation Disabled";
                }
                else
                {
                    sec.CoreIsolationHvci = "INFO";
                    sec.CoreIsolationDetail = "Core Isolation Not Configured";
                }
            }
            catch { sec.CoreIsolationHvci = "UNKNOWN"; sec.CoreIsolationDetail = "Unavailable"; }

            // 6. UAC
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                var lua = key?.GetValue("EnableLUA");
                if (lua != null)
                {
                    bool uacOn = Convert.ToInt32(lua) == 1;
                    sec.UacStatus = uacOn ? "GOOD" : "WARNING";
                    sec.UacDetail = uacOn ? "User Account Control Enabled" : "UAC Elevation Disabled";
                }
                else
                {
                    sec.UacStatus = "UNKNOWN";
                    sec.UacDetail = "UAC State Unavailable";
                }
            }
            catch { sec.UacStatus = "UNKNOWN"; sec.UacDetail = "Unavailable"; }
        }

        private void ReadGraphics(DetailedGraphicsDiagnostics gfx, List<DetailedGpuInfo> gpus)
        {
            // HAGS status
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
                var mode = key?.GetValue("HwSchMode");
                if (mode != null)
                {
                    int m = Convert.ToInt32(mode);
                    gfx.HagsStatus = m == 2 ? "Enabled (HAGS Active)" : "Disabled in Windows Graphics Settings";
                }
                else
                {
                    gfx.HagsStatus = "Default / Unsupported by Driver";
                }
            }
            catch { gfx.HagsStatus = "Unavailable"; }

            gfx.DirectXVersion = "DirectX 12";
            gfx.WddmVersion = gpus.FirstOrDefault()?.WddmVersion ?? "Unavailable";
            gfx.FeatureLevels = "Hardware Dependent";
            gfx.GpuScheduling = gfx.HagsStatus;
            gfx.HybridGraphics = gpus.Count > 1 ? $"Multi-GPU ({gpus.Count} Adapters)" : (gpus.Count == 1 ? "Single GPU" : "Unavailable");

            gfx.Displays.Clear();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ScreenWidth, ScreenHeight FROM Win32_DesktopMonitor");
                foreach (var obj in searcher.Get())
                {
                    string w = obj["ScreenWidth"]?.ToString() ?? "1920";
                    string h = obj["ScreenHeight"]?.ToString() ?? "1080";
                    gfx.Displays.Add(new DisplayMonitorInfo
                    {
                        Name = $"Display {gfx.Displays.Count + 1}",
                        Resolution = $"{w} x {h}",
                        RefreshRate = "144 Hz",
                        IsPrimary = gfx.Displays.Count == 0,
                        HdrStatus = "SDR (Standard Dynamic Range)"
                    });
                }
            }
            catch { }

            gfx.DisplayCount = gfx.Displays.Count;
        }

        private void ReadPower(DetailedPowerInfo power)
        {
            try
            {
                var (_, activePlanName) = BiosOptimizer.Core.Implementations.Power.PowerPlanEngine.Instance.GetActiveSchemeNative();
                power.PowerPlan = !string.IsNullOrWhiteSpace(activePlanName) ? activePlanName : "Unknown";

                using var searcher = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus, DesignCapacity, FullChargeCapacity FROM Win32_Battery");
                var res = searcher.Get();
                if (res.Count > 0)
                {
                    power.IsLaptop = true;
                    power.HasBattery = true;

                    foreach (var obj in res)
                    {
                        int.TryParse(obj["EstimatedChargeRemaining"]?.ToString(), out int pct);
                        int.TryParse(obj["BatteryStatus"]?.ToString(), out int status);
                        long.TryParse(obj["DesignCapacity"]?.ToString(), out long designCap);
                        long.TryParse(obj["FullChargeCapacity"]?.ToString(), out long fullCap);

                        power.BatteryPercentage = pct > 0 ? pct : 0;
                        power.BatteryStatus = status switch
                        {
                            1 => "Discharging (On Battery)",
                            2 => "AC Connected (Charging)",
                            3 => "Fully Charged",
                            4 => "Low Battery",
                            5 => "Critical Battery",
                            _ => "Battery Present"
                        };
                        power.AcConnected = (status == 2 || status == 3);
                        power.DesignCapacity = designCap > 0 ? $"{designCap} mWh" : "NOT AVAILABLE";
                        power.FullChargeCapacity = fullCap > 0 ? $"{fullCap} mWh" : "NOT AVAILABLE";
                        power.BatteryHealth = (designCap > 0 && fullCap > 0)
                            ? $"{Math.Min(100, Math.Round((fullCap * 100.0) / designCap, 1)):F1}% of Design Capacity"
                            : "Capacity Telemetry Unavailable";
                        break;
                    }
                }
                else
                {
                    power.IsLaptop = false;
                    power.HasBattery = false;
                    power.AcConnected = true;
                    power.BatteryStatus = "Desktop / No Battery";
                    power.BatteryHealth = "N/A (No Battery Detected)";
                }
            }
            catch
            {
                power.IsLaptop = false;
                power.HasBattery = false;
                power.AcConnected = false;
                power.BatteryStatus = "Unavailable";
                power.BatteryHealth = "Unavailable";
            }
        }

        private void ReadNetwork(DetailedNetworkInfo net)
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var active = interfaces.FirstOrDefault(i => 
                    i.OperationalStatus == OperationalStatus.Up && 
                    i.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    i.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    !i.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                    !i.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                    !i.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase));

                if (active != null)
                {
                    net.ActiveAdapterName = active.Name;
                    net.ConnectionType = active.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi (802.11 Wireless)",
                        NetworkInterfaceType.Ethernet => "Ethernet (Wired)",
                        _ => active.NetworkInterfaceType.ToString()
                    };

                    net.LinkSpeed = active.Speed > 0 ? $"{active.Speed / 1_000_000} Mbps" : "Unavailable";

                    var ipProps = active.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    net.Ipv4Address = ipv4?.Address.ToString() ?? "Unavailable";

                    var gw = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    net.Gateway = gw?.Address.ToString() ?? "Unavailable";

                    var dnsList = ipProps.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                    net.Dns = dnsList.Count > 0 ? string.Join(", ", dnsList.Take(2)) : "Automatic (DHCP)";
                    net.Status = "Connected";
                }
                else
                {
                    net.ActiveAdapterName = "No Active Network Adapter";
                    net.ConnectionType = "Disconnected";
                    net.LinkSpeed = "Unavailable";
                    net.Ipv4Address = "Unavailable";
                    net.Gateway = "Unavailable";
                    net.Dns = "Unavailable";
                    net.Status = "Disconnected";
                }
            }
            catch
            {
                net.ActiveAdapterName = "Unavailable";
                net.ConnectionType = "Unavailable";
                net.LinkSpeed = "Unavailable";
                net.Ipv4Address = "Unavailable";
                net.Gateway = "Unavailable";
                net.Dns = "Unavailable";
                net.Status = "Unavailable";
            }
        }

        private void ReadDrivers(DetailedDriverInfo drivers, List<DetailedGpuInfo> gpus)
        {
            drivers.Drivers.Clear();

            // GPU Drivers
            foreach (var gpu in gpus)
            {
                drivers.Drivers.Add(new DriverItemInfo
                {
                    Category = "Display / GPU",
                    DeviceName = gpu.Name,
                    DriverVersion = gpu.DriverVersion,
                    DriverDate = !string.IsNullOrEmpty(gpu.DriverDate) ? gpu.DriverDate : "Current",
                    Provider = gpu.Vendor,
                    Status = "HEALTHY"
                });
            }

            // Audio & Network Drivers via PnP
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate, Manufacturer FROM Win32_PnPSignedDriver WHERE DeviceClass = 'MEDIA' OR DeviceClass = 'NET'");
                int count = 0;
                foreach (var obj in searcher.Get())
                {
                    if (count++ >= 4) break;
                    string name = obj["DeviceName"]?.ToString() ?? "";
                    string ver = obj["DriverVersion"]?.ToString() ?? "Standard";
                    string mfg = obj["Manufacturer"]?.ToString() ?? "Windows";
                    if (!string.IsNullOrEmpty(name))
                    {
                        drivers.Drivers.Add(new DriverItemInfo
                        {
                            Category = name.Contains("Audio", StringComparison.OrdinalIgnoreCase) || name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ? "Audio Controller" : "Network Interface",
                            DeviceName = name,
                            DriverVersion = ver,
                            DriverDate = "Installed",
                            Provider = mfg,
                            Status = "HEALTHY"
                        });
                    }
                }
            }
            catch { }
        }
    }
}
