using System.Runtime.CompilerServices;
using Ironbees.Core;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// Adapter that connects Microsoft.Extensions.AI IChatClient to Ironbees ILLMFrameworkAdapter.
/// Supports tool execution loop, permission checks, and dynamic tool provisioning.
/// </summary>
public class ChatClientFrameworkAdapter : ILLMFrameworkAdapter
{
    private readonly Func<ModelConfig, IChatClient> _clientFactory;
    private readonly Func<IList<AITool>>? _toolsFactory;
    private readonly ApprovalGate? _gate;
    private readonly int _maxToolTurns;

    /// <summary>
    /// Creates a new ChatClientFrameworkAdapter with full configuration.
    /// </summary>
    /// <param name="clientFactory">Factory function to create IChatClient from ModelConfig.</param>
    /// <param name="toolsFactory">Dynamic tool provider (called each invocation to support hot reload).</param>
    /// <param name="permissionEvaluator">
    /// Permission evaluator for tool execution. Used to build a <see cref="ModeToolFilter"/> when
    /// <paramref name="modeToolFilter"/> is not given; with neither, tool calls are not gated at all.
    /// </param>
    /// <param name="maxToolTurns">Maximum tool execution turns to prevent infinite loops.</param>
    /// <param name="modeToolFilter">Produces the verdict for each tool call; takes precedence over the evaluator.</param>
    /// <param name="approvalService">
    /// Asked when a verdict is <c>Ask</c>. Without one, an <c>Ask</c> verdict is refused with a reason —
    /// the same rule <see cref="ApprovalGatedFunctionInvoker"/> applies.
    /// </param>
    public ChatClientFrameworkAdapter(
        Func<ModelConfig, IChatClient> clientFactory,
        Func<IList<AITool>>? toolsFactory = null,
        IPermissionEvaluator? permissionEvaluator = null,
        int maxToolTurns = 20,
        IModeToolFilter? modeToolFilter = null,
        IHumanApprovalService? approvalService = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _toolsFactory = toolsFactory;
        _maxToolTurns = maxToolTurns;

        var filter = modeToolFilter ?? (permissionEvaluator is null ? null : new ModeToolFilter(permissionEvaluator));
        _gate = filter is null ? null : new ApprovalGate(filter, approvalService);
    }

