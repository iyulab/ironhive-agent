using IronHive.Agent.Memory;
using NSubstitute;

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
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        provider.Dimensions.Returns(1536);

        var adapter = new EmbeddingServiceAdapter(provider);

        Assert.Equal(1536, adapter.Dimensions);
    }

    [Fact]
    public void Dimensions_ReturnsZeroWhenProviderReturnsZero()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        provider.Dimensions.Returns(0);

        var adapter = new EmbeddingServiceAdapter(provider);

        Assert.Equal(0, adapter.Dimensions);
    }

    // --- GenerateEmbeddingAsync ---

    [Fact]
    public async Task GenerateEmbedding_ReturnsReadOnlyMemoryFromFloatArray()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        provider.EmbedAsync("hello", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateEmbeddingAsync("hello");

        Assert.Equal(3, result.Length);
        Assert.Equal(0.1f, result.Span[0]);
        Assert.Equal(0.2f, result.Span[1]);
        Assert.Equal(0.3f, result.Span[2]);
    }

    [Fact]
    public async Task GenerateEmbedding_EmptyArray_ReturnsEmptyMemory()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        provider.EmbedAsync("", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateEmbeddingAsync("");

        Assert.Equal(0, result.Length);
    }

    [Fact]
    public async Task GenerateEmbedding_PassesCancellationToken()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        var dummy = new float[] { 1.0f };
        provider.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(dummy));

        var adapter = new EmbeddingServiceAdapter(provider);
        using var cts = new CancellationTokenSource();

        await adapter.GenerateEmbeddingAsync("test", cts.Token);

        await provider.Received(1).EmbedAsync("test", cts.Token);
    }

    // --- GenerateBatchEmbeddingsAsync ---

    [Fact]
    public async Task GenerateBatch_ReturnsListOfReadOnlyMemory()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        var vec1 = new float[] { 1.0f, 2.0f };
        var vec2 = new float[] { 3.0f, 4.0f };
        var embeddings = new List<float[]> { vec1, vec2 };
        provider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>(embeddings));

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateBatchEmbeddingsAsync(["hello", "world"]);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.0f, result[0].Span[0]);
        Assert.Equal(3.0f, result[1].Span[0]);
    }

    [Fact]
    public async Task GenerateBatch_EmptyInput_ReturnsEmptyList()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        provider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>(new List<float[]>()));

        var adapter = new EmbeddingServiceAdapter(provider);
        var result = await adapter.GenerateBatchEmbeddingsAsync([]);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateBatch_MaterializesEnumerable()
    {
        var provider = Substitute.For<IAgentEmbeddingProvider>();
        IEnumerable<string>? capturedTexts = null;
        var singleVec = new float[] { 1.0f };
        provider.EmbedBatchAsync(Arg.Do<IEnumerable<string>>(t => capturedTexts = t), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>(new List<float[]> { singleVec }));

        var adapter = new EmbeddingServiceAdapter(provider);
        await adapter.GenerateBatchEmbeddingsAsync(["text1"]);

        // The adapter converts to List<string> before passing
        Assert.NotNull(capturedTexts);
        Assert.IsType<List<string>>(capturedTexts);
    }
}
