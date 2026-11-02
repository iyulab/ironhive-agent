using System.Globalization;
using System.Text;

namespace IronHive.Agent.Mode;

/// <summary>One alternative the model weighed for a tool call that needs approval.</summary>
/// <param name="Id">A short identifier, unique within the proposal.</param>
/// <param name="Label">A short name for the alternative.</param>
/// <param name="Rationale">Why the model weighed it; may be empty.</param>
public sealed record ProposalOption(string Id, string Label, string Rationale = "");

/// <summary>
/// What the <b>model</b> proposed for a tool call that needs approval: the alternatives it weighed, the one it recommends,
/// or - when it could not recommend - the one question it would put to the person. Carried by
/// <see cref="ApprovalRequest.ModelProposal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the model's claim, not a verified fact.</b> The tool, the arguments and the risk assessment of an
/// <see cref="ApprovalRequest"/> are what the pipeline checked; everything here is text the model wrote, which an
/// instruction planted in a tool result or a fetched document can steer. The type says so by its name, and a host shows
/// it apart from the verified fields - never in the same line or style as the tool, the arguments or
/// <see cref="ApprovalRequest.Description"/>.
/// </para>
/// <para>
/// It is information only: the policy verdict is reached before it is read, it never replaces the approval and it never
/// lowers a threshold. Render every string as <b>plain text</b> - never as markup, never as a link, whatever it contains;
/// the library removes control and formatting characters (a right-to-left override cannot reorder the line) but does not
/// try to detect links.
/// </para>
/// <para>
/// The shape is closed: at most <see cref="MaxOptions"/> options, each string capped (see the constants). Build one with
/// <see cref="TryCreate"/>, which returns false - and no proposal - for a structural fault (too many options, a missing or
/// repeated id, a recommendation that names no option) and cuts an over-long string to its cap with <c>...</c>.
/// </para>
/// </remarks>
public sealed record ModelAuthoredProposal
{
    /// <summary>The most options a proposal carries.</summary>
    public const int MaxOptions = 5;

    /// <summary>The longest an option id is, in characters.</summary>
    public const int MaxOptionIdLength = 32;

    /// <summary>The longest an option label is, in characters.</summary>
    public const int MaxLabelLength = 80;

    /// <summary>The longest an option rationale is, in characters.</summary>
    public const int MaxRationaleLength = 400;

    /// <summary>The longest the criterion question is, in characters.</summary>
    public const int MaxQuestionLength = 200;

    private const string Ellipsis = "...";

    private ModelAuthoredProposal(IReadOnlyList<ProposalOption> options, string? recommendedOptionId, string? criterionQuestion)
    {
        Options = options;
        RecommendedOptionId = recommendedOptionId;
        CriterionQuestion = criterionQuestion;
    }

    /// <summary>The alternatives the model weighed; may be empty when there is only a question.</summary>
    public IReadOnlyList<ProposalOption> Options { get; }

    /// <summary>The id of the option the model recommends, or null when it makes no recommendation.</summary>
    public string? RecommendedOptionId { get; }

    /// <summary>The one question the model would put to the person when it cannot recommend, or null.</summary>
    public string? CriterionQuestion { get; }

    /// <summary>
    /// Builds a proposal from what the model wrote, normalizing its text. Returns false, with no proposal, when the shape is
    /// invalid or there is nothing to show.
    /// </summary>
    /// <param name="options">The alternatives; at most <see cref="MaxOptions"/>, each with an id and a label.</param>
    /// <param name="recommendedOptionId">The recommended option's id, or null; must name one of <paramref name="options"/>.</param>
    /// <param name="criterionQuestion">The question for the person, or null.</param>
    /// <param name="proposal">The proposal when this returns true.</param>
    public static bool TryCreate(
        IEnumerable<ProposalOption>? options,
        string? recommendedOptionId,
        string? criterionQuestion,
        out ModelAuthoredProposal? proposal) =>
        TryBuild(options, recommendedOptionId, criterionQuestion, out proposal, out _);

    /// <summary>As <see cref="TryCreate"/>, saying why a proposal was refused (an ASCII reason for a log line).</summary>
    internal static bool TryBuild(
        IEnumerable<ProposalOption>? options,
        string? recommendedOptionId,
        string? criterionQuestion,
        out ModelAuthoredProposal? proposal,
        out string? rejection)
    {
        proposal = null;
        rejection = null;

        // One past the cap is enough to know it is exceeded - a runaway list is never enumerated to its end.
        var given = options?.Take(MaxOptions + 1).ToList() ?? [];
        if (given.Count > MaxOptions)
        {
            rejection = "more options than the cap";
            return false;
        }

        var cleaned = new List<ProposalOption>(given.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in given)
        {
            if (option is null)
            {
                rejection = "an option is null";
                return false;
            }

            var id = Clean(option.Id, MaxOptionIdLength);
            if (id.Length == 0)
            {
                rejection = "an option has no id";
                return false;
            }

            var label = Clean(option.Label, MaxLabelLength);
            if (label.Length == 0)
            {
                rejection = "an option has no label";
                return false;
            }

            if (!seen.Add(id))
            {
                rejection = "two options share an id";
                return false;
            }

            cleaned.Add(new ProposalOption(id, label, Clean(option.Rationale, MaxRationaleLength)));
        }

        string? recommended = null;
        var recommendedText = Clean(recommendedOptionId, MaxOptionIdLength);
        if (recommendedText.Length > 0)
        {
            recommended = cleaned.FirstOrDefault(o => string.Equals(o.Id, recommendedText, StringComparison.OrdinalIgnoreCase))?.Id;
            if (recommended is null)
            {
                rejection = "the recommended id names no option";
                return false;
            }
        }

        var question = Clean(criterionQuestion, MaxQuestionLength);
        if (cleaned.Count == 0 && question.Length == 0)
        {
            rejection = "no options and no question";
            return false;
        }

        proposal = new ModelAuthoredProposal(cleaned, recommended, question.Length == 0 ? null : question);
        return true;
    }

    /// <summary>
    /// Plain text only: whitespace and control characters become one space, formatting characters (bidirectional marks,
    /// zero-width joiners), private-use and unassigned code points are dropped, and a string over <paramref name="maxLength"/>
    /// is cut to it ending in <c>...</c>. Lengths count Unicode scalar values, so a character is never split.
    /// </summary>
    internal static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var runes = new List<Rune>(Math.Min(text.Length, maxLength + Ellipsis.Length + 1));
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    pendingSpace = runes.Count > 0;
                    continue;
                case UnicodeCategory.Format:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.Surrogate:
                    continue;
            }

            if (pendingSpace)
            {
                runes.Add(new Rune(' '));
                pendingSpace = false;
            }

            runes.Add(rune);
        }

        if (runes.Count > maxLength)
        {
            var keep = maxLength - Ellipsis.Length;
            runes.RemoveRange(keep, runes.Count - keep);
            while (runes.Count > 0 && runes[^1] == new Rune(' '))
            {
                runes.RemoveAt(runes.Count - 1);
            }

            runes.AddRange(Ellipsis.Select(c => new Rune(c)));
        }

        var builder = new StringBuilder(runes.Count);
        foreach (var rune in runes)
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
