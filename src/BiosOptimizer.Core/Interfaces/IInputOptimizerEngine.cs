using System.Collections.Generic;
using System.Threading.Tasks;

namespace BiosOptimizer.Core.Interfaces
{
    public enum InputVerificationMode
    {
        ImmediateReadback,
        PostRebootReadback,
        PostDeviceReloadReadback,
        PostLogoffReadback
    }

    public static class InputDiagnosticCodes
    {
        public const string InputConfigurationVerified = "INPUT_CONFIGURATION_VERIFIED";
        public const string InputTimingStable = "INPUT_TIMING_STABLE";
        public const string InputTimingVarianceDetected = "INPUT_TIMING_VARIANCE_DETECTED";
        public const string InputMeasurementNotAvailable = "INPUT_MEASUREMENT_NOT_AVAILABLE";
        public const string InputMeasurementInsufficient = "INPUT_MEASUREMENT_INSUFFICIENT";
        public const string PointerAccelerationDetected = "POINTER_ACCELERATION_DETECTED";
        public const string DeviceInformationIncomplete = "DEVICE_INFORMATION_INCOMPLETE";
        public const string ManualGameConfigurationRequired = "MANUAL_GAME_CONFIGURATION_REQUIRED";
        public const string InputOptimizationUnsupported = "INPUT_OPTIMIZATION_UNSUPPORTED";
    }

    public class InputTimingStatistics
    {
        public int TotalSamplesCaptured { get; set; }
        public int ValidSamples { get; set; }
        public int IdleSamplesFiltered { get; set; }
        public double DurationSeconds { get; set; }
        public double MeanIntervalMs { get; set; }
        public double MedianIntervalMs { get; set; }
        public double StandardDeviationMs { get; set; } // Jitter
        public double VarianceMs2 { get; set; }
        public double Percentile95Ms { get; set; }
        public double Percentile99Ms { get; set; }
        public double MinIntervalMs { get; set; }
        public double MaxIntervalMs { get; set; }
        public double EstimatedFrequencyHz { get; set; }
        public double TimingVariationCoefficientPercent { get; set; }
        public string DiagnosticCode { get; set; } = InputDiagnosticCodes.InputMeasurementNotAvailable;
        public string DiagnosticSummary { get; set; } = string.Empty;
        public string StatusBrush { get; set; } = "#6B7280";
        public bool IsStable { get; set; }
    }

