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
        await File.WriteAllTextAsync(testFile, "Hello, World!");

        // Act
        var result = await _tools.ReadFile("test.txt");

        // Assert
        Assert.Equal("Hello, World!", result);
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
        await File.WriteAllTextAsync(testFile, "Line1\nLine2\nLine3\nLine4\nLine5");

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
        Assert.Equal("New content", await File.ReadAllTextAsync(Path.Combine(_testDir, "new.txt")));
    }

    [Fact]
    public async Task WriteFile_AppendMode_AppendsContent()
    {
        // Arrange
        var testFile = Path.Combine(_testDir, "append.txt");
        await File.WriteAllTextAsync(testFile, "First");

        // Act
        var result = await _tools.WriteFile("append.txt", "Second", append: true);

        // Assert
        Assert.Contains("Successfully appended", result);
        Assert.Equal("FirstSecond", await File.ReadAllTextAsync(testFile));
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
        Assert.Equal(7, tools.Count); // ReadFile, WriteFile, ListDirectory, GlobFiles, GrepFiles, ExecuteCommand, ManageTodo
    }
}
