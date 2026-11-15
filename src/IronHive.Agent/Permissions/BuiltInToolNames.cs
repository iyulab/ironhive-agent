namespace IronHive.Agent.Permissions;

/// <summary>
/// What the permission rules know about the built-in file tools — one list, read by the evaluator (Planning's read-only
/// test), the default <see cref="PermissionConfig.Tools"/> rules and the default tool-call policy.
/// </summary>
internal static class BuiltInToolNames
{
    // Static initialisers run in textual order: the two lists first, the union after them.

    /// <summary>The built-in tools that look at files. Their verdict also depends on the path they are pointed at, so a file's own Read rules speak for them.</summary>
    private static readonly string[] FileReadOnlyPatterns =
    [
        "ReadFile", "read_file",
        "ListDirectory", "list_directory",
        "GlobFiles", "glob*",
        "GrepFiles", "grep*",
    ];

    /// <summary>
    /// The built-in tools that touch no path: they return text or carry text to a person. A permission file cannot name them, so
    /// with no rule about them they are allowed where the default would only ask (<see cref="PermissionEvaluator.EvaluateTool"/>).
    /// The advisor sends the conversation to a model and returns text — no side effects.
    /// </summary>
    internal static readonly string[] PathFreeReadOnlyPatterns =
    [
        "advisor",
        "retrieve_tool_result",
        "propose_for_approval",
        "ask_user",
    ];

    /// <summary>
    /// Name patterns of the built-in tools that only look, in both spellings the built-ins have shipped under. Delegation tools
    /// are not listed: what they can do depends on the delegated agent's own tools.
    /// </summary>
    internal static readonly IReadOnlyList<string> ReadOnlyPatterns = [.. FileReadOnlyPatterns, .. PathFreeReadOnlyPatterns];

    /// <summary>The built-in file tools whose verdict is decided by the path they are given (read, write, edit, delete, move).</summary>
    internal static bool IsPathTool(string toolName) => Normalize(toolName) is "read_file" or "write_file" or "edit_file" or "delete_file" or "move_file";

    /// <summary>The names a built-in tool has shipped under - what a rule that names it may be written in. A name that is no built-in is its own only spelling.</summary>
    internal static IEnumerable<string> Spellings(string toolName)
    {
        yield return toolName;

        var normalized = Normalize(toolName);
        if (!string.Equals(normalized, toolName, StringComparison.Ordinal))
        {
            yield return normalized;
            yield break;
        }

        var pascal = toolName switch
        {
            "read_file" => "ReadFile",
            "write_file" => "WriteFile",
            "edit_file" => "EditFile",
            "delete_file" => "DeleteFile",
            "move_file" => "MoveFile",
            _ => null
        };
        if (pascal is not null)
        {
            yield return pascal;
        }
    }

    /// <summary>Maps the PascalCase built-in names to the snake_case ones the rules are written for.</summary>
    internal static string Normalize(string toolName) => toolName switch
    {
        "ReadFile" => "read_file",
        "WriteFile" => "write_file",
        "EditFile" => "edit_file",
        "DeleteFile" => "delete_file",
        "MoveFile" => "move_file",
        "ExecuteCommand" => "execute_command",
        "GlobFiles" => "glob_files",
        "GrepFiles" => "grep_files",
        "ListDirectory" => "list_directory",
        _ => toolName
    };
}
