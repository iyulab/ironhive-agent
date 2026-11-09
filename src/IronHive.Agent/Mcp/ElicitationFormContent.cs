using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Checks the answer to a form-mode elicitation against the flat schema the server sent: every required field present, no
/// field the schema does not have, and each value of the schema's primitive type, inside its list, range, length or format.
/// </summary>
/// <remarks>
/// <para>
/// A host that takes a person's answer calls <see cref="Validate"/> before it hands the answer to the plugin, so a typo can
/// be fixed while the question is still open. <see cref="McpPluginManager"/> runs the same check on what a handler returns and
/// does not forward an answer that fails it (the server reads <c>cancel</c>).
/// </para>
/// <para>
/// The schema is the specification's restricted subset: a flat object of strings (with <c>email</c>, <c>uri</c>, <c>date</c>
/// and <c>date-time</c> formats), numbers, booleans and single- or multi-select enums. A field type this check does not know is
/// not checked - among them the legacy titled enum, which the MCP SDK has deprecated since specification 2025-11-25.
/// </para>
/// </remarks>
public static class ElicitationFormContent
{
    /// <summary>Checks <paramref name="content"/> (the submitted values of an <c>accept</c>) against the schema of <paramref name="request"/>.</summary>
    /// <param name="request">The form request. One without a schema (a URL-mode request) has nothing to check.</param>
    /// <param name="content">The submitted values by field name; null is an empty submission.</param>
    /// <returns>Null when the answer satisfies the schema, otherwise one sentence naming the first field that does not.</returns>
    public static string? Validate(ElicitRequestParams request, IDictionary<string, JsonElement>? content)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestedSchema is not { } schema)
        {
            return null;
        }

        var fields = schema.Properties ?? new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();

        foreach (var name in schema.Required ?? [])
        {
            if (content is null || !content.ContainsKey(name))
            {
                return $"'{name}' is required.";
            }
        }

        if (content is null)
        {
            return null;
        }

        foreach (var (name, value) in content)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                return $"'{name}' is not a field of this form.";
            }

            if (Check(name, field, value) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? Check(string name, ElicitRequestParams.PrimitiveSchemaDefinition field, JsonElement value) => field switch
    {
        ElicitRequestParams.StringSchema s => CheckString(name, s, value),
        ElicitRequestParams.NumberSchema n => CheckNumber(name, n, value),
        ElicitRequestParams.BooleanSchema => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"'{name}' must be a boolean.",
        ElicitRequestParams.UntitledSingleSelectEnumSchema e => CheckOne(name, value, e.Enum),
        ElicitRequestParams.TitledSingleSelectEnumSchema e => CheckOne(name, value, e.OneOf?.Select(o => o.Const)),
        ElicitRequestParams.UntitledMultiSelectEnumSchema m => CheckMany(name, value, m.Items?.Enum, m.MinItems, m.MaxItems),
        ElicitRequestParams.TitledMultiSelectEnumSchema m => CheckMany(name, value, m.Items?.AnyOf?.Select(o => o.Const), m.MinItems, m.MaxItems),
        _ => null,
    };

    private static string? CheckString(string name, ElicitRequestParams.StringSchema schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return $"'{name}' must be a string.";
        }

        var text = value.GetString() ?? string.Empty;

        if (schema.MinLength is { } min && text.Length < min)
        {
            return $"'{name}' must be at least {min} characters.";
        }

        if (schema.MaxLength is { } max && text.Length > max)
        {
            return $"'{name}' must be at most {max} characters.";
        }

        var ok = schema.Format switch
        {
            "email" => MailAddress.TryCreate(text, out var address) && address.Address == text,
            "uri" => Uri.TryCreate(text, UriKind.Absolute, out _),
            "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "date-time" => text.Contains('T') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            _ => true,
        };

        return ok ? null : $"'{name}' must be a valid {schema.Format}.";
    }

    private static string? CheckNumber(string name, ElicitRequestParams.NumberSchema schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            return $"'{name}' must be a number.";
        }

        if (schema.Type == "integer" && number != Math.Truncate(number))
        {
            return $"'{name}' must be a whole number.";
        }

        if (schema.Minimum is { } min && number < min)
        {
            return $"'{name}' must be at least {min.ToString(CultureInfo.InvariantCulture)}.";
        }

        if (schema.Maximum is { } max && number > max)
        {
            return $"'{name}' must be at most {max.ToString(CultureInfo.InvariantCulture)}.";
        }

        return null;
    }

    private static string? CheckOne(string name, JsonElement value, IEnumerable<string>? allowed)
    {
        if (value.ValueKind != JsonValueKind.String || allowed is null || !allowed.Contains(value.GetString(), StringComparer.Ordinal))
        {
            return $"'{name}' must be one of: {string.Join(", ", allowed ?? [])}.";
        }

        return null;
    }

    private static string? CheckMany(string name, JsonElement value, IEnumerable<string>? allowed, int? minItems, int? maxItems)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return $"'{name}' must be an array.";
        }

        var items = value.EnumerateArray().ToList();

        if (minItems is { } min && items.Count < min)
        {
            return $"'{name}' must have at least {min} items.";
        }

        if (maxItems is { } max && items.Count > max)
        {
            return $"'{name}' must have at most {max} items.";
        }

        foreach (var item in items)
        {
            if (CheckOne(name, item, allowed) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }
}
