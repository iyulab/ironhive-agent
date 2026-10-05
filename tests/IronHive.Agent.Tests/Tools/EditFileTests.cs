using System.Text;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// <c>EditFile</c> replaces exact text and changes nothing else in the file, byte for byte. Without it, a one-line change
/// to a large file meant rewriting the whole file through <c>WriteFile</c>.
/// </summary>
public class EditFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"edit-file-{Guid.NewGuid():N}");
    private readonly ToolProvider _tools;

    public EditFileTests()
    {
        Directory.CreateDirectory(_dir);
        _tools = new ToolProvider(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task AUniqueMatch_IsReplaced_AndEveryOtherByteStaysTheSame()
    {
        var before = "[pool-16]\r\nmax_connections = 40\r\n\r\n[pool-17]\r\nmax_connections = 40\n\n[pool-18]\r\nmax_connections = 40\r\n";
        File.WriteAllBytes(PathOf("settings.ini"), Encoding.UTF8.GetBytes(before));

        var result = await _tools.EditFile("settings.ini", "[pool-17]\r\nmax_connections = 40", "[pool-17]\r\nmax_connections = 64", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Successfully edited settings.ini (1 replacement)", result);
        Assert.Equal(before.Replace("[pool-17]\r\nmax_connections = 40", "[pool-17]\r\nmax_connections = 64", StringComparison.Ordinal),
            Encoding.UTF8.GetString(File.ReadAllBytes(PathOf("settings.ini"))));
    }

    [Fact]
    public async Task OldTextSentWithLf_EditsACrlfFile_AndTheFileKeepsCrlf()
    {
        File.WriteAllText(PathOf("a.txt"), "one\r\ntwo\r\nthree\r\n");

        var result = await _tools.EditFile("a.txt", "two\nthree", "2\n3", cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("Successfully edited", result, StringComparison.Ordinal);
        Assert.Equal("one\r\n2\r\n3\r\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public async Task NoMatch_IsAnError_AndTheFileIsUntouched()
    {
        File.WriteAllText(PathOf("a.txt"), "alpha beta");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _tools.EditFile("a.txt", "gamma", "delta", cancellationToken: TestContext.Current.CancellationToken));

        Assert.StartsWith("oldText was not found in a.txt.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("alpha beta", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public async Task AMatchThatDiffersOnlyInWhitespace_IsNamedInTheError()
    {
        File.WriteAllText(PathOf("a.py"), "def f():\n    return 1\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _tools.EditFile("a.py", "def f():\n  return 1", "def f():\n  return 2", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("differs only in whitespace", ex.Message, StringComparison.Ordinal);
        Assert.Equal("def f():\n    return 1\n", File.ReadAllText(PathOf("a.py")));
    }

    [Fact]
    public async Task TwoMatches_WithoutReplaceAll_AreAnErrorWithTheCount_AndTheFileIsUntouched()
    {
        File.WriteAllText(PathOf("a.txt"), "x = 1\ny = 1\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _tools.EditFile("a.txt", "= 1", "= 2", cancellationToken: TestContext.Current.CancellationToken));

        Assert.StartsWith("oldText occurs 2 times in a.txt.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("x = 1\ny = 1\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public async Task ReplaceAll_ReplacesEveryOccurrence()
    {
        File.WriteAllText(PathOf("a.txt"), "x = 1\ny = 1\n");

        var result = await _tools.EditFile("a.txt", "= 1", "= 2", replaceAll: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Successfully edited a.txt (2 replacements)", result);
        Assert.Equal("x = 2\ny = 2\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public async Task AByteOrderMark_IsKept()
    {
        File.WriteAllText(PathOf("bom.txt"), "héllo world", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await _tools.EditFile("bom.txt", "world", "there", cancellationToken: TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(PathOf("bom.txt"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("héllo there", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task AFileWithoutAMark_GetsNone()
    {
        File.WriteAllText(PathOf("plain.txt"), "héllo world", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await _tools.EditFile("plain.txt", "world", "there", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("héllo there", Encoding.UTF8.GetString(File.ReadAllBytes(PathOf("plain.txt"))));
    }

    [Fact]
    public async Task AMissingFile_ADirectory_AndAnEmptyOldText_AreRefused()
    {
        Directory.CreateDirectory(PathOf("dir"));
        File.WriteAllText(PathOf("a.txt"), "x");

        Assert.Contains("use WriteFile to create one", (await Assert.ThrowsAsync<FileNotFoundException>(() => _tools.EditFile("missing.txt", "a", "b", cancellationToken: TestContext.Current.CancellationToken))).Message, StringComparison.Ordinal);
        Assert.Contains("is a directory", (await Assert.ThrowsAsync<InvalidOperationException>(() => _tools.EditFile("dir", "a", "b", cancellationToken: TestContext.Current.CancellationToken))).Message, StringComparison.Ordinal);
        Assert.StartsWith("oldText is empty.", (await Assert.ThrowsAsync<ArgumentException>(() => _tools.EditFile("a.txt", "", "b", cancellationToken: TestContext.Current.CancellationToken))).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(PathOf("missing.txt")));
        Assert.Equal("x", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public async Task TheWriteInterceptor_RunsOnce_AndItsNoteReachesTheModel()
    {
        File.WriteAllText(PathOf("a.txt"), "old");
        var interceptor = new CountingInterceptor();
        var tools = new ToolProvider(_dir, new FileToolOptions { WriteInterceptor = interceptor });

        var result = await tools.EditFile("a.txt", "old", "new", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, interceptor.Calls);
        Assert.Equal("Successfully edited a.txt (1 replacement) (snapshot)", result);
        Assert.Equal("new", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public void GetAll_OffersEditFile()
    {
        Assert.Contains(BuiltInTools.GetAll(_dir), tool => tool.Name == "EditFile");
    }

    private sealed class CountingInterceptor : IFileWriteInterceptor
    {
        public int Calls { get; private set; }

        public async Task<string?> InterceptAsync(string fullPath, Func<Task> write, CancellationToken cancellationToken = default)
        {
            Calls++;
            await write();
            return " (snapshot)";
        }
    }
}
