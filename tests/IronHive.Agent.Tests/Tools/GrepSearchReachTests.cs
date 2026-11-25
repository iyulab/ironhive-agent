using AwesomeAssertions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// "No matches" is a claim about the files a search matched: it has to have read them, or say that it did not.
/// </summary>
public sealed class GrepSearchReachTests : IDisposable
{
    private const string Needle = "needle-in-the-haystack";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"grep-reach-{Guid.NewGuid():N}");

    public GrepSearchReachTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private void Fill(int files)
    {
        for (var i = 0; i < files; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i:D5}.txt"), "filler");
        }
    }

    private Task<string> Search() =>
        new ToolProvider(_root).GrepFiles(Needle, "**/*.txt", ".", TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_match_past_the_fiftieth_file_is_found()
    {
        Fill(60);
        File.WriteAllText(Path.Combine(_root, "z-last.txt"), Needle);

        var found = await Search();

        found.Should().Contain("z-last.txt");
    }

    [Fact]
    public async Task A_search_that_leaves_files_unread_says_how_many()
    {
        Fill(2001);

        var found = await Search();

        found.Should().StartWith("No matches found").And.Contain("stopped after reading 2000 files; 1 more matched file(s) were not looked at");
    }

    [Fact]
    public async Task A_search_that_stops_at_the_match_limit_does_not_claim_how_far_it_read()
    {
        Fill(2001);
        File.WriteAllLines(Path.Combine(_root, "a-first.txt"), Enumerable.Repeat(Needle, 150));

        var found = await Search();

        found.Should().Contain("truncated at 100 matches").And.NotContain("not looked at");
    }

    [Fact]
    public async Task A_file_the_search_could_not_read_is_counted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an exclusive handle only keeps readers out on Windows");
        File.WriteAllText(Path.Combine(_root, "locked.txt"), Needle);
        using var held = new FileStream(Path.Combine(_root, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None);

        var found = await Search();

        found.Should().StartWith("No matches found").And.Contain("could not be read: locked.txt");
    }

    // A log another process is still writing lets others read it; the search reads it like any other file.
    [Fact]
    public async Task A_file_another_process_is_writing_is_searched()
    {
        var path = Path.Combine(_root, "running.log.txt");
        File.WriteAllText(path, Needle);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var found = await Search();

        found.Should().Contain("running.log.txt").And.NotContain("could not be read");
    }

    [Fact]
    public async Task A_binary_file_is_not_read_through_and_the_result_says_so()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Needle).Concat(new byte[] { 0, 1, 2, 0 }).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "blob.txt"), bytes);

        var found = await Search();

        found.Should().StartWith("No matches found").And.Contain("not searched, binary: blob.txt");
    }

    // UTF-16 text is full of zero bytes; its byte order mark says it is text.
    [Fact]
    public async Task A_utf16_file_is_text()
    {
        File.WriteAllText(Path.Combine(_root, "wide.txt"), Needle, System.Text.Encoding.Unicode);

        var found = await Search();

        found.Should().Contain("wide.txt").And.NotContain("binary");
    }

    // The limit is on the files a search reads as text; a binary it leaves out does not take a place.
    [Fact]
    public async Task Binary_files_do_not_use_up_the_files_a_search_reads()
    {
        for (var i = 0; i < 2001; i++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"b{i:D5}.txt"), [0, 1]);
        }

        File.WriteAllText(Path.Combine(_root, "z-last.txt"), Needle);

        var found = await Search();

        found.Should().Contain("z-last.txt").And.NotContain("not looked at").And.Contain("(2001 in all)");
    }

    // A tree of build output or version-control objects is mostly binary: the files a search opens are limited too, or it
    // goes on opening them for minutes.
    [Fact]
    public async Task A_search_stops_opening_files_even_when_few_of_them_are_text()
    {
        for (var i = 0; i < 5001; i++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"b{i:D5}.txt"), [0, 1]);
        }

        var found = await Search();

        found.Should().Contain("stopped after opening 5000 files; 1 more matched file(s) were not looked at");
    }

    [Fact]
    public async Task A_search_that_read_everything_says_nothing_about_a_limit()
    {
        Fill(2000);

        var found = await Search();

        found.Should().StartWith("No matches found").And.NotContain("not looked at");
    }
}
