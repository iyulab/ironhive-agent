using IronHive.DeepResearch.Models.Research;

namespace IronHive.DeepResearch.Orchestration.State;

/// <summary>
/// Token usage summed over one research run. Model calls happen in parallel (search enrichment, analysis), so the
/// totals are updated atomically.
/// </summary>
internal sealed class ResearchUsageTotals
{
    private long _input;
    private long _output;

    public void Add(int inputTokens, int outputTokens)
    {
        Interlocked.Add(ref _input, inputTokens);
        Interlocked.Add(ref _output, outputTokens);
    }

    public TokenUsage ToTokenUsage() => new()
    {
        InputTokens = (int)Interlocked.Read(ref _input),
        OutputTokens = (int)Interlocked.Read(ref _output),
    };
}

/// <summary>
/// The research run a model call belongs to. The orchestrator enters it for the duration of a run; the built-in
/// text-generation adapters record each call's usage into it. The adapters are shared across runs, so the run is found
/// through the async call chain rather than held by the adapter.
/// </summary>
internal static class ResearchUsageScope
{
    private static readonly AsyncLocal<ResearchUsageTotals?> Current = new();

    public static Scope Enter(ResearchUsageTotals totals)
    {
        var previous = Current.Value;
        Current.Value = totals;
        return new Scope(previous);
    }

    public static void Record(int inputTokens, int outputTokens) => Current.Value?.Add(inputTokens, outputTokens);

    public readonly struct Scope(ResearchUsageTotals? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
