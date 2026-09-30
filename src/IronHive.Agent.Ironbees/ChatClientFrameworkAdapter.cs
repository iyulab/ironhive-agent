using System.Runtime.CompilerServices;
using Ironbees.Core;
using Ironbees.Core.Streaming;
using IronHive.Agent.Delegation;
using IronHive.Agent.Invocation;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// Adapter that connects Microsoft.Extensions.AI IChatClient to Ironbees ILLMFrameworkAdapter.
/// Runs its own tool loop — every call through a <see cref="ToolInvocationPipeline"/>, the same one a
/// function-invoking client uses — and resolves tools dynamically.
/// </summary>
public class ChatClientFrameworkAdapter : ILLMFrameworkAdapter
{
    private readonly Func<ModelConfig, IChatClient> _clientFactory;
    /// <summary>
    /// The <see cref="CompletionChunk.FinishReason"/> of a streamed run that a tool call ended
    /// (<see cref="FunctionInvocationContext.Terminate"/>).
    /// </summary>
    public const string ToolTerminatedFinishReason = "tool_terminated";

    private const string NotRunAfterTermination = "Tool call not run: an earlier tool call in this turn ended the request.";

    private readonly Func<IList<AITool>>? _toolsFactory;
    private readonly ToolInvocationPipeline _pipeline;
    private readonly int _maxToolTurns;

