using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// A keyword-based tool retriever that scores tools by token overlap
/// between the query and tool name/description. No external dependencies.
/// </summary>
public class KeywordToolRetriever : IToolRetriever
{
    private const float NameWeight = 3.0f;
    private const float DescriptionWeight = 1.0f;

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
    internal static float CalculateRelevance(
        HashSet<string> queryTokens, string toolName, string toolDescription)
    {
        if (queryTokens.Count == 0)
        {
            return 0f;
        }

        var nameTokens = Tokenize(toolName);
        var descTokens = Tokenize(toolDescription);

        float nameHits = 0;
        float descHits = 0;

        foreach (var qt in queryTokens)
        {
            // Name matching: substring check (bidirectional) for flexibility
            if (nameTokens.Any(nt => nt.Contains(qt, StringComparison.OrdinalIgnoreCase)
                                  || qt.Contains(nt, StringComparison.OrdinalIgnoreCase)))
            {
                nameHits++;
            }

            // Description matching: exact token match
            if (descTokens.Contains(qt))
            {
                descHits++;
            }
        }

        var maxScore = queryTokens.Count * (NameWeight + DescriptionWeight);
        var score = (nameHits * NameWeight + descHits * DescriptionWeight) / maxScore;

        return Math.Min(score, 1.0f);
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
