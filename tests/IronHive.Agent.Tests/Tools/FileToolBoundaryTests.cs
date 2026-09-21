using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// The working directory is where relative paths start, not a wall. A host that wants a wall declares
/// <see cref="FileToolOptions.AllowedRoots"/>; one that declares nothing keeps the agent that can read
/// across the machine. Each fact runs the same request against both and expects two different answers.
/// </summary>
public class FileToolBoundaryTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"file-boundary-{Guid.NewGuid():N}");
    private readonly string _work;
    private readonly string _outside;
    private readonly string _secret;

    private readonly ToolProvider _open;
    private readonly ToolProvider _confined;

    public FileToolBoundaryTests()
    {
        _work = Path.Combine(_base, "work");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_work, "src", "inside.txt"), "inside");
        _secret = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(_secret, "TOP-SECRET needle");

        _open = new ToolProvider(_work);
        _confined = new ToolProvider(_work, new FileToolOptions { AllowedRoots = ["."] });
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, true);
        }

        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> EscapeForms => new()
    {
        "../outside/secret.txt",
        "src/../../outside/secret.txt",
        "./src/./../../outside/secret.txt",
        "{ABSOLUTE}",
    };

    [Theory]
    [MemberData(nameof(EscapeForms))]
    public async Task ReadFile_OutsideTheRoots_IsRefusedWithAReason_AndAllowedWithoutRoots(string form)
    {
        var path = form.Replace("{ABSOLUTE}", _secret, StringComparison.Ordinal);

        var open = await _open.ReadFile(path);
        var confined = await _confined.ReadFile(path);

        Assert.Contains("TOP-SECRET", open, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", confined, StringComparison.Ordinal);
        Assert.Contains("outside the directories this tool may access", confined, StringComparison.Ordinal);
        Assert.Contains("policy boundary", confined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsideTheRoots_NothingChanges()
    {
        Assert.Contains("inside", await _confined.ReadFile("src/inside.txt"), StringComparison.Ordinal);
        Assert.Contains("inside", await _confined.ReadFile(Path.Combine(_work, "src", "inside.txt")), StringComparison.Ordinal);
        Assert.Contains("inside", await _confined.ReadFile("src/../src/inside.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteFile_OutsideTheRoots_WritesNothing()
    {
        var target = Path.Combine(_outside, "planted.txt");

        var confined = await _confined.WriteFile("../outside/planted.txt", "x");
        Assert.Contains("outside the directories", confined, StringComparison.Ordinal);
        Assert.False(File.Exists(target));

        var open = await _open.WriteFile("../outside/planted.txt", "x");
        Assert.StartsWith("Successfully wrote", open, StringComparison.Ordinal);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ListDirectory_OutsideTheRoots_IsRefused()
    {
        Assert.Contains("secret.txt", _open.ListDirectory("../outside"), StringComparison.Ordinal);

        var confined = _confined.ListDirectory("../outside");
        Assert.DoesNotContain("secret.txt", confined, StringComparison.Ordinal);
        Assert.Contains("outside the directories", confined, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobFiles_APatternThatClimbsOut_FindsNothingOutside()
    {
        // The base directory is inside the roots; it is the pattern that leaves.
        Assert.Contains("secret.txt", _open.GlobFiles("../outside/*.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", _confined.GlobFiles("../outside/*.txt"), StringComparison.Ordinal);

        Assert.Contains("inside.txt", _confined.GlobFiles("**/*.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrepFiles_DoesNotReadOutsideTheRoots()
    {
        Assert.Contains("needle", await _open.GrepFiles("needle", "../outside/*.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", await _confined.GrepFiles("needle", "../outside/*.txt"), StringComparison.Ordinal);

        var viaBase = await _confined.GrepFiles("needle", "*.txt", "../outside");
        Assert.Contains("outside the directories", viaBase, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARootThatOnlySharesAPrefix_IsNotARoot()
    {
        // "work-notes" starts with "work" as text; it is not inside it.
        var sibling = Path.Combine(_base, "work-notes");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "n.txt"), "sibling");

        var result = await _confined.ReadFile("../work-notes/n.txt");

        Assert.DoesNotContain("sibling", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralRoots_AreEachReachable()
    {
        var tools = new ToolProvider(_work, new FileToolOptions { AllowedRoots = [".", _outside] });

        Assert.Contains("TOP-SECRET", await tools.ReadFile(_secret), StringComparison.Ordinal);
        Assert.Contains("inside", await tools.ReadFile("src/inside.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefault_IsNoBoundary()
    {
        // Read from the type rather than restated: flipping the default turns this red.
        Assert.Empty(new FileToolOptions().AllowedRoots);
    }
}
