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

    /// <summary>
    /// When greater than 0, tools already sent in this conversation (<see cref="StickyTools"/>) stay in the
    /// selection, in the order they were first sent, with newly selected tools after them — as long as the
    /// whole set fits this many tools. When it would not, the selection starts over from this request alone.
    /// <para>
    /// For a prefix-cached server (llama-server, vLLM) whose chat template renders tools before the system
    /// text: a selection that changes with every message makes the server re-read the whole prompt on every
    /// message; a set that only grows at its tail is re-read from the first new tool, and not at all while
    /// it holds. Set it above <see cref="MaxTools"/> plus the pins, or nothing will ever fit. Leave it at 0
    /// for a remote API that bills every prompt token.
    /// </para>
    /// <para>Default: 0 — off; every request is selected on its own.</para>
    /// </summary>
    public int StickyToolLimit { get; init; }

    /// <summary>
    /// The tools sent earlier in this conversation, in the order first sent; read only when
    /// <see cref="StickyToolLimit"/> is greater than 0. The agent loops fill it from their own previous
    /// request. A caller driving an <see cref="IToolRetriever"/> directly passes the names of the previous
    /// result's <see cref="ToolRetrievalResult.SelectedTools"/>. Names no longer in the catalog are dropped.
    /// </summary>
    public IReadOnlyList<string> StickyTools { get; init; } = [];
}

/// <summary>
/// Result of a tool retrieval operation.
/// </summary>
public record ToolRetrievalResult
{
    /// <summary>
    /// The selected tools, in the order they are sent. The retrievers in this library send them ordinal by name,
    /// so the same set always serialises identically — a prefix-cached server keeps its prompt cache while the set
    /// holds. With <see cref="ToolRetrievalOptions.StickyToolLimit"/> on, the tools carried from earlier requests
    /// come first, in the order first sent, and the rest follow ordinal by name. The ranking that chose them is in
    /// <see cref="Selections"/>.
    /// </summary>
    public required IList<AITool> SelectedTools { get; init; }

    /// <summary>
    /// Relevance scores per tool name (0.0–1.0). Null if scoring is not applicable.
    /// </summary>
    public IReadOnlyDictionary<string, float>? RelevanceScores { get; init; }

    /// <summary>
    /// Why each selected tool was selected, in selection order (pins, exact names, the scored tail by score,
    /// companions) — what a request was actually sent and on what grounds. This is the ranking, not the order sent
    /// (<see cref="SelectedTools"/>). Empty when the retriever does not report it.
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

    /// <summary>
    /// Sent earlier in the conversation and kept in the selection by <see cref="ToolRetrievalOptions.StickyToolLimit"/>,
    /// without being selected again for this request.
    /// </summary>
    Carried = 6,
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
