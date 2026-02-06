using System.Text.RegularExpressions;

namespace IronHive.Agent.ErrorRecovery;

/// <summary>
/// Error recovery configuration.
/// </summary>
public class ErrorRecoveryConfig
{
    /// <summary>
    /// Maximum repeated errors before escalation.
    /// </summary>
    public int MaxRepeatedErrors { get; init; } = 3;

    /// <summary>
    /// Maximum total errors before escalation.
    /// </summary>
    public int MaxTotalErrors { get; init; } = 10;

    /// <summary>
    /// Error rate threshold for escalation (errors per minute).
    /// </summary>
    public float ErrorRateThreshold { get; init; } = 5.0f;

    /// <summary>
    /// Default retry delay.
    /// </summary>
    public TimeSpan DefaultRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum retry delay.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Error recovery service implementation.
/// </summary>
public class ErrorRecoveryService : IErrorRecoveryService
{
    private readonly ErrorRecoveryConfig _config;
    private readonly List<ErrorOccurrence> _errors = [];
    private readonly Dictionary<string, int> _patternCounts = [];
    private DateTimeOffset _sessionStart = DateTimeOffset.UtcNow;

    public ErrorRecoveryService(ErrorRecoveryConfig? config = null)
    {
        _config = config ?? new ErrorRecoveryConfig();
    }

    /// <inheritdoc />
    public void RecordError(ErrorOccurrence errorOccurrence)
    {
        _errors.Add(errorOccurrence);
        var pattern = GetErrorPattern(errorOccurrence);
        _patternCounts[pattern] = _patternCounts.GetValueOrDefault(pattern, 0) + 1;
    }

    /// <inheritdoc />
    public void RecordError(Exception exception, string? toolName = null)
    {
        var errorOccurrence = CreateErrorFromException(exception, toolName);
        RecordError(errorOccurrence);
    }

    /// <inheritdoc />
    public RecoveryAnalysis AnalyzeError(ErrorOccurrence errorOccurrence)
    {
        var pattern = GetErrorPattern(errorOccurrence);
        var occurrenceCount = _patternCounts.GetValueOrDefault(pattern, 0) + 1;
        var isRepeated = occurrenceCount > 1;

        var (action, explanation, retryDelay) = DetermineRecoveryAction(errorOccurrence, occurrenceCount);

        var shouldNotify = errorOccurrence.Severity >= ErrorSeverity.High ||
                          occurrenceCount >= _config.MaxRepeatedErrors ||
                          action == RecoveryAction.Escalate;

        return new RecoveryAnalysis
        {
            Error = errorOccurrence,
            IsRepeated = isRepeated,
            OccurrenceCount = occurrenceCount,
            RecommendedAction = action,
            Explanation = explanation,
            RetryDelay = retryDelay,
            ShouldNotify = shouldNotify
        };
    }

    /// <inheritdoc />
    public RecoveryAnalysis AnalyzeException(Exception exception, string? toolName = null)
    {
        var error = CreateErrorFromException(exception, toolName);
        return AnalyzeError(error);
    }

    /// <inheritdoc />
    public IReadOnlyList<ErrorOccurrence> GetSessionErrors()
    {
        return _errors.AsReadOnly();
    }

    /// <inheritdoc />
    public ErrorStatistics GetStatistics()
    {
        var byCategory = _errors
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        var bySeverity = _errors
            .GroupBy(e => e.Severity)
            .ToDictionary(g => g.Key, g => g.Count());

        var topPatterns = _patternCounts
            .OrderByDescending(p => p.Value)
            .Take(5)
            .Select(p => (p.Key, p.Value))
            .ToList();

        var elapsed = DateTimeOffset.UtcNow - _sessionStart;
        // Only calculate error rate if at least 1 minute has passed
        // to avoid inflated rates from rapid initial errors
        var errorRate = elapsed.TotalMinutes >= 1.0
            ? (float)(_errors.Count / elapsed.TotalMinutes)
            : 0;

        return new ErrorStatistics
        {
            TotalErrors = _errors.Count,
            ByCategory = byCategory,
            BySeverity = bySeverity,
            TopPatterns = topPatterns,
            ErrorRate = errorRate
        };
    }

