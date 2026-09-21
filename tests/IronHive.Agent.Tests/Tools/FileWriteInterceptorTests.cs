using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// The write interceptor is how a host attaches behaviour to the agent's <c>WriteFile</c> without
/// keeping a second copy of the file tools. It has to see the path the tool acts on, decide whether the
/// write happens, and be able to add to what the model is told.
/// </summary>
public class FileWriteInterceptorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"write-interceptor-{Guid.NewGuid():N}");

    public FileWriteInterceptorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheInterceptor_SeesTheResolvedPath_AndItsNoteReachesTheModel()
    {
        var interceptor = new Recording(note: " (snapshot saved)");
        var tools = new ToolProvider(_dir, interceptor);

        var result = await tools.WriteFile("sub/notes.md", "hello");

        var expectedPath = Path.GetFullPath(Path.Combine(_dir, "sub", "notes.md"));
        Assert.Equal([expectedPath], interceptor.Paths.Select(Path.GetFullPath));
        Assert.Equal("Successfully wrote to file: sub/notes.md (snapshot saved)", result);
        Assert.Equal("hello", await File.ReadAllTextAsync(expectedPath, TestContext.Current.CancellationToken));
        Assert.True(interceptor.FileExistedBeforeWrite is false, "the interceptor runs before the bytes land");
    }

    [Fact]
    public async Task WithoutAnInterceptor_TheMessageIsUnchanged()
    {
        var result = await new ToolProvider(_dir).WriteFile("plain.txt", "x");

        Assert.Equal("Successfully wrote to file: plain.txt", result);
    }

    [Fact]
    public async Task AppendMode_GoesThroughTheInterceptorToo()
    {
        var interceptor = new Recording(note: null);
        var tools = new ToolProvider(_dir, interceptor);
        await tools.WriteFile("log.txt", "a");

        var result = await tools.WriteFile("log.txt", "b", append: true);

        Assert.Equal("Successfully appended to file: log.txt", result);
        Assert.Equal("ab", await File.ReadAllTextAsync(Path.Combine(_dir, "log.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(2, interceptor.Paths.Count);
    }

    [Fact]
    public async Task AnInterceptorThatDoesNotCallWrite_PreventsTheWrite()
    {
        var tools = new ToolProvider(_dir, new Recording(note: " (refused)", callWrite: false));

        await tools.WriteFile("blocked.txt", "x");

        Assert.False(File.Exists(Path.Combine(_dir, "blocked.txt")));
    }

    [Fact]
    public async Task AnInterceptorThatThrows_IsReportedAsAFailedWrite()
    {
        var tools = new ToolProvider(_dir, new Throwing());

        var result = await tools.WriteFile("x.txt", "x");

        Assert.Equal("Error writing file: versioning store is read-only", result);
    }

    [Fact]
    public async Task GetAll_PassesTheInterceptorToTheWriteTool()
    {
        var interceptor = new Recording(note: null);
        var write = BuiltInTools.GetAll(_dir, interceptor).OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "WriteFile");

        await write.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = "via-tool.txt", ["content"] = "x" },
            TestContext.Current.CancellationToken);

        Assert.Single(interceptor.Paths);
    }

    private sealed class Recording(string? note, bool callWrite = true) : IFileWriteInterceptor
    {
        public List<string> Paths { get; } = [];

        public bool? FileExistedBeforeWrite { get; private set; }

        public async Task<string?> InterceptAsync(string fullPath, Func<Task> write, CancellationToken cancellationToken = default)
        {
            Paths.Add(fullPath);
            FileExistedBeforeWrite ??= File.Exists(fullPath);
            if (callWrite)
            {
                await write();
            }

            return note;
        }
    }

    private sealed class Throwing : IFileWriteInterceptor
    {
        public Task<string?> InterceptAsync(string fullPath, Func<Task> write, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("versioning store is read-only");
    }
}
