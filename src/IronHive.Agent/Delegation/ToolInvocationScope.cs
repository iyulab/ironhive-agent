using Microsoft.Extensions.AI;

namespace IronHive.Agent.Delegation;

/// <summary>
/// The conversation a tool is being invoked from, for tool loops that are not
/// <see cref="FunctionInvokingChatClient"/> (whose <see cref="FunctionInvokingChatClient.CurrentContext"/> carries it
/// already). A loop of its own (the Ironbees adapter in <c>IronHive.Agent.Ironbees</c>, or a host's) sets this
/// around each call, so a tool that needs the conversation — the advisor — works under either loop.
/// </summary>
public static class ToolInvocationScope
{
    private static readonly AsyncLocal<IReadOnlyList<ChatMessage>?> Current = new();

    /// <summary>The conversation of the tool call in progress on this async flow; null outside a scope.</summary>
    public static IReadOnlyList<ChatMessage>? Messages => Current.Value;

    /// <summary>Sets <paramref name="messages"/> as the current conversation until the returned scope is disposed.</summary>
    public static Scope Enter(IReadOnlyList<ChatMessage> messages)
    {
        var previous = Current.Value;
        Current.Value = messages;
        return new Scope(previous);
    }

    /// <summary>Restores the previous conversation when disposed.</summary>
    public readonly struct Scope(IReadOnlyList<ChatMessage>? previous) : IDisposable
    {
        /// <summary>Restores the conversation that was current before <see cref="Enter"/>.</summary>
        public void Dispose() => Current.Value = previous;
    }
}
