using System.Text;
using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using Xunit;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// A session shaped like the E2E «rotated findings» task, prepared turn by turn through the loop's view: five logs read one per
/// turn (~1.7k tokens each) in an 8k window, the first naming an exception type on its last line. What reaches the model on
/// the last turn, and what the summarizer is shown on the way, must still carry the exception and the paths read.
/// </summary>
public class AnchoredLongSessionTests
{
    private const string Exception = "LeaseQuorumLostException";

    private static string Log(int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 110; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"2026-09-{n:00}T{i / 6 % 24:00}:{i % 60:00}:00Z {(i % 9 == 0 ? "ERROR" : "INFO")} worker-{i % 7} processed batch {n * 1000 + i} in {(i * 37 + n) % 900} ms\n");
        }

        if (n == 1)
        {
            sb.Append($"2026-09-11T23:59:00Z ERROR lease-manager unhandled {Exception}: quorum of 3 lost after 2 renewals\n");
        }

        return sb.ToString();
    }

    private sealed class RecordingSummarizer : IChatClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The user is tracing an outage through logs; counts were reported.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static async Task<(IReadOnlyList<ChatMessage> Sent, int HistoryCount)> RunSessionAsync(IChatClient? summarizer)
    {
        var manager = ContextManager.FromConfig(new ContextTokenCounter("unknown-model", 8_000), new CompactionConfig
        {
            UseAnchoredCompaction = true,
            ProtectRecentTokens = 3_000,
            MinimumPruneTokens = 1_500,
            EnableObservationMasking = false,
        }, summarizer);
        var view = new ModelView();
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "We are tracing an outage through rotating logs. Just confirm you are ready."),
            new(ChatRole.Assistant, "Ready."),
        };
        for (var n = 1; n <= 5; n++)
        {
            history.Add(new ChatMessage(ChatRole.User, "Call next_log, read the log it names and tell me how many lines contain ERROR."));
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"n{n}", "next_log")]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"n{n}", $"logs/shard-{n}.log")]));
            history.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"r{n}", "ReadFile", new Dictionary<string, object?> { ["path"] = $"logs/shard-{n}.log" })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"r{n}", Log(n))]));
            history.Add(new ChatMessage(ChatRole.Assistant, "13"));
            var turn = await manager.PrepareHistoryAsync(view.Compose(history), TestContext.Current.CancellationToken);
            view.Update(turn, history);
        }

        history.Add(new ChatMessage(ChatRole.User, "Now write findings.json with the files you read and the first log's exception."));
        var prepared = await manager.PrepareHistoryAsync(view.Compose(history), TestContext.Current.CancellationToken);
        return (prepared, history.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLastTurn_StillCarriesTheFirstLogsException_AndThePathsRead(bool withSummarizer)
    {
        var (sentMessages, historyCount) = await RunSessionAsync(withSummarizer ? new RecordingSummarizer() : null);

        var sent = string.Join("\n", sentMessages.SelectMany(m => m.Contents).Select(c => c switch
        {
            TextContent t => t.Text,
            FunctionResultContent r => r.Result?.ToString(),
            FunctionCallContent f => $"{f.Name} {string.Join(",", f.Arguments?.Values ?? [])}",
            _ => "",
        }));
        Assert.True(sentMessages.Count < historyCount, "the session was compacted");
        Assert.Contains(Exception, sent, StringComparison.Ordinal);
        Assert.Contains("logs/shard-1.log", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummarizer_SeesTheEndOfALongToolResult_AndTheFactsTheStateBlockKeeps()
    {
        // The exception is the log's last line. A summarizer shown only the head of each result wrote «no error codes
        // occurred» next to a state block that listed it, and the model believed the summary (E2E rotated-findings).
        var summarizer = new RecordingSummarizer();
        await RunSessionAsync(summarizer);

        var first = summarizer.Prompts.First(p => p.Contains("logs/shard-1.log", StringComparison.Ordinal));
        Assert.Contains(Exception, first, StringComparison.Ordinal);
        Assert.Contains("[CONVERSATION STATE]", first, StringComparison.Ordinal);
    }
}
