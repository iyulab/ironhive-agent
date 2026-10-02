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
    /// When greater than 0, the set of tools sent earlier in this conversation (<see cref="StickyTools"/>) is sent
    /// again unchanged, in the order first sent, for as long as it serves the request. It changes only when the
    /// request needs a tool it lacks: a pin, a tool the request names exactly or by a declared alias, or the
    /// request's best-scored tool when that tool scores at least <see cref="StickyChangeScore"/> (a confident match,
    /// not merely the best of what is left). Then this request's whole selection joins it, after the carried tools — or,
    /// when that would exceed this many tools, the selection starts over from this request alone. Lower-ranked
    /// tools of a request that does not change the set are reported in <see cref="ToolRetrievalResult.Withheld"/>.
    /// <para>
    /// For a prefix-cached server (llama-server, vLLM) or a provider prompt cache: chat templates put the tools
    /// first, so any change to the tool list re-reads the prompt after it — all of it on a hybrid or recurrent
    /// model, which can roll back only to a saved checkpoint. A held set keeps the cache from message to message.
    /// Set it above <see cref="MaxTools"/> plus the pins, or nothing will ever fit. Leave it at 0 when every
    /// request should get exactly its own selection.
    /// </para>
    /// <para>Default: 0 — off; every request is selected on its own.</para>
    /// </summary>
    public int StickyToolLimit { get; init; }

    /// <summary>
    /// The score a scored tool the carried set lacks must reach before it may change that set; read only when
    /// <see cref="StickyToolLimit"/> is greater than 0. Below it the request is served by the carried set and the
    /// tool is reported in <see cref="ToolRetrievalResult.Withheld"/> with its score. Pins, exact names and declared
    /// aliases change the set whatever this is.
    /// <para>
    /// Every change to the tool block re-reads the prompt, so a held set should change for a real need, not for the
    /// best of what a generic follow-up ("what can you help me with?") happens to touch — such a request still has a
    /// best-scored tool, usually just above <see cref="MinRelevanceScore"/>. The default sits just under the
    /// <see cref="KeywordToolRetriever"/> name weight (0.75): a newcomer clears it when the request covers its whole
    /// name, or most of its name and description. Same scale as <see cref="MinRelevanceScore"/>; with
    /// <see cref="EmbeddingToolRetriever"/>, whose scores are <c>(cosine + 1) / 2</c>, tune both together. A value at
    /// or below <see cref="MinRelevanceScore"/> lets any selected best-scored tool change the set, as before 0.39.
    /// </para>
    /// <para>Default: 0.7.</para>
    /// </summary>
    public float StickyChangeScore { get; init; } = 0.7f;

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

    /// <summary>
    /// Tools this request selected but did not send, because <see cref="ToolRetrievalOptions.StickyToolLimit"/> held
    /// the carried set unchanged — each with the reason it was selected. Empty when nothing was held back.
    /// </summary>
    public IReadOnlyList<ToolSelection> Withheld { get; init; } = [];
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
