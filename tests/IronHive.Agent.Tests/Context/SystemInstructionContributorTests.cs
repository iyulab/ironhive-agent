using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// A consumer used to have one way to tell the agent something more: replace the whole system prompt,
/// which meant copying the host's default prompt and watching the copy go stale. A contributor adds a
/// section instead. These facts pin what that has to mean: the prompt stays, nothing changes when
/// nobody contributes, sections do not pile up turn over turn, and an over-budget warning names the
/// section that caused it.
/// </summary>
public class SystemInstructionContributorTests
{
    private const string HostPrompt = "You are a file agent.\n## Tool Usage Guidelines\nRead before you write.";

    private static ContextManager Manager(params ISystemInstructionContributor[] contributors)
        => new(new ContextTokenCounter("gpt-4o"), instructionContributors: contributors);

    private static List<ChatMessage> History() =>
    [
        new(ChatRole.System, HostPrompt),
        new(ChatRole.User, "list the files"),
    ];

    [Fact]
    public async Task AContribution_IsAddedAfterTheSystemPrompt_WhichStaysAsItWas()
    {
        var manager = Manager(new Fixed("house-rules", "## House rules\nNever touch the archive folder."));

        var prepared = await manager.PrepareHistoryAsync(History(), TestContext.Current.CancellationToken);

        Assert.Equal(HostPrompt, prepared[0].Text);
        Assert.Equal(ChatRole.System, prepared[1].Role);
        Assert.Equal("## House rules\nNever touch the archive folder.", prepared[1].Text);
        Assert.Equal(ChatRole.User, prepared[2].Role);
    }

    [Fact]
    public async Task WithNoContributor_ThePreparedHistoryIsTheSameMessages()
    {
        var history = History();

        var prepared = await Manager().PrepareHistoryAsync(history, TestContext.Current.CancellationToken);

        Assert.Equal(history.Select(m => (m.Role, m.Text)), prepared.Select(m => (m.Role, m.Text)));
    }

    [Fact]
    public async Task AContributorWithNothingToSay_AddsNothing()
    {
        var prepared = await Manager(new Fixed("quiet", null), new Fixed("blank", "   "))
            .PrepareHistoryAsync(History(), TestContext.Current.CancellationToken);

        Assert.Equal(2, prepared.Count);
    }

    [Fact]
    public async Task Contributors_ApplyInOrder_AndTheScratchpadComesAfterThem()
    {
        var scratchpad = new Scratchpad();
        scratchpad.CurrentPlan = "inspect, then edit";
        var manager = new ContextManager(
            new ContextTokenCounter("gpt-4o"),
            scratchpad: scratchpad,
            instructionContributors: [new Fixed("first", "FIRST"), new Fixed("second", "SECOND")]);

        var prepared = await manager.PrepareHistoryAsync(History(), TestContext.Current.CancellationToken);

        Assert.Equal("FIRST", prepared[1].Text);
        Assert.Equal("SECOND", prepared[2].Text);
        Assert.Contains("inspect, then edit", prepared[3].Text, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, prepared[4].Role);
    }

    [Fact]
    public async Task SectionsDoNotPileUp_WhenTheCallerStoresThePreparedHistory()
    {
        // The agent loops write the prepared history back into their own and prepare it again next turn.
        var counter = new Counting();
        var manager = Manager(counter);
        IReadOnlyList<ChatMessage> history = History();

        for (var turn = 0; turn < 3; turn++)
        {
            history = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);
            history = [.. history, new ChatMessage(ChatRole.Assistant, "ok"), new ChatMessage(ChatRole.User, "again")];
        }

        var final = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);

        Assert.Single(final, m => m.Role == ChatRole.System && m.Text.StartsWith("turn ", StringComparison.Ordinal));
        Assert.Contains(final, m => m.Text == "turn 4"); // recomputed each time, so it is the current text
    }

    [Fact]
    public async Task TheScratchpadBlock_DoesNotPileUpEither()
    {
        var scratchpad = new Scratchpad();
        scratchpad.CurrentPlan = "PLAN-MARKER";
        var manager = new ContextManager(new ContextTokenCounter("gpt-4o"), scratchpad: scratchpad);
        IReadOnlyList<ChatMessage> history = History();

        for (var turn = 0; turn < 3; turn++)
        {
            history = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);
        }

        Assert.Single(history, m => m.Text.Contains("PLAN-MARKER", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverBudgetWarning_NamesTheSectionThatCausedIt()
    {
        var small = new ContextManager(
            new ContextTokenCounter("gpt-4o", maxContextTokens: 200),
            instructionContributors: [new Fixed("skill-catalogue", string.Join(' ', Enumerable.Repeat("skill", 400)))]);

        var warning = small.ValidateContextFit(History());

        Assert.NotNull(warning);
        Assert.True(warning.IsOverBudget);
        Assert.Equal("skill-catalogue", warning.Sections[0].Name);
        Assert.Contains(warning.Sections, s => s.Name == "system prompt");
        Assert.Equal(warning.SystemPromptTokens, warning.Sections.Sum(s => s.Tokens));
    }

    [Fact]
    public void WithoutContributions_TheSameHistoryFits()
    {
        // The control for the fact above: it is the contribution, not the prompt, that breaks the budget.
        var small = new ContextManager(new ContextTokenCounter("gpt-4o", maxContextTokens: 200));

        Assert.Null(small.ValidateContextFit(History()));
    }

    [Fact]
    public async Task AContribution_IsStillThere_AfterTheHistoryWasCompacted()
    {
        // A window small enough that the long history below has to be compacted.
        var counter = new ContextTokenCounter("gpt-4o", maxContextTokens: 600);
        var manager = new ContextManager(
            counter,
            new ThresholdCompactionTrigger(0.5f),
            new HistoryCompactor(counter),
            instructionContributors: [new Fixed("house-rules", "HOUSE-RULES")]);

        var history = History();
        for (var i = 0; i < 40; i++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, $"step {i}: " + new string('x', 120)));
            history.Add(new ChatMessage(ChatRole.User, $"continue {i}"));
        }

        var prepared = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);

        Assert.True(prepared.Count < history.Count, "the control: compaction really ran");
        Assert.Single(prepared, m => m.Text == "HOUSE-RULES");
        Assert.Equal(HostPrompt, prepared[0].Text);
    }

    [Fact]
    public void AContributorRegisteredInTheContainer_ReachesTheContextManager()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        IronHive.Agent.Extensions.AgentServiceCollectionExtensions.AddIronHiveAgent(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<ISystemInstructionContributor>(services, new Fixed("from-di", "FROM-DI"));

        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var manager = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ContextManager>(provider);

        Assert.Equal(["from-di"], manager.InstructionContributors.Select(c => c.Name));
    }

    private sealed class Fixed(string name, string? text) : ISystemInstructionContributor
    {
        public string Name => name;

        public string? GetInstructions() => text;
    }

    private sealed class Counting : ISystemInstructionContributor
    {
        private int _calls;

        public string Name => "counter";

        public string? GetInstructions() => $"turn {++_calls}";
    }
}
