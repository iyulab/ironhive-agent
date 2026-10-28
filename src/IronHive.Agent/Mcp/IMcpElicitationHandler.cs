using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// The person-facing side of an MCP server's elicitation request — what a server asks for when it needs a person before a
/// tool call can finish: «approve this at the link», a payment confirmation, a first-use OAuth connection.
/// </summary>
/// <remarks>
/// <para>
/// With a handler, <see cref="McpPluginManager"/> advertises URL-mode elicitation to every server it connects. A server that
/// answers <c>tools/call</c> with <c>input_required</c> (multi round-trip requests) or sends <c>elicitation/create</c> reaches
/// this handler; the MCP client keeps the round trip — it retries the call with the server's <c>requestState</c> once the
/// handler returns. The handler shows <see cref="ElicitRequestParams.Message"/> and <see cref="ElicitRequestParams.Url"/>
/// to a person and returns when they are done (<c>accept</c>), declined (<c>decline</c>) or dismissed it (<c>cancel</c>).
/// </para>
/// <para>
/// A person can also finish the step where no host sees it (another device, a closed screen). A server that reports it with
/// <c>notifications/elicitation/complete</c> ends the wait: the token passed to <see cref="ElicitAsync(string, ElicitRequestParams, CancellationToken)"/> is cancelled, the
/// handler should return promptly (its return value is then ignored) and the call is sent again — the server judges the
/// outcome. A handler that waits on a person must therefore honour its token.
/// </para>
/// <para>
/// Without one, the server's request has nobody to answer it and the tool call fails, as before. Only URL mode is
/// advertised: a form a server would have the agent fill in is not collected.
/// </para>
/// </remarks>
public interface IMcpElicitationHandler
{
    /// <summary>Puts a server's elicitation request in front of a person and returns their answer.</summary>
    /// <param name="pluginName">The plugin (server) asking.</param>
    /// <param name="request">The request: <see cref="ElicitRequestParams.Mode"/> <c>url</c>, its message, URL and id.</param>
    /// <param name="cancellationToken">Cancels the wait: the tool call was cancelled, or the server reported the elicitation
    /// complete (the call then continues and this wait's result is ignored).</param>
    ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken);

    /// <summary>
    /// The same, with the tool call the request belongs to when it can be named: the call the plugin is running when the
    /// request arrives, if it is running exactly one. A handler that shows a person what they are approving overrides this;
    /// the default forwards to <see cref="ElicitAsync(string, ElicitRequestParams, CancellationToken)"/>.
    /// </summary>
    /// <param name="pluginName">The plugin (server) asking.</param>
    /// <param name="call">The call that is waiting on this request, or <see langword="null"/> when it cannot be told — the
    /// plugin has no call in flight (a server-initiated request) or several (the request cannot be tied to one of them).</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">As for the overload without a call.</param>
    ValueTask<ElicitResult> ElicitAsync(string pluginName, McpCallInfo? call, ElicitRequestParams request, CancellationToken cancellationToken)
        => ElicitAsync(pluginName, request, cancellationToken);
}

/// <summary>A tool call in flight on an MCP plugin, as seen by an <see cref="IMcpElicitationHandler"/>.</summary>
/// <param name="ToolName">The tool the plugin was asked to run.</param>
/// <param name="CallId">The model's id for the call (the chat client's function-call id), or <see langword="null"/> when the
/// call was not made from a function-invocation turn.</param>
public sealed record McpCallInfo(string ToolName, string? CallId);
