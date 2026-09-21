using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace IronHive.Agent.Tools;

/// <summary>
/// Built-in tools for the agent, registered via AIFunctionFactory.
/// </summary>
public static class BuiltInTools
{
    /// <summary>
    /// Gets all built-in tools as AITool instances.
    /// </summary>
    /// <param name="workingDirectory">Working directory for tools.</param>
    /// <returns>List of AI tools.</returns>
    public static IList<AITool> GetAll(string? workingDirectory = null)
        => GetAll(workingDirectory, options: null);

    /// <summary>
    /// Gets all built-in tools, with the host's decisions about the file tools.
    /// </summary>
    /// <param name="workingDirectory">Working directory for tools.</param>
    /// <param name="options">Where the file tools may reach and what runs around a write; <see langword="null"/> for the defaults.</param>
    /// <returns>List of AI tools. The list is mutable so a host can append its own tools.</returns>
    public static IList<AITool> GetAll(string? workingDirectory, FileToolOptions? options)
    {
        var wd = workingDirectory ?? Directory.GetCurrentDirectory();
        var tools = new ToolProvider(wd, options);
        var todoTool = new TodoTool(wd);

        return new List<AITool>
        {
            AIFunctionFactory.Create(tools.ReadFile),
            AIFunctionFactory.Create(tools.WriteFile),
            AIFunctionFactory.Create(tools.ListDirectory),
            AIFunctionFactory.Create(tools.GlobFiles),
            AIFunctionFactory.Create(tools.GrepFiles),
            AIFunctionFactory.Create(tools.ExecuteCommand),
            todoTool.GetAITool()
        };
    }

}

/// <summary>
/// Tool provider with working directory context.
/// </summary>
/// <remarks>
/// The working directory is where relative paths start. It is not a boundary: absolute paths and
/// <c>..</c> leave it unless <see cref="FileToolOptions.AllowedRoots"/> says where the tools may reach.
/// </remarks>
public class ToolProvider
{
    private readonly string _workingDirectory;
    private readonly IFileWriteInterceptor? _writeInterceptor;
    private readonly string[] _allowedRoots;
    private const int MaxFileSize = 1024 * 1024; // 1MB
    private const int MaxOutputLength = 50000; // Characters
    private const int DefaultCommandTimeout = 30000; // 30 seconds

