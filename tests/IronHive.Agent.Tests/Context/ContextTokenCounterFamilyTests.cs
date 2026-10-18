using IndexThinking.Tokenization;
using IronHive.Agent.Context;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// <see cref="ContextTokenCounter"/> counts with the ratios of its model's tokenizer family, so the budgets a context manager
/// measures for a local Gemma or Qwen model do not undercount the JSON tool results that fill a small window.
/// </summary>
public class ContextTokenCounterFamilyTests
{
    private const string JsonResult = "{\"items\":[{\"id\":1042,\"path\":\"src/a.cs\",\"size\":3381},{\"id\":1043,\"path\":\"src/b.cs\",\"size\":902}],\"total\":2}";

    [Theory]
    [InlineData("gguf:qwen3-fast")]
    [InlineData("gemma-4-E4B-it")]
    public void A_local_model_counts_json_with_its_family_ratios(string model)
    {
        var counter = new ContextTokenCounter(model, 8192);

        Assert.Equal(ApproximateTokenCounter.ForModel(model).Count(JsonResult), counter.CountTokens(JsonResult));
        Assert.True(counter.CountTokens(JsonResult) > new ContextTokenCounter("gpt-4o").CountTokens(JsonResult));
    }

    [Fact]
    public void A_cloud_model_keeps_the_o200k_fitted_count()
    {
        Assert.Equal(new ApproximateTokenCounter().Count(JsonResult), new ContextTokenCounter("gpt-4o").CountTokens(JsonResult));
    }
}
