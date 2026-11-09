using System.IO.Pipelines;
using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using IronHive.Agent.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// Form-mode elicitation: a server asks a person for a few non-sensitive values (a choice of account, a missing argument)
/// with a flat schema, and the answer travels back to the same call. Off unless the handler says it can render a form
/// (<see cref="IMcpElicitationHandler.SupportsForms"/>): a handler that does not is never asked one and the client does not
/// advertise the mode, exactly as before. The server here is a real MCP server over in-memory pipes.
/// </summary>
public sealed class McpFormElicitationTests : IAsyncDisposable
{
    private const string State = "form-state-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverStop = new();
    private Task? _server;
    private McpServer? _mcpServer;
    private bool _schemaless;

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A person with a form: records what was asked and answers with <paramref name="content"/>.</summary>
    private sealed class FormPerson(string action, Dictionary<string, JsonElement>? content) : IMcpElicitationHandler
    {
        public bool SupportsForms { get; } = true;

        public List<(string Plugin, ElicitRequestParams Request)> Asked { get; } = [];

        public ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken)
        {
            Asked.Add((pluginName, request));
            return ValueTask.FromResult(new ElicitResult { Action = action, Content = content });
        }
    }

    /// <summary>A handler of today's shape: URL mode only, no form support declared.</summary>
    private sealed class UrlOnlyPerson : IMcpElicitationHandler
    {
        public List<ElicitRequestParams> Asked { get; } = [];

        public ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return ValueTask.FromResult(new ElicitResult { Action = "accept" });
        }
    }

    private static ElicitRequestParams AccountForm() => new()
    {
        Mode = "form",
        Message = "Which account should the report be filed under?",
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                ["account"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = ["alpha", "beta"] },
            },
            Required = ["account"],
        },
    };

    private void StartServer()
    {
        var tool = McpServerTool.Create(
            (RequestContext<CallToolRequestParams> context) =>
            {
                _mcpServer = context.Server;
                if (context.Params?.RequestState == State)
                {
                    var answer = context.Params.InputResponses!["pick"].Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                    return answer.Action == "accept"
                        ? "picked:" + answer.Content!["account"].GetString()
                        : "no-answer:" + answer.Action;
                }

                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["pick"] = InputRequest.ForElicitation(_schemaless ? new ElicitRequestParams { Mode = "form", Message = "Anything?" } : AccountForm()),
                    },
                    State);
            },
            new McpServerToolCreateOptions { Name = "file_report" });

        var server = McpServer.Create(
            new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [tool] });
        _server = server.RunAsync(_serverStop.Token);
    }

    private async Task<McpPluginManager> Connect(McpPluginManager manager)
    {
        StartServer();
        await manager.ConnectCoreAsync(
            "reports",
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            new McpPluginConfig(),
            Ct);
        return manager;
    }

    [Fact]
    public async Task AFormRequest_ReachesThePersonWithItsSchema_AndTheAnswerReachesTheSameCall()
    {
        var person = new FormPerson("accept", new() { ["account"] = Json("beta") });
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("reports", "file_report", new Dictionary<string, object?>(), Ct);

        result.IsError.Should().BeFalse(result.Content);
        result.Content.Should().Contain("picked:beta", "the person's answer came back with the retry");
        var (plugin, request) = person.Asked.Should().ContainSingle().Subject;
        plugin.Should().Be("reports");
        request.Mode.Should().Be("form");
        request.Message.Should().Contain("Which account");
        var schema = request.RequestedSchema!;
        schema.Properties.Should().ContainKey("account");
        schema.Required.Should().Equal("account");
    }

    [Fact]
    public async Task ADeclineOrACancel_IsWhatTheServerReads()
    {
        var person = new FormPerson("decline", content: null);
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("reports", "file_report", new Dictionary<string, object?>(), Ct);

        result.Content.Should().Contain("no-answer:decline");
    }

    [Fact]
    public async Task AHandlerThatSupportsForms_AdvertisesBothModes()
    {
        await using var manager = await Connect(new McpPluginManager(new FormPerson("accept", new() { ["account"] = Json("alpha") })));

        await manager.CallToolAsync("reports", "file_report", new Dictionary<string, object?>(), Ct);

        var elicitation = _mcpServer!.ClientCapabilities!.Elicitation!;
        elicitation.Form.Should().NotBeNull();
        elicitation.Url.Should().NotBeNull("URL mode stays advertised");
    }

    [Fact]
    public async Task AHandlerOfTodaysShape_IsNeverAskedAForm_AndTheClientDoesNotAdvertiseIt()
    {
        var person = new UrlOnlyPerson();
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("reports", "file_report", new Dictionary<string, object?>(), Ct);

        person.Asked.Should().BeEmpty("a handler that did not opt in is never asked a form");
        _mcpServer!.ClientCapabilities!.Elicitation!.Form.Should().BeNull();
        _mcpServer.ClientCapabilities.Elicitation.Url.Should().NotBeNull();
        result.Content.Should().NotContain("picked:");
    }

    [Fact]
    public async Task AFormWithNoSchema_IsNotShown_TheServerReadsDecline()
    {
        _schemaless = true;
        var person = new FormPerson("accept", new() { ["anything"] = Json("x") });
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("reports", "file_report", new Dictionary<string, object?>(), Ct);

        person.Asked.Should().BeEmpty("there is nothing to fill in");
        result.Content.Should().Contain("no-answer:decline");
    }

    [Fact]
    public async Task APluginCannotTakeTheNameTheModelAsksUnder()
    {
        await using var manager = new McpPluginManager(new FormPerson("cancel", content: null));
        StartServer();

        var connect = () => manager.ConnectCoreAsync(
            AskUserTool.AskerName,
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            new McpPluginConfig(),
            Ct);

        await connect.Should().ThrowAsync<ArgumentException>().WithMessage("*reserved*");
    }

    [Fact]
    public async Task AnAnswerThatBreaksTheSchema_NeverReachesTheServer_ItReadsAsCancel()
    {
        // The handler is the last place a wrong value can come from; the manager does not forward it as an answer.
        var person = new FormPerson("accept", new() { ["account"] = Json("gamma") });
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("reports", "file_report", new Dictionary<string, object?>(), Ct);

        result.Content.Should().Contain("no-answer:cancel");
        result.Content.Should().NotContain("gamma");
    }

    public async ValueTask DisposeAsync()
    {
        await _serverStop.CancelAsync();
        if (_server is not null)
        {
            try
            {
                await _server;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _serverStop.Dispose();
    }
}

/// <summary>What a form answer has to satisfy: the flat, primitive schema the request carried.</summary>
public class ElicitationFormContentTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static ElicitRequestParams Form(string name, ElicitRequestParams.PrimitiveSchemaDefinition field, bool required = true) => new()
    {
        Mode = "form",
        Message = "m",
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { [name] = field },
            Required = required ? [name] : [],
        },
    };

    private static Dictionary<string, JsonElement> Content(string name, object value) => new() { [name] = Json(value) };

    private static Dictionary<string, JsonElement> Many(params string[] values) => Content("m", values);

    [Fact]
    public void AnEnumValueInTheList_IsValid_AnotherIsNot()
    {
        var request = Form("a", new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = ["x", "y"] });

        ElicitationFormContent.Validate(request, Content("a", "x")).Should().BeNull();
        ElicitationFormContent.Validate(request, Content("a", "z")).Should().Contain("'a'");
    }

    [Fact]
    public void ARequiredFieldThatIsMissing_IsNamed()
    {
        var request = Form("a", new ElicitRequestParams.StringSchema());

        ElicitationFormContent.Validate(request, new Dictionary<string, JsonElement>()).Should().Contain("'a'").And.Contain("required");
        ElicitationFormContent.Validate(request, content: null).Should().Contain("required");
    }

    [Fact]
    public void AnOptionalFieldMayBeLeftOut()
    {
        var request = Form("a", new ElicitRequestParams.StringSchema(), required: false);

        ElicitationFormContent.Validate(request, new Dictionary<string, JsonElement>()).Should().BeNull();
    }

    [Fact]
    public void ATypeThatDoesNotMatch_IsRefused()
    {
        ElicitationFormContent.Validate(Form("a", new ElicitRequestParams.StringSchema()), Content("a", 5)).Should().Contain("string");
        ElicitationFormContent.Validate(Form("a", new ElicitRequestParams.BooleanSchema()), Content("a", "yes")).Should().Contain("boolean");
        ElicitationFormContent.Validate(Form("a", new ElicitRequestParams.NumberSchema()), Content("a", "5")).Should().Contain("number");
    }

    [Fact]
    public void ANumberOutsideItsRange_IsRefused_AndAnIntegerSchemaWantsAWholeNumber()
    {
        var ranged = Form("n", new ElicitRequestParams.NumberSchema { Minimum = 1, Maximum = 3 });
        ElicitationFormContent.Validate(ranged, Content("n", 2)).Should().BeNull();
        ElicitationFormContent.Validate(ranged, Content("n", 4)).Should().Contain("at most");
        ElicitationFormContent.Validate(ranged, Content("n", 0)).Should().Contain("at least");

        var whole = Form("n", new ElicitRequestParams.NumberSchema { Type = "integer" });
        ElicitationFormContent.Validate(whole, Content("n", 2)).Should().BeNull();
        ElicitationFormContent.Validate(whole, Content("n", 2.5)).Should().Contain("whole");
    }

    [Fact]
    public void AStringOutsideItsLengthBounds_IsRefused()
    {
        var request = Form("s", new ElicitRequestParams.StringSchema { MinLength = 2, MaxLength = 4 });

        ElicitationFormContent.Validate(request, Content("s", "abc")).Should().BeNull();
        ElicitationFormContent.Validate(request, Content("s", "a")).Should().Contain("at least 2");
        ElicitationFormContent.Validate(request, Content("s", "abcde")).Should().Contain("at most 4");
    }

    [Fact]
    public void Length_is_counted_in_code_points_not_utf16_units()
    {
        // JSON Schema counts characters: an emoji (two UTF-16 units) is one.
        var request = Form("s", new ElicitRequestParams.StringSchema { MinLength = 2, MaxLength = 2 });

        ElicitationFormContent.Validate(request, Content("s", "\U0001F600\U0001F600")).Should().BeNull();
        ElicitationFormContent.Validate(request, Content("s", "\U0001F600")).Should().Contain("at least 2");
        ElicitationFormContent.Validate(request, Content("s", "\U0001F600\U0001F600\U0001F600")).Should().Contain("at most 2");
    }

    [Fact]
    public void A_malformed_schema_with_null_entries_is_checked_without_throwing()
    {
        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = "m",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["t"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        OneOf = [null!, new ElicitRequestParams.EnumSchemaOption { Const = "k", Title = "K" }],
                    },
                },
                Required = [null!, "t"],
            },
        };

        ElicitationFormContent.Validate(request, Content("t", "k")).Should().BeNull();
        ElicitationFormContent.Validate(request, Content("t", "z")).Should().Contain("'t'");
        ElicitationFormContent.Validate(request, new Dictionary<string, JsonElement>()).Should().Contain("required");
    }

    [Theory]
    [InlineData("email", "a@b.example", true)]
    [InlineData("email", "not-an-address", false)]
    [InlineData("uri", "https://example.invalid/x", true)]
    [InlineData("uri", "relative/path", false)]
    [InlineData("date", "2026-11-09", true)]
    [InlineData("date", "09/11/2026", false)]
    [InlineData("date-time", "2026-11-09T10:00:00Z", true)]
    [InlineData("date-time", "yesterday", false)]
    public void AStringFormat_IsChecked(string format, string value, bool valid)
    {
        var request = Form("s", new ElicitRequestParams.StringSchema { Format = format });

        (ElicitationFormContent.Validate(request, Content("s", value)) is null).Should().Be(valid);
    }

    [Fact]
    public void AMultiSelect_WantsAnArrayOfListedValues_WithinItsCount()
    {
        var request = Form("m", new ElicitRequestParams.UntitledMultiSelectEnumSchema
        {
            Items = new ElicitRequestParams.UntitledEnumItemsSchema { Enum = ["a", "b", "c"] },
            MinItems = 1,
            MaxItems = 2,
        });

        ElicitationFormContent.Validate(request, Many("a", "c")).Should().BeNull();
        ElicitationFormContent.Validate(request, Many("a", "z")).Should().Contain("'m'");
        ElicitationFormContent.Validate(request, Many()).Should().Contain("at least 1");
        ElicitationFormContent.Validate(request, Many("a", "b", "c")).Should().Contain("at most 2");
        ElicitationFormContent.Validate(request, Content("m", "a")).Should().Contain("array");
    }

    [Fact]
    public void ATitledSingleSelect_IsCheckedAgainstItsConstValues()
    {
        var request = Form("t", new ElicitRequestParams.TitledSingleSelectEnumSchema
        {
            OneOf = [new ElicitRequestParams.EnumSchemaOption { Const = "k1", Title = "First" }, new ElicitRequestParams.EnumSchemaOption { Const = "k2", Title = "Second" }],
        });

        ElicitationFormContent.Validate(request, Content("t", "k2")).Should().BeNull();
        ElicitationFormContent.Validate(request, Content("t", "First")).Should().Contain("'t'");
    }

    [Fact]
    public void AFieldTheSchemaDoesNotHave_IsRefused()
    {
        var request = Form("a", new ElicitRequestParams.StringSchema());
        var content = Content("a", "x");
        content["extra"] = Json("y");

        ElicitationFormContent.Validate(request, content).Should().Contain("'extra'");
    }

    [Fact]
    public void ARequestWithoutASchema_HasNothingToCheck()
    {
        ElicitationFormContent.Validate(new ElicitRequestParams { Mode = "url", Message = "m", Url = "https://example.invalid" }, content: null)
            .Should().BeNull();
    }
}
