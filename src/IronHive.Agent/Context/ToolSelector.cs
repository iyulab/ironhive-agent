using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// The selection every <see cref="IToolRetriever"/> in this library shares once its tools are scored:
/// pins, then tools the query names exactly, then the scored tail above the threshold, then the declared
/// companions of everything selected. Retrievers differ only in how they score.
/// </summary>
/// <remarks>
/// The groups decide which tools are selected and are kept, in that order, in
/// <see cref="ToolRetrievalResult.Selections"/>. The order the tools are sent in is a separate contract:
/// ordinal by name, so a given set always serialises identically whatever the scores, the query, or the
/// order the catalog arrived in. A prefix-cached server re-reads the whole prompt when the tool list
/// differs, and a chat template that renders tools before the system text turns even a reordering of the
/// same set into a full cache miss.
/// </remarks>
internal static class ToolSelector
{
    /// <summary>A scored tool. <paramref name="AliasMatched"/> marks a score that came from a declared alias.</summary>
    internal readonly record struct Candidate(AITool Tool, string Name, float Score, bool AliasMatched = false);

    private static readonly char[] WordSeparators = [' ', '\t', '\r', '\n'];

    private static readonly char[] WordPunctuation =
        ['.', ',', ';', ':', '!', '?', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>'];

    public static ToolRetrievalResult Select(
        string query,
        IReadOnlyList<Candidate> candidates,
        IList<AITool> availableTools,
        ToolRetrievalOptions options,
        IReadOnlyDictionary<string, float> scores)
    {
        var selected = new List<AITool>();
        var selections = new List<ToolSelection>();
        var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(AITool tool, string name, ToolSelectionReason reason, float? score)
        {
            selected.Add(tool);
            selections.Add(new ToolSelection(name, reason, score));
        }

        // 1. Pins, regardless of score.
        if (options.AlwaysInclude is { Count: > 0 })
        {
            var pinned = new HashSet<string>(options.AlwaysInclude, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (pinned.Contains(candidate.Name) && selectedNames.Add(candidate.Name))
                {
                    Add(candidate.Tool, candidate.Name, ToolSelectionReason.Pinned, null);
                }
            }
        }

        // The scored tail reserves at least MinScoredSlots regardless of how many pins already consumed the
        // nominal MaxTools budget — pins do not count against it. See ToolSelectionBudget.
        var scoredBudget = ToolSelectionBudget.ScoredBudget(options, selected.Count);
        var scoredCount = 0;

        // 2. Tools the query names by their exact name take the first scored slots, regardless of score. A
        // user naming a tool is the strongest signal there is; every named tool is kept even past the budget.
        var queryWords = QueryWords(query);
        if (queryWords.Count > 0)
        {
            foreach (var candidate in candidates)
            {
                if (queryWords.Contains(candidate.Name) && selectedNames.Add(candidate.Name))
                {
                    Add(candidate.Tool, candidate.Name, ToolSelectionReason.ExactName, candidate.Score);
                    scoredCount++;
                }
            }
        }

        // 3. The scored tail above the threshold.
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            if (scoredCount >= scoredBudget || candidate.Score < options.MinRelevanceScore)
            {
                break;
            }

            if (!selectedNames.Add(candidate.Name))
            {
                continue;
            }

            Add(candidate.Tool, candidate.Name,
                candidate.AliasMatched ? ToolSelectionReason.Alias : ToolSelectionReason.Scored,
                candidate.Score);
            scoredCount++;
        }

        // 4. Companions of everything selected, outside the budget, one level deep.
        var byName = new Dictionary<string, AITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in availableTools)
        {
            byName.TryAdd(tool.Name, tool);
        }

        foreach (var owner in selected.ToList())
        {
            foreach (var companion in ToolRetrievalHints.GetCompanions(owner).Take(ToolRetrievalHints.MaxCompanionsPerTool))
            {
                if (byName.TryGetValue(companion, out var tool) && selectedNames.Add(tool.Name))
                {
                    Add(tool, tool.Name, ToolSelectionReason.Companion,
                        scores.TryGetValue(tool.Name, out var score) ? score : null);
                }
            }
        }

        // 5. Tools sent earlier in the conversation, first-sent order, while the whole set fits the limit.
        var carried = Carry(options, byName, selected);
        foreach (var name in carried)
        {
            if (selectedNames.Add(name))
            {
                Add(byName[name], name, ToolSelectionReason.Carried, scores.TryGetValue(name, out var score) ? score : null);
            }
        }

        var carriedOrder = new HashSet<string>(carried, StringComparer.OrdinalIgnoreCase);
        var wire = carried.Select(name => selected.First(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Concat(selected.Where(tool => !carriedOrder.Contains(tool.Name)).OrderBy(tool => tool.Name, StringComparer.Ordinal))
            .ToList();

        return new ToolRetrievalResult
        {
            SelectedTools = wire,
            RelevanceScores = scores,
            Selections = selections,
        };
    }

    /// <summary>
    /// The carried tools still in the catalog, in first-sent order — or none, when sticky selection is off or the
    /// carried tools and this request's selection together would exceed <see cref="ToolRetrievalOptions.StickyToolLimit"/>
    /// (the selection then starts over from this request).
    /// </summary>
    private static List<string> Carry(
        ToolRetrievalOptions options, Dictionary<string, AITool> byName, List<AITool> selected)
    {
        if (options.StickyToolLimit <= 0 || options.StickyTools is not { Count: > 0 })
        {
            return [];
        }

        var carried = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in options.StickyTools)
        {
            if (byName.TryGetValue(name, out var tool) && seen.Add(tool.Name))
            {
                carried.Add(tool.Name);
            }
        }

        var added = selected.Count(tool => !seen.Contains(tool.Name));
        return carried.Count + added <= options.StickyToolLimit ? carried : [];
    }

    /// <summary>
    /// The query's whitespace-separated words with surrounding punctuation removed — the form a tool name takes
    /// when a user writes it ("use GrepFiles.", "call `restore_file_version`").
    /// </summary>
    private static HashSet<string> QueryWords(string query)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = part.Trim(WordPunctuation);
            if (word.Length > 0)
            {
                words.Add(word);
            }
        }

        return words;
    }
}
