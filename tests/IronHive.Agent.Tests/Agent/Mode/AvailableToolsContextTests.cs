using IronHive.Agent.Mode;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// Tests for AvailableToolsContext — ambient available-tools tracking.
/// </summary>
public class AvailableToolsContextTests
{
    private readonly AvailableToolsContext _ctx = new();

    [Fact]
    public void IsAvailable_BeforeSetAvailableTools_ReturnsFalse()
    {
        Assert.False(_ctx.IsAvailable("ReadFile"));
    }

    [Fact]
    public void AvailableToolNames_BeforeSetAvailableTools_IsEmpty()
    {
        Assert.Empty(_ctx.AvailableToolNames);
    }

    [Fact]
    public void SetAvailableTools_Then_IsAvailable_ReturnsTrue()
    {
        _ctx.SetAvailableTools(["ReadFile", "WriteFile", "GlobFiles"]);

        Assert.True(_ctx.IsAvailable("ReadFile"));
        Assert.True(_ctx.IsAvailable("WriteFile"));
        Assert.True(_ctx.IsAvailable("GlobFiles"));
    }

    [Fact]
    public void SetAvailableTools_Then_IsAvailable_ReturnsFalse_ForAbsentTool()
    {
        _ctx.SetAvailableTools(["ReadFile", "WriteFile"]);

        Assert.False(_ctx.IsAvailable("DeleteFile"));
    }

    [Theory]
    [InlineData("readfile")]
    [InlineData("READFILE")]
    [InlineData("readFile")]
    [InlineData("ReadFile")]
    public void IsAvailable_IsCaseInsensitive(string name)
    {
        _ctx.SetAvailableTools(["ReadFile"]);

        Assert.True(_ctx.IsAvailable(name));
    }

    [Fact]
    public void AvailableToolNames_ReflectsSetTools()
    {
        _ctx.SetAvailableTools(["ReadFile", "WriteFile"]);

        Assert.Equal(2, _ctx.AvailableToolNames.Count);
        Assert.Contains("ReadFile", _ctx.AvailableToolNames);
        Assert.Contains("WriteFile", _ctx.AvailableToolNames);
    }

    [Fact]
    public void SetAvailableTools_CalledTwice_ReflectsLatestSet()
    {
        _ctx.SetAvailableTools(["ReadFile", "WriteFile", "DeleteFile"]);
        _ctx.SetAvailableTools(["ReadFile"]);

        Assert.True(_ctx.IsAvailable("ReadFile"));
        Assert.False(_ctx.IsAvailable("WriteFile"));
        Assert.False(_ctx.IsAvailable("DeleteFile"));
        Assert.Single(_ctx.AvailableToolNames);
    }

    [Fact]
    public void SetAvailableTools_WithEmpty_ClearsAvailableTools()
    {
        _ctx.SetAvailableTools(["ReadFile"]);
        _ctx.SetAvailableTools([]);

        Assert.False(_ctx.IsAvailable("ReadFile"));
        Assert.Empty(_ctx.AvailableToolNames);
    }

    [Fact]
    public void SetAvailableTools_WithDuplicates_IsAvailable_StillReturnsTrue()
    {
        _ctx.SetAvailableTools(["ReadFile", "ReadFile"]);

        Assert.True(_ctx.IsAvailable("ReadFile"));
    }
}
