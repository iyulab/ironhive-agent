using Ironbees.Core;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace IronHive.Agent.Tests.Ironbees;

/// <summary>
/// <see cref="IronbeesOptions.ShellTools"/> reaches the shell tool the framework adapter offers its agents, so a host that
/// runs Ironbees agents with tool execution keeps its own secrets out of their commands too.
/// </summary>
public sealed class IronbeesShellToolOptionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ironbees-shell").FullName;
    private readonly string _secretName = "IRONHIVE_IRONBEES_SECRET_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public IronbeesShellToolOptionsTests() => Environment.SetEnvironmentVariable(_secretName, "ironbees-secret-789");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_secretName, null);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Echo => OperatingSystem.IsWindows() ? $"echo %{_secretName}%" : $"echo ${_secretName}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheShellToolOptions_DecideWhatTheAgentsCommandsSee(bool removeIt)
    {
        var client = new MockChatClient()
            .EnqueueToolCallResponse(nameof(ToolProvider.ExecuteCommand),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = Echo }))
            .EnqueueResponse("done");
        var services = new ServiceCollection();
        // The permission rules allow the call — what is under test is the command's environment, not the approval gate.
        var policy = Substitute.For<IToolCallPolicy>();
        policy.Evaluate(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>()).Returns(RiskAssessment.Safe);
        services.AddSingleton(policy);
        services.AddIronbees(options =>
        {
            options.AgentsDirectory = _dir;
            options.EnableToolExecution = true;
            options.WorkingDirectory = _dir;
            options.ChatClientFactory = _ => client;
            options.ShellTools = removeIt ? new ShellToolOptions { RemoveEnvironmentVariables = [_secretName] } : null;
        });
        using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<ILLMFrameworkAdapter>();
        var agent = await adapter.CreateAgentAsync(new AgentConfig
        {
            Name = "worker",
            Description = "test",
            Version = "1.0.0",
            SystemPrompt = "You are a test assistant.",
            Model = new ModelConfig { Deployment = "test-model", MaxTokens = 1000 },
        }, TestContext.Current.CancellationToken);

        await adapter.RunAsync(agent, "print the variable", TestContext.Current.CancellationToken);

        var toolResult = client.ReceivedMessages[^1]
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single().Result?.ToString();
        if (removeIt)
        {
            Assert.DoesNotContain("ironbees-secret-789", toolResult);
        }
        else
        {
            Assert.Contains("ironbees-secret-789", toolResult);
        }
    }
}
