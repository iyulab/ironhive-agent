using IronHive.Abstractions.Exceptions;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using IronHive.Extensions.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// The shape of a document-search app on a small served window, on the Agent path: an IronHive message generator behind
/// <see cref="ChatClientAdapter"/> whose window is 8k tokens (it refuses a larger request with
/// <see cref="ContextOverflowException"/>, as a server does), and one user question answered over several rounds of large
/// search results. With in-turn masking every round fits and the answer arrives; without it the same turn overflows.
/// </summary>
public class AimsShapedToolRoundsTests
{
    private const int WindowChars = 32_000;   // ≈ 8k tokens
    private const int Rounds = 6;
    private static readonly string Passage = string.Join(' ', Enumerable.Repeat("the contract term renews annually unless", 250)); // ≈ 10k chars

    [Fact]
    public async Task Large_results_over_many_rounds_stay_inside_the_window_and_the_answer_arrives()
    {
        var generator = new WindowedSearchModel();
        var contextManager = ContextManager.FromConfig(new ContextTokenCounter("gpt-4o"), new CompactionConfig
        {
            ObservationMaskingProtectedTokens = 4_000,
        });
        using var adapter = new ChatClientAdapter(generator.Substitute, "served-8k");
        var client = adapter.AsBuilder().UseFunctionInvocation().UseToolRoundContext(contextManager).Build();
        var loop = new AgentLoop(client, new AgentOptions { Tools = [Search()] }, contextManager: contextManager);

        var response = await loop.RunAsync("When does the contract renew?", TestContext.Current.CancellationToken);

        Assert.Equal("It renews annually.", response.Content);
        Assert.Equal(Rounds + 1, generator.RequestSizes.Count);
        Assert.All(generator.RequestSizes, size => Assert.True(size <= WindowChars, $"a request of {size} chars was sent"));
    }

    [Fact]
    public async Task Without_in_turn_masking_the_same_turn_overflows()
    {
        // The control: the fixture's results really do outgrow the window.
        var generator = new WindowedSearchModel();
        using var adapter = new ChatClientAdapter(generator.Substitute, "served-8k");
        var client = adapter.AsBuilder().UseFunctionInvocation().Build();
        var loop = new AgentLoop(client, new AgentOptions { Tools = [Search()] });

        await Assert.ThrowsAnyAsync<ContextOverflowException>(
            () => loop.RunAsync("When does the contract renew?", TestContext.Current.CancellationToken));
    }

    private static AIFunction Search() => AIFunctionFactory.Create((int page) => $"page {page}: {Passage}", "search_documents");

    /// <summary>Searches page by page for <see cref="Rounds"/> rounds, then answers; refuses any request larger than the window.</summary>
    private sealed class WindowedSearchModel
    {
        public IMessageGenerator Substitute { get; } = NSubstitute.Substitute.For<IMessageGenerator>();

        public List<int> RequestSizes { get; } = [];

        public WindowedSearchModel()
        {
            Substitute.GenerateMessageAsync(Arg.Any<MessageGenerationRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => Respond(call.Arg<MessageGenerationRequest>()));
        }

        private MessageResponse Respond(MessageGenerationRequest request)
        {
            var contents = request.Messages.SelectMany(m => m.Content).ToList();
            var size = contents.Sum(Size);
            RequestSizes.Add(size);
            if (size > WindowChars)
            {
                throw new ContextOverflowException($"The request ({size} chars) exceeds the served window ({WindowChars}).");
            }

            var searched = contents.OfType<ToolMessageContent>().Count(t => t.Output is not null);
            MessageContent reply = searched < Rounds
                ? new ToolMessageContent { IsApproved = true, Id = $"c{searched + 1}", Name = "search_documents", Input = $"{{\"page\":{searched + 1}}}" }
                : new TextMessageContent { Value = "It renews annually." };
            return new MessageResponse
            {
                ResponseId = $"r{RequestSizes.Count}",
                DoneReason = searched < Rounds ? MessageDoneReason.ToolCall : MessageDoneReason.EndTurn,
                Message = new Message { Role = MessageRole.Assistant, Content = { reply } },
            };
        }

        private static int Size(MessageContent content) => content switch
        {
            TextMessageContent text => text.Value?.Length ?? 0,
            ToolMessageContent tool => (tool.Input?.Length ?? 0)
                + (tool.Output?.Content.OfType<TextMessageContent>().Sum(t => t.Value?.Length ?? 0) ?? 0),
            _ => 0,
        };
    }
}
