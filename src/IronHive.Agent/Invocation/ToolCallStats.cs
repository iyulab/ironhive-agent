using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Lets a running tool report how much work its call did — items visited, bytes read, why it stopped — to whoever
/// watches the loop, without putting it in the result the model reads.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Report(string, long)"/> (or an overload) from inside the tool while it runs through a
/// <see cref="ToolInvocationPipeline"/>. The values land on that call's <see cref="Loop.ToolCallResult.Stats"/>, which a
/// host can forward to its client (the IronHive host puts them on <c>tool_end</c>); they are never added to the tool's
/// result and never sent to the model. Outside a pipeline-run tool call, reporting does nothing.
/// </para>
/// <para>
/// The keys are the tool's own. The channel is bounded so it cannot become a second output: at most
/// <see cref="MaxEntries"/> keys of at most <see cref="MaxKeyLength"/> characters, and string values of at most
/// <see cref="MaxStringLength"/> characters. What exceeds a bound is dropped, not truncated. Reporting a key again
/// replaces its value.
/// </para>
/// </remarks>
public static class ToolCallStats
{
    /// <summary>The most keys one call can report; further new keys are dropped.</summary>
    public const int MaxEntries = 16;

    /// <summary>The longest key accepted; a longer key is dropped.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The longest string value accepted; a longer value is dropped.</summary>
    public const int MaxStringLength = 256;

    private static readonly AsyncLocal<Collector?> CurrentCollector = new();

    // Keyed by the call the pipeline ran (FunctionInvocationContext.CallContent): the loop correlates results against
    // the same FunctionCallContent instances, and nothing is attached to the messages the model is sent.
    private static readonly ConditionalWeakTable<FunctionCallContent, IReadOnlyDictionary<string, JsonElement>> ByCall = [];

    /// <summary>Reports a count or size.</summary>
    /// <param name="key">The tool's name for the measurement.</param>
    /// <param name="value">Its value.</param>
    public static void Report(string key, long value) => Set(key, JsonSerializer.SerializeToElement(value, ToolCallStatsJson.Default.Int64));

    /// <summary>Reports a fractional measurement (seconds, a ratio).</summary>
    /// <param name="key">The tool's name for the measurement.</param>
    /// <param name="value">Its value; NaN and infinities are dropped.</param>
    public static void Report(string key, double value)
    {
        if (double.IsFinite(value))
        {
            Set(key, JsonSerializer.SerializeToElement(value, ToolCallStatsJson.Default.Double));
        }
    }

    /// <summary>Reports a flag (stopped on a budget, scope was truncated).</summary>
    /// <param name="key">The tool's name for the measurement.</param>
    /// <param name="value">Its value.</param>
    public static void Report(string key, bool value) => Set(key, JsonSerializer.SerializeToElement(value, ToolCallStatsJson.Default.Boolean));

    /// <summary>Reports a short label (why the call stopped, what kind of scope it had).</summary>
    /// <param name="key">The tool's name for the measurement.</param>
    /// <param name="value">Its value; longer than <see cref="MaxStringLength"/> is dropped.</param>
    public static void Report(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= MaxStringLength)
        {
            Set(key, JsonSerializer.SerializeToElement(value, ToolCallStatsJson.Default.String));
        }
    }

    /// <summary>
    /// What the tool reported during <paramref name="call"/>, or <c>null</c> when it reported nothing (or the call was
    /// not run through a <see cref="ToolInvocationPipeline"/>).
    /// </summary>
    /// <param name="call">The call as the model emitted it.</param>
    public static IReadOnlyDictionary<string, JsonElement>? For(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return ByCall.TryGetValue(call, out var stats) ? stats : null;
    }

    private static void Set(string key, JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length <= MaxKeyLength)
        {
            CurrentCollector.Value?.Set(key, value);
        }
    }

    /// <summary>Opens the collector for one call; the tool runs inside it.</summary>
    internal static Scope Begin(FunctionCallContent? call)
    {
        var previous = CurrentCollector.Value;
        var collector = new Collector();
        CurrentCollector.Value = collector;
        return new Scope(call, collector, previous);
    }

    internal readonly struct Scope(FunctionCallContent? call, Collector collector, Collector? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentCollector.Value = previous;
            if (call is not null && collector.Snapshot() is { } stats)
            {
                ByCall.AddOrUpdate(call, stats);
            }
        }
    }

    internal sealed class Collector
    {
        private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();

        public void Set(string key, JsonElement value)
        {
            lock (_lock)
            {
                if (_values.Count < MaxEntries || _values.ContainsKey(key))
                {
                    _values[key] = value;
                }
            }
        }

        public IReadOnlyDictionary<string, JsonElement>? Snapshot()
        {
            lock (_lock)
            {
                return _values.Count == 0 ? null : new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(_values, StringComparer.Ordinal));
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(long))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ToolCallStatsJson : System.Text.Json.Serialization.JsonSerializerContext;
