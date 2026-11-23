using BiosOptimizer.Core.Interfaces;
using BiosOptimizer.Core.Models;
using BiosOptimizer.Core.Services;
using System;
using System.Linq;

namespace BiosOptimizer.Core.ActionHandlers;

public class SetRegistryValueHandler : IActionHandler
{
    private readonly IRegistryManager _registryManager;

    public SetRegistryValueHandler(IRegistryManager registryManager)
    {
        _registryManager = registryManager;
    }

    public string ActionName => "SetRegistryValue";

    public TargetState CheckAvailability(OptimizationEntry entry, EnvironmentContext context)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Target))
            return TargetState.NotApplicable;

        // Security mitigation protection: Spectre mitigations cannot and must not be weakened
        if (entry.Target.Contains("FeatureSettingsOverride", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("spectre", StringComparison.OrdinalIgnoreCase) ||
            (entry.DisplayName?.Contains("Spectre", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return TargetState.Protected;
        }

        // Windows Kernel Storage driver protection: Storport parameters under stornvme are protected
        if (entry.Target.Contains(@"\Services\stornvme", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("storport_command_timeout", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("nvme_command_timeout", StringComparison.OrdinalIgnoreCase))
        {
            return TargetState.Protected;
        }

        var parts = entry.Target.Split('\\', 2);
        if (parts.Length < 2) return TargetState.NotApplicable;

        var hive = parts[0];
        var subParts = parts[1].Split('\\');
        if (subParts.Length < 2) return TargetState.NotApplicable;

        bool isHklm = hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ||
                      hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase);
        bool isHkcu = hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ||
                      hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);

        if (!isHklm && !isHkcu)
        {
            return TargetState.NotApplicable;
        }

        var path = string.Join("\\", subParts.Take(subParts.Length - 1));

        // Hardware/firmware prerequisites check for platform-specific keys
        if (entry.Target.Contains("DmaSecurity", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("dma_remapping", StringComparison.OrdinalIgnoreCase) ||
            (entry.DisplayName?.Contains("DMA Remapping", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            bool hasDma = false;
            try
            {
                using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var dgKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\KernelDmaProtection", false);
                if (dgKey != null && dgKey.GetValue("Enabled") is int en && en == 1) hasDma = true;
                using var dmaKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DmaSecurity", false);
                if (dmaKey != null && dmaKey.GetValue("DefaultPolicy") != null) hasDma = true;
            }
            catch { }
            if (!hasDma) return TargetState.NotAvailable;
        }

        // Preflight non-mutating check of registry tree existence
        try
        {
            var regHive = isHklm ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser;
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(regHive, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path, false);
            if (key != null)
            {
                return TargetState.Ready;
            }

            // Parent key does not exist. Check if path is hardware/subsystem-dependent:
            if (IsHardwareOrDriverSpecificPath(path))
            {
                // Key does not exist and belongs to specific hardware/driver -> hardware/driver absent on this system
                return TargetState.NotAvailable;
            }

            // For standard configurable Windows policy or OS settings paths, verify that ancestor key exists
            if (IsStandardConfigurablePath(path, baseKey))
            {
                return TargetState.Ready;
            }

            return TargetState.NotAvailable;
        }
        catch (System.Security.SecurityException)
        {
            return TargetState.NotApplicable;
        }
        catch (UnauthorizedAccessException)
        {
            return TargetState.NotApplicable;
        }
        catch
        {
            return TargetState.NotAvailable;
        }
    }

    private static bool IsHardwareOrDriverSpecificPath(string path)
    {
        if (path.Contains(@"SYSTEM\CurrentControlSet\Services\amdppm", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"SYSTEM\CurrentControlSet\Services\amdsata", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"SYSTEM\CurrentControlSet\Services\amdfendr", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"SYSTEM\CurrentControlSet\Services\nvlddmkm", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"Software\NVIDIA Corporation", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"SYSTEM\CurrentControlSet\Services\stornvme", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"SYSTEM\CurrentControlSet\Control\DmaSecurity", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"{4d36e972-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"Control\Video\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"Control\GraphicsDrivers\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static bool IsStandardConfigurablePath(string path, Microsoft.Win32.RegistryKey baseKey)
    {
        var segments = path.Split('\\');
        for (int i = segments.Length - 1; i >= 1; i--)
        {
            var ancestor = string.Join("\\", segments.Take(i));
            try
            {
                using var testKey = baseKey.OpenSubKey(ancestor, false);
                if (testKey != null)
                {
                    if (ancestor.StartsWith(@"SOFTWARE\Policies", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"SOFTWARE\Microsoft\Windows", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"Software\Microsoft\Windows", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"Software\Policies", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"System\GameConfigStore", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"Control Panel\Desktop", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"Control Panel\Mouse", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"SYSTEM\CurrentControlSet\Control\Session Manager", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"SYSTEM\CurrentControlSet\Control\Power", StringComparison.OrdinalIgnoreCase) ||
                        ancestor.StartsWith(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    public OptimizationResult Apply(OptimizationEntry entry)
    {
        var result = new OptimizationResult { ItemId = entry.Id, DisplayName = entry.DisplayName, Category = entry.Category, ActionName = ActionName };

        // Never weaken Spectre mitigations: Speculative execution mitigations are enforced by CPU hardware & protected by Windows VBS
        if (entry.Target.Contains("FeatureSettingsOverride", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("spectre", StringComparison.OrdinalIgnoreCase) ||
            (entry.DisplayName?.Contains("Spectre", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            result.Status = ResultStatus.Protected;
            result.Message = "Speculative execution mitigations (Spectre Variant 2) are enforced by CPU hardware and protected by Windows Virtualization-Based Security (VBS). Weakening hardware mitigations is prohibited by system safety policy.";
            return result;
        }

        // Never force-write protected driver settings: Storport miniport driver parameters under stornvme are kernel storage infrastructure
        if (entry.Target.Contains(@"\Services\stornvme", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("storport_command_timeout", StringComparison.OrdinalIgnoreCase) ||
            entry.Id.Contains("nvme_command_timeout", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = ResultStatus.Protected;
            result.Message = "NVMe Storport driver parameters under 'stornvme' are protected Windows kernel storage infrastructure. Modifying driver reset timeouts on active boot storage controllers is restricted by driver safety policy.";
            return result;
        }

        var parts = entry.Target.Split('\\', 2);
        if (parts.Length < 2)
        {
            result.Status = ResultStatus.Failed;
            result.Message = "Invalid registry target format. Use HIVE\\Path\\ValueName format (e.g. HKCU\\Software\\Key\\Value).";
            return result;
        }

        var hive = parts[0];
        var subParts = parts[1].Split('\\');
        if (subParts.Length < 2)
        {
            result.Status = ResultStatus.Failed;
            result.Message = "Invalid registry target path.";
            return result;
        }
        
        var valueName = subParts.Last();
        var path = string.Join("\\", subParts.Take(subParts.Length - 1));

        // Value parsing
        object targetValue = 0;
        var targetValueType = RegistryValueType.DWord;
        
        var existingBefore = _registryManager.ReadValue(hive, path, valueName);
        if (entry.Value is System.Text.Json.JsonElement jsonElement)
        {
            switch (jsonElement.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    targetValue = jsonElement.GetString() ?? "";
                    targetValueType = ResolveStringTypedRegistryKind(entry.ValueType, valueName, existingBefore?.ValueType, targetValue.ToString() ?? "", out targetValue);
                    break;
                case System.Text.Json.JsonValueKind.Number:
                    if (jsonElement.TryGetInt32(out int iVal))
                    {
                        targetValue = iVal;
                        targetValueType = RegistryValueType.DWord;
                    }
                    else if (jsonElement.TryGetInt64(out long lVal))
                    {
                        targetValue = lVal;
                        targetValueType = RegistryValueType.QWord;
                    }
                    break;
                case System.Text.Json.JsonValueKind.True:
                    targetValue = 1;
                    targetValueType = RegistryValueType.DWord;
                    break;
                case System.Text.Json.JsonValueKind.False:
                    targetValue = 0;
                    targetValueType = RegistryValueType.DWord;
                    break;
                default:
                    targetValue = jsonElement.ToString();
                    targetValueType = RegistryValueType.String;
                    break;
            }
        }
        else if (entry.Value is string s)
        {
            targetValueType = ResolveStringTypedRegistryKind(entry.ValueType, valueName, existingBefore?.ValueType, s, out targetValue);
        }
        else if (entry.Value is int intVal)
        {
            targetValue = intVal;
            targetValueType = RegistryValueType.DWord;
        }
        else if (entry.Value is bool b)
        {
            targetValue = b ? 1 : 0;
            targetValueType = RegistryValueType.DWord;
        }
        else if (entry.Value != null)
        {
            targetValue = entry.Value;
        }

        bool isInterfacesTarget =
            path.Equals(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", StringComparison.OrdinalIgnoreCase) &&
            (valueName.Equals("TcpAckFrequency", StringComparison.OrdinalIgnoreCase) || valueName.Equals("TCPNoDelay", StringComparison.OrdinalIgnoreCase));

        string regProfile = entry.SourceProfile is { Length: > 0 } sp ? sp : "Manual";

        // Interface-specific TCP settings targeted at root Interfaces container: route to active interface GUIDs
        if (isInterfacesTarget)
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                int intValToSet = targetValue is int iv ? iv : (int.TryParse(targetValue.ToString(), out int parsed) ? parsed : 1);

                // 1. State-first non-mutating preflight: check if all active interfaces already have optimal value
                using (var readOnlyInterfacesKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", false))
                {
                    if (readOnlyInterfacesKey != null)
                    {
                        int activeInterfaces = 0;
                        int optimalInterfaces = 0;
                        foreach (var guid in readOnlyInterfacesKey.GetSubKeyNames())
                        {
                            using var ifSubKey = readOnlyInterfacesKey.OpenSubKey(guid, false);
                            if (ifSubKey != null)
                            {
                                var ipObj = ifSubKey.GetValue("IPAddress") ?? ifSubKey.GetValue("DhcpIPAddress");
                                if (ipObj != null)
                                {
                                    activeInterfaces++;
                                    var currentVal = ifSubKey.GetValue(valueName);
                                    if (currentVal is int cInt && cInt == intValToSet)
                                    {
                                        optimalInterfaces++;
                                    }
                                    else if (currentVal != null && int.TryParse(currentVal.ToString(), out int cParsed) && cParsed == intValToSet)
                                    {
                                        optimalInterfaces++;
                                    }
                                }
                            }
                        }

                        if (activeInterfaces > 0 && optimalInterfaces == activeInterfaces)
                        {
                            result.Status = ResultStatus.AlreadyOptimized;
                            result.Message = $"All active network interfaces ({activeInterfaces}) already have {valueName}={intValToSet}.";
                            return result;
                        }
                    }
                }

                // 2. Mutation pass: open with write access only when mutation is needed
                using var interfacesKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", true);
                if (interfacesKey != null)
                {
                    int interfacesConfigured = 0;
                    int verifiedInterfaces = 0;
                    foreach (var guid in interfacesKey.GetSubKeyNames())
                    {
                        using var ifSubKey = interfacesKey.OpenSubKey(guid, true);
                        if (ifSubKey != null)
                        {
                            var ipObj = ifSubKey.GetValue("IPAddress") ?? ifSubKey.GetValue("DhcpIPAddress");
                            if (ipObj != null)
                            {
                                var prevIfVal = ifSubKey.GetValue(valueName);
                                var ifBkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureRegistryTweak(
                                    regProfile,
                                    $"{entry.DisplayName ?? valueName} ({guid})",
                                    "HKLM",
                                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}",
                                    valueName,
                                    prevIfVal ?? "VALUE DID NOT EXIST",
                                    "DWord",
                                    intValToSet,
                                    "SAFE");
                                if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(ifBkTx, out string ifBkErr))
                                {
                                    result.Status = ResultStatus.Failed;
                                    result.Message = ifBkErr;
                                    return result;
                                }

                                ifSubKey.SetValue(valueName, intValToSet, Microsoft.Win32.RegistryValueKind.DWord);
                                interfacesConfigured++;
                                if (ifSubKey.GetValue(valueName) is int rb && rb == intValToSet)
                                    verifiedInterfaces++;
                            }
                        }
                    }
                    if (interfacesConfigured > 0 && verifiedInterfaces == interfacesConfigured)
                    {
                        result.Status = ResultStatus.Success;
                        result.Message = $"Applied and verified {valueName}={intValToSet} across {verifiedInterfaces} active network interface(s).";
                        return result;
                    }
                    if (interfacesConfigured == 0)
                    {
                        result.Status = ResultStatus.NotApplicable;
                        result.Message = $"No active network adapter interface subkeys found for {valueName}.";
                        return result;
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                result.Status = ResultStatus.Blocked;
                result.Message = $"Registry interface configuration blocked: Administrator privileges required (Access Denied). {ex.Message}";
                return result;
            }
            catch (System.Security.SecurityException ex)
            {
                result.Status = ResultStatus.Blocked;
                result.Message = $"Registry interface configuration blocked by security policy: {ex.Message}";
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ResultStatus.Failed;
                result.Message = $"Failed to configure TCP interface {valueName}: {ex.Message}";
                return result;
            }
        }

        // Capture before state into Universal Backup Manager and verify SHA-256 integrity before mutating
        var before = _registryManager.ReadValue(hive, path, valueName);
        if (before != null && StateNormalizer.IsSatisfied(before.Value?.ToString(), targetValue.ToString()))
        {
            result.Status = ResultStatus.AlreadyOptimized;
            result.Message = $"Registry value '{hive}\\{path}\\{valueName}' is already set to target state.";
            return result;
        }

        var bkTx = BiosOptimizer.Core.Implementations.BackupManager.Instance.CaptureRegistryTweak(
            regProfile,
            entry.DisplayName ?? valueName,
            hive,
            path,
            valueName,
            before?.Value ?? "VALUE DID NOT EXIST",
            targetValueType.ToString(),
            targetValue,
            "SAFE"
        );
        if (!BiosOptimizer.Core.Implementations.BackupManager.EnsureVerifiedTransaction(bkTx, out string bkErr))
        {
            result.Status = ResultStatus.Failed;
            result.Message = bkErr;
            return result;
        }

        // Apply via RegistryManager
        try
        {
            _registryManager.WriteValue(hive, path, valueName, targetValue, targetValueType);
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Status = ResultStatus.Protected;
            result.Message = $"Registry write blocked: Windows access protection policy ({ex.Message})";
            return result;
        }
        catch (System.Security.SecurityException ex)
        {
            result.Status = ResultStatus.Protected;
            result.Message = $"Registry write blocked by Windows security policy ({ex.Message})";
            return result;
        }
        catch (ArgumentException ex)
        {
            result.Status = ResultStatus.Failed;
            result.Message = $"Registry write failed: Invalid value type or conversion ({ex.Message})";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = ResultStatus.Failed;
            result.Message = $"Registry write failed: {ex.Message}";
            return result;
        }

        // Live Readback Verification
        if (valueName.Equals("MouseTrails", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                uint trailsVal = uint.TryParse(targetValue.ToString(), out uint parsedTrails) ? parsedTrails : 0;
                SystemParametersInfo(0x005D /* SPI_SETMOUSETRAILS */, trailsVal, IntPtr.Zero, 0x01 /* SPIF_UPDATEINIFILE */ | 0x02 /* SPIF_SENDCHANGE */);
            }
            catch { }
        }

        var verification = _registryManager.ReadValue(hive, path, valueName);
        if (verification == null)
        {
            result.Status = ResultStatus.Failed;
            result.Message = $"Verification failed: Registry value '{hive}\\{path}\\{valueName}' was not found after write.";
            return result;
        }

        bool isMatch = StateNormalizer.IsSatisfied(verification.Value?.ToString(), targetValue.ToString());

        if (!isMatch)
        {
            result.Status = ResultStatus.Failed;
            result.Message = $"Verification mismatch: Target was '{targetValue}', but readback was '{verification.Value}'.";
            return result;
        }

        result.Status = ResultStatus.Success;
        result.Message = $"Registry verified successfully ({valueName} = {targetValue}).";
        return result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    public string GetCurrentValueDisplay(OptimizationEntry entry)
    {
        var parts = entry.Target.Split('\\', 2);
        if (parts.Length < 2) return "Unknown";
        var hive = parts[0];
        var subParts = parts[1].Split('\\');
        if (subParts.Length < 2) return "Unknown";
        var valueName = subParts.Last();
        var path = string.Join("\\", subParts.Take(subParts.Length - 1));

        if (path.Equals(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", StringComparison.OrdinalIgnoreCase) &&
            (valueName.Equals("TcpAckFrequency", StringComparison.OrdinalIgnoreCase) || valueName.Equals("TCPNoDelay", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var interfacesKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", false);
                if (interfacesKey != null)
                {
                    foreach (var guid in interfacesKey.GetSubKeyNames())
                    {
                        using var ifSubKey = interfacesKey.OpenSubKey(guid, false);
                        if (ifSubKey != null)
                        {
                            var ipObj = ifSubKey.GetValue("IPAddress") ?? ifSubKey.GetValue("DhcpIPAddress");
                            if (ipObj != null)
                            {
                                var v = ifSubKey.GetValue(valueName);
                                if (v != null) return v.ToString() ?? "Not Set";
                            }
                        }
                    }
                }
            }
            catch { }
        }

        var existing = _registryManager.ReadValue(hive, path, valueName);
        return existing?.Value?.ToString() ?? "Not Set";
    }

    private static readonly HashSet<string> KnownStringRegistryValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "MenuShowDelay", "AutoEndTasks", "HungAppTimeout", "WaitToKillAppTimeout", "WaitToKillServiceTimeout",
        "LowLevelHooksTimeout", "DragFullWindows", "FontSmoothing", "MinAnimate",
        "MouseSensitivity", "MouseSpeed", "MouseThreshold1", "MouseThreshold2", "MouseTrails", "DoubleClickSpeed",
        "KeyboardDelay", "KeyboardSpeed", "Beep", "ExtendedSounds", "SnapToDefaultButton"
    };

    private static RegistryValueType ResolveStringTypedRegistryKind(
        string? explicitTypeHint,
        string valueName,
        RegistryValueType? existingType,
        string raw,
        out object coercedValue)
    {
        if (!string.IsNullOrWhiteSpace(explicitTypeHint))
        {
            string hint = explicitTypeHint.Trim().ToUpperInvariant();
            if (hint.Contains("DWORD") && int.TryParse(raw, out int dw))
            {
                coercedValue = dw;
                return RegistryValueType.DWord;
            }
            if (hint.Contains("QWORD") && long.TryParse(raw, out long qw))
            {
                coercedValue = qw;
                return RegistryValueType.QWord;
            }
            if (hint.Contains("BINARY") && TryParseHexBytes(raw, out byte[] bytes))
            {
                coercedValue = bytes;
                return RegistryValueType.Binary;
            }
            if (hint.Contains("SZ"))
            {
                coercedValue = raw;
                return RegistryValueType.String;
            }
        }

        if (existingType == RegistryValueType.Binary || valueName.Equals("UserPreferencesMask", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseHexBytes(raw, out byte[] bin))
            {
                coercedValue = bin;
                return RegistryValueType.Binary;
            }
        }

        if (KnownStringRegistryValues.Contains(valueName) || existingType == RegistryValueType.String)
        {
            coercedValue = raw;
            return RegistryValueType.String;
        }

        if (existingType == RegistryValueType.DWord && int.TryParse(raw, out int existingDw))
        {
            coercedValue = existingDw;
            return RegistryValueType.DWord;
        }

        if (existingType == RegistryValueType.QWord && long.TryParse(raw, out long existingQw))
        {
            coercedValue = existingQw;
            return RegistryValueType.QWord;
        }

        coercedValue = raw;
        return RegistryValueType.String;
    }

    private static bool TryParseHexBytes(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        string cleaned = hex.Replace(" ", "").Replace("-", "").Trim();
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0) return false;
        try
        {
            bytes = Convert.FromHexString(cleaned);
            return true;
        }
        catch
        {
            return false;
        }
    }
}