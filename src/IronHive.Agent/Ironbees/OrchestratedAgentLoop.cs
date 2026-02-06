using System.Runtime.CompilerServices;
using Ironbees.Core;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// IAgentLoop implementation that delegates to Ironbees IAgentOrchestrator.
/// Enables multi-agent orchestration through the standard IAgentLoop interface.
/// </summary>
public class OrchestratedAgentLoop : IAgentLoop
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly string? _preferredAgentName;

    /// <summary>
    /// Creates a new OrchestratedAgentLoop.
    /// </summary>
    /// <param name="orchestrator">The Ironbees orchestrator to use.</param>
    /// <param name="preferredAgentName">Optional agent name to always use instead of auto-selection.</param>
    public OrchestratedAgentLoop(IAgentOrchestrator orchestrator, string? preferredAgentName = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _preferredAgentName = preferredAgentName;
    }

    /// <inheritdoc />
    public async Task<AgentResponse> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        string response;

        if (!string.IsNullOrEmpty(_preferredAgentName))
        {
            response = await _orchestrator.ProcessAsync(prompt, _preferredAgentName, cancellationToken);
        }
        else
        {
            response = await _orchestrator.ProcessAsync(prompt, cancellationToken);
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

        IAsyncEnumerable<string> stream;

        if (!string.IsNullOrEmpty(_preferredAgentName))
        {
            stream = _orchestrator.StreamAsync(prompt, _preferredAgentName, cancellationToken);
        }
        else
        {
            stream = _orchestrator.StreamAsync(prompt, cancellationToken);
        }

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
    /// <remarks>
    /// OrchestratedAgentLoop does not support history management.
    /// The orchestrator manages its own internal state.
    /// </remarks>
    public IReadOnlyList<ChatMessage> History => [];

    /// <inheritdoc />
    /// <remarks>
    /// OrchestratedAgentLoop does not support history management.
    /// </remarks>
    public void ClearHistory()
    {
        // No-op: orchestrator manages its own state
    }

    /// <inheritdoc />
    /// <remarks>
    /// OrchestratedAgentLoop does not support session restoration.
    /// Use standard AgentLoop or ThinkingAgentLoop for session support.
    /// </remarks>
    public void InitializeHistory(IEnumerable<ChatMessage> messages)
    {
        // No-op: orchestrator manages its own state
        // Consider logging a warning if messages are non-empty
    }
}
