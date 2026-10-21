using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// A host keeps its own secrets out of the commands the model runs: <see cref="ShellToolOptions.RemoveEnvironmentVariables"/>
/// takes them out of the child's environment, and everything else is still inherited.
/// </summary>
public sealed class ExecuteCommandEnvironmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("execenv").FullName;
    private readonly string _secretName = "IRONHIVE_TEST_SECRET_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _keptName = "IRONHIVE_TEST_KEPT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public ExecuteCommandEnvironmentTests()
    {
        Environment.SetEnvironmentVariable(_secretName, "secret-value-123");
        Environment.SetEnvironmentVariable(_keptName, "kept-value-456");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_secretName, null);
        Environment.SetEnvironmentVariable(_keptName, null);
        Directory.Delete(_dir, recursive: true);
    }

    private static string Echo(string name) => OperatingSystem.IsWindows() ? $"echo %{name}%" : $"echo ${name}";

    [Fact]
    public async Task By_default_a_command_inherits_the_whole_environment()
    {
        var tools = new ToolProvider(_dir);

        var result = await tools.ExecuteCommand(Echo(_secretName), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("secret-value-123", result);
    }

    [Fact]
    public async Task A_removed_variable_is_not_visible_to_the_command_and_the_rest_still_is()
    {
        var tools = new ToolProvider(_dir, options: null, new ShellToolOptions { RemoveEnvironmentVariables = [_secretName] });

        var secret = await tools.ExecuteCommand(Echo(_secretName), cancellationToken: TestContext.Current.CancellationToken);
        var kept = await tools.ExecuteCommand(Echo(_keptName), cancellationToken: TestContext.Current.CancellationToken);
        // PATH is inherited: a command found through it still runs.
        var path = await tools.ExecuteCommand(OperatingSystem.IsWindows() ? "where cmd" : "command -v sh",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret-value-123", secret);
        Assert.Contains("kept-value-456", kept);
        Assert.Contains("Exit code: 0", path);
    }

    [Fact]
    public async Task Removing_does_not_change_the_host_process_environment()
    {
        var tools = new ToolProvider(_dir, options: null, new ShellToolOptions { RemoveEnvironmentVariables = [_secretName] });

        await tools.ExecuteCommand(Echo(_secretName), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("secret-value-123", Environment.GetEnvironmentVariable(_secretName));
    }

    [Fact]
    public async Task GetAll_passes_the_shell_options_to_the_shell_tool()
    {
        var tools = BuiltInTools.GetAll(_dir, options: null, new ShellToolOptions { RemoveEnvironmentVariables = [_secretName] });
        var shell = tools.OfType<AIFunction>().Single(t => t.Name == nameof(ToolProvider.ExecuteCommand));

        var result = await shell.InvokeAsync(
            new AIFunctionArguments { ["command"] = Echo(_secretName) }, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret-value-123", result?.ToString());
    }
}
