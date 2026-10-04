using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tools;

/// <summary>
/// Returns a tool that invokes and describes itself exactly as the original does but carries other
/// <see cref="AITool.AdditionalProperties"/> — how the hints a tool declares about itself (retrieval aliases and
/// companions, invocation target arguments) are attached to a tool that was made elsewhere.
/// </summary>
internal static class ToolPropertyOverlay
{
    /// <summary>A copy of <paramref name="tool"/>'s properties to edit and pass to <see cref="With"/>.</summary>
    public static AdditionalPropertiesDictionary CopyProperties(AITool tool)
    {
        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in tool.AdditionalProperties)
        {
            properties[key] = value;
        }

        return properties;
    }

    /// <summary><paramref name="tool"/> carrying <paramref name="properties"/> in place of its own.</summary>
    /// <param name="tool">An <see cref="AIFunction"/> or an <see cref="AIFunctionDeclaration"/>.</param>
    /// <param name="properties">The properties the returned tool reports.</param>
    /// <param name="what">What is being attached, for the exception message.</param>
    /// <param name="paramName">The caller's parameter that holds <paramref name="tool"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="tool"/> is neither an <see cref="AIFunction"/> nor an <see cref="AIFunctionDeclaration"/>.</exception>
    public static AITool With(AITool tool, AdditionalPropertiesDictionary properties, string what, string paramName) => tool switch
    {
        AIFunction function => new OverlaidFunction(function, properties),
        AIFunctionDeclaration declaration => new OverlaidDeclaration(declaration, properties),
        _ => throw new ArgumentException(
            $"{what} can be attached to an AIFunction or an AIFunctionDeclaration, not to {tool.GetType().Name}.",
            paramName),
    };

    /// <summary>
    /// The argument names <paramref name="tool"/>'s parameter schema declares (the keys of its <c>properties</c>), or an
    /// empty set when it declares none or is not a function.
    /// </summary>
    public static HashSet<string> DeclaredArguments(AITool tool)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (tool is AIFunctionDeclaration declaration
            && declaration.JsonSchema.ValueKind == JsonValueKind.Object
            && declaration.JsonSchema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                names.Add(property.Name);
            }
        }

        return names;
    }

    private sealed class OverlaidFunction(AIFunction inner, AdditionalPropertiesDictionary properties)
        : DelegatingAIFunction(inner)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => properties;
    }

    // M.E.AI keeps its delegating declaration internal, so this forwards the declaration surface itself.
    private sealed class OverlaidDeclaration(AIFunctionDeclaration inner, AdditionalPropertiesDictionary properties)
        : AIFunctionDeclaration
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => properties;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : inner.GetService(serviceType, serviceKey);

        public override string ToString() => inner.ToString();
    }
}
