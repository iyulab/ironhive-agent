using IronHive.Agent.ErrorRecovery;

namespace IronHive.Agent.Tests.Agent;

public class ErrorRecoveryServiceTests
{
    private readonly ErrorRecoveryService _service;

    public ErrorRecoveryServiceTests()
    {
        _service = new ErrorRecoveryService();
    }

    [Fact]
    public void RecordError_IncreasesErrorCount()
    {
        var error = new ErrorOccurrence
        {
            Message = "Test error"
        };

        _service.RecordError(error);
        var stats = _service.GetStatistics();

        Assert.Equal(1, stats.TotalErrors);
    }

    [Fact]
    public void RecordError_FromException_CategorizesCorrectly()
    {
        _service.RecordError(new HttpRequestException("Network error"));

        var errors = _service.GetSessionErrors();

        Assert.Single(errors);
        Assert.Equal(ErrorCategory.Network, errors[0].Category);
    }

    [Fact]
    public void RecordError_FileNotFound_CategorizedAsFileSystem()
    {
        _service.RecordError(new FileNotFoundException("File not found"));

        var errors = _service.GetSessionErrors();

        Assert.Equal(ErrorCategory.FileSystem, errors[0].Category);
    }

    [Fact]
    public void RecordError_Timeout_CategorizedCorrectly()
    {
        _service.RecordError(new TimeoutException("Operation timed out"));

        var errors = _service.GetSessionErrors();

        Assert.Equal(ErrorCategory.Timeout, errors[0].Category);
    }

    [Fact]
    public void RecordError_UnauthorizedAccess_CategorizedAsAuth()
    {
        _service.RecordError(new UnauthorizedAccessException("Access denied"));

        var errors = _service.GetSessionErrors();

        Assert.Equal(ErrorCategory.Authentication, errors[0].Category);
    }

