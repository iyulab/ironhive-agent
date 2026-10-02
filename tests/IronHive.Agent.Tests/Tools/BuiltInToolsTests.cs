using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

public class BuiltInToolsTests : IDisposable
{
    private readonly string _testDir;
    private readonly ToolProvider _tools;

    public BuiltInToolsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ironhive-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        _tools = new ToolProvider(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        // Arrange
        var testFile = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "Hello, World!", TestContext.Current.CancellationToken);

        // Act
        var result = await _tools.ReadFile("test.txt");

        // Assert
        Assert.Equal("Hello, World!", result);
    }

    [Fact]
    public async Task DeleteFile_RemovesTheFile_AndRefusesAMissingFileOrADirectory()
    {
        var file = Path.Combine(_testDir, "gone.txt");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_testDir, "folder"));

        Assert.StartsWith("Successfully deleted file", _tools.DeleteFile("gone.txt"), StringComparison.Ordinal);
        Assert.False(File.Exists(file));
        Assert.StartsWith("Error: File not found", _tools.DeleteFile("gone.txt"), StringComparison.Ordinal);
        Assert.Contains("is a directory", _tools.DeleteFile("folder"), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_testDir, "folder")));
    }

    [Fact]
    public async Task MoveFile_Renames_IntoANewDirectory_LeavingNothingBehind()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "OrderService.cs"), "class OrderService {}", TestContext.Current.CancellationToken);

        var result = _tools.MoveFile("OrderService.cs", "src/OrderManager.cs");

        Assert.StartsWith("Successfully moved file", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_testDir, "OrderService.cs")));
        Assert.Equal("class OrderService {}", await File.ReadAllTextAsync(Path.Combine(_testDir, "src", "OrderManager.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MoveFile_RefusesToReplaceAnExistingFile_UnlessToldTo()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "a.txt"), "a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "b.txt"), "b", TestContext.Current.CancellationToken);

        Assert.Contains("already exists", _tools.MoveFile("a.txt", "b.txt"), StringComparison.Ordinal);
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(_testDir, "b.txt"), TestContext.Current.CancellationToken));

        Assert.StartsWith("Successfully moved file", _tools.MoveFile("a.txt", "b.txt", overwrite: true), StringComparison.Ordinal);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_testDir, "b.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_testDir, "a.txt")));
    }

    [Fact]
    public void MoveFile_RefusesAMissingSource_AndADirectoryDestination()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "dir"));
        File.WriteAllText(Path.Combine(_testDir, "f.txt"), "f");

        Assert.StartsWith("Error: File not found", _tools.MoveFile("missing.txt", "x.txt"), StringComparison.Ordinal);
        Assert.Contains("including the file name", _tools.MoveFile("f.txt", "dir"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_testDir, "f.txt")));
    }

    [Fact]
    public async Task ReadFile_NonExistent_ReturnsError()
    {
        // Act
        var result = await _tools.ReadFile("nonexistent.txt");

        // Assert
        Assert.StartsWith("Error: File not found", result);
    }

    [Fact]
    public async Task ReadFile_WithLineRange_ReturnsSubset()
    {
        // Arrange
        var testFile = Path.Combine(_testDir, "lines.txt");
        await File.WriteAllTextAsync(testFile, "Line1\nLine2\nLine3\nLine4\nLine5", TestContext.Current.CancellationToken);

        // Act
        var result = await _tools.ReadFile("lines.txt", startLine: 2, lineCount: 2);

        // Assert
        Assert.Equal($"Line2{Environment.NewLine}Line3", result);
    }

    [Fact]
    public async Task WriteFile_CreatesNewFile()
    {
        // Act
        var result = await _tools.WriteFile("new.txt", "New content");

        // Assert
        Assert.Contains("Successfully wrote", result);
        Assert.True(File.Exists(Path.Combine(_testDir, "new.txt")));
        Assert.Equal("New content", await File.ReadAllTextAsync(Path.Combine(_testDir, "new.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteFile_AppendMode_AppendsContent()
    {
        // Arrange
        var testFile = Path.Combine(_testDir, "append.txt");
        await File.WriteAllTextAsync(testFile, "First", TestContext.Current.CancellationToken);

        // Act
        var result = await _tools.WriteFile("append.txt", "Second", append: true);

        // Assert
        Assert.Contains("Successfully appended", result);
        Assert.Equal("FirstSecond", await File.ReadAllTextAsync(testFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ListDirectory_ShowsContents()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDir, "file1.txt"), "");
        File.WriteAllText(Path.Combine(_testDir, "file2.txt"), "");
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));

        // Act
        var result = _tools.ListDirectory();

        // Assert
        Assert.Contains("[DIR]", result);
        Assert.Contains("subdir", result);
        Assert.Contains("[FILE]", result);
        Assert.Contains("file1.txt", result);
        Assert.Contains("file2.txt", result);
    }

    [Fact]
    public void GlobFiles_FindsMatchingFiles()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDir, "test1.cs"), "");
        File.WriteAllText(Path.Combine(_testDir, "test2.cs"), "");
        File.WriteAllText(Path.Combine(_testDir, "test.txt"), "");

        // Act
        var result = _tools.GlobFiles("*.cs");

        // Assert
        Assert.Contains("test1.cs", result);
        Assert.Contains("test2.cs", result);
        Assert.DoesNotContain("test.txt", result);
    }

    [Fact]
    public async Task GrepFiles_FindsPattern()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDir, "search1.txt"), "This contains PATTERN here");
        File.WriteAllText(Path.Combine(_testDir, "search2.txt"), "No match here");

        // Act
        var result = await _tools.GrepFiles("PATTERN", "*.txt");

        // Assert
        Assert.Contains("search1.txt", result);
        Assert.Contains("PATTERN", result);
        Assert.DoesNotContain("search2.txt", result);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsOutput()
    {
        // Arrange
        var command = OperatingSystem.IsWindows() ? "echo Hello" : "echo Hello";

        // Act
        var result = await _tools.ExecuteCommand(command);

        // Assert
        Assert.Contains("Exit code: 0", result);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public async Task ExecuteCommand_Timeout_ReturnsError()
    {
        // Arrange - command that takes a long time
        var command = OperatingSystem.IsWindows() ? "ping -n 10 127.0.0.1" : "sleep 10";

        // Act
        var result = await _tools.ExecuteCommand(command, timeoutMs: 100);

        // Assert
        Assert.Contains("timed out", result);
    }

    [Fact]
    public void GetAll_ReturnsAllTools()
    {
        // Act
        var tools = BuiltInTools.GetAll(_testDir);

        // Assert
        Assert.Equal(
            ["ReadFile", "WriteFile", "EditFile", "DeleteFile", "MoveFile", "ListDirectory", "GlobFiles", "GrepFiles", "ExecuteCommand", "ManageTodo"],
            tools.Select(t => t.Name));
    }
}
