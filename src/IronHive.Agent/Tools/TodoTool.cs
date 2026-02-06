using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tools;

/// <summary>
/// Todo/task management tool for tracking work items.
/// Stores tasks in .ironhive/todo.json.
/// </summary>
public class TodoTool
{
    private readonly string _todoFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a new TodoTool with the specified working directory.
    /// </summary>
    public TodoTool(string workingDirectory)
    {
        var ironhiveDir = Path.Combine(workingDirectory, ".ironhive");
        _todoFilePath = Path.Combine(ironhiveDir, "todo.json");
    }

    /// <summary>
    /// Gets the AITool instance for this todo tool.
    /// </summary>
    public AITool GetAITool()
    {
        return AIFunctionFactory.Create(ManageTodo);
    }

    /// <summary>
    /// Manages todo items - add, list, update, complete, or clear tasks.
    /// </summary>
    [Description("Manage todo items for tracking work. Use to add, list, update, complete, or clear tasks.")]
    public async Task<string> ManageTodo(
        [Description("Action to perform: 'add', 'list', 'update', 'complete', 'remove', 'clear'")] string action,
        [Description("Task description (required for 'add')")] string? task = null,
        [Description("Task ID (required for 'update', 'complete', 'remove')")] string? id = null,
        [Description("New status for 'update': 'pending', 'in_progress', 'blocked', 'completed'")] string? status = null,
        [Description("Task priority for 'add' or 'update': 'low', 'medium', 'high'")] string? priority = null,
        [Description("Dependencies - comma-separated task IDs that must complete first")] string? dependencies = null)
    {
        return action.ToLowerInvariant() switch
        {
            "add" => await AddTodoAsync(task, priority, dependencies),
            "list" => await ListTodosAsync(),
            "update" => await UpdateTodoAsync(id, task, status, priority, dependencies),
            "complete" => await CompleteTodoAsync(id),
            "remove" => await RemoveTodoAsync(id),
            "clear" => await ClearTodosAsync(),
            _ => $"Error: Unknown action '{action}'. Use 'add', 'list', 'update', 'complete', 'remove', or 'clear'."
        };
    }

