namespace IronHive.Agent.Delegation;

/// <summary>
/// The delegated agent whose run is in progress on this async flow. A delegation tool (<c>DelegationTools</c> in
/// <c>IronHive.Agent.Ironbees</c>, or a host's own) enters it around the delegated run, so code on the run's tool path — the
/// approval gate, which names the agent in <see cref="Mode.ApprovalRequest.DelegatedBy"/> — knows who asked.
/// </summary>
/// <remarks>Nested delegation reports the innermost agent; disposing a scope restores the one around it.</remarks>
public static class DelegationScope
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The name of the innermost delegated agent running on this async flow; null outside a delegated run.</summary>
    public static string? AgentName => Current.Value;

    /// <summary>Sets <paramref name="agentName"/> as the current delegated agent until the returned scope is disposed.</summary>
    public static Scope Enter(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        var previous = Current.Value;
        Current.Value = agentName;
        return new Scope(previous);
    }

    /// <summary>Restores the previous delegated agent when disposed.</summary>
    public readonly struct Scope(string? previous) : IDisposable
    {
        /// <summary>Restores the agent that was current before <see cref="Enter"/>.</summary>
        public void Dispose() => Current.Value = previous;
    }
}
