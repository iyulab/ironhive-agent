using IronHive.Agent.Providers;

namespace IronHive.Agent.Tests.Providers;

public class AvailableModelInfoTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var info = new AvailableModelInfo
        {
            ModelId = "gpt-4o",
            Provider = "openai"
        };

        Assert.Null(info.DisplayName);
        Assert.Null(info.ContextWindow);
        Assert.Null(info.InputPricePerMillion);
        Assert.Null(info.OutputPricePerMillion);
        Assert.Equal(ModelSource.Api, info.Source);
        Assert.False(info.IsDefault);
        Assert.Null(info.LocalPath);
        Assert.Null(info.SizeBytes);
    }

    [Fact]
    public void ShouldInitialize_WithAllFields()
    {
        var info = new AvailableModelInfo
        {
            ModelId = "claude-sonnet-4-20250514",
            Provider = "anthropic",
            DisplayName = "Claude Sonnet 4",
            ContextWindow = 200000,
            InputPricePerMillion = 3.0m,
            OutputPricePerMillion = 15.0m,
            Source = ModelSource.Static,
            IsDefault = true
        };

        Assert.Equal("claude-sonnet-4-20250514", info.ModelId);
        Assert.Equal(200000, info.ContextWindow);
        Assert.Equal(3.0m, info.InputPricePerMillion);
        Assert.Equal(15.0m, info.OutputPricePerMillion);
        Assert.Equal(ModelSource.Static, info.Source);
        Assert.True(info.IsDefault);
    }

    [Fact]
    public void ShouldInitialize_CachedModel()
    {
        var info = new AvailableModelInfo
        {
            ModelId = "llama-3-8b",
            Provider = "gpustack",
            Source = ModelSource.Cached,
            LocalPath = "/models/llama-3-8b.gguf",
            SizeBytes = 4_500_000_000L
        };

        Assert.Equal(ModelSource.Cached, info.Source);
        Assert.Contains("llama-3-8b", info.LocalPath);
        Assert.Equal(4_500_000_000L, info.SizeBytes);
    }
}

public class ModelSourceEnumTests
{
    [Fact]
    public void ShouldHaveThreeValues()
    {
        Assert.Equal(3, Enum.GetValues<ModelSource>().Length);
    }

    [Theory]
    [InlineData(ModelSource.Api, 0)]
    [InlineData(ModelSource.Cached, 1)]
    [InlineData(ModelSource.Static, 2)]
    public void ShouldHaveExpectedIntValues(ModelSource source, int expected)
    {
        Assert.Equal(expected, (int)source);
    }
}
