using IronHive.Agent.Context;
using IronHive.Agent.Extensions;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.DependencyInjection;

namespace IronHive.Agent.Tests.Extensions;

/// <summary>
/// What the container builds from the configuration it is given. Before 0.25.0 the container's ContextManager was built
/// with its bare constructor: of the registered CompactionConfig it read only the trigger and compactor settings, so
/// observation masking, tool-result compaction, the goal reminder settings, CompactOnOverflow and TargetRatio never
/// applied, and the window ignored MaxContextTokens. And AddIronHiveAgentPermissions started from an empty config, so
/// configuring one rule dropped every default rule.
/// </summary>
public class ContainerContextAndPermissionsTests
{
    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        services.AddIronHiveAgent();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheContainerContextManager_AppliesTheRegisteredCompactionConfig()
    {
        using var provider = Build(s => s.AddIronHiveAgentContext(c =>
        {
            c.TargetRatio = 0.5f;
            c.CompactOnOverflow = false;
            c.MaxContextTokens = 32_000;
        }));

        var manager = provider.GetRequiredService<ContextManager>();

        Assert.Equal(0.5f, manager.TargetRatio);
        Assert.False(manager.CompactOnOverflow);
        Assert.Equal(32_000, provider.GetRequiredService<IContextTokenCounter>().MaxContextTokens);
    }

    [Fact]
    public void WithoutAConfig_TheContainerContextManager_HasTheConfigDefaults()
    {
        using var provider = Build(_ => { });

        var manager = provider.GetRequiredService<ContextManager>();
        var defaults = new CompactionConfig();

        Assert.Equal(defaults.CompactOnOverflow, manager.CompactOnOverflow);
        Assert.Equal(defaults.TargetRatio, manager.TargetRatio);
    }

    [Fact]
    public void AddIronHiveAgentPermissions_StartsFromTheDefaultRules()
    {
        using var provider = Build(s => s.AddIronHiveAgentPermissions(c => c.ReadOnlyTools.Add("search_catalog")));

        var config = provider.GetRequiredService<PermissionConfig>();
        var defaults = PermissionConfig.CreateDefault();

        Assert.Contains("search_catalog", config.ReadOnlyTools);
        Assert.Equal(defaults.Read.Count, config.Read.Count);
        Assert.Equal(defaults.Bash.Count, config.Bash.Count);
        Assert.NotEmpty(config.Bash);
    }

    [Fact]
    public void TryLoadFromDefaultLocations_SaysWhetherAFileWasFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perm-try-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(PermissionConfigLoader.TryLoadFromDefaultLocations(dir, out var none));
            Assert.Null(none);

            Directory.CreateDirectory(Path.Combine(dir, ".ironhive"));
            File.WriteAllText(Path.Combine(dir, ".ironhive", "permissions.yaml"),
                "permissions:\n  read:\n    - pattern: \"docs/**\"\n      action: allow\n");

            Assert.True(PermissionConfigLoader.TryLoadFromDefaultLocations(dir, out var found));
            Assert.Equal("docs/**", Assert.Single(found!.Read).Pattern);
            Assert.Equal(dir, found.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
