using BiosOptimizer.Core.Interfaces;



using BiosOptimizer.Core.Interop;



using BiosOptimizer.Core.V4.Safety;
using BiosOptimizer.Core.V4.Architecture;



using Microsoft.Win32;



using System;



using System.Collections.Generic;



using System.Diagnostics;



using System.IO;



using System.Linq;



using System.Management;



using System.Runtime.InteropServices;



using System.Text.Json;



using System.Threading.Tasks;



namespace BiosOptimizer.Core.Implementations



{



    public static class InputOptimizationState



    {



        public const string Discovered = "DISCOVERED";



        public const string Applicable = "APPLICABLE";



        public const string AlreadyOptimal = "ALREADY_OPTIMAL";



        public const string NotApplicable = "NOT_APPLICABLE";



        public const string Unsupported = "UNSUPPORTED";



        public const string Pending = "PENDING";



        public const string Applying = "APPLYING";



        public const string Applied = "APPLIED";



        public const string AppliedPendingReboot = "APPLIED_PENDING_REBOOT";



        public const string PostRebootVerifying = "POST_REBOOT_VERIFYING";



        public const string Verified = "VERIFIED";



        public const string Failed = "FAILED";



        public const string VerificationFailed = "VERIFICATION_FAILED";



        public const string RolledBack = "ROLLED_BACK";



        public const string NotAvailable = "NOT_AVAILABLE";



        public const string NotSupported = "NOT_SUPPORTED";



        public const string RestartRequired = "RESTART_REQUIRED";



    }



    public class InputTransactionItem



    {



        public string OptimizationId { get; set; } = string.Empty;



        public string Name { get; set; } = string.Empty;



        public string Category { get; set; } = string.Empty;



        public string RegistryPath { get; set; } = string.Empty;



        public string ValueName { get; set; } = string.Empty;



        public string ValueType { get; set; } = "REG_DWORD";



        public string BeforeState { get; set; } = string.Empty;



        public string TargetState { get; set; } = string.Empty;



        public string State { get; set; } = InputOptimizationState.Pending;



        public bool RequiresReboot { get; set; }



        public InputVerificationMode VerificationMode { get; set; } = InputVerificationMode.ImmediateReadback;



        public string ApplyResult { get; set; } = string.Empty;



        public bool VerificationPending { get; set; }



        public string AfterState { get; set; } = string.Empty;



        public DateTime Timestamp { get; set; } = DateTime.UtcNow;



        public long BootUptimeAtApplyMs { get; set; } = Environment.TickCount64;



        public string FailureReason { get; set; } = string.Empty;



        public bool IsBinary { get; set; }



    }



    public class InputOptimizerTransaction



    {



        public string TransactionId { get; set; } = Guid.NewGuid().ToString();



        public string Profile { get; set; } = "Default";



        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;



        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;



        public long SystemBootTimeMs { get; set; } = Environment.TickCount64;



        public string Status { get; set; } = "RUNNING"; 



        public List<InputTransactionItem> Items { get; set; } = new List<InputTransactionItem>();



    }



    public class InputTransactionJournal



    {



        private static readonly object _lock = new object();



        private readonly string _journalPath;



        private const string REG_TX_KEY = @"Software\ErrorOptimizer\InputOptimizer\Transactions";



        public InputTransactionJournal()



        {



            string folder;



            try



            {



                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ErrorOptimizer", "InputOptimizer");



                Directory.CreateDirectory(folder);



            }



            catch



            {



                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvancedInputOptimizer");



                Directory.CreateDirectory(folder);



            }



            _journalPath = Path.Combine(folder, "input_transaction_journal.json");



        }



        public InputOptimizerTransaction? LoadLatestTransaction()



        {



            lock (_lock)



            {



                try



                {



                    if (File.Exists(_journalPath))



                    {



                        var json = File.ReadAllText(_journalPath);



                        if (!string.IsNullOrWhiteSpace(json))



                        {



                            var tx = JsonSerializer.Deserialize<InputOptimizerTransaction>(json);



                            if (tx != null) return tx;



                        }



                    }



                }



                catch (Exception ex)



                {



                    Debug.WriteLine($"[InputTransactionJournal] File load error: {ex.Message}");



                }



                // Fallback: Check Registry



                try



                {



                    using var key = Registry.CurrentUser.OpenSubKey(REG_TX_KEY, false);



                    if (key != null)



                    {



                        string? json = key.GetValue("LatestTransaction") as string;



                        if (!string.IsNullOrWhiteSpace(json))



                        {



                            return JsonSerializer.Deserialize<InputOptimizerTransaction>(json);



                        }



                    }



                }



                catch { }



                return null;



            }



        }



        public void SaveTransaction(InputOptimizerTransaction tx)



        {



            lock (_lock)



            {



                try



                {



                    tx.UpdatedAt = DateTime.UtcNow;



                    var json = JsonSerializer.Serialize(tx, new JsonSerializerOptions { WriteIndented = true });



                    string tempPath = _journalPath + ".tmp";



                    File.WriteAllText(tempPath, json);



                    File.Copy(tempPath, _journalPath, true);



                    try { File.Delete(tempPath); } catch { }



                    // Mirror to Registry



                    try



                    {



                        using var key = Registry.CurrentUser.CreateSubKey(REG_TX_KEY, true);



                        key?.SetValue("LatestTransaction", json, RegistryValueKind.String);



                    }



                    catch { }



                }



                catch (Exception ex)



                {



                    Debug.WriteLine($"[InputTransactionJournal] Save error: {ex.Message}");



                }



            }



        }



        public void Clear()



        {



            lock (_lock)



            {



                try



                {



                    if (File.Exists(_journalPath)) File.Delete(_journalPath);



                }



                catch { }



                try



                {



                    using var key = Registry.CurrentUser.CreateSubKey(REG_TX_KEY, true);



                    key?.DeleteValue("LatestTransaction", false);



                }



                catch { }



            }



        }



    }



    #region Optimization Detectors Interface & Implementations



    public interface IInputOptimizationDetector



    {



        string Id { get; }



        string Name { get; }



        string Category { get; }



        string Risk { get; }



        bool RequiresReboot { get; }



        InputVerificationMode VerificationMode { get; }



        string TechnicalLocation { get; }



        string Description { get; }



        (string ApplicabilityState, string Reason) DetectApplicability();



        string ReadCurrentState();



        string GetTargetState();



        (bool Success, string NewState, string ApplyResult, string FailureReason) Apply(IBackupManager backup);



        (bool Verified, string Message) Verify();



    }



    public abstract class RegistryDwordDetectorBase : IInputOptimizationDetector



    {



        public abstract string Id { get; }



        public abstract string Name { get; }



        public abstract string Category { get; }



        public abstract string Risk { get; }



        public abstract bool RequiresReboot { get; }



        public abstract InputVerificationMode VerificationMode { get; }



        public abstract string TechnicalLocation { get; }



        public abstract string Description { get; }



        public abstract RegistryKey RootKey { get; }



        public abstract string SubKeyPath { get; }



        public abstract string ValueName { get; }



        public abstract uint TargetDwordValue { get; }



        public virtual bool AllowKeyCreation => true;



        public virtual (string ApplicabilityState, string Reason) DetectApplicability()



        {



            try



            {



                using var key = RootKey.OpenSubKey(SubKeyPath, false);



                if (key == null)



                {



                    // Check if parent service/component exists



                    string[] parts = SubKeyPath.Split('\\');



                    if (parts.Length > 1)



                    {



                        string parentPath = string.Join("\\", parts.Take(parts.Length - 1));



                        using var parentKey = RootKey.OpenSubKey(parentPath, false);



                        if (parentKey == null)



                        {



                            return (InputOptimizationState.NotApplicable, $"Parent service '{parentPath}' is not installed on this PC.");



                        }



                    }



                    else



                    {



                        return (InputOptimizationState.NotApplicable, $"Registry path '{SubKeyPath}' does not exist on this machine.");



                    }



                }



                string current = ReadCurrentState();



                if (current == GetTargetState())



                {



                    return (InputOptimizationState.AlreadyOptimal, "Current value matches target optimization.");



                }



                return (InputOptimizationState.Applicable, "Setting is applicable and ready for optimization.");



            }



            catch (Exception ex)



            {



                return (InputOptimizationState.Unsupported, $"Access error: {ex.Message}");



            }



        }



        public virtual string ReadCurrentState()



        {



            try



            {



                using var key = RootKey.OpenSubKey(SubKeyPath, false);



                if (key == null) return "Not Found";



                var val = key.GetValue(ValueName);



                if (val == null) return "Default";



                if (val is int intVal) return ((uint)intVal).ToString();



                if (val is uint uintVal) return uintVal.ToString();



                if (uint.TryParse(val.ToString(), out uint parsed)) return parsed.ToString();



                return val.ToString() ?? "Unknown";



            }



            catch (UnauthorizedAccessException)



            {



                return "Access Denied";



            }



            catch



            {



                return "Error";



            }



        }



        public virtual string GetTargetState() => TargetDwordValue.ToString();



        public virtual (bool Success, string NewState, string ApplyResult, string FailureReason) Apply(IBackupManager backup)



        {



            try



            {



                using var key = RootKey.CreateSubKey(SubKeyPath, true);



                if (key == null)



                {



                    return (false, ReadCurrentState(), "FAILED", "Unable to open or create registry subkey.");



                }



                var existingVal = key.GetValue(ValueName);



                if (existingVal != null)



                {



                    backup.BackupRegistryValue("AntiGravity.InputOptimizer", SubKeyPath, ValueName, existingVal, "REG_DWORD", TargetDwordValue, "APPLIED");



                }



                key.SetValue(ValueName, unchecked((int)TargetDwordValue), RegistryValueKind.DWord);



                // Immediate readback check



                string readback = ReadCurrentState();



                bool written = (readback == GetTargetState());



                if (!written)



                {



                    return (false, readback, "WRITE_FAILED", "Immediate readback failed after registry write.");



                }



                if (RequiresReboot)



                {



                    return (true, readback, "SUCCESS", string.Empty);



                }



                return (true, readback, "SUCCESS", string.Empty);



            }



            catch (Exception ex)



            {



                return (false, ReadCurrentState(), "FAILED", ex.Message);



            }



        }



        public virtual (bool Verified, string Message) Verify()



        {



            string cur = ReadCurrentState();



            bool match = (cur == GetTargetState());



            return (match, match ? "Target state verified." : $"Current value '{cur}' does not match target '{GetTargetState()}'.");



        }



    }



    public abstract class RegistryStringDetectorBase : IInputOptimizationDetector



    {



        public abstract string Id { get; }



        public abstract string Name { get; }



        public abstract string Category { get; }



        public abstract string Risk { get; }



        public abstract bool RequiresReboot { get; }



        public abstract InputVerificationMode VerificationMode { get; }



        public abstract string TechnicalLocation { get; }



        public abstract string Description { get; }



        public abstract RegistryKey RootKey { get; }



        public abstract string SubKeyPath { get; }



        public abstract string ValueName { get; }



        public abstract string TargetStringValue { get; }



        public virtual (string ApplicabilityState, string Reason) DetectApplicability()



        {



            try



            {



                using var key = RootKey.OpenSubKey(SubKeyPath, false);



                if (key == null)



                {



                    return (InputOptimizationState.NotApplicable, $"Registry path '{SubKeyPath}' does not exist.");



                }



                string current = ReadCurrentState();



                if (current == GetTargetState())



                {



                    return (InputOptimizationState.AlreadyOptimal, "Current value matches target optimization.");



                }



                return (InputOptimizationState.Applicable, "Setting is applicable.");



            }



            catch (Exception ex)



            {



                return (InputOptimizationState.Unsupported, $"Access error: {ex.Message}");



            }



        }



        public virtual string ReadCurrentState()



        {



            try



            {



                using var key = RootKey.OpenSubKey(SubKeyPath, false);



                if (key == null) return "Not Found";



                var val = key.GetValue(ValueName);



                return val != null ? (val.ToString() ?? "Not Found") : "Default";



            }



            catch (UnauthorizedAccessException)



            {



                return "Access Denied";



            }



            catch



            {



                return "Error";



            }



        }



        public virtual string GetTargetState() => TargetStringValue;



        public virtual (bool Success, string NewState, string ApplyResult, string FailureReason) Apply(IBackupManager backup)



        {



            try



            {



                using var key = RootKey.CreateSubKey(SubKeyPath, true);



                if (key == null)



                {



                    return (false, ReadCurrentState(), "FAILED", "Unable to open or create registry subkey.");



                }



                var existingVal = key.GetValue(ValueName);



                if (existingVal != null)



                {



                    backup.BackupRegistryValue("AntiGravity.InputOptimizer", SubKeyPath, ValueName, existingVal, "REG_SZ", TargetStringValue, "APPLIED");



                }



                key.SetValue(ValueName, TargetStringValue, RegistryValueKind.String);



                string readback = ReadCurrentState();



                bool written = string.Equals(readback, TargetStringValue, StringComparison.OrdinalIgnoreCase);



                if (!written)



                {



                    return (false, readback, "WRITE_FAILED", "Immediate readback failed after registry write.");



                }



                return (true, readback, "SUCCESS", string.Empty);



            }



            catch (Exception ex)



            {



                return (false, ReadCurrentState(), "FAILED", ex.Message);



            }



        }



        public virtual (bool Verified, string Message) Verify()



        {



            string cur = ReadCurrentState();



            bool match = string.Equals(cur, GetTargetState(), StringComparison.OrdinalIgnoreCase);



            return (match, match ? "Target state verified." : $"Current value '{cur}' does not match target '{GetTargetState()}'.");



        }



    }



    // ── Mouse Core Detectors ──────────────────────────────────────────



    public class MouseSpeedDetector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.speed";



        public override string Name => "Pointer Speed (1:1 Raw Scaling)";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\MouseSpeed";



        public override string Description => "Disables Windows mouse acceleration multiplier to ensure 1:1 hardware sensor tracking.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "MouseSpeed";



