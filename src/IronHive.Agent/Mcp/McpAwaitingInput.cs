using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// A tool call an MCP server could not finish before a person answered, released by the elicitation handler instead of being
/// waited for: the call's exact identity and the server's own state, kept so the call can be sent again with the answer.
/// </summary>
/// <remarks>
/// The server decides how long its <see cref="RequestState"/> stays valid; sending the call again after that is judged by the
/// server and its refusal is the call's result - no time guarantee is made here.
/// </remarks>
/// <param name="PluginName">The plugin (server) the call went to.</param>
/// <param name="ToolName">The tool the call runs.</param>
/// <param name="Arguments">The call's arguments exactly as they were sent (never re-planned), or <see langword="null"/> for none.</param>
/// <param name="RequestState">The server's opaque state to send back with the answers, or <see langword="null"/> when it sent none.</param>
/// <param name="Requests">What the server asked, by the key the answer must carry.</param>
/// <param name="CallId">The model's id for the call when it was made from a function-invocation turn, else <see langword="null"/>.</param>
public sealed record McpAwaitingInput(
    string PluginName,
    string ToolName,
    IReadOnlyDictionary<string, object?>? Arguments,
    string? RequestState,
    IReadOnlyDictionary<string, ElicitRequestParams> Requests,
    string? CallId = null);

/// <summary>
/// Thrown from inside the SDK's own retry of an <c>input_required</c> call to end it: the handler stopped waiting for the person.
/// The SDK passes a handler's exception through unwrapped, so <see cref="McpPluginManager"/> can catch it where the call was made.
/// </summary>
internal sealed class McpInputDeferredException(ElicitRequestParams request, CapturedInputRequired captured)
    : Exception("The call was released to wait for the person's answer.")
{
    /// <summary>The request the person was asked.</summary>
    public ElicitRequestParams Request { get; } = request;

    /// <summary>The server's result that asked it, found once when the call was released (so the entry cannot take another's state).</summary>
    public CapturedInputRequired Captured { get; } = captured;
}

/// <summary>Event data of <see cref="McpPluginManager.CallAwaitingInput"/>.</summary>
/// <param name="awaiting">The released call.</param>
public sealed class McpAwaitingInputEventArgs(McpAwaitingInput awaiting) : EventArgs
{
    /// <summary>The released call.</summary>
    public McpAwaitingInput Awaiting { get; } = awaiting;
}
