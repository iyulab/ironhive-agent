using Microsoft.Extensions.AI;

namespace IronHive.Agent.Delegation;

/// <summary>
/// The conversation a tool is being invoked from, for tool loops that are not
/// <see cref="FunctionInvokingChatClient"/> (whose <see cref="FunctionInvokingChatClient.CurrentContext"/> carries it
/// already). <see cref="Ironbees.ChatClientFrameworkAdapter"/> runs its own loop and sets this around each call, so
/// a tool that needs the conversation — the advisor — works under either loop.
/// </summary>
internal static class ToolInvocationScope
{
    private static readonly AsyncLocal<IReadOnlyList<ChatMessage>?> Current = new();

    public static IReadOnlyList<ChatMessage>? Messages => Current.Value;

    public static Scope Enter(IReadOnlyList<ChatMessage> messages)
    {
        var previous = Current.Value;
        Current.Value = messages;
        return new Scope(previous);
    }

    public readonly struct Scope(IReadOnlyList<ChatMessage>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
