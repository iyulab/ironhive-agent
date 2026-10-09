using IronHive.Agent.Context;
using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Invocation hints a tool declares about itself, carried in <see cref="AITool.AdditionalProperties"/> so they travel
/// with the tool whichever way it was created (an in-process <see cref="AIFunction"/>, a declaration-only tool, a tool
/// loaded from an MCP server — see <c>McpPluginManager.WithDeclaredHints</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Target arguments</b> are the arguments that name what a call acts on — the <c>path</c> of an image-description
/// tool, the <c>url</c> of a fetch tool. The other arguments only shape the answer (a question, a format, a page size).
/// For a tool that declares them, the loop guards treat two calls with the same target arguments as the same call,
/// whatever the other arguments are: <see cref="RepeatedCallGuardMiddleware"/> stops a model that keeps acting on one
/// target while varying the rest, and <see cref="RepeatedResultGuardMiddleware"/> counts its visits by target. A tool
/// that declares none keeps the default — every argument decides whether two calls are the same.
/// </para>
/// <para>
/// Declare targets only where a different value of every other argument still means "the same thing again". A tool
/// whose other arguments move it on — a file read with an <c>offset</c>, a paged search — must not declare them, or its
/// paging is refused as repetition.
/// </para>
/// <para>
/// <b>Read-only</b> says whether calling the tool can change anything (<see cref="WithReadOnly"/>). A tool declared not
/// read-only is outside <see cref="RepeatedResultGuardMiddleware"/>: that guard stops a model that keeps fetching content
/// it already has, and running a command or a write again after other calls is not that — the same output (a check that
/// still fails) is information. An MCP tool answers with its server's <c>readOnlyHint</c> annotation. A tool that declares
/// nothing is treated as before.
/// </para>
/// <para>
/// A target-arguments value is either a sequence of strings or one comma-separated string (the form a string-only
/// transport such as MCP <c>_meta</c> can carry).
/// </para>
/// </remarks>
public static class ToolInvocationHints
{
    /// <summary>The <see cref="AITool.AdditionalProperties"/> key for a tool's target arguments.</summary>
    public const string TargetArgumentsKey = "ironhive.invocation.target";

    /// <summary>The <see cref="AITool.AdditionalProperties"/> key for whether a tool is read-only (a <see cref="bool"/>).</summary>
    public const string ReadOnlyKey = "ironhive.invocation.readonly";

    /// <summary>
    /// The <see cref="AITool.AdditionalProperties"/> key for a tool's own time limit per call: a <see cref="TimeSpan"/>,
    /// a number of seconds, or a string holding either. Set it with <see cref="WithMaxDuration"/>.
    /// </summary>
    public const string MaxDurationKey = "ironhive.invocation.maxduration";

    /// <summary>
    /// Whether <paramref name="tool"/> declares itself read-only (<c>true</c>), able to change state (<c>false</c>), or
    /// declares nothing (<c>null</c>). <see cref="ReadOnlyKey"/> is read first (a bool, the strings <c>"true"</c>/<c>"false"</c>,
    /// or a JSON boolean); otherwise an MCP tool's <c>readOnlyHint</c> annotation, found through
    /// <c>GetService&lt;McpClientTool&gt;()</c> so a wrapped MCP tool still answers.
    /// </summary>
    public static bool? IsReadOnly(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.AdditionalProperties.TryGetValue(ReadOnlyKey, out var value))
        {
            return value switch
            {
                bool flag => flag,
                string text when bool.TryParse(text, out var parsed) => parsed,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => false,
                _ => null,
            };
        }

