using Perga.AgentKit;
using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static AgentSessionDto ApplyContextUsage(
        AgentSessionDto session, TokenUsage usage, long atMs, bool invalidated) => session with
    {
        ContextUsedTokens = usage.EffectiveUsedTokens,
        ContextWindowTokens = usage.ContextWindowTokens,
        ContextUsageAtMs = atMs,
        ContextUsageRatio = usage.ContextWindowTokens is > 0
            ? Math.Clamp((double)usage.EffectiveUsedTokens / usage.ContextWindowTokens.Value, 0.0, 1.0)
            : 0.0,
        ContextUsageInvalidated = invalidated,
        UpdatedAtMs = atMs
    };
}
