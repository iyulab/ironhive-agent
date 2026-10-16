using IndexThinking.Abstractions;

namespace IronHive.Agent.Context;

/// <summary>
/// An IndexThinking <see cref="ITokenCounter"/> that counts with a <see cref="IContextTokenCounter"/>, so the IndexThinking
/// stages a <see cref="ContextManager"/> composes (the tool-result masker) measure their budgets the way the manager does —
/// with a consumer's own counter when one is given.
/// </summary>
internal sealed class ContextTokenCounterAdapter(IContextTokenCounter counter) : ITokenCounter
{
    private readonly IContextTokenCounter _counter = counter ?? throw new ArgumentNullException(nameof(counter));

    public int Count(string text) => _counter.CountTokens(text);

    public int Count(IEnumerable<string> texts) => texts.Sum(_counter.CountTokens);

    public bool SupportsModel(string modelId) => true;
}
