using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// Retrieval aliases in scripts where a whole-word rule never matches a real request: Hangul attaches particles and
/// endings to the word, and Han and Kana write words without spaces. English keeps the whole-word rule.
/// </summary>
public class ToolRetrievalAliasScriptTests
{
    private static readonly ToolRetrievalOptions Options = new() { MaxTools = 3, MinRelevanceScore = 0.25f };

    private readonly KeywordToolRetriever _retriever = new();

    private static List<AITool> Catalogue() =>
    [
        AIFunctionFactory.Create(() => "ok", "read_file", "Read the contents of a file."),
        AIFunctionFactory.Create(() => "ok", "transcribe", "Convert the speech in an audio or video file to text.")
            .WithRetrievalHints(aliases: ["transcript", "subtitles", "captions", "전사", "받아쓰기", "자막", "녹취", "文字起こし"]),
    ];

    private async Task<List<string>> SelectedAsync(string query) =>
        (await _retriever.RetrieveAsync(query, Catalogue(), Options, TestContext.Current.CancellationToken))
            .SelectedTools.Select(t => t.Name).ToList();

    [Theory]
    [InlineData("이 동영상의 대화를 모두 텍스트로 전사해줘")]
    [InlineData("회의 녹음 자막을 만들어 줘")]
    [InlineData("この動画を文字起こししてください")]
    public async Task AnAliasWithAttachedParticlesOrNoSpaces_SelectsTheTool(string query)
    {
        Assert.Contains("transcribe", await SelectedAsync(query));
    }

    [Fact]
    public async Task WithoutTheAlias_TheSameRequestDoesNotReachTheTool()
    {
        // Positive control: the English description alone does not match the Korean request.
        var retriever = new KeywordToolRetriever();
        List<AITool> bare =
        [
            AIFunctionFactory.Create(() => "ok", "transcribe", "Convert the speech in an audio or video file to text."),
        ];

        var result = await retriever.RetrieveAsync("이 동영상의 대화를 모두 텍스트로 전사해줘", bare, Options, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("transcribe", result.SelectedTools.Select(t => t.Name));
    }

    [Fact]
    public void EnglishAliases_StayWholeWord()
    {
        var query = KeywordToolRetriever.Tokenize("please output the report");

        Assert.False(KeywordToolRetriever.MatchesAnyAlias(query, ["put"]), "\"put\" must not match inside \"output\"");
        Assert.True(KeywordToolRetriever.MatchesAnyAlias(KeywordToolRetriever.Tokenize("make subtitles for talk.mp4"), ["subtitles"]));
    }

    [Fact]
    public void AHangulAlias_MatchesAWordItBegins_NotOneItEndsOrSitsInside()
    {
        Assert.True(KeywordToolRetriever.MatchesAnyAlias(KeywordToolRetriever.Tokenize("전사해줘"), ["전사"]));
        Assert.False(KeywordToolRetriever.MatchesAnyAlias(KeywordToolRetriever.Tokenize("재전사"), ["전사"]),
            "a Hangul alias matches the start of a word, where the stem is");
    }

    [Fact]
    public void ASingleCharacterAlias_NeverMatchesByPrefix()
    {
        Assert.False(KeywordToolRetriever.MatchesAnyAlias(KeywordToolRetriever.Tokenize("전체 목록"), ["전"]));
    }
}
