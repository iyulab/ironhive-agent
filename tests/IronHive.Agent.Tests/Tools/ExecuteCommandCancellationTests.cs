using System.Diagnostics;
using IronHive.Agent.Tools;
using Xunit;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// A cancelled turn stops the command it is running. Before the tool took the invocation's token, the command ran on
/// until its own timeout (30 s by default) after the turn had already been cancelled.
/// </summary>
public class ExecuteCommandCancellationTests
{
    [Fact]
    public async Task Cancelling_the_call_stops_a_long_command_well_before_its_timeout()
    {
        var dir = Directory.CreateTempSubdirectory("execcancel").FullName;
        try
        {
            var tools = new ToolProvider(dir);
            // Shell built-ins only: a loop that runs far longer than the test waits.
            var command = OperatingSystem.IsWindows()
                ? "for /L %i in (1,1,2000000000) do @rem"
                : "sleep 120";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            var stopwatch = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.ExecuteCommand(command, timeoutMs: 60_000, cancellationToken: cts.Token));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"cancellation took {stopwatch.Elapsed}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
