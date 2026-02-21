using IronHive.Agent.Context;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-09: Tests for Scratchpad — structured external working memory.
/// </summary>
public class ScratchpadTests
{
    #region Basic Properties

    [Fact]
    public void HasContent_FalseWhenEmpty()
    {
        var scratchpad = new Scratchpad();
        Assert.False(scratchpad.HasContent);
    }

    [Fact]
    public void HasContent_TrueWithPlan()
    {
        var scratchpad = new Scratchpad { CurrentPlan = "Step 1: Read code" };
        Assert.True(scratchpad.HasContent);
    }

    [Fact]
    public void HasContent_TrueWithObservation()
    {
        var scratchpad = new Scratchpad();
        scratchpad.AddObservation("Found a bug");
        Assert.True(scratchpad.HasContent);
    }

    [Fact]
    public void HasContent_TrueWithFact()
    {
        var scratchpad = new Scratchpad();
        scratchpad.SetFact("framework", "xUnit");
        Assert.True(scratchpad.HasContent);
    }

    [Fact]
    public void DefaultStep_IsZero()
    {
        var scratchpad = new Scratchpad();
        Assert.Equal(0, scratchpad.CurrentStep);
    }

    #endregion

    #region Observations

    [Fact]
    public void AddObservation_AddsToList()
    {
        var scratchpad = new Scratchpad();
        scratchpad.AddObservation("First observation");
        scratchpad.AddObservation("Second observation");

        Assert.Equal(2, scratchpad.Observations.Count);
        Assert.Contains("First observation", scratchpad.Observations);
        Assert.Contains("Second observation", scratchpad.Observations);
    }

    [Fact]
    public void AddObservation_NullOrWhitespace_Throws()
    {
        var scratchpad = new Scratchpad();

        Assert.Throws<ArgumentNullException>(() => scratchpad.AddObservation(null!));
        Assert.Throws<ArgumentException>(() => scratchpad.AddObservation(""));
        Assert.Throws<ArgumentException>(() => scratchpad.AddObservation("   "));
    }

    [Fact]
    public void AddObservation_EvictsOldest_WhenOverLimit()
    {
        var scratchpad = new Scratchpad(maxObservations: 3);
        scratchpad.AddObservation("First");
        scratchpad.AddObservation("Second");
        scratchpad.AddObservation("Third");
        scratchpad.AddObservation("Fourth");

        Assert.Equal(3, scratchpad.Observations.Count);
        Assert.DoesNotContain("First", scratchpad.Observations);
        Assert.Contains("Second", scratchpad.Observations);
        Assert.Contains("Third", scratchpad.Observations);
        Assert.Contains("Fourth", scratchpad.Observations);
    }

    #endregion

    #region Key Facts

    [Fact]
    public void SetFact_AddsFact()
    {
        var scratchpad = new Scratchpad();
        scratchpad.SetFact("framework", "xUnit");

        Assert.Single(scratchpad.KeyFacts);
        Assert.Equal("xUnit", scratchpad.KeyFacts["framework"]);
    }

    [Fact]
    public void SetFact_OverwritesExisting()
    {
        var scratchpad = new Scratchpad();
        scratchpad.SetFact("framework", "NUnit");
        scratchpad.SetFact("framework", "xUnit");

        Assert.Single(scratchpad.KeyFacts);
        Assert.Equal("xUnit", scratchpad.KeyFacts["framework"]);
    }

    [Fact]
    public void SetFact_NullOrWhitespaceKey_Throws()
    {
        var scratchpad = new Scratchpad();

        Assert.Throws<ArgumentNullException>(() => scratchpad.SetFact(null!, "value"));
        Assert.Throws<ArgumentException>(() => scratchpad.SetFact("", "value"));
    }

    [Fact]
    public void SetFact_CaseInsensitiveKeys()
    {
        var scratchpad = new Scratchpad();
        scratchpad.SetFact("Framework", "xUnit");
        scratchpad.SetFact("framework", "NUnit");

        Assert.Single(scratchpad.KeyFacts);
        Assert.Equal("NUnit", scratchpad.KeyFacts["framework"]);
    }