    private async Task<string> AddTodoAsync(string? task, string? priority, string? dependencies)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "Error: Task description is required for 'add' action.";
        }

        var todos = await LoadTodosAsync();
        var newId = GenerateId(todos);

        var newTodo = new TodoItem
        {
            Id = newId,
            Task = task,
            Status = TodoStatus.Pending,
            Priority = ParsePriority(priority),
            Dependencies = ParseDependencies(dependencies),
            CreatedAt = DateTime.UtcNow
        };

        todos.Items.Add(newTodo);
        await SaveTodosAsync(todos);

        return $"Added task #{newId}: {task}";
    }

    private async Task<string> ListTodosAsync()
    {
        var todos = await LoadTodosAsync();

        if (todos.Items.Count == 0)
        {
            return "No todo items.";
        }

        var lines = new List<string> { $"Todo list ({todos.Items.Count} items):", "" };

        // Group by status
        var grouped = todos.Items
            .GroupBy(t => t.Status)
            .OrderBy(g => (int)g.Key);

        foreach (var group in grouped)
        {
            lines.Add($"[{group.Key}]");
            foreach (var item in group.OrderByDescending(t => t.Priority))
            {
                var priorityMarker = item.Priority switch
                {
                    TodoPriority.High => "!",
                    TodoPriority.Medium => "·",
                    _ => " "
                };
                var deps = item.Dependencies?.Count > 0
                    ? $" (depends: {string.Join(", ", item.Dependencies)})"
                    : "";
                lines.Add($"  {priorityMarker} #{item.Id}: {item.Task}{deps}");
            }
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> UpdateTodoAsync(string? id, string? task, string? status, string? priority, string? dependencies)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Error: Task ID is required for 'update' action.";
        }

        var todos = await LoadTodosAsync();
        var todo = todos.Items.Find(t => t.Id == id);

        if (todo == null)
        {
            return $"Error: Task #{id} not found.";
        }

        if (!string.IsNullOrWhiteSpace(task))
        {
            todo.Task = task;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            todo.Status = ParseStatus(status);
        }

        if (!string.IsNullOrWhiteSpace(priority))
        {
            todo.Priority = ParsePriority(priority);
        }

        if (dependencies != null)
        {
            todo.Dependencies = ParseDependencies(dependencies);
        }

        todo.UpdatedAt = DateTime.UtcNow;
        await SaveTodosAsync(todos);

        return $"Updated task #{id}: {todo.Task} [{todo.Status}]";
    }

    private async Task<string> CompleteTodoAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Error: Task ID is required for 'complete' action.";
        }

        var todos = await LoadTodosAsync();
        var todo = todos.Items.Find(t => t.Id == id);

        if (todo == null)
        {
            return $"Error: Task #{id} not found.";
        }

        // Check dependencies
        if (todo.Dependencies?.Count > 0)
        {
            var pendingDeps = todo.Dependencies
                .Where(depId => todos.Items.Any(t => t.Id == depId && t.Status != TodoStatus.Completed))
                .ToList();

            if (pendingDeps.Count > 0)
            {
                return $"Warning: Task #{id} has incomplete dependencies: {string.Join(", ", pendingDeps)}. Complete anyway? (use 'update' with status='completed' to force)";
            }
        }

        todo.Status = TodoStatus.Completed;
        todo.CompletedAt = DateTime.UtcNow;
        todo.UpdatedAt = DateTime.UtcNow;
        await SaveTodosAsync(todos);

        return $"Completed task #{id}: {todo.Task}";
    }

    private async Task<string> RemoveTodoAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Error: Task ID is required for 'remove' action.";
        }

        var todos = await LoadTodosAsync();
        var removed = todos.Items.RemoveAll(t => t.Id == id);

        if (removed == 0)
        {
            return $"Error: Task #{id} not found.";
        }

        await SaveTodosAsync(todos);
        return $"Removed task #{id}";
    }

    private async Task<string> ClearTodosAsync()
    {
        var todos = await LoadTodosAsync();
        var count = todos.Items.Count;

        // Only clear completed tasks
        var removed = todos.Items.RemoveAll(t => t.Status == TodoStatus.Completed);

        if (removed == 0)
        {
            return "No completed tasks to clear.";
        }

        await SaveTodosAsync(todos);
        return $"Cleared {removed} completed tasks. {todos.Items.Count} tasks remaining.";
    }

    private async Task<TodoList> LoadTodosAsync()
    {
        if (!File.Exists(_todoFilePath))
        {
            return new TodoList();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_todoFilePath);
            return JsonSerializer.Deserialize<TodoList>(json, JsonOptions) ?? new TodoList();
        }
        catch
        {
            return new TodoList();
        }
    }

    private async Task SaveTodosAsync(TodoList todos)
    {
        var directory = Path.GetDirectoryName(_todoFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(todos, JsonOptions);
        await File.WriteAllTextAsync(_todoFilePath, json);
    }

    private static string GenerateId(TodoList todos)
    {
        var maxId = 0;
        foreach (var item in todos.Items)
        {
            if (int.TryParse(item.Id, out var id) && id > maxId)
            {
                maxId = id;
            }
        }
        return (maxId + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static TodoPriority ParsePriority(string? priority)
    {
        return priority?.ToLowerInvariant() switch
        {
            "high" or "h" => TodoPriority.High,
            "medium" or "m" => TodoPriority.Medium,
            "low" or "l" => TodoPriority.Low,
            _ => TodoPriority.Medium
        };
    }

    private static TodoStatus ParseStatus(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "pending" or "p" => TodoStatus.Pending,
            "in_progress" or "inprogress" or "i" => TodoStatus.InProgress,
            "blocked" or "b" => TodoStatus.Blocked,
            "completed" or "done" or "c" => TodoStatus.Completed,
            _ => TodoStatus.Pending
        };
    }

    private static List<string>? ParseDependencies(string? dependencies)
    {
        if (string.IsNullOrWhiteSpace(dependencies))
        {
            return null;
        }

        return dependencies
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// Container for todo items.
/// </summary>
public class TodoList
{
    public List<TodoItem> Items { get; set; } = [];
}

/// <summary>
/// A single todo item.
/// </summary>
public class TodoItem
{
    public string Id { get; set; } = "";
    public string Task { get; set; } = "";
    public TodoStatus Status { get; set; }
    public TodoPriority Priority { get; set; }
    public List<string>? Dependencies { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Status of a todo item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoStatus
{
    Pending,
    InProgress,
    Blocked,
    Completed
}

/// <summary>
/// Priority of a todo item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoPriority
{
    Low,
    Medium,
    High
}
