using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class GoalReminderTests
{
    [Fact]
    public void SetGoalFromFirstUserMessage_ExtractsGoal()
    {
        var reminder = new GoalReminder();
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.User, "Please help me refactor this code"),
            new(ChatRole.Assistant, "Sure!")
        };

        reminder.SetGoalFromFirstUserMessage(history);

        Assert.Equal("Please help me refactor this code", reminder.CurrentGoal);
    }

    [Fact]
    public void SetGoalFromFirstUserMessage_TruncatesLongGoal()
    {
        var reminder = new GoalReminder();
        var longGoal = new string('x', 600);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, longGoal)
        };

        reminder.SetGoalFromFirstUserMessage(history);

        Assert.True(reminder.CurrentGoal!.Length <= 500);
        Assert.EndsWith("...", reminder.CurrentGoal);
    }

    [Fact]
    public void ShouldInjectReminder_ReturnsFalse_WhenDisabled()
    {
        var options = new GoalReminderOptions { Enabled = false };
        var reminder = new GoalReminder(options);
        reminder.CurrentGoal = "Test goal";

        var history = CreateHistory(10);

        Assert.False(reminder.ShouldInjectReminder(history));
    }

    [Fact]
    public void ShouldInjectReminder_ReturnsFalse_WhenNoGoal()
    {
        var reminder = new GoalReminder();
        var history = CreateHistory(10);

        Assert.False(reminder.ShouldInjectReminder(history));
    }

    [Fact]
    public void ShouldInjectReminder_ReturnsFalse_WhenTooFewMessages()
    {
        var options = new GoalReminderOptions { MinMessagesBeforeReminder = 10 };
        var reminder = new GoalReminder(options);
        reminder.CurrentGoal = "Test goal";

        var history = CreateHistory(3); // Only 6 messages (below 10)

        Assert.False(reminder.ShouldInjectReminder(history));
    }

    [Fact]
    public void ShouldInjectReminder_ReturnsTrue_WhenConditionsMet()
    {
        var options = new GoalReminderOptions { MinMessagesBeforeReminder = 4 };
        var reminder = new GoalReminder(options);
        reminder.CurrentGoal = "Test goal";

        var history = CreateHistory(5); // 10 non-system messages

        Assert.True(reminder.ShouldInjectReminder(history));
    }

    [Fact]
    public void CreateReminderMessage_CreatesCorrectMessage()
    {
        var reminder = new GoalReminder();
        reminder.CurrentGoal = "Implement feature X";

        var message = reminder.CreateReminderMessage();

        Assert.Equal(ChatRole.System, message.Role);
        Assert.Contains("Implement feature X", message.Text);
        Assert.Contains("[REMINDER]", message.Text);
    }

    [Fact]
    public void CreateReminderMessage_ThrowsWhenNoGoal()
    {
        var reminder = new GoalReminder();

        Assert.Throws<InvalidOperationException>(() => reminder.CreateReminderMessage());
    }

    [Fact]
    public void CreateReminderMessage_UsesCustomTemplate()
    {
        var options = new GoalReminderOptions
        {
            ReminderTemplate = "*** GOAL: {goal} ***"
        };
        var reminder = new GoalReminder(options);
        reminder.CurrentGoal = "Test goal";

        var message = reminder.CreateReminderMessage();

        Assert.Equal("*** GOAL: Test goal ***", message.Text);
    }

    [Fact]
    public void InjectReminderIfNeeded_ReturnsOriginal_WhenNotNeeded()
    {
        var reminder = new GoalReminder();
        // No goal set
        var history = CreateHistory(10);

        var result = reminder.InjectReminderIfNeeded(history);

        Assert.Same(history, result);
    }

    [Fact]
    public void InjectReminderIfNeeded_AppendsReminder_WhenNeeded()
    {
        var options = new GoalReminderOptions { MinMessagesBeforeReminder = 4 };
        var reminder = new GoalReminder(options);
        reminder.CurrentGoal = "Test goal";
        var history = CreateHistory(5);

        var result = reminder.InjectReminderIfNeeded(history);

        Assert.Equal(history.Count + 1, result.Count);
        Assert.Contains("[REMINDER]", result[^1].Text);
    }

    [Fact]
    public void ClearGoal_RemovesCurrentGoal()
    {
        var reminder = new GoalReminder();
        reminder.CurrentGoal = "Test goal";

        reminder.ClearGoal();

        Assert.Null(reminder.CurrentGoal);
    }

    [Fact]
    public void DefaultOptions_HasCorrectDefaults()
    {
        var options = new GoalReminderOptions();

        Assert.True(options.Enabled);
        Assert.Equal(6, options.MinMessagesBeforeReminder);
        Assert.Contains("{goal}", options.ReminderTemplate);
    }

    private static List<ChatMessage> CreateHistory(int turns)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };

        for (var i = 0; i < turns; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"User message {i}"));
            history.Add(new ChatMessage(ChatRole.Assistant, $"Assistant response {i}"));
        }

        return history;
    }
}
