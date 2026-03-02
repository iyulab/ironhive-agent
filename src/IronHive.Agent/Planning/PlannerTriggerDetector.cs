using System.Text.RegularExpressions;

namespace IronHive.Agent.Planning;

/// <summary>
/// Detects whether a user prompt should trigger the planning pipeline
/// instead of direct execution.
/// <para>
/// Default patterns target English multi-step / explicit-plan phrases.
/// Subclass and supply custom <see cref="PlannerTriggerOptions"/> to add
/// domain-specific or locale-specific patterns.
/// </para>
/// </summary>
public class PlannerTriggerDetector
{
    private readonly PlannerTriggerOptions _options;
    private readonly List<Regex> _multiStepRegexes;
    private readonly List<Regex> _explicitPlanRegexes;

    /// <summary>
    /// Initializes a new instance with default options.
    /// </summary>
    public PlannerTriggerDetector()
        : this(new PlannerTriggerOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified options.
    /// </summary>
    public PlannerTriggerDetector(PlannerTriggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _multiStepRegexes = CompilePatterns(_options.MultiStepPatterns);
        _explicitPlanRegexes = CompilePatterns(_options.ExplicitPlanPatterns);
    }

    /// <summary>
    /// Returns <c>true</c> if the prompt should trigger plan-and-execute mode.
    /// </summary>
    public virtual bool ShouldTriggerPlanning(string prompt, bool forcePlanning = false)
    {
        if (forcePlanning)
        {
            return true;
        }

        if (_options.MinContentLength > 0 && prompt.Length > _options.MinContentLength)
        {
            return true;
        }

        return _multiStepRegexes.Any(r => r.IsMatch(prompt))
            || _explicitPlanRegexes.Any(r => r.IsMatch(prompt));
    }

    private static List<Regex> CompilePatterns(List<string> patterns)
    {
        return patterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
    }
}
