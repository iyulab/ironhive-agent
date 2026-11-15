using AwesomeAssertions;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// The permission rules judge a directory tool by the directory it is pointed at, so <c>read: .config-folder/** deny</c> let a
/// search from <c>.</c> walk into the folder it denies. <see cref="FileToolOptions.ReadFilter"/> is what the tools themselves
/// leave out. Each fact runs the same request without a filter and with one, on a real folder.
/// </summary>
public class ReadFilterTests : IDisposable
{
    private const string Needle = "needle-in-the-haystack";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"read-filter-{Guid.NewGuid():N}");
    private readonly ToolProvider _unfiltered;
    private readonly ToolProvider _filtered;

    public ReadFilterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".config-folder", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, ".config-folder", "secret.txt"), $"password {Needle}");
        File.WriteAllText(Path.Combine(_root, ".config-folder", "nested", "deep.txt"), $"deeper {Needle}");
        File.WriteAllText(Path.Combine(_root, "src", "ok.txt"), $"public {Needle}");
        File.WriteAllText(Path.Combine(_root, "top.txt"), $"top {Needle}");

        _unfiltered = new ToolProvider(_root);
        _filtered = new ToolProvider(_root, new FileToolOptions { ReadFilter = HideConfigFolder });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        GC.SuppressFinalize(this);
    }

    // The shape of the rule `.config-folder/**`: the folder's contents, and the folder asked as a directory.
    private bool HideConfigFolder(string absolutePath)
    {
        var relative = Path.GetRelativePath(_root, absolutePath).Replace('\\', '/');
        if (absolutePath.EndsWith(Path.DirectorySeparatorChar) || absolutePath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            relative += "/";
        }

        return !relative.StartsWith(".config-folder/", StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrepFiles_from_the_root_does_not_search_a_hidden_folder()
    {
        var open = await _unfiltered.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);
        var filtered = await _filtered.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);

        open.Should().Contain("secret.txt", "without a filter the search walks into the folder");
        filtered.Should().NotContain("secret.txt").And.NotContain("deep.txt").And.NotContain("password");
        filtered.Should().Contain("ok.txt").And.Contain("top.txt", "what the filter does not hide is still searched");
    }

    [Fact]
    public void GlobFiles_from_the_root_does_not_match_a_hidden_folder()
    {
        var open = _unfiltered.GlobFiles("**/*.txt", ".");
        var filtered = _filtered.GlobFiles("**/*.txt", ".");

        open.Should().Contain("secret.txt");
        filtered.Should().NotContain("secret.txt").And.NotContain("deep.txt");
        filtered.Should().Contain("ok.txt").And.Contain("top.txt").And.Contain("Found 2 files");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListDirectory_from_the_root_does_not_list_a_hidden_folder(bool recursive)
    {
        var open = _unfiltered.ListDirectory(".", recursive);
        var filtered = _filtered.ListDirectory(".", recursive);

        open.Should().Contain(".config-folder");
        filtered.Should().NotContain(".config-folder").And.NotContain("secret.txt").And.NotContain("deep.txt").And.NotContain("nested");
        filtered.Should().Contain("src").And.Contain("top.txt");
    }

    [Fact]
    public void ListDirectory_pointed_straight_at_a_hidden_folder_refuses_with_a_reason()
    {
        var open = _unfiltered.ListDirectory(".config-folder");
        var filtered = _filtered.ListDirectory(".config-folder");

        open.Should().Contain("secret.txt");
        filtered.Should().NotContain("secret.txt").And.Contain("not available", "the model is told it is a refusal, not an empty folder");
    }

    [Fact]
    public async Task GrepFiles_and_GlobFiles_pointed_into_a_hidden_folder_return_nothing_from_it()
    {
        var grep = await _filtered.GrepFiles(Needle, "*.txt", ".config-folder", TestContext.Current.CancellationToken);
        var glob = _filtered.GlobFiles("**/*.txt", ".config-folder/nested");

        grep.Should().NotContain("secret.txt").And.NotContain("password");
        glob.Should().NotContain("deep.txt");
    }

    [Fact]
    public async Task A_pattern_that_climbs_out_of_the_base_is_held_to_the_filter_too()
    {
        var grep = await _filtered.GrepFiles(Needle, "../**/*.txt", "src", TestContext.Current.CancellationToken);
        var glob = _filtered.GlobFiles("../.config-folder/*.txt", "src");

        grep.Should().NotContain("secret.txt");
        glob.Should().NotContain("secret.txt");
    }

    [Fact]
    public async Task A_filter_that_refuses_only_the_directory_hides_what_lies_under_it()
    {
        // A rule may close a directory without naming its files; the files under it are not shown either.
        var folderOnly = new ToolProvider(_root, new FileToolOptions
        {
            ReadFilter = path => !path.EndsWith(Path.DirectorySeparatorChar + ".config-folder" + Path.DirectorySeparatorChar, StringComparison.Ordinal),
        });

        var grep = await folderOnly.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);
        var glob = folderOnly.GlobFiles("**/*.txt", ".");

        grep.Should().NotContain("secret.txt").And.NotContain("deep.txt").And.Contain("ok.txt");
        glob.Should().NotContain("secret.txt").And.NotContain("deep.txt").And.Contain("ok.txt");
    }

    [Fact]
    public void No_filter_keeps_today_s_results_and_a_filter_that_hides_nothing_changes_none()
    {
        var everything = new ToolProvider(_root, new FileToolOptions { ReadFilter = _ => true });

        // The two walk the folder in different orders, so the lines are compared as a set.
        static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order()];

        Lines(everything.ListDirectory(".", true)).Should().Equal(Lines(_unfiltered.ListDirectory(".", true)));
        Lines(everything.GlobFiles("**/*.txt", ".")).Should().Equal(Lines(_unfiltered.GlobFiles("**/*.txt", ".")));
    }

    [Fact]
    public async Task The_permission_rules_deny_becomes_the_filter_through_ToReadFilter()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read =
            [
                new PermissionRule { Pattern = ".config-folder/**", Action = PermissionAction.Deny },
                new PermissionRule { Pattern = "**", Action = PermissionAction.Allow },
            ],
        });
        var tools = new ToolProvider(_root, new FileToolOptions { ReadFilter = evaluator.ToReadFilter() });

        var listing = tools.ListDirectory(".", true);
        var grep = await tools.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);

        listing.Should().NotContain("secret.txt").And.NotContain(".config-folder");
        grep.Should().NotContain("secret.txt").And.Contain("ok.txt");
        tools.ListDirectory(".config-folder").Should().NotContain("secret.txt");
    }

    [Fact]
    public void ToReadFilter_leaves_a_path_the_rules_would_only_ask_about_visible()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = _root,
            Read = [new PermissionRule { Pattern = "src/**", Action = PermissionAction.Ask }],
            DefaultAction = PermissionAction.Ask,
        });

        var visible = evaluator.ToReadFilter()(Path.Combine(_root, "src", "ok.txt"));

        visible.Should().BeTrue("a listing cannot ask a question per entry - ReadFile does");
    }
}
