#nullable enable

namespace BiosOptimizer.Core.V4.Diagnostics
{
    public sealed class TierTrace
    {
        public TierPass Tier1 { get; init; } = new();
        public TierPass Tier2 { get; init; } = new();
        public TierPass Tier3 { get; init; } = new();
    }

    public sealed class TierPass
    {
        public bool Ran { get; set; }
        public string? SkipReason { get; set; }
        public int CandidatesDiscovered { get; set; }
        public int ClassifiedReducible { get; set; }
    }
}
