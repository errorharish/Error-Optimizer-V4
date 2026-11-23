#pragma warning disable CA1416 // Validate platform compatibility

using System.Management;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using Microsoft.Win32;

namespace BiosOptimizer.Detection;

public class EnvironmentDetector : IEnvironmentDetector
{
    private readonly IEnumerable<IWorkloadDetector> _workloadDetectors;

    public EnvironmentDetector(IEnumerable<IWorkloadDetector> workloadDetectors)
    {
        _workloadDetectors = workloadDetectors;
    }

    public EnvironmentContext Detect()
    {
        var context = new EnvironmentContext();
        DetectOS(context);
        DetectHardware(context);
        DetectEnvironment(context);
        DetectWorkloads(context);
        DetectPerformanceProfile(context);
        
        var machineDetector = new BiosOptimizer.Core.Implementations.MachineProfileDetector();
        context.MachineProfile = machineDetector.DetectMachine();

        // Override laptop/desktop with authoritative chassis-type from MachineProfileDetector
        if (context.MachineProfile.MachineType == "Laptop")
        {
            context.IsLaptop = true;
            context.IsDesktop = false;
            context.HasBattery = true;
        }
        else if (context.MachineProfile.MachineType == "Desktop")
        {
            // Only override if battery wasn't found (battery wins for correctness)
            if (!context.HasBattery)
            {
                context.IsLaptop = false;
                context.IsDesktop = true;
            }
        }

        // Sync IsOnBattery state
        if (context.HasBattery && context.MachineProfile.IsOnBattery)
        {
            // Already captured in MachineProfile.IsOnBattery
        }

        return context;
    }

    private void DetectOS(EnvironmentContext context)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var caption = obj["Caption"]?.ToString() ?? "";
                var buildNumber = obj["BuildNumber"]?.ToString() ?? "";
                var architecture = obj["OSArchitecture"]?.ToString() ?? "";
                
