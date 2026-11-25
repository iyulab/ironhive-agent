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
    /// The check is on the resolved path text. It does not follow symbolic links or junctions, so a call whose
    /// path goes through a link inside a root reaches wherever the link points (a recursive listing or search does
    /// not enter such a link on its own); and <c>ExecuteCommand</c> is a shell, which no
    /// path check confines. A host that needs those closed has to withhold the shell tool and control
    /// what the roots contain.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// What the tools that look around - <c>ListDirectory</c>, <c>GlobFiles</c> and <c>GrepFiles</c> - may show, and whose
    /// content <c>GrepFiles</c> may return. <see langword="null"/> - the default - hides nothing and searches everything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The permission rules judge a call by the path it is pointed at, not by what lies under it: a search from the working
    /// directory is allowed even when a rule denies reading one folder in it, or asks before reading one file in it. A host
    /// that keeps such rules passes <see cref="Permissions.PermissionEvaluatorExtensions.ToReadRules"/> of the same evaluator
    /// here: what the rules refuse for <c>ReadFile</c> is left out of what the tools list and search, and a search does not
    /// return lines of a file that <c>ReadFile</c> would ask a person about.
    /// </para>
    /// <para>
    /// <c>ReadFile</c>, <c>WriteFile</c>, <c>EditFile</c>, <c>DeleteFile</c> and <c>MoveFile</c> take a path from the model
    /// and are judged on it by the permission rules before they run, so they do not use these rules.
    /// </para>
    /// <para>
    /// The rules judge the text of a path, as <see cref="AllowedRoots"/> does and as the permission rules do for
    /// <c>ReadFile</c>: a file reached through a link is judged by the link's path. With or without them, a recursive
    /// <c>ListDirectory</c>, a <c>GlobFiles</c> and a <c>GrepFiles</c> do not enter a link to a directory below the folder
    /// they start in (a link back up the tree would never end).
    /// </para>
    /// </remarks>
    public FileReadRules? ReadRules { get; init; }

    /// <summary>
    /// Runs around every <c>WriteFile</c>; <see langword="null"/> for none.
    /// </summary>
    public IFileWriteInterceptor? WriteInterceptor { get; init; }
}
