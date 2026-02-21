using System.Numerics;
using System.Runtime.InteropServices;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// An embedding-based tool retriever that uses cosine similarity
/// to find the most semantically relevant tools for a given query.
/// Builds an in-memory index of tool description embeddings on first use.
/// </summary>
public class EmbeddingToolRetriever : IToolRetriever
{
    private readonly IEmbeddingProvider _embedder;
    private readonly object _lock = new();

    // Cached index: tool name → embedding vector
    private Dictionary<string, float[]>? _toolEmbeddings;
    private IList<AITool>? _indexedTools;

    public EmbeddingToolRetriever(IEmbeddingProvider embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <inheritdoc />
    public async Task<ToolRetrievalResult> RetrieveAsync(
        string query,
        IList<AITool> availableTools,
        ToolRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ToolRetrievalOptions();

        if (availableTools.Count == 0)
        {
            return new ToolRetrievalResult
            {
                SelectedTools = [],
                RelevanceScores = new Dictionary<string, float>()
            };
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return SelectAlwaysIncludeOnly(availableTools, options);
        }

        // Ensure index is built (lazy, rebuild if tool list changed)
        await EnsureIndexAsync(availableTools, cancellationToken);

        // Embed the query
        var queryEmbedding = await _embedder.EmbedAsync(query, cancellationToken);

        // Score all tools via cosine similarity
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var scored = new List<(AITool Tool, string Name, float Score)>(availableTools.Count);

        foreach (var tool in availableTools)
        {
            var name = GetToolName(tool);
            if (_toolEmbeddings!.TryGetValue(name, out var toolEmb))
            {
                var score = CosineSimilarity(queryEmbedding, toolEmb);
                // Normalize from [-1, 1] to [0, 1]
                var normalizedScore = (score + 1f) / 2f;
                scores[name] = normalizedScore;
                scored.Add((tool, name, normalizedScore));
            }
            else
            {
                scores[name] = 0f;
                scored.Add((tool, name, 0f));
            }
        }

        // Build always-include set
        var alwaysIncludeSet = options.AlwaysInclude is { Count: > 0 }
            ? new HashSet<string>(options.AlwaysInclude, StringComparer.OrdinalIgnoreCase)
            : null;

        // Select tools
        var selected = new List<AITool>();
        var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Always-include tools
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

        return new ToolRetrievalResult
        {
            SelectedTools = selected,
            RelevanceScores = scores
        };
    }

    /// <summary>
    /// Forces a rebuild of the tool embedding index.
    /// </summary>
    public async Task RebuildIndexAsync(IList<AITool> tools, CancellationToken cancellationToken = default)
    {
        await BuildIndexAsync(tools, cancellationToken);
    }

    private async Task EnsureIndexAsync(IList<AITool> tools, CancellationToken cancellationToken)
    {
        // Simple change detection: reference equality + count
        bool needsRebuild;
        lock (_lock)
        {
            needsRebuild = _toolEmbeddings is null
                        || _indexedTools is null
                        || !ReferenceEquals(_indexedTools, tools)
                        || _indexedTools.Count != tools.Count;
        }

        if (needsRebuild)
        {
            await BuildIndexAsync(tools, cancellationToken);
        }
    }

    private async Task BuildIndexAsync(IList<AITool> tools, CancellationToken cancellationToken)
    {
        var names = new List<string>(tools.Count);
        var texts = new List<string>(tools.Count);

        foreach (var tool in tools)
        {
            var name = GetToolName(tool);
            var text = GetToolText(tool);
            names.Add(name);
            texts.Add(text);
        }

        var embeddings = await _embedder.EmbedBatchAsync(texts, cancellationToken);

        var index = new Dictionary<string, float[]>(names.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            index[names[i]] = embeddings[i];
        }

        lock (_lock)
        {
            _toolEmbeddings = index;
            _indexedTools = tools;
        }
    }

    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// Uses SIMD acceleration when available.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;
        }

        var spanA = a.AsSpan();
        var spanB = b.AsSpan();

        float dot = 0, normA = 0, normB = 0;

        // SIMD path
        var simdLength = Vector<float>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && a.Length >= simdLength)
        {
            var vecDot = Vector<float>.Zero;
            var vecNormA = Vector<float>.Zero;
            var vecNormB = Vector<float>.Zero;

            var floatsA = MemoryMarshal.Cast<float, Vector<float>>(spanA);
            var floatsB = MemoryMarshal.Cast<float, Vector<float>>(spanB);

            for (var v = 0; v < floatsA.Length; v++)
            {
                vecDot += floatsA[v] * floatsB[v];
                vecNormA += floatsA[v] * floatsA[v];
                vecNormB += floatsB[v] * floatsB[v];
            }

            dot = Vector.Dot(vecDot, Vector<float>.One);
            normA = Vector.Dot(vecNormA, Vector<float>.One);
            normB = Vector.Dot(vecNormB, Vector<float>.One);

            i = floatsA.Length * simdLength;
        }

        // Scalar remainder
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }

    private static string GetToolName(AITool tool)
    {
        return tool is AIFunction func ? func.Name : tool.GetType().Name;
    }

    private static string GetToolText(AITool tool)
    {
        var name = GetToolName(tool);
        var desc = tool is AIFunction func ? func.Description ?? string.Empty : string.Empty;
        return $"{name}: {desc}";
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
