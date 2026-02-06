using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Mocks;

/// <summary>
/// Mock implementation of IChatClient for testing agent behavior
/// without actual LLM calls.
/// </summary>
public class MockChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();
    private readonly List<List<ChatMessage>> _receivedMessages = [];

    /// <summary>
    /// Gets the messages received by the mock client.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages => _receivedMessages;

    /// <summary>
    /// Enqueues a response to be returned by the next call.
    /// </summary>
    public MockChatClient EnqueueResponse(string content, UsageDetails? usage = null)
    {
        var message = new ChatMessage(ChatRole.Assistant, content);
        var response = new ChatResponse([message])
        {
            Usage = usage
        };
        _responses.Enqueue(response);
        return this;
    }

    /// <summary>
    /// Enqueues a response with a tool call.
    /// </summary>
    public MockChatClient EnqueueToolCallResponse(string toolName, string arguments, string? textContent = null)
    {
        var contents = new List<AIContent>();

        if (!string.IsNullOrEmpty(textContent))
        {
            contents.Add(new TextContent(textContent));
        }

        // Parse arguments string as dictionary
        var argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)
            ?? new Dictionary<string, object?>();

        contents.Add(new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: toolName,
            arguments: argsDict));

        var message = new ChatMessage(ChatRole.Assistant, contents);
        var response = new ChatResponse([message]);
        _responses.Enqueue(response);
        return this;
    }

    /// <summary>
    /// Enqueues an error response.
    /// </summary>
    public MockChatClient EnqueueError(Exception exception)
    {
        // Store exception to throw later
        _pendingException = exception;
        return this;
    }

    private Exception? _pendingException;

    public ChatClientMetadata Metadata { get; } = new("MockChatClient", null, "mock-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _receivedMessages.Add(messages.ToList());

        if (_pendingException is not null)
        {
            var ex = _pendingException;
            _pendingException = null;
            throw ex;
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No mock responses queued. Call EnqueueResponse() before GetResponseAsync().");
        }

        return Task.FromResult(_responses.Dequeue());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent textContent)
                {
                    // Simulate streaming by yielding character by character
                    foreach (var chunk in ChunkText(textContent.Text ?? string.Empty, 10))
                    {
                        yield return new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new TextContent(chunk)]
                        };

                        await Task.Delay(1, cancellationToken); // Simulate network latency
                    }
                }
                else if (content is FunctionCallContent functionCall)
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [functionCall]
                    };
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient))
        {
            return this;
        }

        return null;
    }

    public void Dispose()
    {
        // Nothing to dispose
        GC.SuppressFinalize(this);
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
        }
    }

    /// <summary>
    /// Resets the mock client to its initial state.
    /// </summary>
    public void Reset()
    {
        _responses.Clear();
        _receivedMessages.Clear();
        _pendingException = null;
    }
}