    public ToolProvider(string workingDirectory, FileToolOptions? options = null)
    {
        _writeInterceptor = options?.WriteInterceptor;
        _allowedRoots = [.. (options?.AllowedRoots ?? [])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(workingDirectory, root))))];
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Reads the content of a file.
    /// </summary>
    /// <param name="path">Relative or absolute path to the file to read.</param>
    /// <param name="startLine">Optional 1-based line number to start reading from.</param>
    /// <param name="lineCount">Optional number of lines to read. If not specified, reads entire file.</param>
    [Description("Read the content of a file. Returns the file content as text.")]
    public async Task<string> ReadFile(
        [Description("Path to the file to read (relative to working directory or absolute)")] string path,
        [Description("Line number to start reading from (1-based, optional)")] int? startLine = null,
        [Description("Number of lines to read (optional, reads all if not specified)")] int? lineCount = null)
    {
        if (!TryResolvePath(path, out var fullPath, out var refusal))
        {
            return refusal;
        }

        if (!File.Exists(fullPath))
        {
            return $"Error: File not found: {path}";
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxFileSize)
        {
            return $"Error: File too large ({fileInfo.Length / 1024}KB). Maximum size is {MaxFileSize / 1024}KB.";
        }

        try
        {
            if (startLine.HasValue || lineCount.HasValue)
            {
                var lines = await File.ReadAllLinesAsync(fullPath);
                var start = Math.Max(0, (startLine ?? 1) - 1);
                var count = lineCount ?? (lines.Length - start);
                var selectedLines = lines.Skip(start).Take(count);
                return string.Join(Environment.NewLine, selectedLines);
            }

            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    /// <summary>
    /// Writes content to a file.
    /// </summary>
    /// <param name="path">Relative or absolute path to the file to write.</param>
    /// <param name="content">Content to write to the file.</param>
    /// <param name="append">If true, appends to existing file instead of overwriting.</param>
    [Description("Write content to a file. Creates the file if it doesn't exist, or overwrites if it does.")]
    public async Task<string> WriteFile(
        [Description("Path to the file to write (relative to working directory or absolute)")] string path,
        [Description("Content to write to the file")] string content,
        [Description("If true, append to existing file instead of overwriting")] bool append = false)
    {
        if (!TryResolvePath(path, out var fullPath, out var refusal))
        {
            return refusal;
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Func<Task> write = append
                ? () => File.AppendAllTextAsync(fullPath, content)
                : () => File.WriteAllTextAsync(fullPath, content);

            string? note = null;
            if (_writeInterceptor is null)
            {
                await write();
            }
            else
            {
                note = await _writeInterceptor.InterceptAsync(fullPath, write);
            }

            return append
                ? $"Successfully appended to file: {path}{note}"
                : $"Successfully wrote to file: {path}{note}";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists the contents of a directory.
    /// </summary>
    /// <param name="path">Relative or absolute path to the directory.</param>
    /// <param name="recursive">If true, lists contents recursively.</param>
    [Description("List the contents of a directory, showing files and subdirectories.")]
    public string ListDirectory(
        [Description("Path to the directory (relative to working directory or absolute)")] string? path = null,
        [Description("If true, list contents recursively")] bool recursive = false)
    {
        if (!TryResolvePath(path ?? ".", out var fullPath, out var refusal))
        {
            return refusal;
        }

        if (!Directory.Exists(fullPath))
        {
            return $"Error: Directory not found: {path ?? "."}";
        }

        try
        {
            var sb = new StringBuilder();
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var dirs = Directory.GetDirectories(fullPath, "*", searchOption);
            var files = Directory.GetFiles(fullPath, "*", searchOption);

            foreach (var dir in dirs.Take(500))
            {
                var relativePath = Path.GetRelativePath(fullPath, dir);
                sb.AppendLine(CultureInfo.InvariantCulture, $"[DIR]  {relativePath}/");
            }

            foreach (var file in files.Take(500))
            {
                var relativePath = Path.GetRelativePath(fullPath, file);
                var size = new FileInfo(file).Length;
                sb.AppendLine(CultureInfo.InvariantCulture, $"[FILE] {relativePath} ({FormatSize(size)})");
            }

            if (dirs.Length > 500 || files.Length > 500)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"... (truncated, total: {dirs.Length} dirs, {files.Length} files)");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }
    }

    /// <summary>
    /// Searches for files matching a glob pattern.
    /// </summary>
    /// <param name="pattern">Glob pattern to match (e.g., "**/*.cs", "src/**/*.json").</param>
    /// <param name="path">Base directory for the search.</param>
    [Description("Search for files matching a glob pattern.")]
    public string GlobFiles(
        [Description("Glob pattern to match (e.g., '**/*.cs', 'src/**/*.json')")] string pattern,
        [Description("Base directory for the search (optional, defaults to working directory)")] string? path = null)
    {
        if (!TryResolvePath(path ?? ".", out var basePath, out var refusal))
        {
            return refusal;
        }

        if (!Directory.Exists(basePath))
        {
            return $"Error: Directory not found: {path ?? "."}";
        }

        try
        {
            var matcher = new Matcher();
            matcher.AddInclude(pattern);

            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(basePath)));

            // A pattern such as "../**" climbs out of the base directory; what it finds is held to the boundary too.
            var files = result.Files.Where(file => IsWithinAllowedRoots(Path.GetFullPath(Path.Combine(basePath, file.Path)))).ToList();

            if (files.Count == 0)
            {
                return $"No files found matching pattern: {pattern}";
            }

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Found {files.Count} files matching '{pattern}':");

            foreach (var file in files.Take(100))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {file.Path}");
            }

            if (files.Count > 100)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... (truncated, total: {files.Count} files)");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    /// <summary>
    /// Searches for text patterns in files.
    /// </summary>
    /// <param name="pattern">Text pattern or regex to search for.</param>
    /// <param name="filePattern">Glob pattern for files to search in.</param>
    /// <param name="path">Base directory for the search.</param>
    [Description("Search for text patterns in files (like grep).")]
    public async Task<string> GrepFiles(
        [Description("Text pattern to search for")] string pattern,
        [Description("Glob pattern for files to search in (e.g., '**/*.cs')")] string filePattern,
        [Description("Base directory for the search (optional)")] string? path = null)
    {
        if (!TryResolvePath(path ?? ".", out var basePath, out var refusal))
        {
            return refusal;
        }

        if (!Directory.Exists(basePath))
        {
            return $"Error: Directory not found: {path ?? "."}";
        }

        try
        {
            var matcher = new Matcher();
            matcher.AddInclude(filePattern);

            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(basePath)));

            var files = result.Files.Where(file => IsWithinAllowedRoots(Path.GetFullPath(Path.Combine(basePath, file.Path)))).ToList();

            if (files.Count == 0)
            {
                return $"No files found matching pattern: {filePattern}";
            }

            var sb = new StringBuilder();
            var matchCount = 0;
            var fileCount = 0;

            foreach (var file in files.Take(50))
            {
                var fullFilePath = Path.Combine(basePath, file.Path);

                try
                {
                    var lines = await File.ReadAllLinesAsync(fullFilePath);
                    var fileHasMatch = false;

                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!fileHasMatch)
                            {
                                sb.AppendLine(CultureInfo.InvariantCulture, $"\n{file.Path}:");
                                fileHasMatch = true;
                                fileCount++;
                            }

                            sb.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1}: {TruncateLine(lines[i], 200)}");
                            matchCount++;

                            if (matchCount >= 100)
                            {
                                sb.AppendLine("\n... (truncated at 100 matches)");
                                return sb.ToString();
                            }
                        }
                    }
                }
                catch
                {
                    // Skip files that can't be read
                }
            }

            if (matchCount == 0)
            {
                return $"No matches found for '{pattern}' in files matching '{filePattern}'";
            }

            sb.Insert(0, $"Found {matchCount} matches in {fileCount} files:\n");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    /// <summary>
    /// Executes a shell command.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    [Description("Execute a shell command and return its output. Use with caution.")]
    public async Task<string> ExecuteCommand(
        [Description("The command to execute")] string command,
        [Description("Timeout in milliseconds (default: 30000)")] int timeoutMs = DefaultCommandTimeout)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var processInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null && outputBuilder.Length < MaxOutputLength)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && errorBuilder.Length < MaxOutputLength)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return $"Error: Command timed out after {timeoutMs}ms";
            }

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            var result = new StringBuilder();
            result.AppendLine(CultureInfo.InvariantCulture, $"Exit code: {process.ExitCode}");

            if (!string.IsNullOrWhiteSpace(output))
            {
                result.AppendLine("Output:");
                result.AppendLine(TruncateOutput(output));
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                result.AppendLine("Stderr:");
                result.AppendLine(TruncateOutput(error));
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    // The one place a path from the model becomes a path on disk - and so the one place the boundary is
    // checked, on the path the tool will actually open rather than on the text the model wrote.
    private bool TryResolvePath(string path, out string fullPath, out string refusal)
    {
        fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_workingDirectory, path));

        if (IsWithinAllowedRoots(fullPath))
        {
            refusal = string.Empty;
            return true;
        }

        refusal = $"Error: '{path}' is outside the directories this tool may access ({string.Join(", ", _allowedRoots)}). " +
                  "This is a policy boundary, not a transient failure: another spelling of the same path will be refused too.";
        return false;
    }

    private bool IsWithinAllowedRoots(string fullPath)
    {
        if (_allowedRoots.Length == 0)
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidate = Path.TrimEndingDirectorySeparator(fullPath);

        foreach (var root in _allowedRoots)
        {
            if (candidate.Equals(root, comparison)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    private static string TruncateLine(string line, int maxLength)
    {
        if (line.Length <= maxLength)
        {
            return line;
        }
        return line[..maxLength] + "...";
    }

    private static string TruncateOutput(string output)
    {
        if (output.Length <= MaxOutputLength)
        {
            return output;
        }
        return output[..MaxOutputLength] + $"\n... (truncated, total {output.Length} chars)";
    }
}