    [Fact]
    public void AnalyzeError_NetworkError_RecommendsWaitAndRetry()
    {
        var error = new ErrorOccurrence
        {
            Message = "Connection failed",
            Category = ErrorCategory.Network
        };

        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.WaitAndRetry, analysis.RecommendedAction);
        Assert.NotNull(analysis.RetryDelay);
    }

    [Fact]
    public void AnalyzeError_RateLimit_RecommendsWaitAndRetry()
    {
        var error = new ErrorOccurrence
        {
            Message = "Rate limit exceeded",
            Category = ErrorCategory.RateLimit
        };

        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.WaitAndRetry, analysis.RecommendedAction);
        Assert.NotNull(analysis.RetryDelay);
    }

    [Fact]
    public void AnalyzeError_AuthError_RecommendsEscalate()
    {
        var error = new ErrorOccurrence
        {
            Message = "Invalid credentials",
            Category = ErrorCategory.Authentication
        };

        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.Escalate, analysis.RecommendedAction);
    }

    [Fact]
    public void AnalyzeError_ContextLimit_RecommendsReduceScope()
    {
        var error = new ErrorOccurrence
        {
            Message = "Token limit exceeded",
            Category = ErrorCategory.ContextLimit
        };

        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.ReduceScope, analysis.RecommendedAction);
    }

    [Fact]
    public void AnalyzeError_RepeatedError_EscalatesAfterThreshold()
    {
        var config = new ErrorRecoveryConfig { MaxRepeatedErrors = 3 };
        var service = new ErrorRecoveryService(config);

        var error = new ErrorOccurrence
        {
            Message = "Same error",
            Category = ErrorCategory.ToolExecution
        };

        // Record the same error multiple times
        service.RecordError(error);
        service.RecordError(error);
        service.RecordError(error);

        var analysis = service.AnalyzeError(error);

        Assert.True(analysis.IsRepeated);
        Assert.Equal(4, analysis.OccurrenceCount);
        Assert.Equal(RecoveryAction.Escalate, analysis.RecommendedAction);
    }

    [Fact]
    public void ShouldEscalate_TooManyErrors_ReturnsTrue()
    {
        var config = new ErrorRecoveryConfig { MaxTotalErrors = 3 };
        var service = new ErrorRecoveryService(config);

        service.RecordError(new InvalidOperationException("Error 1"));
        service.RecordError(new InvalidOperationException("Error 2"));
        service.RecordError(new InvalidOperationException("Error 3"));

        Assert.True(service.ShouldEscalate());
    }

    [Fact]
    public void ShouldEscalate_CriticalError_ReturnsTrue()
    {
        var error = new ErrorOccurrence
        {
            Message = "Critical error",
            Severity = ErrorSeverity.Critical
        };

        _service.RecordError(error);

        Assert.True(_service.ShouldEscalate());
    }

    [Fact]
    public void ShouldEscalate_NormalErrors_ReturnsFalse()
    {
        // Use fresh instance to avoid shared state from other tests
        var service = new ErrorRecoveryService();
        service.RecordError(new InvalidOperationException("Error 1"));
        service.RecordError(new InvalidOperationException("Error 2"));

        Assert.False(service.ShouldEscalate());
    }

    [Fact]
    public void GetStatistics_CalculatesCorrectly()
    {
        _service.RecordError(new HttpRequestException("Network 1"));
        _service.RecordError(new HttpRequestException("Network 2"));
        _service.RecordError(new TimeoutException("Timeout"));
        _service.RecordError(new FileNotFoundException("File"));

        var stats = _service.GetStatistics();

        Assert.Equal(4, stats.TotalErrors);
        Assert.Equal(2, stats.ByCategory[ErrorCategory.Network]);
        Assert.Equal(1, stats.ByCategory[ErrorCategory.Timeout]);
        Assert.Equal(1, stats.ByCategory[ErrorCategory.FileSystem]);
    }

    [Fact]
    public void ClearHistory_ResetsAllCounters()
    {
        _service.RecordError(new InvalidOperationException("Error 1"));
        _service.RecordError(new InvalidOperationException("Error 2"));

        _service.ClearHistory();

        var stats = _service.GetStatistics();
        Assert.Equal(0, stats.TotalErrors);
        Assert.Empty(_service.GetSessionErrors());
    }

    [Fact]
    public void AnalyzeError_InvalidInput_RecommendsTryAlternative()
    {
        var error = new ErrorOccurrence
        {
            Message = "Invalid argument",
            Category = ErrorCategory.InvalidInput
        };

        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.TryAlternative, analysis.RecommendedAction);
    }

    [Fact]
    public void AnalyzeError_FirstToolError_RecommendsRetry()
    {
        var error = new ErrorOccurrence
        {
            Message = "Tool failed",
            Category = ErrorCategory.ToolExecution,
            ToolName = "shell"
        };

        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.Retry, analysis.RecommendedAction);
    }

    [Fact]
    public void AnalyzeError_RepeatedToolError_RecommendsAskUser()
    {
        var error = new ErrorOccurrence
        {
            Message = "Tool failed",
            Category = ErrorCategory.ToolExecution,
            ToolName = "shell"
        };

        _service.RecordError(error);
        var analysis = _service.AnalyzeError(error);

        Assert.Equal(RecoveryAction.AskUser, analysis.RecommendedAction);
    }

    [Fact]
    public void ErrorOccurrence_DefaultValues_AreCorrect()
    {
        var error = new ErrorOccurrence { Message = "Test" };

        Assert.NotEmpty(error.ErrorId);
        Assert.Equal(ErrorCategory.Unknown, error.Category);
        Assert.Equal(ErrorSeverity.Medium, error.Severity);
        Assert.True(error.Timestamp <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RecoveryAnalysis_ShouldNotify_TrueForHighSeverity()
    {
        var error = new ErrorOccurrence
        {
            Message = "Critical error",
            Category = ErrorCategory.Authentication,
            Severity = ErrorSeverity.High
        };

        var analysis = _service.AnalyzeError(error);

        Assert.True(analysis.ShouldNotify);
    }
}
