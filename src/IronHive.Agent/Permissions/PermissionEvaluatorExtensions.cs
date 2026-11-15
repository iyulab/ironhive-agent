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
    /// <c>ReadFile</c> call that follows does).
    /// </summary>
    /// <param name="evaluator">The evaluator whose <see cref="IPermissionEvaluator.EvaluateRead"/> decides.</param>
    /// <returns>A filter that is <see langword="false"/> for the paths the read rules deny.</returns>
    public static Func<string, bool> ToReadFilter(this IPermissionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        return path => evaluator.EvaluateRead(path).Action != PermissionAction.Deny;
    }
}
