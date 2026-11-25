namespace IronHive.Agent.Tools;

/// <summary>
/// What the tools that look around may do with what they find: which paths they show, and whose content a search may return.
/// Both are required, so that a host cannot decide the first and leave the second open.
/// </summary>
/// <remarks>
/// <see cref="Permissions.PermissionEvaluatorExtensions.ToReadRules"/> builds the rules that follow a permission evaluator's
/// <c>Read</c> rules.
/// </remarks>
public sealed record FileReadRules
{
    /// <summary>
    /// Whether the model may see a path through <c>ListDirectory</c>, <c>GlobFiles</c> and <c>GrepFiles</c>. It is given the
    /// absolute, resolved path of a file or - with a trailing directory separator - of a directory; <see langword="false"/>
    /// hides it. A hidden file is left out of listings, matches and search results, and a hidden directory is not entered.
    /// </summary>
    public required Func<string, bool> IsVisible { get; init; }

    /// <summary>
    /// Whether <c>GrepFiles</c> may return lines of a visible file. It is given the directory the search was pointed at and
    /// the file, both absolute and resolved. <see langword="false"/>: the file is not read, and the result names it as not
    /// searched.
    /// </summary>
    /// <remarks>
    /// A search cannot ask a question per file, and what it returns is content. Where <c>ReadFile</c> of a file would ask a
    /// person, answer <see langword="false"/> unless the search was already asked about on the same terms.
    /// </remarks>
    public required Func<string, string, bool> MayReturnContent { get; init; }
}
