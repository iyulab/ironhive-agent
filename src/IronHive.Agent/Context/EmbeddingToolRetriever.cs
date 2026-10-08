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
            return SelectAlwaysIncludeOnly(query ?? string.Empty, availableTools, options);
        }

        // Ensure index is built (lazy, rebuild if tool list changed)
        await EnsureIndexAsync(availableTools, cancellationToken);

        // Embed the query on the query side; tool descriptions are the documents it is compared against
        var queryEmbedding = await _embedder.EmbedQueryAsync(query, cancellationToken);

        // Score all tools via cosine similarity
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ToolSelector.Candidate>(availableTools.Count);

        foreach (var tool in availableTools)
        {
            var name = GetToolName(tool);
            var normalizedScore = 0f;
            if (_toolEmbeddings!.TryGetValue(name, out var toolEmb))
            {
                // Normalize from [-1, 1] to [0, 1]
                normalizedScore = (CosineSimilarity(queryEmbedding, toolEmb) + 1f) / 2f;
            }

            scores[name] = normalizedScore;
            candidates.Add(new ToolSelector.Candidate(tool, name, normalizedScore));
        }

        return ToolSelector.Select(query, candidates, availableTools, options, scores);
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

    // AITool.Name, not AIFunction.Name: a declaration-only tool (the host runs it) has a name and description too.
    private static string GetToolName(AITool tool) => tool.Name;

    private static string GetToolText(AITool tool)
    {
        var name = GetToolName(tool);
        var desc = tool.Description ?? string.Empty;

        // Declared aliases are the words people use for the tool; embedding them with the description puts
        // those words in the tool's neighbourhood.
        var aliases = ToolRetrievalHints.GetAliases(tool);
        return aliases.Count == 0 ? $"{name}: {desc}" : $"{name}: {desc} Also: {string.Join(", ", aliases)}.";
    }

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
