namespace IronHive.Agent.Context;

/// <summary>
/// The tools a conversation has sent so far, for <see cref="ToolRetrievalOptions.StickyToolLimit"/>. The retrievers
/// stay stateless; an agent loop owns one of these for the life of its conversation.
/// </summary>
internal sealed class StickyToolSelection
{
    private IReadOnlyList<string> _sent = [];

    /// <summary>
    /// The options for the next retrieval: <paramref name="options"/> with the tools sent so far, when sticky selection
    /// is on; otherwise <paramref name="options"/> unchanged.
    /// </summary>
    public ToolRetrievalOptions? Apply(ToolRetrievalOptions? options) =>
        options is { StickyToolLimit: > 0 } ? options with { StickyTools = _sent } : options;

    /// <summary>Remembers what a request was sent; the selector already put the carried tools first.</summary>
    public void Record(ToolRetrievalOptions? options, ToolRetrievalResult result)
    {
        if (options is { StickyToolLimit: > 0 })
        {
            _sent = result.SelectedTools.Select(tool => tool.Name).ToList();
        }
    }

    /// <summary>Forgets the conversation — a cleared or replaced history starts a new one.</summary>
    public void Clear() => _sent = [];
}
