using AwesomeAssertions;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// Where the file system tells letter case apart, <c>SRC</c> is another folder than <c>src</c>: an allow for one is not an
/// allow for the other. Asking and refusing keep ignoring case, which can only be stricter.
/// </summary>
public sealed class LetterCaseOfPathsTests : IDisposable
{
    private const string CaseSensitiveOnly = "Windows and macOS open both spellings as one name.";

    private readonly string _base = Path.Combine(Path.GetTempPath(), $"letter-case-{Guid.NewGuid():N}");
    private readonly string _root;

    public LetterCaseOfPathsTests()
    {
        _root = Path.Combine(_base, "work");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, true);
        }
    }

    private static bool FileSystemTellsCaseApart => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    private PermissionEvaluator OnlySrc() => new(new PermissionConfig
    {
        WorkingDirectory = _root,
        Edit =
        [
            new PermissionRule { Pattern = "src/**", Action = PermissionAction.Allow, Priority = 10 },
            new PermissionRule { Pattern = "**/*", Action = PermissionAction.Deny, Priority = 0 },
        ],
        DefaultAction = PermissionAction.Deny,
    });

    // The positive control: the tool creates a second folder next to the allowed one.
    [Fact]
    public async Task The_tool_creates_another_folder_for_another_letter_case()
    {
        Assert.SkipUnless(FileSystemTellsCaseApart, CaseSensitiveOnly);

        await new ToolProvider(_root).WriteFile("SRC/planted.txt", "x", cancellationToken: TestContext.Current.CancellationToken);

        Directory.EnumerateDirectories(_root).Select(Path.GetFileName).Should().BeEquivalentTo("src", "SRC");
        Directory.EnumerateFileSystemEntries(Path.Combine(_root, "src")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("SRC/planted.txt")]
    [InlineData("Src/deeper/planted.txt")]
    public void An_allow_for_a_folder_does_not_cover_the_folder_with_other_letter_case(string path)
    {
        Assert.SkipUnless(FileSystemTellsCaseApart, CaseSensitiveOnly);

        OnlySrc().EvaluateEdit(path).Action.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void An_allow_outside_the_working_directory_tells_case_apart_too()
    {
        Assert.SkipUnless(FileSystemTellsCaseApart, CaseSensitiveOnly);
        var outside = Path.Combine(_base, "outside");
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            ExternalDirectory = [new PermissionRule { Pattern = $"{outside}/allowed/**", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny,
        });

        evaluator.EvaluateEdit($"{outside}/allowed/ok.txt").Action.Should().Be(PermissionAction.Allow);
        evaluator.EvaluateEdit($"{outside}/Allowed/planted.txt").Action.Should().Be(PermissionAction.Deny);
    }

    // The same spelling is allowed everywhere, and on the systems that open both spellings as one name, either is.
    [Theory]
    [InlineData("src/ok.txt")]
    [InlineData("SRC/ok.txt")]
    public void Where_two_spellings_are_one_name_either_is_allowed(string path)
    {
        Assert.SkipWhen(FileSystemTellsCaseApart && path != "src/ok.txt", "Here the two spellings are two names.");

        OnlySrc().EvaluateEdit(path).Action.Should().Be(PermissionAction.Allow);
    }

    [Theory]
    [InlineData(PermissionAction.Ask)]
    [InlineData(PermissionAction.Deny)]
    public void A_rule_that_asks_or_refuses_still_ignores_letter_case(PermissionAction action)
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read =
            [
                new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow },
                new PermissionRule { Pattern = "**/secret*", Action = action },
            ],
            DefaultAction = PermissionAction.Allow,
        });

        evaluator.EvaluateRead("sub/SECRET.txt").Action.Should().Be(action);
    }

    // Command lines and tool names are not paths of this file system; their rules ignore case as before.
    [Fact]
    public void A_command_rule_still_ignores_letter_case()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Bash = [new PermissionRule { Pattern = "git status", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny,
        });

        evaluator.EvaluateBash("Git Status").Action.Should().Be(PermissionAction.Allow);
    }
}
