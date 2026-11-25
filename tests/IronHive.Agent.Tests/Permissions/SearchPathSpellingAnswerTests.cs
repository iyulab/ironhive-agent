using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// A search returns lines of an asked-about file only where the search itself was asked about. So whatever way the search
/// folder is written, a call the policy lets through without a question must return none of those lines: the policy and the
/// tool have to read the same folder out of the same text.
/// </summary>
public sealed class SearchPathSpellingAnswerTests : IDisposable
{
    private const string Needle = "needle-in-the-haystack";
    private const string Secret = "value-only-the-asked-file-holds";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"search-spelling-{Guid.NewGuid():N}");

    public SearchPathSpellingAnswerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "ok.txt"), $"public {Needle}");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), $"{Needle} {Secret}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    // One catch-all rule asks, a narrower one allows: the folder "." and the files under it are asked about on the same terms.
    private PermissionConfig AskAboutAllButSources() => new()
    {
        WorkingDirectory = _root,
        Read =
        [
            new PermissionRule { Pattern = "src/**", Action = PermissionAction.Allow, Priority = 10 },
            new PermissionRule { Pattern = "**/*", Action = PermissionAction.Ask, Priority = 0 },
        ],
        Tools = PermissionConfig.CreateDefault().Tools,
        DefaultAction = PermissionAction.Ask,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(".")]
    [InlineData(". ")]
    [InlineData(" .")]
    [InlineData("./")]
    [InlineData("src/..")]
    public async Task A_search_let_through_without_a_question_returns_no_line_of_an_asked_about_file(string? path)
    {
        var evaluator = new PermissionEvaluator(AskAboutAllButSources());
        var arguments = new Dictionary<string, object?> { ["pattern"] = Needle, ["filePattern"] = "**/*", ["path"] = path };
        if (new ToolCallPolicy(evaluator).Evaluate("GrepFiles", arguments).Verdict != PermissionAction.Allow)
        {
            return; // a person is asked first, and the answer covers what the search returns
        }

        var tools = new ToolProvider(_root, new FileToolOptions { ReadRules = evaluator.ToReadRules() });
        string result;
        try
        {
            result = await tools.GrepFiles(Needle, "**/*", path, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return; // the tool could not open what the text names, so it returned nothing
        }

        result.Should().NotContain(Secret);
    }

    // The positive control for the fact above: the folder, written plainly, is asked about.
    [Fact]
    public void The_working_directory_itself_is_asked_about_under_these_rules() =>
        new ToolCallPolicy(AskAboutAllButSources())
            .Evaluate("GrepFiles", new Dictionary<string, object?> { ["path"] = "." })
            .Verdict.Should().Be(PermissionAction.Ask);
}
