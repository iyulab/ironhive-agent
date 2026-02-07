using System.Diagnostics;
using Ironbees.Core;

namespace IronHive.Agent.SubAgent;

/// <summary>
/// Default implementation of ISubAgentService.
/// Delegates to Ironbees IAgentOrchestrator for agent execution.
/// </summary>
public sealed class SubAgentService : ISubAgentService, IDisposable
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly SubAgentConfig _config;
    private readonly string? _parentId;

    private int _currentDepth;
    private int _runningCount;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    public SubAgentService(
        IAgentOrchestrator orchestrator,
        SubAgentConfig? config = null,
        string? parentId = null,
        int currentDepth = 0)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _config = config ?? new SubAgentConfig();
        _parentId = parentId;
        _currentDepth = currentDepth;
        _semaphore = new SemaphoreSlim(_config.MaxConcurrent);
    }

    /// <inheritdoc />
    public int CurrentDepth => _currentDepth;

    /// <inheritdoc />
    public int RunningCount => _runningCount;

    /// <inheritdoc />
    public bool CanSpawn(SubAgentType type)
    {
        // Check depth limit
        if (_currentDepth >= _config.MaxDepth)
        {
            return false;
        }

        // Check concurrency limit
        if (_runningCount >= _config.MaxConcurrent)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public Task<SubAgentResult> ExploreAsync(
        string task,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var agentContext = SubAgentContext.Create(
            SubAgentType.Explore,
            task,
            context,
            _currentDepth + 1,
            _parentId);

        // Override with config values
        agentContext = agentContext with
        {
            MaxTurns = _config.Explore.MaxTurns,
            MaxTokens = _config.Explore.MaxTokens
        };

        return SpawnAsync(agentContext, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SubAgentResult> GeneralAsync(
        string task,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var agentContext = SubAgentContext.Create(
            SubAgentType.General,
            task,
            context,
            _currentDepth + 1,
            _parentId);

        // Override with config values
        agentContext = agentContext with
        {
            MaxTurns = _config.General.MaxTurns,
            MaxTokens = _config.General.MaxTokens
        };

        return SpawnAsync(agentContext, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SubAgentResult> SpawnAsync(
        SubAgentContext agentContext,
        CancellationToken cancellationToken = default)
    {
        if (!CanSpawn(agentContext.Type))
        {
            return SubAgentResult.Failed(
                agentContext,
                $"Cannot spawn sub-agent: depth limit ({_config.MaxDepth}) or concurrency limit ({_config.MaxConcurrent}) exceeded");
        }

        await _semaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _runningCount);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var agentName = agentContext.Type == SubAgentType.Explore ? "explore" : "general";

            var prompt = BuildUserPrompt(agentContext);
            var options = new ProcessOptions
            {
                AgentName = agentName,
                MaxHistoryTurns = agentContext.MaxTurns
            };

            var result = await _orchestrator.ProcessAsync(prompt, options, cancellationToken);

            stopwatch.Stop();

            if (string.IsNullOrEmpty(result))
            {
                return SubAgentResult.Failed(
                    agentContext,
                    "Sub-agent returned empty response",
                    duration: stopwatch.Elapsed);
            }

            return SubAgentResult.Succeeded(
                agentContext,
                result,
                duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return SubAgentResult.Failed(
                agentContext,
                "Sub-agent execution was cancelled",
                duration: stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return SubAgentResult.Failed(
                agentContext,
                $"Sub-agent error: {ex.Message}",
                duration: stopwatch.Elapsed);
        }
        finally
        {
            Interlocked.Decrement(ref _runningCount);
            _semaphore.Release();
        }
    }

    private static string BuildUserPrompt(SubAgentContext context)
    {
        var prompt = $"Task: {context.Task}";

        if (!string.IsNullOrWhiteSpace(context.AdditionalContext))
        {
            prompt += $"\n\nContext:\n{context.AdditionalContext}";
        }

        return prompt;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            _disposed = true;
        }
    }
}
