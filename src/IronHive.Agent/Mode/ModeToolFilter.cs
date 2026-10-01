using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mode;

/// <summary>
/// Which tools an agent mode offers and permits. Judging an individual call (Allow / Deny / Ask) is
/// <see cref="IToolCallPolicy"/>'s job; this answers only "may this mode use this tool at all".
/// </summary>
public interface IModeToolFilter
{
    /// <summary>
    /// Filters tools based on the current mode.
    /// </summary>
    /// <param name="tools">All available tools</param>
    /// <param name="mode">Current agent mode</param>
    /// <returns>Tools permitted in the current mode</returns>
    IList<AITool> FilterTools(IList<AITool> tools, AgentMode mode);

    /// <summary>
    /// Checks if a tool is permitted in the current mode. <see cref="ApprovalGateMiddleware"/> enforces the
    /// <see cref="AgentMode.Planning"/> answer for every call when it is given an <see cref="IModeManager"/>.
    /// </summary>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="mode">Current agent mode</param>
    /// <returns>True if the tool is permitted</returns>
    bool IsToolPermitted(string toolName, AgentMode mode);
}

/// <summary>
/// Default <see cref="IModeToolFilter"/>: Planning permits the tools the permission evaluator calls read-only (the
/// built-in read-only tools plus <see cref="PermissionConfig.ReadOnlyTools"/>); Working permits every tool.
/// </summary>
public class ModeToolFilter : IModeToolFilter
{
    private readonly IPermissionEvaluator _permissionEvaluator;

    /// <summary>
    /// Creates a new ModeToolFilter with default configuration.
    /// </summary>
    public ModeToolFilter() : this(new PermissionEvaluator())
    {
    }

    /// <summary>
    /// Creates a new ModeToolFilter with the specified permission evaluator.
    /// </summary>
    public ModeToolFilter(IPermissionEvaluator permissionEvaluator)
    {
        _permissionEvaluator = permissionEvaluator ?? throw new ArgumentNullException(nameof(permissionEvaluator));
    }

    /// <summary>
    /// Creates a new ModeToolFilter with the specified permission configuration.
    /// </summary>
    public ModeToolFilter(PermissionConfig permissionConfig)
        : this(new PermissionEvaluator(permissionConfig))
    {
    }

    /// <inheritdoc />
    public IList<AITool> FilterTools(IList<AITool> tools, AgentMode mode)
    {
        return mode switch
        {
            AgentMode.Idle => [], // No tools in idle
            AgentMode.Planning => tools.Where(t => IsToolPermitted(t.Name, mode)).ToList(),
            AgentMode.Working => tools.ToList(), // All tools in working mode
            AgentMode.HumanInTheLoop => [], // No tools while waiting for approval
            _ => []
        };
    }

    /// <inheritdoc />
    public bool IsToolPermitted(string toolName, AgentMode mode)
    {
        return mode switch
        {
            AgentMode.Idle => false,
            AgentMode.Planning => _permissionEvaluator.IsReadOnlyTool(toolName),
            AgentMode.Working => true, // All tools permitted (each call is still judged by the tool-call policy)
            AgentMode.HumanInTheLoop => false,
            _ => false
        };
    }
}
