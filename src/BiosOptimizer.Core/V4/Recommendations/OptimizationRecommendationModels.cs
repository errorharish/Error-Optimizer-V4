#nullable enable
using System;
using System.Collections.Generic;

namespace BiosOptimizer.Core.V4.Recommendations
{
    public enum RecommendationLifecycle
    {
        Detected = 0,
        Evaluated = 1,
        Recommended = 2,
        Selected = 3,
        Applying = 4,
        Verified = 5,
        AlreadyConfigured = 6,
        NotApplicable = 7,
        BlockedByProtectedRules = 8,
        Unsupported = 9,
        RequiresManualAction = 10,
        Failed = 11,
        Restored = 12
    }

    public enum RecommendationRisk
    {
        Safe,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Master data model for an intelligent, hardware-aware optimization recommendation.
    /// Strictly links every frontend recommendation to a real executable backend handler.
    /// </summary>
    public class OptimizationRecommendation
    {
        // ── 1. IDENTIFIERS & DOMAIN OWNERSHIP ────────────────────────────────
        public string Id { get; set; } = string.Empty;
        public OptimizationEngineDomain EngineDomain { get; set; } = OptimizationEngineDomain.Registry;
        public string EngineId => EngineDomain.ToString();
        public string OperationId { get; set; } = string.Empty;

        // ── 2. METADATA & DISPLAY ────────────────────────────────────────────
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Icon { get; set; } = "\uE945"; // MDL2 glyph

        // ── 3. REAL DETECTED SYSTEM STATE VS TARGET ──────────────────────────
        public string CurrentObservedState { get; set; } = "Unknown";
        public string TargetState { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string HardwareEvidence { get; set; } = string.Empty;

        // ── 4. BENEFIT & LIMITATIONS (HONEST TELEMETRY) ──────────────────────
        public string ExpectedBenefit { get; set; } = string.Empty;
        public string Limitations { get; set; } = string.Empty;

        // ── 5. SAFETY, RISK & PREREQUISITES ──────────────────────────────────
        public RecommendationRisk Risk { get; set; } = RecommendationRisk.Safe;
        public string RiskString => Risk.ToString().ToUpperInvariant();
        public bool RequiresAdmin { get; set; } = true;
        public bool RequiresRestart { get; set; } = false;
        public bool RollbackAvailable { get; set; } = true;

        // ── 6. VERIFICATION & EXECUTION BACKEND ──────────────────────────────
        public string VerificationMethod { get; set; } = "Readback Verification";
        public string BackendHandler { get; set; } = string.Empty;

        // ── 7. LIFECYCLE & STATE ─────────────────────────────────────────────
        public RecommendationLifecycle LifecycleState { get; set; } = RecommendationLifecycle.Detected;
        public string LifecycleStateText => LifecycleState switch
        {
            RecommendationLifecycle.Detected => "DETECTED",
            RecommendationLifecycle.Evaluated => "EVALUATED",
            RecommendationLifecycle.Recommended => "RECOMMENDED",
            RecommendationLifecycle.Selected => "SELECTED",
            RecommendationLifecycle.Applying => "APPLYING",
            RecommendationLifecycle.Verified => "VERIFIED",
            RecommendationLifecycle.AlreadyConfigured => "ALREADY CONFIGURED",
            RecommendationLifecycle.NotApplicable => "NOT APPLICABLE",
            RecommendationLifecycle.BlockedByProtectedRules => "BLOCKED BY PROTECTED RULES",
            RecommendationLifecycle.Unsupported => "UNSUPPORTED",
            RecommendationLifecycle.RequiresManualAction => "REQUIRES MANUAL ACTION",
            RecommendationLifecycle.Failed => "FAILED",
            RecommendationLifecycle.Restored => "RESTORED",
            _ => "DETECTED"
        };

        // ── 8. SELECTION & PRIORITY ──────────────────────────────────────────
        public int Priority { get; set; } = 50; // 1 to 100
        public bool IsSelected { get; set; } = true;
        public bool IsActionable => LifecycleState == RecommendationLifecycle.Recommended ||
                                   LifecycleState == RecommendationLifecycle.Selected;

        public string ExecutionStatusMessage { get; set; } = string.Empty;
        public DateTime? ExecutedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Result payload returned following real backend execution of a recommendation.
    /// </summary>
    public class OptimizationExecutionResult
    {
        public bool Success { get; set; }
        public string RecommendationId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public RecommendationLifecycle LifecycleState { get; set; } = RecommendationLifecycle.Verified;
        public bool Verified { get; set; }
        public bool RestartRequired { get; set; }
        public string BeforeState { get; set; } = string.Empty;
        public string AfterState { get; set; } = string.Empty;
        public string ErrorDetails { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
