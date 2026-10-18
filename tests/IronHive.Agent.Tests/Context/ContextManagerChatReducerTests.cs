using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// <see cref="ContextManagerChatReducer"/>: a pipeline that takes an <see cref="IChatReducer"/> (M.E.AI <c>UseChatReducer</c>) applies the
/// manager's preparation to what it sends, leaves the caller's list alone, and reducing twice gives the same request.
/// </summary>
public class ContextManagerChatReducerTests
{
    private static readonly string LongResult = new('x', 2_000);

    private static ContextManager MaskingManager()
    {
        var counter = new ContextTokenCounter("gpt-4o");
        var perResult = counter.CountTokens($"section 1: {LongResult}");
        return ContextManager.FromConfig(counter, new CompactionConfig
        {
            EnableObservationMasking = true,
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingProtectedTokens = perResult * 2 + perResult / 2,
            EnableToolResultCompaction = false,
        });
    }

    private static List<ChatMessage> OneTurnWithRounds(int rounds)
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "Read every section and take notes.") };
        for (var r = 1; r <= rounds; r++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{r}", "read_section")]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{r}", $"section {r}: {LongResult}")]));
        }
        return history;
    }

    private static List<string> Results(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result!.ToString()!).ToList();

    [Fact]
    public async Task A_reducing_pipeline_sends_the_managers_preparation_and_keeps_the_callers_list()
    {
        var model = new RecordingClient();
        var client = new ChatClientBuilder(model).UseChatReducer(MaskingManager().AsChatReducer()).Build();
        var history = OneTurnWithRounds(4);

        await client.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        var sent = Results(model.Requests[^1]);
        Assert.StartsWith("[Masked:", sent[0]);
        Assert.StartsWith("[Masked:", sent[1]);
        Assert.StartsWith("section 3:", sent[2]);
        Assert.StartsWith("section 4:", sent[3]);
        Assert.All(Results(history), r => Assert.StartsWith("section", r));
    }

    [Fact]
    public async Task Reducing_a_reduced_list_again_gives_the_same_request()
    {
        var reducer = MaskingManager().AsChatReducer();
        var once = (await reducer.ReduceAsync(OneTurnWithRounds(4), TestContext.Current.CancellationToken)).ToList();

        var twice = (await reducer.ReduceAsync(once, TestContext.Current.CancellationToken)).ToList();

        Assert.Equal(once.Count, twice.Count);
        Assert.Equal(Results(once), Results(twice));
    }

    private sealed class RecordingClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
