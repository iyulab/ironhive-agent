using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>Installs a <see cref="ToolNameAliasChatClient"/> on a chat client pipeline.</summary>
public static class ToolNameAliasChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="ToolNameAliasChatClient"/>: a tool whose name a provider would refuse (<c>message.send</c>) is offered
    /// to the model under a stable safe alias, and the model's call is mapped back to the real name. Place it directly after
    /// the function invocation and above the text recovery:
    /// <c>builder.UseToolInvocationPipeline(…).UseToolNameAlias().UseTextToolCallRecovery()</c>.
    /// </summary>
    /// <param name="builder">The chat client builder.</param>
    public static ChatClientBuilder UseToolNameAlias(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new ToolNameAliasChatClient(inner));
    }
}
