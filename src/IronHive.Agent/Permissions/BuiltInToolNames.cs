namespace IronHive.Agent.Permissions;

/// <summary>
/// What the permission rules know about the built-in file tools — one list, read by the evaluator (Planning's read-only
/// test), the default <see cref="PermissionConfig.Tools"/> rules and the default tool-call policy.
/// </summary>
internal static class BuiltInToolNames
{
    /// <summary>
    /// Name patterns of the built-in tools that only look, in both spellings the built-ins have shipped under. The advisor
    /// sends the conversation to a model and returns text — no side effects. Delegation tools are not listed: what they can
    /// do depends on the delegated agent's own tools.
    /// </summary>
    internal static readonly IReadOnlyList<string> ReadOnlyPatterns =
    [
        "ReadFile", "read_file",
        "ListDirectory", "list_directory",
        "GlobFiles", "glob*",
        "GrepFiles", "grep*",
        "advisor",
    ];

    /// <summary>Maps the PascalCase built-in names to the snake_case ones the rules are written for.</summary>
    internal static string Normalize(string toolName) => toolName switch
    {
        "ReadFile" => "read_file",
        "WriteFile" => "write_file",
        "DeleteFile" => "delete_file",
        "MoveFile" => "move_file",
        "ExecuteCommand" => "execute_command",
        "GlobFiles" => "glob_files",
        "GrepFiles" => "grep_files",
        "ListDirectory" => "list_directory",
        _ => toolName
    };
}