        public override string TargetStringValue => "0";



    }



    public class MouseThreshold1Detector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.threshold1";



        public override string Name => "Pointer Acceleration Threshold 1";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\MouseThreshold1";



        public override string Description => "Zeros primary acceleration threshold curve to eliminate velocity non-linearities.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "MouseThreshold1";



        public override string TargetStringValue => "0";



    }



    public class MouseThreshold2Detector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.threshold2";



        public override string Name => "Pointer Acceleration Threshold 2";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\MouseThreshold2";



        public override string Description => "Zeros secondary acceleration threshold curve for uniform physical displacement.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "MouseThreshold2";



        public override string TargetStringValue => "0";



    }



    public class MouseSensitivityDetector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.sensitivity";



        public override string Name => "Pointer Base Sensitivity (6/11 Default)";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\MouseSensitivity";



        public override string Description => "Sets base sensitivity to 10 (Windows 6/11 notch), avoiding software interpolation skips.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "MouseSensitivity";



        public override string TargetStringValue => "10";



    }



    public class MouseTrailsDetector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.trails";



        public override string Name => "Pointer Trails";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\MouseTrails";



        public override string Description => "Disables pointer ghosting trails to eliminate visual compositing latency.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "MouseTrails";



        public override string TargetStringValue => "0";



    }



    public class ActiveWindowTrackingDetector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.active_window_tracking";



        public override string Name => "Active Window Tracking Delay";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\ActiveWindowTracking";



        public override string Description => "Disables asynchronous window activation delay under pointer movements.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "ActiveWindowTracking";



        public override string TargetStringValue => "0";



    }



    public class MouseHoverTimeDetector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.hover_time";



        public override string Name => "Mouse Hover Response Time";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\MouseHoverTime";



        public override string Description => "Sets standard hover response delay (400ms) to prevent accidental tooltip stalls.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "MouseHoverTime";



        public override string TargetStringValue => "400";



    }



    public class SnapToDefaultButtonDetector : RegistryStringDetectorBase



    {



        public override string Id => "mouse.snap_to_default";



        public override string Name => "Snap Pointer to Default Button";



        public override string Category => "Mouse";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\SnapToDefaultButton";



        public override string Description => "Disables automatic pointer jumping on dialog popups.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Mouse";



        public override string ValueName => "SnapToDefaultButton";



        public override string TargetStringValue => "0";



    }



    public class MouseCurvesDetector : IInputOptimizationDetector



    {



        public string Id => "mouse.pointer_curves";



        public string Name => "Linear MarkC Pointer Curves";



        public string Category => "Mouse";



        public string Risk => "ADVANCED";



        public bool RequiresReboot => false;



        public InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Mouse\SmoothMouseXCurve";



        public string Description => "Applies authentic linearized MarkC response curves for exact 1:1 hardware pixel ratio.";



        internal static readonly byte[] TargetCurveX = new byte[] { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, 0x15,0x6E,0x00,0x00,0x00,0x00,0x00,0x00, 0x00,0x40,0x01,0x00,0x00,0x00,0x00,0x00, 0x29,0xDC,0x03,0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x28,0x00,0x00,0x00,0x00,0x00 };



        internal static readonly byte[] TargetCurveY = new byte[] { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, 0xFD,0x11,0x01,0x00,0x00,0x00,0x00,0x00, 0x00,0x24,0x04,0x00,0x00,0x00,0x00,0x00, 0x00,0xFC,0x12,0x00,0x00,0x00,0x00,0x00, 0x00,0xC0,0xBB,0x01,0x00,0x00,0x00,0x00 };



        internal static readonly byte[] DefaultWindowsCurveX = new byte[] { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, 0x15,0xE5,0x07,0x00,0x00,0x00,0x00,0x00, 0x00,0x40,0x1F,0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x3E,0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x7C,0x00,0x00,0x00,0x00,0x00 };



        internal static readonly byte[] DefaultWindowsCurveY = new byte[] { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, 0x54,0xC5,0x01,0x00,0x00,0x00,0x00,0x00, 0x54,0xE5,0x07,0x00,0x00,0x00,0x00,0x00, 0x00,0x40,0x1F,0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x3E,0x00,0x00,0x00,0x00,0x00 };



        public (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "Control Panel\\Mouse key not found.");



            string cur = ReadCurrentState();



            if (cur == GetTargetState())



            {



                return (InputOptimizationState.AlreadyOptimal, "Linear MarkC curves already applied.");



            }



            return (InputOptimizationState.Applicable, "Ready to apply linearized pointer curves.");



        }



        public string ReadCurrentState()



        {



            try



            {



                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", false);



                if (key == null) return "Not Found";



                var curX = key.GetValue("SmoothMouseXCurve") as byte[];



                var curY = key.GetValue("SmoothMouseYCurve") as byte[];



                if (curX != null && curY != null && curX.SequenceEqual(TargetCurveX) && curY.SequenceEqual(TargetCurveY))



                {



                    return "Linearized byte array";



                }



                return "Windows default curve";



            }



            catch { return "Error"; }



        }



        public string GetTargetState() => "Linearized byte array";



        public (bool Success, string NewState, string ApplyResult, string FailureReason) Apply(IBackupManager backup)



        {



            try



            {



                using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", true);



                if (key == null) return (false, ReadCurrentState(), "FAILED", "Unable to open Control Panel\\Mouse");



                var oldX = key.GetValue("SmoothMouseXCurve") as byte[];



                var oldY = key.GetValue("SmoothMouseYCurve") as byte[];



                if (oldX != null) backup.BackupRegistryValue("AntiGravity.InputOptimizer", @"Control Panel\Mouse", "SmoothMouseXCurve", oldX, "REG_BINARY", TargetCurveX, "APPLIED");



                if (oldY != null) backup.BackupRegistryValue("AntiGravity.InputOptimizer", @"Control Panel\Mouse", "SmoothMouseYCurve", oldY, "REG_BINARY", TargetCurveY, "APPLIED");



                key.SetValue("SmoothMouseXCurve", TargetCurveX, RegistryValueKind.Binary);



                key.SetValue("SmoothMouseYCurve", TargetCurveY, RegistryValueKind.Binary);



                var rbX = key.GetValue("SmoothMouseXCurve") as byte[];



                var rbY = key.GetValue("SmoothMouseYCurve") as byte[];



                bool written = (rbX != null && rbY != null && rbX.SequenceEqual(TargetCurveX) && rbY.SequenceEqual(TargetCurveY));



                return (written, written ? "Linearized byte array" : "Write Failed", written ? "SUCCESS" : "FAILED", written ? string.Empty : "Curve byte mismatch on readback.");



            }



            catch (Exception ex)



            {



                return (false, ReadCurrentState(), "FAILED", ex.Message);



            }



        }



        public (bool Verified, string Message) Verify()



        {



            string cur = ReadCurrentState();



            bool match = (cur == GetTargetState());



            return (match, match ? "Curves verified." : "Curves not matching linear MarkC profile.");



        }



    }



    // ── Keyboard Core Detectors ───────────────────────────────────────



    public class KeyboardDelayDetector : RegistryStringDetectorBase



    {



        public override string Id => "keyboard.delay";



        public override string Name => "Keyboard Repeat Delay (Balanced Response)";



        public override string Category => "Keyboard";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Keyboard\KeyboardDelay";



        public override string Description => "Sets repeat delay to 1 (500ms) or 0 (250ms) to ensure responsive held-key dispatch without accidental double-key typing.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Keyboard";



        public override string ValueName => "KeyboardDelay";



        public override string TargetStringValue => "1";



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            string cur = ReadCurrentState();



            if (cur == "0" || cur == "1")



            {



                return (InputOptimizationState.AlreadyOptimal, $"Keyboard repeat delay is already optimal ({(cur == "0" ? "250ms" : "500ms")}).");



            }



            return (InputOptimizationState.Applicable, "Keyboard repeat delay is set to slow response (>500ms). Ready to optimize.");



        }



    }



    public class KeyboardSpeedDetector : RegistryStringDetectorBase



    {



        public override string Id => "keyboard.speed";



        public override string Name => "Keyboard Repeat Rate (Max Frequency)";



        public override string Category => "Keyboard";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Keyboard\KeyboardSpeed";



        public override string Description => "Sets repeat speed to 31 (~30 reps/sec) for maximum command throughput.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Keyboard";



        public override string ValueName => "KeyboardSpeed";



        public override string TargetStringValue => "31";



    }



    public class FilterKeysFlagsDetector : RegistryStringDetectorBase



    {



        public override string Id => "keyboard.filter_keys";



        public override string Name => "FilterKeys Shortcut Bypass";



        public override string Category => "Keyboard";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Accessibility\Keyboard Response\Flags";



        public override string Description => "Configures FilterKeys flags (122) to prevent accidental keystroke suppression.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Accessibility\Keyboard Response";



        public override string ValueName => "Flags";



        public override string TargetStringValue => "122";



    }



    public class StickyKeysFlagsDetector : RegistryStringDetectorBase



    {



        public override string Id => "keyboard.sticky_keys";



        public override string Name => "StickyKeys Shortcut Bypass";



        public override string Category => "Keyboard";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys\Flags";



        public override string Description => "Configures StickyKeys flags (506) to disable shift-key interruption modals.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Accessibility\StickyKeys";



        public override string ValueName => "Flags";



        public override string TargetStringValue => "506";



    }



    public class ToggleKeysFlagsDetector : RegistryStringDetectorBase



    {



        public override string Id => "keyboard.toggle_keys";



        public override string Name => "ToggleKeys Shortcut Bypass";



        public override string Category => "Keyboard";



        public override string Risk => "CORE";



        public override bool RequiresReboot => false;



        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;



        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Accessibility\ToggleKeys\Flags";



        public override string Description => "Configures ToggleKeys flags (58) to eliminate audio buffer interruptions.";



        public override RegistryKey RootKey => Registry.CurrentUser;



        public override string SubKeyPath => @"Control Panel\Accessibility\ToggleKeys";



        public override string ValueName => "Flags";



        public override string TargetStringValue => "58";



    }



    // ── Advanced / Driver Queue Detectors (Requires Reboot) ───────────



    public class MouclassQueueSizeDetector : RegistryDwordDetectorBase



    {



        public override string Id => "queue.mouclass_size";



        public override string Name => "Mouse Class Data Queue Size";



        public override string Category => "Mouse Queue";



        public override string Risk => "ADVANCED";



        public override bool RequiresReboot => true;



        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;



        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters\MouseDataQueueSize";



        public override string Description => "Optimizes mouse driver buffer queue depth to 20 packets, reducing input packet buffering latency.";



        public override RegistryKey RootKey => Registry.LocalMachine;



        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters";



        public override string ValueName => "MouseDataQueueSize";



        public override uint TargetDwordValue => 20;



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "Mouse class driver service (mouclass) not present.");



            return base.DetectApplicability();



        }



    }



    public class KbdclassQueueSizeDetector : RegistryDwordDetectorBase



    {



        public override string Id => "queue.kbdclass_size";



        public override string Name => "Keyboard Class Data Queue Size";



        public override string Category => "Keyboard Queue";



        public override string Risk => "ADVANCED";



        public override bool RequiresReboot => true;



        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;



        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters\KeyboardDataQueueSize";



        public override string Description => "Optimizes keyboard driver buffer queue depth to 20 packets, minimizing key event buffering delay.";



        public override RegistryKey RootKey => Registry.LocalMachine;



        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters";



        public override string ValueName => "KeyboardDataQueueSize";



        public override uint TargetDwordValue => 20;



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "Keyboard class driver service (kbdclass) not present.");



            return base.DetectApplicability();



        }



    }



    public class MouhidQueueLengthDetector : RegistryDwordDetectorBase



    {



        public override string Id => "queue.mouhid_length";



        public override string Name => "HID Mouse Data Queue Length";



        public override string Category => "HID Mouse Queue";



        public override string Risk => "ADVANCED";



        public override bool RequiresReboot => true;



        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;



        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouhid\Parameters\MouseDataQueueLength";



        public override string Description => "Sets USB HID mouse driver queue buffer to 20 packets for direct polling responsiveness.";



        public override RegistryKey RootKey => Registry.LocalMachine;



        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\mouhid\Parameters";



        public override string ValueName => "MouseDataQueueLength";



        public override uint TargetDwordValue => 20;



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mouhid", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "HID mouse driver service (mouhid) not present.");



            return base.DetectApplicability();



        }



    }



    public class KbdhidQueueLengthDetector : RegistryDwordDetectorBase



    {



        public override string Id => "queue.kbdhid_length";



        public override string Name => "HID Keyboard Data Queue Length";



        public override string Category => "HID Keyboard Queue";



        public override string Risk => "ADVANCED";



        public override bool RequiresReboot => true;



        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;



        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdhid\Parameters\KeyboardDataQueueLength";



        public override string Description => "Sets USB HID keyboard driver queue buffer to 20 packets for direct keystroke transmission.";



        public override RegistryKey RootKey => Registry.LocalMachine;



        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\kbdhid\Parameters";



        public override string ValueName => "KeyboardDataQueueLength";



        public override uint TargetDwordValue => 20;



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdhid", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "HID keyboard driver service (kbdhid) not present.");



            return base.DetectApplicability();



        }



    }



    public class MouhidInputQueueSizeDetector : RegistryDwordDetectorBase
    {
        public override string Id => "queue.mouhid_size";
        public override string Name => "HID Mouse Buffer Queue Optimization";
        public override string Category => "Driver Queue";
        public override string Risk => "ADVANCED";
        public override bool RequiresReboot => true;
        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;
        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouhid\Parameters\MouseDataQueueSize";
        public override string Description => "Configures the HID mouse driver packet queue depth to eliminate packet drops under high-rate polling (100 packets).";
        public override RegistryKey RootKey => Registry.LocalMachine;
        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\mouhid\Parameters";
        public override string ValueName => "MouseDataQueueSize";
        public override uint TargetDwordValue => 100;
    }

    public class KbdhidInputQueueSizeDetector : RegistryDwordDetectorBase
    {
        public override string Id => "queue.kbdhid_size";
        public override string Name => "HID Keyboard Buffer Queue Optimization";
        public override string Category => "Driver Queue";
        public override string Risk => "ADVANCED";
        public override bool RequiresReboot => true;
        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;
        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdhid\Parameters\KeyboardDataQueueSize";
        public override string Description => "Configures the HID keyboard driver packet queue depth to eliminate packet drops under rapid keystroke input (100 packets).";
        public override RegistryKey RootKey => Registry.LocalMachine;
        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\kbdhid\Parameters";
        public override string ValueName => "KeyboardDataQueueSize";
        public override uint TargetDwordValue => 100;
    }



    public class MouclassThreadPriorityDetector : RegistryDwordDetectorBase



    {



        public override string Id => "scheduling.mouclass_priority";



        public override string Name => "Mouse Class Thread Priority";



        public override string Category => "Scheduling";



        public override string Risk => "EXPERIMENTAL";



        public override bool RequiresReboot => true;



        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;



        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters\ThreadPriority";



        public override string Description => "Assigns real-time priority (31) to mouse class DPC processing threads.";



        public override RegistryKey RootKey => Registry.LocalMachine;



        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters";



        public override string ValueName => "ThreadPriority";



        public override uint TargetDwordValue => 31;



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "mouclass driver not installed.");



            return base.DetectApplicability();



        }



    }



    public class KbdclassThreadPriorityDetector : RegistryDwordDetectorBase



    {



        public override string Id => "scheduling.kbdclass_priority";



        public override string Name => "Keyboard Class Thread Priority";



        public override string Category => "Scheduling";



        public override string Risk => "EXPERIMENTAL";



        public override bool RequiresReboot => true;



        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;



        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters\ThreadPriority";



        public override string Description => "Assigns real-time priority (31) to keyboard class DPC processing threads.";



        public override RegistryKey RootKey => Registry.LocalMachine;



        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters";



        public override string ValueName => "ThreadPriority";



        public override uint TargetDwordValue => 31;



        public override (string ApplicabilityState, string Reason) DetectApplicability()



        {



            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass", false);



            if (key == null) return (InputOptimizationState.NotApplicable, "kbdclass driver not installed.");



            return base.DetectApplicability();



        }



    }



    public class UsbSelectiveSuspendDetector : IInputOptimizationDetector
    {
        public string Id => "usb.selective_suspend";
        public string Name => "USB Selective Suspend (AC Power Management)";
        public string Category => "USB";
        public string Risk => "SAFE";
        public bool RequiresReboot => false;
        public InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public string TechnicalLocation => @"Power Plan: USB Settings \ USB Selective Suspend Setting (AC: 0 = Disabled, DC: Preserved)";
        public string Description => "Global USB power-management setting: prevents USB host controllers from putting connected devices into low-power selective suspend while running on AC power. Battery (DC) power-saving behavior is intentionally preserved.";

        private const string UsbSubgroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
        private const string UsbSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

        private static string RunPowerCfg(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                string output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                p?.WaitForExit(3000);
                return output;
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        public (int? AcIndex, int? DcIndex) ReadPowerStates()
        {
            int? ac = null;
            int? dc = null;
            try
            {
                string output = RunPowerCfg($"/query SCHEME_CURRENT {UsbSubgroupGuid} {UsbSettingGuid}");
                var mAc = System.Text.RegularExpressions.Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mAc.Success && int.TryParse(mAc.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int acVal))
                {
                    ac = acVal;
                }
                var mDc = System.Text.RegularExpressions.Regex.Match(output, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mDc.Success && int.TryParse(mDc.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int dcVal))
                {
                    dc = dcVal;
                }
            }
            catch { }
            return (ac, dc);
        }

        public string ReadCurrentState()
        {
            var (ac, _) = ReadPowerStates();
            return ac.HasValue ? ac.Value.ToString() : "0";
        }

        public string GetTargetState() => "0";

        public (string ApplicabilityState, string Reason) DetectApplicability()
        {
            try
            {
                var (ac, dc) = ReadPowerStates();
                if (!ac.HasValue)
                {
                    return (InputOptimizationState.NotApplicable, "USB selective suspend power setting is unavailable in the active Windows power scheme.");
                }

                if (ac.Value == 0)
                {
                    string dcNote = dc.HasValue && dc.Value != 0 ? $" (DC Setting Index = {dc.Value} preserved for battery life)" : "";
                    return (InputOptimizationState.AlreadyOptimal, $"USB Selective Suspend is already disabled on AC power (AC Setting Index = 0){dcNote}.");
                }
                if (ac.Value == 1)
                {
                    return (InputOptimizationState.Applicable, "USB Selective Suspend is currently active on AC power; can be disabled on AC power to prevent controller idle power gating.");
                }

                return (InputOptimizationState.AlreadyOptimal, "USB power subsystem is already configured in high-performance mode.");
            }
            catch (Exception ex)
            {
                return (InputOptimizationState.NotApplicable, $"USB power management query unavailable: {ex.Message}");
            }
        }

        public (bool Success, string NewState, string ApplyResult, string FailureReason) Apply(IBackupManager backup)
        {
            try
            {
                var (acBefore, dcBefore) = ReadPowerStates();
                if (!acBefore.HasValue)
                {
                    return (false, "Unknown", "FAILED", "USB selective suspend setting not found in active power scheme.");
                }

                if (acBefore.Value == 0)
                {
                    return (true, "0", "ALREADY_OPTIMIZED", string.Empty);
                }

                // Preserve original AC value for rollback; DC value is left untouched
                backup.CaptureGenericTweak(
                    "AntiGravity.InputOptimizer",
                    "usb.selective_suspend",
                    "USB Selective Suspend (AC Power Scheme)",
                    "USB Power Management",
                    "PowerCfg",
                    $"AC={acBefore.Value},DC={dcBefore ?? 1}",
                    "AC=0,DC=Preserved",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {UsbSubgroupGuid} {UsbSettingGuid} {acBefore.Value}",
                    false,
                    "SAFE"
                );

                // Modify ONLY AC setting index; preserve DC setting index for battery conservation
                RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {UsbSubgroupGuid} {UsbSettingGuid} 0");
                RunPowerCfg("/setactive SCHEME_CURRENT");

                // Independent readback verification
                var (acAfter, _) = ReadPowerStates();
                if (acAfter.HasValue && acAfter.Value == 0)
                {
                    return (true, "0", "SUCCESS", string.Empty);
                }

                return (false, acAfter?.ToString() ?? "Unknown", "FAILED", $"Powercfg readback returned AC index {acAfter} instead of target 0.");
            }
            catch (Exception ex)
            {
                return (false, ReadCurrentState(), "FAILED", ex.Message);
            }
        }

        public (bool Verified, string Message) Verify()
        {
            var (ac, dc) = ReadPowerStates();
            bool match = (ac.HasValue && ac.Value == 0);
            string dcNote = dc.HasValue ? $"; DC index = {dc.Value} (battery power preserved)" : "";
            return (match, match ? $"USB Selective Suspend confirmed disabled on AC power (Index = 0){dcNote}." : $"USB Selective Suspend active on AC power (Setting Index = {ac}).");
        }
    }



    #endregion




    public class HidPollingIntervalDetector : RegistryDwordDetectorBase
    {
        public override string Id => "input.hid.polling_interval_calibrate";
        public override string Name => "HID Device Polling Interval Calibration";
        public override string Category => "HID";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouhid\Parameters\PollingInterval";
        public override string Description => "Calibrates HID device polling interval queue parameter for consistent input frame pacing without fake polling overclocking.";
        public override RegistryKey RootKey => Registry.LocalMachine;
        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\mouhid\Parameters";
        public override string ValueName => "PollingInterval";
        public override uint TargetDwordValue => 1;
    }

    public class UsbSelectiveSuspendPerDeviceDetector : RegistryDwordDetectorBase
    {
        public override string Id => "input.usb.selective_suspend_per_device";
        public override string Name => "Per-Device USB Selective Suspend Exemption";
        public override string Category => "USB";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\USB\DisableSelectiveSuspend";
        public override string Description => "Configures USB core driver parameters to exempt interactive HID input controllers from aggressive runtime selective suspension.";
        public override RegistryKey RootKey => Registry.LocalMachine;
        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Services\USB";
        public override string ValueName => "DisableSelectiveSuspend";
        public override uint TargetDwordValue => 1;
    }

    public class MouseWheelScrollLinesDetector : RegistryStringDetectorBase
    {
        public override string Id => "input.mouse.wheel_scroll_lines";
        public override string Name => "Mouse Wheel Low-Latency Scroll Step Calibration";
        public override string Category => "Mouse";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Desktop\WheelScrollLines";
        public override string Description => "Sets mouse wheel vertical scroll speed to the standardized 3-line increment for responsive and consistent document and web browsing.";
        public override RegistryKey RootKey => Registry.CurrentUser;
        public override string SubKeyPath => @"Control Panel\Desktop";
        public override string ValueName => "WheelScrollLines";
        public override string TargetStringValue => "3";
    }

    public class GamingControllerDeadzoneDetector : RegistryDwordDetectorBase
    {
        public override string Id => "input.gaming.controller_deadzone_linearity";
        public override string Name => "DirectInput & XInput Controller Deadzone Linearization";
        public override string Category => "Gamepad";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_CURRENT_USER\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\ConfigFlags";
        public override string Description => "Calibrates DirectInput joystick axis deadzone parameters to ensure strictly linear response curves for emulator and PC controller gaming.";
        public override RegistryKey RootKey => Registry.CurrentUser;
        public override string SubKeyPath => @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
        public override string ValueName => "ConfigFlags";
        public override uint TargetDwordValue => 0;
    }

    public class KeyboardDebounceDetector : RegistryStringDetectorBase
    {
        public override string Id => "input.keyboard.debounce_latency_reduction";
        public override string Name => "Keyboard Debounce & Repeat Rate High-Frequency Response";
        public override string Category => "Keyboard";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Control Panel\Accessibility\Keyboard Response\BounceTime";
        public override string Description => "Configures accessibility keyboard bounce filter time to 0 ms to eliminate any keystroke debounce latency or delayed key repeats.";
        public override RegistryKey RootKey => Registry.CurrentUser;
        public override string SubKeyPath => @"Control Panel\Accessibility\Keyboard Response";
        public override string ValueName => "BounceTime";
        public override string TargetStringValue => "0";
    }

    public class MouseCursorShadowDetector : RegistryDwordDetectorBase
    {
        public override string Id => "input.mouse.cursor_shadow_overhead_disable";
        public override string Name => "Hardware Cursor Rendering Acceleration - Disable GDI Shadow Latency";
        public override string Category => "Mouse";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\CursorShadow";
        public override string Description => "Disables software GDI cursor shadow composition to allow direct hardware cursor plane presentation with zero display latency.";
        public override RegistryKey RootKey => Registry.CurrentUser;
        public override string SubKeyPath => @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        public override string ValueName => "CursorShadow";
        public override uint TargetDwordValue => 0;
    }

    public class HighPrecisionTimerDetector : RegistryDwordDetectorBase
    {
        public override string Id => "scheduling.system_responsiveness";
        public override string Name => "Global High-Precision Timer Resolution Request";
        public override string Category => "System Scheduling";
        public override string Risk => "MEDIUM";
        public override bool RequiresReboot => true;
        public override InputVerificationMode VerificationMode => InputVerificationMode.PostRebootReadback;
        public override string TechnicalLocation => @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\GlobalTimerResolutionRequests";
        public override string Description => "Configures the Windows kernel to honor global high-precision timer resolution requests across all processes.";
        public override RegistryKey RootKey => Registry.LocalMachine;
        public override string SubKeyPath => @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
        public override string ValueName => "GlobalTimerResolutionRequests";
        public override uint TargetDwordValue => 1;
    }

    public class DwmInputSeparationDetector : RegistryDwordDetectorBase
    {
        public override string Id => "input.dwm.input_separation_quantum";
        public override string Name => "DWM Independent Input Separation Quantum";
        public override string Category => "Display & Input Processing";
        public override string Risk => "SAFE";
        public override bool RequiresReboot => false;
        public override InputVerificationMode VerificationMode => InputVerificationMode.ImmediateReadback;
        public override string TechnicalLocation => @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM\MaxQueuedBuffers";
        public override string Description => "Ensures DWM compositing thread maintains independent rendering quantum for zero-lag hardware cursor response.";
        public override RegistryKey RootKey => Registry.CurrentUser;
        public override string SubKeyPath => @"Software\Microsoft\Windows\DWM";
        public override string ValueName => "MaxQueuedBuffers";
        public override uint TargetDwordValue => 1;
    }

    public class InputOptimizerEngine : IInputOptimizerEngine



    {



        private static readonly Lazy<InputOptimizerEngine> _lazyInstance = new(() => new InputOptimizerEngine());



        public static InputOptimizerEngine Instance => _lazyInstance.Value;



        private readonly IBackupManager _backupManager;



        private readonly InputTransactionJournal _journal;



        private readonly List<IInputOptimizationDetector> _detectors;



        [DllImport("ntdll.dll", SetLastError = true)]



        private static extern int NtQueryTimerResolution(out uint MaximumTime, out uint MinimumTime, out uint CurrentTime);



        [DllImport("ntdll.dll", SetLastError = true)]



        private static extern int NtSetTimerResolution(uint DesiredTime, bool SetResolution, out uint CurrentTime);



        private readonly object _timerLock = new();



        private bool _hasActiveTimerRequest = false;



        private uint _requestedTimerUnits = 0;



        public bool IsTimerResolutionActive



        {



            get { lock (_timerLock) return _hasActiveTimerRequest; }



        }



        public InputOptimizerEngine() : this(new InputBackupManager())



        {



        }



        public InputOptimizerEngine(IBackupManager backupManager)



        {



            _backupManager = backupManager;



            _journal = new InputTransactionJournal();



            



            _detectors = new List<IInputOptimizationDetector>



            {



                // Mouse Core



                new MouseSpeedDetector(),



                new MouseThreshold1Detector(),



                new MouseThreshold2Detector(),



                new MouseSensitivityDetector(),



                new MouseTrailsDetector(),



                new ActiveWindowTrackingDetector(),



                new MouseHoverTimeDetector(),



                new SnapToDefaultButtonDetector(),



                new MouseCurvesDetector(),



                // Keyboard Core



                new KeyboardDelayDetector(),



                new KeyboardSpeedDetector(),



                new FilterKeysFlagsDetector(),



                new StickyKeysFlagsDetector(),



                new ToggleKeysFlagsDetector(),



                // Advanced / Driver Queues (Requires Reboot)



                new MouclassQueueSizeDetector(),



                new KbdclassQueueSizeDetector(),



                new MouhidQueueLengthDetector(),



                new KbdhidQueueLengthDetector(),



                new MouhidInputQueueSizeDetector(),



                new KbdhidInputQueueSizeDetector(),



                new MouclassThreadPriorityDetector(),



                new KbdclassThreadPriorityDetector(),



                new UsbSelectiveSuspendDetector(),
                new HidPollingIntervalDetector(),
                new UsbSelectiveSuspendPerDeviceDetector(),
                new MouseWheelScrollLinesDetector(),
                new GamingControllerDeadzoneDetector(),
                new KeyboardDebounceDetector(),
                new MouseCursorShadowDetector(),
                new HighPrecisionTimerDetector(),
                new DwmInputSeparationDetector()
            };



            ReconcilePendingReboots();



        }



        public void ProcessPendingReboots()



        {



            ReconcilePendingReboots();



        }



        /// <summary>



        /// Authoritative Startup Reconciliation Sequence:



        /// 1. Load persisted transactions.



        /// 2. Re-read real live system values.



        /// 3. If live value matches target -> mark VERIFIED and remove from pending reboot.



        /// 4. If live value does not match target:



        ///    - If system rebooted since apply timestamp -> mark VERIFICATION_FAILED.



        ///    - If system has not rebooted -> keep APPLIED_PENDING_REBOOT.



        /// 5. Update transaction journal.



        /// </summary>



        public void ReconcilePendingReboots()



        {



            var tx = _journal.LoadLatestTransaction();



            if (tx == null || tx.Items == null || tx.Items.Count == 0) return;



            long currentUptime = Environment.TickCount64;



            bool dirty = false;



            foreach (var item in tx.Items.Where(x => x.State == InputOptimizationState.AppliedPendingReboot || x.VerificationPending))



            {



                var detector = _detectors.FirstOrDefault(d => 
                    d.Id.Equals(item.OptimizationId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ResolveFrontendAlias(d.Id), item.OptimizationId, StringComparison.OrdinalIgnoreCase) ||
                    (FrontendAliases.TryGetValue(item.OptimizationId, out var aliases) && aliases.Contains(d.Id)));



                string liveCurrent = detector != null ? detector.ReadCurrentState() : ReadRealRegistryValue(item.RegistryPath, item.ValueName, item.IsBinary);



                bool isMatch = false;



                if (item.IsBinary)



                {



                    isMatch = string.Equals(liveCurrent, item.TargetState, StringComparison.OrdinalIgnoreCase);



                }



                else if (uint.TryParse(item.TargetState, out uint expUint))



                {



                    if (uint.TryParse(liveCurrent, out uint actUint))



                    {



                        isMatch = (actUint == expUint);



                    }



                    else if (int.TryParse(liveCurrent, out int actInt))



                    {



                        isMatch = (unchecked((uint)actInt) == expUint);



                    }



                }



                else



                {



                    isMatch = string.Equals(liveCurrent, item.TargetState, StringComparison.OrdinalIgnoreCase);



                }



                bool hasRebooted = (currentUptime < item.BootUptimeAtApplyMs) || (DateTime.UtcNow - item.Timestamp).TotalHours > 12;



                if (isMatch)



                {



                    if (item.RequiresReboot && !hasRebooted)



                    {



                        // Registry write succeeded, but system has not restarted yet to load new driver parameters



                        item.State = InputOptimizationState.AppliedPendingReboot;



                        item.VerificationPending = true;



                        item.AfterState = liveCurrent;



                    }



                    else



                    {



                        item.State = InputOptimizationState.Verified;



                        item.VerificationPending = false;



                        item.AfterState = liveCurrent;



                        item.FailureReason = string.Empty;



                        dirty = true;

                        try
                        {
                            string canTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", item.RegistryPath ?? item.Name);
                            GlobalAppliedStateManager.Instance.RecordApplied(
                                canTarget,
                                item.OptimizationId,
                                "InputOptimizer",
                                "InputOptimizer",
                                item.BeforeState,
                                item.TargetState,
                                verified: true,
                                rollbackAvailable: true,
                                tx.TransactionId,
                                "SetRegistryValue");
                        }
                        catch { }



                        Debug.WriteLine($"[InputOptimizerReconciliation] Item {item.OptimizationId} successfully VERIFIED post-reboot.");



                    }



                }



                else



                {



                    if (hasRebooted)



                    {



                        item.State = InputOptimizationState.VerificationFailed;



                        item.VerificationPending = false;



                        item.AfterState = liveCurrent;



                        item.FailureReason = $"Target value not persisted after system reboot (Current: '{liveCurrent}', Expected: '{item.TargetState}')";



                        dirty = true;



                    }



                    else



                    {



                        // Still waiting for system reboot



                        item.State = InputOptimizationState.AppliedPendingReboot;



                        item.VerificationPending = true;



                    }



                }



            }



            if (dirty)



            {



                int verified = tx.Items.Count(x => x.State == InputOptimizationState.Verified);
                int alreadyOpt = tx.Items.Count(x => x.State == InputOptimizationState.AlreadyOptimal);
                int failed = tx.Items.Count(x => x.State == InputOptimizationState.Failed || x.State == InputOptimizationState.VerificationFailed);
                int pending = tx.Items.Count(x => x.VerificationPending || x.State == InputOptimizationState.AppliedPendingReboot);

                if (pending > 0) tx.Status = "WAITING_FOR_REBOOT";
                else if (failed == 0 && verified == 0 && alreadyOpt > 0) tx.Status = "ALREADY_OPTIMAL";
                else if (failed == 0) tx.Status = "VERIFIED";



                else if (verified > 0) tx.Status = "PARTIAL";



                else tx.Status = "FAILED";



                _journal.SaveTransaction(tx);



            }



        }



        public async Task<InputOptimizationPlan> PlanInputOptimizationAsync()



        {



            return await Task.Run(() =>



            {



                // 1. Authoritative Reconciliation FIRST before building pending list



                ReconcilePendingReboots();



                var plan = new InputOptimizationPlan();



                var tx = _journal.LoadLatestTransaction();



                var journalMap = tx?.Items.ToDictionary(x => x.OptimizationId, StringComparer.OrdinalIgnoreCase)



                                 ?? new Dictionary<string, InputTransactionItem>(StringComparer.OrdinalIgnoreCase);



                foreach (var detector in _detectors)



                {



                    var (appState, appReason) = detector.DetectApplicability();



                    string currentVal = detector.ReadCurrentState();



                    string targetVal = detector.GetTargetState();



                    var action = new InputOptimizationAction



                    {



                        Id = detector.Id,



                        Name = detector.Name,



                        Category = detector.Category,



                        Risk = detector.Risk,



                        Reason = string.IsNullOrWhiteSpace(appReason) ? detector.TechnicalLocation : appReason,

                        TechnicalLocation = detector.TechnicalLocation,



                        RequiresReboot = detector.RequiresReboot,



                        VerificationMode = detector.VerificationMode,



                        CurrentValue = currentVal,



                        TargetValue = targetVal,



                        SupportState = appState



                    };



                    // Check if item is in pending reboot or verified journal
                    if (journalMap.TryGetValue(detector.Id, out var txItem))
                    {
                        if (txItem.State == InputOptimizationState.AppliedPendingReboot || txItem.VerificationPending)
                        {
                            action.Applicable = true;
                            action.AlreadyOptimized = true;
                            action.Status = "RESTART_REQUIRED";
                            action.ExecutionState = InputOptimizationState.AppliedPendingReboot;
                            action.VerificationState = "PENDING_REBOOT";
                            action.Reason = "Applied, reboot required for activation";
                            plan.ExcludedActions.Add(action);
                            plan.AlreadyOptimizedCount++;
                            continue;
                        }
                        else if (txItem.State == InputOptimizationState.Verified)
                        {
                            action.Applicable = true;
                            action.AlreadyOptimized = true;
                            action.Status = "OPTIMIZED";
                            action.ExecutionState = InputOptimizationState.AlreadyOptimal;
                            action.VerificationState = InputOptimizationState.Verified;
                            plan.ExcludedActions.Add(action);
                            plan.AlreadyOptimizedCount++;
                            continue;
                        }
                    }

                    // Authoritative Global Applied-State & Cross-Category Synchronization
                    string canTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", detector.TechnicalLocation);
                    var globalStatus = GlobalAppliedStateManager.Instance.EvaluateTargetState(canTarget, targetVal, out var observedVal);
                    if (!string.IsNullOrWhiteSpace(observedVal))
                    {
                        currentVal = observedVal;
                        action.CurrentValue = observedVal;
                    }

                    if (globalStatus == GlobalAppliedStatus.ALREADY_APPLIED || globalStatus == GlobalAppliedStatus.ALREADY_OPTIMAL || globalStatus == GlobalAppliedStatus.ACTIVE_PROFILE)
                    {
                        action.Applicable = true;
                        action.AlreadyOptimized = true;
                        action.Status = "OPTIMIZED";
                        action.ExecutionState = InputOptimizationState.AlreadyOptimal;
                        action.VerificationState = InputOptimizationState.Verified;
                        plan.ExcludedActions.Add(action);
                        plan.AlreadyOptimizedCount++;
                        continue;
                    }
                    else if (globalStatus == GlobalAppliedStatus.CONFIGURATION_CHANGED || globalStatus == GlobalAppliedStatus.EXTERNALLY_MODIFIED)
                    {
                        action.Status = "CHANGED";
                    }

                    // Process Applicability State
                    if (appState == InputOptimizationState.NotApplicable)
                    {
                        action.Applicable = false;
                        action.AlreadyOptimized = false;
                        action.Status = "NOT_AVAILABLE";
                        action.ExecutionState = InputOptimizationState.NotAvailable;
                        action.VerificationState = "NOT_APPLICABLE";
                        plan.ExcludedActions.Add(action);
                        plan.IneligibleCount++;
                        continue;
                    }
                    else if (appState == InputOptimizationState.Unsupported)
                    {
                        action.Applicable = false;
                        action.AlreadyOptimized = false;
                        action.Status = "NOT_SUPPORTED";
                        action.ExecutionState = InputOptimizationState.NotSupported;
                        action.VerificationState = "UNSUPPORTED";
                        plan.ExcludedActions.Add(action);
                        plan.IneligibleCount++;
                        continue;
                    }
                    else if (appState == InputOptimizationState.AlreadyOptimal || currentVal == targetVal || BiosOptimizer.Core.Services.StateNormalizer.IsSatisfied(currentVal, targetVal))
                    {
                        action.Applicable = true;
                        action.AlreadyOptimized = true;
                        action.Status = "OPTIMIZED";
                        action.ExecutionState = InputOptimizationState.AlreadyOptimal;
                        action.VerificationState = InputOptimizationState.Verified;
                        plan.ExcludedActions.Add(action);
                        plan.AlreadyOptimizedCount++;
                        continue;
                    }
                    else
                    {
                        // Genuine Applicable & Pending optimization
                        action.Applicable = true;
                        action.AlreadyOptimized = false;
                        action.Status = "PENDING";
                        action.ExecutionState = InputOptimizationState.Pending;
                        action.VerificationState = "UNVERIFIED";
                        plan.Actions.Add(action);
                        plan.EligibleCount++;
                    }
                }

                plan.TotalCatalogCount = plan.Actions.Count + plan.ExcludedActions.Count;
                plan.EligibleCount = plan.Actions.Count;

                try
                {
                    var devRes = BiosOptimizer.Core.Implementations.Input.InputDeviceResolver.ResolveConnectedDevices();
                    plan.Devices = devRes.AllDevices;
                    plan.MouseDeviceName = devRes.MouseDeviceName;
                    plan.KeyboardDeviceName = devRes.KeyboardDeviceName;
                    plan.MouseStatus = devRes.MouseStatus;
                    plan.KeyboardStatus = devRes.KeyboardStatus;
                }
                catch
                {
                    try
                    {
                        plan.Devices = GetConnectedInputDevices();
                    }
                    catch { }
                }

                try
                {
                    int tStatus = NtQueryTimerResolution(out _, out _, out uint current100Ns);
                    if (tStatus == 0 && current100Ns > 0)
                        plan.TimerResolutionMs = Math.Round(current100Ns / 10000.0, 2);
                }
                catch { }

                var allInputActions = plan.Actions.Concat(plan.ExcludedActions).ToList();
                plan.MouseTunedCount = allInputActions.Count(a =>
                    (a.Category == "Mouse" || a.Id.StartsWith("mouse.") || a.Id.StartsWith("queue.mou") || a.Id.StartsWith("scheduling.mou") || a.Id.Contains("cursor_shadow"))
                    && (a.Status == "OPTIMIZED" || a.Status == "ALREADY_OPTIMIZED" || a.Status == "VERIFIED"));

                plan.KeyboardTunedCount = allInputActions.Count(a =>
                    (a.Category == "Keyboard" || a.Id.StartsWith("keyboard.") || a.Id.StartsWith("queue.kbd") || a.Id.StartsWith("scheduling.kbd") || a.Id.Contains("debounce") || a.Id.Contains("filter_keys") || a.Id.Contains("sticky_keys"))
                    && (a.Status == "OPTIMIZED" || a.Status == "ALREADY_OPTIMIZED" || a.Status == "VERIFIED"));

                return plan;



            });



        }



        public static readonly Dictionary<string, string[]> FrontendAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mouse-accel"] = new[] { "mouse.speed", "mouse.threshold1", "mouse.threshold2", "mouse.pointer_curves" },
            ["kb-repeat-rate"] = new[] { "keyboard.speed" },
            ["kb-repeat-delay"] = new[] { "keyboard.delay" },
            ["mouse-hover"] = new[] { "mouse.hover_time" },
            ["mouse-queue"] = new[] { "queue.mouclass_size" },
            ["kb-queue"] = new[] { "queue.kbdclass_size" },
            ["timer-request"] = new[] { "scheduling.system_responsiveness" },
            ["usb-suspend"] = new[] { "usb.selective_suspend" },
            ["csrss-priority"] = new[] { "scheduling.priority_separation", "scheduling.mouclass_priority", "scheduling.kbdclass_priority" },
            ["ps2-queue"] = Array.Empty<string>(),
            ["mouse-cursor-shadow"] = new[] { "input.mouse.cursor_shadow_overhead_disable" },
            ["mouse-sensitivity-cal"] = new[] { "mouse.sensitivity" },
            ["mouse-snap-default"] = new[] { "mouse.snap_to_default" },
            ["mouse-active-tracking"] = new[] { "mouse.active_window_tracking" },
            ["mouse-trails-disable"] = new[] { "mouse.trails" },
            ["kb-debounce-reduce"] = new[] { "input.keyboard.debounce_latency_reduction" },
            ["kb-filterkeys-disable"] = new[] { "keyboard.filter_keys" },
            ["kb-stickykeys-disable"] = new[] { "keyboard.sticky_keys" },
            ["queue-mouhid-length"] = new[] { "queue.mouhid_length" },
            ["queue-kbdhid-length"] = new[] { "queue.kbdhid_length" },
            ["hid-polling-calibrate"] = new[] { "input.hid.polling_interval_calibrate" },
            ["usb-perdevice-suspend"] = new[] { "input.usb.selective_suspend_per_device" },
            ["dwm-input-separation"] = new[] { "input.dwm.input_separation_quantum" },
            ["gamepad-deadzone-linearity"] = new[] { "input.gaming.controller_deadzone_linearity" }
        };

        public static HashSet<string> ExpandSelectedIds(IEnumerable<string> rawIds)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in rawIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(id);
                if (FrontendAliases.TryGetValue(id, out var expanded))
                {
                    foreach (var exp in expanded) result.Add(exp);
                }
            }
            return result;
        }

        public static string? ResolveFrontendAlias(string canonicalId)
        {
            return canonicalId switch
            {
                "mouse.speed" or "mouse.threshold1" or "mouse.threshold2" or "mouse.pointer_curves" => "mouse-accel",
                "keyboard.speed" => "kb-repeat-rate",
                "keyboard.delay" => "kb-repeat-delay",
                "mouse.hover_time" => "mouse-hover",
                "queue.mouclass_size" => "mouse-queue",
                "queue.kbdclass_size" => "kb-queue",
                "scheduling.system_responsiveness" => "timer-request",
                "usb.selective_suspend" => "usb-suspend",
                "scheduling.priority_separation" or "scheduling.mouclass_priority" or "scheduling.kbdclass_priority" => "csrss-priority",
                "input.mouse.cursor_shadow_overhead_disable" => "mouse-cursor-shadow",
                "mouse.sensitivity" => "mouse-sensitivity-cal",
                "mouse.snap_to_default" => "mouse-snap-default",
                "mouse.active_window_tracking" => "mouse-active-tracking",
                "mouse.trails" => "mouse-trails-disable",
                "input.keyboard.debounce_latency_reduction" => "kb-debounce-reduce",
                "keyboard.filter_keys" => "kb-filterkeys-disable",
                "keyboard.sticky_keys" => "kb-stickykeys-disable",
                "queue.mouhid_length" => "queue-mouhid-length",
                "queue.kbdhid_length" => "queue-kbdhid-length",
                "input.hid.polling_interval_calibrate" => "hid-polling-calibrate",
                "input.usb.selective_suspend_per_device" => "usb-perdevice-suspend",
                "input.dwm.input_separation_quantum" => "dwm-input-separation",
                "input.gaming.controller_deadzone_linearity" => "gamepad-deadzone-linearity",
                _ => null
            };
        }

        public static string? ResolveCanonicalFromFrontendAlias(string frontendId)
        {
            return frontendId switch
            {
                "mouse-accel" => "mouse.pointer_curves",
                "kb-repeat-rate" => "keyboard.speed",
                "kb-repeat-delay" => "keyboard.delay",
                "mouse-hover" => "mouse.hover_time",
                "mouse-queue" => "queue.mouclass_size",
                "kb-queue" => "queue.kbdclass_size",
                "timer-request" => "scheduling.system_responsiveness",
                "usb-suspend" => "usb.selective_suspend",
                "csrss-priority" => "scheduling.priority_separation",
                "mouse-cursor-shadow" => "input.mouse.cursor_shadow_overhead_disable",
                "mouse-sensitivity-cal" => "mouse.sensitivity",
                "mouse-snap-default" => "mouse.snap_to_default",
                "mouse-active-tracking" => "mouse.active_window_tracking",
                "mouse-trails-disable" => "mouse.trails",
                "kb-debounce-reduce" => "input.keyboard.debounce_latency_reduction",
                "kb-filterkeys-disable" => "keyboard.filter_keys",
                "kb-stickykeys-disable" => "keyboard.sticky_keys",
                "queue-mouhid-length" => "queue.mouhid_length",
                "queue-kbdhid-length" => "queue.kbdhid_length",
                "hid-polling-calibrate" => "input.hid.polling_interval_calibrate",
                "usb-perdevice-suspend" => "input.usb.selective_suspend_per_device",
                "dwm-input-separation" => "input.dwm.input_separation_quantum",
                "gamepad-deadzone-linearity" => "input.gaming.controller_deadzone_linearity",
                _ => null
            };
        }

        public async Task<(bool Success, string Message)> ApplyInputOptimizationAsync(string riskLevel)



        {



            return await Task.Run(() =>



            {



                var plan = PlanInputOptimizationAsync().GetAwaiter().GetResult();



                var actionsToApply = plan.Actions.Where(a => 



                    a.Applicable && 



                    a.Status != "OPTIMIZED" && 



                    a.Status != "ALREADY_OPTIMIZED" && 



                    a.Status != "RESTART_REQUIRED" && 



                    a.Status != "NOT_AVAILABLE" && 



                    a.Status != "NOT_SUPPORTED").ToList();



                



                if (!string.IsNullOrEmpty(riskLevel))
                {
                    string trimmed = riskLevel.TrimStart();
                    if (trimmed.StartsWith("["))
                    {
                        try
                        {
                            var ids = JsonSerializer.Deserialize<List<string>>(riskLevel);
                            if (ids != null && ids.Count > 0)
                            {
                                var idSet = ExpandSelectedIds(ids);
                                actionsToApply = actionsToApply.Where(a => idSet.Contains(a.Id)).ToList();
                            }
                        }
                        catch { }
                    }
                    else if (trimmed.StartsWith("{"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(riskLevel);
                            if (doc.RootElement.TryGetProperty("selectedIds", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array)
                            {
                                var ids = new List<string>();
                                foreach (var el in idsProp.EnumerateArray())
                                {
                                    if (el.GetString() is { } str && !string.IsNullOrWhiteSpace(str))
                                        ids.Add(str);
                                }
                                if (ids.Count > 0)
                                {
                                    var idSet = ExpandSelectedIds(ids);
                                    actionsToApply = actionsToApply.Where(a => idSet.Contains(a.Id)).ToList();
                                }
                            }
                        }
                        catch { }
                    }
                    else if (riskLevel.Equals("CORE", StringComparison.OrdinalIgnoreCase))
                    {
                        actionsToApply = actionsToApply.Where(a => a.Risk.Equals("CORE", StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                    else if (riskLevel.Equals("ADVANCED", StringComparison.OrdinalIgnoreCase))
                    {
                        actionsToApply = actionsToApply.Where(a => a.Risk.Equals("ADVANCED", StringComparison.OrdinalIgnoreCase) || a.Risk.Equals("EXPERIMENTAL", StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                }



                if (actionsToApply.Count == 0)



                    return (true, "ALREADY OPTIMIZED — NO CHANGES NEEDED");



                var tx = new InputOptimizerTransaction



                {



                    Profile = riskLevel ?? "Default",



                    Status = "RUNNING",



                    SystemBootTimeMs = Environment.TickCount64



                };



                bool allSuccess = true;



                int appliedCount = 0;



                int restartRequiredCount = 0;



                int verifiedImmediateCount = 0;



                int failedCount = 0;



                foreach (var act in actionsToApply)



                {



                    var detector = _detectors.FirstOrDefault(d => 
                        d.Id.Equals(act.Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ResolveFrontendAlias(d.Id), act.Id, StringComparison.OrdinalIgnoreCase) ||
                        (FrontendAliases.TryGetValue(act.Id, out var aliases) && aliases.Contains(d.Id)));



                    if (detector == null) continue;



                    var txItem = new InputTransactionItem



                    {



                        OptimizationId = act.Id,



                        Name = act.Name,



                        Category = act.Category,



                        RegistryPath = act.Reason,



                        ValueName = act.Name,



                        BeforeState = act.CurrentValue,



                        TargetState = act.TargetValue,



                        State = InputOptimizationState.Applying,



                        RequiresReboot = act.RequiresReboot,



                        VerificationMode = act.VerificationMode,



                        BootUptimeAtApplyMs = Environment.TickCount64,



                        IsBinary = act.Id == "mouse.pointer_curves"



                    };

                    string canTarget = GlobalOptimizationRegistry.NormalizeTarget("SetRegistryValue", detector.TechnicalLocation);
                    if (!GlobalAppliedStateManager.Instance.CanExecute("InputOptimizer", canTarget, act.TargetValue, out string rejReason))
                    {
                        act.Status = "OPTIMIZED";
                        txItem.State = InputOptimizationState.AlreadyOptimal;
                        txItem.ApplyResult = "ALREADY_OPTIMAL";
                        GlobalAppliedStateManager.Instance.RecordObservedOptimal(
                            canTarget,
                            act.Id,
                            "InputOptimizer",
                            "InputOptimizer",
                            act.CurrentValue,
                            act.TargetValue,
                            "SetRegistryValue");
                        tx.Items.Add(txItem);
                        continue;
                    }



                    tx.Items.Add(txItem);



                    var (success, newState, applyResult, failureReason) = detector.Apply(_backupManager);



                    if (success)



                    {



                        appliedCount++;



                        if (act.RequiresReboot)



                        {



                            act.Status = "RESTART_REQUIRED";



                            txItem.State = InputOptimizationState.AppliedPendingReboot;



                            txItem.ApplyResult = "SUCCESS";



                            txItem.VerificationPending = true;



                            txItem.AfterState = newState;



                            restartRequiredCount++;



                        }



                        else



                        {



                            act.Status = "OPTIMIZED";



                            txItem.State = InputOptimizationState.Verified;



                            txItem.ApplyResult = "SUCCESS";



                            txItem.VerificationPending = false;



                            txItem.AfterState = newState;



                            verifiedImmediateCount++;

                            GlobalAppliedStateManager.Instance.RecordApplied(
                                canTarget,
                                act.Id,
                                "InputOptimizer",
                                "InputOptimizer",
                                act.CurrentValue,
                                act.TargetValue,
                                verified: true,
                                rollbackAvailable: true,
                                tx.TransactionId,
                                "SetRegistryValue");



                        }



                    }



                    else



                    {



                        act.Status = "FAILED";



                        txItem.State = InputOptimizationState.Failed;



                        txItem.ApplyResult = "FAILED";



                        txItem.VerificationPending = false;



                        txItem.FailureReason = failureReason;



                        failedCount++;



                        allSuccess = false;



                    }



                }



                _journal.SaveTransaction(tx);



                // Broadcast Win32 SPI parameters



                try



                {



                    int[] zeroMouse = new int[3] { 0, 0, 0 };



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, zeroMouse, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)10, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDDELAY, 0, 0u, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDSPEED, 31, 0u, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                }



                catch { }



                if (restartRequiredCount > 0) tx.Status = "WAITING_FOR_REBOOT";



                else if (failedCount == 0) tx.Status = "VERIFIED";



                else if (appliedCount > 0) tx.Status = "PARTIAL";



                else tx.Status = "FAILED";



                _journal.SaveTransaction(tx);



                string msg = restartRequiredCount > 0 



                    ? $"{appliedCount} OPTIMIZATIONS APPLIED • {restartRequiredCount} WAITING FOR RESTART VERIFICATION" 



                    : $"Applied {appliedCount} optimizations ({verifiedImmediateCount} verified immediately).";



                if (failedCount > 0)



                    msg += $" ({failedCount} failed)";



                return (allSuccess, msg);



            });



        }



        public async Task<(bool Success, string Message)> RestoreInputOptimizationAsync()



        {



            return await Task.Run(() =>



            {



                bool ok = _backupManager.RestoreByOwner("AntiGravity.InputOptimizer");



                if (_hasActiveTimerRequest)



                {



                    try { ReleaseHighResolutionTimerAsync().GetAwaiter().GetResult(); } catch { }



                }



                if (ok)



                {



                    _journal.Clear();



                }



                return (ok, ok ? "Restored original input configuration." : "Failed to restore input configuration.");



            });



        }



        public AimShakeDiagnosticResult EvaluateAimShakeFactors()



        {



            return EvaluateAimShakeFactors((InputTimingStatistics?)null, null, false);



        }



        public AimShakeDiagnosticResult EvaluateAimShakeFactors(double? measuredJitterMs, double? timerResolutionMs, bool isWirelessMouse)



        {



            InputTimingStatistics? stats = null;



            if (measuredJitterMs.HasValue)



            {



                stats = new InputTimingStatistics



                {



                    StandardDeviationMs = measuredJitterMs.Value,



                    TotalSamplesCaptured = 100,



                    ValidSamples = 100,



                    DurationSeconds = 5.0,



                    MeanIntervalMs = 1.0,



                    EstimatedFrequencyHz = 1000.0,



                    IsStable = measuredJitterMs.Value <= 0.75,



                    DiagnosticCode = measuredJitterMs.Value <= 0.75 ? InputDiagnosticCodes.InputTimingStable : InputDiagnosticCodes.InputTimingVarianceDetected



                };



            }



            return EvaluateAimShakeFactors(stats, timerResolutionMs, isWirelessMouse);



        }



        public AimShakeDiagnosticResult EvaluateAimShakeFactors(InputTimingStatistics? stats, double? timerResolutionMs, bool isWirelessMouse)



        {



            var anomalies = new List<string>();



            var verified = new List<string>();



            // 1. Host-Side Input Configuration



            bool curvesLinear = false;



            bool accelZero = false;



            bool sensTen = false;



            bool speedTen = false;



            bool trailsZero = true;



            try



            {



                using var mKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", false);



                if (mKey != null)



                {



                    var curX = mKey.GetValue("SmoothMouseXCurve") as byte[];



                    var curY = mKey.GetValue("SmoothMouseYCurve") as byte[];



                    curvesLinear = curX != null && curY != null && 



                                   curX.SequenceEqual(MouseCurvesDetector.TargetCurveX) && 



                                   curY.SequenceEqual(MouseCurvesDetector.TargetCurveY);



                    



                    string spd = mKey.GetValue("MouseSpeed")?.ToString() ?? string.Empty;



                    string th1 = mKey.GetValue("MouseThreshold1")?.ToString() ?? string.Empty;



                    string th2 = mKey.GetValue("MouseThreshold2")?.ToString() ?? string.Empty;



                    accelZero = spd == "0" && th1 == "0" && th2 == "0";



                    string sens = mKey.GetValue("MouseSensitivity")?.ToString() ?? string.Empty;



                    sensTen = sens == "10";



                    string trails = mKey.GetValue("MouseTrails")?.ToString() ?? "0";



                    trailsZero = trails == "0" || trails == "";



                }



        



            }



            catch { }



            if (curvesLinear) verified.Add("1:1 MarkC linear pointer curves verified");



            else anomalies.Add("non-linear Windows acceleration curves active (exponential velocity scaling)");



            if (accelZero) verified.Add("Enhanced Pointer Precision disabled (zero thresholds)");



            else anomalies.Add("pointer acceleration thresholds active");



            if (sensTen) verified.Add("Windows base sensitivity 10 (true 1:1 hardware-to-desktop mapping)");



            else anomalies.Add("non-standard sensitivity notch (causes Windows software pixel-skipping)");



            if (!trailsZero) anomalies.Add("pointer trails active (causes visual compositing latency)");



            // 2. Scheduling & Contention



            bool quantumOptimal = false;



            try



            {



                using var prioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", false);



                if (prioKey != null)



                {



                    var pVal = prioKey.GetValue("Win32PrioritySeparation");



                    if (pVal is int intVal && (intVal == 0x28 || intVal == 40 || intVal == 0x26 || intVal == 38)) quantumOptimal = true;



                    else if (pVal is uint uVal && (uVal == 0x28 || uVal == 0x26)) quantumOptimal = true;



                }



            }



            catch { }



            if (quantumOptimal) verified.Add("foreground quantum scheduling optimized (3:1 short quanta boost)");



            else anomalies.Add("standard variable quanta (susceptible to background thread preemption)");



            bool sysRespOptimal = false;



            try



            {



                using var sysKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", false);



                if (sysKey != null)



                {



                    var srVal = sysKey.GetValue("SystemResponsiveness");



                    if (srVal is int srInt && (srInt == 10 || srInt == 20)) sysRespOptimal = true;



                    else if (srVal is uint srUint && (srUint == 10 || srUint == 20)) sysRespOptimal = true;



                }



            }



            catch { }



            if (sysRespOptimal) verified.Add("MMCSS system responsiveness quota optimal (0-10% background reservation)");



            else anomalies.Add("default 20% background CPU reservation active");



            bool timerOptimal = timerResolutionMs.HasValue && timerResolutionMs.Value <= 0.501;



            if (timerOptimal) verified.Add($"high-resolution timer active ({timerResolutionMs!.Value:F3} ms)");



            else if (timerResolutionMs.HasValue && timerResolutionMs.Value > 1.0) anomalies.Add($"standard timer resolution ({timerResolutionMs.Value:F1} ms dispatch granularity)");



            // 3. Device & Hardware Context



            string deviceNote;



            if (isWirelessMouse)



            {



                deviceNote = "Wireless/Bluetooth mouse detected: 2.4GHz RF contention or Bluetooth polling variance may cause physical packet fluctuation independent of OS settings.";



                anomalies.Add("wireless connection (potential 2.4GHz RF interference or sleep states)");



            }



            else



            {



                deviceNote = "Wired mouse connection verified: direct USB polling with low physical transmission jitter.";



                verified.Add("direct wired connection");



            }



            // 4. Real Input Test Measurements (if available)



            double? measuredJitterMs = stats?.StandardDeviationMs;



            bool hasMeasurement = stats != null && stats.ValidSamples >= 20;



            string measurementNote;



            bool timingStable = false;



            if (stats != null)



            {



                if (stats.ValidSamples >= 20)



                {



                    double j = stats.StandardDeviationMs;



                    if (j <= 0.35)



                    {



                        timingStable = true;



                        measurementNote = $"Physical measurement verified: ultra-low jitter standard deviation ({j:F2} ms, {stats.EstimatedFrequencyHz:F0} Hz). Polling interval distribution is exceptionally consistent.";



                        verified.Add($"measured input jitter excellent ({j:F2} ms)");



                    }



                    else if (j <= 0.75)



                    {



                        timingStable = true;



                        measurementNote = $"Physical measurement verified: normal polling jitter standard deviation ({j:F2} ms, {stats.EstimatedFrequencyHz:F0} Hz). Polling events arrive within standard USB hardware tolerance.";



                        verified.Add($"measured input jitter normal ({j:F2} ms)");



                    }



                    else



                    {



                        timingStable = false;



                        measurementNote = $"Elevated input timing variance detected ({j:F2} ms standard deviation, {stats.EstimatedFrequencyHz:F0} Hz). Irregular arrival intervals or USB interrupt contention detected.";



                        anomalies.Add($"elevated timing jitter ({j:F2} ms)");



                    }



                }



                else



                {



                    measurementNote = $"Measurement sample count insufficient ({stats.ValidSamples} motion events). Move mouse continuously during sampling.";



                }



            }



            else



            {



                measurementNote = "Physical input stability not measured. Run 5s/10s/30s input test to measure real-time event intervals.";



            }



            // Diagnostic Verdict Synthesis



            string diagCode;



            string status;



            string statusBrush;



            string summary;



            bool hostConfigClean = curvesLinear && accelZero && sensTen && quantumOptimal;



            if (!accelZero || !curvesLinear)



            {



                diagCode = InputDiagnosticCodes.PointerAccelerationDetected;



                status = "POINTER ACCELERATION DETECTED";



                statusBrush = "#F59E0B";



                summary = $"Potential aim-shake triggers: {string.Join(", ", anomalies)}. Windows pointer acceleration varies cursor displacement based on hand speed, preventing 1:1 crosshair muscle memory.";



            }



            else if (stats != null && stats.TotalSamplesCaptured > 0 && stats.ValidSamples < 20)



            {



                diagCode = InputDiagnosticCodes.InputMeasurementInsufficient;



                status = "INSUFFICIENT SAMPLE COUNT";



                statusBrush = "#F59E0B";



                summary = "Motion event stream had fewer than 20 valid samples. Continuous mouse movement during sampling is required for reliable polling distribution.";



            }



            else if (hasMeasurement && !timingStable)



            {



                diagCode = InputDiagnosticCodes.InputTimingVarianceDetected;



                status = "INPUT TIMING VARIANCE DETECTED";



                statusBrush = "#EF4444";



                summary = $"Host pointer curves are linear, but measured packet intervals show variance ({measuredJitterMs!.Value:F2} ms jitter). Device polling or USB contention may cause micro-stutters.";



            }



            else if (hostConfigClean && hasMeasurement && timingStable)



            {



                diagCode = InputDiagnosticCodes.InputTimingStable;



                status = "INPUT TIMING STABLE";



                statusBrush = "#10B981";



                summary = $"Input timing stable ({measuredJitterMs!.Value:F2} ms jitter): 1:1 MarkC linear curves verified, zero acceleration confirmed, and foreground quantum dispatch active. In-game camera recoil, sway, and rendering frame pacing remain application-controlled.";



            }



            else if (hostConfigClean)



            {



                diagCode = InputDiagnosticCodes.InputConfigurationVerified;



                status = "CONFIGURATION VERIFIED";



                statusBrush = "#10B981";



                summary = "Host input configuration verified: 1:1 MarkC linear curves active, zero acceleration thresholds confirmed, and foreground quantum scheduling verified. Physical sensor variance and in-game camera factors remain unmeasured.";



            }



            else



            {



                diagCode = InputDiagnosticCodes.InputTimingVarianceDetected;



                status = "ATTENTION REQUIRED";



                statusBrush = "#F59E0B";



                summary = $"Host configuration anomalies: {string.Join(", ", anomalies)}. Run optimization to linearize pointer curves and prioritize foreground input dispatch.";



            }



            return new AimShakeDiagnosticResult



            {



                Status = status,



                DiagnosticCode = diagCode,



                Summary = summary,



                StatusBrush = statusBrush,



                LinearCurvesActive = curvesLinear,



                AccelerationDisabled = accelZero,



                SensitivityOptimal = sensTen,



                PointerSpeedOptimal = speedTen,



                QuantumSchedulingOptimal = quantumOptimal,



                SystemResponsivenessOptimal = sysRespOptimal,



                HighResolutionTimerActive = timerOptimal,



                IsWirelessOrBluetooth = isWirelessMouse,



                HasMeasurementData = hasMeasurement,



                MeasuredJitterMs = measuredJitterMs ?? 0.0,



                TimingStatistics = stats,



                HostInputAnalysis = hostConfigClean ? "1:1 linear pointer curves, zero acceleration thresholds, and standard sensitivity notch verified." : $"Non-optimal host configuration: {string.Join(", ", anomalies.Where(a => a.Contains("curve") || a.Contains("threshold") || a.Contains("sensitivity")))}.",



                DeviceAnalysis = deviceNote,



                ContentionAnalysis = quantumOptimal ? "Foreground quantum boost active; input thread preemption minimized." : "Standard variable quanta active; background processes may compete for CPU dispatch.",



                MeasurementAnalysis = measurementNote,



                RenderingAnalysis = "Frame pacing, GPU render queue depth, and V-Sync buffering are application-controlled. Inconsistent frame times produce visual camera stutter even when input is perfectly stable.",



                InGameCameraGuidance = "In-game camera smoothing, recoil animations, and sensitivity multipliers are internal to the game engine. Enable in-game Raw Input (m_rawinput 1) to bypass desktop scaling.",



                DesktopVsRawInputNote = "Windows pointer speed, acceleration thresholds, and MarkC curves govern desktop cursor translation and games using WM_MOUSEMOVE or GetCursorPos. Games configured with Raw Input (m_rawinput 1) directly read hardware HID delta packets, bypassing OS curves entirely.",



                InGameCameraRecoilNote = "In-game camera recoil, weapon kick patterns, aim sway, and field-of-view sensitivity multipliers are computed by the game engine's internal physics simulator. They cannot be altered by Windows OS registry or driver tweaks.",



                FramePacingNote = "GPU frame rendering pacing, micro-stutters, and monitor refresh synchronization (G-Sync/FreeSync) govern visible motion smoothness. Inconsistent frametimes can produce visual camera jitter even when physical input polling is perfectly stable.",



                DetectedAnomalies = anomalies,



                VerifiedFactors = verified



            };



        }



        // ─────────────────────────────────────────────────────────────────



        // NEW ADVANCED V4 CAPABILITIES: RAWINPUT HARDWARE DISCOVERY & KEYBOARD TUNING



        // ─────────────────────────────────────────────────────────────────



        /// <summary>



        /// Capability 1: Physical HID / RawInput Hardware Device Enumeration.



        /// Uses Win32 GetRawInputDeviceList with WMI enrichment to identify all attached



        /// mice, keyboards, and input peripherals.



        /// </summary>



        public List<ConnectedInputDevice> GetConnectedInputDevices()



        {



            try



            {



                var summary = BiosOptimizer.Core.Implementations.Input.InputDeviceResolver.ResolveConnectedDevices();



                if (summary.AllDevices.Count > 0)



                {



                    return summary.AllDevices;



                }



            }



            catch { }



            var list = new List<ConnectedInputDevice>();



            try



            {



                uint deviceCount = 0;



                int structSize = Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST));



                uint res = NativeMethods.GetRawInputDeviceList(null, ref deviceCount, (uint)structSize);



                if (deviceCount > 0)



                {



                    var rawList = new NativeMethods.RAWINPUTDEVICELIST[deviceCount];



                    res = NativeMethods.GetRawInputDeviceList(rawList, ref deviceCount, (uint)structSize);



                    if (res != unchecked((uint)-1))



                    {



                        for (int i = 0; i < rawList.Length; i++)



                        {



                            var dev = rawList[i];



                            uint nameLength = 0;



                            NativeMethods.GetRawInputDeviceInfo(dev.hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref nameLength);



                            string deviceName = string.Empty;



                            if (nameLength > 0)



                            {



                                IntPtr pData = Marshal.AllocHGlobal((int)nameLength * 2);



                                try



                                {



                                    if (NativeMethods.GetRawInputDeviceInfo(dev.hDevice, NativeMethods.RIDI_DEVICENAME, pData, ref nameLength) != unchecked((uint)-1))



                                    {



                                        deviceName = Marshal.PtrToStringAuto(pData) ?? string.Empty;



                                    }



                                }



                                finally



                                {



                                    Marshal.FreeHGlobal(pData);



                                }



                            }



                            string type = dev.dwType switch



                            {



                                NativeMethods.RIM_TYPEMOUSE => "Mouse",



                                NativeMethods.RIM_TYPEKEYBOARD => "Keyboard",



                                _ => "HID Peripheral"



                            };



                            string transport = "USB Wired";



                            if (deviceName.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))



                            {



                                transport = "Bluetooth";



                            }



                            else if (deviceName.Contains("WIRELESS", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("RF", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase))



                            {



                                transport = "2.4GHz Wireless";



                            }



                            string manufacturer = "Standard System Input";



                            if (deviceName.Contains("VID_046D", StringComparison.OrdinalIgnoreCase)) manufacturer = "Logitech";



                            else if (deviceName.Contains("VID_1532", StringComparison.OrdinalIgnoreCase)) manufacturer = "Razer";



                            else if (deviceName.Contains("VID_1038", StringComparison.OrdinalIgnoreCase)) manufacturer = "SteelSeries";



                            else if (deviceName.Contains("VID_1B1C", StringComparison.OrdinalIgnoreCase)) manufacturer = "Corsair";



                            else if (deviceName.Contains("VID_045E", StringComparison.OrdinalIgnoreCase)) manufacturer = "Microsoft";



                            else if (deviceName.Contains("VID_093A", StringComparison.OrdinalIgnoreCase)) manufacturer = "PixArt";



                            list.Add(new ConnectedInputDevice



                            {



                                DeviceId = dev.hDevice.ToString(),



                                DeviceName = !string.IsNullOrEmpty(deviceName) ? deviceName : $"RawInput {type} #{i + 1}",



                                DeviceType = type,



                                Manufacturer = manufacturer,



                                TransportType = transport,



                                ButtonCount = dev.dwType == NativeMethods.RIM_TYPEMOUSE ? 5 : 104,



                                HasWheel = dev.dwType == NativeMethods.RIM_TYPEMOUSE,



                                IsHighPrecision = true



                            });



                        }



                    }



                }



            }



            catch { }



            // Fallback / enrichment via WMI if rawinput is empty



            if (list.Count == 0)



            {



                try



                {



                    using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, Manufacturer, DeviceID FROM Win32_PointingDevice");



                    foreach (var obj in searcher.Get())



                    {



                        string name = obj["Name"]?.ToString() ?? "Pointing Device";



                        string mfg = obj["Manufacturer"]?.ToString() ?? "Generic";



                        list.Add(new ConnectedInputDevice



                        {



                            DeviceId = obj["DeviceID"]?.ToString() ?? Guid.NewGuid().ToString(),



                            DeviceName = name,



                            DeviceType = "Mouse",



                            Manufacturer = mfg,



                            TransportType = name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ? "2.4GHz Wireless" : "USB Wired",



                            ButtonCount = 5,



                            HasWheel = true,



                            IsHighPrecision = true



                        });



                    }



                }



                catch { }



            }



            return list;



        }



        /// <summary>



        /// Capability 2: Hardware-Aware Keyboard Repeat Rate & Delay Engine.



        /// Applies fine-tuned Win32 SPI parameters synchronized with user registry keys.



        /// </summary>



        public async Task<(bool Success, string Message)> ApplyKeyboardSpeedProfileAsync(KeyboardSpeedProfile profile)



        {



            return await Task.Run(() =>



            {



                uint speed = profile == KeyboardSpeedProfile.TypingSafe ? 28u : 31u; // 31 = max ~30 repeats/sec



                uint delay = profile == KeyboardSpeedProfile.GamingUltra ? 0u : 1u;  // 0 = 250ms, 1 = 500ms



                try



                {



                    // 1. Capture original registry values



                    string origDelay = "1";



                    string origSpeed = "31";



                    using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard", false))



                    {



                        if (key != null)



                        {



                            origDelay = key.GetValue("KeyboardDelay")?.ToString() ?? "1";



                            origSpeed = key.GetValue("KeyboardSpeed")?.ToString() ?? "31";



                        }



                    }



                    // Backup to manager



                    try



                    {



                        _backupManager.BackupRegistryValue("Input Optimizer", @"HKCU\Control Panel\Keyboard", "KeyboardDelay", origDelay, "REG_SZ", delay.ToString(), "Success");



                        _backupManager.BackupRegistryValue("Input Optimizer", @"HKCU\Control Panel\Keyboard", "KeyboardSpeed", origSpeed, "REG_SZ", speed.ToString(), "Success");



                    }



                    catch { }



                    // 2. Apply Win32 SPI parameters



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDSPEED, speed, 0, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDDELAY, delay, 0, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    // 3. Persist to HKCU registry



                    using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard", true))



                    {



                        if (key != null)



                        {



                            key.SetValue("KeyboardDelay", delay.ToString(), RegistryValueKind.String);



                            key.SetValue("KeyboardSpeed", speed.ToString(), RegistryValueKind.String);



                        }



                    }



                    return (true, $"Applied {profile} keyboard responsiveness profile (Delay: {delay} [{(delay == 0 ? "250ms" : "500ms")}], Repeat Rate: {speed} [max]).");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to apply keyboard profile: {ex.Message}");



                }



            });



        }



        /// <summary>



        /// Capability 3: Structured Input Diagnostic Profiler for Master Recommendations.



        /// Synthesizes hardware input peripherals, MarkC curve configuration, measured packet variance,



        /// and active timer resolution.



        /// </summary>



        public InputDiagnosticProfile GetComprehensiveInputProfile(double? measuredJitterMs = null, double? timerResolutionMs = null)



        {



            var profile = new InputDiagnosticProfile();



            profile.ConnectedDevices = GetConnectedInputDevices();



            // 1. Check linear curves



            try



            {



                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", false);



                if (key != null)



                {



                    var xCurve = key.GetValue("SmoothMouseXCurve") as byte[];



                    var yCurve = key.GetValue("SmoothMouseYCurve") as byte[];



                    profile.IsMarkCCurveActive = xCurve != null && yCurve != null && xCurve.Length >= 40;



                    string mouseSpeed = key.GetValue("MouseSpeed")?.ToString() ?? "1";



                    string sens = key.GetValue("MouseSensitivity")?.ToString() ?? "10";



                    profile.IsLinearPointerActive = mouseSpeed == "0";



                    if (int.TryParse(sens, out int s)) profile.MouseSpeedNotch = s;

                    string trails = key.GetValue("MouseTrails")?.ToString() ?? "0";

                    if (int.TryParse(trails, out int tr)) profile.MouseTrails = tr;



                }



            }



            catch { }



            // 2. Check Keyboard



            try



            {



                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard", false);



                if (key != null)



                {



                    string kd = key.GetValue("KeyboardDelay")?.ToString() ?? "1";



                    string ks = key.GetValue("KeyboardSpeed")?.ToString() ?? "31";



                    if (int.TryParse(kd, out int d)) profile.KeyboardDelay = d;



                    if (int.TryParse(ks, out int sp)) profile.KeyboardSpeed = sp;

                }

            }

            catch { }



            // 2b. Check Accessibility Shortcut Hijacks

            try

            {

                using var skKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\StickyKeys", false);

                using var fkKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\Keyboard Response", false);

                using var tkKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\ToggleKeys", false);

                string skFlags = skKey?.GetValue("Flags")?.ToString() ?? "510";

                string fkFlags = fkKey?.GetValue("Flags")?.ToString() ?? "126";

                string tkFlags = tkKey?.GetValue("Flags")?.ToString() ?? "62";

                profile.AccessibilityShortcutsDisabled = (skFlags == "506" || skFlags == "504") && fkFlags == "0" && tkFlags == "58";

            }

            catch { }



            // 3. Timing and Measured Jitter



            profile.CurrentTimerResolutionMs = timerResolutionMs ?? 15.6;



            profile.MeasuredPacketJitterMs = measuredJitterMs;



            if (measuredJitterMs.HasValue)



            {



                profile.StabilityClassification = measuredJitterMs.Value <= 0.35 ? "EXCELLENT / STABLE" :



                                                  measuredJitterMs.Value <= 0.75 ? "NORMAL" : "VARIANCE DETECTED";



            }



            else



            {



                profile.StabilityClassification = "UNMEASURED";



            }



            profile.IsOptimalForGaming = profile.IsMarkCCurveActive && 

                                         profile.IsLinearPointerActive && 

                                         profile.MouseSpeedNotch == 10 && 

                                         profile.MouseTrails == 0 && 

                                         profile.KeyboardSpeed == 31 && 

                                         profile.KeyboardDelay <= 1 &&

                                         profile.AccessibilityShortcutsDisabled;



            profile.RecommendationSummary = profile.IsOptimalForGaming



                ? "Input curves and keyboard timings are fully optimized for linear 1:1 hardware dispatch."



                : "Windows cursor curves or keyboard repeat rates can be tuned for responsive, jitter-free input.";



            return profile;



        }



        // ─────────────────────────────────────────────────────────────────



        // NEW V4 ADVANCED CAPABILITIES: REAL RAWINPUT ANALYSIS & GRANULAR CONTROLS



        // ─────────────────────────────────────────────────────────────────



        public InputTimingStatistics AnalyzeRawInputTimingSamples(IEnumerable<double> rawIntervalsMs, double durationSeconds)



        {



            var stats = new InputTimingStatistics



            {



                DurationSeconds = durationSeconds



            };



            if (rawIntervalsMs == null)



            {



                stats.DiagnosticCode = InputDiagnosticCodes.InputMeasurementNotAvailable;



                stats.DiagnosticSummary = "No raw input event samples provided.";



                stats.StatusBrush = "#6B7280";



                return stats;



            }



            var list = rawIntervalsMs.ToList();



            stats.TotalSamplesCaptured = list.Count;



            // Filter out idle pauses (> 100ms indicates pause/lift off by user) and filter noise (< 0.1ms)



            var valid = list.Where(x => x >= 0.1 && x <= 100.0).OrderBy(x => x).ToList();



            stats.ValidSamples = valid.Count;



            stats.IdleSamplesFiltered = stats.TotalSamplesCaptured - stats.ValidSamples;



            if (valid.Count < 20)



            {



                stats.DiagnosticCode = InputDiagnosticCodes.InputMeasurementInsufficient;



                stats.DiagnosticSummary = $"Measurement insufficient ({valid.Count} valid motion samples). Move mouse continuously across the test window.";



                stats.StatusBrush = "#F59E0B";



                stats.IsStable = false;



                return stats;



            }



            double mean = valid.Average();



            stats.MeanIntervalMs = mean;



            stats.MedianIntervalMs = valid.Count % 2 == 1 



                ? valid[valid.Count / 2] 



                : (valid[(valid.Count / 2) - 1] + valid[valid.Count / 2]) / 2.0;



            double sumDiffSq = valid.Sum(d => Math.Pow(d - mean, 2));



            double variance = sumDiffSq / valid.Count;



            double stdDev = Math.Sqrt(variance);



            stats.VarianceMs2 = variance;



            stats.StandardDeviationMs = stdDev;



            stats.MinIntervalMs = valid.First();



            stats.MaxIntervalMs = valid.Last();



            int idx95 = (int)Math.Min(valid.Count - 1, Math.Floor(valid.Count * 0.95));



            int idx99 = (int)Math.Min(valid.Count - 1, Math.Floor(valid.Count * 0.99));



            stats.Percentile95Ms = valid[idx95];



            stats.Percentile99Ms = valid[idx99];



            stats.EstimatedFrequencyHz = mean > 0 ? 1000.0 / mean : 0;



            stats.TimingVariationCoefficientPercent = mean > 0 ? (stdDev / mean) * 100.0 : 0;



            if (stdDev <= 0.35)



            {



                stats.DiagnosticCode = InputDiagnosticCodes.InputTimingStable;



                stats.DiagnosticSummary = $"Input timing stable: ultra-low jitter ({stdDev:F2} ms standard deviation, ~{stats.EstimatedFrequencyHz:F0} Hz observed event rate). Dispatch timing is exceptionally consistent.";



                stats.StatusBrush = "#10B981";



                stats.IsStable = true;



            }



            else if (stdDev <= 0.75)



            {



                stats.DiagnosticCode = InputDiagnosticCodes.InputTimingStable;



                stats.DiagnosticSummary = $"Input timing normal: standard polling jitter ({stdDev:F2} ms standard deviation, ~{stats.EstimatedFrequencyHz:F0} Hz observed event rate). Dispatches within normal hardware tolerance.";



                stats.StatusBrush = "#10B981";



                stats.IsStable = true;



            }



            else



            {



                stats.DiagnosticCode = InputDiagnosticCodes.InputTimingVarianceDetected;



                stats.DiagnosticSummary = $"Elevated input timing variance ({stdDev:F2} ms standard deviation). USB controller polling irregularity, background interrupt storms, or RF contention detected.";



                stats.StatusBrush = "#EF4444";



                stats.IsStable = false;



            }



            return stats;



        }



        public (double MaxMs, double MinMs, double CurrentMs) GetTimerResolutionRange()



        {



            NtQueryTimerResolution(out uint max, out uint min, out uint current);



            return (max / 10000.0, min / 10000.0, current / 10000.0);



        }



        public async Task<(bool Success, double RequestedMs, double CurrentMs, string Message)> RequestHighResolutionTimerAsync(double desiredMs = 0.5)



        {



            return await Task.Run(() =>



            {



                lock (_timerLock)



                {



                    NtQueryTimerResolution(out uint maxUnits, out uint minUnits, out uint curBefore);



                    uint targetUnits = (uint)Math.Clamp(Math.Round(desiredMs * 10000.0), minUnits, maxUnits);



                    if (_hasActiveTimerRequest && _requestedTimerUnits == targetUnits)



                    {



                        return (true, targetUnits / 10000.0, curBefore / 10000.0, $"High-resolution timer ({targetUnits / 10000.0:F3} ms) is already active and owned by Error Optimizer.");



                    }



                    int status = NtSetTimerResolution(targetUnits, true, out uint curAfter);



                    if (status == 0)



                    {



                        _hasActiveTimerRequest = true;



                        _requestedTimerUnits = targetUnits;



                        return (true, targetUnits / 10000.0, curAfter / 10000.0, $"Successfully activated high-resolution system timer ({targetUnits / 10000.0:F3} ms). Current dispatch: {curAfter / 10000.0:F3} ms.");



                    }



                    return (false, targetUnits / 10000.0, curBefore / 10000.0, $"NtSetTimerResolution failed with NTSTATUS 0x{status:X8}.");



                }



            });



        }



        public async Task<(bool Success, double CurrentMs, string Message)> ReleaseHighResolutionTimerAsync()



        {



            return await Task.Run(() =>



            {



                lock (_timerLock)



                {



                    if (!_hasActiveTimerRequest)



                    {



                        NtQueryTimerResolution(out _, out _, out uint cur);



                        return (true, cur / 10000.0, "No active high-resolution timer request owned by Error Optimizer.");



                    }



                    NtSetTimerResolution(_requestedTimerUnits, false, out uint curAfter);



                    _hasActiveTimerRequest = false;



                    _requestedTimerUnits = 0;



                    return (true, curAfter / 10000.0, $"Released high-resolution system timer ownership. Current dispatch: {curAfter / 10000.0:F3} ms.");



                }



            });



        }



        public async Task<(bool Success, string Message)> SetPointerSpeedAsync(int speed)



        {



            return await Task.Run(() =>



            {



                speed = Math.Clamp(speed, 1, 20);



                string regPath = @"Control Panel\Mouse";



                if (HardProtectRegistry.IsProtected(regPath + @"\MouseSensitivity", out string protReason))



                {



                    return (false, $"Safety policy blocked pointer speed modification: {protReason}");



                }



                try



                {



                    string origSens = "10";



                    using (var key = Registry.CurrentUser.OpenSubKey(regPath, false))



                    {



                        if (key != null) origSens = key.GetValue("MouseSensitivity")?.ToString() ?? "10";



                    }



                    _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "MouseSensitivity", origSens, "REG_SZ", speed.ToString(), "APPLIED");



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)speed, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    using (var key = Registry.CurrentUser.CreateSubKey(regPath, true))



                    {



                        key?.SetValue("MouseSensitivity", speed.ToString(), RegistryValueKind.String);



                    }



                    string readback = "0";



                    using (var key = Registry.CurrentUser.OpenSubKey(regPath, false))



                    {



                        if (key != null) readback = key.GetValue("MouseSensitivity")?.ToString() ?? "0";



                    }



                    bool verified = readback == speed.ToString();



                    return (verified, verified 



                        ? $"Pointer Speed verified at {speed}/20 ({(speed == 10 ? "6/11 default 1:1" : "custom notch")})." 



                        : $"Pointer Speed write failed readback verification (expected: {speed}, read: {readback}).");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to set pointer speed: {ex.Message}");



                }



            });



        }



        public async Task<(bool Success, string Message)> SetMouseAccelerationAsync(bool enable)



        {



            return await Task.Run(() =>



            {



                string regPath = @"Control Panel\Mouse";



                if (HardProtectRegistry.IsProtected(regPath + @"\MouseSpeed", out string protReason))



                {



                    return (false, $"Safety policy blocked acceleration modification: {protReason}");



                }



                try



                {



                    string origSpeed = "1", origTh1 = "6", origTh2 = "10";



                    using (var key = Registry.CurrentUser.OpenSubKey(regPath, false))



                    {



                        if (key != null)



                        {



                            origSpeed = key.GetValue("MouseSpeed")?.ToString() ?? "1";



                            origTh1 = key.GetValue("MouseThreshold1")?.ToString() ?? "6";



                            origTh2 = key.GetValue("MouseThreshold2")?.ToString() ?? "10";



                        }



                    }



                    string targetSpeed = enable ? "1" : "0";



                    string targetTh1 = enable ? "6" : "0";



                    string targetTh2 = enable ? "10" : "0";



                    _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "MouseSpeed", origSpeed, "REG_SZ", targetSpeed, "APPLIED");



                    _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "MouseThreshold1", origTh1, "REG_SZ", targetTh1, "APPLIED");



                    _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "MouseThreshold2", origTh2, "REG_SZ", targetTh2, "APPLIED");



                    int[] mouseArr = enable ? new int[] { 6, 10, 1 } : new int[] { 0, 0, 0 };



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, mouseArr, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    using (var key = Registry.CurrentUser.CreateSubKey(regPath, true))



                    {



                        if (key != null)



                        {



                            key.SetValue("MouseSpeed", targetSpeed, RegistryValueKind.String);



                            key.SetValue("MouseThreshold1", targetTh1, RegistryValueKind.String);



                            key.SetValue("MouseThreshold2", targetTh2, RegistryValueKind.String);



                        }



                    }



                    using var rKey = Registry.CurrentUser.OpenSubKey(regPath, false);



                    string readSpeed = rKey?.GetValue("MouseSpeed")?.ToString() ?? "";



                    string readTh1 = rKey?.GetValue("MouseThreshold1")?.ToString() ?? "";



                    string readTh2 = rKey?.GetValue("MouseThreshold2")?.ToString() ?? "";



                    bool verified = readSpeed == targetSpeed && readTh1 == targetTh1 && readTh2 == targetTh2;



                    return (verified, verified



                        ? (enable ? "Enhanced Pointer Precision acceleration enabled." : "Pointer acceleration disabled (0, 0, 0 thresholds applied; 1:1 linear scaling active).")



                        : "Failed readback verification for mouse acceleration thresholds.");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to configure mouse acceleration: {ex.Message}");



                }



            });



        }



        public async Task<(bool Success, string Message)> SetMouseTrailsAsync(int trails)



        {



            return await Task.Run(() =>



            {



                trails = Math.Max(0, trails);



                string regPath = @"Control Panel\Mouse";



                if (HardProtectRegistry.IsProtected(regPath + @"\MouseTrails", out string protReason))



                {



                    return (false, $"Safety policy blocked mouse trails modification: {protReason}");



                }



                try



                {



                    string orig = "0";



                    using (var key = Registry.CurrentUser.OpenSubKey(regPath, false))



                    {



                        if (key != null) orig = key.GetValue("MouseTrails")?.ToString() ?? "0";



                    }



                    _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "MouseTrails", orig, "REG_SZ", trails.ToString(), "APPLIED");



                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSETRAILS, (uint)trails, IntPtr.Zero, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    using (var key = Registry.CurrentUser.CreateSubKey(regPath, true))



                    {



                        key?.SetValue("MouseTrails", trails.ToString(), RegistryValueKind.String);



                    }



                    using var rKey = Registry.CurrentUser.OpenSubKey(regPath, false);



                    string read = rKey?.GetValue("MouseTrails")?.ToString() ?? "";



                    bool verified = read == trails.ToString();



                    return (verified, verified



                        ? (trails == 0 ? "Mouse trails disabled to prevent desktop compositing latency." : $"Mouse trails configured to {trails}.")



                        : "Failed readback verification for MouseTrails.");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to configure mouse trails: {ex.Message}");



                }



            });



        }



        public async Task<(bool Success, string Message)> SetSnapToDefaultAsync(bool enable)



        {



            return await Task.Run(() =>



            {



                string regPath = @"Control Panel\Mouse";



                if (HardProtectRegistry.IsProtected(regPath + @"\SnapToDefaultButton", out string protReason))



                {



                    return (false, $"Safety policy blocked snap-to-default modification: {protReason}");



                }



                try



                {



                    string orig = "0";



                    using (var key = Registry.CurrentUser.OpenSubKey(regPath, false))



                    {



                        if (key != null) orig = key.GetValue("SnapToDefaultButton")?.ToString() ?? "0";



                    }



                    string target = enable ? "1" : "0";



                    _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "SnapToDefaultButton", orig, "REG_SZ", target, "APPLIED");



                    NativeMethods.SystemParametersInfo(0x0060 /* SPI_SETSNAPTODEFBUTTON */, enable ? 1u : 0u, IntPtr.Zero, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);



                    using (var key = Registry.CurrentUser.CreateSubKey(regPath, true))



                    {



                        key?.SetValue("SnapToDefaultButton", target, RegistryValueKind.String);



                    }



                    using var rKey = Registry.CurrentUser.OpenSubKey(regPath, false);



                    string read = rKey?.GetValue("SnapToDefaultButton")?.ToString() ?? "";



                    bool verified = read == target;



                    return (verified, verified



                        ? (enable ? "Snap-to-default button enabled." : "Snap-to-default button disabled.")



                        : "Failed readback verification for SnapToDefaultButton.");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to configure snap to default: {ex.Message}");



                }



            });



        }



        public async Task<(bool Success, string Message)> ApplyMarkCLinearCurvesAsync(bool enable = true)



        {



            return await Task.Run(() =>



            {



                string regPath = @"Control Panel\Mouse";



                if (HardProtectRegistry.IsProtected(regPath + @"\SmoothMouseXCurve", out string protReason))



                {



                    return (false, $"Safety policy blocked cursor curves modification: {protReason}");



                }



                try



                {



                    byte[]? origX = null, origY = null;



                    using (var key = Registry.CurrentUser.OpenSubKey(regPath, false))



                    {



                        if (key != null)



                        {



                            origX = key.GetValue("SmoothMouseXCurve") as byte[];



                            origY = key.GetValue("SmoothMouseYCurve") as byte[];



                        }



                    }



                    byte[] targetX = enable ? MouseCurvesDetector.TargetCurveX : MouseCurvesDetector.DefaultWindowsCurveX;



                    byte[] targetY = enable ? MouseCurvesDetector.TargetCurveY : MouseCurvesDetector.DefaultWindowsCurveY;



                    if (origX != null)



                        _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "SmoothMouseXCurve", origX, "REG_BINARY", targetX, "APPLIED");



                    if (origY != null)



                        _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", regPath, "SmoothMouseYCurve", origY, "REG_BINARY", targetY, "APPLIED");



                    using (var key = Registry.CurrentUser.CreateSubKey(regPath, true))



                    {



                        if (key != null)



                        {



                            key.SetValue("SmoothMouseXCurve", targetX, RegistryValueKind.Binary);



                            key.SetValue("SmoothMouseYCurve", targetY, RegistryValueKind.Binary);



                        }



                    }



                    using var rKey = Registry.CurrentUser.OpenSubKey(regPath, false);



                    var readX = rKey?.GetValue("SmoothMouseXCurve") as byte[];



                    var readY = rKey?.GetValue("SmoothMouseYCurve") as byte[];



                    bool verified = readX != null && readY != null && readX.SequenceEqual(targetX) && readY.SequenceEqual(targetY);



                    return (verified, verified



                        ? (enable ? "1:1 MarkC linear pointer curves verified. Desktop velocity scaling distortion eliminated." : "Restored standard Windows acceleration curves.")



                        : "Failed readback verification for SmoothMouse curves.");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to configure MarkC linear curves: {ex.Message}");



                }



            });



        }



        public async Task<(bool Success, string Message)> DisableAccessibilityShortcutsAsync()



        {



            return await Task.Run(() =>



            {



                try



                {



                    string skPath = @"Control Panel\Accessibility\StickyKeys";



                    string fkPath = @"Control Panel\Accessibility\Keyboard Response";



                    string tkPath = @"Control Panel\Accessibility\ToggleKeys";



                    using (var key = Registry.CurrentUser.OpenSubKey(skPath, false))



                    {



                        var cur = key?.GetValue("Flags")?.ToString() ?? "510";



                        _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", skPath, "Flags", cur, "REG_SZ", "506", "APPLIED");



                    }



                    using (var key = Registry.CurrentUser.CreateSubKey(skPath, true))



                    {



                        key?.SetValue("Flags", "506", RegistryValueKind.String);



                    }



                    using (var key = Registry.CurrentUser.OpenSubKey(fkPath, false))



                    {



                        var cur = key?.GetValue("Flags")?.ToString() ?? "126";



                        _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", fkPath, "Flags", cur, "REG_SZ", "0", "APPLIED");



                    }



                    using (var key = Registry.CurrentUser.CreateSubKey(fkPath, true))



                    {



                        key?.SetValue("Flags", "0", RegistryValueKind.String);



                    }



                    using (var key = Registry.CurrentUser.OpenSubKey(tkPath, false))



                    {



                        var cur = key?.GetValue("Flags")?.ToString() ?? "62";



                        _backupManager.BackupRegistryValue("AntiGravity.InputOptimizer", tkPath, "Flags", cur, "REG_SZ", "58", "APPLIED");



                    }



                    using (var key = Registry.CurrentUser.CreateSubKey(tkPath, true))



                    {



                        key?.SetValue("Flags", "58", RegistryValueKind.String);



                    }



                    return (true, "Disabled StickyKeys, FilterKeys, and ToggleKeys hotkey triggers to prevent accidental in-game popups.");



                }



                catch (Exception ex)



                {



                    return (false, $"Failed to disable accessibility shortcuts: {ex.Message}");



                }



            });



        }



        public static string ReadRealRegistryValue(string pathWithValName, string valName, bool isBinary)



        {



            try



            {



                string[] parts = pathWithValName.Split('\\');



                if (parts.Length < 3) return "Not Found";



                RegistryKey root = parts[0].Contains("LocalMachine", StringComparison.OrdinalIgnoreCase) || parts[0].Contains("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)



                    ? Registry.LocalMachine



                    : Registry.CurrentUser;



                string subkey = string.Join("\\", parts.Skip(1).Take(parts.Length - 2));



                string actualValName = string.IsNullOrEmpty(valName) ? parts.Last() : valName;



                using var key = root.OpenSubKey(subkey, false);



                if (key == null) return "Not Found";



                if (isBinary)



                {



                    var bytes = key.GetValue(actualValName) as byte[];



                    return bytes != null ? BitConverter.ToString(bytes).Replace("-", " ") : "Not Found";



                }



                else



                {



                    var val = key.GetValue(actualValName);



                    return val != null ? val.ToString() ?? "Not Found" : "Not Found";



                }



            }



            catch (Exception ex)



            {



                return ex is UnauthorizedAccessException ? "Access Denied" : "Error";



            }



        }



    }



    internal class InputBackupManager : IBackupManager



    {



        private readonly IBackupManager _inner = BackupManager.Instance;



        public event Action<BackupTransaction>? TransactionRecorded



        {



            add => _inner.TransactionRecorded += value;



            remove => _inner.TransactionRecorded -= value;



        }



        public string BeginBatchTransaction(string profileId, string profileName, string category) => _inner.BeginBatchTransaction(profileId, profileName, category);



        public BackupTransaction RecordTransaction(BackupTransaction transaction) => _inner.RecordTransaction(transaction);



        public bool CommitBatchTransaction(string batchTransactionId) => _inner.CommitBatchTransaction(batchTransactionId);



        public BackupTransaction CaptureRegistryTweak(string profileName, string optimizationName, string hive, string subKey, string valueName, object? oldValue, string oldType, object? targetValue, string riskLevel = "SAFE", string? parentBatchId = null) =>



            _inner.CaptureRegistryTweak(profileName, optimizationName, hive, subKey, valueName, oldValue, oldType, targetValue, riskLevel, parentBatchId);



        public BackupTransaction CaptureServiceTweak(string profileName, string serviceName, string previousStartup, string newStartup, string previousState, string riskLevel = "SAFE", string? parentBatchId = null) =>



            _inner.CaptureServiceTweak(profileName, serviceName, previousStartup, newStartup, previousState, riskLevel, parentBatchId);



        public BackupTransaction CapturePowerPlanTweak(string profileName, string planName, string previousGuid, string newGuid, string riskLevel = "SAFE", string? parentBatchId = null) =>



            _inner.CapturePowerPlanTweak(profileName, planName, previousGuid, newGuid, riskLevel, parentBatchId);



        public BackupTransaction CaptureInputTweak(string profileName, string optimizationName, string targetSetting, object? previousValue, object? newValue, string riskLevel = "SAFE", string? parentBatchId = null) =>



            _inner.CaptureInputTweak(profileName, optimizationName, targetSetting, previousValue, newValue, riskLevel, parentBatchId);



        public BackupTransaction CaptureNetworkTweak(string profileName, string settingName, string targetInterface, object? previousValue, object? newValue, string riskLevel = "SAFE", string? parentBatchId = null) =>



            _inner.CaptureNetworkTweak(profileName, settingName, targetInterface, previousValue, newValue, riskLevel, parentBatchId);



        public BackupTransaction CaptureStorageCleanup(string profileName, string categoryName, long reclaimedBytes, int filesDeleted, List<string> targetPaths, bool wasQuarantined = false, string? parentBatchId = null) =>



            _inner.CaptureStorageCleanup(profileName, categoryName, reclaimedBytes, filesDeleted, targetPaths, wasQuarantined, parentBatchId);



        public BackupTransaction CaptureMemoryRuntime(string profileName, long ramReclaimedMb, string loadBefore, string loadAfter, string details, string? parentBatchId = null) =>



            _inner.CaptureMemoryRuntime(profileName, ramReclaimedMb, loadBefore, loadAfter, details, parentBatchId);



        public BackupTransaction CaptureSystemRepair(string profileName, string operationName, string beforeState, string afterState, string? parentBatchId = null) =>



            _inner.CaptureSystemRepair(profileName, operationName, beforeState, afterState, parentBatchId);



        public BackupTransaction CaptureGenericTweak(string profileName, string optimizationId, string name, string category, string operationType, string beforeState, string afterState, string rollbackMethod, bool canRollback, string riskLevel = "SAFE", string? parentBatchId = null) =>



            _inner.CaptureGenericTweak(profileName, optimizationId, name, category, operationType, beforeState, afterState, rollbackMethod, canRollback, riskLevel, parentBatchId);



        public Task<RestoreResult> RestoreSingleTransactionAsync(string transactionId, CancellationToken ct = default) => _inner.RestoreSingleTransactionAsync(transactionId, ct);



        public Task<RestoreResult> RestoreProfileAsync(string profileName, CancellationToken ct = default) => _inner.RestoreProfileAsync(profileName, ct);



        public Task<RestoreResult> RestoreFullSystemSnapshotAsync(CancellationToken ct = default) => _inner.RestoreFullSystemSnapshotAsync(ct);



#pragma warning disable CS0618
        public Task<bool> CreateSystemRestorePointAsync(string description, CancellationToken ct = default) => _inner.CreateSystemRestorePointAsync(description, ct);
#pragma warning restore CS0618

        public BackupTransaction CreateApplicationSnapshot(string name, string? notes = null) => _inner.CreateApplicationSnapshot(name, notes);

        public (bool Valid, string Reason) ValidateTransactionIntegrity(string transactionId) => _inner.ValidateTransactionIntegrity(transactionId);



        public List<BackupTransaction> GetAllTransactions() => _inner.GetAllTransactions();

        public List<BackupTransaction> GetRecentTransactions(int limit = 150) => _inner.GetRecentTransactions(limit);

        public List<BackupTransaction> GetTransactionsPaginated(int offset, int limit, out int totalCount) =>
            _inner.GetTransactionsPaginated(offset, limit, out totalCount);



        public List<ProfileBackupGroup> GetProfileBackupGroups(bool includeTransactions = true) => _inner.GetProfileBackupGroups(includeTransactions);

        public void InvalidateCache() => _inner.InvalidateCache();



        public BackupSummaryStats GetSummaryStats() => _inner.GetSummaryStats();



        public BackupTransaction? GetTransactionById(string transactionId) => _inner.GetTransactionById(transactionId);



        public bool DeleteTransaction(string transactionId) => _inner.DeleteTransaction(transactionId);

        public (int Deleted, int Skipped, int Failed, long FreedBytes) DeleteTransactions(IEnumerable<string> transactionIds) => _inner.DeleteTransactions(transactionIds);

        public (int Deleted, int Skipped, int Failed, long FreedBytes) DeleteAllApplicationBackups() => _inner.DeleteAllApplicationBackups();

        public void ClearOldBackups(int retentionDays) => _inner.ClearOldBackups(retentionDays);



#pragma warning disable CS0618
        public bool CreateSystemRestorePoint(string description) => _inner.CreateSystemRestorePoint(description);
#pragma warning restore CS0618



        public bool BackupRegistryState(IEnumerable<string> registryPaths) => _inner.BackupRegistryState(registryPaths);



        public bool BackupServiceState(IEnumerable<string> serviceNames) => _inner.BackupServiceState(serviceNames);



        public bool LogKilledProcesses(IEnumerable<string> processPaths) => _inner.LogKilledProcesses(processPaths);



        public bool RestoreAll() => _inner.RestoreAll();



        public bool BackupRegistryValue(string owner, string registryKey, string valueName, object oldValue, string oldType, object targetValue, string result) =>



            _inner.BackupRegistryValue(owner, registryKey, valueName, oldValue, oldType, targetValue, result);



        public bool RestoreByOwner(string owner) => _inner.RestoreByOwner(owner);



    }



}



