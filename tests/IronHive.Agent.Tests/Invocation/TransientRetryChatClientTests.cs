using System.Net;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// A model call that fails for a transient reason is tried again — through a real <see cref="FunctionInvokingChatClient"/>,
/// so «retried» means the turn carries on after a tool round instead of ending on the provider's blip.
/// </summary>
public class TransientRetryChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HttpRequestException BadGateway =
        new("Chat completion request failed with status 502 (Bad Gateway).", null, HttpStatusCode.BadGateway);

    private static readonly HttpRequestException Refused =
        new(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.");

    private static readonly HttpRequestException Unauthorized =
        new("Unauthorized.", null, HttpStatusCode.Unauthorized);

    [Fact]
    public async Task AGatewayBlip_IsRetriedOnce()
    {
        var model = new Scripted([BadGateway], reply: "done");
        var client = new TransientRetryChatClient(model) { MaxDelay = TimeSpan.FromSeconds(5) };

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        response.Text.Should().Be("done");
        model.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AStreamThatFailsBeforeItsFirstUpdate_IsRetried()
    {
        var model = new Scripted([Refused], reply: "done");
        var client = new TransientRetryChatClient(model);

        var text = await Collect(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));

        text.Should().Be("done");
        model.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AStreamThatFailsAfterAnUpdate_IsNotRetried()
    {
        // The caller has seen «par» already; a retry would hand it a second beginning.
        var model = new Scripted([], reply: "partial", failAfterFirstUpdate: new HttpIOException(HttpRequestError.ResponseEnded));
        var client = new TransientRetryChatClient(model);

        var act = () => Collect(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));

        await act.Should().ThrowAsync<HttpIOException>();
        model.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ARefusal_IsNotRetried()
    {
        var model = new Scripted([Unauthorized], reply: "never");
        var client = new TransientRetryChatClient(model);

        var act = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        model.Calls.Should().Be(1);
    }

    [Fact]
    public async Task OnlyOneRetry_ByDefault()
    {
        var model = new Scripted([BadGateway, BadGateway], reply: "never");
        var client = new TransientRetryChatClient(model);

        var act = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        model.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UnderFunctionInvocation_ABlipOnTheSecondModelCall_DoesNotEndTheTurn()
    {
        // Call 1 asks for a tool, call 2 (after the tool ran) fails once at the gateway, its retry answers.
        var tool = AIFunctionFactory.Create(() => "42", "Answer");
        var model = new Scripted([null, BadGateway], reply: "the answer is 42", firstCallTool: "Answer");
        var client = new TransientRetryChatClient(model).AsBuilder().UseFunctionInvocation().Build();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "what is it?")], new ChatOptions { Tools = [tool] }, Ct);

        response.Text.Should().Be("the answer is 42");
        model.Calls.Should().Be(3);
    }

    // The builder extension puts the retry where the doc says — innermost, inside function invocation — when called last.
    [Fact]
    public async Task UseTransientRetry_CalledLast_RetriesEachModelCallInsideFunctionInvocation()
    {
        var tool = AIFunctionFactory.Create(() => "42", "Answer");
        var model = new Scripted([null, BadGateway], reply: "the answer is 42", firstCallTool: "Answer");
        var client = model.AsBuilder().UseFunctionInvocation().UseTransientRetry(maxDelay: TimeSpan.FromSeconds(5)).Build();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "what is it?")], new ChatOptions { Tools = [tool] }, Ct);

        response.Text.Should().Be("the answer is 42");
        model.Calls.Should().Be(3);
        client.GetService<TransientRetryChatClient>()!.MaxDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseTransientRetry_WithZeroRetries_LetsTheFailureThrough()
    {
        var model = new Scripted([BadGateway], reply: "done");
        var client = model.AsBuilder().UseTransientRetry(maxRetries: 0).Build();

        var act = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        model.Calls.Should().Be(1);
    }

    private static async Task<string> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in updates)
        {
            text.Append(update.Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// A model whose calls fail in turn with the scripted exceptions (null = that call succeeds), then answer
    /// <paramref name="reply"/>; the first call asks for <paramref name="firstCallTool"/> when one is given.
    /// </summary>
    private sealed class Scripted(Exception?[] failures, string reply, Exception? failAfterFirstUpdate = null, string? firstCallTool = null)
        : IChatClient
    {
        public int Calls { get; private set; }

        private ChatResponse Next()
        {
            var call = Calls++;
            if (call < failures.Length && failures[call] is { } failure)
            {
                throw failure;
            }

            if (call == 0 && firstCallTool is not null)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", firstCallTool)]));
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Next());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var response = Next();
            if (failAfterFirstUpdate is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text[..3]);
                throw failAfterFirstUpdate;
            }

            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
