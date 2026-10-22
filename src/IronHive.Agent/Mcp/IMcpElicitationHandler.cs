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
/// Without one, the server's request has nobody to answer it and the tool call fails, as before. Only URL mode is
/// advertised: a form a server would have the agent fill in is not collected.
/// </para>
/// </remarks>
public interface IMcpElicitationHandler
{
    /// <summary>Puts a server's elicitation request in front of a person and returns their answer.</summary>
    /// <param name="pluginName">The plugin (server) asking.</param>
    /// <param name="request">The request: <see cref="ElicitRequestParams.Mode"/> <c>url</c>, its message, URL and id.</param>
    /// <param name="cancellationToken">Cancels the wait (the tool call is cancelled with it).</param>
    ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken);
}
