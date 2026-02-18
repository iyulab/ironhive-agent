using IronHive.Agent.Memory;

namespace IronHive.Agent.Tests.Memory;

public class MemoryRecallResultTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var result = new MemoryRecallResult();

        Assert.Empty(result.UserMemories);
        Assert.Empty(result.SessionMemories);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void TotalCount_ShouldSumBothLists()
    {
        var result = new MemoryRecallResult
        {
            UserMemories =
            [
                new MemoryItem { Content = "Fact 1" },
                new MemoryItem { Content = "Fact 2" }
            ],
            SessionMemories =
            [
                new MemoryItem { Content = "Context 1" }
            ]
        };

        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public void FormatAsContext_EmptyMemories_ShouldReturnEmpty()
    {
        var result = new MemoryRecallResult();
        Assert.Equal(string.Empty, result.FormatAsContext());
    }

    [Fact]
    public void FormatAsContext_OnlyUserMemories_ShouldFormatCorrectly()
    {
        var result = new MemoryRecallResult
        {
            UserMemories =
            [
                new MemoryItem { Content = "User prefers dark mode" },
                new MemoryItem { Content = "User speaks Korean" }
            ]
        };

        var context = result.FormatAsContext();
        Assert.Contains("## User Knowledge", context);
        Assert.Contains("- User prefers dark mode", context);
        Assert.Contains("- User speaks Korean", context);
        Assert.DoesNotContain("## Session Context", context);
    }

    [Fact]
    public void FormatAsContext_OnlySessionMemories_ShouldFormatCorrectly()
    {
        var result = new MemoryRecallResult
        {
            SessionMemories =
            [
                new MemoryItem { Content = "Working on auth", Role = "user" },
                new MemoryItem { Content = "Added JWT support", Role = "assistant" }
            ]
        };

        var context = result.FormatAsContext();
        Assert.DoesNotContain("## User Knowledge", context);
        Assert.Contains("## Session Context", context);
        Assert.Contains("- [user] Working on auth", context);
        Assert.Contains("- [assistant] Added JWT support", context);
    }

    [Fact]
    public void FormatAsContext_BothMemories_ShouldIncludeBothSections()
    {
        var result = new MemoryRecallResult
        {
            UserMemories = [new MemoryItem { Content = "fact" }],
            SessionMemories = [new MemoryItem { Content = "context", Role = "user" }]
        };

        var context = result.FormatAsContext();
        Assert.Contains("## User Knowledge", context);
        Assert.Contains("## Session Context", context);
    }
}

public class MemoryItemTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var item = new MemoryItem { Content = "Test" };

        Assert.Null(item.Role);
        Assert.Equal(0f, item.Score);
        Assert.Equal(default, item.CreatedAt);
    }

    [Fact]
    public void ShouldInitialize_WithAllFields()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new MemoryItem
        {
            Content = "User prefers TypeScript",
            Role = "user",
            Score = 0.95f,
            CreatedAt = now
        };

        Assert.Equal("User prefers TypeScript", item.Content);
        Assert.Equal("user", item.Role);
        Assert.Equal(0.95f, item.Score);
        Assert.Equal(now, item.CreatedAt);
    }
}
