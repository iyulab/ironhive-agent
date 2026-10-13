using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>Installs a <see cref="TextToolCallRecoveryChatClient"/> on a chat client pipeline.</summary>
public static class TextToolCallRecoveryChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="TextToolCallRecoveryChatClient"/>: a tool call the model wrote as text (Hermes / Qwen
    /// <c>&lt;tool_call&gt;</c> markup, Qwen3-Coder <c>&lt;function=…&gt;</c>) becomes the structured call it meant. Place
    /// it directly after the function invocation, so that client runs the recovered calls:
    /// <c>builder.UseToolInvocationPipeline(…).UseTextToolCallRecovery()</c>.
    /// </summary>
    /// <param name="builder">The chat client builder.</param>
    public static ChatClientBuilder UseTextToolCallRecovery(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new TextToolCallRecoveryChatClient(inner));
    }
}
