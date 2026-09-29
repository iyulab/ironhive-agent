using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// A keyword-based tool retriever that scores tools by token overlap
/// between the query and tool name/description. No external dependencies.
/// </summary>
public class KeywordToolRetriever : IToolRetriever
{
    private const float NameWeight = 0.75f;
    private const float DescriptionWeight = 0.25f;

    /// <summary>
    /// Shortest token allowed to match by substring. A two-character token is a substring of half
    /// the language ("to" in "history", "in" in "tracking"), so shorter tokens must match exactly.
    /// </summary>
    private const int MinSubstringMatchLength = 3;

    /// <inheritdoc />
    public Task<ToolRetrievalResult> RetrieveAsync(
        string query,
        IList<AITool> availableTools,
        ToolRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ToolRetrievalOptions();

        if (availableTools.Count == 0)
        {
            return Task.FromResult(new ToolRetrievalResult
            {
                SelectedTools = [],
                RelevanceScores = new Dictionary<string, float>()
            });
        }

        var queryTokens = Tokenize(query);

        // No query tokens → return AlwaysInclude tools (and their companions) only
        if (queryTokens.Count == 0)
        {
            return Task.FromResult(SelectAlwaysIncludeOnly(query, availableTools, options));
        }

        // Score all tools
        var candidates = new List<ToolSelector.Candidate>(availableTools.Count);
        var scores = new Dictionary<string, float>(availableTools.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var tool in availableTools)
        {
            var name = GetToolName(tool);
            var description = tool.Description ?? string.Empty;
            var aliasMatched = MatchesAnyAlias(queryTokens, ToolRetrievalHints.GetAliases(tool));
            var score = CalculateRelevance(queryTokens, name, description, aliasMatched);
            candidates.Add(new ToolSelector.Candidate(tool, name, score, aliasMatched));
            scores[name] = score;
        }

        return Task.FromResult(ToolSelector.Select(query, candidates, availableTools, options, scores));
    }

    /// <summary>
    /// Calculates relevance score between query tokens and a tool's name + description.
    /// Name matches are weighted higher than description matches.
    /// </summary>
    /// <remarks>
    /// Coverage is measured over the tool's own tokens, never the query's. Dividing by the query
    /// length would answer "what fraction of the query is about this tool", which falls towards
    /// zero as the prompt grows even though the tool's relevance never changed — starving tool
    /// selection exactly when the prompt carries the most instruction.
    /// </remarks>
    internal static float CalculateRelevance(
        HashSet<string> queryTokens, string toolName, string toolDescription, bool aliasMatched = false)
    {
        if (queryTokens.Count == 0)
        {
            return 0f;
        }

        // A query holding one of the tool's declared aliases names the tool as surely as its name would.
        var nameCoverage = aliasMatched ? 1f : Coverage(Tokenize(toolName), queryTokens, allowSubstring: true);
        var descCoverage = Coverage(Tokenize(toolDescription), queryTokens, allowSubstring: false);

        // Name and description are normalised separately on purpose: a single shared denominator
        // buries the name signal under a verbose description, so a well-documented tool would
        // score lower than a sparse one carrying the same name.
        var score = nameCoverage * NameWeight + descCoverage * DescriptionWeight;

        return Math.Min(score, 1.0f);
    }

    /// <summary>
    /// True when the query holds one of <paramref name="aliases"/>: every word of the alias, each as a whole
    /// word. No substring matching — an alias is a word a person chose, and a substring match is what makes
    /// "put" hit "compute".
    /// </summary>
    internal static bool MatchesAnyAlias(HashSet<string> queryTokens, IReadOnlyList<string> aliases)
    {
        foreach (var alias in aliases)
        {
            var words = alias.Split(AliasWordSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && words.All(queryTokens.Contains))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly char[] AliasWordSeparators = [' ', '\t', '-', '_'];

    /// <summary>
    /// Fraction of a tool's own tokens that the query covers.
    /// </summary>
    private static float Coverage(HashSet<string> toolTokens, HashSet<string> queryTokens, bool allowSubstring)
    {
        if (toolTokens.Count == 0)
        {
            return 0f;
        }

        var hits = toolTokens.Count(toolToken =>
            queryTokens.Any(queryToken => Matches(toolToken, queryToken, allowSubstring)));

        return (float)hits / toolTokens.Count;
    }

    private static bool Matches(string toolToken, string queryToken, bool allowSubstring)
    {
        if (string.Equals(toolToken, queryToken, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!allowSubstring
            || toolToken.Length < MinSubstringMatchLength
            || queryToken.Length < MinSubstringMatchLength)
        {
            return false;
        }

        return toolToken.Contains(queryToken, StringComparison.OrdinalIgnoreCase)
            || queryToken.Contains(toolToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tokenizes text into a set of lowercase tokens.
    /// Handles snake_case, camelCase, PascalCase, and separator-delimited text.
    /// </summary>
    internal static HashSet<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var separators = new[] { ' ', '_', '-', '.', ',', '/', '(', ')', '[', ']', '{', '}', ':', ';', '"', '\'' };
        var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (part.Length >= 2)
            {
                tokens.Add(part);
            }

            // Split camelCase/PascalCase
            foreach (var sub in SplitCamelCase(part))
            {
                if (sub.Length >= 2)
                {
                    tokens.Add(sub);
                }
            }
        }

        return tokens;
    }

    private static List<string> SplitCamelCase(string text)
    {
        var parts = new List<string>();
        var start = 0;

        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]))
            {
                parts.Add(text[start..i]);
                start = i;
            }
        }

        if (start < text.Length)
        {
            parts.Add(text[start..]);
        }

        return parts;
    }

    // AITool.Name, not AIFunction.Name: a declaration-only tool (the host runs it) has a name and description too.
    private static string GetToolName(AITool tool) => tool.Name;

    private static ToolRetrievalResult SelectAlwaysIncludeOnly(
        string query, IList<AITool> availableTools, ToolRetrievalOptions options)
    {
        if (options.AlwaysInclude is not { Count: > 0 })
        {
            return new ToolRetrievalResult
            {
                SelectedTools = [],
                RelevanceScores = new Dictionary<string, float>()
            };
        }

        // Only pins are candidates: with nothing to score, the scored tail stays empty.
        var set = new HashSet<string>(options.AlwaysInclude, StringComparer.OrdinalIgnoreCase);
        var pinned = availableTools.Where(t => set.Contains(GetToolName(t)))
            .Select(t => new ToolSelector.Candidate(t, GetToolName(t), 1.0f))
            .ToList();
        var scores = pinned.ToDictionary(c => c.Name, c => c.Score, StringComparer.OrdinalIgnoreCase);

        return ToolSelector.Select(query, pinned, availableTools, options, scores);
    }
}
