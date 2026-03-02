using FluentAssertions;
using IronHive.Agent.Planning;

namespace IronHive.Agent.Tests.Planning;

public sealed class PlannerTriggerDetectorTests
{
    // ── Force Flag ──────────────────────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_ForceFlag_AlwaysTrue()
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning("simple", forcePlanning: true).Should().BeTrue();
    }

    [Fact]
    public void ShouldTriggerPlanning_ForceWithEmptyString_StillTrue()
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning(string.Empty, forcePlanning: true).Should().BeTrue();
    }

    // ── Long Prompt Threshold ───────────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_LongPrompt_Triggers()
    {
        var detector = new PlannerTriggerDetector();
        var longPrompt = new string('a', 801);
        detector.ShouldTriggerPlanning(longPrompt).Should().BeTrue();
    }

    [Fact]
    public void ShouldTriggerPlanning_Exactly800Chars_DoesNotTrigger()
    {
        var detector = new PlannerTriggerDetector();
        var prompt = new string('a', 800);
        detector.ShouldTriggerPlanning(prompt).Should().BeFalse();
    }

    [Fact]
    public void ShouldTriggerPlanning_799Chars_DoesNotTrigger()
    {
        var detector = new PlannerTriggerDetector();
        var prompt = new string('a', 799);
        detector.ShouldTriggerPlanning(prompt).Should().BeFalse();
    }

    // ── Short Prompt Without Patterns ───────────────────────────────────────

    [Theory]
    [InlineData("hello")]
    [InlineData("read this file")]
    [InlineData("search for files")]
    [InlineData("delete old.txt")]
    public void ShouldTriggerPlanning_ShortWithoutPatterns_DoesNotTrigger(string prompt)
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning(prompt).Should().BeFalse();
    }

    // ── English Multi-Step Patterns ─────────────────────────────────────────

    [Theory]
    [InlineData("first, then do the next thing")]
    [InlineData("first then organize files")]
    [InlineData("after that, compile the report")]
    [InlineData("next, then check the output")]
    [InlineData("step by step analysis")]
    public void ShouldTriggerPlanning_EnglishMultiStepPattern_Triggers(string prompt)
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning(prompt).Should().BeTrue();
    }

    // ── English Explicit Plan Patterns ──────────────────────────────────────

    [Theory]
    [InlineData("plan this task")]
    [InlineData("Plan This Reorganization")]
    [InlineData("create a plan for deployment")]
    [InlineData("make a plan for the migration")]
    [InlineData("break it down into steps")]
    [InlineData("break down the problem")]
    [InlineData("step by step approach")]
    public void ShouldTriggerPlanning_EnglishExplicitPlanPattern_Triggers(string prompt)
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning(prompt).Should().BeTrue();
    }

    // ── Custom Patterns Via Options ─────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_CustomPatterns_Detected()
    {
        var options = new PlannerTriggerOptions
        {
            MultiStepPatterns = [@"(foo\s+bar)"],
            ExplicitPlanPatterns = [@"(baz\s+qux)"],
        };
        var detector = new PlannerTriggerDetector(options);

        detector.ShouldTriggerPlanning("foo bar").Should().BeTrue();
        detector.ShouldTriggerPlanning("baz qux").Should().BeTrue();
        detector.ShouldTriggerPlanning("hello world").Should().BeFalse();
    }

    [Fact]
    public void ShouldTriggerPlanning_CustomPatterns_DefaultPatternsNoLongerMatch()
    {
        var options = new PlannerTriggerOptions
        {
            MultiStepPatterns = [@"(custom_only)"],
            ExplicitPlanPatterns = [@"(custom_plan)"],
        };
        var detector = new PlannerTriggerDetector(options);

        // Default patterns should not match
        detector.ShouldTriggerPlanning("step by step").Should().BeFalse();
        detector.ShouldTriggerPlanning("plan this").Should().BeFalse();

        // Custom patterns should match
        detector.ShouldTriggerPlanning("custom_only").Should().BeTrue();
        detector.ShouldTriggerPlanning("custom_plan").Should().BeTrue();
    }

    // ── MinContentLength Disabled ───────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_MinContentLengthDisabled_LongPromptDoesNotTrigger()
    {
        var options = new PlannerTriggerOptions
        {
            MinContentLength = 0,
        };
        var detector = new PlannerTriggerDetector(options);
        var longPrompt = new string('a', 10_000);

        detector.ShouldTriggerPlanning(longPrompt).Should().BeFalse();
    }

    [Fact]
    public void ShouldTriggerPlanning_MinContentLengthNegative_LongPromptDoesNotTrigger()
    {
        var options = new PlannerTriggerOptions
        {
            MinContentLength = -1,
        };
        var detector = new PlannerTriggerDetector(options);
        var longPrompt = new string('a', 10_000);

        detector.ShouldTriggerPlanning(longPrompt).Should().BeFalse();
    }

    // ── Empty Pattern Lists ─────────────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_EmptyPatternLists_OnlyLengthBased()
    {
        var options = new PlannerTriggerOptions
        {
            MultiStepPatterns = [],
            ExplicitPlanPatterns = [],
        };
        var detector = new PlannerTriggerDetector(options);

        // Pattern-bearing prompts should not trigger (no patterns configured)
        detector.ShouldTriggerPlanning("step by step").Should().BeFalse();
        detector.ShouldTriggerPlanning("plan this").Should().BeFalse();

        // Length-based still works
        detector.ShouldTriggerPlanning(new string('a', 801)).Should().BeTrue();
    }

    // ── Default Options Validation ──────────────────────────────────────────

    [Fact]
    public void DefaultOptions_HasExpectedDefaults()
    {
        var options = new PlannerTriggerOptions();

        options.MinContentLength.Should().Be(800);
        options.MultiStepPatterns.Should().HaveCount(1);
        options.ExplicitPlanPatterns.Should().HaveCount(1);
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var act = () => new PlannerTriggerDetector(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ShouldTriggerPlanning_NullPrompt_ThrowsArgumentNullException()
    {
        var detector = new PlannerTriggerDetector();
        var act = () => detector.ShouldTriggerPlanning(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Empty / Whitespace Inputs ───────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_EmptyString_DoesNotTrigger()
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void ShouldTriggerPlanning_WhitespaceOnly_DoesNotTrigger()
    {
        var detector = new PlannerTriggerDetector();
        detector.ShouldTriggerPlanning("   \t\n   ").Should().BeFalse();
    }

    // ── Virtual Method Override ─────────────────────────────────────────────

    [Fact]
    public void ShouldTriggerPlanning_CanBeOverridden()
    {
        var detector = new AlwaysTrueDetector();
        detector.ShouldTriggerPlanning("anything").Should().BeTrue();
    }

    private sealed class AlwaysTrueDetector : PlannerTriggerDetector
    {
        public override bool ShouldTriggerPlanning(string prompt, bool forcePlanning = false) => true;
    }
}
