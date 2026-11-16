namespace IronHive.Agent.Permissions;

/// <summary>
/// Ways to put a permission evaluator's verdicts to use where no approval question can be asked.
/// </summary>
public static class PermissionEvaluatorExtensions
{
    /// <summary>
    /// The filter for <see cref="Tools.FileToolOptions.ReadFilter"/> that follows the read rules of <paramref name="evaluator"/>:
    /// a path the rules deny is hidden from what <c>ListDirectory</c>, <c>GlobFiles</c> and <c>GrepFiles</c> list, match and
    /// search; a path they allow or would ask about stays visible (a listing cannot ask a question per entry, and the
    /// <c>ReadFile</c> call that follows does). A directory (a path that ends with a separator) is hidden only where a rule
    /// speaks about it or it lies outside the working directory: Read patterns are written for files, so "no pattern matches
    /// this folder's name" under a default of Deny is no verdict on the folder - hiding it would hide the files the same rules
    /// allow (the tool-call policy reads a directory the same way). A rule that matches every path (<c>**</c> deny) names every
    /// folder too, so it hides them all.
    /// </summary>
    /// <param name="evaluator">The evaluator whose <see cref="IPermissionEvaluator.EvaluateRead"/> decides.</param>
    /// <returns>A filter that is <see langword="false"/> for the paths the read rules deny.</returns>
    public static Func<string, bool> ToReadFilter(this IPermissionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        return path =>
        {
            var result = evaluator.EvaluateRead(path);
            if (result.Action != PermissionAction.Deny)
            {
                return true;
            }

            var isDirectory = path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
            return isDirectory && result.MatchedRule is null && !result.OutsideWorkingDirectory;
        };
    }
}
