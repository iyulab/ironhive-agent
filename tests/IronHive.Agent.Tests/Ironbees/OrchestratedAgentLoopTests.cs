using IronHive.Agent.Ironbees;
using Ironbees.Core;
using Ironbees.Core.Conversation;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Ironbees;

public class OrchestratedAgentLoopTests
{
    [Fact]
    public async Task RunAsync_WithNullOverrideOptions_DelegatesNormally()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.ProcessAsync("Hello", Arg.Any<CancellationToken>()).Returns("Hi there!");
        var loop = new OrchestratedAgentLoop(orchestrator);

        var response = await loop.RunAsync("Hello", overrideOptions: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hi there!", response.Content);
    }

    [Fact]
    public async Task RunAsync_WithNonNullOverrideOptions_ThrowsNotSupported()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        var loop = new OrchestratedAgentLoop(orchestrator);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => loop.RunAsync("Hello", new ChatOptions { Temperature = 0.5f }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunStreamingAsync_WithNonNullOverrideOptions_ThrowsNotSupported()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        var loop = new OrchestratedAgentLoop(orchestrator);

        async Task Act()
        {
            await foreach (var _ in loop.RunStreamingAsync("Hello", new ChatOptions { Temperature = 0.5f }))
            {
            }
        }

        await Assert.ThrowsAsync<NotSupportedException>(Act);
    }

    // Moved from the host (its AddIronbeesOrchestration helper was removed): the loop's history is the conversation
    // store's, not an in-memory field, and without a store it is always empty rather than throwing.
    [Fact]
    public async Task WithAConversationStore_TheHistoryIsPersistedAndCleared()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"orchestrated-history-{Guid.NewGuid():N}");
        try
        {
            using var store = new FileSystemConversationStore(directory);
            var loop = new OrchestratedAgentLoop(Substitute.For<IAgentOrchestrator>(), conversationStore: store);

            await loop.InitializeHistoryAsync(
                [new ChatMessage(ChatRole.User, "hello"), new ChatMessage(ChatRole.Assistant, "hi there")],
                TestContext.Current.CancellationToken);

            var history = await loop.GetHistoryAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["hello", "hi there"], history.Select(m => m.Text));
            Assert.Equal([ChatRole.User, ChatRole.Assistant], history.Select(m => m.Role));

            var id = Assert.Single(await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken));
            var stored = await store.LoadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(2, stored!.Messages.Count);

            await loop.ClearHistoryAsync(TestContext.Current.CancellationToken);
            Assert.Empty(await loop.GetHistoryAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WithoutAConversationStore_TheHistoryIsAlwaysEmpty()
    {
        var loop = new OrchestratedAgentLoop(Substitute.For<IAgentOrchestrator>());

        await loop.InitializeHistoryAsync([new ChatMessage(ChatRole.User, "hello")], TestContext.Current.CancellationToken);

        Assert.Empty(await loop.GetHistoryAsync(TestContext.Current.CancellationToken));
    }
}
