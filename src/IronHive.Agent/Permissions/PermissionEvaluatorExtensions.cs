using IronHive.Agent.Tools;

namespace IronHive.Agent.Permissions;

/// <summary>
/// Ways to put a permission evaluator's verdicts to use where no approval question can be asked.
/// </summary>
public static class PermissionEvaluatorExtensions
{
    /// <summary>
    /// The rules for <see cref="FileToolOptions.ReadRules"/> that follow the read rules of <paramref name="evaluator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is shown.</b> A path the rules deny is hidden from what <c>ListDirectory</c>, <c>GlobFiles</c> and
    /// <c>GrepFiles</c> list, match and search; a path they allow or would ask about stays visible (a listing cannot ask a
    /// question per entry, and the <c>ReadFile</c> call that follows does). A directory (a path that ends with a separator) is
    /// hidden only where a rule speaks about it or it lies outside the working directory: Read patterns are written for files,
    /// so "no pattern matches this folder's name" under a default of Deny is no verdict on the folder - hiding it would hide
    /// the files the same rules allow (the tool-call policy reads a directory the same way). A rule that matches every path
    /// (<c>**</c> deny) names every folder too, so it hides them all.
    /// </para>
    /// <para>
    /// <b>Whose content a search returns.</b> Lines of a file the rules allow. A file they would ask about - by a rule or by a
    /// default of <c>Ask</c> - is not read, because <c>ReadFile</c> would put that question to a person first. The one
    /// exception: when the search directory itself had to be asked about, files under it that are asked about on the same
    /// terms - by the same rule, or, outside the working directory, by the same lack of one - are covered by that answer.
    /// The exception takes it that the search was put to the approval gate first, as <see cref="Mode.ToolCallPolicy"/> does.
    /// </para>
    /// </remarks>
    /// <param name="evaluator">The evaluator whose <see cref="IPermissionEvaluator.EvaluateRead"/> decides.</param>
    /// <returns>The read rules of <paramref name="evaluator"/> for the tools that look around.</returns>
    public static FileReadRules ToReadRules(this IPermissionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        return new FileReadRules
        {
            IsVisible = path => IsVisible(evaluator, path),
            MayReturnContent = (searchDirectory, file) =>
            {
                var verdict = evaluator.EvaluateRead(file);
                return verdict.Action == PermissionAction.Allow
                    || (verdict.Action == PermissionAction.Ask
                        && IsUnder(searchDirectory, file)
                        && AskedWithTheSearch(evaluator.EvaluateRead(searchDirectory), verdict));
            },
        };
    }

    private static bool IsVisible(IPermissionEvaluator evaluator, string path)
    {
        var result = evaluator.EvaluateRead(path);
        if (result.Action != PermissionAction.Deny)
        {
            return true;
        }

        var isDirectory = path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
        return isDirectory && result.MatchedRule is null && !result.OutsideWorkingDirectory && !result.NameKeptAsWritten;
    }

    // A pattern such as "../*" climbs out of the folder the search was pointed at; what was asked about is that folder.
    private static bool IsUnder(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Whether the question a person answered for the search directory is the question ReadFile would put about the file. The
    // tool-call policy asks about a search directory only where a rule speaks about it or it lies outside the working
    // directory; inside it, "no rule matched" was never put to anyone.
    private static bool AskedWithTheSearch(PermissionResult directory, PermissionResult file)
    {
        if (directory.Action != PermissionAction.Ask || directory.OutsideWorkingDirectory != file.OutsideWorkingDirectory)
        {
            return false;
        }

        // Two verdicts without a rule are the same question only about ordinary paths: a folder kept as written was asked
        // about because of its name, which says nothing about the files under it.
        if (directory.MatchedRule is null && file.MatchedRule is null)
        {
            return directory.OutsideWorkingDirectory && !directory.NameKeptAsWritten;
        }

        return ReferenceEquals(directory.MatchedRule, file.MatchedRule);
    }
}
