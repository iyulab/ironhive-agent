using System.ComponentModel;
using IronHive.Agent.Context;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-05: Embedding-based Tool Retriever — semantic tool search via cosine similarity.
/// Uses a fake bag-of-words embedding provider for deterministic tests.
/// </summary>
public class EmbeddingToolRetrieverTests
{
    #region Empty / Edge Cases

    [Fact]
    public async Task RetrieveAsync_EmptyTools_ReturnsEmpty()
    {
        var retriever = CreateRetriever();

        var result = await retriever.RetrieveAsync("read a file", []);

        Assert.Empty(result.SelectedTools);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyQuery_ReturnsAlwaysIncludeOnly()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { AlwaysInclude = ["ReadFile"] };

        var result = await retriever.RetrieveAsync("", tools, options);

        Assert.Single(result.SelectedTools);
        Assert.Equal("ReadFile", GetName(result.SelectedTools[0]));
    }

    [Fact]
    public async Task RetrieveAsync_WhitespaceQuery_ReturnsEmpty()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("   ", tools);

        Assert.Empty(result.SelectedTools);
    }

    #endregion

    #region Semantic Matching

    [Fact]
    public async Task RetrieveAsync_SemanticMatch_ReturnsRelevantTool()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();

        // "read file" should match ReadFile tool
        var result = await retriever.RetrieveAsync("read file content", tools);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ReadFile", names);
    }

    [Fact]
    public async Task RetrieveAsync_TopToolIsHighestScored()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("execute command shell", tools);

        // ExecuteCommand should be top result
        Assert.Equal("ExecuteCommand", GetName(result.SelectedTools[0]));
    }

    [Fact]
    public async Task RetrieveAsync_AllToolsGetScores()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("read", tools);

        Assert.Equal(tools.Count, result.RelevanceScores!.Count);
    }

    [Fact]
    public async Task RetrieveAsync_HigherRelevanceForExactMatch()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("grep files pattern regex", tools);

        var scores = result.RelevanceScores!;
        // GrepFiles should have higher score than unrelated tools
        Assert.True(scores["GrepFiles"] > scores["WriteFile"],
            $"GrepFiles ({scores["GrepFiles"]}) should score higher than WriteFile ({scores["WriteFile"]})");
    }

    #endregion

    #region MaxTools / MinScore

    [Fact]
    public async Task RetrieveAsync_RespectsMaxTools()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            MaxTools = 2,
            MinRelevanceScore = 0.0f
        };

        var result = await retriever.RetrieveAsync("read write list grep execute", tools, options);

        Assert.True(result.SelectedTools.Count <= 2);
    }

    [Fact]
    public async Task RetrieveAsync_MinRelevanceScore_FiltersLowScored()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { MinRelevanceScore = 0.99f };

        var result = await retriever.RetrieveAsync("something unrelated xyz", tools, options);

        // Very high threshold should filter most/all tools
        Assert.True(result.SelectedTools.Count <= 1);
    }

    #endregion

    #region AlwaysInclude

    [Fact]
    public async Task RetrieveAsync_AlwaysInclude_AlwaysPresent()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["GrepFiles"],
            MaxTools = 10
        };

        var result = await retriever.RetrieveAsync("write content output", tools, options);

        Assert.Contains("GrepFiles", result.SelectedTools.Select(GetName));
    }

    [Fact]
    public async Task RetrieveAsync_AlwaysInclude_NoDuplication()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["ReadFile"],
            MinRelevanceScore = 0.0f
        };

        var result = await retriever.RetrieveAsync("read file", tools, options);

        var count = result.SelectedTools.Count(t => GetName(t) == "ReadFile");
        Assert.Equal(1, count);
    }

    #endregion

    #region Index Caching

    [Fact]
    public async Task RetrieveAsync_SameToolList_DoesNotRebuildIndex()
    {
        var provider = new FakeEmbeddingProvider();
        var retriever = new EmbeddingToolRetriever(provider);
        var tools = CreateTestTools();

        // First call builds index
        await retriever.RetrieveAsync("read", tools);
        var firstBatchCount = provider.BatchCallCount;

        // Second call with same reference should not rebuild
        await retriever.RetrieveAsync("write", tools);
        Assert.Equal(firstBatchCount, provider.BatchCallCount);
    }

    [Fact]
    public async Task RetrieveAsync_DifferentToolList_RebuildsIndex()
    {
        var provider = new FakeEmbeddingProvider();
        var retriever = new EmbeddingToolRetriever(provider);
        var tools1 = CreateTestTools();
        var tools2 = CreateTestTools(); // Different reference

        await retriever.RetrieveAsync("read", tools1);
        var firstBatchCount = provider.BatchCallCount;

        await retriever.RetrieveAsync("read", tools2);
        Assert.True(provider.BatchCallCount > firstBatchCount);
    }

    [Fact]
    public async Task RebuildIndexAsync_ForcesReindex()
    {
        var provider = new FakeEmbeddingProvider();
        var retriever = new EmbeddingToolRetriever(provider);
        var tools = CreateTestTools();

        await retriever.RetrieveAsync("read", tools);
        var firstBatchCount = provider.BatchCallCount;

        await retriever.RebuildIndexAsync(tools);
        Assert.True(provider.BatchCallCount > firstBatchCount);
    }

    #endregion

    #region CosineSimilarity

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1, 2, 3, 4, 5 };
        var sim = EmbeddingToolRetriever.CosineSimilarity(v, v);

        Assert.True(MathF.Abs(sim - 1.0f) < 0.0001f, $"Expected ~1.0, got {sim}");
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 0, 1, 0 };
        var sim = EmbeddingToolRetriever.CosineSimilarity(a, b);

        Assert.True(MathF.Abs(sim) < 0.0001f, $"Expected ~0.0, got {sim}");
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { -1, -2, -3 };
        var sim = EmbeddingToolRetriever.CosineSimilarity(a, b);

        Assert.True(MathF.Abs(sim + 1.0f) < 0.0001f, $"Expected ~-1.0, got {sim}");
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { 1, 2 };
        var sim = EmbeddingToolRetriever.CosineSimilarity(a, b);

        Assert.Equal(0f, sim);
    }

    [Fact]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var sim = EmbeddingToolRetriever.CosineSimilarity([], []);

        Assert.Equal(0f, sim);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new float[] { 0, 0, 0 };
        var b = new float[] { 1, 2, 3 };
        var sim = EmbeddingToolRetriever.CosineSimilarity(a, b);

        Assert.Equal(0f, sim);
    }

    #endregion

    #region Interface Contract

    [Fact]
    public void Constructor_NullEmbedder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EmbeddingToolRetriever(null!));
    }

    [Fact]
    public async Task RetrieveAsync_ImplementsIToolRetriever()
    {
        var retriever = CreateRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("test", tools);

        Assert.NotNull(result);
        Assert.NotNull(result.SelectedTools);
    }

    #endregion

    #region Helpers

    private static string GetName(AITool tool) =>
        tool is AIFunction func ? func.Name : tool.GetType().Name;

    private static EmbeddingToolRetriever CreateRetriever()
    {
        return new EmbeddingToolRetriever(new FakeEmbeddingProvider());
    }

    private static IList<AITool> CreateTestTools()
    {
        return
        [
            AIFunctionFactory.Create(SampleTools.ReadFile),
            AIFunctionFactory.Create(SampleTools.WriteFile),
            AIFunctionFactory.Create(SampleTools.ListDirectory),
            AIFunctionFactory.Create(SampleTools.GrepFiles),
            AIFunctionFactory.Create(SampleTools.ExecuteCommand),
        ];
    }

    private static class SampleTools
    {
        [Description("Read the content of a file at the specified path.")]
        public static string ReadFile(
            [Description("File path to read")] string path) => $"content of {path}";

        [Description("Write content to a file. Creates the file if it doesn't exist.")]
        public static string WriteFile(
            [Description("File path to write")] string path,
            [Description("Content to write")] string content) => "ok";

        [Description("List the contents of a directory.")]
        public static string ListDirectory(
            [Description("Directory path")] string path) => "files";

        [Description("Search for a pattern in files using regex matching.")]
        public static string GrepFiles(
            [Description("Regex pattern")] string pattern,
            [Description("Directory to search")] string? path = null) => "matches";

        [Description("Execute a shell command and return the output.")]
        public static string ExecuteCommand(
            [Description("The command to execute")] string command) => "output";
    }

    /// <summary>
    /// A fake embedding provider that generates deterministic bag-of-words embeddings.
    /// Each unique word maps to a dimension, making cosine similarity proportional
    /// to word overlap — ideal for predictable test behavior.
    /// </summary>
    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        private readonly Dictionary<string, int> _vocabulary = new(StringComparer.OrdinalIgnoreCase);
        private int _nextDim;

        public string ProviderName => "fake-bow";
        public bool IsAvailable => true;
        public int Dimensions => Math.Max(_nextDim, 64);
        public int BatchCallCount { get; private set; }

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return new ValueTask<float[]>(Embed(text));
        }

        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            var results = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
            {
                results[i] = Embed(texts[i]);
            }
            return new ValueTask<float[][]>(results);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private float[] Embed(string text)
        {
            var tokens = text.Split([' ', '_', '-', '.', ',', ':', ';', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries);

            // Assign dimension indices to new tokens
            foreach (var token in tokens)
            {
                if (!_vocabulary.ContainsKey(token))
                {
                    _vocabulary[token] = _nextDim++;
                }
            }

            // Create bag-of-words vector
            var dim = Math.Max(_nextDim, 64);
            var vec = new float[dim];
            foreach (var token in tokens)
            {
                if (_vocabulary.TryGetValue(token, out var idx) && idx < vec.Length)
                {
                    vec[idx] += 1f;
                }
            }

            // Normalize
            var norm = 0f;
            foreach (var v in vec)
            {
                norm += v * v;
            }
            norm = MathF.Sqrt(norm);
            if (norm > 0)
            {
                for (var i = 0; i < vec.Length; i++)
                {
                    vec[i] /= norm;
                }
            }

            return vec;
        }
    }

    #endregion
}
