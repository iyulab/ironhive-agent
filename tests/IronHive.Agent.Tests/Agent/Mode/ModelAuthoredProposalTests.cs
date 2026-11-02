using System.Text;
using AwesomeAssertions;
using IronHive.Agent.Mode;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// The closed shape of <see cref="ModelAuthoredProposal"/>: text the model wrote reaches a trust surface only after these
/// bounds, so each is pinned on its own.
/// </summary>
public class ModelAuthoredProposalTests
{
    private static ProposalOption Option(string id, string label = "label", string rationale = "because") => new(id, label, rationale);

    private static ModelAuthoredProposal Create(IEnumerable<ProposalOption>? options, string? recommended = null, string? question = null)
    {
        ModelAuthoredProposal.TryCreate(options, recommended, question, out var proposal).Should().BeTrue();
        return proposal!;
    }

    [Fact]
    public void A_proposal_keeps_the_options_the_recommendation_and_the_question_it_was_given()
    {
        var proposal = Create([Option("a", "Edit in place", "keeps history"), Option("b", "Copy first", "safer")], recommended: "B", question: "Is history needed?");

        proposal.Options.Select(o => o.Id).Should().Equal("a", "b");
        proposal.Options[0].Label.Should().Be("Edit in place");
        proposal.Options[0].Rationale.Should().Be("keeps history");
        proposal.RecommendedOptionId.Should().Be("b", "the recommendation resolves to the option's own id, whatever case the model used");
        proposal.CriterionQuestion.Should().Be("Is history needed?");
    }

    [Fact]
    public void A_question_alone_is_a_proposal_and_a_recommendation_is_optional()
    {
        var proposal = Create(null, question: "Which environment is this?");

        proposal.Options.Should().BeEmpty();
        proposal.RecommendedOptionId.Should().BeNull();
        proposal.CriterionQuestion.Should().Be("Which environment is this?");
    }

    [Fact]
    public void Options_without_a_recommendation_or_a_question_are_a_proposal_too()
    {
        var proposal = Create([Option("a")]);

        proposal.RecommendedOptionId.Should().BeNull();
        proposal.CriterionQuestion.Should().BeNull();
    }

    public static TheoryData<string, ProposalOption[]?, string?, string?> StructuralFaults() => new()
    {
        { "more options than the cap", Enumerable.Range(0, ModelAuthoredProposal.MaxOptions + 1).Select(i => Option($"o{i}")).ToArray(), null, null },
        { "an option has no id", [Option("  ")], null, null },
        { "an option has no label", [Option("a", " \t ")], null, null },
        { "two options share an id", [Option("a"), Option("A")], null, null },
        { "the recommended id names no option", [Option("a")], "zzz", null },
        { "no options and no question", null, null, null },
        { "no options and no question", [], null, "   " },
    };

    [Theory]
    [MemberData(nameof(StructuralFaults))]
    public void A_structural_fault_refuses_the_whole_proposal_and_says_why(string reason, ProposalOption[]? options, string? recommended, string? question)
    {
        ModelAuthoredProposal.TryBuild(options, recommended, question, out var proposal, out var rejection).Should().BeFalse();

        proposal.Should().BeNull();
        rejection.Should().Be(reason);
        ModelAuthoredProposal.TryCreate(options, recommended, question, out var viaPublic).Should().BeFalse();
        viaPublic.Should().BeNull();
    }

    [Fact]
    public void A_null_option_refuses_the_proposal()
    {
        ModelAuthoredProposal.TryCreate([Option("a"), null!], null, null, out var proposal).Should().BeFalse();
        proposal.Should().BeNull();
    }

    [Fact]
    public void A_runaway_option_list_is_refused_without_being_enumerated_to_its_end()
    {
        var endless = Enumerable.Range(0, int.MaxValue).Select(i => Option($"o{i}"));

        ModelAuthoredProposal.TryCreate(endless, null, null, out var proposal).Should().BeFalse();
        proposal.Should().BeNull();
    }

    [Fact]
    public void An_over_long_string_is_cut_to_its_cap_ending_in_an_ellipsis_and_the_rest_is_kept()
    {
        var proposal = Create(
            [Option(new string('i', 100), new string('l', 300), new string('r', 1000))],
            recommended: new string('i', 100),
            question: new string('q', 900));

        var option = proposal.Options.Single();
        option.Id.Should().HaveLength(ModelAuthoredProposal.MaxOptionIdLength).And.EndWith("...");
        option.Label.Should().HaveLength(ModelAuthoredProposal.MaxLabelLength).And.EndWith("...");
        option.Rationale.Should().HaveLength(ModelAuthoredProposal.MaxRationaleLength).And.EndWith("...");
        proposal.CriterionQuestion.Should().HaveLength(ModelAuthoredProposal.MaxQuestionLength).And.EndWith("...");
        proposal.RecommendedOptionId.Should().Be(option.Id, "the recommendation is cut the same way, so it still names its option");
    }

    [Fact]
    public void A_string_exactly_at_its_cap_is_left_alone()
    {
        var label = new string('l', ModelAuthoredProposal.MaxLabelLength);

        Create([Option("a", label)]).Options.Single().Label.Should().Be(label);
    }

    [Fact]
    public void Cutting_never_splits_a_character_made_of_two_code_units()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));

        var label = Create([Option("a", emoji)]).Options.Single().Label;

        label.Should().EndWith("...");
        label.EnumerateRunes().Count().Should().Be(ModelAuthoredProposal.MaxLabelLength);
        label.EnumerateRunes().Should().NotContain(new Rune(0xFFFD), "a split pair would read back as the replacement character");
    }

    [Fact]
    public void Whitespace_and_control_characters_collapse_to_single_spaces()
    {
        var proposal = Create([Option("a", "  first\r\nsecond\tthird\u0000\u0007  fourth  ", "line one\n\n\nline two")]);

        proposal.Options.Single().Label.Should().Be("first second third fourth");
        proposal.Options.Single().Rationale.Should().Be("line one line two");
    }

    [Theory]
    [InlineData("‮", "a right-to-left override that reorders what follows")]
    [InlineData("​", "a zero-width space")]
    [InlineData("‍", "a zero-width joiner")]
    [InlineData("⁦", "a bidirectional isolate")]
    [InlineData("﻿", "a byte order mark")]
    public void Formatting_characters_that_could_disguise_the_line_are_dropped(string hidden, string what)
    {
        var proposal = Create([Option("a", $"Approve{hidden} safely{hidden}", $"x{hidden}y")], question: $"ok{hidden}?");

        proposal.Options.Single().Label.Should().Be("Approve safely", what);
        proposal.Options.Single().Rationale.Should().Be("xy", what);
        proposal.CriterionQuestion.Should().Be("ok?", what);
    }

    [Fact]
    public void Markup_and_links_are_not_interpreted_or_removed_they_stay_plain_text_for_the_host_to_render_as_text()
    {
        var text = "<b>safe</b> [click](https://example.test/) https://example.test/x";

        Create([Option("a", text)]).Options.Single().Label.Should().Be(text);
    }

    [Fact]
    public void An_approval_request_carries_no_proposal_unless_one_is_set()
    {
        var request = new ApprovalRequest { ToolName = "WriteFile", RiskAssessment = RiskAssessment.Risky(RiskLevel.High, "writes a file") };

        request.ModelProposal.Should().BeNull();
    }
}
