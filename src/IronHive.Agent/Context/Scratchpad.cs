using System.Text;

namespace IronHive.Agent.Context;

/// <summary>
/// External working memory for agent conversations.
/// Stores structured state (plan, observations, key facts) outside the conversation
/// context window, injected on demand to prevent context bloat while preserving
/// important intermediate reasoning results.
/// </summary>
public class Scratchpad
{
    /// <summary>Marker for the start of a scratchpad context block.</summary>
    public const string BlockStart = "[SCRATCHPAD]";

    /// <summary>Marker for the end of a scratchpad context block.</summary>
    public const string BlockEnd = "[END SCRATCHPAD]";

    private readonly int _maxChars;
    private readonly int _maxObservations;

    /// <summary>
    /// Creates a new scratchpad with the specified size limits.
    /// </summary>
    /// <param name="maxChars">Maximum characters for the context block output. Default: 2000.</param>
    /// <param name="maxObservations">Maximum number of observations to keep. Default: 20.</param>
    public Scratchpad(int maxChars = 2000, int maxObservations = 20)
    {
        _maxChars = maxChars;
        _maxObservations = maxObservations;
    }

    /// <summary>
    /// Current execution plan (free-text description).
    /// </summary>
    public string? CurrentPlan { get; set; }

    /// <summary>
    /// Current step index in the plan (0-based).
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    /// Notable observations made during execution.
    /// Oldest observations are evicted when <see cref="_maxObservations"/> is exceeded.
    /// </summary>
    public List<string> Observations { get; } = [];

    /// <summary>
    /// Key-value pairs of important facts discovered during execution.
    /// </summary>
    public Dictionary<string, string> KeyFacts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the scratchpad has any content worth injecting.
    /// </summary>
    public bool HasContent =>
        CurrentPlan is not null ||
        Observations.Count > 0 ||
        KeyFacts.Count > 0;

    /// <summary>
    /// Adds an observation. Evicts the oldest observation if the limit is exceeded.
    /// </summary>
    public void AddObservation(string observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observation);

        Observations.Add(observation);

        // Evict oldest if over limit
        while (Observations.Count > _maxObservations)
        {
            Observations.RemoveAt(0);
        }
    }

    /// <summary>
    /// Sets a key fact. Overwrites if the key already exists.
    /// </summary>
    public void SetFact(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        KeyFacts[key] = value;
    }

    /// <summary>
    /// Removes a key fact.
    /// </summary>
    public bool RemoveFact(string key)
    {
        return KeyFacts.Remove(key);
    }

    /// <summary>
    /// Clears all scratchpad content.
    /// </summary>
    public void Clear()
    {
        CurrentPlan = null;
        CurrentStep = 0;
        Observations.Clear();
        KeyFacts.Clear();
    }

    /// <summary>
    /// Formats the scratchpad as a context block for injection into conversation history.
    /// Truncates output to <see cref="_maxChars"/> if necessary.
    /// </summary>
    public string ToContextBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine(BlockStart);

        if (CurrentPlan is not null)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"Plan (step {CurrentStep}):");
            sb.AppendLine(CurrentPlan);
        }

        if (KeyFacts.Count > 0)
        {
            sb.AppendLine("Key facts:");
            foreach (var (key, value) in KeyFacts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"  {key}: {value}");
            }
        }

        if (Observations.Count > 0)
        {
            sb.AppendLine("Observations:");
            foreach (var observation in Observations)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"  - {observation}");
            }
        }

        sb.Append(BlockEnd);

        var result = sb.ToString();
        if (result.Length > _maxChars)
        {
            return result[.._maxChars] + "\n[SCRATCHPAD TRUNCATED]";
        }

        return result;
    }
}
