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

        found.Should().StartWith("No matches found").And.Contain("only the first 2000 of 2001 files were searched");
    }

    [Fact]
    public async Task A_search_that_read_everything_says_nothing_about_a_limit()
    {
        Fill(2000);

        var found = await Search();

        found.Should().StartWith("No matches found").And.NotContain("were searched");
    }
}
