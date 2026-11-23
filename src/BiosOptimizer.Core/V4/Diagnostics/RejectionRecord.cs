#nullable enable
using BiosOptimizer.IPC.Contracts;

namespace BiosOptimizer.Core.V4.Diagnostics
{
    public sealed record RejectionRecord(
        int Pid,
        string Name,
        string ImagePath,
        ProcessClassification FinalClass,
        string RejectionReason,
        string RejectionRule,
        string Explanation = "");
}
