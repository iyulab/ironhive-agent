using System.Runtime.CompilerServices;
using Ironbees.Core;
using Ironbees.Core.Conversation;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// IAgentLoop implementation that delegates to Ironbees IAgentOrchestrator.
/// Enables multi-agent orchestration through the standard IAgentLoop interface.
/// Supports conversation persistence via IConversationStore.
/// </summary>
public class OrchestratedAgentLoop : IAgentLoop
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly string? _preferredAgentName;
    private readonly IConversationStore? _conversationStore;
    private readonly string _conversationId;

    /// <summary>
    /// Creates a new OrchestratedAgentLoop.
    /// </summary>
    /// <param name="orchestrator">The Ironbees orchestrator to use.</param>
    /// <param name="preferredAgentName">Optional agent name to always use instead of auto-selection.</param>
    /// <param name="conversationStore">Optional conversation store for history persistence.</param>
    /// <param name="conversationId">Optional conversation ID. If null, a new ID is generated.</param>
    public OrchestratedAgentLoop(
        IAgentOrchestrator orchestrator,
        string? preferredAgentName = null,
        IConversationStore? conversationStore = null,
        string? conversationId = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _preferredAgentName = preferredAgentName;
        _conversationStore = conversationStore;
        _conversationId = conversationId ?? Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc />
    public async Task<AgentResponse> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var response = _preferredAgentName is not null
            ? await _orchestrator.ProcessAsync(prompt, _preferredAgentName, cancellationToken)
            : await _orchestrator.ProcessAsync(prompt, cancellationToken);

        // Store conversation if store is available
        if (_conversationStore is not null)
        {
            await _conversationStore.AppendMessageAsync(
                _conversationId,
                new ConversationMessage { Role = "user", Content = prompt },
                cancellationToken);
            await _conversationStore.AppendMessageAsync(
                _conversationId,
                new ConversationMessage { Role = "assistant", Content = response },
                cancellationToken);
        }

        return new AgentResponse
        {
            Content = response,
            ToolCalls = []
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var stream = _preferredAgentName is not null
            ? _orchestrator.StreamAsync(prompt, _preferredAgentName, cancellationToken)
            : _orchestrator.StreamAsync(prompt, cancellationToken);

        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            yield return new AgentResponseChunk
            {
                TextDelta = chunk
            };
        }
    }

    /// <summary>
    /// Gets the list of available agents.
    /// </summary>
    public IReadOnlyCollection<string> ListAgents() => _orchestrator.ListAgents();

    /// <summary>
    /// Gets a specific agent by name.
    /// </summary>
    public IAgent? GetAgent(string name) => _orchestrator.GetAgent(name);

    /// <summary>
    /// Selects the best agent for the given input.
    /// </summary>
    public Task<AgentSelectionResult> SelectAgentAsync(string input, CancellationToken cancellationToken = default)
        => _orchestrator.SelectAgentAsync(input, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<ChatMessage> History
        => GetHistoryAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public void ClearHistory()
        => ClearHistoryAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public void InitializeHistory(IEnumerable<ChatMessage> messages)
        => InitializeHistoryAsync(messages).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_conversationStore is null)
        {
            return [];
        }

        var state = await _conversationStore.LoadAsync(_conversationId, cancellationToken);
        if (state is null)
        {
            return [];
        }

        return state.Messages
            .Select(m => m.ToChatMessage())
            .ToList();
    }

    /// <inheritdoc />
    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_conversationStore is not null)
        {
            await _conversationStore.DeleteAsync(_conversationId, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task InitializeHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (_conversationStore is null)
        {
            return;
        }

        foreach (var msg in messages)
        {
            var conversationMsg = ConversationMessage.FromChatMessage(msg);
            await _conversationStore.AppendMessageAsync(_conversationId, conversationMsg, cancellationToken);
        }
    }
}