        return tool.GetService<ModelContextProtocol.Client.McpClientTool>()?.ProtocolTool.Annotations?.ReadOnlyHint;
    }

    /// <summary>
    /// Returns <paramref name="tool"/> declaring whether calling it can change anything. The returned tool invokes and
    /// describes itself exactly as <paramref name="tool"/> does.
    /// </summary>
    /// <param name="tool">An <see cref="AIFunction"/> or an <see cref="AIFunctionDeclaration"/>.</param>
    /// <param name="readOnly"><c>true</c> when a call only reads; <c>false</c> when it can write, run or delete.</param>
    public static AITool WithReadOnly(this AITool tool, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var properties = ToolPropertyOverlay.CopyProperties(tool);
        properties[ReadOnlyKey] = readOnly;
        return ToolPropertyOverlay.With(tool, properties, "Invocation hints", nameof(tool));
    }

    /// <summary>
    /// The time limit per call <paramref name="tool"/> declares (<see cref="MaxDurationKey"/>), or <c>null</c> when it
    /// declares none or a value that is not a positive duration. <see cref="Timeout.InfiniteTimeSpan"/> means the tool
    /// declares no limit at all, whatever <see cref="ToolInvocationOptions.MaxInvocationDuration"/> says.
    /// </summary>
    /// <remarks>
    /// Only the host declares this — an MCP server's <c>_meta</c> is not read for it, since a server must not be able to
    /// lift the limit its client set.
    /// </remarks>
    public static TimeSpan? GetMaxDuration(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.AdditionalProperties.TryGetValue(MaxDurationKey, out var value))
        {
            return null;
        }

        TimeSpan? limit = value switch
        {
            TimeSpan span => span,
            int seconds => TimeSpan.FromSeconds(seconds),
            long seconds => TimeSpan.FromSeconds(seconds),
            double seconds => TimeSpan.FromSeconds(seconds),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } json => TimeSpan.FromSeconds(json.GetDouble()),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json => ParseDuration(json.GetString()),
            string text => ParseDuration(text),
            _ => null,
        };
        return limit == Timeout.InfiniteTimeSpan || limit > TimeSpan.Zero ? limit : null;
    }

    /// <summary>
    /// Returns <paramref name="tool"/> carrying its own time limit per call, which wins over
    /// <see cref="ToolInvocationOptions.MaxInvocationDuration"/> — longer for a tool that legitimately runs long (a build,
    /// a large download), shorter for one that should answer fast; <see cref="Timeout.InfiniteTimeSpan"/> for no limit.
    /// The returned tool invokes and describes itself exactly as <paramref name="tool"/> does.
    /// </summary>
    /// <param name="tool">An <see cref="AIFunction"/> or an <see cref="AIFunctionDeclaration"/>.</param>
    /// <param name="limit">A positive duration, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is zero or negative (other than infinite).</exception>
    public static AITool WithMaxDuration(this AITool tool, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (limit != Timeout.InfiniteTimeSpan && limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A tool's time limit must be positive, or Timeout.InfiniteTimeSpan for none.");
        }

        var properties = ToolPropertyOverlay.CopyProperties(tool);
        properties[MaxDurationKey] = limit;
        return ToolPropertyOverlay.With(tool, properties, "Invocation hints", nameof(tool));
    }

    private static TimeSpan? ParseDuration(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var span) ? span : null;

    /// <summary>The target arguments <paramref name="tool"/> declares, or an empty list.</summary>
    public static IReadOnlyList<string> GetTargetArguments(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.AdditionalProperties.TryGetValue(TargetArgumentsKey, out var value)
            ? ToolRetrievalHints.ParseValue(value).Distinct(StringComparer.Ordinal).ToArray()
            : [];
    }

    /// <summary>
    /// Returns <paramref name="tool"/> declaring <paramref name="arguments"/> as its target arguments, in place of any it
    /// already declares. The returned tool invokes and describes itself exactly as <paramref name="tool"/> does.
    /// </summary>
    /// <param name="tool">An <see cref="AIFunction"/> or an <see cref="AIFunctionDeclaration"/>.</param>
    /// <param name="arguments">Names of the arguments that name what a call acts on; each must be a parameter of the tool.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="arguments"/> is empty or names an argument the tool's parameter schema does not declare (every call
    /// would then share one target, and the guards would refuse the tool's ordinary use), or <paramref name="tool"/> is
    /// neither an <see cref="AIFunction"/> nor an <see cref="AIFunctionDeclaration"/>.
    /// </exception>
    public static AITool WithTargetArguments(this AITool tool, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);

        var targets = ToolRetrievalHints.ParseValue(arguments.ToArray()).Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0)
        {
            throw new ArgumentException("At least one target argument is required.", nameof(arguments));
        }

        var declared = ToolPropertyOverlay.DeclaredArguments(tool);
        var unknown = targets.Where(t => !declared.Contains(t)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"'{tool.Name}' has no argument named {string.Join(", ", unknown.Select(u => $"'{u}'"))}; target arguments must be " +
                $"parameters of the tool (declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared.Order(StringComparer.Ordinal)))}).",
                nameof(arguments));
        }

        var properties = ToolPropertyOverlay.CopyProperties(tool);
        properties[TargetArgumentsKey] = targets;
        return ToolPropertyOverlay.With(tool, properties, "Invocation hints", nameof(tool));
    }

    /// <summary>
    /// The subset of <paramref name="declaredTargets"/> that <paramref name="tool"/>'s parameter schema declares — for a
    /// source that cannot be trusted to name real arguments (an MCP server's <c>_meta</c>), where an unknown name is
    /// dropped instead of rejected.
    /// </summary>
    internal static string[] KnownTargets(AITool tool, IEnumerable<string> declaredTargets)
    {
        var declared = ToolPropertyOverlay.DeclaredArguments(tool);
        return declaredTargets.Where(declared.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }
}