    [Fact]
    public void RemoveFact_RemovesExisting()
    {
        var scratchpad = new Scratchpad();
        scratchpad.SetFact("key", "value");

        Assert.True(scratchpad.RemoveFact("key"));
        Assert.Empty(scratchpad.KeyFacts);
    }

    [Fact]
    public void RemoveFact_ReturnsFalse_WhenNotFound()
    {
        var scratchpad = new Scratchpad();
        Assert.False(scratchpad.RemoveFact("nonexistent"));
    }

    #endregion

    #region Clear

    [Fact]
    public void Clear_RemovesAllContent()
    {
        var scratchpad = new Scratchpad
        {
            CurrentPlan = "Some plan",
            CurrentStep = 3
        };
        scratchpad.AddObservation("Observation");
        scratchpad.SetFact("key", "value");

        scratchpad.Clear();

        Assert.Null(scratchpad.CurrentPlan);
        Assert.Equal(0, scratchpad.CurrentStep);
        Assert.Empty(scratchpad.Observations);
        Assert.Empty(scratchpad.KeyFacts);
        Assert.False(scratchpad.HasContent);
    }

    #endregion

    #region ToContextBlock

    [Fact]
    public void ToContextBlock_ContainsMarkers()
    {
        var scratchpad = new Scratchpad { CurrentPlan = "Test plan" };
        var block = scratchpad.ToContextBlock();

        Assert.Contains(Scratchpad.BlockStart, block);
        Assert.Contains(Scratchpad.BlockEnd, block);
    }

    [Fact]
    public void ToContextBlock_IncludesPlan()
    {
        var scratchpad = new Scratchpad
        {
            CurrentPlan = "1. Read code\n2. Refactor\n3. Test",
            CurrentStep = 2
        };
        var block = scratchpad.ToContextBlock();

        Assert.Contains("Plan (step 2):", block);
        Assert.Contains("1. Read code", block);
        Assert.Contains("2. Refactor", block);
    }

    [Fact]
    public void ToContextBlock_IncludesKeyFacts()
    {
        var scratchpad = new Scratchpad();
        scratchpad.SetFact("framework", "xUnit");
        scratchpad.SetFact("mock", "NSubstitute");

        var block = scratchpad.ToContextBlock();

        Assert.Contains("Key facts:", block);
        Assert.Contains("framework: xUnit", block);
        Assert.Contains("mock: NSubstitute", block);
    }

    [Fact]
    public void ToContextBlock_IncludesObservations()
    {
        var scratchpad = new Scratchpad();
        scratchpad.AddObservation("Found circular dependency");
        scratchpad.AddObservation("Performance bottleneck in query");

        var block = scratchpad.ToContextBlock();

        Assert.Contains("Observations:", block);
        Assert.Contains("Found circular dependency", block);
        Assert.Contains("Performance bottleneck in query", block);
    }

    [Fact]
    public void ToContextBlock_TruncatesLargeContent()
    {
        var scratchpad = new Scratchpad(maxChars: 100);
        scratchpad.SetFact("long_key", new string('x', 200));

        var block = scratchpad.ToContextBlock();

        Assert.Contains("[SCRATCHPAD TRUNCATED]", block);
    }

    [Fact]
    public void ToContextBlock_AllSections()
    {
        var scratchpad = new Scratchpad
        {
            CurrentPlan = "Implement feature",
            CurrentStep = 1
        };
        scratchpad.SetFact("target", "net10.0");
        scratchpad.AddObservation("API changed in v2");

        var block = scratchpad.ToContextBlock();

        Assert.Contains("Plan (step 1):", block);
        Assert.Contains("Key facts:", block);
        Assert.Contains("Observations:", block);
        Assert.Contains(Scratchpad.BlockStart, block);
        Assert.Contains(Scratchpad.BlockEnd, block);
    }

    #endregion
}
