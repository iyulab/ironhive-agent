namespace IronHive.Agent.Tools;

/// <summary>
/// What a host decides about the built-in file tools: where they may reach, and what runs around a write.
/// </summary>
public sealed class FileToolOptions
{
    /// <summary>
    /// The directories the file tools (<c>ReadFile</c>, <c>WriteFile</c>, <c>ListDirectory</c>,
    /// <c>GlobFiles</c>, <c>GrepFiles</c>) may touch. Empty — the default — means no boundary: absolute
    /// paths and <c>..</c> resolve wherever they point, which is what an agent working across a machine
    /// needs. With one or more roots, a path that resolves outside all of them is refused, and the tool
    /// tells the model why instead of returning content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relative root is resolved against the working directory. The working directory itself is
    /// <b>not</b> a boundary — it is only where relative paths start — so a host that wants the agent
    /// confined to it lists it here.
    /// </para>
    /// <para>
    /// The check is on the resolved path text. It does not follow symbolic links or junctions, so a link
    /// inside a root that points outside it is followed; and <c>ExecuteCommand</c> is a shell, which no
    /// path check confines. A host that needs those closed has to withhold the shell tool and control
    /// what the roots contain.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// Runs around every <c>WriteFile</c>; <see langword="null"/> for none.
    /// </summary>
    public IFileWriteInterceptor? WriteInterceptor { get; init; }
}
