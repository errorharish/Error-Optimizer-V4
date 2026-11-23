#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BiosOptimizer.Core.Interfaces
{
    public enum DetectedGpuVendor
    {
        Nvidia,
        Amd,
        Intel,
        Unknown
    }

    public class GpuDeviceSnapshot
    {
        public string Name { get; set; } = "Display Adapter";
        public DetectedGpuVendor Vendor { get; set; } = DetectedGpuVendor.Unknown;
        public string VendorName { get; set; } = "Unknown";
        public string DriverVersion { get; set; } = "Unavailable";
        public string DriverDate { get; set; } = "Unavailable";
        public string DeviceId { get; set; } = "";
        public uint VendorId { get; set; }
        public uint PciDeviceId { get; set; }
        public string RegistryClassKey { get; set; } = "";
        public double VramGb { get; set; }
        public double SharedVramGb { get; set; }
        public string VramFormatted { get; set; } = "Unavailable";
        public string WddmVersion { get; set; } = "Unavailable";
        public bool IsDiscrete { get; set; }
        public bool IsPrimary { get; set; }
        public bool SupportsHags { get; set; }
        public string HagsDiagnosticReason { get; set; } = "";
        public bool IsNvidiaRtx { get; set; }
        public bool IsAmdRadeonRx { get; set; }
        public bool IsIntelArc { get; set; }
        public bool SupportsDirectX12Ultimate { get; set; }
        public bool SupportsVrr { get; set; }
    }

    public class CpuHardwareSnapshot
    {
        public string Name { get; set; } = "Processor";
        public int LogicalProcessors { get; set; } = Environment.ProcessorCount;
        public int PhysicalCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
        public double BaseFrequencyGhz { get; set; }
        public string Architecture { get; set; } = "x64";
        public bool IsIntel { get; set; }
        public bool IsAmd { get; set; }
        public bool IsHybridCpu { get; set; }
        public bool IsAmd3DVcache { get; set; }
        public bool IsAmdRyzenAi { get; set; }
        public int PerformanceCores { get; set; }
        public int EfficientCores { get; set; }
        public bool IsDualCcdAmd3DVcache { get; set; }
        public int L3CacheInstanceCount { get; set; } = 1;
        public List<long> L3CacheSizesBytes { get; set; } = new();
        public bool SupportsCpuSets { get; set; }
        public bool HasThreadDirectorSupport { get; set; }
        public int ProcessorGroupCount { get; set; } = 1;
        public int NumaNodeCount { get; set; } = 1;
    }

    public class StorageHardwareSnapshot
    {
        public bool HasNvmeDrive { get; set; } = true;
        public bool HasSataSsd { get; set; }
        public bool HasHdd { get; set; }
        public bool SystemDriveIsNvme { get; set; } = true;
        public bool IsTrimEnabled { get; set; } = true;
        public string SystemDriveType { get; set; } = "NVMe SSD";
        public string PrimaryDriveInterface { get; set; } = "NVMe / PCIe";
    }

    public class FirmwareHardwareSnapshot
    {
        public bool IsUefi { get; set; } = true;
        public string SecureBootState { get; set; } = "Enabled";
        public string TpmVersion { get; set; } = "2.0";
        public bool VirtualizationFirmwareEnabled { get; set; } = true;
        public bool IommuDmaProtectionEnabled { get; set; } = true;
        public bool ResizableBarSupported { get; set; } = true;
        public bool Above4GDecodingEnabled { get; set; } = true;
        public string OemVendor { get; set; } = "Universal";
    }

    public class MemoryHardwareSnapshot
    {
        public double InstalledPhysicalGb { get; set; }
        public double UsablePhysicalGb { get; set; }
        public double HardwareReservedGb { get; set; }
        public double AvailablePhysicalGb { get; set; }
        public double UsedPhysicalGb { get; set; }
        public double LoadPercentage { get; set; }
        public double CommitUsedGb { get; set; }
        public double CommitLimitGb { get; set; }
        public double CommitPercentage { get; set; }
        public int SpeedMhz { get; set; }
        public string MemoryType { get; set; } = "Unknown";
        public int DimmCount { get; set; }
        public int TotalSlots { get; set; }
        public string ChannelMode { get; set; } = "Unknown";
        public string SpecFormatted { get; set; } = "System Memory";
        public string InstalledFormatted => InstalledPhysicalGb > 0 ? $"{InstalledPhysicalGb:F1} GB" : "Unavailable";
        public string UsableFormatted => UsablePhysicalGb > 0 ? $"{UsablePhysicalGb:F1} GB" : "Unavailable";
    }

    public class WindowsEnvironmentSnapshot
    {
        public string ProductName { get; set; } = "Windows 11";
        public string DisplayVersion { get; set; } = "";
        public string BuildNumber { get; set; } = "26100";
        public int Ubr { get; set; } = 0;
        public string Edition { get; set; } = "Home Single Language";
        public string Architecture { get; set; } = "64-bit";

        public string FullDisplayString
        {
            get
            {
                string buildStr = Ubr > 0 ? $"{BuildNumber}.{Ubr}" : BuildNumber;
                string verPart = !string.IsNullOrWhiteSpace(DisplayVersion) ? $"{DisplayVersion}, " : "";
                return $"{ProductName} ({verPart}Build {buildStr}) {Architecture}";
            }
        }
    }

    public class HardwareSnapshot
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string MachineName { get; set; } = Environment.MachineName;
        public string Manufacturer { get; set; } = "Unknown";
        public string Model { get; set; } = "Unknown";
        public string ComputerModelFormatted => $"{Manufacturer} {Model}".Trim();
        
        public CpuHardwareSnapshot Cpu { get; set; } = new();
        public MemoryHardwareSnapshot Memory { get; set; } = new();
        public List<GpuDeviceSnapshot> Gpus { get; set; } = new();
        public StorageHardwareSnapshot Storage { get; set; } = new();
        public FirmwareHardwareSnapshot Firmware { get; set; } = new();
        public WindowsEnvironmentSnapshot Windows { get; set; } = new();
        
        public bool IsLaptop { get; set; }
        public bool IsOnBattery { get; set; }
        public int BatteryPercentage { get; set; } = 100;
        public string Motherboard { get; set; } = "";
        public string BiosVersion { get; set; } = "";

        public GpuDeviceSnapshot PrimaryGpu => Gpus.Find(g => g.IsPrimary) ?? (Gpus.Count > 0 ? Gpus[0] : new GpuDeviceSnapshot());
        public bool HasNvidia => Gpus.Exists(g => g.Vendor == DetectedGpuVendor.Nvidia);
        public bool HasAmd => Gpus.Exists(g => g.Vendor == DetectedGpuVendor.Amd);
        public bool HasIntel => Gpus.Exists(g => g.Vendor == DetectedGpuVendor.Intel);
        public bool IsHybridGpu => Gpus.Count > 1;
        public bool HasDiscreteGpu => Gpus.Exists(g => g.IsDiscrete);
        public bool HasTouchScreen { get; set; }
        public bool HasAudioDevice { get; set; } = true;
    }

    public interface IHardwareDetectionService
    {
        HardwareSnapshot GetSnapshot(bool forceRefresh = false);
        Task<HardwareSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken ct = default);
        void InvalidateCache();
        event Action<HardwareSnapshot>? HardwareChanged;
    }
}