                context.IsWindows10 = caption.Contains("Windows 10", StringComparison.OrdinalIgnoreCase);
                context.IsWindows11 = caption.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
                context.WindowsBuild = buildNumber;
                context.Architecture = architecture;
                context.Edition = caption;
                break;
            }
        }
        catch { /* Fallback */ }
    }

    private void DetectHardware(EnvironmentContext context)
    {
        try
        {
            using var biosSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS");
            foreach (var obj in biosSearcher.Get())
            {
                context.BIOSVendor = obj["Manufacturer"]?.ToString() ?? "";
                context.BIOSVersion = obj["SMBIOSBIOSVersion"]?.ToString() ?? "";
                context.BIOSReleaseDate = obj["ReleaseDate"]?.ToString() ?? "";
                break;
            }

            using var systemSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (var obj in systemSearcher.Get())
            {
                context.Manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                context.Model = obj["Model"]?.ToString() ?? "";
                context.SystemFamily = obj["SystemFamily"]?.ToString() ?? "";
                context.SystemSKU = obj["SystemSKUNumber"]?.ToString() ?? "";
                
                if (long.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out var ramBytes))
                {
                    context.RamSizeBytes = ramBytes;
                    context.RamSizeGb = (long)Math.Ceiling(ramBytes / (1024.0 * 1024.0 * 1024.0));
                }

                // Domain detection
                var domainRole = obj["DomainRole"]?.ToString();
                context.IsDomainJoined = domainRole != "0" && domainRole != "2";
                context.DomainName = obj["Domain"]?.ToString() ?? "";
                break;
            }

            using var procSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (var obj in procSearcher.Get())
            {
                context.CpuName = obj["Name"]?.ToString() ?? "";
                if (int.TryParse(obj["NumberOfCores"]?.ToString(), out var cores)) context.PhysicalCoreCount = cores;
                if (int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString(), out var threads)) context.LogicalCoreCount = threads;
                
                var virt = obj["VirtualizationFirmwareEnabled"]?.ToString();
                context.VirtualizationSupported = virt != null;
                context.VirtualizationEnabled = virt != null && bool.TryParse(virt, out var v) && v;
                break;
            }

            var dxgiGpus = BiosOptimizer.Core.Implementations.DxgiGpuReader.EnumerateAdapters();
            if (dxgiGpus.Count > 0)
            {
                bool first = true;
                var preferred = dxgiGpus.Find(g => g.IsDedicatedGpu) ?? dxgiGpus[0];
                foreach (var g in dxgiGpus)
                {
                    string vendor = g.VendorId switch
                    {
                        0x10DE => "NVIDIA",
                        0x1002 => "AMD",
                        0x8086 => "Intel",
                        _ => "Unknown"
                    };
                    context.Gpus.Add(new GpuInfo
                    {
                        Name = g.Description,
                        Vendor = vendor,
                        VramBytes = (long)g.DedicatedVideoMemoryBytes,
                        IsPrimary = ReferenceEquals(g, preferred) || (first && dxgiGpus.Count == 1)
                    });
                    first = false;
                }
                context.GpuName = preferred.Description;
                context.GpuVendor = preferred.VendorId switch
                {
                    0x10DE => "NVIDIA",
                    0x1002 => "AMD",
                    0x8086 => "Intel",
                    _ => "Unknown"
                };
                context.VramBytes = (long)preferred.DedicatedVideoMemoryBytes;
            }
            else
            {
                using var gpuSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                bool isFirstGpu = true;
                foreach (var obj in gpuSearcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var vendor = obj["AdapterCompatibility"]?.ToString() ?? "";
                    var driver = obj["DriverVersion"]?.ToString() ?? "";
                    long.TryParse(obj["AdapterRAM"]?.ToString(), out var vram);

                    context.Gpus.Add(new GpuInfo
                    {
                        Name = name,
                        Vendor = vendor,
                        VramBytes = vram,
                        DriverVersion = driver,
                        IsPrimary = isFirstGpu
                    });

                    bool discreteLike = !name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                    if (isFirstGpu || discreteLike)
                    {
                        context.GpuName = name;
                        context.GpuVendor = vendor;
                        context.GpuDriverVersion = driver;
                        context.VramBytes = vram;
                        if (discreteLike) isFirstGpu = false;
                    }
                }
            }
            
            // Storage detection — try MSFT_PhysicalDisk first (MediaType: 3=HDD, 4=SSD, 5=SCM/NVMe)
            try
            {
                var storageSearcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\microsoft\windows\storage"),
                    new ObjectQuery("SELECT MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk"));
                int highestMediaType = 0;
                bool hasNvme = false;
                foreach (var obj in storageSearcher.Get())
                {
                    if (int.TryParse(obj["MediaType"]?.ToString(), out int mt) && mt > highestMediaType)
                        highestMediaType = mt;
                    if (int.TryParse(obj["BusType"]?.ToString(), out int bt) && bt == 17) // 17=NVMe
                        hasNvme = true;
                    var fname = obj["FriendlyName"]?.ToString() ?? "";
                    if (fname.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) hasNvme = true;
                }
                if (hasNvme) context.StorageType = "NVMe";
                else if (highestMediaType == 4) context.StorageType = "SSD";
                else if (highestMediaType == 3) context.StorageType = "HDD";
                else context.StorageType = "Unknown";
            }
            catch
            {
                // Fallback: check Win32_DiskDrive for NVMe or SSD keywords
                try
                {
                    using var ddSearcher = new ManagementObjectSearcher("SELECT Model FROM Win32_DiskDrive");
                    bool nvme = false, ssd = false;
                    foreach (var obj in ddSearcher.Get())
                    {
                        var model = obj["Model"]?.ToString() ?? "";
                        if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) nvme = true;
                        if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("Solid", StringComparison.OrdinalIgnoreCase)) ssd = true;
                    }
                    context.StorageType = nvme ? "NVMe" : ssd ? "SSD" : "HDD";
                }
                catch { context.StorageType = "Unknown"; }
            }

        }
        catch { /* Ignore failures in WMI access */ }
    }

    private void DetectEnvironment(EnvironmentContext context)
    {
        // Primary laptop detection: chassis WMI type (set in MachineProfileDetector which runs after)
        // We use battery presence + model string as a fallback until MachineProfile is merged
        var laptopKeywords = new[] { "Laptop", "Notebook", "Mobile", "Book" };
        bool modelHasLaptop = laptopKeywords.Any(k => context.Model.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                       context.SystemFamily.Contains(k, StringComparison.OrdinalIgnoreCase));
        context.IsLaptop = modelHasLaptop;
        context.IsDesktop = !modelHasLaptop;
        
        try
        {
            using var batterySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            context.HasBattery = batterySearcher.Get().Count > 0;
            if (context.HasBattery) { context.IsLaptop = true; context.IsDesktop = false; }
        }
        catch { }

        try
        {
            using var printerSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Network=FALSE");
            context.HasPrinter = printerSearcher.Get().Count > 0 ? DeviceState.Available : DeviceState.Unavailable;
        }
        catch { context.HasPrinter = DeviceState.Unknown; }

        try
        {
            using var pnpSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'");
            context.HasBluetooth = pnpSearcher.Get().Count > 0 ? DeviceState.Available : DeviceState.Unavailable;
        }
        catch { context.HasBluetooth = DeviceState.Unknown; }
        
        try
        {
            using var touchSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass='HIDClass' AND Name LIKE '%Touch screen%'");
            context.HasTouchscreen = touchSearcher.Get().Count > 0 ? DeviceState.Available : DeviceState.Unavailable;
        }
        catch { context.HasTouchscreen = DeviceState.Unknown; }

        try
        {
            using var cameraSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass='Camera' OR PNPClass='Image'");
            context.HasCamera = cameraSearcher.Get().Count > 0 ? DeviceState.Available : DeviceState.Unavailable;
        }
        catch { context.HasCamera = DeviceState.Unknown; }

        // BitLocker check
        context.BitLockerActive = false;
        context.BitLockerSupported = true;
        try
        {
            using var blSearcher = new ManagementObjectSearcher(
                @"\\.\root\cimv2\security\microsoftvolumeencryption",
                "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'");
            foreach (var obj in blSearcher.Get())
            {
                var status = Convert.ToInt32(obj["ProtectionStatus"]);
                context.BitLockerActive = status == 1; // 1 = Protection On
                break;
            }
        }
        catch { }
    }

    private void DetectWorkloads(EnvironmentContext context)
    {
        foreach (var detector in _workloadDetectors)
        {
            var workload = detector.DetectInstallation();
            if (workload != null)
            {
                context.Workloads.Add(workload);
            }
        }
    }

    private void DetectPerformanceProfile(EnvironmentContext context)
    {
        context.HardwareProfile.CPUName = context.CpuName;
        context.HardwareProfile.PhysicalCores = context.PhysicalCoreCount;
        context.HardwareProfile.LogicalProcessors = context.LogicalCoreCount;
        context.HardwareProfile.RAM = context.RamSizeBytes;
        context.HardwareProfile.GPU = context.GpuName;
        context.HardwareProfile.GPUVendor = context.GpuVendor;
        context.HardwareProfile.VRAM = context.VramBytes;
        context.HardwareProfile.GPUDriver = context.GpuDriverVersion;
        context.HardwareProfile.StorageType = context.StorageType;
        context.HardwareProfile.IsSSD = context.StorageType.Equals("SSD", StringComparison.OrdinalIgnoreCase);
        context.HardwareProfile.VirtualizationSupported = context.VirtualizationSupported;
        context.HardwareProfile.VirtualizationEnabled = context.VirtualizationEnabled;
        context.HardwareProfile.BatteryPresent = context.HasBattery;
        context.HardwareProfile.IsLaptop = context.IsLaptop;
        
        // HAGS Detection
        try
        {
            using var hagsKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (hagsKey != null)
            {
                var hws = hagsKey.GetValue("HwSchMode");
                if (hws != null)
                {
                    context.HardwareProfile.HagsSupported = true;
                    context.HardwareProfile.HagsEnabled = hws.ToString() == "2";
                }
            }

            if (!context.HardwareProfile.HagsSupported && int.TryParse(context.WindowsBuild, out int buildNum) && buildNum >= 19041)
            {
                string gpuUpper = (context.GpuName ?? "").ToUpperInvariant();
                bool isNvidiaHags = gpuUpper.Contains("NVIDIA") && (
                    gpuUpper.Contains("GTX 10") || gpuUpper.Contains("1050") || gpuUpper.Contains("1060") ||
                    gpuUpper.Contains("1070") || gpuUpper.Contains("1080") || gpuUpper.Contains("GT 1030") ||
                    gpuUpper.Contains("TITAN X") || gpuUpper.Contains("TITAN XP") || gpuUpper.Contains("QUADRO P") ||
                    gpuUpper.Contains("TESLA P") || gpuUpper.Contains("MX150") || gpuUpper.Contains("MX250") ||
                    gpuUpper.Contains("MX350") || gpuUpper.Contains("RTX") || gpuUpper.Contains("GTX 16") ||
                    gpuUpper.Contains("TITAN RTX") || gpuUpper.Contains("QUADRO RTX") || gpuUpper.Contains("RTX A") ||
                    gpuUpper.Contains("A100") || gpuUpper.Contains("A10") || gpuUpper.Contains("A40") ||
                    gpuUpper.Contains("A6000") || gpuUpper.Contains("H100") || gpuUpper.Contains("B100"));

                bool isAmdHags = (gpuUpper.Contains("AMD") || gpuUpper.Contains("RADEON")) && (
                    gpuUpper.Contains("RX 5300") || gpuUpper.Contains("RX 5500") || gpuUpper.Contains("RX 5600") ||
                    gpuUpper.Contains("RX 5700") || gpuUpper.Contains("RX 6") || gpuUpper.Contains("RX 7") ||
                    gpuUpper.Contains("RX 8") || gpuUpper.Contains("680M") || gpuUpper.Contains("780M") ||
                    gpuUpper.Contains("880M") || gpuUpper.Contains("PRO W5") || gpuUpper.Contains("PRO W6") ||
                    gpuUpper.Contains("PRO W7"));

                bool isIntelHags = gpuUpper.Contains("INTEL") && (gpuUpper.Contains("ARC") || gpuUpper.Contains("XE-LPG") || gpuUpper.Contains("CORE ULTRA"));

                if (isNvidiaHags || isAmdHags || isIntelHags)
                {
                    context.HardwareProfile.HagsSupported = true;
                    context.HardwareProfile.HagsEnabled = false;
                }
            }
        }
        catch { }

        // Power Plan Detection
        try
        {
            // Simple query via powercfg /getactivescheme parsing could be used, or WMI
            using var searcher = new ManagementObjectSearcher(@"root\cimv2\power", "SELECT * FROM Win32_PowerPlan WHERE IsActive=true");
            foreach (var obj in searcher.Get())
            {
                context.HardwareProfile.PowerPlan = obj["ElementName"]?.ToString() ?? "";
                break;
            }
        }
        catch { }

        // Low End Classification
        if (context.RamSizeGb <= 8 || context.PhysicalCoreCount <= 4 || context.StorageType.Equals("HDD", StringComparison.OrdinalIgnoreCase))
        {
            context.Classification = LowEndClassification.LowEnd;
        }
        else if (context.RamSizeGb >= 32 && context.PhysicalCoreCount >= 8 && context.HardwareProfile.IsSSD)
        {
            context.Classification = LowEndClassification.HighPerformance;
        }
        else
        {
            context.Classification = LowEndClassification.Standard;
        }
    }
}
#pragma warning restore CA1416
