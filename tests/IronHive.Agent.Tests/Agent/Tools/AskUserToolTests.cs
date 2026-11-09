using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mcp;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Tests.Agent.Tools;

/// <summary>
/// The tool a model uses to ask the person driving the turn a short question with choices. It builds the same flat form a
/// server's form-mode elicitation carries and sends it down the same bridge (<see cref="IMcpElicitationHandler"/>), so a host
/// shows one kind of question card whichever side asked; the answer comes back to the model as short JSON text. Observed through
/// a real function-invoking loop.
/// </summary>
public class AskUserToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] BothIds = ["inv", "rec"];

    private const string TwoChoices = """{"question":"What should arrive in this folder?","options":[{"id":"inv","label":"Invoices"},{"id":"rec","label":"Receipts"}]}""";

    private sealed class Person(string action, Dictionary<string, JsonElement>? content, bool supportsForms = true) : IMcpElicitationHandler
    {
        public bool SupportsForms { get; } = supportsForms;

        public List<(string Plugin, McpCallInfo? Call, ElicitRequestParams Request)> Asked { get; } = [];

        public ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the call-aware overload is the one the tool uses");

        public ValueTask<ElicitResult> ElicitAsync(string pluginName, McpCallInfo? call, ElicitRequestParams request, CancellationToken cancellationToken)
        {
            Asked.Add((pluginName, call, request));
            return ValueTask.FromResult(new ElicitResult { Action = action, Content = content });
        }
    }

    private static Dictionary<string, JsonElement> Answer(params (string Name, object Value)[] fields) =>
        fields.ToDictionary(f => f.Name, f => JsonSerializer.SerializeToElement(f.Value));

    private static async Task<string> AskAsync(Person person, string arguments, Func<bool>? available = null)
    {
        var mock = new MockChatClient()
            .EnqueueToolCallResponse(AskUserTool.ToolName, arguments)
            .EnqueueResponse("done");
        var tools = new List<AITool> { AskUserTool.Create(person, available) };

        var client = mock.AsBuilder().UseToolInvocationPipeline(new ToolInvocationPipeline([])).Build();
        await new AgentLoop(client, new AgentOptions { Tools = tools }).RunAsync("go", Ct);

        return mock.ReceivedMessages[^1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single().Result!.ToString()!;
    }

    [Fact]
    public async Task The_question_and_choices_reach_the_person_as_a_flat_form_asked_by_the_model()
    {
        var person = new Person("accept", Answer(("choice", "rec")));

        await AskAsync(person, TwoChoices);

        var (plugin, call, request) = person.Asked.Should().ContainSingle().Subject;
        plugin.Should().Be(AskUserTool.AskerName, "the person is told the model is asking, not a server");
        call!.ToolName.Should().Be(AskUserTool.ToolName);
        call.CallId.Should().NotBeNullOrEmpty("the question is tied to the model's call");
        request.Mode.Should().Be("form");
        request.Message.Should().Be("What should arrive in this folder?");
        var schema = request.RequestedSchema!;
        schema.Required.Should().Equal("choice");
        var choice = schema.Properties["choice"].Should().BeOfType<ElicitRequestParams.TitledSingleSelectEnumSchema>().Subject;
        choice.OneOf.Select(o => (o.Const, o.Title)).Should().Equal(("inv", "Invoices"), ("rec", "Receipts"));
        schema.Properties.Should().NotContainKey("other");
    }

    [Fact]
    public async Task The_answer_comes_back_to_the_model_as_short_json()
    {
        var text = await AskAsync(new Person("accept", Answer(("choice", "rec"))), TwoChoices);

        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("answer").GetString().Should().Be("accepted");
        json.RootElement.GetProperty("choice").GetString().Should().Be("rec");
    }

    [Fact]
    public async Task Allowing_other_adds_a_free_text_field_and_either_answer_counts()
    {
        var typed = new Person("accept", Answer(("other", "Contracts, mostly")));
        var text = await AskAsync(typed, """{"question":"What arrives?","options":[{"id":"inv","label":"Invoices"}],"allowOther":true}""");

        var schema = typed.Asked.Single().Request.RequestedSchema!;
        schema.Required.Should().BeEmpty("with a free-text answer allowed, no single field is required");
        schema.Properties.Should().ContainKey("other");
        using (var json = JsonDocument.Parse(text))
        {
            json.RootElement.GetProperty("answer").GetString().Should().Be("accepted");
            json.RootElement.GetProperty("other").GetString().Should().Be("Contracts, mostly");
            json.RootElement.TryGetProperty("choice", out _).Should().BeFalse();
        }

        var neither = await AskAsync(new Person("accept", new Dictionary<string, JsonElement>()),
            """{"question":"What arrives?","options":[{"id":"inv","label":"Invoices"}],"allowOther":true}""");
        JsonDocument.Parse(neither).RootElement.GetProperty("answer").GetString().Should().Be("cancelled", "an accept that says nothing is no answer");
    }

    [Fact]
    public async Task An_accept_that_carries_no_content_at_all_reads_as_cancelled_not_as_a_fault()
    {
        // With allowOther and an option no field is required, so a bare accept is a valid (empty) submission.
        var person = new Person("accept", content: null);

        var text = await AskAsync(person,
            """{"question":"What arrives?","options":[{"id":"inv","label":"Invoices"}],"allowOther":true}""");

        JsonDocument.Parse(text).RootElement.GetProperty("answer").GetString().Should().Be("cancelled");
    }

    [Fact]
    public async Task An_option_id_that_is_too_long_is_refused_rather_than_cut_to_an_id_the_model_did_not_send()
    {
        var person = new Person("accept", Answer(("choice", "x")));
        var longId = new string('i', AskUserTool.MaxIdLength + 1);

        var text = await AskAsync(person, $$"""{"question":"Q?","options":[{"id":"{{longId}}","label":"One"}]}""");

        person.Asked.Should().BeEmpty();
        text.Should().StartWith("Not asked").And.Contain("ids");
    }

    [Fact]
    public async Task Multiple_uses_a_multi_select_and_returns_an_array()
    {
        var person = new Person("accept", Answer(("choice", BothIds)));

        var text = await AskAsync(person, """{"question":"Which?","options":[{"id":"inv","label":"Invoices"},{"id":"rec","label":"Receipts"}],"multiple":true}""");

        person.Asked.Single().Request.RequestedSchema!.Properties["choice"].Should().BeOfType<ElicitRequestParams.TitledMultiSelectEnumSchema>();
        JsonDocument.Parse(text).RootElement.GetProperty("choice").EnumerateArray().Select(e => e.GetString()).Should().Equal("inv", "rec");
    }

    [Theory]
    [InlineData("decline", "declined")]
    [InlineData("cancel", "cancelled")]
    public async Task A_decline_or_a_cancel_is_told_plainly(string action, string expected)
    {
        var text = await AskAsync(new Person(action, content: null), TwoChoices);

        JsonDocument.Parse(text).RootElement.GetProperty("answer").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task An_answer_that_does_not_fit_the_question_is_not_passed_on_as_one()
    {
        var text = await AskAsync(new Person("accept", Answer(("choice", "nope"))), TwoChoices);

        JsonDocument.Parse(text).RootElement.GetProperty("answer").GetString().Should().Be("cancelled");
        text.Should().NotContain("nope");
    }

    [Fact]
    public async Task With_no_one_to_answer_the_tool_refuses_without_asking()
    {
        var person = new Person("accept", Answer(("choice", "inv")));

        var text = await AskAsync(person, TwoChoices, available: () => false);

        person.Asked.Should().BeEmpty();
        text.Should().StartWith("Not asked").And.Contain("no one");
    }

    [Fact]
    public async Task A_handler_that_cannot_show_forms_is_not_asked()
    {
        var person = new Person("accept", Answer(("choice", "inv")), supportsForms: false);

        var text = await AskAsync(person, TwoChoices);

        person.Asked.Should().BeEmpty();
        text.Should().StartWith("Not asked");
    }

    [Theory]
    [InlineData("""{"question":"  ","options":[{"id":"a","label":"A"}]}""", "question")]
    [InlineData("""{"question":"Q?","options":[]}""", "option")]
    [InlineData("""{"question":"Q?","options":[{"id":"a","label":"A"},{"id":"a","label":"B"}]}""", "twice")]
    [InlineData("""{"question":"Q?","options":[{"id":"","label":"A"}]}""", "id")]
    [InlineData("""{"question":"Q?","options":[{"id":"a","label":" "}]}""", "label")]
    public async Task Arguments_that_cannot_make_a_question_are_told_back_not_asked(string arguments, string mention)
    {
        var person = new Person("accept", Answer(("choice", "a")));

        var text = await AskAsync(person, arguments);

        person.Asked.Should().BeEmpty();
        text.Should().StartWith("Not asked").And.Contain(mention);
    }

    [Fact]
    public async Task Too_many_options_are_refused_and_long_text_is_cut()
    {
        var many = string.Join(",", Enumerable.Range(1, AskUserTool.MaxOptions + 1).Select(i => $$"""{"id":"o{{i}}","label":"Option {{i}}"}"""));
        var person = new Person("accept", Answer(("choice", "o1")));

        (await AskAsync(person, $$"""{"question":"Q?","options":[{{many}}]}""")).Should().StartWith("Not asked");
        person.Asked.Should().BeEmpty();

        var longQuestion = new string('q', AskUserTool.MaxQuestionLength + 200);
        await AskAsync(person, $$"""{"question":"{{longQuestion}}","options":[{"id":"o1","label":"One"}]}""");
        person.Asked.Single().Request.Message!.Length.Should().BeLessThanOrEqualTo(AskUserTool.MaxQuestionLength + 3);
    }

    [Fact]
    public void The_tool_is_declared_read_only_and_has_the_reserved_name()
    {
        var tool = AskUserTool.Create(new Person("cancel", null));

        tool.Name.Should().Be("ask_user");
        ToolInvocationHints.IsReadOnly(tool).Should().BeTrue("a question changes nothing, so planning mode and the default policy allow it");
    }
}
