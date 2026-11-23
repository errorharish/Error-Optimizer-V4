using System;

namespace BiosOptimizer.GUI.ViewModels.Modal
{
    public enum UniversalOptimizationModalState
    {
        Closed,
        Loading,
        Cancelling,
        Cancelled,
        Success,
        Failed,
        RiskConfirmation,
        RestorePointPrompt,
        SystemProtectionPrompt
    }
}
