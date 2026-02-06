using MemoryIndexer.Interfaces;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Memory;

/// <summary>
/// Adapts Microsoft.Extensions.AI's IChatClient to MemoryIndexer's ITextCompletionService.
/// </summary>
public class TextCompletionServiceAdapter : ITextCompletionService
{
    private readonly IChatClient _chatClient;

    public TextCompletionServiceAdapter(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(
        string prompt,
        TextCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var chatOptions = MapOptions(options);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        return response.Text ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> CompleteBatchAsync(
        IEnumerable<string> prompts,
        TextCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        var chatOptions = MapOptions(options);

        foreach (var prompt in prompts)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, prompt)
            };

            var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            results.Add(response.Text ?? string.Empty);
        }

        return results;
    }

    private static ChatOptions? MapOptions(TextCompletionOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return new ChatOptions
        {
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxTokens,
            TopP = options.TopP,
            PresencePenalty = options.PresencePenalty,
            FrequencyPenalty = options.FrequencyPenalty,
            StopSequences = options.StopSequences?.ToList()
        };
    }
}
