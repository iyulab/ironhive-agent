using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// Tests for TodoTool task management functionality.
/// </summary>
public class TodoToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TodoTool _todoTool;

    public TodoToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ironhive-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _todoTool = new TodoTool(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddTodo_CreatesNewTask()
    {
        var result = await _todoTool.ManageTodo("add", task: "Test task", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Added task #1", result);
        Assert.Contains("Test task", result);
    }

    [Fact]
    public async Task AddTodo_WithoutTask_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _todoTool.ManageTodo("add", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public async Task AddTodo_WithPriority_SetsCorrectPriority()
    {
        await _todoTool.ManageTodo("add", task: "High priority task", priority: "high", cancellationToken: TestContext.Current.CancellationToken);
        var list = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("!", list); // High priority marker
    }

    [Fact]
    public async Task AddTodo_WithDependencies_SetsDependencies()
    {
        await _todoTool.ManageTodo("add", task: "Task 1", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 2", dependencies: "1", cancellationToken: TestContext.Current.CancellationToken);
        var list = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("depends: 1", list);
    }

    [Fact]
    public async Task ListTodo_EmptyList_ReturnsNoItems()
    {
        var result = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("No todo items", result);
    }

    [Fact]
    public async Task ListTodo_WithItems_ShowsAllTasks()
    {
        await _todoTool.ManageTodo("add", task: "Task 1", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 2", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 3", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("3 items", result);
        Assert.Contains("Task 1", result);
        Assert.Contains("Task 2", result);
        Assert.Contains("Task 3", result);
    }

    [Fact]
    public async Task CompleteTodo_MarksAsCompleted()
    {
        await _todoTool.ManageTodo("add", task: "Task to complete", cancellationToken: TestContext.Current.CancellationToken);
        var result = await _todoTool.ManageTodo("complete", id: "1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Completed task #1", result);

        var list = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Completed", list);
    }

    [Fact]
    public async Task CompleteTodo_WithoutId_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _todoTool.ManageTodo("complete", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public async Task CompleteTodo_NonExistent_Throws()
    {
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => _todoTool.ManageTodo("complete", id: "999", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task CompleteTodo_WithPendingDependencies_ReturnsWarning()
    {
        await _todoTool.ManageTodo("add", task: "Task 1", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 2", dependencies: "1", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _todoTool.ManageTodo("complete", id: "2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Warning", result);
        Assert.Contains("dependencies", result);
    }

    [Fact]
    public async Task UpdateTodo_ChangesStatus()
    {
        await _todoTool.ManageTodo("add", task: "Task to update", cancellationToken: TestContext.Current.CancellationToken);
        var result = await _todoTool.ManageTodo("update", id: "1", status: "in_progress", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Updated task #1", result);
        Assert.Contains("InProgress", result);
    }

    [Fact]
    public async Task UpdateTodo_ChangesTask()
    {
        await _todoTool.ManageTodo("add", task: "Original task", cancellationToken: TestContext.Current.CancellationToken);
        var result = await _todoTool.ManageTodo("update", id: "1", task: "Updated task", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Updated task", result);
    }

    [Fact]
    public async Task UpdateTodo_WithoutId_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _todoTool.ManageTodo("update", task: "New task", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public async Task RemoveTodo_DeletesTask()
    {
        await _todoTool.ManageTodo("add", task: "Task to remove", cancellationToken: TestContext.Current.CancellationToken);
        var result = await _todoTool.ManageTodo("remove", id: "1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Removed task #1", result);

        var list = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("No todo items", list);
    }

    [Fact]
    public async Task RemoveTodo_NonExistent_Throws()
    {
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => _todoTool.ManageTodo("remove", id: "999", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ClearTodos_RemovesOnlyCompleted()
    {
        await _todoTool.ManageTodo("add", task: "Task 1", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 2", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("complete", id: "1", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _todoTool.ManageTodo("clear", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Cleared 1 completed tasks", result);
        Assert.Contains("1 tasks remaining", result);
    }

    [Fact]
    public async Task ClearTodos_NoCompleted_ReturnsMessage()
    {
        await _todoTool.ManageTodo("add", task: "Pending task", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _todoTool.ManageTodo("clear", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("No completed tasks to clear", result);
    }

    [Fact]
    public async Task UnknownAction_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _todoTool.ManageTodo("unknown", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Unknown action", ex.Message);
    }

    [Fact]
    public async Task MultipleOperations_MaintainsConsistency()
    {
        // Add multiple tasks
        await _todoTool.ManageTodo("add", task: "Task 1", priority: "high", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 2", priority: "low", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("add", task: "Task 3", priority: "medium", dependencies: "1,2", cancellationToken: TestContext.Current.CancellationToken);

        // Update and complete
        await _todoTool.ManageTodo("update", id: "1", status: "in_progress", cancellationToken: TestContext.Current.CancellationToken);
        await _todoTool.ManageTodo("complete", id: "2", cancellationToken: TestContext.Current.CancellationToken);

        // Check state
        var list = await _todoTool.ManageTodo("list", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("3 items", list);
        Assert.Contains("InProgress", list);
        Assert.Contains("Completed", list);
        Assert.Contains("Pending", list);
    }

    [Fact]
    public void GetAITool_ReturnsValidTool()
    {
        var tool = _todoTool.GetAITool();

        Assert.NotNull(tool);
    }
}
