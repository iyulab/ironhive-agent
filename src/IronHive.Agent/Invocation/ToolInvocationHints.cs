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
/// A value is either a sequence of strings or one comma-separated string (the form a string-only transport such as MCP
/// <c>_meta</c> can carry).
/// </para>
/// </remarks>
public static class ToolInvocationHints
{
    /// <summary>The <see cref="AITool.AdditionalProperties"/> key for a tool's target arguments.</summary>
    public const string TargetArgumentsKey = "ironhive.invocation.target";

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
