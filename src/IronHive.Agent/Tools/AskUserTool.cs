using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using IronHive.Agent.Invocation;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Tools;

/// <summary>One choice the model offers the person with <see cref="AskUserTool"/>.</summary>
/// <param name="Id">A short id; returned to the model when the person picks the option.</param>
/// <param name="Label">What the person reads.</param>
public sealed record AskUserOption(
    [property: Description("A short id for the option; you get it back if the person picks it.")] string Id,
    [property: Description("A short label the person reads.")] string Label);

/// <summary>
/// The tool a model uses to ask the person who is driving the turn a short question with a few choices - «what should arrive in
/// this folder?», «which account?» - and carry on with the answer inside the same turn.
/// </summary>
/// <remarks>
/// <para>
/// Off until registered: add <see cref="Create"/> to the loop's <c>Tools</c>. The question is sent as the same flat form an MCP
/// server's form-mode elicitation carries, through the same <see cref="IMcpElicitationHandler"/>, so a host shows one kind of
/// question card whichever side asked; the handler's <see cref="IMcpElicitationHandler.ElicitAsync(string, McpCallInfo?, ModelContextProtocol.Protocol.ElicitRequestParams, CancellationToken)"/>
/// is given <see cref="AskerName"/> as the asker (never a server's name), so the person is told the <b>model</b> is asking - the
/// question and the labels are the model's text, shown as such.
/// </para>
/// <para>
/// The answer reaches the model as short JSON text: <c>{"answer":"accepted","choice":"id"}</c> (an array of ids for
/// <c>multiple</c>, and <c>"other"</c> with the person's own words when the model allowed that), <c>{"answer":"declined"}</c> or
/// <c>{"answer":"cancelled"}</c>. A refusal is plain text starting with <c>Not asked</c>: the arguments cannot make a question,
/// the handler cannot show forms (<see cref="IMcpElicitationHandler.SupportsForms"/>), or nobody is there to answer
/// (<c>isPersonAvailable</c> - a scheduled or background run has no one to ask).
/// </para>
/// <para>
/// The tool changes nothing and is declared read-only, so planning mode and the default policy allow it. A question is not a place
/// for secrets - tell the model not to ask for passwords, keys or tokens.
/// </para>
/// </remarks>
public static class AskUserTool
{
    /// <summary>The tool's name.</summary>
    public const string ToolName = "ask_user";

    /// <summary>The asker named to the handler for a question the model asks (an MCP server's own name is never this).</summary>
    public const string AskerName = "model";

    /// <summary>The most options a question can offer.</summary>
    public const int MaxOptions = 8;

    /// <summary>The longest question; longer text is cut.</summary>
    public const int MaxQuestionLength = 500;

    /// <summary>The longest option label; longer text is cut.</summary>
    public const int MaxLabelLength = 80;

    /// <summary>The longest option id; longer text is cut.</summary>
    public const int MaxIdLength = 40;

    /// <summary>The longest free-text answer a person can give when the model allows one.</summary>
    public const int MaxOtherLength = 1000;

    internal const string Description =
        "Ask the person you are working with a short question when you need their answer to go on - a choice you cannot make " +
        "for them. Give the question and 1 to 8 options (a short id and a label each); set allowOther when they may answer in " +
        "their own words, and multiple when they may pick several. You get their answer back as JSON. Ask only what you need, " +
        "once, and never ask for a password, key or token. If this tool says it did not ask, decide yourself or end your turn " +
        "with the question in plain text.";

    /// <summary>Creates the tool (declared read-only: a question changes nothing).</summary>
    /// <param name="handler">Puts the question in front of a person; the host's elicitation handler (it must say it can show forms).</param>
    /// <param name="isPersonAvailable">Says whether anyone can answer right now (for <c>run --server</c>: a client is attached);
    /// null means always. When it says no, the tool refuses without asking.</param>
    public static AITool Create(IMcpElicitationHandler handler, Func<bool>? isPersonAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        async Task<string> Ask(
            [Description("The question, short and plain - no markup or links.")] string question,
            [Description("The choices, 1 to 8, each with a short id and a label. May be empty only when allowOther is true.")] AskUserOption[]? options = null,
            [Description("True when the person may answer in their own words instead of, or as well as, picking.")] bool allowOther = false,
            [Description("True when the person may pick more than one option.")] bool multiple = false,
            CancellationToken cancellationToken = default)
            => await AskAsync(handler, isPersonAvailable, question, options, allowOther, multiple, cancellationToken);

        return AIFunctionFactory.Create(Ask, new AIFunctionFactoryOptions { Name = ToolName, Description = Description })
            .WithReadOnly(true);
    }

