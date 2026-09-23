using System.Runtime.CompilerServices;
using Ironbees.Core;
using Ironbees.Core.Streaming;
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
        // Ironbees 0.10.0 made ModelConfig.Deployment nullable: null means "resolve at runtime from
        // IronbeesCoreOptions.DefaultModelDeployment". Say that in the log rather than an empty slot.
        LogAgentCreated(config.Name, config.Model.Provider, config.Model.Deployment ?? "(runtime-resolved)");
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

    /// <inheritdoc />
    /// <remarks>
    /// Carries the response's <see cref="AgentRunResult.Usage"/>. Before, this fell through to the interface default,
    /// which returned the text alone although the chat response reported token usage. Per-invoke options are refused
    /// exactly as the default refuses them.
    /// </remarks>
    public async Task<AgentRunResult> RunStructuredAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        RefuseUnsupported(options);

        var client = await GetChatClientAsync(agent.Config.Model, cancellationToken);
        var messages = BuildMessages(agent.Config.SystemPrompt, input, conversationHistory);
        var response = await client.GetResponseAsync(messages, BuildChatOptions(agent.Config.Model), cancellationToken);

        return new AgentRunResult { Text = response.Text ?? string.Empty, Usage = response.Usage };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Emits the reasoning the model streams as <see cref="ThinkingChunk"/>, its token usage as <see cref="UsageChunk"/>,
    /// and the finish reason on the closing <see cref="CompletionChunk"/>. Before, this fell through to the interface
    /// default, which forwarded text only.
    /// </remarks>
    public IAsyncEnumerable<StreamChunk> StreamStructuredAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        RefuseUnsupported(options);
        return StreamStructuredCoreAsync(agent, input, conversationHistory, cancellationToken);
    }

    private async IAsyncEnumerable<StreamChunk> StreamStructuredCoreAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await GetChatClientAsync(agent.Config.Model, cancellationToken);
        var messages = BuildMessages(agent.Config.SystemPrompt, input, conversationHistory);
        string? finishReason = null;

        await foreach (var update in client.GetStreamingResponseAsync(messages, BuildChatOptions(agent.Config.Model), cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        yield return new ThinkingChunk(reasoning.Text);
                        break;
                    case UsageContent usage:
                        yield return new UsageChunk(
                            (int)(usage.Details.InputTokenCount ?? 0),
                            (int)(usage.Details.OutputTokenCount ?? 0),
                            usage.Details.TotalTokenCount is { } total ? (int)total : null);
                        break;
                }
            }

            if (update.Text is { Length: > 0 } text)
            {
                yield return new TextChunk(text);
            }

            if (update.FinishReason is { } reason)
            {
                finishReason = reason.Value;
            }
        }

        yield return new CompletionChunk(FinishReason: finishReason);
    }

    /// <summary>The per-invoke options this adapter does not honour — the same set the interface default refuses.</summary>
    private static void RefuseUnsupported(AgentRunOptions? options)
    {
        if (options is null)
        {
            return;
        }

        var unsupported = new[]
        {
            (options.Suggestions is not null, nameof(AgentRunOptions.Suggestions)),
            (options.ThinkingEffort is not null, nameof(AgentRunOptions.ThinkingEffort)),
            (options.Tools is not null, nameof(AgentRunOptions.Tools)),
            (options.MaxTokens is not null, nameof(AgentRunOptions.MaxTokens)),
            (options.MaxToolTurns is not null, nameof(AgentRunOptions.MaxToolTurns)),
        }.FirstOrDefault(o => o.Item1);

        if (unsupported.Item1)
        {
            throw new NotSupportedException(
                $"{nameof(ChatClientLLMAdapter)} does not support AgentRunOptions.{unsupported.Item2}.");
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
