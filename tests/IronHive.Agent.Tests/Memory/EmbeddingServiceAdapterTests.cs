using IronHive.Agent.Memory;
using IronHive.Agent.Providers;

namespace IronHive.Agent.Tests.Memory;

public class EmbeddingServiceAdapterTests
{
    // --- Constructor ---

    [Fact]
    public void Constructor_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EmbeddingServiceAdapter(null!));
    }

    // --- Dimensions ---

    [Fact]
    public void Dimensions_DelegatesToProvider()
    {
        var adapter = new EmbeddingServiceAdapter(new FakeProvider { Dimensions = 1536 });

        Assert.Equal(1536, adapter.Dimensions);
    }

    [Fact]
    public void Dimensions_ReturnsZeroWhenProviderReturnsZero()
    {
        var adapter = new EmbeddingServiceAdapter(new FakeProvider { Dimensions = 0 });

        Assert.Equal(0, adapter.Dimensions);
    }

    // --- GenerateEmbeddingAsync ---

    [Fact]
    public async Task GenerateEmbedding_ReturnsReadOnlyMemoryFromFloatArray()
    {
        var provider = new FakeProvider { Single = [0.1f, 0.2f, 0.3f] };

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateEmbeddingAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Length);
        Assert.Equal(0.1f, result.Span[0]);
        Assert.Equal(0.2f, result.Span[1]);
        Assert.Equal(0.3f, result.Span[2]);
        Assert.Equal(["hello"], provider.Texts);
    }

    [Fact]
    public async Task GenerateEmbedding_EmptyArray_ReturnsEmptyMemory()
    {
        var provider = new FakeProvider { Single = [] };

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateEmbeddingAsync("", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Length);
    }

    [Fact]
    public async Task GenerateEmbedding_PassesCancellationToken()
    {
        var provider = new FakeProvider();
        var adapter = new EmbeddingServiceAdapter(provider);
        using var cts = new CancellationTokenSource();

        await adapter.GenerateEmbeddingAsync("test", cts.Token);

        Assert.Equal(cts.Token, provider.LastToken);
    }

    // --- GenerateBatchEmbeddingsAsync ---

    [Fact]
    public async Task GenerateBatch_ReturnsListOfReadOnlyMemory()
    {
        var provider = new FakeProvider { Batch = [[1.0f, 2.0f], [3.0f, 4.0f]] };

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateBatchEmbeddingsAsync(["hello", "world"], TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.0f, result[0].Span[0]);
        Assert.Equal(3.0f, result[1].Span[0]);
    }

    [Fact]
    public async Task GenerateBatch_EmptyInput_ReturnsEmptyList()
    {
        var provider = new FakeProvider { Batch = [] };

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateBatchEmbeddingsAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateBatch_MaterializesEnumerable()
    {
        var provider = new FakeProvider { Batch = [[1.0f]] };

        var adapter = new EmbeddingServiceAdapter(provider);
        await adapter.GenerateBatchEmbeddingsAsync(Enumerable.Repeat("text1", 1), TestContext.Current.CancellationToken);

        Assert.Equal(["text1"], provider.Texts);
    }

    private sealed class FakeProvider : IEmbeddingProvider
    {
        public float[] Single { get; init; } = [1.0f];
        public float[][] Batch { get; init; } = [];
        public List<string> Texts { get; } = [];
        public CancellationToken LastToken { get; private set; }

        public string ProviderName => "fake";
        public bool IsAvailable => true;
        public int Dimensions { get; init; }

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Texts.Add(text);
            LastToken = cancellationToken;
            return new(Single);
        }

        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Texts.AddRange(texts);
            LastToken = cancellationToken;
            return new(Batch);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