    internal static async Task<string> AskAsync(
        IMcpElicitationHandler handler, Func<bool>? isPersonAvailable, string? question, AskUserOption[]? options,
        bool allowOther, bool multiple, CancellationToken cancellationToken)
    {
        if (isPersonAvailable is not null && !isPersonAvailable())
        {
            return "Not asked: no one is attached to answer questions in this run (it is unattended or in the background). " +
                   "Decide yourself, or end your turn with the question in plain text.";
        }

        if (!handler.SupportsForms)
        {
            return "Not asked: this host cannot show a question with choices. End your turn with the question in plain text instead.";
        }

        if (Build(question, options, allowOther, multiple, out var request, out var problem) is false)
        {
            return $"Not asked: {problem}. Call again with a valid question.";
        }

        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId;
        var result = await handler.ElicitAsync(AskerName, new McpCallInfo(ToolName, callId), request!, cancellationToken);

        return Told(request!, result);
    }

    private static bool Build(
        string? question, AskUserOption[]? options, bool allowOther, bool multiple,
        out ElicitRequestParams? request, out string? problem)
    {
        request = null;
        var asked = ModelAuthoredProposal.Clean(question, MaxQuestionLength);
        if (asked.Length == 0)
        {
            problem = "give the question in 'question'";
            return false;
        }

        var offered = options ?? [];
        if (offered.Length == 0 && !allowOther)
        {
            problem = "give at least one option, or set allowOther so the person can answer in their own words";
            return false;
        }

        if (offered.Length > MaxOptions)
        {
            problem = $"offer at most {MaxOptions} options";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var built = new List<ElicitRequestParams.EnumSchemaOption>(offered.Length);
        foreach (var option in offered)
        {
            var id = ModelAuthoredProposal.Clean(option?.Id, MaxIdLength);
            var label = ModelAuthoredProposal.Clean(option?.Label, MaxLabelLength);
            if (id.Length == 0)
            {
                problem = "every option needs an id";
                return false;
            }

            if (label.Length == 0)
            {
                problem = $"option '{id}' needs a label";
                return false;
            }

            if (!seen.Add(id))
            {
                problem = $"the option id '{id}' is used twice";
                return false;
            }

            built.Add(new ElicitRequestParams.EnumSchemaOption { Const = id, Title = label });
        }

        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        if (built.Count > 0)
        {
            properties["choice"] = multiple
                ? new ElicitRequestParams.TitledMultiSelectEnumSchema
                {
                    Title = "Choice",
                    Items = new ElicitRequestParams.TitledEnumItemsSchema { AnyOf = built },
                    MinItems = allowOther ? 0 : 1,
                }
                : new ElicitRequestParams.TitledSingleSelectEnumSchema { Title = "Choice", OneOf = built };
        }

        if (allowOther)
        {
            properties["other"] = new ElicitRequestParams.StringSchema { Title = "Other", MaxLength = MaxOtherLength };
        }

        request = new ElicitRequestParams
        {
            Mode = "form",
            Message = asked,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = properties,
                Required = allowOther ? (built.Count == 0 ? ["other"] : []) : ["choice"],
            },
        };
        problem = null;
        return true;
    }

    // What the model reads back: short JSON, never the person's raw event.
    private static string Told(ElicitRequestParams request, ElicitResult result)
    {
        var answer = new JsonObject();
        switch (result.Action)
        {
            case "accept" when ElicitationFormContent.Validate(request, result.Content) is null:
                var content = result.Content!;
                var gave = false;
                if (content.TryGetValue("choice", out var choice))
                {
                    answer["choice"] = JsonNode.Parse(choice.GetRawText());
                    gave = !(choice.ValueKind == JsonValueKind.Array && choice.GetArrayLength() == 0);
                }

                if (content.TryGetValue("other", out var other) && other.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(other.GetString()))
                {
                    answer["other"] = other.GetString();
                    gave = true;
                }

                // An accept that says nothing is no answer.
                return gave ? Json("accepted", answer) : Json("cancelled", new JsonObject());
            case "decline":
                return Json("declined", new JsonObject());
            default:
                // A cancel, a dismissed question, and an accept that does not fit the question it answers.
                return Json("cancelled", new JsonObject());
        }
    }

    private static string Json(string outcome, JsonObject fields)
    {
        var result = new JsonObject { ["answer"] = outcome };
        foreach (var (name, value) in fields.ToList())
        {
            fields.Remove(name);
            result[name] = value;
        }

        return result.ToJsonString();
    }
}
