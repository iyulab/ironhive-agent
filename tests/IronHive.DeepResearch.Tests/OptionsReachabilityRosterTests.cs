using System.Reflection;
using Iyu.Conventions.Testing;
using Xunit;

namespace IronHive.DeepResearch.Tests;

/// <summary>
/// Every public option in this library is read by the library. An option nothing reads is a promise it does not keep:
/// a caller sets it, and nothing changes and nothing is reported. The roster fails both ways — a new unread option,
/// and a listed one that has since been wired — so each change is recorded on purpose.
/// </summary>
public class OptionsReachabilityRosterTests
{
    private static readonly Assembly[] Libraries = [Assembly.Load("IronHive.DeepResearch")];

    /// <summary>Options accepted as unread today, each with the reason. Shrink this list; never grow it silently.</summary>
    private static readonly Dictionary<string, string[]> KnownUnread = new()
    {
        // Baseline when the roster was adopted (2026-09-19). Each is either to be wired or removed; the decision per
        // option is tracked in the umbrella issue draft "deepresearch-options-roster-baseline". Do not add to this list.
        ["IronHive.DeepResearch.Models.Analysis.AnalysisOptions"] = ["EnableFindingVerification", "SufficiencyThreshold"],
        ["IronHive.DeepResearch.Models.Content.ContentEnrichmentOptions"] = ["ContinueOnError"],
        ["IronHive.DeepResearch.Models.Report.ReportGenerationOptions"] = ["OutputFormat"],
        ["IronHive.DeepResearch.Models.Search.SearchExecutionOptions"] = ["ContinueOnError"],
        ["IronHive.DeepResearch.Options.DeepResearchOptions"] =
            ["CheckpointBasePath", "DefaultMaxIterations", "DefaultMaxSourcesPerIteration", "MinSourcesBeforeReport", "SessionExpiration"],
    };

    [Fact]
    public void EveryPublicOption_IsRead() =>
        OptionsReachability.Scan(Libraries, OptionsTypes.NamedWith("Options", "Config"))
            .ShouldMatchRoster(KnownUnread);
}
