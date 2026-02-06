using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// Tests for ModeToolFilter risk assessment and tool filtering.
/// </summary>
public class ModeToolFilterTests
{
    private readonly ModeToolFilter _filter = new();

    [Theory]
    [InlineData("read_file", AgentMode.Planning, true)]
    [InlineData("glob", AgentMode.Planning, true)]
    [InlineData("grep", AgentMode.Planning, true)]
    [InlineData("list_directory", AgentMode.Planning, true)]
    [InlineData("write_file", AgentMode.Planning, false)]
    [InlineData("shell", AgentMode.Planning, false)]
    [InlineData("delete_file", AgentMode.Planning, false)]
    public void IsToolPermitted_PlanningMode_FiltersCorrectly(string toolName, AgentMode mode, bool expected)
    {
        var result = _filter.IsToolPermitted(toolName, mode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("read_file", AgentMode.Working, true)]
    [InlineData("write_file", AgentMode.Working, true)]
    [InlineData("shell", AgentMode.Working, true)]
    [InlineData("delete_file", AgentMode.Working, true)]
    public void IsToolPermitted_WorkingMode_AllowsAllTools(string toolName, AgentMode mode, bool expected)
    {
        var result = _filter.IsToolPermitted(toolName, mode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("read_file", AgentMode.Idle, false)]
    [InlineData("write_file", AgentMode.Idle, false)]
    [InlineData("read_file", AgentMode.HumanInTheLoop, false)]
    [InlineData("write_file", AgentMode.HumanInTheLoop, false)]
    public void IsToolPermitted_IdleOrHITL_DeniesAllTools(string toolName, AgentMode mode, bool expected)
    {
        var result = _filter.IsToolPermitted(toolName, mode);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AssessRisk_DeleteFile_ReturnsRisk()
    {
        var args = new Dictionary<string, object?> { ["path"] = "test.cs" };
        var result = _filter.AssessRisk("delete_file", args);

        Assert.True(result.IsRisky);
        Assert.True(result.Level >= RiskLevel.Medium);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void AssessRisk_ReadFile_WithDefaultConfig_ReturnsSafe()
    {
        // Default config allows all reads (except *.env and secrets)
        var args = new Dictionary<string, object?> { ["path"] = "src/Program.cs" };
        var result = _filter.AssessRisk("read_file", args);

        Assert.False(result.IsRisky);
        Assert.Equal(RiskLevel.Low, result.Level);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("sudo rm -rf .")]
    [InlineData("del /s /q c:\\")]
    [InlineData("format c:")]
    public void AssessRisk_DangerousShellCommand_ReturnsCriticalRisk(string command)
    {
        var args = new Dictionary<string, object?> { ["command"] = command };

        var result = _filter.AssessRisk("shell", args);

        Assert.True(result.IsRisky);
        Assert.True(result.Level >= RiskLevel.High);
    }

    [Theory]
    [InlineData("sudo apt update")]
    [InlineData("runas /user:admin cmd")]
    public void AssessRisk_ElevatedCommand_ReturnsHighRisk(string command)
    {
        var args = new Dictionary<string, object?> { ["command"] = command };

        var result = _filter.AssessRisk("shell", args);

        Assert.True(result.IsRisky);
        Assert.True(result.Level >= RiskLevel.High);
    }

    [Fact]
    public void AssessRisk_ReadFile_WithNoArgs_ReturnsSafe()
    {
        var result = _filter.AssessRisk("read_file", null);

        Assert.False(result.IsRisky);
        Assert.Equal(RiskLevel.Low, result.Level);
    }

    [Fact]
    public void RiskAssessment_Safe_CreatesNonRiskyAssessment()
    {
        var result = RiskAssessment.Safe;

        Assert.False(result.IsRisky);
        Assert.Equal(RiskLevel.Low, result.Level);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void RiskAssessment_Risky_CreatesRiskyAssessment()
    {
        var result = RiskAssessment.Risky(RiskLevel.High, "Test reason", "Test prompt");

        Assert.True(result.IsRisky);
        Assert.Equal(RiskLevel.High, result.Level);
        Assert.Equal("Test reason", result.Reason);
        Assert.Equal("Test prompt", result.ApprovalPrompt);
    }

    #region Permission Config Tests

    [Fact]
    public void AssessRisk_AllowedReadPath_ReturnsSafe()
    {
        var config = new PermissionConfig
        {
            Read =
            [
                new PermissionRule { Pattern = "src/**/*", Action = PermissionAction.Allow }
            ],
            DefaultAction = PermissionAction.Ask
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["path"] = "src/Program.cs" };
        var result = filter.AssessRisk("read_file", args);

        Assert.False(result.IsRisky);
    }

    [Fact]
    public void AssessRisk_DeniedReadPath_ReturnsDenied()
    {
        var config = new PermissionConfig
        {
            Read =
            [
                new PermissionRule { Pattern = "**/secrets/**", Action = PermissionAction.Deny, Priority = 10 }
            ],
            DefaultAction = PermissionAction.Allow
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["path"] = "config/secrets/api-key.txt" };
        var result = filter.AssessRisk("read_file", args);

        Assert.True(result.IsRisky);
    }

    [Fact]
    public void AssessRisk_AskReadPath_ReturnsAsk()
    {
        var config = new PermissionConfig
        {
            Read =
            [
                new PermissionRule { Pattern = "*.env", Action = PermissionAction.Ask }
            ],
            DefaultAction = PermissionAction.Allow
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["path"] = ".env" };
        var result = filter.AssessRisk("read_file", args);

        Assert.True(result.IsRisky);
        Assert.Equal(RiskLevel.Medium, result.Level);
    }

    [Fact]
    public void AssessRisk_AllowedBashCommand_ReturnsSafe()
    {
        var config = new PermissionConfig
        {
            Bash =
            [
                new PermissionRule { Pattern = "git *", Action = PermissionAction.Allow },
                new PermissionRule { Pattern = "dotnet *", Action = PermissionAction.Allow }
            ],
            DefaultAction = PermissionAction.Ask
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["command"] = "git status" };
        var result = filter.AssessRisk("shell", args);

        Assert.False(result.IsRisky);
    }

    [Fact]
    public void AssessRisk_DeniedBashCommand_ReturnsDenied()
    {
        var config = new PermissionConfig
        {
            Bash =
            [
                new PermissionRule { Pattern = "dangerous_cmd *", Action = PermissionAction.Deny, Priority = 100 }
            ],
            DefaultAction = PermissionAction.Allow
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["command"] = "dangerous_cmd test" };
        var result = filter.AssessRisk("shell", args);

        Assert.True(result.IsRisky);
    }

    [Fact]
    public void AssessRisk_AllowedEditPath_ReturnsSafe()
    {
        var config = new PermissionConfig
        {
            Edit =
            [
                new PermissionRule { Pattern = "src/**/*", Action = PermissionAction.Allow }
            ],
            DefaultAction = PermissionAction.Ask
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["path"] = "src/Program.cs" };
        var result = filter.AssessRisk("write_file", args);

        Assert.False(result.IsRisky);
    }

    [Fact]
    public void AssessRisk_PriorityRules_HigherPriorityWins()
    {
        var config = new PermissionConfig
        {
            Read =
            [
                new PermissionRule { Pattern = "*", Action = PermissionAction.Allow, Priority = 0 },
                new PermissionRule { Pattern = "*.env", Action = PermissionAction.Deny, Priority = 10 }
            ]
        };
        var filter = new ModeToolFilter(config);

        var args = new Dictionary<string, object?> { ["path"] = ".env" };
        var result = filter.AssessRisk("read_file", args);

        Assert.True(result.IsRisky); // Higher priority Deny rule wins
    }

    [Fact]
    public void AssessRisk_McpTool_UsesPermissionEvaluator()
    {
        var config = new PermissionConfig
        {
            McpTools =
            [
                new PermissionRule { Pattern = "mcp__safe_*", Action = PermissionAction.Allow }
            ],
            DefaultAction = PermissionAction.Ask
        };
        var filter = new ModeToolFilter(config);

        var result = filter.AssessRisk("mcp__safe_tool", null);

        Assert.False(result.IsRisky);
    }

    #endregion

    #region PermissionEvaluator Tests

    [Theory]
    [InlineData("git status", "git *", true)]
    [InlineData("git commit -m test", "git *", true)]
    [InlineData("dotnet build", "dotnet *", true)]
    [InlineData("npm install", "npm *", true)]
    public void PermissionEvaluator_BashPatterns_MatchCorrectly(string command, string pattern, bool shouldAllow)
    {
        var config = new PermissionConfig
        {
            Bash = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny
        };
        var evaluator = new PermissionEvaluator(config);

        var result = evaluator.EvaluateBash(command);

        Assert.Equal(shouldAllow ? PermissionAction.Allow : PermissionAction.Deny, result.Action);
    }

    [Theory]
    [InlineData("test.tmp", "*.tmp", true)]
    [InlineData("obj/Debug/test.dll", "obj/**/*", true)]
    [InlineData("bin/Release/app.exe", "bin/**/*", true)]
    [InlineData("src/Program.cs", "src/**/*", true)]
    public void PermissionEvaluator_PathPatterns_MatchCorrectly(string path, string pattern, bool shouldAllow)
    {
        var config = new PermissionConfig
        {
            Read = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny
        };
        var evaluator = new PermissionEvaluator(config);

        var result = evaluator.EvaluateRead(path);

        Assert.Equal(shouldAllow ? PermissionAction.Allow : PermissionAction.Deny, result.Action);
    }

    [Fact]
    public void PermissionEvaluator_DefaultConfig_HasSensibleDefaults()
    {
        var config = PermissionConfig.CreateDefault();
        var evaluator = new PermissionEvaluator(config);

        // Should allow reading normal files
        var readResult = evaluator.EvaluateRead("src/Program.cs");
        Assert.Equal(PermissionAction.Allow, readResult.Action);

        // Should ask for .env files
        var envResult = evaluator.EvaluateRead(".env");
        Assert.Equal(PermissionAction.Ask, envResult.Action);

        // Should allow git commands
        var gitResult = evaluator.EvaluateBash("git status");
        Assert.Equal(PermissionAction.Allow, gitResult.Action);

        // Should deny secrets directory
        var secretsResult = evaluator.EvaluateRead("config/secrets/key.txt");
        Assert.Equal(PermissionAction.Deny, secretsResult.Action);
    }

    #endregion
}
