using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using Xunit;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// What a provider's prompt cache sees across the turns of a loop: each request is prepared from the loop's view (the
/// reduced front kept between turns, <see cref="ModelView"/>), and a request whose start differs from the previous request
/// misses the cache from there. Masking in batches keeps the start the same between batches.
/// </summary>
public class MaskingPrefixStabilityTests
{
    private const string UnknownModel = "unknown-model-for-prefix-tests";

    private static async Task<(int Breaks, int Masked)> RunTurnsAsync(int minimumClearTokens, int turns = 12)
    {
        var manager = ContextManager.ForModel(UnknownModel, new CompactionConfig
        {
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingMinimumClearTokens = minimumClearTokens,
            GoalReminder = new GoalReminderOptions { Enabled = false },
        });
        var view = new ModelView();
        var history = new List<ChatMessage>();
        List<string>? previous = null;
        var breaks = 0;
        IReadOnlyList<ChatMessage> prepared = [];

        for (var t = 1; t <= turns; t++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"Read part {t}"));
            history.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{t}", "read_file", new Dictionary<string, object?> { ["path"] = $"part{t}.txt" })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{t}", new string((char)('a' + t % 26), 2_400))]));
            history.Add(new ChatMessage(ChatRole.Assistant, $"Part {t} read."));

            prepared = await manager.PrepareHistoryAsync(view.Compose(history), TestContext.Current.CancellationToken);
            view.Update(prepared, history);

            var current = prepared.Select(m => $"{m.Role}:{string.Concat(m.Contents.Select(Describe))}").ToList();
            if (previous is not null && !current.Take(previous.Count).SequenceEqual(previous))
            {
                breaks++;
            }

            previous = current;
        }

        var masked = prepared.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Count(r => r.Result?.ToString()?.StartsWith("[Masked:", StringComparison.Ordinal) == true);
        return (breaks, masked);
    }

    private static string Describe(AIContent content) => content switch
    {
        TextContent text => text.Text,
        FunctionCallContent call => $"call {call.CallId}",
        FunctionResultContent result => $"result {result.CallId} {result.Result}",
        _ => content.GetType().Name,
    };

    [Fact]
    public async Task MaskingAtEveryMove_ChangesThePromptStartOnEveryTurn()
    {
        var (breaks, masked) = await RunTurnsAsync(minimumClearTokens: 0);

        Assert.Equal(10, masked);
        Assert.Equal(10, breaks);
    }

    [Fact]
    public async Task MaskingInBatches_ChangesItOnlyAtEachBatch_AndStillMasks()
    {
        var (breaks, masked) = await RunTurnsAsync(minimumClearTokens: 2_000);

        // Ten results cross the boundary over twelve turns (protected 2); a batch is as many results as reach 2,000 tokens
        // (six at ~343 tokens each): each full batch is masked at once and is the only change of the prompt start, and the
        // remainder is still waiting at the end
        var perResult = new ContextTokenCounter(UnknownModel).CountTokens(new string('a', 2_400));
        var batch = (2_000 + perResult - 1) / perResult;
        Assert.InRange(batch, 2, 9);
        Assert.Equal(10 / batch * batch, masked);
        Assert.Equal(10 / batch, breaks);
    }
}
