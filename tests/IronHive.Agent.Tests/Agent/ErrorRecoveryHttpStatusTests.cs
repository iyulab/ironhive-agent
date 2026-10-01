using System.Net;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A provider refusal carries its HTTP status. Retrying a bad key or a rejected request the same way gets the same answer,
/// so only transient statuses (408, 5xx, 429) are retried; before, every <see cref="HttpRequestException"/> was a
/// "network error" and was retried once after a delay.
/// </summary>
public class ErrorRecoveryHttpStatusTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RecoveryAction.Escalate)]
    [InlineData(HttpStatusCode.Forbidden, RecoveryAction.Escalate)]
    [InlineData(HttpStatusCode.BadRequest, RecoveryAction.TryAlternative)]
    [InlineData(HttpStatusCode.NotFound, RecoveryAction.TryAlternative)]
    [InlineData(HttpStatusCode.TooManyRequests, RecoveryAction.WaitAndRetry)]
    [InlineData(HttpStatusCode.RequestTimeout, RecoveryAction.WaitAndRetry)]
    [InlineData(HttpStatusCode.InternalServerError, RecoveryAction.WaitAndRetry)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RecoveryAction.WaitAndRetry)]
    public void AnHttpRefusal_IsClassifiedByItsStatus(HttpStatusCode status, RecoveryAction expected)
    {
        var analysis = new ErrorRecoveryService().AnalyzeException(new HttpRequestException("refused", null, status));

        Assert.Equal(expected, analysis.RecommendedAction);
    }

    [Fact]
    public void AnHttpFailureWithoutAStatus_IsStillANetworkError()
    {
        var analysis = new ErrorRecoveryService().AnalyzeException(new HttpRequestException("connection reset"));

        Assert.Equal(RecoveryAction.WaitAndRetry, analysis.RecommendedAction);
    }

    [Fact]
    public async Task ATurnRefusedWith401_IsNotRetried()
    {
        var client = new MockChatClient()
            .EnqueueError(new HttpRequestException("invalid api key", null, HttpStatusCode.Unauthorized))
            .EnqueueResponse("never");
        var loop = new AgentLoop(client, errorRecovery: new ErrorRecoveryService());

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => loop.RunAsync("hi", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, thrown.StatusCode);
        Assert.Single(client.ReceivedMessages);
    }
}
