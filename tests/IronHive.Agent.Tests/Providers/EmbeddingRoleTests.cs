using System.ComponentModel;
using IronHive.Agent.Context;
using IronHive.Agent.Memory;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Providers;

/// <summary>
/// The query role of <see cref="IEmbeddingProvider"/>: an asymmetric model (E5, Nomic, BGE with a query instruction)
/// embeds a search query differently from the text it is compared against, so every search seam must ask for the query
/// side and every wrapper must forward it.
/// </summary>
public class EmbeddingRoleTests
{
    [Fact]
    public async Task EmbedQueryAsync_DefaultsToEmbedAsync_ForASymmetricProvider()
    {
        IEmbeddingProvider provider = new SymmetricProvider();

        var query = await provider.EmbedQueryAsync("find files", TestContext.Current.CancellationToken);

        Assert.Equal([7f], query);
    }

    [Fact]
    public async Task Fallback_ForwardsTheQueryRoleToTheActiveProvider()
    {
        var inner = new RecordingProvider();
        await using var fallback = new FallbackEmbeddingProvider(inner);

        var query = await fallback.EmbedQueryAsync("find files", TestContext.Current.CancellationToken);

        Assert.Equal(RecordingProvider.QueryVector, query);
        Assert.Equal(["find files"], inner.Queries);
    }

    [Fact]
    public async Task ToolRetriever_EmbedsTheQueryAsAQuery_AndToolsAsDocuments()
    {
        var provider = new RecordingProvider();
        var retriever = new EmbeddingToolRetriever(provider);
        IList<AITool> tools = [AIFunctionFactory.Create(ReadFile), AIFunctionFactory.Create(WriteFile)];

        await retriever.RetrieveAsync("read the config file", tools, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["read the config file"], provider.Queries);
        Assert.Equal(2, provider.Documents.Count);
        Assert.DoesNotContain("read the config file", provider.Documents);
    }

    [Fact]
    public async Task MemoryAdapter_SendsQueriesToTheQueryRole_AndStoredTextToTheDocumentRole()
    {
        var provider = new RecordingProvider();
        var adapter = new EmbeddingServiceAdapter(provider);
        var ct = TestContext.Current.CancellationToken;

        var query = await adapter.GenerateQueryEmbeddingAsync("what did I say about tests", ct);
        var stored = await adapter.GenerateEmbeddingAsync("the user prefers xunit", ct);
        var batch = await adapter.GenerateBatchEmbeddingsAsync(["a", "b"], ct);

        Assert.Equal(RecordingProvider.QueryVector, query.ToArray());
        Assert.Equal(RecordingProvider.DocumentVector, stored.ToArray());
        Assert.Equal(2, batch.Count);
        Assert.Equal(["what did I say about tests"], provider.Queries);
        Assert.Equal(["the user prefers xunit", "a", "b"], provider.Documents);
    }

    [Description("Read a file from disk")]
    private static string ReadFile(string path) => path;

    [Description("Write a file to disk")]
    private static string WriteFile(string path) => path;

    private sealed class SymmetricProvider : IEmbeddingProvider
    {
        private static readonly float[] Vector = [7f];

        public string ProviderName => "symmetric";
        public bool IsAvailable => true;
        public int Dimensions => 1;

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            new(Vector);

        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            new(texts.Select(_ => Vector).ToArray());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An asymmetric provider: queries and documents land on different vectors, and every call is recorded.</summary>
    private sealed class RecordingProvider : IEmbeddingProvider
    {
        public static readonly float[] QueryVector = [0f, 1f];
        public static readonly float[] DocumentVector = [1f, 0f];

        public List<string> Queries { get; } = [];
        public List<string> Documents { get; } = [];

        public string ProviderName => "recording";
        public bool IsAvailable => true;
        public int Dimensions => 2;

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            if (text != "test")
            {
                Documents.Add(text);
            }

            return new(DocumentVector);
        }

        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Documents.AddRange(texts);
            return new(texts.Select(_ => DocumentVector).ToArray());
        }

        public ValueTask<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return new(QueryVector);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
