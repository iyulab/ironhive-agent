using AwesomeAssertions;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// A path rule judges the file the tool opens. Outside Windows a backslash is an ordinary character of a name, so
/// <c>sub\x</c> is one file next to the folder <c>sub</c>, not a file inside it.
/// </summary>
public sealed class BackslashInNameTests : IDisposable
{
    private const string NotOnWindows = "On Windows a backslash separates names.";

    private readonly string _base = Path.Combine(Path.GetTempPath(), $"backslash-name-{Guid.NewGuid():N}");
    private readonly string _root;
    private readonly string _outside;

    public BackslashInNameTests()
    {
        _root = Path.Combine(_base, "work");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_outside, "allowed"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, true);
        }
    }

    // Only the sub-folder may be written; everything else in the working directory is refused.
    private PermissionEvaluator OnlySub(string allowed = "sub/**") => new(new PermissionConfig
    {
        WorkingDirectory = _root,
        Edit =
        [
            new PermissionRule { Pattern = allowed, Action = PermissionAction.Allow, Priority = 10 },
            new PermissionRule { Pattern = "**/*", Action = PermissionAction.Deny, Priority = 0 },
        ],
        DefaultAction = PermissionAction.Deny,
    });

    [Theory]
    [InlineData("""sub\planted.txt""")]
    [InlineData("""./sub\planted.txt""")]
    [InlineData("""sub\deeper\planted.txt""")]
    public void A_name_that_only_looks_like_it_is_in_the_allowed_folder_is_not_allowed(string path)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);

        OnlySub().EvaluateEdit(path).Action.Should().Be(PermissionAction.Deny);
    }

    // The positive control for the fact above: the tool really does create such a file outside the folder.
    [Fact]
    public async Task The_tool_creates_that_name_next_to_the_folder_not_inside_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);

        await new ToolProvider(_root).WriteFile("""sub\planted.txt""", "x", cancellationToken: TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_root, """sub\planted.txt""")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_root, "sub")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("sub/**")]
    [InlineData("sub/*")]
    [InlineData("sub/?????")]
    public void A_name_with_a_backslash_inside_the_allowed_folder_is_one_name_there(string allowed)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);

        OnlySub(allowed).EvaluateEdit("""sub/ab\cd""").Action.Should().Be(PermissionAction.Allow);
    }

    // "**/" stands for whole folders. A backslash does not end one, so a name that holds it is not "file.cs" in a folder.
    [Fact]
    public void Whole_folders_in_a_pattern_end_at_a_slash_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read = [new PermissionRule { Pattern = "src/**/file.cs", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny,
        });

        // The positive control: a file really named "file.cs" in a folder under "src".
        evaluator.EvaluateRead("src/a/file.cs").Action.Should().Be(PermissionAction.Allow);
        evaluator.EvaluateRead("""src/evil\file.cs""").Action.Should().Be(PermissionAction.Deny);
    }

    // A rule written with backslashes never spoke about a path in "sub", and must not start to speak about the names that
    // only look as if they were there.
    [Theory]
    [InlineData("""sub\**""", """sub\planted.txt""")]
    [InlineData("""sub\*""", """sub\planted.txt""")]
    [InlineData("""sub\**\*""", """sub\deeper/planted.txt""")]
    public void A_pattern_written_with_a_backslash_allows_no_look_alike_name(string pattern, string path)
    {
        OnlySub(pattern).EvaluateEdit(path).Action.Should().Be(PermissionAction.Deny);
    }

    // "secret*" speaks about every name that starts with "secret". A backslash does not end such a name here.
    [Theory]
    [InlineData("""secret\x""")]
    [InlineData("""sub/secret\x""")]
    public void An_ask_rule_about_a_name_covers_a_name_that_holds_a_backslash(string path)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read =
            [
                new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow },
                new PermissionRule { Pattern = "**/secret*", Action = PermissionAction.Ask },
            ],
            // Allow, so that the question can only come from the rule: a default of Ask would pass without it.
            DefaultAction = PermissionAction.Allow,
        });

        var result = evaluator.EvaluateRead(path);

        result.Action.Should().Be(PermissionAction.Ask);
        result.MatchedRule!.Pattern.Should().Be("**/secret*");
    }

    [Fact]
    public void Outside_the_working_directory_the_same_name_is_not_in_the_allowed_folder_either()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            ExternalDirectory =
            [
                new PermissionRule { Pattern = $"{_outside}/allowed/**", Action = PermissionAction.Allow },
            ],
            DefaultAction = PermissionAction.Deny,
        });

        // The positive control: a file really in the folder is allowed.
        evaluator.EvaluateEdit($"{_outside}/allowed/ok.txt").Action.Should().Be(PermissionAction.Allow);
        evaluator.EvaluateEdit($"""{_outside}/allowed\planted.txt""").Action.Should().Be(PermissionAction.Deny);
    }

    // The same through the tool: a search nobody was asked about returns no line of the asked-about file.
    [Fact]
    public async Task A_search_returns_no_line_of_an_asked_about_file_whose_name_holds_a_backslash()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NotOnWindows);
        await File.WriteAllTextAsync(Path.Combine(_root, """secret\x"""), "needle value-of-the-asked-file", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "ok.txt"), "needle public", TestContext.Current.CancellationToken);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read =
            [
                new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow },
                new PermissionRule { Pattern = "**/secret*", Action = PermissionAction.Ask },
            ],
            // Allow, so that only the rule can keep the file's lines back.
            DefaultAction = PermissionAction.Allow,
        });
        var tools = new ToolProvider(_root, new FileToolOptions { ReadRules = evaluator.ToReadRules() });

        // The positive control: without the read rules the search does find the file's line.
        (await new ToolProvider(_root).GrepFiles("needle", "**/*", ".", TestContext.Current.CancellationToken))
            .Should().Contain("value-of-the-asked-file");

        var result = await tools.GrepFiles("needle", "**/*", ".", TestContext.Current.CancellationToken);

        result.Should().Contain("needle public");
        result.Should().NotContain("value-of-the-asked-file");
    }

    [Fact]
    public void On_Windows_a_backslash_still_separates_names()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A backslash separates names on Windows only.");

        OnlySub().EvaluateEdit("""sub\ok.txt""").Action.Should().Be(PermissionAction.Allow);
    }
}
