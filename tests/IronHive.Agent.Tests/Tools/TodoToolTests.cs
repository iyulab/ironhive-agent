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
        var result = await _todoTool.ManageTodo("add", task: "Test task");

        Assert.Contains("Added task #1", result);
        Assert.Contains("Test task", result);
    }

    [Fact]
    public async Task AddTodo_WithoutTask_ReturnsError()
    {
        var result = await _todoTool.ManageTodo("add");

        Assert.Contains("Error", result);
        Assert.Contains("required", result);
    }

    [Fact]
    public async Task AddTodo_WithPriority_SetsCorrectPriority()
    {
        await _todoTool.ManageTodo("add", task: "High priority task", priority: "high");
        var list = await _todoTool.ManageTodo("list");

        Assert.Contains("!", list); // High priority marker
    }

    [Fact]
    public async Task AddTodo_WithDependencies_SetsDependencies()
    {
        await _todoTool.ManageTodo("add", task: "Task 1");
        await _todoTool.ManageTodo("add", task: "Task 2", dependencies: "1");
        var list = await _todoTool.ManageTodo("list");

        Assert.Contains("depends: 1", list);
    }

    [Fact]
    public async Task ListTodo_EmptyList_ReturnsNoItems()
    {
        var result = await _todoTool.ManageTodo("list");

        Assert.Contains("No todo items", result);
    }

    [Fact]
    public async Task ListTodo_WithItems_ShowsAllTasks()
    {
        await _todoTool.ManageTodo("add", task: "Task 1");
        await _todoTool.ManageTodo("add", task: "Task 2");
        await _todoTool.ManageTodo("add", task: "Task 3");

        var result = await _todoTool.ManageTodo("list");

        Assert.Contains("3 items", result);
        Assert.Contains("Task 1", result);
        Assert.Contains("Task 2", result);
        Assert.Contains("Task 3", result);
    }

    [Fact]
    public async Task CompleteTodo_MarksAsCompleted()
    {
        await _todoTool.ManageTodo("add", task: "Task to complete");
        var result = await _todoTool.ManageTodo("complete", id: "1");

        Assert.Contains("Completed task #1", result);

        var list = await _todoTool.ManageTodo("list");
        Assert.Contains("Completed", list);
    }

    [Fact]
    public async Task CompleteTodo_WithoutId_ReturnsError()
    {
        var result = await _todoTool.ManageTodo("complete");

        Assert.Contains("Error", result);
        Assert.Contains("required", result);
    }

    [Fact]
    public async Task CompleteTodo_NonExistent_ReturnsError()
    {
        var result = await _todoTool.ManageTodo("complete", id: "999");

        Assert.Contains("Error", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task CompleteTodo_WithPendingDependencies_ReturnsWarning()
    {
        await _todoTool.ManageTodo("add", task: "Task 1");
        await _todoTool.ManageTodo("add", task: "Task 2", dependencies: "1");

        var result = await _todoTool.ManageTodo("complete", id: "2");

        Assert.Contains("Warning", result);
        Assert.Contains("dependencies", result);
    }

    [Fact]
    public async Task UpdateTodo_ChangesStatus()
    {
        await _todoTool.ManageTodo("add", task: "Task to update");
        var result = await _todoTool.ManageTodo("update", id: "1", status: "in_progress");

        Assert.Contains("Updated task #1", result);
        Assert.Contains("InProgress", result);
    }

    [Fact]
    public async Task UpdateTodo_ChangesTask()
    {
        await _todoTool.ManageTodo("add", task: "Original task");
        var result = await _todoTool.ManageTodo("update", id: "1", task: "Updated task");

        Assert.Contains("Updated task", result);
    }

    [Fact]
    public async Task UpdateTodo_WithoutId_ReturnsError()
    {
        var result = await _todoTool.ManageTodo("update", task: "New task");

        Assert.Contains("Error", result);
        Assert.Contains("required", result);
    }

    [Fact]
    public async Task RemoveTodo_DeletesTask()
    {
        await _todoTool.ManageTodo("add", task: "Task to remove");
        var result = await _todoTool.ManageTodo("remove", id: "1");

        Assert.Contains("Removed task #1", result);

        var list = await _todoTool.ManageTodo("list");
        Assert.Contains("No todo items", list);
    }

    [Fact]
    public async Task RemoveTodo_NonExistent_ReturnsError()
    {
        var result = await _todoTool.ManageTodo("remove", id: "999");

        Assert.Contains("Error", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ClearTodos_RemovesOnlyCompleted()
    {
        await _todoTool.ManageTodo("add", task: "Task 1");
        await _todoTool.ManageTodo("add", task: "Task 2");
        await _todoTool.ManageTodo("complete", id: "1");

        var result = await _todoTool.ManageTodo("clear");

        Assert.Contains("Cleared 1 completed tasks", result);
        Assert.Contains("1 tasks remaining", result);
    }

    [Fact]
    public async Task ClearTodos_NoCompleted_ReturnsMessage()
    {
        await _todoTool.ManageTodo("add", task: "Pending task");

        var result = await _todoTool.ManageTodo("clear");

        Assert.Contains("No completed tasks to clear", result);
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var result = await _todoTool.ManageTodo("unknown");

        Assert.Contains("Error", result);
        Assert.Contains("Unknown action", result);
    }

    [Fact]
    public async Task MultipleOperations_MaintainsConsistency()
    {
        // Add multiple tasks
        await _todoTool.ManageTodo("add", task: "Task 1", priority: "high");
        await _todoTool.ManageTodo("add", task: "Task 2", priority: "low");
        await _todoTool.ManageTodo("add", task: "Task 3", priority: "medium", dependencies: "1,2");

        // Update and complete
        await _todoTool.ManageTodo("update", id: "1", status: "in_progress");
        await _todoTool.ManageTodo("complete", id: "2");

        // Check state
        var list = await _todoTool.ManageTodo("list");
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
