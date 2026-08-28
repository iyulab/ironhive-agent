using System.Reflection;
using AwesomeAssertions;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// Exercises the per-call <c>overrideOptions</c> seam (<see cref="ChatOptionsOverride.Apply"/>,
/// invoked internally by both <see cref="AgentLoop"/> and <c>ThinkingAgentLoop</c>) through the
/// public <see cref="AgentLoop.RunAsync(string, ChatOptions?, CancellationToken)"/> surface, so the
/// assertions cover the same path a caller like docket #135 (iyulab/ironhive-agent) actually hit.
/// </summary>
public class ChatOptionsOverrideTests
{
    [Fact]
    public async Task RunAsync_ToolModeOverride_ReachesTheRequest()
    {
        // Regression guard for docket #135 (iyulab/ironhive-agent): ChatOptionsOverride.Apply
        // silently dropped ToolMode, so a caller retrying with ToolMode.None to force a text-only
        // response still had tool calls executed against it.
        var mockClient = new MockChatClient().EnqueueResponse("done");
        var agentLoop = new AgentLoop(mockClient);

        await agentLoop.RunAsync("retry without tools", new ChatOptions { ToolMode = ChatToolMode.None }, CancellationToken.None);

        mockClient.ReceivedOptions.Should().ContainSingle();
        mockClient.ReceivedOptions[0]!.ToolMode.Should().Be(ChatToolMode.None);
    }

    [Fact]
    public async Task RunAsync_EveryOverrideField_ReachesTheRequest()
    {
        var mockClient = new MockChatClient().EnqueueResponse("done");
        var agentLoop = new AgentLoop(mockClient);

        var overrideOptions = new ChatOptions
        {
            ConversationId = "conv-override",
            Instructions = "instructions-override",
            Temperature = 0.9f,
            MaxOutputTokens = 999,
            TopP = 0.8f,
            TopK = 40,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.2f,
            Seed = 12345,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            ResponseFormat = ChatResponseFormat.Text,
            ModelId = "model-override",
            StopSequences = ["STOP"],
            AllowMultipleToolCalls = false,
            ToolMode = ChatToolMode.None,
            AllowBackgroundResponses = true,
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
        };

        await agentLoop.RunAsync("prompt", overrideOptions, CancellationToken.None);

        var received = mockClient.ReceivedOptions.Should().ContainSingle().Subject!;
        received.ConversationId.Should().Be(overrideOptions.ConversationId);
        received.Instructions.Should().Be(overrideOptions.Instructions);
        received.Temperature.Should().Be(overrideOptions.Temperature);
        received.MaxOutputTokens.Should().Be(overrideOptions.MaxOutputTokens);
        received.TopP.Should().Be(overrideOptions.TopP);
        received.TopK.Should().Be(overrideOptions.TopK);
        received.FrequencyPenalty.Should().Be(overrideOptions.FrequencyPenalty);
        received.PresencePenalty.Should().Be(overrideOptions.PresencePenalty);
        received.Seed.Should().Be(overrideOptions.Seed);
        received.Reasoning.Should().BeSameAs(overrideOptions.Reasoning);
        received.ResponseFormat.Should().BeSameAs(overrideOptions.ResponseFormat);
        received.ModelId.Should().Be(overrideOptions.ModelId);
        received.StopSequences.Should().BeSameAs(overrideOptions.StopSequences);
        received.AllowMultipleToolCalls.Should().Be(overrideOptions.AllowMultipleToolCalls);
        received.ToolMode.Should().Be(overrideOptions.ToolMode);
        received.AllowBackgroundResponses.Should().Be(overrideOptions.AllowBackgroundResponses);
        received.ContinuationToken.Should().BeSameAs(overrideOptions.ContinuationToken);
    }

    [Fact]
    public async Task RunAsync_UnsetOverrideFields_KeepBaselineValues()
    {
        var mockClient = new MockChatClient().EnqueueResponse("done");
        var agentOptions = new AgentOptions { Temperature = 0.42f, MaxTokens = 4096 };
        var agentLoop = new AgentLoop(mockClient, agentOptions);

        await agentLoop.RunAsync("prompt", new ChatOptions(), CancellationToken.None);

        var received = mockClient.ReceivedOptions.Should().ContainSingle().Subject!;
        received.Temperature.Should().Be(0.42f);
        received.MaxOutputTokens.Should().Be(4096);
    }

    /// <summary>
    /// Convention teeth (CONVENTIONS.md §1 — "봉인된 변형 집합 확장 시 조용한 흡수 금지"):
    /// <see cref="ChatOptionsOverride.Apply"/> merges a fixed, hand-written list of
    /// <see cref="ChatOptions"/> properties. If a future <c>Microsoft.Extensions.AI.Abstractions</c>
    /// upgrade adds a new property, this test fails instead of the new property silently becoming
    /// unreachable through the per-call override seam the way <c>ToolMode</c> did (docket #135).
    /// </summary>
    [Fact]
    public void ChatOptionsOverrideSource_ReferencesEveryChatOptionsProperty()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "IronHive.Agent", "Loop", "ChatOptionsOverride.cs"));
        File.Exists(sourcePath).Should().BeTrue($"expected to find the source file at {sourcePath}");
        var source = File.ReadAllText(sourcePath);

        var propertyNames = typeof(ChatOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        var missing = propertyNames
            .Where(name => !source.Contains($".{name}", StringComparison.Ordinal))
            .ToList();

        missing.Should().BeEmpty(
            "every ChatOptions property must be explicitly merged (or explicitly excluded with a " +
            "documented reason) in ChatOptionsOverride.Apply — missing: " + string.Join(", ", missing));
    }
}
