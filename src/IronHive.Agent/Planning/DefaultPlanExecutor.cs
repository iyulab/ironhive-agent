using System.Runtime.CompilerServices;
using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Invocation;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Planning;

/// <summary>
/// Default <see cref="IPlanExecutor"/> implementation that executes individual
/// plan steps using a chat client with optional tool access.
/// When tools are provided, the chat client handles the tool-call -> result -> re-send loop,
/// running every call through a <see cref="ToolInvocationPipeline"/>.
/// </summary>
public class DefaultPlanExecutor : IPlanExecutor, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly IList<AITool>? _tools;
    private readonly bool _ownsClient;

    /// <summary>Creates an executor over <paramref name="chatClient"/>.</summary>
    /// <param name="chatClient">The model that executes each step.</param>
    /// <param name="tools">Tools a step may call; none by default.</param>
    /// <param name="toolInvocationPipeline">
    /// What each tool call runs through (a permission gate, result guards). Defaults to
    /// <see cref="ToolInvocationPipeline.CreateDefault"/> — the loop guards only.
    /// </param>
    public DefaultPlanExecutor(IChatClient chatClient, IList<AITool>? tools = null, ToolInvocationPipeline? toolInvocationPipeline = null)
    {
        // Wrap with function invocation if tools are provided
        if (tools is { Count: > 0 })
        {
            _chatClient = new ChatClientBuilder(chatClient)
                .UseToolInvocationPipeline(toolInvocationPipeline ?? ToolInvocationPipeline.CreateDefault())
                .Build();
            _tools = tools;
            _ownsClient = true;
        }
        else
        {
            _chatClient = chatClient;
            _tools = null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PlanExecutionEvent> ExecuteStepAsync(
        TaskPlan plan,
        PlanStep planStep,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StepCompletedEvent completedEvent;
        try
        {
            var messages = BuildStepMessages(plan, planStep);
            var options = _tools is { Count: > 0 }
                ? new ChatOptions { Tools = _tools }
                : null;

            var response = await _chatClient.GetResponseAsync(
                messages, options, cancellationToken);

            var output = response.Text ?? string.Empty;
            completedEvent = new StepCompletedEvent(planStep.Index,
                new StepResult { Success = true, Output = output });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            completedEvent = new StepCompletedEvent(planStep.Index,
                new StepResult
                {
                    Success = false,
                    Output = string.Empty,
                    Error = ex.Message,
                });
        }

        yield return completedEvent;
    }

    /// <summary>
    /// Builds the chat messages for a plan step, including context from completed steps.
    /// Override to customize the prompt template or include additional context.
    /// </summary>
    protected virtual List<ChatMessage> BuildStepMessages(TaskPlan plan, PlanStep step)
    {
        var completedSteps = string.Join("\n",
            plan.Steps
                .Where(s => s.Status == StepStatus.Completed && s.Result is not null)
                .Select(s => $"- Step {s.Index} ({s.Description}): {s.Result}"));

        var contextSection = string.IsNullOrEmpty(completedSteps)
            ? ""
            : $"\n\nCompleted steps so far:\n{completedSteps}";

        return
        [
            new(ChatRole.System,
                $"""
                You are executing step {step.Index} of a plan for: {plan.Goal}
                {contextSection}

                Execute the following instruction. Use available tools as needed.
                Be thorough but concise in your response.
                """),
            new(ChatRole.User, step.Instruction),
        ];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient && _chatClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
