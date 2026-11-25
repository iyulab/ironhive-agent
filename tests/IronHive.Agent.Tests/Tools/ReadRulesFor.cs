using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>Read rules for the facts that are about what the tools show.</summary>
internal static class ReadRulesFor
{
    public static FileReadRules Showing(Func<string, bool> visible) =>
        new() { IsVisible = visible, MayReturnContent = (_, _) => true };
}
