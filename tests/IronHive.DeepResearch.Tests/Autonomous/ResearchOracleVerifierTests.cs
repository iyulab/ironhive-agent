using FluentAssertions;
using IronHive.DeepResearch.Autonomous;
using Xunit;

namespace IronHive.DeepResearch.Tests.Autonomous;

public class ResearchOracleVerifierTests
{
    private readonly ResearchOracleVerifier _verifier = new();

    #region IsConfigured

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        _verifier.IsConfigured.Should().BeTrue();
    }

    #endregion

    #region VerifyAsync — Empty output

    [Fact]
    public async Task VerifyAsync_NullOutput_ReturnsContinueWithLowConfidence()
    {
        var verdict = await _verifier.VerifyAsync("prompt", null!);

        verdict.IsComplete.Should().BeFalse();
        verdict.CanContinue.Should().BeTrue();
        verdict.Confidence.Should().BeApproximately(0.1, 0.01);
        verdict.Analysis.Should().Contain("empty");
    }

    [Fact]
    public async Task VerifyAsync_EmptyOutput_ReturnsContinue()
    {
        var verdict = await _verifier.VerifyAsync("prompt", "");

        verdict.IsComplete.Should().BeFalse();
        verdict.CanContinue.Should().BeTrue();
        verdict.Confidence.Should().BeApproximately(0.1, 0.01);
    }

    [Fact]
    public async Task VerifyAsync_WhitespaceOutput_ReturnsContinue()
    {
        var verdict = await _verifier.VerifyAsync("prompt", "   \n  ");

        verdict.IsComplete.Should().BeFalse();
        verdict.CanContinue.Should().BeTrue();
    }

    #endregion

    #region VerifyAsync — Confidence scoring tiers

    [Fact]
    public async Task VerifyAsync_ShortReport_LowConfidence()
    {
        // <100 chars → length score 0.0
        var output = "Short report.";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeFalse();
        verdict.Confidence.Should().BeLessThan(0.5);
    }

    [Fact]
    public async Task VerifyAsync_MediumReport_MediumConfidence()
    {
        // >500 chars → length score 0.2, no sections/refs → total 0.2
        var output = new string('A', 501);

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeFalse();
        verdict.Confidence.Should().BeApproximately(0.2, 0.01);
    }

    [Fact]
    public async Task VerifyAsync_LongReport_HigherLengthScore()
    {
        // >2000 chars → length score 0.3
        var output = new string('A', 2001);

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.Confidence.Should().BeApproximately(0.3, 0.01);
    }

    [Fact]
    public async Task VerifyAsync_VeryLongReport_HighestLengthScore()
    {
        // >5000 chars → length score 0.4
        var output = new string('A', 5001);

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.Confidence.Should().BeApproximately(0.4, 0.01);
    }

    #endregion

    #region VerifyAsync — Sections and references

    [Fact]
    public async Task VerifyAsync_WithSections_Adds03()
    {
        // >500 chars with # → 0.2 + 0.3 = 0.5
        var output = new string('A', 501) + "\n# Section\nContent";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.Confidence.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public async Task VerifyAsync_WithReferences_Adds03()
    {
        // >500 chars with [] → 0.2 + 0.3 = 0.5
        var output = new string('A', 501) + " [ref1] more text";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.Confidence.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public async Task VerifyAsync_WithSectionsAndReferences_GoalAchieved()
    {
        // >5000 chars + # + [] → 0.4 + 0.3 + 0.3 = 1.0 >= 0.8 → GoalAchieved
        var output = new string('A', 5001) + "\n# Section\n[ref1]";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeTrue();
        verdict.CanContinue.Should().BeFalse();
        verdict.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task VerifyAsync_LongWithSections_GoalAchieved()
    {
        // >5000 chars + # → 0.4 + 0.3 = 0.7, but without refs it's <0.8
        // Actually: 0.7 < 0.8, so NOT GoalAchieved. Let's make it >2000 + # + [] = 0.3 + 0.3 + 0.3 = 0.9
        var output = new string('A', 2001) + "\n# Section\n[ref1]";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeTrue();
        verdict.Confidence.Should().BeApproximately(0.9, 0.01);
    }

    #endregion

    #region VerifyAsync — GoalAchieved threshold (0.8)

    [Fact]
    public async Task VerifyAsync_ConfidenceExactly08_GoalAchieved()
    {
        // >500 chars + # + [] → 0.2 + 0.3 + 0.3 = 0.8 (exactly)
        var output = new string('A', 501) + "\n# Section\nSee [ref1]";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeTrue();
        verdict.CanContinue.Should().BeFalse();
        verdict.Analysis.Should().Contain("comprehensive");
    }

    [Fact]
    public async Task VerifyAsync_ConfidenceBelow08_ContinueToNextIteration()
    {
        // >5000 chars + # but no refs → 0.4 + 0.3 = 0.7
        var output = new string('A', 5001) + "\n# Section";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeFalse();
        verdict.CanContinue.Should().BeTrue();
        // 0.7 >= 0.5 → "partially complete"
        verdict.Analysis.Should().Contain("partially complete");
    }

    #endregion

    #region VerifyAsync — Partial threshold (0.5)

    [Fact]
    public async Task VerifyAsync_ConfidenceBelow05_InsufficientMessage()
    {
        // >100 chars, no sections, no refs → 0.1
        var output = new string('A', 101);

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeFalse();
        verdict.CanContinue.Should().BeTrue();
        verdict.Confidence.Should().BeApproximately(0.1, 0.01);
        verdict.Analysis.Should().Contain("insufficient");
    }

    [Fact]
    public async Task VerifyAsync_Confidence05_PartiallyComplete()
    {
        // >500 chars + # → 0.2 + 0.3 = 0.5 (exactly)
        var output = new string('A', 501) + "\n# Heading";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.IsComplete.Should().BeFalse();
        verdict.CanContinue.Should().BeTrue();
        verdict.Analysis.Should().Contain("partially complete");
    }

    #endregion

    #region VerifyAsync — Confidence cap

    [Fact]
    public async Task VerifyAsync_MaxConfidence_CappedAt10()
    {
        // Maximum possible: 0.4 + 0.3 + 0.3 = 1.0, already at cap
        var output = new string('A', 5001) + "\n# Sec [ref]";

        var verdict = await _verifier.VerifyAsync("prompt", output);

        verdict.Confidence.Should().BeLessThanOrEqualTo(1.0);
    }

    #endregion

    #region BuildVerificationPrompt

    [Fact]
    public void BuildVerificationPrompt_IncludesQuery()
    {
        var prompt = _verifier.BuildVerificationPrompt("test query", "some output");

        prompt.Should().Contain("test query");
    }

    [Fact]
    public void BuildVerificationPrompt_IncludesLength()
    {
        var output = "some output";

        var prompt = _verifier.BuildVerificationPrompt("query", output);

        prompt.Should().Contain(output.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BuildVerificationPrompt_NullOutput_HandlesGracefully()
    {
        var prompt = _verifier.BuildVerificationPrompt("query", null!);

        prompt.Should().Contain("0");
        prompt.Should().Contain("False");
    }

    [Fact]
    public void BuildVerificationPrompt_WithSections_ReportsTrue()
    {
        var prompt = _verifier.BuildVerificationPrompt("query", "# Section content");

        prompt.Should().Contain("True");
    }

    #endregion

    #region Logger optional

    [Fact]
    public void Constructor_NullLogger_DoesNotThrow()
    {
        var verifier = new ResearchOracleVerifier(null);

        verifier.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_Default_DoesNotThrow()
    {
        var verifier = new ResearchOracleVerifier();

        verifier.Should().NotBeNull();
    }

    #endregion
}
