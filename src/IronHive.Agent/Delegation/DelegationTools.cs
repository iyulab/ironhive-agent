using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ironbees.Core;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Delegation;

/// <summary>
/// Turns Ironbees named agents into tools a model can call to delegate a sub-task — each call is an isolated run
/// of that agent (its own tools, prompt and model), whose final answer comes back as the tool result.
/// </summary>
/// <remarks>
/// <para>This is the agent-as-tool form other agent frameworks call composition (an agent exposed as a function
/// another agent invokes). The parent conversation is not shared: the calling model passes the task, and optional
/// context, as arguments.</para>
/// <para>What a delegated run cannot do on its own the caller's settings enforce: nesting depth
/// (<see cref="DelegationOptions.MaxDepth"/>), concurrency, the parent's usage limit, and the tool-turn limit. A
/// refused or failed delegation returns a result that says so — the calling model reads it and can take another
/// route — while cancellation propagates.</para>
/// </remarks>
public static partial class DelegationTools
{
    private static readonly AsyncLocal<int> CurrentDepth = new();

    /// <summary>Creates one delegation tool.</summary>
    public static AIFunction Create(IAgentOrchestrator orchestrator, DelegatedAgent agent, DelegationOptions? options = null)
        => Create(orchestrator, [agent], options)[0];

    /// <summary>
    /// Creates a delegation tool per agent. The tools share one concurrency limit, so a model that fans out across
    /// several agents is bounded as a whole.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(
        IAgentOrchestrator orchestrator, IEnumerable<DelegatedAgent> agents, DelegationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(agents);

        var settings = options ?? new DelegationOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxDepth, 1, nameof(DelegationOptions.MaxDepth));
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxConcurrent, 1, nameof(DelegationOptions.MaxConcurrent));

        var gate = new SemaphoreSlim(settings.MaxConcurrent, settings.MaxConcurrent);
        return agents.Select(a => Build(orchestrator, a, settings, gate)).ToList();
    }

    private static AIFunction Build(
        IAgentOrchestrator orchestrator, DelegatedAgent agent, DelegationOptions settings, SemaphoreSlim gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentName);

        var definition = orchestrator.GetAgent(agent.AgentName)
            ?? throw new InvalidOperationException(
                $"No Ironbees agent named '{agent.AgentName}' is loaded. Add agents/{agent.AgentName}/agent.yaml, " +
                "or remove it from the delegation list.");

        var description = agent.Description ?? definition.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException(
                $"Agent '{agent.AgentName}' has no description, and the model decides whether to delegate from it. " +
                "Give the agent a description in agent.yaml or set DelegatedAgent.Description.");
        }

        async Task<string> DelegateAsync(
            [Description("The sub-task for the agent, stated so it can be done without seeing this conversation.")] string task,
            [Description("Optional facts from this conversation the agent needs.")] string? context = null,
            CancellationToken cancellationToken = default)
        {
            if (CurrentDepth.Value >= settings.MaxDepth)
            {
                return $"Delegation to '{agent.AgentName}' refused: delegation is already {CurrentDepth.Value} level(s) deep " +
                    $"(limit {settings.MaxDepth}). Do this part yourself.";
            }

            if (settings.UsageLimiter?.CheckLimits() is { ShouldStop: true } limit)
            {
                return $"Delegation to '{agent.AgentName}' refused: the session usage limit is reached ({limit.Message}).";
            }

            await gate.WaitAsync(cancellationToken);
            var depth = CurrentDepth.Value;
            CurrentDepth.Value = depth + 1;
            try
            {
                var result = await orchestrator.ProcessStructuredAsync(
                    BuildInput(task, context),
                    new ProcessOptions
                    {
                        AgentName = agent.AgentName,
                        ModelOverride = agent.Model,
                        ThinkingEffort = agent.ThinkingEffort,
                        MaxTokens = agent.MaxTokens,
                        MaxToolTurns = agent.MaxToolTurns,
                    },
                    cancellationToken);

                Account(settings, agent, result.Usage);
                return Format(agent, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"Delegation to '{agent.AgentName}' failed: {ex.Message}";
            }
            finally
            {
                CurrentDepth.Value = depth;
                gate.Release();
            }
        }

        return AIFunctionFactory.Create(DelegateAsync, new AIFunctionFactoryOptions
        {
            Name = agent.ToolName ?? ToToolName(agent.AgentName),
            Description = description,
        });
    }

    private static string BuildInput(string task, string? context)
        => string.IsNullOrWhiteSpace(context) ? task : $"{task}\n\nContext:\n{context}";

    private static string Format(DelegatedAgent agent, AgentRunResult result)
    {
        if (result.TurnLimitReached != true)
        {
            return result.Text;
        }

        var text = new StringBuilder(result.Text);
        if (text.Length > 0)
        {
            text.AppendLine().AppendLine();
        }

        text.Append(CultureInfo.InvariantCulture, $"[Agent '{agent.AgentName}' stopped at its tool-turn limit");
        if (result.TurnsUsed is { } turns)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({turns} turns)");
        }

        text.Append(" before finishing — the answer above is partial.]");
        return text.ToString();
    }

    private static void Account(DelegationOptions settings, DelegatedAgent agent, UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        var tokens = TokenUsage.From(usage)!;
        settings.UsageTracker?.Record(tokens);

        if (settings.UsageLimiter is { } limiter)
        {
            // Priced on the model the delegated agent ran on, not the parent's.
            var pricing = !string.IsNullOrEmpty(agent.Model) ? ModelCatalog.FindModel(agent.Model) : null;
            var cost = tokens.CostAt(pricing) ?? 0m;
            limiter.RecordTokenUsage((int)tokens.TotalTokens, cost);
        }
    }

    private static string ToToolName(string agentName) => InvalidToolNameChars().Replace(agentName, "_");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidToolNameChars();
}
