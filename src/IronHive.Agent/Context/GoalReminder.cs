using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Options for goal reminder injection.
/// </summary>
public class GoalReminderOptions
{
    /// <summary>
    /// Whether to inject goal reminder at the end of context.
    /// Default is true.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Minimum number of messages before goal reminder is injected.
    /// Default is 6 (3 turns).
    /// </summary>
    public int MinMessagesBeforeReminder { get; init; } = 6;

    /// <summary>
    /// Template for the goal reminder message.
    /// Use {goal} placeholder for the goal text.
    /// </summary>
    public string ReminderTemplate { get; init; } = "[REMINDER] Current goal: {goal}";
}

/// <summary>
/// Injects goal reminders at the end of context to prevent "lost-in-the-middle" problem.
/// Research shows models have recency bias and may forget earlier goals in long contexts.
/// </summary>
public class GoalReminder
{
    private readonly GoalReminderOptions _options;
    private string? _currentGoal;

    public GoalReminder(GoalReminderOptions? options = null)
    {
        _options = options ?? new GoalReminderOptions();
    }

    /// <summary>
    /// Gets or sets the current goal to be reminded.
    /// </summary>
    public string? CurrentGoal
    {
        get => _currentGoal;
        set => _currentGoal = value;
    }

    /// <summary>
    /// Sets the current goal from the first user message.
    /// </summary>
    public void SetGoalFromFirstUserMessage(IReadOnlyList<ChatMessage> history)
    {
        var firstUserMessage = history.FirstOrDefault(m => m.Role == ChatRole.User);
        if (firstUserMessage is not null && !string.IsNullOrWhiteSpace(firstUserMessage.Text))
        {
            // Truncate long goals to a reasonable length
            var goalText = firstUserMessage.Text;
            if (goalText.Length > 500)
            {
                goalText = goalText[..497] + "...";
            }
            _currentGoal = goalText;
        }
    }

    /// <summary>
    /// Checks if a goal reminder should be injected.
    /// </summary>
    public bool ShouldInjectReminder(IReadOnlyList<ChatMessage> history)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_currentGoal))
        {
            return false;
        }

        // Count non-system messages
        var messageCount = history.Count(m => m.Role != ChatRole.System);
        return messageCount >= _options.MinMessagesBeforeReminder;
    }

    /// <summary>
    /// Creates a goal reminder message.
    /// </summary>
    public ChatMessage CreateReminderMessage()
    {
        if (string.IsNullOrWhiteSpace(_currentGoal))
        {
            throw new InvalidOperationException("No goal has been set.");
        }

        var reminderText = _options.ReminderTemplate.Replace("{goal}", _currentGoal);
        return new ChatMessage(ChatRole.System, reminderText);
    }

    /// <summary>
    /// Injects goal reminder into the history if needed.
    /// Returns a new list with the reminder appended (does not modify original).
    /// </summary>
    public IReadOnlyList<ChatMessage> InjectReminderIfNeeded(IReadOnlyList<ChatMessage> history)
    {
        if (!ShouldInjectReminder(history))
        {
            return history;
        }

        var result = new List<ChatMessage>(history)
        {
            CreateReminderMessage()
        };

        return result;
    }

    /// <summary>
    /// Clears the current goal.
    /// </summary>
    public void ClearGoal()
    {
        _currentGoal = null;
    }
}
