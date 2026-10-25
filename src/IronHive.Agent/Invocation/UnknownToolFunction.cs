using System.Text.Json;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Stands in for a tool the model called but the request did not offer, so the call goes through the
/// <see cref="ToolInvocationPipeline"/> like any other and the loop guards see it. It does not run anything: it answers a
/// <see cref="ToolCallRefusal"/> (<see cref="ToolCallRefusalKind.UnknownTool"/>) whose text is the «not found» text a
/// function-invoking client gives, which <see cref="RepeatedErrorGuardMiddleware"/> counts as a failure.
/// </summary>
/// <remarks>
/// <see cref="ToolInvocationPipelineChatClientBuilderExtensions.UseToolInvocationPipeline(ChatClientBuilder, Action{FunctionInvokingChatClient}?)"/>
/// creates one per unknown name, per request, and makes it callable through the client's
/// <see cref="FunctionInvokingChatClient.AdditionalTools"/> — never advertised to the model. A middleware that acts on what a
/// tool is (a permission gate, an argument check) should let it pass: there is nothing to approve or validate.
/// </remarks>
public sealed class UnknownToolFunction : AIFunction
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    /// <summary>Creates the stand-in for <paramref name="name"/>.</summary>
    /// <param name="name">The name the model called.</param>
    public UnknownToolFunction(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description => "A tool this request does not offer.";

    /// <inheritdoc />
    public override JsonElement JsonSchema => EmptySchema;

    // The refusal every call to the name answers.
    internal static ToolCallRefusal RefusalFor(string name) =>
        new(ToolCallRefusalKind.UnknownTool, $"Requested function \"{name}\" not found.");

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>(RefusalFor(Name));
}
