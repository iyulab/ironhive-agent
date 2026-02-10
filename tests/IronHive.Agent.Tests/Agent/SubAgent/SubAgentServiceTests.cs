using Ironbees.Core;
using IronHive.Agent.SubAgent;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent.SubAgent;

/// <summary>
/// SubAgent service tests using IAgentOrchestrator mock.
/// </summary>
public class SubAgentServiceTests : IDisposable
{
    private readonly IAgentOrchestrator _mockOrchestrator;

    public SubAgentServiceTests()
    {
        _mockOrchestrator = Substitute.For<IAgentOrchestrator>();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    #region CanSpawn and Limits

    [Fact]
    public void CanSpawn_WithinLimits_ReturnsTrue()
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = 2, MaxConcurrent = 3 };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: 0);

        // Act & Assert
        Assert.True(service.CanSpawn(SubAgentType.Explore));
        Assert.True(service.CanSpawn(SubAgentType.General));
    }

    [Fact]
    public void CurrentDepth_ReturnsConfiguredDepth()
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = 2 };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: 1);

        // Act & Assert
        Assert.Equal(1, service.CurrentDepth);
    }

    [Fact]
    public void RunningCount_InitiallyZero()
    {
        // Arrange
        var config = new SubAgentConfig();
        using var service = new SubAgentService(_mockOrchestrator, config);

        // Act & Assert
        Assert.Equal(0, service.RunningCount);
    }

    #endregion

    #region Depth Limits

    [Fact]
    public void CanSpawn_AtMaxDepth_ReturnsFalse()
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = 2 };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: 2);

        // Act & Assert
        Assert.False(service.CanSpawn(SubAgentType.Explore));
        Assert.False(service.CanSpawn(SubAgentType.General));
    }

    [Fact]
    public void CanSpawn_BelowMaxDepth_ReturnsTrue()
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = 2 };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: 1);

        // Act & Assert
        Assert.True(service.CanSpawn(SubAgentType.Explore));
    }

    [Fact]
    public async Task ExploreAsync_AtMaxDepth_ReturnsFailure()
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = 2 };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: 2);

        // Act
        var result = await service.ExploreAsync("test task");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("depth limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 2, true)]  // Depth 0, max 2 -> can spawn
    [InlineData(1, 2, true)]  // Depth 1, max 2 -> can spawn
    [InlineData(2, 2, false)] // Depth 2, max 2 -> cannot spawn
    [InlineData(3, 2, false)] // Depth 3, max 2 -> cannot spawn
    public void CanSpawn_DepthBoundaries(int currentDepth, int maxDepth, bool expected)
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = maxDepth };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: currentDepth);

        // Act & Assert
        Assert.Equal(expected, service.CanSpawn(SubAgentType.Explore));
    }

    #endregion

    #region Concurrent Limit

    [Fact]
    public void CanSpawn_AtMaxConcurrent_ReturnsFalse()
    {
        // Arrange - MaxConcurrent=0 means no concurrent allowed
        var config = new SubAgentConfig { MaxConcurrent = 0 };
        using var service = new SubAgentService(_mockOrchestrator, config);

        // RunningCount=0, MaxConcurrent=0 -> RunningCount >= MaxConcurrent is true -> false
        Assert.False(service.CanSpawn(SubAgentType.Explore));
    }

    [Fact]
    public async Task SpawnAsync_ExceedsDepthLimit_ReturnsError()
    {
        // Arrange
        var config = new SubAgentConfig { MaxDepth = 1, MaxConcurrent = 3 };
        using var service = new SubAgentService(_mockOrchestrator, config, currentDepth: 1);

        var context = SubAgentContext.Create(
            SubAgentType.Explore,
            "test task",
            null,
            depth: 2,
            parentId: null,
            workingDirectory: ".");

        // Act
        var result = await service.SpawnAsync(context);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Orchestrator Delegation

    [Fact]
    public async Task ExploreAsync_DelegatesToOrchestrator()
    {
        // Arrange
        _mockOrchestrator.ProcessAsync(
            Arg.Any<string>(),
            Arg.Is("explore"),
            Arg.Any<CancellationToken>())
            .Returns("Task completed successfully!");

        var config = new SubAgentConfig
        {
            Explore = new ExploreAgentConfig { MaxTurns = 5, MaxTokens = 8000 }
        };
        using var service = new SubAgentService(_mockOrchestrator, config);

        // Act
        var result = await service.ExploreAsync("Find all test files");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Task completed successfully!", result.Output);
        await _mockOrchestrator.Received(1).ProcessAsync(
            Arg.Is<string>(s => s.Contains("Find all test files")),
            Arg.Is("explore"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneralAsync_DelegatesToOrchestrator()
    {
        // Arrange
        _mockOrchestrator.ProcessAsync(
            Arg.Any<string>(),
            Arg.Is("general"),
            Arg.Any<CancellationToken>())
            .Returns("General task completed!");

        var config = new SubAgentConfig
        {
            General = new GeneralAgentConfig { MaxTurns = 30, MaxTokens = 64000 }
        };
        using var service = new SubAgentService(_mockOrchestrator, config);

        // Act
        var result = await service.GeneralAsync("Complex multi-step task");

        // Assert
        Assert.True(result.Success);
        await _mockOrchestrator.Received(1).ProcessAsync(
            Arg.Is<string>(s => s.Contains("Complex multi-step task")),
            Arg.Is("general"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExploreAsync_WithContext_IncludesContextInPrompt()
    {
        // Arrange
        _mockOrchestrator.ProcessAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns("Found 10 test files");

        using var service = new SubAgentService(_mockOrchestrator, new SubAgentConfig());

        // Act
        var result = await service.ExploreAsync("Find test files", context: "Look in src/ directory");

        // Assert
        Assert.True(result.Success);
        Assert.Contains("10 test files", result.Output);
        await _mockOrchestrator.Received(1).ProcessAsync(
            Arg.Is<string>(s => s.Contains("Find test files") && s.Contains("Look in src/ directory")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SpawnAsync_ReturnsTimingInfo()
    {
        // Arrange
        _mockOrchestrator.ProcessAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns("Done");

        using var service = new SubAgentService(_mockOrchestrator, new SubAgentConfig());

        // Act
        var result = await service.ExploreAsync("Quick task");

        // Assert
        Assert.True(result.Duration.TotalMilliseconds >= 0);
    }

    [Fact]
    public async Task SpawnAsync_EmptyResponse_ReturnsFailure()
    {
        // Arrange
        _mockOrchestrator.ProcessAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        using var service = new SubAgentService(_mockOrchestrator, new SubAgentConfig());

        // Act
        var result = await service.ExploreAsync("task");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("empty response", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpawnAsync_OrchestratorThrows_ReturnsFailure()
    {
        // Arrange
        _mockOrchestrator.ProcessAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("LLM error"));

        using var service = new SubAgentService(_mockOrchestrator, new SubAgentConfig());

        // Act
        var result = await service.ExploreAsync("task");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("LLM error", result.Error);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task SpawnAsync_WhenCancelledDuringExecution_ThrowsOrReturnsFailure()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        using var service = new SubAgentService(_mockOrchestrator, new SubAgentConfig());

        // Act & Assert
        // When cancelled during semaphore wait, OperationCanceledException is thrown
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await service.ExploreAsync("task", cancellationToken: cts.Token));
    }

    #endregion

    #region SubAgentContext

    [Fact]
    public void SubAgentContext_Create_SetsProperties()
    {
        // Act
        var context = SubAgentContext.Create(
            SubAgentType.Explore,
            "Test task",
            "Additional context",
            depth: 1,
            parentId: "parent-123",
            workingDirectory: "/home/test");

        // Assert
        Assert.Equal(SubAgentType.Explore, context.Type);
        Assert.Equal("Test task", context.Task);
        Assert.Equal("Additional context", context.AdditionalContext);
        Assert.Equal(1, context.Depth);
        Assert.Equal("parent-123", context.ParentId);
        Assert.Equal("/home/test", context.WorkingDirectory);
        Assert.NotNull(context.Id);
    }

    [Fact]
    public void SubAgentContext_Create_GeneratesUniqueIds()
    {
        // Act
        var context1 = SubAgentContext.Create(SubAgentType.Explore, "task1", null, 0, null, ".");
        var context2 = SubAgentContext.Create(SubAgentType.Explore, "task2", null, 0, null, ".");

        // Assert
        Assert.NotEqual(context1.Id, context2.Id);
    }

    #endregion

    #region SubAgentResult

    [Fact]
    public void SubAgentResult_Succeeded_CreatesSuccessResult()
    {
        // Arrange
        var context = SubAgentContext.Create(SubAgentType.Explore, "task", null, 0, null, ".");

        // Act
        var result = SubAgentResult.Succeeded(
            context,
            "Output text",
            turnsUsed: 3,
            tokensUsed: 1000,
            duration: TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Output text", result.Output);
        Assert.Null(result.Error);
        Assert.Equal(3, result.TurnsUsed);
        Assert.Equal(1000, result.TokensUsed);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
    }

    [Fact]
    public void SubAgentResult_Failed_CreatesFailureResult()
    {
        // Arrange
        var context = SubAgentContext.Create(SubAgentType.General, "task", null, 0, null, ".");

        // Act
        var result = SubAgentResult.Failed(
            context,
            "Error message",
            turnsUsed: 1,
            tokensUsed: 500,
            duration: TimeSpan.FromSeconds(2));

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Error message", result.Error);
        Assert.Null(result.Output);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = new SubAgentService(_mockOrchestrator, new SubAgentConfig());

        // Act & Assert - should not throw
        service.Dispose();
        service.Dispose();
    }

    #endregion
}
