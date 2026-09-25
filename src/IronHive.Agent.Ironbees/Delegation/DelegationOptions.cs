using IronHive.Agent.Tracking;

namespace IronHive.Agent.Delegation;

/// <summary>
/// Limits and accounting shared by a set of delegation tools.
/// </summary>
public sealed record DelegationOptions
{
    /// <summary>
    /// How deep delegation may nest. A delegated agent that itself delegates counts one level deeper; a call past
    /// this depth is refused with a result the model can read. The depth follows the async call chain, so it holds
    /// across agents, not just within one tool. Default 2.
    /// </summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>How many delegated runs from this tool set may run at once; further calls wait. Default 3.</summary>
    public int MaxConcurrent { get; init; } = 3;

    /// <summary>
    /// The parent's usage limit. A delegation is refused once it is reached, and each delegated run's usage is fed
    /// into it — priced on the delegated agent's model — so delegating is not a way around the limit.
    /// </summary>
    public IUsageLimiter? UsageLimiter { get; init; }

    /// <summary>The parent's usage accounting; each delegated run's tokens are recorded into it.</summary>
    public IUsageTracker? UsageTracker { get; init; }
}
