namespace IronHive.Agent.ErrorRecovery;

/// <summary>
/// Error severity level.
/// </summary>
public enum ErrorSeverity
{
    /// <summary>Minor issue, can continue.</summary>
    Low,

    /// <summary>Moderate issue, may need intervention.</summary>
    Medium,

    /// <summary>Serious issue, requires attention.</summary>
    High,

    /// <summary>Critical issue, must escalate.</summary>
    Critical
}

/// <summary>
/// Error category for pattern matching.
/// </summary>
public enum ErrorCategory
{
    /// <summary>Unknown/uncategorized error.</summary>
    Unknown,

    /// <summary>Network/API connectivity issues.</summary>
    Network,

    /// <summary>Authentication/authorization failures.</summary>
    Authentication,

    /// <summary>Rate limiting or quota exceeded.</summary>
    RateLimit,

    /// <summary>Tool execution failures.</summary>
    ToolExecution,

    /// <summary>File system errors.</summary>
    FileSystem,

    /// <summary>Context/token limit issues.</summary>
    ContextLimit,

    /// <summary>Invalid input/request.</summary>
    InvalidInput,

    /// <summary>Timeout errors.</summary>
    Timeout,

    /// <summary>Internal/unexpected errors.</summary>
    Internal
}

/// <summary>
/// Recommended recovery action.
/// </summary>
public enum RecoveryAction
{
    /// <summary>Continue without intervention.</summary>
    Continue,

    /// <summary>Retry the operation.</summary>
    Retry,

    /// <summary>Wait and retry (with backoff).</summary>
    WaitAndRetry,

    /// <summary>Try an alternative approach.</summary>
    TryAlternative,

    /// <summary>Rollback changes and retry.</summary>
    Rollback,

    /// <summary>Reduce scope/context and retry.</summary>
    ReduceScope,

    /// <summary>Ask user for guidance.</summary>
    AskUser,

    /// <summary>Escalate to human operator.</summary>
    Escalate,

    /// <summary>Abort the current operation.</summary>
    Abort
}

/// <summary>
/// Represents an error occurrence with context.
/// </summary>
public record ErrorOccurrence
{
    /// <summary>
    /// Unique error ID.
    /// </summary>
    public string ErrorId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Exception type name.
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// Error category.
    /// </summary>
    public ErrorCategory Category { get; init; } = ErrorCategory.Unknown;

    /// <summary>
    /// Error severity.
    /// </summary>
    public ErrorSeverity Severity { get; init; } = ErrorSeverity.Medium;

    /// <summary>
    /// Tool name (if tool-related).
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Timestamp of occurrence.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Additional context data.
    /// </summary>
    public Dictionary<string, object?> Context { get; init; } = [];
}

/// <summary>
/// Recovery analysis result.
/// </summary>
public record RecoveryAnalysis
{
    /// <summary>
    /// The error being analyzed.
    /// </summary>
    public required ErrorOccurrence Error { get; init; }

    /// <summary>
    /// Whether this is a repeated error pattern.
    /// </summary>
    public bool IsRepeated { get; init; }

    /// <summary>
    /// Number of times this error pattern has occurred.
    /// </summary>
    public int OccurrenceCount { get; init; }

    /// <summary>
    /// Recommended action.
    /// </summary>
    public RecoveryAction RecommendedAction { get; init; }

    /// <summary>
    /// Human-readable explanation.
    /// </summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>
    /// Suggested wait time before retry (if applicable).
    /// </summary>
    public TimeSpan? RetryDelay { get; init; }

    /// <summary>
    /// Whether to notify via webhook.
    /// </summary>
    public bool ShouldNotify { get; init; }
}

/// <summary>
/// Service for error recovery analysis and management.
/// </summary>
public interface IErrorRecoveryService
{
    /// <summary>
    /// Records an error occurrence.
    /// </summary>
    void RecordError(ErrorOccurrence errorOccurrence);

    /// <summary>
    /// Records an error from an exception.
    /// </summary>
    void RecordError(Exception exception, string? toolName = null);

    /// <summary>
    /// Analyzes an error and recommends recovery action.
    /// </summary>
    RecoveryAnalysis AnalyzeError(ErrorOccurrence errorOccurrence);

    /// <summary>
    /// Analyzes an exception and recommends recovery action.
    /// </summary>
    RecoveryAnalysis AnalyzeException(Exception exception, string? toolName = null);

    /// <summary>
    /// Gets all recorded errors for the current session.
    /// </summary>
    IReadOnlyList<ErrorOccurrence> GetSessionErrors();

    /// <summary>
    /// Gets error statistics for the current session.
    /// </summary>
    ErrorStatistics GetStatistics();

    /// <summary>
    /// Clears error history.
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// Checks if the session should be escalated based on error patterns.
    /// </summary>
    bool ShouldEscalate();
}

/// <summary>
/// Error statistics.
/// </summary>
public record ErrorStatistics
{
    /// <summary>
    /// Total error count.
    /// </summary>
    public int TotalErrors { get; init; }

    /// <summary>
    /// Errors by category.
    /// </summary>
    public Dictionary<ErrorCategory, int> ByCategory { get; init; } = [];

    /// <summary>
    /// Errors by severity.
    /// </summary>
    public Dictionary<ErrorSeverity, int> BySeverity { get; init; } = [];

    /// <summary>
    /// Most frequent error patterns.
    /// </summary>
    public List<(string Pattern, int Count)> TopPatterns { get; init; } = [];

    /// <summary>
    /// Error rate (errors per minute).
    /// </summary>
    public float ErrorRate { get; init; }
}
