using System.Runtime.CompilerServices;
using System.Text;
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
    /// <remarks>
    /// The step is streamed: every text delta is yielded as a <see cref="StepProgressEvent"/>, and every tool call
    /// the step makes is yielded as a <see cref="StepToolCallEvent"/> once its result is in. The last event is always
    /// a <see cref="StepCompletedEvent"/> whose output is the whole text. A model or transport failure ends the step
    /// as a failed <see cref="StepCompletedEvent"/> that keeps the text streamed so far; cancellation throws.
    /// </remarks>
    public async IAsyncEnumerable<PlanExecutionEvent> ExecuteStepAsync(
        TaskPlan plan,
        PlanStep planStep,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? error = null;

        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        try
        {
            var messages = BuildStepMessages(plan, planStep);
            var options = _tools is { Count: > 0 }
                ? new ChatOptions { Tools = _tools }
                : null;
            updates = _chatClient.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        if (updates is not null)
        {
            await using (updates.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await updates.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        error = ex.Message;
                        break;
                    }

                    var update = updates.Current;
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case FunctionCallContent call:
                                toolNames[call.CallId] = call.Name;
                                break;
                            case FunctionResultContent result:
                                yield return new StepToolCallEvent(
                                    planStep.Index,
                                    toolNames.TryGetValue(result.CallId, out var name) ? name : result.CallId,
                                    result.Exception is null);
                                break;
                        }
                    }

                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        output.Append(text);
                        yield return new StepProgressEvent(planStep.Index, text);
                    }
                }
            }
        }

        yield return new StepCompletedEvent(planStep.Index, error is null
            ? new StepResult { Success = true, Output = output.ToString() }
            : new StepResult { Success = false, Output = output.ToString(), Error = error });
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
