using System.Text.Json.Nodes;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// A server's <c>notifications/elicitation/complete</c> ends the wait it names — the person finished the step where the host
/// never heard of it — and nothing else: other ids, a missing id and the caller's own cancellation behave as before.
/// </summary>
public sealed class ElicitationWaitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ElicitRequestParams Request(string? id) => new() { Mode = "url", Message = "approve", Url = "https://example.invalid/a", ElicitationId = id };

    private static JsonRpcNotification Completed(string id) => new()
    {
        Method = NotificationMethods.ElicitationCompleteNotification,
        Params = new JsonObject { ["elicitationId"] = id },
    };

    /// <summary>A wait that ends only when its token does, the way a handler waiting on a person does.</summary>
    private static Func<CancellationToken, ValueTask<ElicitResult>> WaitsForever(TaskCompletionSource started) => async token =>
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        return new ElicitResult { Action = "cancel" };
    };

    [Fact]
    public async Task Completion_EndsTheNamedWait_AsAccept()
    {
        var waits = new ElicitationWaits();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = waits.RunAsync(Request("e1"), WaitsForever(started), Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        waits.Complete(Completed("e1"));

        (await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Action.Should().Be("accept");
    }

    [Fact]
    public async Task Completion_OverridesAHandlerThatReturnsAnAnswerAfterBeingCancelled()
    {
        var waits = new ElicitationWaits();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = waits.RunAsync(
            Request("e1"),
            async token =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                }

                return new ElicitResult { Action = "cancel" };
            },
            Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        waits.Complete(Completed("e1"));

        (await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Action.Should().Be("accept", "the server said the step is done; the call is sent again");
    }

    [Fact]
    public async Task Completion_ThatArrivesFirst_AnswersTheLaterWaitWithoutAsking()
    {
        var waits = new ElicitationWaits();
        var asked = false;
        waits.Complete(Completed("e1"));

        var result = await waits.RunAsync(Request("e1"), _ => { asked = true; return ValueTask.FromResult(new ElicitResult { Action = "decline" }); }, Ct);

        result.Action.Should().Be("accept");
        asked.Should().BeFalse("the person had already finished");
    }

    [Fact]
    public async Task Completion_OfAnotherId_LeavesTheWaitOpen()
    {
        var waits = new ElicitationWaits();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = waits.RunAsync(Request("e1"), WaitsForever(started), Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        waits.Complete(Completed("e2"));
        await Task.Delay(200, Ct);

        run.IsCompleted.Should().BeFalse("only e1 was reported complete (positive control: e1 below ends it)");
        waits.Complete(Completed("e1"));
        (await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Action.Should().Be("accept");
    }

    [Fact]
    public async Task TheCallersCancellation_StillCancelsTheWait()
    {
        var waits = new ElicitationWaits();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = waits.RunAsync(Request("e1"), WaitsForever(started), caller.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await caller.CancelAsync();

        await FluentActions.Awaiting(() => run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ARequestWithoutAnId_IsPassedThrough_AndMalformedCompletionsAreIgnored()
    {
        var waits = new ElicitationWaits();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = waits.RunAsync(Request(null), WaitsForever(started), Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        waits.Complete(new JsonRpcNotification { Method = NotificationMethods.ElicitationCompleteNotification });
        waits.Complete(new JsonRpcNotification { Method = NotificationMethods.ElicitationCompleteNotification, Params = new JsonObject() });
        waits.Complete(new JsonRpcNotification { Method = NotificationMethods.ElicitationCompleteNotification, Params = new JsonObject { ["elicitationId"] = 5 } });
        await Task.Delay(200, Ct);

        run.IsCompleted.Should().BeFalse("without an id nothing can name this wait");
    }

    [Fact]
    public async Task AnAnswerFromThePerson_StillWins_WhenNoCompletionComes()
    {
        var waits = new ElicitationWaits();

        var result = await waits.RunAsync(Request("e1"), _ => ValueTask.FromResult(new ElicitResult { Action = "decline" }), Ct);

        result.Action.Should().Be("decline");
    }
}
