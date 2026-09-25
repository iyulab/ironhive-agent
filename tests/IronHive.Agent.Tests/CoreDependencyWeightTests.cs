using System.Xml.Linq;
using AwesomeAssertions;
using IronHive.Agent.Webhook;
using Xunit;

namespace IronHive.Agent.Tests;

/// <summary>
/// <c>IronHive.Agent</c> stays free of the optional integrations its satellite packages carry (0.19.0): Ironbees
/// (<c>IronHive.Agent.Ironbees</c>), FluxGuard (<c>IronHive.Agent.FluxGuard</c>) and MemoryIndexer
/// (<c>IronHive.Agent.Memory</c>) — and, through them, ONNX Runtime natives, Azure.AI.ContentSafety, SQLite and
/// OpenTelemetry. Before the split a host using only the loop shipped all of them. Two facts, because each alone misses
/// a way back in: a package reference nothing uses still ships as a nuspec dependency, and an assembly can arrive through
/// another reference without a package reference of its own.
/// </summary>
public class CoreDependencyWeightTests
{
    private static readonly string[] SatelliteOnly = ["Ironbees", "FluxGuard", "MemoryIndexer", "Microsoft.ML.OnnxRuntime"];

    private static bool IsSatelliteOnly(string name)
        => SatelliteOnly.Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Core_project_has_no_package_reference_to_a_satellite_dependency()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "IronHive.Agent", "IronHive.Agent.csproj");

        var references = XDocument.Load(csproj).Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        references.Should().NotBeEmpty("the scan must read the real project file");
        references.Where(IsSatelliteOnly).Should().BeEmpty(
            "these ship with IronHive.Agent.Ironbees / .FluxGuard / .Memory, not with every host of the loop");
    }

    [Fact]
    public void Core_assembly_references_no_satellite_dependency()
    {
        var referenced = typeof(WebhookService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().Contain("IronHive.Abstractions", "positive control: the scan sees real references");
        referenced.Where(IsSatelliteOnly).Should().BeEmpty();
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IronHive.Agent.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"IronHive.Agent.slnx not found above {AppContext.BaseDirectory}.");
    }
}
