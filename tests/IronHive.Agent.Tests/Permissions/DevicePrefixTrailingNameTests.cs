using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// Behind the <c>\\?\</c> prefix Windows keeps a name as written, trailing dots and spaces included: <c>src.</c> is a
/// folder of its own there, not another spelling of <c>src</c>. The rules must not read it as <c>src</c>.
/// </summary>
public sealed class DevicePrefixTrailingNameTests : IDisposable
{
    private const string WindowsOnly = "The \\\\?\\ prefix is a Windows form.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"device-prefix-{Guid.NewGuid():N}");

    public DevicePrefixTrailingNameTests() => Directory.CreateDirectory(Path.Combine(_root, "src"));

    public void Dispose()
    {
        // The prefix is what lets the literal names be deleted again.
        var removable = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;
        if (Directory.Exists(removable))
        {
            Directory.Delete(removable, true);
        }
    }

    private PermissionEvaluator Prefixed() => new(new PermissionConfig
    {
        WorkingDirectory = @"\\?\" + _root,
        Read = [new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow }],
        Edit = [new PermissionRule { Pattern = "src/**", Action = PermissionAction.Allow }],
        Tools = PermissionConfig.CreateDefault().Tools,
        DefaultAction = PermissionAction.Ask,
    });

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

    // The positive control: written this way the tool creates a second folder next to the allowed one.
    [Fact]
    public async Task The_tool_creates_a_folder_of_its_own_for_a_name_that_ends_in_a_dot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        await new ToolProvider(_root).WriteFile(@"\\?\" + _root + @"\src.\planted.txt", "x", cancellationToken: TestContext.Current.CancellationToken);

        Directory.EnumerateDirectories(_root).Select(Path.GetFileName).Should().BeEquivalentTo("src", "src.");
        Directory.EnumerateFileSystemEntries(Path.Combine(_root, "src")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\?\", @"\src.\planted.txt")]
    [InlineData(@"\??\", @"\src.\planted.txt")]
    [InlineData(@"\\?\", @"\src\planted.txt.")]
    public void A_name_the_prefix_keeps_as_written_is_not_judged_as_the_trimmed_name(string prefix, string below)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        OnlySrc().EvaluateEdit(prefix + _root + below).Action.Should().Be(PermissionAction.Deny);
    }

    // Holding such a name back must not loosen anything: a deny for the ordinary spelling still refuses it, and where the
    // default allows, it is asked about.
    [Theory]
    [InlineData(@"\secrets.\key.txt", PermissionAction.Deny)]
    [InlineData(@"\src\key.txt.", PermissionAction.Ask)]
    public void A_deny_still_holds_and_a_default_of_allow_becomes_a_question(string below, PermissionAction expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Edit =
            [
                new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow },
                new PermissionRule { Pattern = "secrets/**", Action = PermissionAction.Deny, Priority = 10 },
            ],
            DefaultAction = PermissionAction.Allow,
        });

        evaluator.EvaluateEdit(@"\\?\" + _root + below).Action.Should().Be(expected);
    }

    // A directory tool asks about its folder only where the verdict speaks about it. Holding a name back must keep the
    // question a rule asked, and must not let the tool's own allow stand in for a folder the rules did not allow.
    [Theory]
    [InlineData("private*", PermissionAction.Ask, PermissionAction.Deny, @"\private.", PermissionAction.Deny)]
    [InlineData("src", PermissionAction.Allow, PermissionAction.Deny, @"\src.", PermissionAction.Deny)]
    [InlineData("src", PermissionAction.Allow, PermissionAction.Ask, @"\src.", PermissionAction.Ask)]
    public void A_search_of_a_folder_kept_as_written_is_not_let_through_by_the_tool_name_alone(
        string pattern, PermissionAction ruled, PermissionAction otherwise, string below, PermissionAction expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read = [new PermissionRule { Pattern = pattern, Action = ruled, Priority = 10 }],
            Tools = PermissionConfig.CreateDefault().Tools,
            DefaultAction = otherwise,
        });

        var assessment = new ToolCallPolicy(evaluator).Evaluate(
            "GrepFiles", new Dictionary<string, object?> { ["pattern"] = "x", ["path"] = @"\\?\" + _root + below });

        assessment.Verdict.Should().Be(expected);
    }

    // The tools join a relative path to the working directory as it was given, prefix included.
    [Theory]
    [InlineData(@"src.\planted.txt")]
    [InlineData(@"src\planted.txt.")]
    public async Task With_a_prefixed_working_directory_a_relative_name_is_judged_as_the_tool_opens_it(string relative)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var prefixed = @"\\?\" + _root;
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = prefixed,
            Edit =
            [
                new PermissionRule { Pattern = "src/**", Action = PermissionAction.Allow, Priority = 10 },
                new PermissionRule { Pattern = "**/*", Action = PermissionAction.Deny, Priority = 0 },
            ],
            DefaultAction = PermissionAction.Deny,
        });

        await new ToolProvider(prefixed).WriteFile(relative, "x", cancellationToken: TestContext.Current.CancellationToken);
        File.Exists(prefixed + @"\" + relative).Should().BeTrue("the tool wrote the name as written");

        evaluator.EvaluateEdit(relative).Action.Should().Be(PermissionAction.Deny);
    }

    // A stream suffix must not hide a name that ends in a dot: whatever the tool creates for such a spelling, the rules
    // speak about that file or refuse the spelling.
    [Theory]
    [InlineData(@"src.::$INDEX_ALLOCATION\planted.txt", new[] { "src.", @"src.\planted.txt" })]
    [InlineData(@"notes.md.::$DATA", new[] { "notes.md." })]
    public async Task A_stream_suffix_does_not_let_a_trailing_dot_pass_as_the_trimmed_name(string relative, string[] creates)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Edit =
            [
                new PermissionRule { Pattern = "src/**", Action = PermissionAction.Allow, Priority = 10 },
                new PermissionRule { Pattern = "notes.md", Action = PermissionAction.Allow, Priority = 10 },
                new PermissionRule { Pattern = "**/*", Action = PermissionAction.Deny, Priority = 0 },
            ],
            DefaultAction = PermissionAction.Deny,
        });

        evaluator.EvaluateEdit(relative).Action.Should().Be(PermissionAction.Deny);

        // The positive control: the call the rules refused would have created these, none of which an allow rule names.
        await new ToolProvider(_root).WriteFile(relative, "x", cancellationToken: TestContext.Current.CancellationToken);

        Directory.EnumerateFileSystemEntries(@"\\?\" + _root, "*", SearchOption.AllDirectories)
            .Select(entry => entry[(@"\\?\" + _root).Length..].TrimStart('\\'))
            .Where(entry => entry != "src")
            .Should().BeEquivalentTo(creates);
    }

    // Ordinary names stay as they were, also where the working directory carries the prefix.
    [Theory]
    [InlineData(@"src\ok.txt")]
    [InlineData(@"src\deeper\ok.txt")]
    public void With_a_prefixed_working_directory_an_ordinary_relative_name_is_still_allowed(string relative)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        Prefixed().EvaluateEdit(relative).Action.Should().Be(PermissionAction.Allow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void With_a_prefixed_working_directory_a_search_of_it_is_not_held_back(string? path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        new ToolCallPolicy(Prefixed())
            .Evaluate("GrepFiles", new Dictionary<string, object?> { ["pattern"] = "x", ["path"] = path })
            .Verdict.Should().Be(PermissionAction.Allow);
    }

    // A short name behind the prefix is written out by normalization, and is the same folder.
    [Fact]
    public void A_short_name_behind_the_prefix_is_not_a_name_kept_as_written()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Path.GetFullPath(@"C:\PROGRA~1") == @"C:\Program Files",
            "This system does not open Program Files by a short name.");
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = @"C:\Program Files",
            Read = [new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Ask,
        });

        evaluator.EvaluateRead(@"\\?\C:\PROGRA~1\some-file.txt").Action.Should().Be(PermissionAction.Allow);
    }

    [Fact]
    public void The_read_filter_hides_a_folder_kept_as_written_where_the_rules_would_refuse_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var rules = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read = [new PermissionRule { Pattern = "src", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny,
        }).ToReadRules();

        rules.IsVisible(@"\\?\" + _root + @"\src\").Should().BeTrue();
        rules.IsVisible(@"\\?\" + _root + @"\src.\").Should().BeFalse();
    }

    // The question about a folder kept as written is about its name; answering it does not cover the files under it.
    [Fact]
    public void An_answer_about_a_folder_kept_as_written_does_not_return_the_lines_under_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var outside = _root + "-outside";
        var rules = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            ExternalDirectory = [new PermissionRule { Pattern = outside.Replace('\\', '/') + "/ext*", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Ask,
        }).ToReadRules();
        var folder = @"\\?\" + outside + @"\ext.\";

        rules.MayReturnContent!(folder, folder + "f.txt").Should().BeFalse();
    }

    // The prefix alone changes nothing: such a path is still the file it names.
    [Theory]
    [InlineData(@"\\?\", @"\src\ok.txt")]
    [InlineData(@"\\.\", @"\src\ok.txt")]
    [InlineData(@"\\.\", @"\src.\ok.txt")]
    public void A_prefixed_name_the_system_reads_like_any_other_is_still_judged_by_the_rules(string prefix, string below)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        OnlySrc().EvaluateEdit(prefix + _root + below).Action.Should().Be(PermissionAction.Allow);
    }
}
