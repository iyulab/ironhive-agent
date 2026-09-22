using IronHive.Agent.Context;
using IronHive.Agent.Skills;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Skills;

/// <summary>
/// The specification's three disclosure levels, as the agent's own machinery applies them: the
/// metadata is in every prepared turn and the body is not, whatever the body's size; a compaction keeps
/// the metadata (a system message) and trims the body (a tool result). The <c>#371</c> triage claimed
/// the second half needed no work — this is the fact that says so.
/// </summary>
public class SkillsProgressiveDisclosureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skills-disclosure-{Guid.NewGuid():N}");

    public SkillsProgressiveDisclosureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ThePreparedTurn_CarriesTheMetadata_AndItsSizeDoesNotDependOnTheBody()
    {
        var small = Load(bodyChars: 100);
        var large = Load(bodyChars: 200_000);
        var counter = new ContextTokenCounter("gpt-4o");

        var preparedSmall = await new ContextManager(counter, instructionContributors: [small.Contributor])
            .PrepareHistoryAsync(History(), TestContext.Current.CancellationToken);
        var preparedLarge = await new ContextManager(counter, instructionContributors: [large.Contributor])
            .PrepareHistoryAsync(History(), TestContext.Current.CancellationToken);

        var section = Assert.Single(preparedSmall, m => m.Role == ChatRole.System && m.Text.Contains("**pdf-processing**"));
        Assert.Contains("Extract PDF text.", section.Text);
        Assert.Equal(
            counter.CountTokens(preparedSmall),
            counter.CountTokens(preparedLarge));
    }

    [Fact]
    public async Task AfterCompaction_TheMetadataStays_AndTheLoadedBodyIsTrimmed()
    {
        var loader = Load(bodyChars: 200_000);
        var counter = new ContextTokenCounter("gpt-4o");
        var manager = new ContextManager(counter, instructionContributors: [loader.Contributor]);
        var body = (await loader.LoadTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "pdf-processing" }),
            TestContext.Current.CancellationToken))!.ToString()!;

        var history = History();
        history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", SkillsLoader.LoadToolName, new Dictionary<string, object?> { ["name"] = "pdf-processing" })]));
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", body)]));
        var prepared = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);

        var trimmed = new ToolResultCompactor(maxResultChars: 2_000).CompactToolResults(prepared);
        var compacted = await new AnchoredHistoryCompactor(counter).CompactAsync(trimmed, targetTokens: 1_000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(compacted.CompactedHistory, m => m.Role == ChatRole.System && m.Text.Contains("**pdf-processing**"));
        var toolResult = compacted.CompactedHistory.SingleOrDefault(m => m.Role == ChatRole.Tool);
        Assert.True(toolResult is null || toolResult.Text.Length < body.Length / 10, "the loaded body is what compaction trims, never the metadata");
    }

    private SkillsLoader Load(int bodyChars)
    {
        var root = Path.Combine(_dir, $"root-{bodyChars}");
        var d = Path.Combine(root, "pdf-processing");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SKILL.md"), $"---\nname: pdf-processing\ndescription: Extract PDF text.\n---\n# PDF\n{new string('x', bodyChars)}\n");
        return SkillsLoader.Create(new SkillsConfig { Roots = [root] });
    }

    private static List<ChatMessage> History() =>
    [
        new(ChatRole.System, "You are a file agent."),
        new(ChatRole.User, "extract the text of report.pdf"),
    ];
}
