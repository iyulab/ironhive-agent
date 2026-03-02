using System.Runtime.CompilerServices;
using Ironbees.Core;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// Bridges IChatClientFactory to Ironbees' ILLMFrameworkAdapter.
/// Enables Ironbees agents to use IChatClientFactory-configured LLM providers
/// with provider name normalization and full ChatOptions mapping.
/// </summary>
public sealed partial class ChatClientLLMAdapter : ILLMFrameworkAdapter
{
    private readonly IChatClientFactory _clientFactory;
    private readonly ILogger<ChatClientLLMAdapter> _logger;

    public ChatClientLLMAdapter(
        IChatClientFactory clientFactory,
        ILogger<ChatClientLLMAdapter> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<IAgent> CreateAgentAsync(
        AgentConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        IAgent agent = new SimpleAgent(config);
        LogAgentCreated(config.Name, config.Model.Provider, config.Model.Deployment);
        return Task.FromResult(agent);
    }

    /// <inheritdoc />
    public async Task<string> RunAsync(
        IAgent agent,
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var client = await GetChatClientAsync(agent.Config.Model, cancellationToken);
        var messages = BuildMessages(agent.Config.SystemPrompt, input);

        var options = BuildChatOptions(agent.Config.Model);
        var response = await client.GetResponseAsync(messages, options, cancellationToken);

        return response.Text ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task<string> RunAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var client = await GetChatClientAsync(agent.Config.Model, cancellationToken);
        var messages = BuildMessages(agent.Config.SystemPrompt, input, conversationHistory);

        var options = BuildChatOptions(agent.Config.Model);
        var response = await client.GetResponseAsync(messages, options, cancellationToken);

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

        var client = await GetChatClientAsync(agent.Config.Model, cancellationToken);
        var messages = BuildMessages(agent.Config.SystemPrompt, input);

        var options = BuildChatOptions(agent.Config.Model);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Text is not null)
            {
                yield return update.Text;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var client = await GetChatClientAsync(agent.Config.Model, cancellationToken);
        var messages = BuildMessages(agent.Config.SystemPrompt, input, conversationHistory);

        var options = BuildChatOptions(agent.Config.Model);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Text is not null)
            {
                yield return update.Text;
            }
        }
    }

    private async Task<IChatClient> GetChatClientAsync(ModelConfig model, CancellationToken ct)
    {
        var provider = NormalizeProviderName(model.Provider);
        return await _clientFactory.CreateAsync(provider, model.Deployment, ct);
    }

    /// <summary>
    /// Normalizes common provider name aliases to canonical names.
    /// For example, "azure-openai" and "gpt" both map to "openai".
    /// </summary>
    public static string NormalizeProviderName(string provider) => provider.ToLowerInvariant() switch
    {
        "azure-openai" or "azureopenai" => "openai",
        "gpt" => "openai",
        "claude" => "anthropic",
        "gemini" => "google",
        _ => provider
    };

    /// <summary>
    /// Builds a message list with system prompt, optional history, and user input.
    /// </summary>
    public static List<ChatMessage> BuildMessages(
        string systemPrompt,
        string input,
        IReadOnlyList<ChatMessage>? history = null)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };

        if (history is not null)
        {
            messages.AddRange(history);
        }

        messages.Add(new ChatMessage(ChatRole.User, input));
        return messages;
    }

    /// <summary>
    /// Builds ChatOptions from ModelConfig, mapping all supported parameters.
    /// </summary>
    public static ChatOptions BuildChatOptions(ModelConfig model) => new()
    {
        Temperature = (float)model.Temperature,
        MaxOutputTokens = model.MaxTokens,
        TopP = model.TopP is not null ? (float)model.TopP.Value : null,
        FrequencyPenalty = model.FrequencyPenalty is not null ? (float)model.FrequencyPenalty.Value : null,
        PresencePenalty = model.PresencePenalty is not null ? (float)model.PresencePenalty.Value : null
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Created agent '{AgentName}' with {Provider}/{Deployment}")]
    private partial void LogAgentCreated(string agentName, string provider, string deployment);

    /// <summary>
    /// Simple IAgent implementation for IChatClientFactory-based agents.
    /// </summary>
    public sealed record SimpleAgent(AgentConfig Config) : IAgent
    {
        public string Name => Config.Name;
        public string Description => Config.Description;
    }
}
