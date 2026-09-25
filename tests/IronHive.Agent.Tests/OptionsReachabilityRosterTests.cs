using System.Reflection;
using Iyu.Conventions.Testing;

namespace IronHive.Agent.Tests;

/// <summary>
/// Every public option in this library is read by the library. An option nothing reads is a promise it does not keep:
/// a caller sets it, and nothing changes and nothing is reported. The roster fails both ways — a new unread option,
/// and a listed one that has since been wired — so each change is recorded on purpose.
/// </summary>
public class OptionsReachabilityRosterTests
{
    private static readonly Assembly[] Libraries = [Assembly.Load("IronHive.Agent"), Assembly.Load("IronHive.Agent.Memory"), Assembly.Load("IronHive.Agent.FluxGuard"), Assembly.Load("IronHive.Agent.Ironbees")];

    /// <summary>Options accepted as unread today, each with the reason. Shrink this list; never grow it silently.</summary>
    private static readonly Dictionary<string, string[]> KnownUnread = new()
    {
        // The input contract of IAgentLoopFactory: the library defines it, its implementations read it
        // (IronHive.Host's AgentLoopFactory, consumers' own factories). Nothing in this assembly implements the factory.
        ["IronHive.Agent.Loop.AgentLoopFactoryOptions"] =
            ["MaxTokens", "Model", "Provider", "SystemPrompt", "Temperature", "ThinkingOptions", "WorkingDirectory"],
    };

    [Fact]
    public void EveryPublicOption_IsRead() =>
        OptionsReachability.Scan(Libraries, OptionsTypes.NamedWith("Options", "Config"))
            .ShouldMatchRoster(KnownUnread);
}
