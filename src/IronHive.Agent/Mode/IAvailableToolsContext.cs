namespace IronHive.Agent.Mode;

/// <summary>
/// Tracks the set of tools currently available in the active agent session.
/// Populated by the agent loop factory after tool filtering (e.g. <see cref="IModeToolFilter.FilterTools"/>);
/// consumed by tool implementations to generate context-aware error messages and guidance.
/// </summary>
/// <remarks>
/// Register as a singleton in DI. The agent loop factory should call
/// <see cref="SetAvailableTools"/> after filtering; tool implementations
/// inject this service and call <see cref="IsAvailable"/> as needed.
/// </remarks>
public interface IAvailableToolsContext
{
    /// <summary>
    /// Returns <c>true</c> if the named tool is available in the current session.
    /// Comparison is case-insensitive.
    /// Returns <c>false</c> if no session is active (tools not yet filtered).
    /// </summary>
    bool IsAvailable(string toolName);

    /// <summary>
    /// The list of tool names available in the current session.
    /// Empty if no session is active.
    /// </summary>
    IReadOnlyList<string> AvailableToolNames { get; }

    /// <summary>
    /// Updates the available tool names.
    /// Called by the agent loop factory after tool filtering.
    /// </summary>
    void SetAvailableTools(IEnumerable<string> toolNames);
}

/// <summary>
/// Default implementation of <see cref="IAvailableToolsContext"/>.
/// </summary>
public sealed class AvailableToolsContext : IAvailableToolsContext
{
    // volatile ensures cross-thread visibility of the reference swap
    private volatile IReadOnlyList<string> _names = [];

    /// <inheritdoc />
    public bool IsAvailable(string toolName) =>
        _names.Any(n => string.Equals(n, toolName, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableToolNames => _names;

    /// <inheritdoc />
    public void SetAvailableTools(IEnumerable<string> toolNames) =>
        _names = [.. toolNames];
}
