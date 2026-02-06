using System.ComponentModel;
using IronHive.Agent.SubAgent;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tools;

/// <summary>
/// Tool for spawning sub-agents.
/// </summary>
public class SubAgentTool
{
    private readonly ISubAgentService _subAgentService;

    public SubAgentTool(ISubAgentService subAgentService)
    {
        _subAgentService = subAgentService ?? throw new ArgumentNullException(nameof(subAgentService));
    }

    /// <summary>
    /// Spawns an exploration sub-agent for read-only tasks.
    /// </summary>
    [Description("Spawn an exploration sub-agent for read-only tasks like searching code, understanding architecture, or gathering information. The sub-agent has access to read_file, list_directory, glob, and grep tools only.")]
    public async Task<string> Explore(
        [Description("The task to perform (e.g., 'find all usages of the Config class')")] string task,
        [Description("Optional additional context to help the sub-agent")] string? context = null)
    {
        if (!_subAgentService.CanSpawn(SubAgentType.Explore))
        {
            return "Error: Cannot spawn sub-agent due to depth or concurrency limits.";
        }

        var result = await _subAgentService.ExploreAsync(task, context);
        return FormatResult(result);
    }

    /// <summary>
    /// Spawns a general sub-agent for complex multi-step tasks.
    /// </summary>
    [Description("Spawn a general sub-agent for complex multi-step tasks that require full tool access. Use sparingly for tasks that truly need multi-step execution.")]
    public async Task<string> General(
        [Description("The task to perform")] string task,
        [Description("Optional additional context to help the sub-agent")] string? context = null)
    {
        if (!_subAgentService.CanSpawn(SubAgentType.General))
        {
            return "Error: Cannot spawn sub-agent due to depth or concurrency limits.";
        }

        var result = await _subAgentService.GeneralAsync(task, context);
        return FormatResult(result);
    }

    /// <summary>
    /// Gets the AI tools for sub-agent functionality.
    /// </summary>
    public IList<AITool> GetAITools()
    {
        return
        [
            AIFunctionFactory.Create(Explore),
            AIFunctionFactory.Create(General)
        ];
    }

    private static string FormatResult(SubAgentResult result)
    {
        if (result.Success)
        {
            return $"""
                Sub-agent completed successfully.
                Duration: {result.Duration.TotalSeconds:F1}s
                Turns: {result.TurnsUsed}

                Result:
                {result.Output}
                """;
        }
        else
        {
            return $"""
                Sub-agent failed.
                Duration: {result.Duration.TotalSeconds:F1}s
                Turns: {result.TurnsUsed}

                Error: {result.Error}
                """;
        }
    }
}
