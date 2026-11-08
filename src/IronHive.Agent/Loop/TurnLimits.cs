using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Limits that belong to one request rather than to the chat client: today the number of tool rounds a single turn may
/// use. They travel on <see cref="ChatOptions.AdditionalProperties"/>, so a host sets them per turn without touching the
/// shared client, and <see cref="TurnLimitChatClient"/> (placed under function invocation) enforces them.
/// </summary>
public static class TurnLimits
{
    /// <summary>The <see cref="ChatOptions.AdditionalProperties"/> key that carries a turn's cap on tool rounds.</summary>
    public const string MaxIterationsKey = "ironhive.turn.max_iterations";

    /// <summary>
    /// A copy of <paramref name="options"/> whose turn may use at most <paramref name="maxIterations"/> rounds of model
    /// calls with tools. The function-invoking client's own <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/>
    /// stays the ceiling: a larger value here does not raise it. Needs a <see cref="TurnLimitChatClient"/> in the pipeline
    /// (<c>.UseFunctionInvocation().UseTurnLimits()</c>); without one the value is ignored.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxIterations"/> is below 1.</exception>
    public static ChatOptions WithMaxIterations(this ChatOptions options, int maxIterations)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);

        var copy = options.Clone();
        copy.AdditionalProperties ??= [];
        copy.AdditionalProperties[MaxIterationsKey] = maxIterations;
        return copy;
    }

    /// <summary>The turn cap carried by <paramref name="options"/>, or <c>null</c> when it has none (or an unusable value).</summary>
    public static int? MaxIterationsOf(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties || !properties.TryGetValue(MaxIterationsKey, out var value))
        {
            return null;
        }

        var cap = value switch
        {
            int i => i,
            long l when l is >= 1 and <= int.MaxValue => (int)l,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e when e.TryGetInt32(out var n) => n,
            _ => 0,
        };
        return cap >= 1 ? cap : null;
    }
}
