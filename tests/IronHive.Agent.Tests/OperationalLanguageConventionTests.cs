using Iyu.Conventions.Testing;
using Xunit;

namespace IronHive.Agent.Tests;

/// <summary>
/// Operational text - every <c>[LoggerMessage]</c> template and every exception message - is ASCII. Operators grep it,
/// paste it into issues and search it in log pipelines whose tokenizers split on Latin word boundaries; a dash or an
/// arrow outside ASCII is as opaque there as a Korean word. The scan is <c>Iyu.Conventions.Testing</c>'s, shared with
/// the other repositories, over the same assemblies the options roster scans (every assembly this repository ships).
/// </summary>
public class OperationalLanguageConventionTests
{
    private static readonly Lazy<OperationalLanguageReport> Result = new(() =>
        OperationalLanguage.Scan(OptionsReachabilityRosterTests.Libraries, OperationalLanguage.NonAscii));

    [Fact]
    public void LogTemplatesAndExceptionMessages_AreAscii()
    {
        var findings = Result.Value.Findings;
        Assert.True(findings.Count == 0,
            "Non-ASCII operational text:\n" + string.Join("\n", findings.Select(f => $"  [{f.Kind}] {f.Location}: {f.Text}")));
    }

    /// <summary>
    /// No log template carries user or model text - a query, a prompt, a tool's arguments or result, a reason someone
    /// typed. Logs leave the host's per-user boundary (node log files, cluster collectors) and outlive a user's
    /// deletion request; a template logs a length, a count, an id or a kind under a name that says so.
    /// </summary>
    [Fact]
    public void LogTemplates_CarryNoContent()
    {
        var report = OperationalLanguage.Scan(OptionsReachabilityRosterTests.Libraries,
            OperationalLanguage.PlaceholderNamed([.. OperationalLanguage.ContentPlaceholderNames, .. AgentContentNames]));
        Assert.True(report.Findings.Count == 0,
            "Log templates that carry content (log a length, a count or a kind instead):\n"
            + string.Join("\n", report.Findings.Select(f => $"  {f.Location}: {f.Text}")));
    }

    /// <summary>Names this engine uses for text that comes from a user, an operator, a model or a tool.</summary>
    internal static readonly string[] AgentContentNames =
        ["Reason", "Goal", "Task", "Topic", "Message", "Result", "Error", "Feedback", "Plan", "Thought", "Body"];

    // Positive control: the scan must read operational text at all, or an empty finding list would pass because the
    // reader sees nothing.
    [Fact]
    public void Scan_SeesOperationalText() =>
        Assert.True(Result.Value.LogMessagesRead + Result.Value.ExceptionLiteralsRead > 0,
            $"log templates read: {Result.Value.LogMessagesRead}, exception messages read: {Result.Value.ExceptionLiteralsRead}");
}
