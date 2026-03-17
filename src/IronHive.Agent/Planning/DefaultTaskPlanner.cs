using System.Text.Json;
using IronHive.Abstractions.Agent.Planning;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Planning;

/// <summary>
/// Default LLM-based implementation of <see cref="ITaskPlanner"/>.
/// Decomposes goals into structured step-by-step plans via chat completion.
/// </summary>
public class DefaultTaskPlanner : ITaskPlanner
{
    private readonly IChatClient _chatClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public DefaultTaskPlanner(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    /// <inheritdoc />
    public async Task<TaskPlan> CreatePlanAsync(
        string goal, PlanningContext context, CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildPlannerSystemPrompt(context);
        var userPrompt = $"Create a step-by-step plan to accomplish: {goal}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var planDto = ParsePlanResponse(response.Text ?? "{}");

        return new TaskPlan
        {
            Goal = planDto.Goal ?? goal,
            Reasoning = planDto.Reasoning,
            Steps = planDto.Steps?.Select((s, i) => new PlanStep
            {
                Index = s.Index > 0 ? s.Index : i + 1,
                Description = s.Description ?? $"Step {i + 1}",
                Instruction = s.Instruction ?? s.Description ?? string.Empty,
                RequiredTools = s.RequiredTools ?? [],
                DependsOn = s.DependsOn ?? [],
            }).ToList() ?? [],
        };
    }

    /// <inheritdoc />
    public async Task<TaskPlan> ReplanAsync(
        TaskPlan currentPlan, string failureReason, CancellationToken cancellationToken = default)
    {
        var context = new PlanningContext
        {
            AvailableTools = currentPlan.Steps
                .SelectMany(s => s.RequiredTools)
                .Distinct()
                .ToList(),
        };

        var systemPrompt = BuildPlannerSystemPrompt(context);
        var currentPlanJson = JsonSerializer.Serialize(currentPlan, JsonOptions);
        var userPrompt = $"""
            The previous plan failed. Reason: {failureReason}

            Previous plan:
            {currentPlanJson}

            Create a revised plan that addresses the failure.
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var planDto = ParsePlanResponse(response.Text ?? "{}");

        return new TaskPlan
        {
            Goal = planDto.Goal ?? currentPlan.Goal,
            Reasoning = planDto.Reasoning,
            Steps = planDto.Steps?.Select((s, i) => new PlanStep
            {
                Index = s.Index > 0 ? s.Index : i + 1,
                Description = s.Description ?? $"Step {i + 1}",
                Instruction = s.Instruction ?? s.Description ?? string.Empty,
                RequiredTools = s.RequiredTools ?? [],
                DependsOn = s.DependsOn ?? [],
            }).ToList() ?? [],
        };
    }

    private static string BuildPlannerSystemPrompt(PlanningContext context)
    {
        var toolList = context.AvailableTools?.Count > 0
            ? string.Join(", ", context.AvailableTools)
            : "none specified";

        return
            "You are a task planning assistant. Your job is to decompose complex goals into " +
            "clear, actionable steps.\n\n" +
            "Available tools: " + toolList + "\n\n" +
            "Respond ONLY with valid JSON (no markdown fences). Example:\n" +
            """{"goal":"restatement","reasoning":"brief analysis","steps":[{"index":1,"description":"short","instruction":"detailed","requiredTools":["tool"],"dependsOn":[]}]}""" +
            "\n\nRules:\n" +
            "- Maximum 10 steps\n" +
            "- Each step should be independently executable\n" +
            "- List tool dependencies in requiredTools\n" +
            "- Use dependsOn to indicate step ordering constraints (list of step indices)\n" +
            "- Keep instructions specific and actionable";
    }

    private static PlanDto ParsePlanResponse(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0)
            {
                json = json[(firstNewline + 1)..];
            }

            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                json = json[..lastFence];
            }

            json = json.Trim();
        }

        try
        {
            return JsonSerializer.Deserialize<PlanDto>(json, JsonOptions) ?? new PlanDto();
        }
        catch (JsonException)
        {
            return new PlanDto();
        }
    }

    private sealed record PlanDto
    {
        public string? Goal { get; init; }
        public string? Reasoning { get; init; }
        public List<StepDto>? Steps { get; init; }
    }

    private sealed record StepDto
    {
        public int Index { get; init; }
        public string? Description { get; init; }
        public string? Instruction { get; init; }
        public string[]? RequiredTools { get; init; }
        public int[]? DependsOn { get; init; }
    }
}
