using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Options for tool retrieval.
/// </summary>
public record ToolRetrievalOptions
{
    /// <summary>
    /// Maximum number of tools to return. Default: 10.
    /// </summary>
    public int MaxTools { get; init; } = 10;

    /// <summary>
    /// Minimum relevance score (0.0–1.0) for a tool to be included. Default: 0.3.
    /// </summary>
    public float MinRelevanceScore { get; init; } = 0.3f;

    /// <summary>
    /// Tool names that should always be included regardless of score.
    /// </summary>
    public IReadOnlyList<string>? AlwaysInclude { get; init; }

    /// <summary>
    /// Guarantees at least this many scored (non-pinned) slots in the selection, even when
    /// <see cref="AlwaysInclude"/> pins alone already meet or exceed <see cref="MaxTools"/>. Without
    /// this, a caller that grows <c>AlwaysInclude</c> at runtime (e.g. merging plugin-provided tool
    /// names into the pin list) sees the scored tail shrink toward zero as pins accumulate past
    /// <c>MaxTools</c> — silently reintroducing the same starvation that score-based selection exists
    /// to avoid, just via pin growth instead of query-length dilution. Pins can still exceed
    /// <c>MaxTools</c>; worst-case total selection size is <c>pinnedCount + MinScoredSlots</c>, not
    /// <c>pinnedCount</c> alone.
    /// <para>
    /// The floor itself is clamped to <c>MaxTools</c>, so a caller that has deliberately lowered
    /// <c>MaxTools</c> below this value (e.g. a small-context model capping tool-schema token cost)
    /// is respected rather than silently overridden.
    /// </para>
    /// <para>Default: 0 — preserves prior behavior, where pins can shrink the scored tail to zero.</para>
    /// </summary>
    public int MinScoredSlots { get; init; }
}

/// <summary>
/// Result of a tool retrieval operation.
/// </summary>
public record ToolRetrievalResult
{
    /// <summary>
    /// The selected tools.
    /// </summary>
    public required IList<AITool> SelectedTools { get; init; }

    /// <summary>
    /// Relevance scores per tool name (0.0–1.0). Null if scoring is not applicable.
    /// </summary>
    public IReadOnlyDictionary<string, float>? RelevanceScores { get; init; }

    /// <summary>
    /// Why each selected tool was selected, in selection order — what a request was actually sent and on
    /// what grounds. Empty when the retriever does not report it.
    /// </summary>
    public IReadOnlyList<ToolSelection> Selections { get; init; } = [];
}

/// <summary>
/// One selected tool and the reason it was selected.
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Reason">Why the tool was selected.</param>
/// <param name="Score">The tool's relevance score, when it was scored.</param>
public sealed record ToolSelection(string Name, ToolSelectionReason Reason, float? Score = null);

/// <summary>
/// Why a tool was selected.
/// </summary>
public enum ToolSelectionReason
{
    /// <summary>Listed in <see cref="ToolRetrievalOptions.AlwaysInclude"/>.</summary>
    Pinned = 1,

    /// <summary>The query names the tool by its exact name. Takes the first scored slots, regardless of score.</summary>
    ExactName = 2,

    /// <summary>The query holds one of the tool's declared aliases (<see cref="ToolRetrievalHints.AliasesKey"/>).</summary>
    Alias = 3,

    /// <summary>Selected on relevance score.</summary>
    Scored = 4,

    /// <summary>
    /// Declared as a companion (<see cref="ToolRetrievalHints.CompanionsKey"/>) of a selected tool; added outside
    /// the scored budget.
    /// </summary>
    Companion = 5,
}

/// <summary>
/// Retrieves relevant tools for a given query.
/// Implementations may use keyword matching, embeddings, or other strategies.
/// </summary>
public interface IToolRetriever
{
    /// <summary>
    /// Selects the most relevant tools for the given query.
    /// </summary>
    /// <param name="query">The user query or task description.</param>
    /// <param name="availableTools">All available tools to select from.</param>
    /// <param name="options">Retrieval options (max tools, min score, always-include list).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Selected tools with relevance scores.</returns>
    Task<ToolRetrievalResult> RetrieveAsync(
        string query,
        IList<AITool> availableTools,
        ToolRetrievalOptions? options = null,
        CancellationToken cancellationToken = default);
}
