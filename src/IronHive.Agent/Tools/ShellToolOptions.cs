namespace IronHive.Agent.Tools;

/// <summary>
/// What a host decides about the built-in shell tool (<c>ExecuteCommand</c>): which of its own environment variables the
/// commands the model runs may not see.
/// </summary>
public sealed class ShellToolOptions
{
    /// <summary>
    /// Environment variables removed from every command's environment. Empty — the default — means a command inherits the
    /// host process's whole environment, as a command typed in the host's own shell would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host that holds secrets in its environment for its own use — a provider API key, a database password — lists
    /// them here: a command such as <c>env</c> or <c>printenv</c> would otherwise print them into the tool result, where
    /// they reach the model, its transcript and the logs. Everything not listed (<c>PATH</c>, <c>HOME</c>, the variables a
    /// build or a script needs) is still inherited.
    /// </para>
    /// <para>
    /// Names are compared the way the operating system compares them: case-insensitively on Windows, exactly elsewhere.
    /// Removing a variable hides its value from the command; it does not confine the command, which can still read any
    /// file the host can.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> RemoveEnvironmentVariables { get; init; } = [];
}