    /// <summary>
    /// Creates a new ChatClientFrameworkAdapter with a single shared client.
    /// </summary>
    /// <param name="chatClient">The shared IChatClient instance.</param>
    public ChatClientFrameworkAdapter(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        _clientFactory = _ => chatClient;
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
    public async Task<string> RunAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (agent is not ChatClientAgent chatAgent)
        {
            throw new InvalidOperationException(
                $"Agent must be created by this adapter. Expected ChatClientAgent, got {agent.GetType().Name}");
        }

        var messages = BuildMessages(chatAgent, input, conversationHistory);
        var tools = ResolveTools(chatAgent);
        var options = CreateChatOptions(chatAgent.Config.Model, tools);

        if (tools.Count == 0)
        {
            // No tools: single-turn execution
            var response = await chatAgent.ChatClient.GetResponseAsync(messages, options, cancellationToken);
            return response.Text ?? string.Empty;
        }

        // Tool execution loop
        var turnsUsed = 0;
        while (turnsUsed < _maxToolTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turnsUsed++;

            var response = await chatAgent.ChatClient.GetResponseAsync(messages, options, cancellationToken);
            messages.AddRange(response.Messages);

            var pendingToolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (pendingToolCalls.Count == 0)
            {
                return ExtractLastAssistantText(response);
            }

            // Execute tool calls and add results
            var toolResultMessage = await ExecuteToolCallsAsync(pendingToolCalls, tools, cancellationToken);
            messages.Add(toolResultMessage);
        }

        // Max turns reached - return whatever we have
        return ExtractLastTextFromMessages(messages);
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
    public async IAsyncEnumerable<string> StreamAsync(
        IAgent agent,
        string input,
        IReadOnlyList<ChatMessage>? conversationHistory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (agent is not ChatClientAgent chatAgent)
        {
            throw new InvalidOperationException(
                $"Agent must be created by this adapter. Expected ChatClientAgent, got {agent.GetType().Name}");
        }

        var messages = BuildMessages(chatAgent, input, conversationHistory);
        var tools = ResolveTools(chatAgent);
        var options = CreateChatOptions(chatAgent.Config.Model, tools);

        if (tools.Count == 0)
        {
            // No tools: single-turn streaming
            await foreach (var update in chatAgent.ChatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return update.Text;
                }
            }
            yield break;
        }

        // Tool execution loop with streaming for final response
        var turnsUsed = 0;
        while (turnsUsed < _maxToolTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turnsUsed++;

            // Use non-streaming for intermediate turns (tool calls)
            var response = await chatAgent.ChatClient.GetResponseAsync(messages, options, cancellationToken);
            messages.AddRange(response.Messages);

            var pendingToolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (pendingToolCalls.Count == 0)
            {
                // Final response - yield the text
                var text = ExtractLastAssistantText(response);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }
                yield break;
            }

            // Execute tool calls and continue loop
            var toolResultMessage = await ExecuteToolCallsAsync(pendingToolCalls, tools, cancellationToken);
            messages.Add(toolResultMessage);
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

    private IList<AITool> ResolveTools(ChatClientAgent chatAgent)
    {
        if (_toolsFactory is null)
        {
            return [];
        }

        var allTools = _toolsFactory();

        // Filter by agent capabilities if specified
        if (chatAgent.Config.Capabilities is { Count: > 0 })
        {
            return allTools
                .Where(t => t is AIFunction func &&
                    chatAgent.Config.Capabilities.Contains(func.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return allTools;
    }

    private async Task<ChatMessage> ExecuteToolCallsAsync(
        IReadOnlyList<FunctionCallContent> toolCalls,
        IList<AITool> tools,
        CancellationToken cancellationToken)
    {
        var toolResults = new List<AIContent>();

        foreach (var functionCall in toolCalls)
        {
            IDictionary<string, object?>? arguments = functionCall.Arguments;

            // Permission check: the same gate ApprovalGatedFunctionInvoker uses, so Allow / Deny / Ask
            // mean the same thing on this path as on a function-invoking client.
            if (_gate is not null)
            {
                var decision = await _gate.DecideAsync(functionCall.Name, arguments, cancellationToken);
                if (!decision.ShouldProceed)
                {
                    toolResults.Add(new FunctionResultContent(functionCall.CallId, decision.Refusal));
                    continue;
                }

                if (decision.ModifiedArguments is not null)
                {
                    arguments = new Dictionary<string, object?>(arguments ?? new Dictionary<string, object?>());
                    foreach (var (key, value) in decision.ModifiedArguments)
                    {
                        arguments[key] = value;
                    }
                }
            }

            var tool = tools.FirstOrDefault(t => t is AIFunction func && func.Name == functionCall.Name);

            if (tool is AIFunction function)
            {
                try
                {
                    var args = arguments is not null
                        ? new AIFunctionArguments(arguments)
                        : null;
                    var result = await function.InvokeAsync(args, cancellationToken);
                    var resultText = result?.ToString() ?? "null";

                    toolResults.Add(new FunctionResultContent(functionCall.CallId, resultText));
                }
                catch (Exception ex)
                {
                    toolResults.Add(new FunctionResultContent(functionCall.CallId, $"Error: {ex.Message}"));
                }
            }
            else
            {
                toolResults.Add(new FunctionResultContent(
                    functionCall.CallId,
                    $"Error: Tool '{functionCall.Name}' not found"));
            }
        }

        return new ChatMessage(ChatRole.Tool, toolResults);
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

    private static ChatOptions CreateChatOptions(ModelConfig model, IList<AITool>? tools = null)
    {
        var options = new ChatOptions
        {
            ModelId = model.Deployment,
            Temperature = (float)model.Temperature,
            MaxOutputTokens = model.MaxTokens
        };

        if (tools is { Count: > 0 })
        {
            options.Tools = tools;
        }

        return options;
    }
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
