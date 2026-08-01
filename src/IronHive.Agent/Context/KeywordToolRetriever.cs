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

        // No query tokens → return AlwaysInclude tools only
        if (queryTokens.Count == 0)
        {
            return Task.FromResult(SelectAlwaysIncludeOnly(availableTools, options));
        }

        // Score all tools
        var scored = new List<(AITool Tool, string Name, float Score)>(availableTools.Count);
        var scores = new Dictionary<string, float>(availableTools.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var tool in availableTools)
        {
            var name = GetToolName(tool);
            var description = tool is AIFunction func ? func.Description ?? string.Empty : string.Empty;
            var score = CalculateRelevance(queryTokens, name, description);
            scored.Add((tool, name, score));
            scores[name] = score;
        }

        // Build always-include set
        var alwaysIncludeSet = options.AlwaysInclude is { Count: > 0 }
            ? new HashSet<string>(options.AlwaysInclude, StringComparer.OrdinalIgnoreCase)
            : null;

        // Select tools: AlwaysInclude first, then top-scored
        var selected = new List<AITool>();
        var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Always-include tools (regardless of score)
        if (alwaysIncludeSet is not null)
        {
            foreach (var (tool, name, _) in scored)
            {
                if (alwaysIncludeSet.Contains(name) && selectedNames.Add(name))
                {
                    selected.Add(tool);
                }
            }
        }

        // 2. Top-scored tools above threshold
        foreach (var (tool, name, score) in scored.OrderByDescending(x => x.Score))
        {
            if (selected.Count >= options.MaxTools)
            {
                break;
            }

            if (!selectedNames.Add(name))
            {
                continue;
            }

            if (score < options.MinRelevanceScore)
            {
                break;
            }

            selected.Add(tool);
        }

        return Task.FromResult(new ToolRetrievalResult
        {
            SelectedTools = selected,
            RelevanceScores = scores
        });
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
        HashSet<string> queryTokens, string toolName, string toolDescription)
    {
        if (queryTokens.Count == 0)
        {
            return 0f;
        }

        var nameCoverage = Coverage(Tokenize(toolName), queryTokens, allowSubstring: true);
        var descCoverage = Coverage(Tokenize(toolDescription), queryTokens, allowSubstring: false);

        // Name and description are normalised separately on purpose: a single shared denominator
        // buries the name signal under a verbose description, so a well-documented tool would
        // score lower than a sparse one carrying the same name.
        var score = nameCoverage * NameWeight + descCoverage * DescriptionWeight;

        return Math.Min(score, 1.0f);
    }

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

    private static string GetToolName(AITool tool)
    {
        return tool is AIFunction func ? func.Name : tool.GetType().Name;
    }

    private static ToolRetrievalResult SelectAlwaysIncludeOnly(
        IList<AITool> availableTools, ToolRetrievalOptions options)
    {
        if (options.AlwaysInclude is not { Count: > 0 })
        {
            return new ToolRetrievalResult
            {
                SelectedTools = [],
                RelevanceScores = new Dictionary<string, float>()
            };
        }

        var set = new HashSet<string>(options.AlwaysInclude, StringComparer.OrdinalIgnoreCase);
        var selected = availableTools.Where(t => set.Contains(GetToolName(t))).ToList();
        var scores = selected.ToDictionary(GetToolName, _ => 1.0f, StringComparer.OrdinalIgnoreCase);

        return new ToolRetrievalResult
        {
            SelectedTools = selected,
            RelevanceScores = scores
        };
    }
}
