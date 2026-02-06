using System.Runtime.CompilerServices;
using Ironbees.Core;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// Adapter that connects Microsoft.Extensions.AI IChatClient to Ironbees ILLMFrameworkAdapter.
/// This enables ironbees multi-agent orchestration to use IChatClient-based providers.
/// </summary>
public class ChatClientFrameworkAdapter : ILLMFrameworkAdapter
{
    private readonly Func<ModelConfig, IChatClient> _clientFactory;

    /// <summary>
    /// Creates a new ChatClientFrameworkAdapter.
    /// </summary>
    /// <param name="clientFactory">Factory function to create IChatClient from ModelConfig.</param>
    public ChatClientFrameworkAdapter(Func<ModelConfig, IChatClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <summary>
    /// Creates a new ChatClientFrameworkAdapter with a single shared client.
    /// </summary>
    /// <param name="chatClient">The shared IChatClient instance.</param>
    public ChatClientFrameworkAdapter(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        _clientFactory = _ => chatClient;
    }

    /// <inheritdoc />
    public Task<IAgent> CreateAgentAsync(AgentConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var chatClient = _clientFactory(config.Model);
        var agent = new ChatClientAgent(config, chatClient);

        return Task.FromResult<IAgent>(agent);
    }

    /// <inheritdoc />
    public async Task<string> RunAsync(IAgent agent, string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (agent is not ChatClientAgent chatAgent)
        {
            throw new InvalidOperationException(
                $"Agent must be created by this adapter. Expected ChatClientAgent, got {agent.GetType().Name}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, chatAgent.Config.SystemPrompt),
            new(ChatRole.User, input)
        };

        var options = CreateChatOptions(chatAgent.Config.Model);
        var response = await chatAgent.ChatClient.GetResponseAsync(messages, options, cancellationToken);

        return response.Text ?? string.Empty;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IAgent agent,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (agent is not ChatClientAgent chatAgent)
        {
            throw new InvalidOperationException(
                $"Agent must be created by this adapter. Expected ChatClientAgent, got {agent.GetType().Name}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, chatAgent.Config.SystemPrompt),
            new(ChatRole.User, input)
        };

        var options = CreateChatOptions(chatAgent.Config.Model);

        await foreach (var update in chatAgent.ChatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    private static ChatOptions CreateChatOptions(ModelConfig model)
    {
        return new ChatOptions
        {
            ModelId = model.Deployment,
            Temperature = (float)model.Temperature,
            MaxOutputTokens = model.MaxTokens
        };
    }
}

/// <summary>
/// Internal IAgent implementation backed by IChatClient.
/// </summary>
internal sealed class ChatClientAgent : IAgent
{
    public string Name { get; }
    public string Description { get; }
    public AgentConfig Config { get; }
    public IChatClient ChatClient { get; }

    public ChatClientAgent(AgentConfig config, IChatClient chatClient)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Name = config.Name;
        Description = config.Description;
    }
}
