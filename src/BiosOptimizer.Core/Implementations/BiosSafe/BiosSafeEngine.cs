#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.V4.Recommendations;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Implementations.BiosSafe
{
    public enum FirmwareBootMode
    {
        Unknown,
        LegacyBios,
        Uefi
    }

    public enum MotherboardVendor
    {
        Generic,
        Asus,
        Msi,
        Gigabyte,
        Asrock,
        Dell,
        Hp,
        Lenovo
    }

    public class FirmwareCapabilitySnapshot
    {
        public FirmwareBootMode BootMode { get; set; } = FirmwareBootMode.Unknown;
        public bool IsSecureBootEnabled { get; set; }
        public bool IsTpmPresent { get; set; }
        public string TpmVersion { get; set; } = "Not Detected";
        public bool IsVirtualizationEnabledInFirmware { get; set; }
        public MotherboardVendor Vendor { get; set; } = MotherboardVendor.Generic;
        public string MotherboardManufacturer { get; set; } = "Generic";
        public string MotherboardProduct { get; set; } = "Standard Board";
        public string BiosVendor { get; set; } = "Generic";
        public string BiosVersion { get; set; } = "1.0";
        public string BiosReleaseDate { get; set; } = "Unknown";
        public bool IsReBarSupportedByGpu { get; set; }
        public bool IsReBarActive { get; set; }
        public string ReBarStatusSummary { get; set; } = "Analysis Pending";
        public List<string> ActionableRecommendations { get; set; } = new();
    }

    public class FirmwareGuidanceCard
    {
        public string SettingId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CurrentObservedState { get; set; } = string.Empty;
        public string TargetRecommendedState { get; set; } = string.Empty;
        public string WhyRecommended { get; set; } = string.Empty;
        public string UefiSetupPath { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = "Safe";
        public bool CanExecuteInWindows { get; set; }
        public bool RequiresUefiReboot { get; set; } = true;
    }

    /// <summary>
    /// Advanced V4 BIOS Safe Engine providing:
    /// 1. Comprehensive WMI/UEFI hardware and firmware capability inspection.
    /// 2. Automated Resizable BAR and Above 4G Decoding readiness validation with vendor-specific setup guidance.
    /// 3. Firmware optimization guidance cards and one-click direct Windows-to-UEFI reboot.
    /// </summary>
    public sealed class BiosSafeEngine
    {
        private static readonly Lazy<BiosSafeEngine> _instance = new(() => new BiosSafeEngine());
        public static BiosSafeEngine Instance => _instance.Value;

        private FirmwareCapabilitySnapshot? _cachedSnapshot;
        private DateTime _lastScanTime = DateTime.MinValue;
        private readonly SemaphoreSlim _scanLock = new(1, 1);

        private BiosSafeEngine()
        {
        }

        // ══════════════════════════════════════════════════════════════════════
        // CAPABILITY 1: COMPREHENSIVE WMI/UEFI HARDWARE & FIRMWARE INSPECTION
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Inspects live system firmware telemetry across UEFI boot mode, Secure Boot, TPM, Virtualization, and Motherboard.
        /// Non-blocking, strictly bounded with execution timeouts, cached with TTL, and returns safe fallbacks on error.
        /// Zero automatic reboot triggers.
        /// </summary>
        public async Task<FirmwareCapabilitySnapshot> InspectFirmwareCapabilitiesAsync(bool forceRescan = false, CancellationToken ct = default)
        {
            if (!forceRescan && _cachedSnapshot != null && (DateTime.UtcNow - _lastScanTime).TotalSeconds < 120)
            {
                return _cachedSnapshot;
            }

            if (!await _scanLock.WaitAsync(200, ct))
            {
                // If another scan is actively running, return cached if available or fallback
                return _cachedSnapshot ?? CreateDefaultFallbackSnapshot();
            }

            try
            {
                if (!forceRescan && _cachedSnapshot != null && (DateTime.UtcNow - _lastScanTime).TotalSeconds < 120)
                {
                    return _cachedSnapshot;
                }

                // Strict 3.5s bounded timeout to prevent any thread freeze
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3.5));

                var scanTask = Task.Run(() =>
                {
                    var snapshot = new FirmwareCapabilitySnapshot();

                    // 1 & 2. Detect Boot Mode and Secure Boot State via Authoritative BiosSafeAuditor
                    var sbAudit = BiosSafeAuditor.AuditSecureBoot();
                    snapshot.BootMode = sbAudit.BootMode;
                    snapshot.IsSecureBootEnabled = sbAudit.IsSecureBootEnabled == true;

                    // 3. Inspect Motherboard and BIOS via WMI with timeouts
                    try
                    {
                        using var searcherBase = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                        searcherBase.Options.Timeout = TimeSpan.FromSeconds(1.5);
                        searcherBase.Options.ReturnImmediately = true;
                        foreach (var obj in searcherBase.Get())
                        {
                            snapshot.MotherboardManufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "Generic";
                            snapshot.MotherboardProduct = obj["Product"]?.ToString()?.Trim() ?? "Standard Board";
                            break;
                        }

                        using var searcherBios = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                        searcherBios.Options.Timeout = TimeSpan.FromSeconds(1.5);
                        searcherBios.Options.ReturnImmediately = true;
                        foreach (var obj in searcherBios.Get())
                        {
                            snapshot.BiosVendor = obj["Manufacturer"]?.ToString()?.Trim() ?? "Generic";
                            snapshot.BiosVersion = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "1.0";
                            snapshot.BiosReleaseDate = obj["ReleaseDate"]?.ToString()?.Trim() ?? "Unknown";
                            break;
                        }

                        snapshot.Vendor = ClassifyVendor(snapshot.MotherboardManufacturer, snapshot.BiosVendor);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[BiosSafeEngine] WMI inspection warning: {ex.Message}");
                    }

                    // 4. Detect TPM Presence and Version (Safe Isolated WMI query)
                    try
                    {
                        using var tpmSearcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2\Security\MicrosoftTpm"), new ObjectQuery("SELECT SpecVersion, IsEnabled_InitialValue FROM Win32_Tpm"));
                        tpmSearcher.Options.Timeout = TimeSpan.FromSeconds(1.0);
                        tpmSearcher.Options.ReturnImmediately = true;
                        foreach (var obj in tpmSearcher.Get())
                        {
                            snapshot.IsTpmPresent = true;
                            snapshot.TpmVersion = obj["SpecVersion"]?.ToString()?.Trim() ?? "2.0";
                            break;
                        }
                    }
                    catch
                    {
                        snapshot.IsTpmPresent = false;
                        snapshot.TpmVersion = "Not Detected / Hidden";
                    }

                    // 5. Detect CPU Virtualization Enabled in Firmware (VT-x / AMD-V)
                    try
                    {
                        using var procSearcher = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
                        procSearcher.Options.Timeout = TimeSpan.FromSeconds(1.0);
                        procSearcher.Options.ReturnImmediately = true;
                        foreach (var obj in procSearcher.Get())
                        {
                            if (obj["VirtualizationFirmwareEnabled"] is bool virtEnabled)
                            {
                                snapshot.IsVirtualizationEnabledInFirmware = virtEnabled;
                            }
                            break;
                        }
                    }
                    catch
                    {
                        snapshot.IsVirtualizationEnabledInFirmware = false;
                    }

                    // 6. Evaluate Resizable BAR & Above 4G Decoding Readiness
                    EvaluateReBarReadiness(snapshot);

                    return snapshot;
                }, cts.Token);

                var completedTask = await Task.WhenAny(scanTask, Task.Delay(TimeSpan.FromSeconds(3.5), ct));
                if (completedTask == scanTask)
                {
                    _cachedSnapshot = await scanTask;
                    _lastScanTime = DateTime.UtcNow;
                    return _cachedSnapshot;
                }
                else
                {
                    Debug.WriteLine("[BiosSafeEngine] InspectFirmwareCapabilitiesAsync timed out; returning fallback snapshot.");
                    return _cachedSnapshot ?? CreateDefaultFallbackSnapshot();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BiosSafeEngine] InspectFirmwareCapabilitiesAsync error: {ex.Message}");
                return _cachedSnapshot ?? CreateDefaultFallbackSnapshot();
            }
            finally
            {
                _scanLock.Release();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFirmwareType(out uint firmwareType);

        /// <summary>
        /// Determines whether the host platform is booted into native UEFI firmware mode (firmwareType == 2).
        /// Returns false for Legacy BIOS or indeterminate environments.
        /// </summary>
        public bool IsUefiFirmwareSupported()
        {
            try
            {
                if (GetFirmwareType(out uint fw))
                    return fw == 2;
            }
            catch { }
            return false;
        }

        private static FirmwareCapabilitySnapshot CreateDefaultFallbackSnapshot()
        {
            var bootMode = FirmwareBootMode.Unknown;
            try
            {
                if (GetFirmwareType(out uint fw))
                    bootMode = fw == 2 ? FirmwareBootMode.Uefi : (fw == 1 ? FirmwareBootMode.LegacyBios : FirmwareBootMode.Unknown);
            }
            catch { }

            return new FirmwareCapabilitySnapshot
            {
                BootMode = bootMode,
                IsSecureBootEnabled = false,
                IsTpmPresent = false,
                TpmVersion = "Unavailable (Firmware Query Failed)",
                IsVirtualizationEnabledInFirmware = false,
                Vendor = MotherboardVendor.Generic,
                MotherboardManufacturer = "Unavailable",
                MotherboardProduct = "Unavailable",
                BiosVendor = "Unavailable",
                BiosVersion = "Unavailable",
                BiosReleaseDate = "Unavailable",
                IsReBarSupportedByGpu = false,
                IsReBarActive = false,
                ReBarStatusSummary = "Telemetry Unavailable (WMI Query Timed Out or Restricted)"
            };
        }

        private MotherboardVendor ClassifyVendor(string mfg, string bios)
        {
            string combined = $"{mfg} {bios}".ToLowerInvariant();
            if (combined.Contains("asus") || combined.Contains("asustek")) return MotherboardVendor.Asus;
            if (combined.Contains("msi") || combined.Contains("micro-star")) return MotherboardVendor.Msi;
            if (combined.Contains("gigabyte")) return MotherboardVendor.Gigabyte;
            if (combined.Contains("asrock")) return MotherboardVendor.Asrock;
            if (combined.Contains("dell") || combined.Contains("alienware")) return MotherboardVendor.Dell;
            if (combined.Contains("hp") || combined.Contains("hewlett")) return MotherboardVendor.Hp;
            if (combined.Contains("lenovo")) return MotherboardVendor.Lenovo;
            return MotherboardVendor.Generic;
        }

        // ══════════════════════════════════════════════════════════════════════
        // CAPABILITY 2: AUTOMATED RESIZABLE BAR & ABOVE 4G DECODING VALIDATION
        // ══════════════════════════════════════════════════════════════════════

        private void EvaluateReBarReadiness(FirmwareCapabilitySnapshot snapshot)
        {
            var audit = BiosSafeAuditor.AuditReBarAndAbove4G();
            
            bool gpuSupportsReBar = audit.Adapters.Any(a => a.HardwareSupportsReBar);
            bool reBarActive = audit.Adapters.Any(a => 
                (!string.IsNullOrEmpty(a.OperationalBar1Size) && !a.OperationalBar1Size.StartsWith("0")) ||
                a.LargeMemoryRangeRegistryValue == true);

            snapshot.IsReBarSupportedByGpu = gpuSupportsReBar;
            snapshot.IsReBarActive = reBarActive;
            snapshot.ReBarStatusSummary = audit.PrimaryStatusSummary;
        }

        /// <summary>
        /// Generates vendor-specific UEFI setup instructions for Above 4G Decoding and Resizable BAR.
        /// </summary>
        public string GetReBarSetupInstructions(MotherboardVendor vendor)
        {
            return vendor switch
            {
                MotherboardVendor.Asus =>
                    "1. Reboot PC into UEFI (Del / F2).\n" +
                    "2. Press F7 to enter Advanced Mode.\n" +
                    "3. Go to: Advanced > PCI Subsystem Settings.\n" +
                    "4. Set 'Above 4G Decoding' to [Enabled].\n" +
                    "5. Set 'Re-Size BAR Support' to [Auto] or [Enabled].\n" +
                    "6. Press F10 to Save and Reboot.",

                MotherboardVendor.Msi =>
                    "1. Reboot PC into UEFI (Del).\n" +
                    "2. Press F7 to switch to Advanced Mode.\n" +
                    "3. Go to: Settings > Advanced > PCIe / PCI SubSystem Settings.\n" +
                    "4. Set 'Above 4G memory/Crypto Currency mining' to [Enabled].\n" +
                    "5. Set 'Re-Size BAR Support' to [Enabled].\n" +
                    "6. Press F10 to Save and Reboot.",

                MotherboardVendor.Gigabyte =>
                    "1. Reboot PC into UEFI (Del).\n" +
                    "2. Switch to Advanced Mode (F2).\n" +
                    "3. Go to: Settings > IO Ports.\n" +
                    "4. Set 'Above 4G Decoding' to [Enabled].\n" +
                    "5. Set 'Re-Size BAR Support' to [Auto].\n" +
                    "6. Press F10 to Save and Exit.",

                MotherboardVendor.Asrock =>
                    "1. Reboot PC into UEFI (Del / F2).\n" +
                    "2. Navigate to: Advanced > Chipset Configuration.\n" +
                    "3. Set 'Above 4G Decoding' to [Enabled].\n" +
                    "4. Set 'C.A.M. (Clever Access Memory)' to [Enabled].\n" +
                    "5. Press F10 to Save and Reboot.",

                _ =>
                    "1. Reboot PC into UEFI Setup (Del or F2 during boot).\n" +
                    "2. Navigate to Advanced / PCIe / Chipset Configuration.\n" +
                    "3. Enable 'Above 4G Decoding'.\n" +
                    "4. Enable 'Resizable BAR' or 'Re-Size BAR Support'.\n" +
                    "5. Save changes (F10) and restart into Windows."
            };
        }

        // ══════════════════════════════════════════════════════════════════════
        // CAPABILITY 3: FIRMWARE GUIDANCE CARDS & ONE-CLICK DIRECT UEFI REBOOT
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generates rich, hardware-specific guidance cards for supported firmware optimizations.
        /// </summary>
        public async Task<List<FirmwareGuidanceCard>> GenerateFirmwareGuidanceCardsAsync(CancellationToken ct = default)
        {
            var snapshot = await InspectFirmwareCapabilitiesAsync(forceRescan: false, ct: ct);
            var cards = new List<FirmwareGuidanceCard>();

            // 1. Memory XMP / EXPO / DOCP Profile
            cards.Add(new FirmwareGuidanceCard
            {
                SettingId = "biossafe.manual.xmp_expo",
                Title = "Memory Performance Profile (XMP / EXPO / DOCP)",
                CurrentObservedState = "Standard JEDEC Frequency",
                TargetRecommendedState = "Profile 1 / EXPO Active (Full Speed)",
                WhyRecommended = "Unlocks your RAM kit's rated overclock frequency and low latency timings for maximum CPU gaming frame rates.",
                UefiSetupPath = "UEFI > OC / Ai Tweaker / Extreme Tweaker > XMP / EXPO Profile 1",
                RiskLevel = "Low Risk",
                CanExecuteInWindows = false,
                RequiresUefiReboot = true
            });

            // 2. Resizable BAR & Above 4G Decoding
            cards.Add(new FirmwareGuidanceCard
            {
                SettingId = "biossafe.manual.rebar",
                Title = "Resizable BAR (ReBAR) & Above 4G Decoding",
                CurrentObservedState = snapshot.ReBarStatusSummary,
                TargetRecommendedState = "Enabled (Full Concurrent GPU Access)",
                WhyRecommended = "Grants the CPU direct 100% access to GPU VRAM rather than 256MB chunks, eliminating stutter in modern games.",
                UefiSetupPath = GetReBarSetupInstructions(snapshot.Vendor),
                RiskLevel = "Low Risk",
                CanExecuteInWindows = false,
                RequiresUefiReboot = true
            });

            // 3. Hardware Virtualization (Intel VT-x / AMD SVM)
            cards.Add(new FirmwareGuidanceCard
            {
                SettingId = "biossafe.manual.virtualization",
                Title = "Hardware CPU Virtualization (VT-x / AMD-V)",
                CurrentObservedState = snapshot.IsVirtualizationEnabledInFirmware ? "Enabled in Firmware" : "Disabled",
                TargetRecommendedState = "Enabled",
                WhyRecommended = "Required for Windows Hypervisor, Core Isolation Memory Integrity, and Android Emulator virtualization.",
                UefiSetupPath = "UEFI > Advanced > CPU Configuration > Intel Virtualization Tech / SVM Mode [Enabled]",
                RiskLevel = "Safe",
                CanExecuteInWindows = false,
                RequiresUefiReboot = true
            });

            return cards;
        }

        /// <summary>
        /// Privileged direct Windows-to-UEFI firmware reboot trigger.
        /// Reboots the machine directly into UEFI setup without requiring keyboard timing.
        /// </summary>
        public bool RequestDirectUefiReboot(out string message)
        {
            if (!IsUefiFirmwareSupported())
            {
                message = "Direct reboot to UEFI (/fw) is only supported on UEFI systems. Your system is booted in Legacy BIOS mode; access BIOS setup manually on reboot (press Del or F2).";
                return false;
            }

            try
            {
                // Save pending session state for post-reboot verification
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorOptimizer");
                Directory.CreateDirectory(appData);
                string path = Path.Combine(appData, "pending_uefi_session.json");
                var sessionData = new
                {
                    SessionId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    Actions = new[] { "XMP_EXPO", "RESIZABLE_BAR", "VIRTUALIZATION" },
                    ExpectedStatus = "Enabled"
                };
                File.WriteAllText(path, JsonSerializer.Serialize(sessionData));

                var psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/r /fw /t 2",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var p = Process.Start(psi);
                message = "Initiating direct restart into UEFI Firmware Settings in 2 seconds...";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Direct UEFI reboot request failed: {ex.Message}. Please restart manually and press Del or F2.";
                return false;
            }
        }
    }
}
