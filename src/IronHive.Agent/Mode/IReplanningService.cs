namespace IronHive.Agent.Mode;

/// <summary>
/// Service for tracking failures and triggering replanning.
/// </summary>
public interface IReplanningService
{
    /// <summary>
    /// Records a tool execution failure.
    /// </summary>
    /// <param name="toolName">Name of the failed tool</param>
    /// <param name="errorMessage">Error message</param>
    void RecordFailure(string toolName, string errorMessage);

    /// <summary>
    /// Records a successful tool execution.
    /// </summary>
    /// <param name="toolName">Name of the successful tool</param>
    void RecordSuccess(string toolName);

    /// <summary>
    /// Checks if replanning is needed based on failure patterns.
    /// </summary>
    ReplanningDecision ShouldReplan();

    /// <summary>
    /// Gets the current failure context for replanning.
    /// </summary>
    FailureContext GetFailureContext();

    /// <summary>
    /// Resets failure tracking (called when entering planning mode).
    /// </summary>
    void Reset();
}

/// <summary>
/// Decision on whether replanning is needed.
/// </summary>
public record ReplanningDecision
{
    /// <summary>
    /// Whether replanning is recommended.
    /// </summary>
    public bool ShouldReplan { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Severity of the situation.
    /// </summary>
    public ReplanningSeverity Severity { get; init; }

    /// <summary>
    /// No replanning needed.
    /// </summary>
    public static ReplanningDecision No => new() { ShouldReplan = false };

    /// <summary>
    /// Replanning recommended.
    /// </summary>
    public static ReplanningDecision Yes(string reason, ReplanningSeverity severity = ReplanningSeverity.Normal) =>
        new() { ShouldReplan = true, Reason = reason, Severity = severity };
}

/// <summary>
/// Severity levels for replanning.
/// </summary>
public enum ReplanningSeverity
{
    /// <summary>
    /// Normal replanning - continue with modified plan.
    /// </summary>
    Normal,

    /// <summary>
    /// High severity - significant issues detected.
    /// </summary>
    High,

    /// <summary>
    /// Critical - stop and require user intervention.
    /// </summary>
    Critical
}

/// <summary>
/// Context about failures for replanning.
/// </summary>
public class FailureContext
{
    /// <summary>
    /// List of recent failures.
    /// </summary>
    public List<ToolFailure> RecentFailures { get; init; } = [];

    /// <summary>
    /// Consecutive failure count.
    /// </summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Total failure count in current session.
    /// </summary>
    public int TotalFailures { get; init; }

    /// <summary>
    /// Whether a repeated failure pattern is detected.
    /// </summary>
    public bool RepeatedFailureDetected { get; init; }

    /// <summary>
    /// Summary of the failure context for LLM.
    /// </summary>
    public string GetSummary()
    {
        if (RecentFailures.Count == 0)
        {
            return "No recent failures.";
        }

        var lines = new List<string>
        {
            $"Failure Context: {TotalFailures} total failures, {ConsecutiveFailures} consecutive"
        };

        if (RepeatedFailureDetected)
        {
            lines.Add("WARNING: Repeated failure pattern detected!");
        }

        lines.Add("Recent failures:");
        foreach (var failure in RecentFailures.TakeLast(5))
        {
            lines.Add($"  - {failure.ToolName}: {failure.Error}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Record of a tool failure.
/// </summary>
public class ToolFailure
{
    /// <summary>
    /// Name of the failed tool.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Error message.
    /// </summary>
    public required string Error { get; init; }

    /// <summary>
    /// Time of the failure.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Default implementation of replanning service.
/// </summary>
public class ReplanningService : IReplanningService
{
    private readonly List<ToolFailure> _failures = [];
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private string? _lastFailedTool;
    private string? _lastError;

    /// <summary>
    /// Maximum consecutive failures before forcing replan.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Maximum total failures before forcing replan.
    /// </summary>
    public int MaxTotalFailures { get; set; } = 10;

    /// <inheritdoc />
    public void RecordFailure(string toolName, string errorMessage)
    {
        lock (_lock)
        {
            _failures.Add(new ToolFailure
            {
                ToolName = toolName,
                Error = errorMessage
            });

            _consecutiveFailures++;

            // Detect repeated failure
            if (_lastFailedTool == toolName && _lastError == errorMessage)
            {
                // Same failure repeated
            }

            _lastFailedTool = toolName;
            _lastError = errorMessage;
        }
    }

    /// <inheritdoc />
    public void RecordSuccess(string toolName)
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _lastFailedTool = null;
            _lastError = null;
        }
    }

    /// <inheritdoc />
    public ReplanningDecision ShouldReplan()
    {
        lock (_lock)
        {
            // Check consecutive failures
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                return ReplanningDecision.Yes(
                    $"Too many consecutive failures ({_consecutiveFailures})",
                    ReplanningSeverity.High);
            }

            // Check total failures
            if (_failures.Count >= MaxTotalFailures)
            {
                return ReplanningDecision.Yes(
                    $"Too many total failures ({_failures.Count})",
                    ReplanningSeverity.Critical);
            }

            // Check for repeated failure pattern
            if (DetectRepeatedPattern())
            {
                return ReplanningDecision.Yes(
                    "Repeated failure pattern detected - same error occurring multiple times",
                    ReplanningSeverity.Normal);
            }

            return ReplanningDecision.No;
        }
    }

    /// <inheritdoc />
    public FailureContext GetFailureContext()
    {
        lock (_lock)
        {
            return new FailureContext
            {
                RecentFailures = [.. _failures],
                ConsecutiveFailures = _consecutiveFailures,
                TotalFailures = _failures.Count,
                RepeatedFailureDetected = DetectRepeatedPattern()
            };
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
        {
            _failures.Clear();
            _consecutiveFailures = 0;
            _lastFailedTool = null;
            _lastError = null;
        }
    }

    private bool DetectRepeatedPattern()
    {
        if (_failures.Count < 3)
        {
            return false;
        }

        // Check if last 3 failures are the same tool with same error
        var recent = _failures.TakeLast(3).ToList();
        var firstTool = recent[0].ToolName;
        var firstError = recent[0].Error;

        return recent.All(f => f.ToolName == firstTool && f.Error == firstError);
    }
}