    /// <inheritdoc />
    public void ClearHistory()
    {
        _errors.Clear();
        _patternCounts.Clear();
        _sessionStart = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public bool ShouldEscalate()
    {
        // Check total error count
        if (_errors.Count >= _config.MaxTotalErrors)
        {
            return true;
        }

        // Check for repeated patterns
        if (_patternCounts.Values.Any(c => c >= _config.MaxRepeatedErrors))
        {
            return true;
        }

        // Check error rate
        var stats = GetStatistics();
        if (stats.ErrorRate >= _config.ErrorRateThreshold)
        {
            return true;
        }

        // Check for critical errors
        if (_errors.Any(e => e.Severity == ErrorSeverity.Critical))
        {
            return true;
        }

        return false;
    }

    private static ErrorOccurrence CreateErrorFromException(Exception exception, string? toolName)
    {
        var category = CategorizeException(exception);
        var severity = DetermineSeverity(exception, category);

        return new ErrorOccurrence
        {
            Message = exception.Message,
            ExceptionType = exception.GetType().Name,
            Category = category,
            Severity = severity,
            ToolName = toolName,
            Context = new Dictionary<string, object?>
            {
                ["StackTrace"] = exception.StackTrace,
                ["InnerException"] = exception.InnerException?.Message
            }
        };
    }

    private static ErrorCategory CategorizeException(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => ErrorCategory.Network,
            UnauthorizedAccessException => ErrorCategory.Authentication,
            TimeoutException or TaskCanceledException { InnerException: TimeoutException } => ErrorCategory.Timeout,
            IOException or FileNotFoundException or DirectoryNotFoundException => ErrorCategory.FileSystem,
            ArgumentException or FormatException => ErrorCategory.InvalidInput,
            _ when exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) => ErrorCategory.RateLimit,
            _ when exception.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) => ErrorCategory.RateLimit,
            _ when exception.Message.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                   exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) => ErrorCategory.ContextLimit,
            _ when exception.Message.Contains("auth", StringComparison.OrdinalIgnoreCase) => ErrorCategory.Authentication,
            _ => ErrorCategory.Unknown
        };
    }

    private static ErrorSeverity DetermineSeverity(Exception exception, ErrorCategory category)
    {
        return category switch
        {
            ErrorCategory.Authentication => ErrorSeverity.High,
            ErrorCategory.ContextLimit => ErrorSeverity.High,
            ErrorCategory.RateLimit => ErrorSeverity.Medium,
            ErrorCategory.Network => ErrorSeverity.Medium,
            ErrorCategory.Timeout => ErrorSeverity.Medium,
            ErrorCategory.FileSystem => ErrorSeverity.Medium,
            ErrorCategory.InvalidInput => ErrorSeverity.Low,
            _ when exception is OutOfMemoryException or StackOverflowException => ErrorSeverity.Critical,
            _ => ErrorSeverity.Medium
        };
    }

    private (RecoveryAction Action, string Explanation, TimeSpan? RetryDelay) DetermineRecoveryAction(
        ErrorOccurrence error,
        int occurrenceCount)
    {
        // Escalate if too many repeated errors
        if (occurrenceCount >= _config.MaxRepeatedErrors)
        {
            return (
                RecoveryAction.Escalate,
                $"Error has occurred {occurrenceCount} times. Escalating to human operator.",
                null);
        }

        // Handle by category
        return error.Category switch
        {
            ErrorCategory.RateLimit => (
                RecoveryAction.WaitAndRetry,
                "Rate limit exceeded. Waiting before retry.",
                CalculateBackoffDelay(occurrenceCount)),

            ErrorCategory.Network => (
                RecoveryAction.WaitAndRetry,
                "Network error. Will retry after brief delay.",
                CalculateBackoffDelay(occurrenceCount)),

            ErrorCategory.Timeout => (
                RecoveryAction.WaitAndRetry,
                "Operation timed out. Will retry with longer timeout.",
                CalculateBackoffDelay(occurrenceCount)),

            ErrorCategory.Authentication => (
                RecoveryAction.Escalate,
                "Authentication failed. Please check credentials.",
                null),

            ErrorCategory.ContextLimit => (
                RecoveryAction.ReduceScope,
                "Context limit reached. Compacting history and retrying.",
                null),

            ErrorCategory.FileSystem => occurrenceCount > 1
                ? (RecoveryAction.TryAlternative, "File system error persists. Trying alternative approach.", null)
                : (RecoveryAction.Retry, "File system error. Retrying operation.", _config.DefaultRetryDelay),

            ErrorCategory.ToolExecution => occurrenceCount > 1
                ? (RecoveryAction.AskUser, "Tool execution failed repeatedly. Asking for guidance.", null)
                : (RecoveryAction.Retry, "Tool execution error. Retrying.", _config.DefaultRetryDelay),

            ErrorCategory.InvalidInput => (
                RecoveryAction.TryAlternative,
                "Invalid input detected. Trying alternative approach.",
                null),

            _ when error.Severity == ErrorSeverity.Critical => (
                RecoveryAction.Abort,
                "Critical error occurred. Aborting operation.",
                null),

            _ when error.Severity == ErrorSeverity.High => (
                RecoveryAction.AskUser,
                "High severity error. Asking for user guidance.",
                null),

            _ => (
                RecoveryAction.Retry,
                "Error occurred. Will retry.",
                _config.DefaultRetryDelay)
        };
    }

    private TimeSpan CalculateBackoffDelay(int attemptCount)
    {
        // Exponential backoff: 1s, 2s, 4s, 8s, ...
        var delay = TimeSpan.FromSeconds(Math.Pow(2, attemptCount - 1));
        return delay > _config.MaxRetryDelay ? _config.MaxRetryDelay : delay;
    }

    private static string GetErrorPattern(ErrorOccurrence error)
    {
        // Create a pattern key that groups similar errors
        var message = Regex.Replace(error.Message, @"\d+", "#"); // Replace numbers
        message = Regex.Replace(message, @"'[^']*'", "'...'"); // Replace quoted strings
        message = Regex.Replace(message, @"""[^""]*""", "\"...\""); // Replace double-quoted strings

        return $"{error.Category}:{error.ToolName ?? "none"}:{message}";
    }
}