    public class InputOptimizationAction
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Risk { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public bool Applicable { get; set; }
        public bool AlreadyOptimized { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string TechnicalLocation { get; set; } = string.Empty;
        public bool RequiresReboot { get; set; } = false;
        public bool RequiresDeviceReload { get; set; } = false;
        public bool RequiresLogoff { get; set; } = false;
        public InputVerificationMode VerificationMode { get; set; } = InputVerificationMode.ImmediateReadback;
        
        // Advanced Execution States
        public string SupportState { get; set; } = "UNKNOWN"; // SUPPORTED, NOT AVAILABLE, UNSUPPORTED, FAILED
        public string ExecutionState { get; set; } = "IDLE";
        public string VerificationState { get; set; } = "UNVERIFIED";
        public string RollbackInformation { get; set; } = string.Empty;

        // General status string mapped by IPC for GUI backwards-compat
        public string Status { get; set; } = string.Empty;
    }

    public class InputOptimizationPlan
    {
        public List<InputOptimizationAction> Actions { get; set; } = new List<InputOptimizationAction>();
        public List<InputOptimizationAction> ExcludedActions { get; set; } = new List<InputOptimizationAction>();
        public int TotalCatalogCount { get; set; }
        public int EligibleCount { get; set; }
        public int AlreadyOptimizedCount { get; set; }
        public int IneligibleCount { get; set; }
        public List<ConnectedInputDevice> Devices { get; set; } = new List<ConnectedInputDevice>();
        public double? TimerResolutionMs { get; set; }
        public int MouseTunedCount { get; set; }
        public int KeyboardTunedCount { get; set; }
        public string MouseDeviceName { get; set; } = string.Empty;
        public string KeyboardDeviceName { get; set; } = string.Empty;
        public string MouseStatus { get; set; } = "Not detected"; // Detected / Not detected / Multiple devices
        public string KeyboardStatus { get; set; } = "Not detected"; // Detected / Not detected / Multiple devices
    }

    public class AimShakeDiagnosticResult
    {
        public string Status { get; set; } = "CONFIGURATION VERIFIED";
        public string DiagnosticCode { get; set; } = InputDiagnosticCodes.InputConfigurationVerified;
        public string Summary { get; set; } = string.Empty;
        public string StatusBrush { get; set; } = "#10B981";

        public bool LinearCurvesActive { get; set; }
        public bool AccelerationDisabled { get; set; }
        public bool SensitivityOptimal { get; set; }
        public bool PointerSpeedOptimal { get; set; }
        public bool QuantumSchedulingOptimal { get; set; }
        public bool SystemResponsivenessOptimal { get; set; }
        public bool HighResolutionTimerActive { get; set; }

        public bool IsWirelessOrBluetooth { get; set; }
        public bool HasMeasurementData { get; set; }
        public double MeasuredJitterMs { get; set; }
        public InputTimingStatistics? TimingStatistics { get; set; }

        public string HostInputAnalysis { get; set; } = string.Empty;
        public string DeviceAnalysis { get; set; } = string.Empty;
        public string ContentionAnalysis { get; set; } = string.Empty;
        public string MeasurementAnalysis { get; set; } = string.Empty;
        public string RenderingAnalysis { get; set; } = "Frame pacing and GPU render latency are application-dependent and not measurable by Windows input subsystem.";
        public string InGameCameraGuidance { get; set; } = "In-game camera smoothing, recoil patterns, and sensitivity multipliers are internal to the game engine. Enable in-game Raw Input (m_rawinput 1) to bypass desktop scaling.";

        public List<string> DetectedAnomalies { get; set; } = new List<string>();
        public List<string> VerifiedFactors { get; set; } = new List<string>();

        public string SensorNote { get; set; } = "Physical mouse sensor variance and hardware polling irregularity are independent of OS curves.";
        public string InGameNote { get; set; } = "In-game sensitivity multipliers and render frame-pacing remain application-specific.";
        public string DesktopVsRawInputNote { get; set; } = "Windows pointer speed, acceleration thresholds, and MarkC curves govern desktop cursor translation and games using WM_MOUSEMOVE or GetCursorPos. Games configured with Raw Input (m_rawinput 1) directly read hardware HID delta packets, bypassing OS curves entirely.";
        public string InGameCameraRecoilNote { get; set; } = "In-game camera recoil, weapon kick patterns, aim sway, and field-of-view sensitivity multipliers are computed by the game engine's internal physics simulator. They cannot be altered by Windows OS registry or driver tweaks.";
        public string FramePacingNote { get; set; } = "GPU frame rendering pacing, micro-stutters, and monitor refresh synchronization (G-Sync/FreeSync) govern visible motion smoothness. Inconsistent frametimes can produce visual camera jitter even when physical input polling is perfectly stable.";
    }

    public class ConnectedInputDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string DeviceType { get; set; } = "Mouse"; // Mouse, Keyboard, Touchpad, HID
        public string Manufacturer { get; set; } = "Generic";
        public string TransportType { get; set; } = "USB Wired"; // USB Wired, 2.4GHz Wireless, Bluetooth, Internal (I2C / ACPI), Internal (PS/2)
        public int ButtonCount { get; set; } = 3;
        public bool HasWheel { get; set; } = true;
        public bool IsHighPrecision { get; set; } = true;

        public string Vid { get; set; } = string.Empty;
        public string Pid { get; set; } = string.Empty;
        public string DeviceClass { get; set; } = string.Empty;
        public string DeviceInterface { get; set; } = string.Empty;
        public string RawDevicePath { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public bool IsPhysical { get; set; } = true;
        public bool IsInternal { get; set; } = false;
        public bool IsVirtual { get; set; } = false;
        public bool IsTouchpad { get; set; } = false;
    }

    public enum KeyboardSpeedProfile
    {
        GamingUltra,      // Speed 31, Delay 0 (250ms)
        BalancedGaming,   // Speed 31, Delay 1 (500ms)
        TypingSafe        // Speed 28, Delay 1 (500ms)
    }

    public class InputDiagnosticProfile
    {
        public List<ConnectedInputDevice> ConnectedDevices { get; set; } = new();
        public bool IsMarkCCurveActive { get; set; }
        public bool IsLinearPointerActive { get; set; }
        public int MouseSpeedNotch { get; set; } = 10;
        public int MouseTrails { get; set; } = 0;
        public bool AccessibilityShortcutsDisabled { get; set; }
        public int KeyboardDelay { get; set; } = 1;
        public int KeyboardSpeed { get; set; } = 31;
        public double CurrentTimerResolutionMs { get; set; } = 15.6;
        public double? MeasuredPacketJitterMs { get; set; }
        public string StabilityClassification { get; set; } = "UNMEASURED";
        public bool IsOptimalForGaming { get; set; }
        public string RecommendationSummary { get; set; } = string.Empty;
    }

    public interface IInputOptimizerEngine
    {
        Task<InputOptimizationPlan> PlanInputOptimizationAsync();
        Task<(bool Success, string Message)> ApplyInputOptimizationAsync(string riskLevel); 
        Task<(bool Success, string Message)> RestoreInputOptimizationAsync();
        void ProcessPendingReboots();
        AimShakeDiagnosticResult EvaluateAimShakeFactors();
        AimShakeDiagnosticResult EvaluateAimShakeFactors(double? measuredJitterMs, double? timerResolutionMs, bool isWirelessMouse);
        AimShakeDiagnosticResult EvaluateAimShakeFactors(InputTimingStatistics? stats, double? timerResolutionMs, bool isWirelessMouse);
        List<ConnectedInputDevice> GetConnectedInputDevices();
        Task<(bool Success, string Message)> ApplyKeyboardSpeedProfileAsync(KeyboardSpeedProfile profile);
        InputDiagnosticProfile GetComprehensiveInputProfile(double? measuredJitterMs = null, double? timerResolutionMs = null);
        InputTimingStatistics AnalyzeRawInputTimingSamples(IEnumerable<double> rawIntervalsMs, double durationSeconds);
        Task<(bool Success, string Message)> SetPointerSpeedAsync(int speed);
        Task<(bool Success, string Message)> SetMouseAccelerationAsync(bool enable);
        Task<(bool Success, string Message)> SetMouseTrailsAsync(int trails);
        Task<(bool Success, string Message)> SetSnapToDefaultAsync(bool enable);
        Task<(bool Success, string Message)> ApplyMarkCLinearCurvesAsync(bool enable = true);
        Task<(bool Success, string Message)> DisableAccessibilityShortcutsAsync();
        (double MaxMs, double MinMs, double CurrentMs) GetTimerResolutionRange();
        Task<(bool Success, double RequestedMs, double CurrentMs, string Message)> RequestHighResolutionTimerAsync(double desiredMs = 0.5);
        Task<(bool Success, double CurrentMs, string Message)> ReleaseHighResolutionTimerAsync();
        bool IsTimerResolutionActive { get; }
    }
}
