namespace IronHive.Agent.Context;

/// <summary>
/// Shared scored-slot budget math for tool retrievers that select <see cref="ToolRetrievalOptions.AlwaysInclude"/>
/// pins first and a scored tail second (<see cref="KeywordToolRetriever"/>, <see cref="EmbeddingToolRetriever"/>).
/// Pins are a floor that may exceed <see cref="ToolRetrievalOptions.MaxTools"/>; the scored tail is a
/// second, independent floor (<see cref="ToolRetrievalOptions.MinScoredSlots"/>) that must never be
/// crowded out as pins grow — the two floors are unrelated and must not be recoupled by breaking the
/// scored loop on total <c>selected.Count</c> again.
/// </summary>
internal static class ToolSelectionBudget
{
    /// <summary>
    /// Computes how many scored (non-pinned) tools the scored-selection loop may add.
    /// </summary>
    /// <param name="options">Retrieval options.</param>
    /// <param name="pinnedCount">Number of AlwaysInclude tools already selected before the scored loop runs.</param>
    public static int ScoredBudget(ToolRetrievalOptions options, int pinnedCount)
    {
        var floor = Math.Min(options.MinScoredSlots, options.MaxTools);
        return Math.Max(floor, options.MaxTools - pinnedCount);
    }
}