    /// <summary>
    /// Creates a new ChatClientFrameworkAdapter with full configuration.
    /// </summary>
    /// <param name="clientFactory">Factory function to create IChatClient from ModelConfig.</param>
    /// <param name="toolsFactory">Dynamic tool provider (called each invocation to support hot reload).</param>
    /// <param name="toolInvocationPipeline">
    /// What every tool call runs through — the permission gate, result guards and loop guards it carries apply here
    /// exactly as on a function-invoking client. A call that a middleware ends
    /// (<see cref="FunctionInvocationContext.Terminate"/>) ends the run after that tool turn. Defaults to
    /// <see cref="ToolInvocationPipeline.CreateDefault"/> (the loop guards only, no permission gate);
    /// <c>AddIronbees</c> passes the container's pipeline.
    /// </param>
    /// <param name="maxToolTurns">Maximum tool execution turns to prevent infinite loops.</param>
    public ChatClientFrameworkAdapter(
        Func<ModelConfig, IChatClient> clientFactory,
        Func<IList<AITool>>? toolsFactory = null,
        ToolInvocationPipeline? toolInvocationPipeline = null,
        int maxToolTurns = 20)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _toolsFactory = toolsFactory;
        _pipeline = toolInvocationPipeline ?? ToolInvocationPipeline.CreateDefault();
        _maxToolTurns = maxToolTurns;
    }

    /// <summary>
    /// Creates a new ChatClientFrameworkAdapter with a single shared client.
    /// </summary>
    /// <param name="chatClient">The shared IChatClient instance.</param>
    public ChatClientFrameworkAdapter(IChatClient chatClient)
        : this(SharedClient(chatClient))
    {
    }

    private static Func<ModelConfig, IChatClient> SharedClient(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        return _ => chatClient;
    }

    /// <inheritdoc />
    public Task<IAgent> CreateAgentAsync(AgentConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var chatClient = _clientFactory(config.Model);
        var agent = new ChatClientAgent(config, chatClient);

        return Task.FromResult<IAgent>(agent);
    }

    /// <inheritdoc />
    public Task<string> RunAsync(IAgent agent, string input, CancellationToken cancellationToken = default)
    {
        return RunAsync(agent, input, conversationHistory: null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Text projection of <see cref="RunStructuredAsync"/>.</remarks>
    public async Task<string> RunAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunStructuredAsync(agent, input, conversationHistory, options: null, cancellationToken);
        return result.Text;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Honours <see cref="AgentRunOptions.MaxTokens"/>, <see cref="AgentRunOptions.MaxToolTurns"/>,
    /// <see cref="AgentRunOptions.ThinkingEffort"/> (as <see cref="ChatOptions.Reasoning"/>; <c>Minimal</c>, which
    /// the standard options have no value for, is sent as <see cref="ReasoningEffort.Low"/>) and
    /// <see cref="AgentRunOptions.Tools"/> (replaces the agent's tools for this call). Refuses
    /// <see cref="AgentRunOptions.Suggestions"/> — this adapter has no suggestion pass. The result reports summed
    /// usage, the number of model round-trips, and whether the run stopped at its tool-turn limit.
    /// </remarks>
    public async Task<AgentRunResult> RunStructuredAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (chatAgent, messages, tools, chatOptions, maxTurns) = Prepare(agent, input, conversationHistory, options);
        var usage = new UsageAccumulator();

        if (tools.Count == 0)
        {
            var single = await chatAgent.ChatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            usage.Add(single.Usage);
            return new AgentRunResult { Text = single.Text ?? string.Empty, Usage = usage.Result, TurnsUsed = 1, TurnLimitReached = false };
        }

        var turnsUsed = 0;
        while (turnsUsed < maxTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turnsUsed++;

            var response = await chatAgent.ChatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            usage.Add(response.Usage);
            messages.AddRange(response.Messages);

            var pendingToolCalls = PendingToolCalls(response);
            if (pendingToolCalls.Count == 0)
            {
                return new AgentRunResult
                {
                    Text = ExtractLastAssistantText(response), Usage = usage.Result, TurnsUsed = turnsUsed, TurnLimitReached = false
                };
            }

            var (toolMessage, terminated) = await ExecuteToolCallsAsync(pendingToolCalls, tools, messages, turnsUsed, cancellationToken);
            messages.Add(toolMessage);
            if (terminated)
            {
                return new AgentRunResult
                {
                    Text = ExtractLastTextFromMessages(messages), Usage = usage.Result, TurnsUsed = turnsUsed, TurnLimitReached = false
                };
            }
        }

        // The model still wanted tools when the limit ran out: say so rather than pass the partial text off as an answer.
        return new AgentRunResult
        {
            Text = ExtractLastTextFromMessages(messages), Usage = usage.Result, TurnsUsed = turnsUsed, TurnLimitReached = true
        };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> StreamAsync(
        IAgent agent,
        string input,
        CancellationToken cancellationToken = default)
    {
        return StreamAsync(agent, input, conversationHistory: null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Text projection of <see cref="StreamStructuredAsync"/>.</remarks>
    public async IAsyncEnumerable<string> StreamAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in StreamStructuredAsync(agent, input, conversationHistory, options: null, cancellationToken))
        {
            if (chunk is TextChunk { Content: { Length: > 0 } text })
            {
                yield return text;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Same options contract as <see cref="RunStructuredAsync"/>; a refused option throws at call time. Intermediate
    /// tool turns run buffered and only the final answer is streamed (a tool-free agent streams its single call).
    /// Ends with a <see cref="UsageChunk"/> when usage was observed and a <see cref="CompletionChunk"/> whose
    /// <c>FinishReason</c> is <c>tool_turn_limit</c> (and <c>Success</c> false) when the tool-turn limit ran out.
    /// </remarks>
    public IAsyncEnumerable<StreamChunk> StreamStructuredAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(agent, input, conversationHistory, options);
        return StreamPreparedAsync(prepared, cancellationToken);
    }

    private async IAsyncEnumerable<StreamChunk> StreamPreparedAsync(
        PreparedRun prepared,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (chatAgent, messages, tools, chatOptions, maxTurns) = prepared;
        var usage = new UsageAccumulator();

        if (tools.Count == 0)
        {
            await foreach (var update in chatAgent.ChatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return new TextChunk(update.Text);
                }

                usage.Add(update.Contents.OfType<UsageContent>().LastOrDefault()?.Details);
            }

            foreach (var tail in Tail(usage, turnLimitReached: false))
            {
                yield return tail;
            }
            yield break;
        }

        var turnsUsed = 0;
        while (turnsUsed < maxTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turnsUsed++;

            var response = await chatAgent.ChatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            usage.Add(response.Usage);
            messages.AddRange(response.Messages);

            var pendingToolCalls = PendingToolCalls(response);
            if (pendingToolCalls.Count == 0)
            {
                var text = ExtractLastAssistantText(response);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new TextChunk(text);
                }

                foreach (var tail in Tail(usage, turnLimitReached: false))
                {
                    yield return tail;
                }
                yield break;
            }

            var (toolMessage, terminated) = await ExecuteToolCallsAsync(pendingToolCalls, tools, messages, turnsUsed, cancellationToken);
            messages.Add(toolMessage);
            if (terminated)
            {
                var stoppedAt = ExtractLastTextFromMessages(messages);
                if (!string.IsNullOrEmpty(stoppedAt))
                {
                    yield return new TextChunk(stoppedAt);
                }

                foreach (var tail in Tail(usage, ToolTerminatedFinishReason))
                {
                    yield return tail;
                }
                yield break;
            }
        }

        var partial = ExtractLastTextFromMessages(messages);
        if (!string.IsNullOrEmpty(partial))
        {
            yield return new TextChunk(partial);
        }

        foreach (var tail in Tail(usage, "tool_turn_limit"))
        {
            yield return tail;
        }
    }

    private static IEnumerable<StreamChunk> Tail(UsageAccumulator usage, bool turnLimitReached)
        => Tail(usage, turnLimitReached ? "tool_turn_limit" : null);

    /// <summary>The closing chunks: usage when observed, then completion — failed with <paramref name="failedFinishReason"/> when given.</summary>
    private static IEnumerable<StreamChunk> Tail(UsageAccumulator usage, string? failedFinishReason)
    {
        if (usage.Result is { } observed)
        {
            yield return new UsageChunk((int)(observed.InputTokenCount ?? 0), (int)(observed.OutputTokenCount ?? 0));
        }

        yield return failedFinishReason is null
            ? new CompletionChunk()
            : new CompletionChunk(Success: false, FinishReason: failedFinishReason);
    }

    private sealed record PreparedRun(
        ChatClientAgent Agent, List<ChatMessage> Messages, IList<AITool> Tools, ChatOptions Options, int MaxTurns);

    /// <summary>Validates the request and resolves everything a run needs — eagerly, so a refused option throws at call time.</summary>
    private PreparedRun Prepare(
        IAgent agent, string input, IReadOnlyList<ChatMessage>? conversationHistory, AgentRunOptions? options)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (options?.Suggestions is not null)
        {
            throw new NotSupportedException(
                $"{GetType().Name} does not support structured suggestions (AgentRunOptions.Suggestions).");
        }

        if (options?.MaxToolTurns is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxToolTurns, "AgentRunOptions.MaxToolTurns must be at least 1.");
        }

        if (agent is not ChatClientAgent chatAgent)
        {
            throw new InvalidOperationException(
                $"Agent must be created by this adapter. Expected ChatClientAgent, got {agent.GetType().Name}");
        }

        var tools = options?.Tools is { } requested ? requested.ToList() : ResolveTools(chatAgent);
        return new PreparedRun(
            chatAgent,
            BuildMessages(chatAgent, input, conversationHistory),
            tools,
            CreateChatOptions(chatAgent.Config.Model, tools, options),
            options?.MaxToolTurns ?? _maxToolTurns);
    }

    private static List<FunctionCallContent> PendingToolCalls(ChatResponse response)
        => response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();

    /// <summary>Sums usage over a run's model calls; stays null when no call reported any.</summary>
    private sealed class UsageAccumulator
    {
        public UsageDetails? Result { get; private set; }

        public void Add(UsageDetails? usage)
        {
            if (usage is null)
            {
                return;
            }

            Result ??= new UsageDetails();
            Result.Add(usage);
        }
    }

    private static List<ChatMessage> BuildMessages(
        ChatClientAgent chatAgent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, chatAgent.Config.SystemPrompt)
        };

        if (conversationHistory is { Count: > 0 })
        {
            messages.AddRange(conversationHistory);
        }

        messages.Add(new ChatMessage(ChatRole.User, input));
        return messages;
    }

    /// <summary>
    /// The tools an agent may call. An agent that names its tools (<see cref="AgentConfig.Tools"/>) gets exactly
    /// those — resolved against the tool pool, and a name the pool does not have is an error rather than a tool the
    /// agent silently lacks. Otherwise the older <see cref="AgentConfig.Capabilities"/> name filter applies, and with
    /// neither the agent gets the whole pool.
    /// </summary>
    private IList<AITool> ResolveTools(ChatClientAgent chatAgent)
    {
        var allTools = _toolsFactory?.Invoke() ?? [];

        if (chatAgent.Config.Tools is { Count: > 0 } named)
        {
            var byName = allTools.OfType<AIFunction>().ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var missing = named.Where(n => !byName.ContainsKey(n)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Agent '{chatAgent.Name}' names tool(s) the tool pool does not provide: {string.Join(", ", missing)}. " +
                    "Register them in the tools factory given to ChatClientFrameworkAdapter, or remove them from the agent's 'tools' list.");
            }

            return named.Select(n => (AITool)byName[n]).ToList();
        }

        if (chatAgent.Config.Capabilities is { Count: > 0 })
        {
            return allTools
                .Where(t => t is AIFunction func &&
                    chatAgent.Config.Capabilities.Contains(func.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return allTools;
    }

    /// <summary>
    /// Runs one turn's tool calls through the pipeline, in order, and answers every call: a call after one that ended
    /// the request is answered as not run, so the conversation never holds a call without a result.
    /// </summary>
    private async Task<(ChatMessage Message, bool Terminated)> ExecuteToolCallsAsync(
        List<FunctionCallContent> toolCalls,
        IList<AITool> tools,
        List<ChatMessage> conversation,
        int iteration,
        CancellationToken cancellationToken)
    {
        var toolResults = new List<AIContent>();
        var terminated = false;

        for (var index = 0; index < toolCalls.Count; index++)
        {
            var functionCall = toolCalls[index];
            if (terminated)
            {
                toolResults.Add(new FunctionResultContent(functionCall.CallId, NotRunAfterTermination));
                continue;
            }

            if (tools.FirstOrDefault(t => t is AIFunction func && func.Name == functionCall.Name) is not AIFunction function)
            {
                toolResults.Add(new FunctionResultContent(
                    functionCall.CallId,
                    $"Error: Tool '{functionCall.Name}' not found"));
                continue;
            }

            var context = new FunctionInvocationContext
            {
                Function = function,
                // A copy: a middleware that edits the arguments (an approver's changes) must not rewrite the call the
                // conversation records.
                Arguments = new AIFunctionArguments(functionCall.Arguments is null
                    ? null
                    : new Dictionary<string, object?>(functionCall.Arguments)),
                CallContent = functionCall,
                Messages = conversation,
                Iteration = iteration,
                FunctionCallIndex = index,
                FunctionCount = toolCalls.Count,
            };

            try
            {
                // This loop is not FunctionInvokingChatClient, so a tool that reads the conversation (the advisor)
                // gets it from here instead of FunctionInvokingChatClient.CurrentContext.
                object? result;
                using (ToolInvocationScope.Enter(conversation))
                {
                    result = await _pipeline.InvokeAsync(context, cancellationToken);
                }

                toolResults.Add(new FunctionResultContent(functionCall.CallId, result?.ToString() ?? "null"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                toolResults.Add(new FunctionResultContent(functionCall.CallId, $"Error: {ex.Message}") { Exception = ex });
            }

            terminated = context.Terminate;
        }

        return (new ChatMessage(ChatRole.Tool, toolResults), terminated);
    }

    private static string ExtractLastAssistantText(ChatResponse response)
    {
        var lastAssistant = response.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .LastOrDefault();

        return lastAssistant?.Text ?? response.Text ?? string.Empty;
    }

    private static string ExtractLastTextFromMessages(List<ChatMessage> messages)
    {
        var lastAssistant = messages
            .Where(m => m.Role == ChatRole.Assistant)
            .LastOrDefault();

        return lastAssistant?.Text ?? string.Empty;
    }

    private static ChatOptions CreateChatOptions(ModelConfig model, IList<AITool>? tools, AgentRunOptions? runOptions)
    {
        var options = new ChatOptions
        {
            ModelId = model.Deployment,
            Temperature = (float)model.Temperature,
            MaxOutputTokens = runOptions?.MaxTokens ?? model.MaxTokens
        };

        if (runOptions?.ThinkingEffort is { } effort)
        {
            options.Reasoning = new ReasoningOptions { Effort = MapThinkingEffort(effort) };
        }

        if (tools is { Count: > 0 })
        {
            options.Tools = tools;
        }

        return options;
    }

    private static ReasoningEffort MapThinkingEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.None => ReasoningEffort.None,
        ThinkingEffort.Minimal or ThinkingEffort.Low => ReasoningEffort.Low,
        ThinkingEffort.Medium => ReasoningEffort.Medium,
        ThinkingEffort.High => ReasoningEffort.High,
        ThinkingEffort.XHigh => ReasoningEffort.ExtraHigh,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown thinking effort.")
    };
}

/// <summary>
/// Internal IAgent implementation backed by IChatClient.
/// </summary>
internal sealed class ChatClientAgent : IAgent
{
    public string Name { get; }
    public string Description { get; }
    public AgentConfig Config { get; }
    public IChatClient ChatClient { get; }

    public ChatClientAgent(AgentConfig config, IChatClient chatClient)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Name = config.Name;
        Description = config.Description;
    }
}
