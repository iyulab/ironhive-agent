using System.Globalization;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Context;

namespace IronHive.Agent.Planning;

/// <summary>
/// Synchronizes plan execution state to a <see cref="Scratchpad"/> instance,
/// keeping the agent's working memory up-to-date with plan progress.
/// </summary>
public sealed class ScratchpadPlanTracker
{
    private const int MaxOutputLength = 200;

    private readonly Scratchpad _scratchpad;

    public ScratchpadPlanTracker(Scratchpad scratchpad)
    {
        _scratchpad = scratchpad ?? throw new ArgumentNullException(nameof(scratchpad));
    }

    /// <summary>
    /// Records a newly created plan into the scratchpad.
    /// </summary>
    public void OnPlanCreated(TaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _scratchpad.CurrentPlan = FormatPlanSummary(plan);
        _scratchpad.CurrentStep = 0;
    }

    /// <summary>
    /// Updates the scratchpad when a step begins execution.
    /// </summary>
    public void OnStepStarted(int stepIndex, string description)
    {
        _scratchpad.CurrentStep = stepIndex;
    }

    /// <summary>
    /// Records a step completion as an observation in the scratchpad.
    /// </summary>
    public void OnStepCompleted(int stepIndex, StepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Success ? "OK" : "FAILED";
        var output = Truncate(result.Output, MaxOutputLength);
        _scratchpad.AddObservation(string.Create(
            CultureInfo.InvariantCulture,
            $"Step {stepIndex} [{status}]: {output}"));
    }

    /// <summary>
    /// Records a replan event and updates the scratchpad with the new plan.
    /// </summary>
    public void OnReplan(string reason, TaskPlan newPlan)
    {
        ArgumentNullException.ThrowIfNull(newPlan);

        _scratchpad.AddObservation($"Replan: {reason}");
        OnPlanCreated(newPlan);
    }

    private static string FormatPlanSummary(TaskPlan plan)
    {
        var lines = new List<string> { $"Goal: {plan.Goal}" };

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {i}. {plan.Steps[i].Description}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}
