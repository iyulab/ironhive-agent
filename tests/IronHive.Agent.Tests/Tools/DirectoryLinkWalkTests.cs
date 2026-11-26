using AwesomeAssertions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// A recursive listing or search does not enter a link to a directory: a link back up the tree made the framework's own recursion
/// run until it threw <see cref="OutOfMemoryException"/>, and a link out of the tree is not part of the folder that was asked
/// about. The links left out are named so the model knows to look inside one by its own path. Each fact uses a real link and is
/// skipped where the account cannot create one.
/// </summary>
public class DirectoryLinkWalkTests : IDisposable
{
    private const string Needle = "needle-in-the-haystack";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"link-walk-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"link-walk-outside-{Guid.NewGuid():N}");
    private readonly ToolProvider _tools;
    private readonly ToolProvider _filtered;

    public DirectoryLinkWalkTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "top.txt"), $"top {Needle}");
        File.WriteAllText(Path.Combine(_root, "src", "ok.txt"), $"public {Needle}");
        File.WriteAllText(Path.Combine(_outside, "linked.txt"), $"linked {Needle}");

        _tools = new ToolProvider(_root);
        _filtered = new ToolProvider(_root, new FileToolOptions { ReadRules = ReadRulesFor.Showing(_ => true) });
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _outside })
        {
            if (Directory.Exists(directory))
            {
                RemoveLinks(directory);
                Directory.Delete(directory, true);
            }
        }

        GC.SuppressFinalize(this);
    }

    // Each link is removed by itself first: the recursive delete fails on a junction ("The parameter is incorrect")
    private static void RemoveLinks(string directory)
    {
        foreach (var child in new DirectoryInfo(directory).GetDirectories())
        {
            if (child.LinkTarget is not null)
            {
                child.Delete();
            }
            else
            {
                RemoveLinks(child.FullName);
            }
        }
    }

    // A directory symbolic link, or on Windows - where an account without the privilege cannot make one - a junction, which
    // needs none: the facts then run instead of being skipped
    private static void Link(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows() || !Junction(link, target))
            {
                Assert.Skip("This account cannot create a directory link: " + ex.GetType().Name);
            }
        }
    }

    private static bool Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public async Task A_junction_back_to_the_root_is_not_entered_by_any_of_the_three_tools()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions exist on Windows only");
        Junction(Path.Combine(_root, "src", "loop"), _root).Should().BeTrue("a junction needs no privilege");

        var listing = _tools.ListDirectory(".", true);
        var globbed = _tools.GlobFiles("**/*.txt", ".");
        var grepped = await _tools.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);

        listing.Should().Contain("directory link, not listed here");
        listing.Split('\n').Count(line => line.Contains("top.txt", StringComparison.Ordinal)).Should().Be(1);
        globbed.Split('\n').Count(line => line.Contains("top.txt", StringComparison.Ordinal)).Should().Be(1);
        grepped.Split('\n').Count(line => line.Contains("top.txt:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Theory]
    [InlineData("*.txt")]
    [InlineData("src/*.txt")]
    public void A_pattern_that_never_goes_into_a_link_says_nothing_about_it(string pattern)
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var found = _tools.GlobFiles(pattern, ".");

        found.Should().StartWith("Found 1 files").And.NotContain("were not entered");
    }

    // The note's paths are what a path argument takes - also when the search started below the working directory
    [Fact]
    public void A_link_named_by_a_search_that_started_below_the_working_directory_can_be_passed_back_as_named()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "wrong.txt"), "x");
        Link(Path.Combine(_root, "src", "docs"), _outside);

        var found = _tools.GlobFiles("**/*.txt", "src");
        var listed = _tools.ListDirectory("src", true);

        found.Should().Contain("directory links were not entered: src/docs/.");
        listed.Should().Contain("list 'src/docs/' to see inside");
        _tools.GlobFiles("**/*.txt", "src/docs/").Should().Contain("linked.txt").And.NotContain("wrong.txt");
    }

    [Fact]
    public void A_link_outside_the_allowed_roots_is_not_named()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);
        var confined = new ToolProvider(_root, new FileToolOptions { AllowedRoots = [Path.Combine(_root, "src")] });

        var found = confined.GlobFiles("../**/*.txt", "src");

        found.Should().Contain("ok.txt").And.NotContain("linked-pkg");
    }

    [Fact]
    public void A_link_past_the_rows_a_listing_shows_is_still_said_to_be_left_out()
    {
        for (var i = 0; i < 500; i++)
        {
            Directory.CreateDirectory(Path.Combine(_root, $"d{i:000}"));
        }

        // A listing puts a folder's own entries before its subfolders', so a link inside one of the 500 folders comes
        // after all of them on every file system. The order among siblings is the file system's and is not sorted everywhere.
        Link(Path.Combine(_root, "d250", "zz-linked"), _outside);

        var listing = _tools.ListDirectory(".", true);

        listing.Should().Contain("truncated").And.Contain("directory links not shown above were not entered either: d250/zz-linked/.");
    }

    [Fact]
    public void A_folder_removed_while_a_listing_walks_does_not_fail_the_listing()
    {
        var doomed = Path.Combine(_root, "doomed");
        Directory.CreateDirectory(doomed);
        // The filter is asked about the folder while its parent is read; the walk opens it afterwards
        var removing = new ToolProvider(_root, new FileToolOptions
        {
            ReadRules = ReadRulesFor.Showing(path =>
            {
                if (path.EndsWith("doomed" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(doomed))
                {
                    Directory.Delete(doomed, true);
                }

                return true;
            }),
        });

        var listing = removing.ListDirectory(".", true);

        listing.Should().Contain("ok.txt");
    }

    [Fact]
    public void A_folder_removed_after_the_matcher_saw_it_reads_as_empty()
    {
        var gone = new DirectoryInfo(Path.Combine(_root, "gone"));
        gone.Create();
        gone.Refresh();
        gone.Exists.Should().BeTrue("the walk's view of the folder is the one read with its parent");
        Directory.Delete(gone.FullName);

        var entries = new LinkSkippingDirectoryInfo(gone, []).EnumerateFileSystemInfos().ToList();

        entries.Should().BeEmpty();
    }

    [Fact]
    public void A_recursive_listing_ends_at_a_link_back_to_the_root_and_names_it()
    {
        Link(Path.Combine(_root, "src", "loop"), _root);

        foreach (var tools in new[] { _tools, _filtered })
        {
            var listing = tools.ListDirectory(".", true);

            listing.Should().Contain("loop").And.Contain("directory link, not listed here");
            listing.Split('\n').Count(line => line.Contains("top.txt", StringComparison.Ordinal)).Should().Be(1, "top.txt is reached once, not again through the link");
        }
    }

    [Fact]
    public void A_recursive_listing_does_not_list_what_lies_behind_a_link_to_another_tree()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        foreach (var tools in new[] { _tools, _filtered })
        {
            var listing = tools.ListDirectory(".", true);

            listing.Should().Contain("linked-pkg/").And.Contain("directory link, not listed here");
            listing.Should().NotContain("linked.txt", "a link is not entered; the model lists its own path to see inside");
        }
    }

    [Fact]
    public void A_listing_pointed_at_a_link_lists_inside_it()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var listing = _tools.ListDirectory("linked-pkg", true);

        listing.Should().Contain("linked.txt");
    }

    [Fact]
    public void A_non_recursive_listing_lists_a_link_like_any_directory_without_a_note()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var listing = _tools.ListDirectory(".");

        listing.Should().Contain("linked-pkg/").And.NotContain("not listed here");
    }

    [Fact]
    public void GlobFiles_ends_at_a_link_back_to_the_root_and_names_it()
    {
        Link(Path.Combine(_root, "src", "loop"), _root);

        var found = _tools.GlobFiles("**/*.txt", ".");

        found.Should().Contain("top.txt").And.Contain("ok.txt").And.Contain("directory links were not entered: src/loop/");
        found.Split('\n').Count(line => line.Contains("top.txt", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void GlobFiles_does_not_match_files_behind_a_link_to_another_tree_and_says_so()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var found = _tools.GlobFiles("**/*.txt", ".");

        found.Should().Contain("top.txt").And.NotContain("linked.txt").And.Contain("directory links were not entered: linked-pkg/");
    }

    [Fact]
    public void GlobFiles_pointed_at_a_link_matches_inside_it()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var found = _tools.GlobFiles("**/*.txt", "linked-pkg");

        found.Should().Contain("linked.txt").And.NotContain("were not entered");
    }

    [Fact]
    public void GlobFiles_names_the_links_even_when_nothing_matches()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var found = _tools.GlobFiles("**/*.nothing", ".");

        found.Should().StartWith("No files found matching pattern: **/*.nothing").And.Contain("linked-pkg/");
    }

    [Fact]
    public void GlobFiles_with_a_literal_path_through_a_link_does_not_enter_it()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var found = _tools.GlobFiles("linked-pkg/*.txt", ".");

        found.Should().StartWith("No files found matching pattern: linked-pkg/*.txt").And.Contain("directory links were not entered: linked-pkg/");
    }

    [Fact]
    public void A_link_inside_a_folder_the_filter_hides_is_not_named()
    {
        Directory.CreateDirectory(Path.Combine(_root, "secret"));
        Link(Path.Combine(_root, "secret", "inner-link"), _outside);
        Link(Path.Combine(_root, "linked-pkg"), _outside);
        var hidingSecret = new ToolProvider(_root, new FileToolOptions
        {
            // Hides the folder itself and nothing by its spelling: what is under it stays out only because its folder is hidden
            ReadRules = ReadRulesFor.Showing(path => !path.EndsWith(Path.DirectorySeparatorChar + "secret" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
        });

        var found = hidingSecret.GlobFiles("**/*.txt", ".");

        found.Should().Contain("linked-pkg/").And.NotContain("inner-link").And.NotContain("secret");
    }

    [Fact]
    public void A_listing_pointed_at_a_link_still_stops_at_a_link_inside_it()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);
        Link(Path.Combine(_outside, "back"), _root);

        var listing = _tools.ListDirectory("linked-pkg", true);

        listing.Should().Contain("linked.txt").And.Contain("back/").And.Contain("directory link, not listed here");
        listing.Should().NotContain("top.txt", "the link back to the root is not entered");
    }

    [Fact]
    public void A_recursive_listing_without_links_keeps_the_order_of_the_framework_enumeration()
    {
        // A tree whose siblings and depths would come out differently from a depth-first or a reversed walk.
        foreach (var directory in new[] { "a/x/deep", "b", "c/y" })
        {
            Directory.CreateDirectory(Path.Combine(_root, directory));
        }

        foreach (var file in new[] { "a/a.txt", "a/x/ax.txt", "a/x/deep/d.txt", "b/b.txt", "c/c.txt", "c/y/cy.txt" })
        {
            File.WriteAllText(Path.Combine(_root, file), "x");
        }

        var expectedDirs = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(_root, d));
        var expectedFiles = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f));

        foreach (var tools in new[] { _tools, _filtered })
        {
            var lines = tools.ListDirectory(".", true).Split('\n', StringSplitOptions.RemoveEmptyEntries);

            lines.Where(line => line.StartsWith("[DIR]", StringComparison.Ordinal)).Select(line => line[7..].TrimEnd('\r').TrimEnd('/'))
                .Should().Equal(expectedDirs);
            lines.Where(line => line.StartsWith("[FILE]", StringComparison.Ordinal)).Select(line => line[7..line.LastIndexOf(" (", StringComparison.Ordinal)])
                .Should().Equal(expectedFiles);
        }
    }

    [Fact]
    public async Task GrepFiles_ends_at_a_link_back_to_the_root_and_names_it()
    {
        Link(Path.Combine(_root, "src", "loop"), _root);

        var found = await _tools.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);

        found.Should().Contain("top.txt").And.Contain("ok.txt").And.Contain("directory links were not entered: src/loop/");
        found.Split('\n').Count(line => line.Contains("top.txt:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task GrepFiles_does_not_search_behind_a_link_to_another_tree_and_says_so()
    {
        Link(Path.Combine(_root, "linked-pkg"), _outside);

        var found = await _tools.GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);

        found.Should().Contain("top.txt").And.NotContain("linked.txt").And.Contain("directory links were not entered: linked-pkg/");
    }

    [Fact]
    public void Without_a_link_the_output_carries_no_note()
    {
        var listing = _tools.ListDirectory(".", true);
        var found = _tools.GlobFiles("**/*.txt", ".");

        listing.Should().NotContain("not listed here");
        found.Should().NotContain("were not entered");
    }

    [Fact]
    public void A_pattern_that_climbs_out_of_the_base_still_matches_there()
    {
        // The base is src; the parent hop of the directory view still reaches the root's file.
        var found = _tools.GlobFiles("../top.txt", "src");

        found.Should().Contain("top.txt");
    }
}
