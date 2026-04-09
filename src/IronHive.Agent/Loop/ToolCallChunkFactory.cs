using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Canonical factory for building <see cref="ToolCallChunk"/> instances from provider-level
/// <see cref="FunctionCallContent"/>. Centralising this logic ensures every <see cref="IAgentLoop"/>
/// implementation emits the same, documented wire format.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FunctionCallContent.Arguments"/> from Microsoft.Extensions.AI is the fully
/// materialised argument dictionary — the underlying provider has already accumulated any
/// streaming fragments. Because of that, chunks produced by this factory are always marked
/// <see cref="ToolCallChunk.IsComplete"/> = <c>true</c> and <see cref="ToolCallChunk.ArgumentsDelta"/>
/// is guaranteed to be valid JSON (or <c>null</c> when the provider reported no arguments).
/// </para>
/// <para>
/// A future streaming loop that wants to forward raw provider deltas without accumulating
/// should build <see cref="ToolCallChunk"/> directly with <see cref="ToolCallChunk.IsComplete"/> = <c>false</c>
/// rather than using this factory.
/// </para>
/// </remarks>
public static class ToolCallChunkFactory
{
    /// <summary>
    /// Builds a complete <see cref="ToolCallChunk"/> from a <see cref="FunctionCallContent"/>,
    /// serializing <see cref="FunctionCallContent.Arguments"/> as JSON.
    /// </summary>
    /// <param name="functionCall">The function-call content emitted by a chat client.</param>
    /// <returns>
    /// A <see cref="ToolCallChunk"/> with <see cref="ToolCallChunk.IsComplete"/> set to <c>true</c>.
    /// <see cref="ToolCallChunk.ArgumentsDelta"/> is <c>null</c> only when <paramref name="functionCall"/>
    /// has no arguments; otherwise it is guaranteed to be valid JSON.
    /// </returns>
    public static ToolCallChunk FromFunctionCall(FunctionCallContent functionCall)
    {
        ArgumentNullException.ThrowIfNull(functionCall);

        return new ToolCallChunk
        {
            Id = functionCall.CallId,
            NameDelta = functionCall.Name,
            ArgumentsDelta = functionCall.Arguments is not null
                ? JsonSerializer.Serialize(functionCall.Arguments)
                : null,
            IsComplete = true
        };
    }
}
