using System.Diagnostics;
using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.SubAgent;

/// <summary>
/// Default implementation of ISubAgentService.
/// </summary>
public sealed class SubAgentService : ISubAgentService, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly SubAgentConfig _config;
    private readonly string _workingDirectory;
    private readonly string? _parentId;

    private int _currentDepth;
    private int _runningCount;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    public SubAgentService(
        IChatClient chatClient,
        SubAgentConfig? config = null,
        string? workingDirectory = null,
        string? parentId = null,
        int currentDepth = 0)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _config = config ?? new SubAgentConfig();
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _parentId = parentId;
        _currentDepth = currentDepth;
        _semaphore = new SemaphoreSlim(_config.MaxConcurrent);
    }

    /// <inheritdoc />
    public int CurrentDepth => _currentDepth;

    /// <inheritdoc />
    public int RunningCount => _runningCount;

    /// <inheritdoc />
    public bool CanSpawn(SubAgentType type)
    {
        // Check depth limit
        if (_currentDepth >= _config.MaxDepth)
        {
            return false;
        }

        // Check concurrency limit
        if (_runningCount >= _config.MaxConcurrent)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public Task<SubAgentResult> ExploreAsync(
        string task,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var agentContext = SubAgentContext.Create(
            SubAgentType.Explore,
            task,
            context,
            _currentDepth + 1,
            _parentId,
            _workingDirectory);

        // Override with config values
        agentContext = agentContext with
        {
            MaxTurns = _config.Explore.MaxTurns,
            MaxTokens = _config.Explore.MaxTokens
        };

        return SpawnAsync(agentContext, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SubAgentResult> GeneralAsync(
        string task,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var agentContext = SubAgentContext.Create(
            SubAgentType.General,
            task,
            context,
            _currentDepth + 1,
            _parentId,
            _workingDirectory);

        // Override with config values
        agentContext = agentContext with
        {
            MaxTurns = _config.General.MaxTurns,
            MaxTokens = _config.General.MaxTokens
        };

        return SpawnAsync(agentContext, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SubAgentResult> SpawnAsync(
        SubAgentContext agentContext,
        CancellationToken cancellationToken = default)
    {
        if (!CanSpawn(agentContext.Type))
        {
            return SubAgentResult.Failed(
                agentContext,
                $"Cannot spawn sub-agent: depth limit ({_config.MaxDepth}) or concurrency limit ({_config.MaxConcurrent}) exceeded");
        }

        await _semaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _runningCount);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var tools = GetToolsForType(agentContext.Type);
            var systemPrompt = BuildSystemPrompt(agentContext);
            var userPrompt = BuildUserPrompt(agentContext);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var options = new ChatOptions
            {
                Tools = tools,
                MaxOutputTokens = 4096
            };

            var turnsUsed = 0;
            long tokensUsed = 0;
            string? lastAssistantMessage = null;

            while (turnsUsed < agentContext.MaxTurns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                turnsUsed++;

                var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);

                // Track tokens (approximate)
                tokensUsed += response.Usage?.TotalTokenCount ?? 0;

                // Get the last assistant message from the response
                var assistantMessages = response.Messages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .ToList();

                if (assistantMessages.Count == 0)
                {
                    break;
                }

                var lastMessage = assistantMessages[^1];

                // Check for tool calls
                var hasPendingToolCalls = lastMessage.Contents.Any(c => c is FunctionCallContent);

                if (hasPendingToolCalls)
                {
                    // Add all response messages to history
                    messages.AddRange(response.Messages);

                    // Process tool calls
                    var toolResults = await ProcessToolCallsAsync(lastMessage, tools, cancellationToken);
                    messages.Add(toolResults);
                }
                else
                {
                    // Final response
                    lastAssistantMessage = lastMessage.Text;
                    break;
                }
            }

            stopwatch.Stop();

            if (string.IsNullOrEmpty(lastAssistantMessage))
            {
                return SubAgentResult.Failed(
                    agentContext,
                    $"Sub-agent reached turn limit ({agentContext.MaxTurns}) without completing",
                    turnsUsed,
                    (int)tokensUsed,
                    stopwatch.Elapsed);
            }

            return SubAgentResult.Succeeded(
                agentContext,
                lastAssistantMessage,
                turnsUsed,
                (int)tokensUsed,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return SubAgentResult.Failed(
                agentContext,
                "Sub-agent execution was cancelled",
                duration: stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return SubAgentResult.Failed(
                agentContext,
                $"Sub-agent error: {ex.Message}",
                duration: stopwatch.Elapsed);
        }
        finally
        {
            Interlocked.Decrement(ref _runningCount);
            _semaphore.Release();
        }
    }

    private IList<AITool> GetToolsForType(SubAgentType type)
    {
        var allTools = BuiltInTools.GetAll(_workingDirectory);

        if (type == SubAgentType.Explore)
        {
            // Filter to read-only tools
            var allowedToolNames = _config.Explore.AllowedTools;
            return allTools
                .Where(t => t is AIFunction func && allowedToolNames.Contains(func.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        // General agent gets all tools
        return allTools;
    }

    private static string BuildSystemPrompt(SubAgentContext context)
    {
        var typeDescription = context.Type switch
        {
            SubAgentType.Explore => """
                You are an Explore sub-agent specialized in reading and understanding code.
                Your task is to gather information and report findings.
                You have access to read-only tools: read_file, list_directory, glob, grep.
                Do NOT attempt to modify any files or execute commands.
                Be efficient and focused - complete your task and report your findings.
                """,
            SubAgentType.General => """
                You are a General sub-agent for handling complex multi-step tasks.
                You have access to all tools and can read, write, and execute commands.
                Be thorough but efficient - complete your task and report results.
                """,
            _ => "You are a helpful sub-agent."
        };

        return $"""
            {typeDescription}

            IMPORTANT:
            - You are a sub-agent spawned by a parent agent
            - Complete your assigned task and provide a clear, concise summary
            - If you encounter errors, report them clearly
            - Do not spawn additional sub-agents (nesting depth: {context.Depth}/{context.Depth})
            """;
    }

    private static string BuildUserPrompt(SubAgentContext context)
    {
        var prompt = $"Task: {context.Task}";

        if (!string.IsNullOrWhiteSpace(context.AdditionalContext))
        {
            prompt += $"\n\nContext:\n{context.AdditionalContext}";
        }

        return prompt;
    }

    private static async Task<ChatMessage> ProcessToolCallsAsync(
        ChatMessage assistantMessage,
        IList<AITool> tools,
        CancellationToken cancellationToken)
    {
        var toolResults = new List<AIContent>();

        foreach (var content in assistantMessage.Contents)
        {
            if (content is FunctionCallContent functionCall)
            {
                var tool = tools.FirstOrDefault(t => t is AIFunction func && func.Name == functionCall.Name);

                if (tool is AIFunction function)
                {
                    try
                    {
                        var args = functionCall.Arguments is not null
                            ? new AIFunctionArguments(functionCall.Arguments)
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
                    toolResults.Add(new FunctionResultContent(functionCall.CallId, $"Error: Tool '{functionCall.Name}' not found"));
                }
            }
        }

        return new ChatMessage(ChatRole.Tool, toolResults);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            _disposed = true;
        }
    }
}
