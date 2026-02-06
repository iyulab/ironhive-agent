using IronHive.Agent.Mode;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// Tests for ApprovalRequest and ApprovalResult records.
/// </summary>
public class HumanApprovalTests
{
    [Fact]
    public void ApprovalResult_Approve_CreatesApprovedResult()
    {
        var result = ApprovalResult.Approve();

        Assert.True(result.Approved);
        Assert.False(result.AlwaysApprove);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void ApprovalResult_ApproveAlways_SetsAlwaysApproveFlag()
    {
        var result = ApprovalResult.Approve(alwaysApprove: true);

        Assert.True(result.Approved);
        Assert.True(result.AlwaysApprove);
    }

    [Fact]
    public void ApprovalResult_Reject_CreatesRejectedResult()
    {
        var result = ApprovalResult.Reject("Test reason");

        Assert.False(result.Approved);
        Assert.False(result.AlwaysApprove);
        Assert.Equal("Test reason", result.RejectionReason);
    }

    [Fact]
    public void ApprovalResult_RejectWithoutReason_HasNullReason()
    {
        var result = ApprovalResult.Reject();

        Assert.False(result.Approved);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void ApprovalRequest_RequiredProperties_MustBeSet()
    {
        var riskAssessment = RiskAssessment.Risky(RiskLevel.High, "Test");

        var request = new ApprovalRequest
        {
            ToolName = "test_tool",
            RiskAssessment = riskAssessment,
            Description = "Test description"
        };

        Assert.Equal("test_tool", request.ToolName);
        Assert.Equal(riskAssessment, request.RiskAssessment);
        Assert.Equal("Test description", request.Description);
    }

    [Fact]
    public void ApprovalRequest_OptionalArguments_CanBeNull()
    {
        var request = new ApprovalRequest
        {
            ToolName = "test_tool",
            RiskAssessment = RiskAssessment.Safe
        };

        Assert.Null(request.Arguments);
    }

    [Fact]
    public void ApprovalRequest_WithArguments_StoresCorrectly()
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = "/test/path",
            ["recursive"] = true
        };

        var request = new ApprovalRequest
        {
            ToolName = "delete_file",
            RiskAssessment = RiskAssessment.Risky(RiskLevel.High, "Deletion"),
            Arguments = args
        };

        Assert.NotNull(request.Arguments);
        Assert.Equal(2, request.Arguments.Count);
        Assert.Equal("/test/path", request.Arguments["path"]);
    }

    [Fact]
    public void ApprovalResult_WithModifiedArguments_StoresCorrectly()
    {
        var modifiedArgs = new Dictionary<string, object?>
        {
            ["path"] = "/safer/path"
        };

        var result = new ApprovalResult
        {
            Approved = true,
            ModifiedArguments = modifiedArgs
        };

        Assert.True(result.Approved);
        Assert.NotNull(result.ModifiedArguments);
        Assert.Equal("/safer/path", result.ModifiedArguments["path"]);
    }
}
